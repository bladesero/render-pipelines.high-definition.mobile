using System;
using System.Collections.Generic;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Experimental.Rendering.RenderGraphModule;

namespace UnityEngine.Rendering.HighDefinition
{
    internal sealed partial class MobileRenderPipelineBackend
    {
        const int MaxLocalShadowSlices = MaxVisibleLights * 6;
        readonly MobileRenderSettings m_ShadowSettings = new MobileRenderSettings();
        readonly int[] m_LocalVisibleIndices = new int[MaxVisibleLights];
        readonly Vector4[] m_LocalShadowInfo = new Vector4[MaxVisibleLights];
        readonly Matrix4x4[] m_LocalShadowMatrices = new Matrix4x4[MaxLocalShadowSlices];
        readonly Matrix4x4[] m_LocalShadowViews = new Matrix4x4[MaxLocalShadowSlices];
        readonly Matrix4x4[] m_LocalShadowProjections = new Matrix4x4[MaxLocalShadowSlices];
        readonly ShadowSplitData[] m_LocalShadowSplits = new ShadowSplitData[MaxLocalShadowSlices];
        readonly int[] m_LocalShadowLights = new int[MaxLocalShadowSlices];
        int m_LocalLightCount, m_LocalShadowCount, m_LocalShadowGrid = 1;

        // Imported depth atlases survive RenderGraph frames. Keep one pair per camera:
        // directional cascades depend on the camera, while local shadow slots depend
        // on the visible-light order and atlas layout.
        sealed class ShadowCache
        {
            internal RTHandle mainAtlas, localAtlas;
            internal int mainKey, localKey, mainSize, localSize;
            internal bool mainValid, localValid, mainCacheable, localCacheable;
        }

        readonly Dictionary<Camera, ShadowCache> m_ShadowCaches = new Dictionary<Camera, ShadowCache>();
        readonly List<Camera> m_ExpiredShadowCameras = new List<Camera>();
        internal int mainShadowDrawCount, localShadowDrawCount;

        ShadowCache GetShadowCache(Camera camera)
        {
            if (!m_ShadowCaches.TryGetValue(camera, out var cache))
                m_ShadowCaches.Add(camera, cache = new ShadowCache());
            return cache;
        }

        static RTHandle EnsureShadowAtlas(ref RTHandle atlas, int size, string name)
        {
            if (atlas?.rt != null && atlas.rt.width == size && atlas.rt.IsCreated()) return atlas;
            atlas?.Release();
            atlas = RTHandles.Alloc(size, size, depthBufferBits: DepthBits.Depth16,
                isShadowMap: true, filterMode: FilterMode.Bilinear, name: name);
            if (atlas.rt == null || !atlas.rt.IsCreated())
            {
                atlas.Release(); atlas = null;
                throw new InvalidOperationException("Could not allocate " + name + ".");
            }
            return atlas;
        }

        static int MixShadowKey(int key, int value) => unchecked(key * 31 + value);
        static int MixShadowKey(int key, float value) => MixShadowKey(key, value.GetHashCode());
        static int MixShadowKey(int key, Vector3 value) => MixShadowKey(key, value.GetHashCode());
        static int MixShadowKey(int key, Quaternion value) => MixShadowKey(key, value.GetHashCode());

        static bool CacheLightShadows(Light light, out HDAdditionalLightData data)
        {
            light.TryGetComponent(out data);
            return data != null && data.shadowUpdateMode != ShadowUpdateMode.EveryFrame && data.updateUponLightMovement;
        }

        static int LightShadowKey(int key, Light light, HDAdditionalLightData data)
        {
            key = MixShadowKey(key, light.GetInstanceID());
            key = MixShadowKey(key, light.transform.position);
            key = MixShadowKey(key, light.transform.rotation);
            key = MixShadowKey(key, light.range);
            key = MixShadowKey(key, light.spotAngle);
            key = MixShadowKey(key, light.shadowNearPlane);
            key = MixShadowKey(key, light.shadowBias);
            key = MixShadowKey(key, light.shadowNormalBias);
            key = MixShadowKey(key, (int)light.shadows);
            return MixShadowKey(key, data != null ? unchecked((int)data.mobileShadowRequestVersion) : 0);
        }

        bool NeedsMainShadowUpdate(ShadowCache cache, CullingResults culling, bool enabled, int size)
        {
            int key = MixShadowKey(17, size);
            key = MixShadowKey(key, enabled ? 1 : 0);
            bool cacheable = enabled;
            if (enabled)
            {
                var light = culling.visibleLights[m_MainLight].light;
                cacheable = CacheLightShadows(light, out var data);
                key = LightShadowKey(key, light, data);
                key = MixShadowKey(key, m_ShadowSettings.cascadeCount);
                key = MixShadowKey(key, m_ShadowSettings.cascadeSplit);
                key = MixShadowKey(key, m_ShadowSettings.cascadeBlend);
                key = MixShadowKey(key, m_ShadowSettings.shadowNormalBias);
                // A directional atlas is camera-relative. Reuse only while the
                // cascade projections remain identical; otherwise coverage drifts.
                for (int i = 0; i < m_ShadowSettings.cascadeCount; i++)
                {
                    key = MixShadowKey(key, m_ShadowViews[i].GetHashCode());
                    key = MixShadowKey(key, m_ShadowProjections[i].GetHashCode());
                }
            }
            bool update = !cache.mainValid || cache.mainSize != size || cache.mainKey != key || !cacheable;
            cache.mainValid = true; cache.mainSize = size; cache.mainKey = key;
            cache.mainCacheable = cacheable;
            return update;
        }

        bool NeedsLocalShadowUpdate(ShadowCache cache, CullingResults culling, int size)
        {
            int key = MixShadowKey(17, size);
            key = MixShadowKey(key, m_LocalShadowGrid);
            key = MixShadowKey(key, m_LocalShadowCount);
            key = MixShadowKey(key, m_ShadowSettings.shadowFilterSize);
            bool cacheable = m_LocalShadowCount > 0;
            for (int i = 0; i < m_LocalLightCount; i++)
            {
                var info = m_LocalShadowInfo[i];
                if (info.y <= 0) continue;
                var light = culling.visibleLights[m_LocalVisibleIndices[i]].light;
                cacheable &= CacheLightShadows(light, out var data);
                key = MixShadowKey(key, (int)info.x);
                key = MixShadowKey(key, (int)info.z);
                key = LightShadowKey(key, light, data);
                for (int face = 0; face < (int)info.z; face++)
                {
                    int slice = (int)info.x + face;
                    key = MixShadowKey(key, m_LocalShadowViews[slice].GetHashCode());
                    key = MixShadowKey(key, m_LocalShadowProjections[slice].GetHashCode());
                }
            }
            bool update = !cache.localValid || cache.localSize != size || cache.localKey != key || !cacheable;
            cache.localValid = true; cache.localSize = size; cache.localKey = key;
            cache.localCacheable = cacheable;
            return update;
        }

        void PruneShadowCaches()
        {
            m_ExpiredShadowCameras.Clear();
            foreach (var entry in m_ShadowCaches)
                if (entry.Key == null || !entry.Key.gameObject.activeInHierarchy)
                    m_ExpiredShadowCameras.Add(entry.Key);
            foreach (var camera in m_ExpiredShadowCameras)
            {
                var cache = m_ShadowCaches[camera];
                cache.mainAtlas?.Release(); cache.localAtlas?.Release();
                m_ShadowCaches.Remove(camera);
            }
            m_ExpiredShadowCameras.Clear();
        }

        void DisposeShadowCaches()
        {
            foreach (var cache in m_ShadowCaches.Values)
            {
                cache.mainAtlas?.Release(); cache.localAtlas?.Release();
            }
            m_ShadowCaches.Clear();
        }

        internal static int ShadowAtlasSize(int requested) => Mathf.NextPowerOfTwo(Mathf.Clamp(requested, 256, 4096));
        internal static int CascadeGrid(int count) => count <= 1 ? 1 : count <= 4 ? 2 : 3;

        // Unity exposes only three ratios per native call. Use overlapping groups of
        // four splits so the native near/far bounds and preceding exclusion sphere
        // remain identical at the group boundary. Never pass a native count > 4.
        internal static void GetNativeCascade(int count, int cascade, float firstSplit,
            out int nativeIndex, out int nativeCount, out Vector3 ratios)
        {
            count = Mathf.Clamp(count, 1, 6);
            nativeCount = Mathf.Min(count, 4);
            int offset = count > 4 && cascade >= 3 ? count - 4 : 0;
            nativeIndex = cascade - offset;
            firstSplit = Mathf.Clamp(firstSplit, 0.01f, 0.95f);
            ratios = Vector3.zero;
            for (int i = 0; i < nativeCount - 1; i++)
                ratios[i] = firstSplit + (1f - firstSplit) * (i + offset) / (count - 1);
        }

        static T ShadowValue<T>(VolumeParameter<T> parameter, T fallback, bool active)
            => active && parameter.overrideState ? parameter.value : fallback;

        void ResolveShadowSettings(MobileRenderSettings asset)
        {
            var volume = m_Volumes.GetComponent<MobileShadows>();
            bool active = volume != null && volume.active;
            // A missing component can occur briefly while scripts are reloaded.
            m_ShadowSettings.mainLightShadows = active ? ShadowValue(volume.mainLightShadows, asset.mainLightShadows, true) : asset.mainLightShadows;
            m_ShadowSettings.shadowAtlasSize = active ? ShadowValue(volume.shadowAtlasSize, asset.shadowAtlasSize, true) : asset.shadowAtlasSize;
            m_ShadowSettings.shadowDistance = Mathf.Max(0.1f, active ? ShadowValue(volume.shadowDistance, asset.shadowDistance, true) : asset.shadowDistance);
            m_ShadowSettings.cascadeCount = Mathf.Clamp(active ? ShadowValue(volume.cascadeCount, asset.cascadeCount, true) : asset.cascadeCount, 1, 6);
            m_ShadowSettings.cascadeSplit = Mathf.Clamp(active ? ShadowValue(volume.cascadeSplit, asset.cascadeSplit, true) : asset.cascadeSplit, 0.01f, 0.95f);
            m_ShadowSettings.cascadeBlend = Mathf.Clamp(active ? ShadowValue(volume.cascadeBlend, asset.cascadeBlend, true) : asset.cascadeBlend, 0, 0.3f);
            m_ShadowSettings.shadowNormalBias = Mathf.Max(0, active ? ShadowValue(volume.shadowNormalBias, asset.shadowNormalBias, true) : asset.shadowNormalBias);
            m_ShadowSettings.localLightShadows = active ? ShadowValue(volume.localLightShadows, asset.localLightShadows, true) : asset.localLightShadows;
            m_ShadowSettings.localShadowAtlasSize = active ? ShadowValue(volume.localShadowAtlasSize, asset.localShadowAtlasSize, true) : asset.localShadowAtlasSize;
            m_ShadowSettings.shadowFilterSize = Mathf.Clamp(active ? ShadowValue(volume.shadowFilterSize, asset.shadowFilterSize, true) : asset.shadowFilterSize, 1, 4);
        }

        static Matrix4x4 ShadowAtlasMatrix(Matrix4x4 projection, Matrix4x4 view, int slice, int grid, int tileSize, int atlasSize)
        {
            var matrix = projection * view;
            if (SystemInfo.usesReversedZBuffer)
                for (int column = 0; column < 4; column++) matrix[2, column] = -matrix[2, column];
            var bias = Matrix4x4.identity;
            bias.m00 = bias.m11 = bias.m22 = 0.5f;
            bias.m03 = bias.m13 = bias.m23 = 0.5f;
            var tile = Matrix4x4.identity;
            tile.m00 = tile.m11 = (float)tileSize / atlasSize;
            tile.m03 = (slice % grid) * tile.m00;
            tile.m13 = (slice / grid) * tile.m11;
            return tile * bias * matrix;
        }

        void PrepareLocalShadows(ref CullingResults culling)
        {
            Array.Clear(m_LocalShadowInfo, 0, m_LocalShadowInfo.Length);
            m_LocalShadowCount = 0;
            m_LocalShadowGrid = 1;
            if (!m_ShadowSettings.localLightShadows) return;
            for (int i = 0; i < m_LocalLightCount; i++)
            {
                int index = m_LocalVisibleIndices[i];
                var visible = culling.visibleLights[index];
                var light = visible.light;
                if (light == null || light.shadows == LightShadows.None || light.shadowStrength <= 0
                    || !culling.GetShadowCasterBounds(index, out _)) continue;
                int faces = visible.lightType == LightType.Point ? 6 : 1;
                m_LocalShadowInfo[i] = new Vector4(m_LocalShadowCount, light.shadowStrength, faces, 0);
                m_LocalShadowCount += faces;
            }
            m_LocalShadowGrid = Mathf.NextPowerOfTwo(Mathf.Max(1, Mathf.CeilToInt(Mathf.Sqrt(m_LocalShadowCount))));
            int atlasSize = ShadowAtlasSize(m_ShadowSettings.localShadowAtlasSize);
            int tileSize = atlasSize / m_LocalShadowGrid;
            // Expand cube faces enough to retain a guard region at PCF seams.
            float fovBias = 2f * Mathf.Atan(1f + 2f * m_ShadowSettings.shadowFilterSize / tileSize) * Mathf.Rad2Deg - 90f;
            for (int i = 0; i < m_LocalLightCount; i++)
            {
                var info = m_LocalShadowInfo[i];
                if (info.y <= 0) continue;
                int index = m_LocalVisibleIndices[i];
                for (int face = 0; face < (int)info.z; face++)
                {
                    int slice = (int)info.x + face;
                    bool valid;
                    if (info.z == 6)
                    {
                        valid = culling.ComputePointShadowMatricesAndCullingPrimitives(index, (CubemapFace)face, fovBias,
                            out m_LocalShadowViews[slice], out m_LocalShadowProjections[slice], out m_LocalShadowSplits[slice]);
                        // Native cubemap views have inverted winding relative to 2D shadow maps.
                        for (int column = 0; column < 4; column++) m_LocalShadowViews[slice][1, column] *= -1;
                    }
                    else
                        valid = culling.ComputeSpotShadowMatricesAndCullingPrimitives(index,
                            out m_LocalShadowViews[slice], out m_LocalShadowProjections[slice], out m_LocalShadowSplits[slice]);
                    if (!valid) { m_LocalShadowInfo[i] = Vector4.zero; break; }
                    m_LocalShadowLights[slice] = index;
                    m_LocalShadowMatrices[slice] = ShadowAtlasMatrix(m_LocalShadowProjections[slice], m_LocalShadowViews[slice], slice, m_LocalShadowGrid, tileSize, atlasSize);
                }
            }
        }

        class LocalShadowData
        {
            internal Camera camera;
            internal CullingResults culling;
            internal int grid, size, count;
            internal readonly Matrix4x4[] views = new Matrix4x4[MaxLocalShadowSlices], projections = new Matrix4x4[MaxLocalShadowSlices];
            internal readonly ShadowSplitData[] splits = new ShadowSplitData[MaxLocalShadowSlices];
            internal readonly int[] lights = new int[MaxLocalShadowSlices];
            internal readonly bool[] valid = new bool[MaxLocalShadowSlices];
        }

        void RecordLocalShadows(Camera camera, CullingResults culling, TextureHandle atlas, int size)
        {
            if (m_LocalShadowCount > 0) localShadowDrawCount++;
            using (var builder = m_Graph.AddRenderPass<LocalShadowData>("Mobile Local Shadows", out var data))
            {
                data.camera = camera; data.culling = culling; data.grid = m_LocalShadowGrid; data.size = size; data.count = m_LocalShadowCount;
                Array.Copy(m_LocalShadowViews, data.views, data.count); Array.Copy(m_LocalShadowProjections, data.projections, data.count);
                Array.Copy(m_LocalShadowSplits, data.splits, data.count); Array.Copy(m_LocalShadowLights, data.lights, data.count);
                Array.Clear(data.valid, 0, data.valid.Length);
                foreach (var info in m_LocalShadowInfo)
                    if (info.y > 0)
                        for (int face = 0; face < (int)info.z; face++) data.valid[(int)info.x + face] = true;
                builder.UseDepthBuffer(atlas, DepthAccess.Write);
                builder.SetRenderFunc((LocalShadowData pass, RenderGraphContext ctx) =>
                {
                    ctx.cmd.ClearRenderTarget(true, false, Color.clear);
                    if (pass.count == 0) return;
                    int tile = pass.size / pass.grid;
                    ctx.cmd.EnableShaderKeyword(MobileKeyword);
                    try
                    {
                        for (int slice = 0; slice < pass.count; slice++)
                        {
                            if (!pass.valid[slice]) continue;
                            var light = pass.culling.visibleLights[pass.lights[slice]].light;
                            Vector3 position = light.transform.position;
                            ctx.cmd.SetGlobalVector("_MobileShadowLightPosition", new Vector4(position.x, position.y, position.z, 1));
                            // Perspective texel width grows with distance; shader applies that factor per vertex.
                            ctx.cmd.SetGlobalFloat("_MobileShadowNormalBias", GetShadowNormalBias(light.shadowNormalBias, pass.projections[slice], tile));
                            ctx.cmd.SetGlobalFloat("_MobileShadowDepthBias", GetShadowNormalBias(light.shadowBias, pass.projections[slice], tile));
                            ctx.cmd.SetGlobalDepthBias(1f, 2.5f);
                            ctx.cmd.SetViewport(new Rect((slice % pass.grid) * tile, (slice / pass.grid) * tile, tile, tile));
                            ctx.cmd.SetViewProjectionMatrices(pass.views[slice], pass.projections[slice]);
                            ctx.renderContext.ExecuteCommandBuffer(ctx.cmd); ctx.cmd.Clear();
                            var draw = new ShadowDrawingSettings(pass.culling, pass.lights[slice], BatchCullingProjectionType.Perspective) { splitData = pass.splits[slice] };
                            ctx.renderContext.DrawShadows(ref draw);
                        }
                    }
                    finally
                    {
                        ctx.cmd.Clear();
                        ctx.cmd.SetGlobalDepthBias(0, 0);
                        ctx.cmd.SetGlobalVector("_MobileShadowLightPosition", Vector4.zero);
                        try { ctx.renderContext.ExecuteCommandBuffer(ctx.cmd); }
                        finally { ctx.cmd.Clear(); ctx.renderContext.SetupCameraProperties(pass.camera); }
                    }
                });
            }
        }
    }
}
