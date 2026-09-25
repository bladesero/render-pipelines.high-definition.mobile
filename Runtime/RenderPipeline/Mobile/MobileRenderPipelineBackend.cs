using System;
using Unity.Collections;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Experimental.Rendering.RenderGraphModule;

namespace UnityEngine.Rendering.HighDefinition
{
    // No HDCamera, desktop resources or compute light lists.
    internal sealed partial class MobileRenderPipelineBackend : IDisposable
    {
        const int MaxVisibleLights = 32;
        const string MobileKeyword = "HDRP_MOBILE_RENDERER";
        static readonly ShaderTagId ForwardTag = new ShaderTagId("MobileForward");
        static MobileRenderPipelineBackend s_ActiveOwner;
        // Set only for the explicit editor bake camera; ordinary cameras are unaffected.
        internal static Camera reflectionCaptureCamera;
        readonly HDRenderPipelineAsset m_Asset;
        readonly RenderGraph m_Graph;
        readonly Material m_Composite, m_Sky, m_Error;
        readonly VolumeStack m_Volumes;
        readonly MobileExposureHistory m_ExposureHistory = new MobileExposureHistory();
        readonly MobileEnvironmentCache m_Environment = new MobileEnvironmentCache();
        readonly MobileReflectionAtlas m_ReflectionAtlas = new MobileReflectionAtlas();
        MobileEnvironmentCache.State m_CameraEnvironment;
        readonly Volume m_DefaultVolume;
        readonly MobileErrorRenderer m_ErrorRenderer = new MobileErrorRenderer();
        readonly System.Collections.Generic.Dictionary<ReflectionProbe, Texture> m_OriginalProbeTextures = new System.Collections.Generic.Dictionary<ReflectionProbe, Texture>();
        readonly System.Collections.Generic.Dictionary<ReflectionProbe, Vector2> m_OriginalProbeSettings = new System.Collections.Generic.Dictionary<ReflectionProbe, Vector2>();
        readonly System.Collections.Generic.HashSet<ReflectionProbe> m_ActiveBakedProbes = new System.Collections.Generic.HashSet<ReflectionProbe>();
        readonly System.Collections.Generic.List<ReflectionProbe> m_ExpiredProbes = new System.Collections.Generic.List<ReflectionProbe>();
        readonly Vector4[] m_Positions = new Vector4[MaxVisibleLights];
        readonly Vector4[] m_Colors = new Vector4[MaxVisibleLights];
        readonly Vector4[] m_Spots = new Vector4[MaxVisibleLights];
        readonly Vector4[] m_SpotAttenuations = new Vector4[MaxVisibleLights];
        readonly Matrix4x4[] m_ShadowMatrices = new Matrix4x4[6];
        readonly Vector4[] m_ShadowSpheres = new Vector4[6];
        readonly Matrix4x4[] m_ShadowViews = new Matrix4x4[6];
        readonly Matrix4x4[] m_ShadowProjections = new Matrix4x4[6];
        readonly ShadowSplitData[] m_ShadowSplits = new ShadowSplitData[6];
        readonly bool m_PreviousBatcher, m_PreviousLinear, m_PreviousTemperature, m_PreviousKeyword, m_PreviousExtendedKeyword;
        readonly SupportedRenderingFeatures m_PreviousFeatures;
        readonly string m_PreviousShaderTag;
        int m_MainLight = -1;
        Vector4 m_MainDirection, m_MainColor;
        bool m_Disposed;
        // Time.frameCount does not advance reliably for edit-mode Scene View repaints.
        // RenderGraph's pool needs an advancing index to retire old resize allocations.
        int m_RenderGraphFrameIndex;

        internal bool CanUpdateInPlace(HDRenderPipelineAsset asset) => !m_Disposed && asset == m_Asset
            && asset.rendererMode == HDRendererMode.Mobile
            && m_Composite != null && m_Composite.shader == asset.mobileResources.compositeShader
            && m_Sky != null && m_Sky.shader == asset.mobileResources.skyShader
            && m_Error != null && m_Error.shader == asset.mobileResources.errorShader
            && (m_DeferredMaterials == null || m_DeferredMaterials[0].shader == asset.mobileResources.deferredShader);

        internal MobileRenderPipelineBackend(HDRenderPipelineAsset asset, Action<string> initializationCheckpoint = null)
        {
            m_Asset = asset;
            if (s_ActiveOwner != null)
                foreach (var entry in s_ActiveOwner.m_OriginalProbeTextures) m_OriginalProbeTextures.Add(entry.Key, entry.Value);
            if (s_ActiveOwner != null)
                foreach (var entry in s_ActiveOwner.m_OriginalProbeSettings) m_OriginalProbeSettings.Add(entry.Key, entry.Value);
            m_PreviousBatcher = s_ActiveOwner != null ? s_ActiveOwner.m_PreviousBatcher : GraphicsSettings.useScriptableRenderPipelineBatching;
            m_PreviousLinear = s_ActiveOwner != null ? s_ActiveOwner.m_PreviousLinear : GraphicsSettings.lightsUseLinearIntensity;
            m_PreviousTemperature = s_ActiveOwner != null ? s_ActiveOwner.m_PreviousTemperature : GraphicsSettings.lightsUseColorTemperature;
            m_PreviousKeyword = s_ActiveOwner != null ? s_ActiveOwner.m_PreviousKeyword : Shader.IsKeywordEnabled(MobileKeyword);
            m_PreviousExtendedKeyword = s_ActiveOwner != null ? s_ActiveOwner.m_PreviousExtendedKeyword : Shader.IsKeywordEnabled("MOBILE_EXTENDED_LIT");
            m_PreviousFeatures = s_ActiveOwner != null ? s_ActiveOwner.m_PreviousFeatures : SupportedRenderingFeatures.active;
            m_PreviousShaderTag = s_ActiveOwner != null ? s_ActiveOwner.m_PreviousShaderTag : Shader.globalRenderPipeline;
            try
            {
                m_Graph = new RenderGraph("HDRP Mobile");
                initializationCheckpoint?.Invoke("Graph");
                m_Composite = CoreUtils.CreateEngineMaterial(asset.mobileResources.compositeShader);
                m_Sky = CoreUtils.CreateEngineMaterial(asset.mobileResources.skyShader);
                m_Error = CoreUtils.CreateEngineMaterial(asset.mobileResources.errorShader);
                initializationCheckpoint?.Invoke("Materials");
                m_Volumes = VolumeManager.instance.CreateStack();
                initializationCheckpoint?.Invoke("VolumeStack");
                var profile = HDRenderPipelineGlobalSettings.instance?.volumeProfile;
                if (profile != null)
                {
                    var go = new GameObject("HDRP Mobile Default Volume") { hideFlags = HideFlags.HideAndDontSave };
                    m_DefaultVolume = go.AddComponent<Volume>();
                    m_DefaultVolume.isGlobal = true; m_DefaultVolume.priority = float.MinValue;
                    m_DefaultVolume.sharedProfile = profile;
                }
                initializationCheckpoint?.Invoke("DefaultVolume");
                s_ActiveOwner = this;
                MobileExposureControl.active = m_ExposureHistory;
                MobileEnvironmentControl.active = m_Environment;
                Shader.globalRenderPipeline = HDRenderPipeline.k_ShaderTagName;
                GraphicsSettings.useScriptableRenderPipelineBatching = true;
                GraphicsSettings.lightsUseLinearIntensity = true;
                GraphicsSettings.lightsUseColorTemperature = true;
                Shader.EnableKeyword(MobileKeyword);
                SupportedRenderingFeatures.active = new SupportedRenderingFeatures
                {
                    defaultMixedLightingModes = SupportedRenderingFeatures.LightmapMixedBakeModes.IndirectOnly,
                    mixedLightingModes = SupportedRenderingFeatures.LightmapMixedBakeModes.IndirectOnly,
                    lightmapBakeTypes = LightmapBakeType.Baked | LightmapBakeType.Mixed,
                    lightmapsModes = LightmapsMode.NonDirectional | LightmapsMode.CombinedDirectional,
                    lightProbeProxyVolumes = false, motionVectors = false, receiveShadows = true,
                    reflectionProbes = true, overridesFog = true, enlighten = false,
                    reflectionProbeModes = SupportedRenderingFeatures.ReflectionProbeModes.Rotation,
                    rendersUIOverlay = false
                };
            }
            catch { Dispose(); throw; }
        }

        internal void EndFrame()
        {
            if (m_Disposed) return;
            m_ExposureHistory.Prune(); m_Environment.Prune();
            m_Graph.EndFrame();
            ++m_RenderGraphFrameIndex;
            PruneDeferredDepths();
        }

        internal void Render(ScriptableRenderContext context, Camera camera)
        {
            bool capture = camera != null && camera == reflectionCaptureCamera;
            if (m_Disposed || camera == null || camera.stereoEnabled || (camera.cameraType == CameraType.Reflection && !capture))
                return;
            SynchronizeBakedReflectionProbes();
            // HDRP stores baked textures separately. Flush changes into the native
            // spatial cache before culling, including the first Camera.Render call.
            ReflectionProbe.UpdateCachedState();
            if (!camera.TryGetCullingParameters(out var parameters)) return;
            var settings = m_Asset.mobileSettings;
            camera.TryGetComponent<HDAdditionalCameraData>(out var additional);
            MobileExposure.GetVolumeContext(camera, m_Asset, out var anchor, out var mask);
            if (m_DefaultVolume != null) m_DefaultVolume.sharedProfile = HDRenderPipelineGlobalSettings.instance?.volumeProfile;
            VolumeManager.instance.Update(m_Volumes, anchor, mask);
            ResolveShadowSettings(settings);
            if (settings.drawUnsupportedMaterials) m_ErrorRenderer.Prepare(camera);
            parameters.shadowDistance = Mathf.Min(m_ShadowSettings.shadowDistance, camera.farClipPlane);
            parameters.cullingOptions &= ~CullingOptions.Stereo;
            parameters.cullingOptions |= CullingOptions.NeedsReflectionProbes;
#if UNITY_EDITOR
            if (camera.cameraType == CameraType.SceneView)
                ScriptableRenderContext.EmitWorldGeometryForSceneView(camera);
#endif
            var culling = context.Cull(ref parameters);
            PrepareLights(ref culling);
            bool shadows = PrepareShadows(ref culling, m_ShadowSettings);
            PrepareLocalShadows(ref culling);
            FrameSettings frameSettings = new FrameSettings();
            FrameSettings.AggregateFrameSettings(ref frameSettings, camera, additional, m_Asset);
            float exposure = MobileExposure.Evaluate(m_Volumes.GetComponent<Exposure>(), camera, frameSettings.IsEnabled(FrameSettingsField.ExposureControl));
            if (capture) exposure = 1f / Mathf.Max(additional != null ? additional.probeCustomFixedExposure : 1f, 1e-6f);
            var exposureVolume = m_Volumes.GetComponent<Exposure>();
            var exposureState = capture ? null : m_ExposureHistory.Prepare(camera, exposureVolume, frameSettings.IsEnabled(FrameSettingsField.ExposureControl));
            Vector2Int renderSize = capture ? new Vector2Int(camera.targetTexture.width, camera.targetTexture.height) : settings.GetRenderSize(camera);
            int width = renderSize.x, height = renderSize.y;
            bool nativeDeferred = false;
            bool deferred = !capture && ResolveDeferred(context, camera, culling, out nativeDeferred);
            var deferredDepth = deferred ? EnsureDeferredResources(camera, width, height) : null;
            if (!deferred) ReleaseDeferredDepth(camera);
            var cmd = CommandBufferPool.Get("HDRP Mobile");
            try
            {
                if (capture)
                {
                    // No feedback from the probe currently being baked or its old cube.
                    cmd.SetGlobalInt("_MobileReflectionHasHDRPProbes", 1);
                    cmd.SetGlobalInt("_MobileReflectionCount16", 0);
                }
                else m_ReflectionAtlas.Prepare(cmd, camera, culling, m_Sky, settings.reflectionAtlasSize);
                var sky = MobileSky.Resolve(settings, m_Volumes, RenderSettings.skybox, Mathf.Max(m_MainColor.x, Mathf.Max(m_MainColor.y, m_MainColor.z)));
                m_CameraEnvironment = m_Environment.Prepare(cmd, camera, sky, m_MainDirection, m_MainColor, settings, m_Sky);
                context.ExecuteCommandBuffer(cmd); cmd.Clear();
                MobileExposureHistory.Initialize(cmd, exposureState, exposureVolume);
                context.SetupCameraProperties(camera);
                using (m_Graph.RecordAndExecute(new RenderGraphParameters
                {
                    executionName = camera.name, currentFrameIndex = m_RenderGraphFrameIndex,
                    scriptableRenderContext = context, commandBuffer = cmd
                }))
                {
                    var color = m_Graph.CreateTexture(new TextureDesc(width, height)
                    {
                        name = "Mobile HDR Color", colorFormat = deferred ? GraphicsFormat.B10G11R11_UFloatPack32 : GraphicsFormat.R16G16B16A16_SFloat,
                        clearBuffer = !deferred, clearColor = (additional != null ? additional.backgroundColorHDR : camera.backgroundColor.linear) * (capture ? exposure : 1f), filterMode = FilterMode.Bilinear
                    });
                    var depth = deferred ? m_Graph.ImportTexture(deferredDepth) : m_Graph.CreateTexture(new TextureDesc(width, height)
                    {
                        name = "Mobile Depth", depthBufferBits = DepthBits.Depth24, clearBuffer = true
                    });
                    int atlasSize = shadows ? ShadowAtlasSize(m_ShadowSettings.shadowAtlasSize) : 1;
                    var atlas = m_Graph.CreateTexture(new TextureDesc(atlasSize, atlasSize)
                    {
                        name = "Mobile Main Shadow", depthBufferBits = DepthBits.Depth16,
                        isShadowMap = true, clearBuffer = true, filterMode = FilterMode.Bilinear
                    });
                    RecordShadows(camera, culling, atlas, atlasSize, shadows);
                    int localSize = m_LocalShadowCount > 0 ? ShadowAtlasSize(m_ShadowSettings.localShadowAtlasSize) : 1;
                    var localAtlas = m_Graph.CreateTexture(new TextureDesc(localSize, localSize)
                    {
                        name = "Mobile Local Shadow Atlas", depthBufferBits = DepthBits.Depth16,
                        isShadowMap = true, clearBuffer = true, filterMode = FilterMode.Bilinear
                    });
                    RecordLocalShadows(camera, culling, localAtlas, localSize);
                    RecordScene(camera, culling, color, depth, atlas, localAtlas, localSize, atlasSize, shadows, width, height, exposure, exposureState, deferred, nativeDeferred);
                    if (!capture) m_ExposureHistory.Record(m_Graph, color, exposureState, exposureVolume, m_Composite, (int)settings.meteringResolution);
                    TextureHandle bloom = m_Graph.defaultResources.blackTexture;
                    var bloomVolume = m_Volumes.GetComponent<Bloom>();
                    float bloomIntensity = bloomVolume.active && bloomVolume.IsActive() ? bloomVolume.intensity.value : 0f;
                    if (!capture && bloomIntensity > 0f)
                    {
                        var down0 = RecordFilter(color, Mathf.Max(1, width / 2), Mathf.Max(1, height / 2), 1, "Mobile Bloom Half");
                        var down1 = RecordFilter(down0, Mathf.Max(1, width / 4), Mathf.Max(1, height / 4), 2, "Mobile Bloom Quarter");
                        bloom = RecordFilter(down1, Mathf.Max(1, width / 8), Mathf.Max(1, height / 8), 2, "Mobile Bloom Eighth");
                    }
                    var output = m_Graph.ImportBackbuffer(camera.targetTexture != null
                        ? new RenderTargetIdentifier(camera.targetTexture) : new RenderTargetIdentifier(BuiltinRenderTextureType.CameraTarget));
                    if (capture) RecordReflectionCapture(color, output, width, height);
                    else RecordComposite(camera, color, bloom, output, width, height, bloomIntensity);
#if UNITY_EDITOR
                    if (!capture && (camera.cameraType == CameraType.SceneView || UnityEditor.Handles.ShouldRenderGizmos()))
                        RecordEditorGizmos(camera, depth, output);
#endif
                }
                context.ExecuteCommandBuffer(cmd);
                cmd.Clear();
                context.Submit();
                if (!capture) MobileExposureHistory.Commit(exposureState);
            }
            finally { CommandBufferPool.Release(cmd); }
        }

        internal void SynchronizeBakedReflectionProbes()
        {
            // Desktop HDRP keeps textures on HDProbe and only uses ReflectionProbe for
            // culling. Native per-object forward bindings need the baked cube on both.
            m_ActiveBakedProbes.Clear();
            foreach (var probe in HDProbeSystem.bakedProbes)
            {
                if (!(probe is HDAdditionalReflectionData) || !probe.isActiveAndEnabled) continue;
                probe.PrepareCulling();
                var legacy = probe.GetComponent<ReflectionProbe>();
                if (legacy == null) continue;
                m_ActiveBakedProbes.Add(legacy);
                if (!m_OriginalProbeTextures.ContainsKey(legacy)) m_OriginalProbeTextures.Add(legacy, legacy.customBakedTexture);
                if (!m_OriginalProbeSettings.ContainsKey(legacy)) m_OriginalProbeSettings.Add(legacy, new Vector2(legacy.boxProjection ? 1 : 0, legacy.blendDistance));
                bool boxProjection = !probe.isProjectionInfinite && probe.proxyVolume == null && probe.influenceVolume.shape == InfluenceShape.Box;
                if (legacy.boxProjection != boxProjection) legacy.boxProjection = boxProjection;
                var positive = probe.influenceVolume.boxBlendDistancePositive;
                var negative = probe.influenceVolume.boxBlendDistanceNegative;
                float blendDistance = Mathf.Min(Mathf.Min(positive.x, Mathf.Min(positive.y, positive.z)), Mathf.Min(negative.x, Mathf.Min(negative.y, negative.z)));
                // A zero HDRP face distance means a hard boundary, not the legacy default.
                float nativeBlend = Mathf.Max(0, blendDistance);
                if (legacy.blendDistance != nativeBlend) legacy.blendDistance = nativeBlend;
                if (legacy.customBakedTexture != probe.bakedTexture) legacy.customBakedTexture = probe.bakedTexture;
            }
            m_ExpiredProbes.Clear();
            foreach (var entry in m_OriginalProbeTextures)
            {
                if (m_ActiveBakedProbes.Contains(entry.Key)) continue;
                if (entry.Key != null)
                {
                    entry.Key.customBakedTexture = entry.Value;
                    RestoreProbeSettings(entry.Key);
                }
                m_ExpiredProbes.Add(entry.Key);
            }
            foreach (var probe in m_ExpiredProbes) { m_OriginalProbeTextures.Remove(probe); m_OriginalProbeSettings.Remove(probe); }
        }

        void RestoreProbeSettings(ReflectionProbe probe)
        {
            if (!m_OriginalProbeSettings.TryGetValue(probe, out var settings)) return;
            probe.boxProjection = settings.x > 0; probe.blendDistance = settings.y;
        }

        void PrepareLights(ref CullingResults culling)
        {
            var visible = culling.visibleLights;
            var indexMap = culling.GetLightIndexMap(Allocator.Temp);
            m_MainLight = -1;
            float brightest = -1f;
            for (int i = 0; i < visible.Length; i++)
            {
                var light = visible[i];
                if (light.lightType != LightType.Directional || light.light.bakingOutput.lightmapBakeType == LightmapBakeType.Baked) continue;
                float brightness = light.finalColor.maxColorComponent;
                if (light.light == RenderSettings.sun) { m_MainLight = i; break; }
                if (brightness > brightest) { brightest = brightness; m_MainLight = i; }
            }
            m_MainColor = Vector4.zero;
            m_MainDirection = new Vector4(0, 1, 0, 0);
            if (m_MainLight >= 0)
            {
                m_MainDirection = -visible[m_MainLight].localToWorldMatrix.GetColumn(2);
                m_MainColor = visible[m_MainLight].finalColor;
            }
            int count = 0;
            for (int i = 0; i < indexMap.Length; i++)
            {
                indexMap[i] = -1;
                if (i >= visible.Length || count == MaxVisibleLights) continue;
                var light = visible[i];
                if (light.light.bakingOutput.lightmapBakeType == LightmapBakeType.Baked) continue;
                if (light.lightType != LightType.Point && light.lightType != LightType.Spot) continue;
                indexMap[i] = count;
                m_LocalVisibleIndices[count] = i;
                Vector4 position = light.localToWorldMatrix.GetColumn(3);
                position.w = 1f / Mathf.Max(0.0001f, light.range * light.range);
                m_Positions[count] = position;
                // HDAdditionalLightData converts authoring units to lux/candela in Light.intensity.
                // VisibleLight.finalColor includes that conversion and color temperature already.
                m_Colors[count] = light.finalColor;
                m_Spots[count] = -light.localToWorldMatrix.GetColumn(2);
                m_SpotAttenuations[count] = new Vector4(0, 1, 0, 0);
                if (light.lightType == LightType.Spot)
                {
                    float outer = Mathf.Cos(light.spotAngle * Mathf.Deg2Rad * 0.5f);
                    float inner = Mathf.Cos(light.light.innerSpotAngle * Mathf.Deg2Rad * 0.5f);
                    float scale = 1f / Mathf.Max(0.001f, inner - outer);
                    m_SpotAttenuations[count] = new Vector4(scale, -outer * scale, 0, 0);
                }
                count++;
            }
            m_LocalLightCount = count;
            culling.SetLightIndexMap(indexMap);
            indexMap.Dispose();
        }

        bool PrepareShadows(ref CullingResults culling, MobileRenderSettings settings)
        {
            if (!settings.mainLightShadows || m_MainLight < 0) return false;
            var light = culling.visibleLights[m_MainLight].light;
            if (light == null || light.shadows == LightShadows.None || light.shadowStrength <= 0f
                || !culling.GetShadowCasterBounds(m_MainLight, out _)) return false;
            int grid = CascadeGrid(settings.cascadeCount);
            int tileSize = ShadowAtlasSize(settings.shadowAtlasSize) / grid;
            for (int cascade = 0; cascade < settings.cascadeCount; cascade++)
            {
                GetNativeCascade(settings.cascadeCount, cascade, settings.cascadeSplit, out int nativeIndex, out int nativeCount, out var ratios);
                if (!culling.ComputeDirectionalShadowMatricesAndCullingPrimitives(m_MainLight, nativeIndex, nativeCount,
                    ratios, tileSize, light.shadowNearPlane,
                    out m_ShadowViews[cascade], out m_ShadowProjections[cascade], out m_ShadowSplits[cascade])) return false;
                // Shrink the exclusion sphere of preceding splits so their outer
                // blend band is also rendered into the following cascade.
                m_ShadowSplits[cascade].shadowCascadeBlendCullingFactor = 1f - Mathf.Clamp(settings.cascadeBlend, 0f, 0.3f);
                var sphere = m_ShadowSplits[cascade].cullingSphere;
                sphere.w *= sphere.w;
                m_ShadowSpheres[cascade] = sphere;
                m_ShadowMatrices[cascade] = ShadowAtlasMatrix(m_ShadowProjections[cascade], m_ShadowViews[cascade], cascade, grid, tileSize, ShadowAtlasSize(settings.shadowAtlasSize));
            }
            return true;
        }

        internal static float GetShadowNormalBias(float biasInTexels, Matrix4x4 projection, int tileResolution)
        {
            // Directional projections span two clip-space units. Use the cascade tile,
            // not the full atlas, to convert shadow texels into world-space distance.
            return Mathf.Max(0f, biasInTexels) * 2f / (Mathf.Abs(projection.m00) * tileResolution);
        }

        class ShadowData
        {
            internal Camera camera;
            internal CullingResults culling;
            internal int size, mainLight, count, grid;
            internal float normalBias;
            internal Vector4 mainDirection;
            internal readonly Matrix4x4[] views = new Matrix4x4[6], projections = new Matrix4x4[6];
            internal readonly ShadowSplitData[] splits = new ShadowSplitData[6];
            internal bool enabled;
        }

        void RecordShadows(Camera camera, CullingResults culling, TextureHandle atlas, int size, bool enabled)
        {
            using (var builder = m_Graph.AddRenderPass<ShadowData>("Mobile Main Shadow", out var data))
            {
                data.camera = camera; data.culling = culling; data.size = size; data.enabled = enabled;
                data.mainLight = m_MainLight; data.mainDirection = m_MainDirection;
                data.normalBias = m_ShadowSettings.shadowNormalBias;
                data.count = m_ShadowSettings.cascadeCount; data.grid = CascadeGrid(data.count);
                Array.Copy(m_ShadowViews, data.views, 6); Array.Copy(m_ShadowProjections, data.projections, 6);
                Array.Copy(m_ShadowSplits, data.splits, 6);
                builder.UseDepthBuffer(atlas, DepthAccess.Write);
                builder.SetRenderFunc((ShadowData pass, RenderGraphContext ctx) =>
                {
                    if (!pass.enabled) return;
                    int tile = pass.size / pass.grid;
                    ctx.cmd.EnableShaderKeyword(MobileKeyword);
                    ctx.cmd.SetGlobalVector("_MobileMainDirection", pass.mainDirection);
                    ctx.cmd.SetGlobalVector("_MobileShadowLightPosition", Vector4.zero);
                    ctx.cmd.SetGlobalDepthBias(1f, 2.5f);
                    try
                    {
                        for (int i = 0; i < pass.count; i++)
                        {
                            ctx.cmd.SetGlobalFloat("_MobileShadowNormalBias", GetShadowNormalBias(pass.normalBias, pass.projections[i], tile));
                            ctx.cmd.SetViewport(new Rect((i % pass.grid) * tile, (i / pass.grid) * tile, tile, tile));
                            ctx.cmd.SetViewProjectionMatrices(pass.views[i], pass.projections[i]);
                            ctx.renderContext.ExecuteCommandBuffer(ctx.cmd); ctx.cmd.Clear();
                            var draw = new ShadowDrawingSettings(pass.culling, pass.mainLight, BatchCullingProjectionType.Orthographic) { splitData = pass.splits[i] };
                            ctx.renderContext.DrawShadows(ref draw);
                        }
                    }
                    finally
                    {
                        ctx.cmd.Clear();
                        ctx.cmd.SetGlobalDepthBias(0, 0);
                        try { ctx.renderContext.ExecuteCommandBuffer(ctx.cmd); }
                        finally { ctx.cmd.Clear(); ctx.renderContext.SetupCameraProperties(pass.camera); }
                    }
                });
            }
        }

        class SceneData
        {
            internal bool deferred, nativeDeferred, extendedLit;
            internal TextureHandle color, depth, gbuffer0, gbuffer1, depthCopy;
            internal TextureHandle gbuffer3;
            internal MobileRenderPipelineBackend owner;
            internal Camera camera;
            internal CullingResults culling;
            internal TextureHandle atlas, localAtlas;
            internal int cascadeCount, shadowFilter, localGrid;
            internal float localTexel;
            internal readonly Matrix4x4[] localMatrices = new Matrix4x4[MaxLocalShadowSlices];
            internal readonly Vector4[] localShadows = new Vector4[MaxVisibleLights];
            internal int width, height, atlasSize;
            internal bool shadows;
            internal Matrix4x4 vp;
            internal Vector4 position, forward, mainDirection, mainColor, shadowParams, fog;
            internal Color fogColor;
            internal int mainLight, lightsPerObject;
            internal bool drawErrors, drawSky;
            internal MobileSky sky;
            internal float exposure, deExposure;
            internal TextureHandle exposureTexture;
            internal bool automaticExposure;
            internal MobileEnvironmentCache.State environment;
            internal Vector4 indirect;
            internal bool bakedProbes;
            internal readonly Vector4[] positions = new Vector4[MaxVisibleLights], colors = new Vector4[MaxVisibleLights],
                spots = new Vector4[MaxVisibleLights], attenuations = new Vector4[MaxVisibleLights], spheres = new Vector4[6];
            internal readonly Matrix4x4[] shadowMatrices = new Matrix4x4[6];
        }

        void RecordScene(Camera camera, CullingResults culling, TextureHandle color, TextureHandle depth,
            TextureHandle atlas, TextureHandle localAtlas, int localSize, int atlasSize, bool shadows, int width, int height, float exposure, MobileExposureHistory.State exposureState,
            bool deferred = false, bool nativeDeferred = false)
        {
            using (var builder = m_Graph.AddRenderPass<SceneData>(deferred ? "Mobile Compact Deferred" : "Mobile Forward", out var data))
            {
                data.owner = this; data.camera = camera; data.culling = culling; data.width = width;
                data.height = height; data.atlasSize = atlasSize; data.shadows = shadows;
                var settings = m_Asset.mobileSettings;
                data.vp = GL.GetGPUProjectionMatrix(camera.projectionMatrix, true) * camera.worldToCameraMatrix;
                var inverseView = camera.worldToCameraMatrix.inverse;
                data.position = inverseView.GetColumn(3);
                Vector3 forward = -inverseView.GetColumn(2);
                forward.Normalize();
                data.forward = new Vector4(forward.x, forward.y, forward.z, camera.orthographic ? 1 : 0);
                data.mainLight = m_MainLight; data.mainDirection = m_MainDirection; data.mainColor = m_MainColor;
                Array.Copy(m_Positions, data.positions, MaxVisibleLights); Array.Copy(m_Colors, data.colors, MaxVisibleLights);
                Array.Copy(m_Spots, data.spots, MaxVisibleLights); Array.Copy(m_SpotAttenuations, data.attenuations, MaxVisibleLights);
                Array.Copy(m_ShadowMatrices, data.shadowMatrices, 6); Array.Copy(m_ShadowSpheres, data.spheres, 6);
                data.cascadeCount = m_ShadowSettings.cascadeCount; data.shadowFilter = m_ShadowSettings.shadowFilterSize;
                data.localGrid = m_LocalShadowGrid; data.localTexel = 1f / localSize;
                Array.Copy(m_LocalShadowMatrices, data.localMatrices, MaxLocalShadowSlices);
                Array.Copy(m_LocalShadowInfo, data.localShadows, MaxVisibleLights);
                data.lightsPerObject = Mathf.Clamp(settings.additionalLightsPerObject, 0, 4);
                data.shadowParams = new Vector4(shadows ? culling.visibleLights[m_MainLight].light.shadowStrength : 0,
                    1f / atlasSize, Mathf.Min(m_ShadowSettings.shadowDistance, camera.farClipPlane), m_ShadowSettings.cascadeBlend);
                data.fog = new Vector4(settings.fog ? 1 : 0, settings.fogStart, 1f / Mathf.Max(0.01f, settings.fogEnd - settings.fogStart), 0);
                data.fogColor = settings.fogColor.linear;
                data.drawErrors = settings.drawUnsupportedMaterials;
                camera.TryGetComponent<HDAdditionalCameraData>(out var additional);
                data.exposure = exposure;
                data.automaticExposure = exposureState != null;
                data.exposureTexture = builder.ReadTexture(exposureState != null ? m_Graph.ImportTexture(exposureState.read) : m_Graph.defaultResources.whiteTexture);
                data.deExposure = additional != null ? additional.deExposureMultiplier : 1f;
                data.drawSky = additional != null ? additional.clearColorMode == HDAdditionalCameraData.ClearColorMode.Sky : camera.clearFlags == CameraClearFlags.Skybox;
                data.sky = MobileSky.Resolve(settings, m_Volumes, RenderSettings.skybox, Mathf.Max(m_MainColor.x, Mathf.Max(m_MainColor.y, m_MainColor.z)));
                data.environment = m_CameraEnvironment;
                data.bakedProbes = settings.environmentDiffuseSource == MobileEnvironmentDiffuseSource.PreferBakedProbes && LightmapSettings.lightProbes != null && LightmapSettings.lightProbes.count > 0;
                var indirect = m_Volumes.GetComponent<IndirectLightingController>();
                data.indirect = indirect.active ? new Vector4(indirect.indirectDiffuseLightingMultiplier.value, indirect.reflectionLightingMultiplier.value, indirect.reflectionProbeIntensityMultiplier.value, 0) : Vector4.one;
                data.atlas = builder.ReadTexture(atlas);
                data.localAtlas = builder.ReadTexture(localAtlas);
                data.deferred = deferred; data.nativeDeferred = nativeDeferred;
                data.extendedLit = UseExtendedDeferred;
                if (deferred)
                {
                    data.color = builder.WriteTexture(color); data.depth = builder.WriteTexture(depth);
                    if (!nativeDeferred)
                    {
                        data.gbuffer0 = builder.CreateTransientTexture(new TextureDesc(width, height) { name = "Mobile GBuffer0", colorFormat = GraphicsFormat.R8G8B8A8_SRGB });
                        data.gbuffer1 = builder.CreateTransientTexture(new TextureDesc(width, height) { name = "Mobile GBuffer1", colorFormat = GraphicsFormat.R8G8B8A8_UNorm });
                        data.depthCopy = builder.CreateTransientTexture(new TextureDesc(width, height) { name = "Mobile Depth Copy", colorFormat = GraphicsFormat.R32_SFloat });
                    }
                    if (data.extendedLit && !nativeDeferred)
                        data.gbuffer3 = builder.CreateTransientTexture(new TextureDesc(width, height) { name = "Mobile Fresnel Coat RGBA8", colorFormat = GraphicsFormat.R8G8B8A8_UNorm });
                }
                else { builder.UseColorBuffer(color, 0); builder.UseDepthBuffer(depth, DepthAccess.ReadWrite); }
                builder.SetRenderFunc((SceneData pass, RenderGraphContext ctx) => pass.owner.DrawScene(pass, ctx));
            }
        }

        void DrawScene(SceneData pass, RenderGraphContext ctx)
        {
            CoreUtils.SetKeyword(ctx.cmd, "MOBILE_EXTENDED_LIT", true);
            ctx.cmd.SetGlobalFloat("_MobileDeferredExtendedLayout", pass.extendedLit ? 1 : 0);
            BindMobileDiffusionProfiles(ctx.cmd);
            bool environment = pass.environment != null && pass.environment.ready;
            ctx.cmd.SetGlobalVector("_MobileEnvironmentParams", new Vector4(environment ? 1 : 0, pass.bakedProbes ? 1 : 0, environment ? pass.environment.mips - 1 : 0, 1024));
            ctx.cmd.SetGlobalVector("_MobileIndirectMultiplier", pass.indirect);
            ctx.cmd.SetGlobalTexture("_MobileEnvironmentDiffuse", environment ? (Texture)pass.environment.diffuse : CoreUtils.blackCubeTexture);
            ctx.cmd.SetGlobalTexture("_MobileEnvironmentReflection", environment ? (Texture)pass.environment.reflection : CoreUtils.blackCubeTexture);
            ctx.cmd.SetViewport(new Rect(0, 0, pass.width, pass.height));
            ctx.cmd.SetGlobalMatrix("_MobileVP", pass.vp);
            ctx.cmd.SetGlobalFloat("_MobileExposure", pass.exposure);
            ctx.cmd.SetGlobalFloat("_MobileAutomaticExposure", pass.automaticExposure ? 1 : 0);
            ctx.cmd.SetGlobalTexture("_MobileExposureTexture", pass.exposureTexture);
            ctx.cmd.SetGlobalFloat("_MobileDeExposure", pass.deExposure);
            ctx.cmd.SetGlobalFloat("_MobileReflectionCapture", pass.camera == reflectionCaptureCamera ? 1 : 0);
            ctx.cmd.SetGlobalMatrix("_MobileInvVP", pass.vp.inverse);
            ctx.cmd.SetGlobalVector("_MobileCameraPosition", pass.position);
            ctx.cmd.SetGlobalVector("_MobileCameraForward", pass.forward);
            ctx.cmd.SetGlobalVector("_MobileMainDirection", pass.mainDirection);
            ctx.cmd.SetGlobalVector("_MobileMainColor", pass.mainColor);
            ctx.cmd.SetGlobalVectorArray("_MobileLightPositions", pass.positions);
            ctx.cmd.SetGlobalVectorArray("_MobileLightColors", pass.colors);
            ctx.cmd.SetGlobalVectorArray("_MobileLightSpots", pass.spots);
            ctx.cmd.SetGlobalVectorArray("_MobileLightAttenuations", pass.attenuations);
            ctx.cmd.SetGlobalInt("_MobileLightsPerObject", pass.lightsPerObject);
            ctx.cmd.SetGlobalTexture("_MobileShadowAtlas", pass.atlas);
            ctx.cmd.SetGlobalTexture("_MobileLocalShadowAtlas", pass.localAtlas);
            ctx.cmd.SetGlobalMatrixArray("_MobileLocalShadowMatrices", pass.localMatrices);
            ctx.cmd.SetGlobalVectorArray("_MobileLocalShadowInfo", pass.localShadows);
            ctx.cmd.SetGlobalVector("_MobileLocalShadowParams", new Vector4(pass.localTexel, pass.localGrid, 0, 0));
            ctx.cmd.SetGlobalInt("_MobileCascadeCount", pass.cascadeCount);
            ctx.cmd.SetGlobalInt("_MobileShadowFilterSize", pass.shadowFilter);
            // Unity retains global array capacity across domain reloads. Use new IDs
            // for the six-cascade layout so a live two-cascade session can upgrade.
            ctx.cmd.SetGlobalMatrixArray("_MobileCSMShadowMatrices6", pass.shadowMatrices);
            ctx.cmd.SetGlobalVectorArray("_MobileCSMShadowSpheres6", pass.spheres);
            ctx.cmd.SetGlobalVector("_MobileShadowParams", pass.shadowParams);
            ctx.cmd.SetGlobalVector("_MobileFog", pass.fog);
            ctx.cmd.SetGlobalColor("_MobileFogColor", pass.fogColor);
            ctx.renderContext.ExecuteCommandBuffer(ctx.cmd); ctx.cmd.Clear();
            if (pass.deferred) { DrawDeferredScene(pass, ctx); return; }
            DrawObjects(ctx.renderContext, pass.camera, pass.culling, pass.mainLight, false);
            if (pass.drawErrors)
            {
                m_ErrorRenderer.Draw(ctx.cmd, pass.camera, m_Error, false);
                ctx.renderContext.ExecuteCommandBuffer(ctx.cmd); ctx.cmd.Clear();
            }
            if (pass.sky.mode != 0 && pass.drawSky)
            {
                MobileEnvironmentCache.BindSky(ctx.cmd, pass.sky, pass.mainDirection, pass.mainColor);
                ctx.cmd.SetGlobalFloat("_MobileSkyCameraExposure", pass.sky.cameraExposure ? 1 : 0);
                ctx.cmd.DrawProcedural(Matrix4x4.identity, m_Sky, 0, MeshTopology.Triangles, 3);
                ctx.renderContext.ExecuteCommandBuffer(ctx.cmd); ctx.cmd.Clear();
            }
            DrawObjects(ctx.renderContext, pass.camera, pass.culling, pass.mainLight, true);
            if (pass.drawErrors) m_ErrorRenderer.Draw(ctx.cmd, pass.camera, m_Error, true);
            ctx.renderContext.ExecuteCommandBuffer(ctx.cmd); ctx.cmd.Clear();
        }

#if UNITY_EDITOR
        class EditorGizmoData
        {
            internal TextureHandle depth, output;
            internal Camera camera;
            internal Material material;
        }
        void RecordEditorGizmos(Camera camera, TextureHandle depth, TextureHandle output)
        {
            using (var builder = m_Graph.AddRenderPass<EditorGizmoData>("Mobile Editor Depth and Gizmos", out var data))
            {
                data.depth = builder.ReadTexture(depth);
                data.output = builder.WriteTexture(output);
                data.camera = camera; data.material = m_Composite;
                builder.AllowPassCulling(false);
                builder.SetRenderFunc((EditorGizmoData pass, RenderGraphContext ctx) =>
                {
                    // Explicitly bind the final target's depth attachment, not the scaled HDR depth.
                    RTHandle target = pass.output;
                    ctx.cmd.SetRenderTarget(target.nameID);
                    ctx.cmd.SetViewport(pass.camera.pixelRect);
                    ctx.cmd.SetGlobalTexture("_MobileEditorDepth", pass.depth);
                    ctx.cmd.SetGlobalFloat("_MobileEditorDepthFlip", SystemInfo.graphicsUVStartsAtTop && pass.camera.targetTexture == null ? 1 : 0);
                    ctx.cmd.DrawProcedural(Matrix4x4.identity, pass.material, 6, MeshTopology.Triangles, 3);
                    ctx.renderContext.ExecuteCommandBuffer(ctx.cmd); ctx.cmd.Clear();
                    if (UnityEditor.Handles.ShouldRenderGizmos())
                    {
                        // Editor overlays must not contribute to automatic exposure or bloom.
                        var exposure = Gizmos.exposure;
                        try
                        {
                            Gizmos.exposure = Texture2D.whiteTexture;
                            ctx.renderContext.DrawGizmos(pass.camera, GizmoSubset.PreImageEffects);
                            ctx.renderContext.DrawGizmos(pass.camera, GizmoSubset.PostImageEffects);
                        }
                        finally { Gizmos.exposure = exposure; }
                    }
                });
            }
        }
#endif

        void DrawObjects(ScriptableRenderContext context, Camera camera, CullingResults culling, int mainLight, bool transparent, string passName = null)
        {
            var sorting = new SortingSettings(camera) { criteria = transparent ? SortingCriteria.CommonTransparent : SortingCriteria.CommonOpaque };
            var drawing = new DrawingSettings(passName == null ? ForwardTag : new ShaderTagId(passName), sorting)
            {
                enableInstancing = true, enableDynamicBatching = false, fallbackMaterial = passName == null ? m_Error : null, mainLightIndex = mainLight,
                perObjectData = PerObjectData.Lightmaps | PerObjectData.LightProbe | PerObjectData.ReflectionProbes
                    | PerObjectData.LightData | PerObjectData.LightIndices
            };
            var filtering = new FilteringSettings(transparent ? RenderQueueRange.transparent : RenderQueueRange.opaque, camera.cullingMask);
            context.DrawRenderers(culling, ref drawing, ref filtering);
        }

        class FilterData
        {
            internal TextureHandle source;
            internal Material material;
            internal int pass;
        }

        TextureHandle RecordFilter(TextureHandle source, int width, int height, int shaderPass, string name)
        {
            var result = m_Graph.CreateTexture(new TextureDesc(width, height)
            { name = name, colorFormat = GraphicsFormat.R16G16B16A16_SFloat, filterMode = FilterMode.Bilinear });
            using (var builder = m_Graph.AddRenderPass<FilterData>(name, out var data))
            {
                data.source = builder.ReadTexture(source); data.material = m_Composite; data.pass = shaderPass;
                builder.UseColorBuffer(result, 0);
                builder.SetRenderFunc((FilterData pass, RenderGraphContext ctx) =>
                {
                    RTHandle texture = pass.source;
                    ctx.cmd.SetGlobalTexture("_MobileSource", pass.source);
                    ctx.cmd.SetGlobalVector("_MobileSourceSize", new Vector4(1f / texture.rt.width, 1f / texture.rt.height, texture.rt.width, texture.rt.height));
                    ctx.cmd.DrawProcedural(Matrix4x4.identity, pass.material, pass.pass, MeshTopology.Triangles, 3);
                });
            }
            return result;
        }

        class CompositeData
        {
            internal TextureHandle source, bloom;
            internal Material material;
            internal Vector4 sourceSize, adjustments, options;
            internal Color filter;
            internal Rect viewport;
            internal float hue, bloomIntensity;
        }

        void RecordReflectionCapture(TextureHandle source, TextureHandle output, int width, int height)
        {
            using (var builder = m_Graph.AddRenderPass<CompositeData>("Mobile Reflection Capture", out var data))
            {
                data.source = builder.ReadTexture(source);
                builder.UseColorBuffer(output, 0);
                data.material = m_Composite;
                data.viewport = new Rect(0, 0, width, height);
                builder.SetRenderFunc((CompositeData pass, RenderGraphContext ctx) =>
                {
                    ctx.cmd.SetViewport(pass.viewport);
                    ctx.cmd.SetGlobalTexture("_MobileSource", pass.source);
                    ctx.cmd.DrawProcedural(Matrix4x4.identity, pass.material, pass.material.FindPass("MobileReflectionCapture"), MeshTopology.Triangles, 3);
                });
            }
        }

        void RecordComposite(Camera camera, TextureHandle color, TextureHandle bloom, TextureHandle output, int width, int height, float bloomIntensity)
        {
            var adjustments = m_Volumes.GetComponent<ColorAdjustments>();
            var tone = m_Volumes.GetComponent<Tonemapping>();
            var mobilePost = m_Volumes.GetComponent<MobilePostProcessing>();
            using (var builder = m_Graph.AddRenderPass<CompositeData>("Mobile Composite", out var data))
            {
                data.source = builder.ReadTexture(color); data.bloom = builder.ReadTexture(bloom);
                builder.UseColorBuffer(output, 0);
                data.material = m_Composite;
                data.viewport = camera.pixelRect;
                data.sourceSize = new Vector4(1f / width, 1f / height, width, height);
                data.adjustments = new Vector4(Mathf.Pow(2f, adjustments.active ? adjustments.postExposure.value : 0),
                    adjustments.active ? 1 + adjustments.contrast.value / 100f : 1,
                    adjustments.active ? 1 + adjustments.saturation.value / 100f : 1, 0);
                data.filter = adjustments.active ? adjustments.colorFilter.value.linear : Color.white;
                data.hue = adjustments.active ? adjustments.hueShift.value / 360f : 0;
                data.options = new Vector4(mobilePost.active && mobilePost.fxaa.value ? 1 : 0,
                    tone.active ? (int)tone.mode.value : 0,
                    SystemInfo.graphicsUVStartsAtTop && camera.targetTexture == null ? 1 : 0, 0);
                data.bloomIntensity = bloomIntensity;
                builder.SetRenderFunc((CompositeData pass, RenderGraphContext ctx) =>
                {
                    ctx.cmd.SetViewport(pass.viewport);
                    ctx.cmd.SetGlobalTexture("_MobileSource", pass.source);
                    ctx.cmd.SetGlobalTexture("_MobileBloom", pass.bloom);
                    ctx.cmd.SetGlobalVector("_MobileSourceSize", pass.sourceSize);
                    ctx.cmd.SetGlobalVector("_MobileAdjustments", pass.adjustments);
                    ctx.cmd.SetGlobalVector("_MobilePostOptions", pass.options);
                    ctx.cmd.SetGlobalColor("_MobileColorFilter", pass.filter);
                    ctx.cmd.SetGlobalFloat("_MobileHue", pass.hue);
                    ctx.cmd.SetGlobalFloat("_MobileBloomIntensity", pass.bloomIntensity);
                    ctx.cmd.DrawProcedural(Matrix4x4.identity, pass.material, 0, MeshTopology.Triangles, 3);
                });
            }
        }

        public void Dispose()
        {
            if (m_Disposed) return;
            m_Disposed = true;
            try { m_Graph?.Cleanup(); }
            finally
            {
                try
                {
                    m_ExposureHistory.Dispose();
                    m_Environment.Dispose();
                    m_ReflectionAtlas.Dispose();
                    DisposeDeferred();
                    CoreUtils.Destroy(m_Composite); CoreUtils.Destroy(m_Sky); CoreUtils.Destroy(m_Error);
                    if (m_Volumes != null) VolumeManager.instance.DestroyStack(m_Volumes);
                    if (m_DefaultVolume != null) CoreUtils.Destroy(m_DefaultVolume.gameObject);
                }
                finally
                {
                    try
                    {
                        if (s_ActiveOwner == this)
                        {
                            try
                            {
                                foreach (var entry in m_OriginalProbeTextures)
                                    if (entry.Key != null) { entry.Key.customBakedTexture = entry.Value; RestoreProbeSettings(entry.Key); }
                            }
                            finally
                            {
                                s_ActiveOwner = null;
                                GraphicsSettings.useScriptableRenderPipelineBatching = m_PreviousBatcher;
                                GraphicsSettings.lightsUseLinearIntensity = m_PreviousLinear;
                                GraphicsSettings.lightsUseColorTemperature = m_PreviousTemperature;
                                if (m_PreviousKeyword) Shader.EnableKeyword(MobileKeyword); else Shader.DisableKeyword(MobileKeyword);
                                if (m_PreviousExtendedKeyword) Shader.EnableKeyword("MOBILE_EXTENDED_LIT"); else Shader.DisableKeyword("MOBILE_EXTENDED_LIT");
                                SupportedRenderingFeatures.active = m_PreviousFeatures;
                                Shader.globalRenderPipeline = m_PreviousShaderTag;
                            }
                        }
                    }
                    finally
                    {
                        m_OriginalProbeTextures.Clear(); m_ActiveBakedProbes.Clear(); m_ExpiredProbes.Clear();
                        m_OriginalProbeSettings.Clear();
                    }
                }
            }
        }
    }
}
