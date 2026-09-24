using System;
using Unity.Collections;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Experimental.Rendering.RenderGraphModule;

namespace UnityEngine.Rendering.HighDefinition
{
    // No HDCamera, desktop resources, compute light lists or temporal allocations.
    internal sealed class MobileRenderPipelineBackend : IDisposable
    {
        const int MaxVisibleLights = 32;
        const string MobileKeyword = "HDRP_MOBILE_RENDERER";
        static readonly ShaderTagId ForwardTag = new ShaderTagId("MobileForward");
        static MobileRenderPipelineBackend s_ActiveOwner;
        readonly HDRenderPipelineAsset m_Asset;
        readonly RenderGraph m_Graph;
        readonly Material m_Composite, m_Sky, m_Error;
        readonly VolumeStack m_Volumes;
        readonly Volume m_DefaultVolume;
        readonly MobileErrorRenderer m_ErrorRenderer = new MobileErrorRenderer();
        readonly System.Collections.Generic.Dictionary<ReflectionProbe, Texture> m_OriginalProbeTextures = new System.Collections.Generic.Dictionary<ReflectionProbe, Texture>();
        readonly System.Collections.Generic.HashSet<ReflectionProbe> m_ActiveBakedProbes = new System.Collections.Generic.HashSet<ReflectionProbe>();
        readonly System.Collections.Generic.List<ReflectionProbe> m_ExpiredProbes = new System.Collections.Generic.List<ReflectionProbe>();
        readonly Vector4[] m_Positions = new Vector4[MaxVisibleLights];
        readonly Vector4[] m_Colors = new Vector4[MaxVisibleLights];
        readonly Vector4[] m_Spots = new Vector4[MaxVisibleLights];
        readonly Vector4[] m_SpotAttenuations = new Vector4[MaxVisibleLights];
        readonly Matrix4x4[] m_ShadowMatrices = new Matrix4x4[2];
        readonly Vector4[] m_ShadowSpheres = new Vector4[2];
        readonly Matrix4x4[] m_ShadowViews = new Matrix4x4[2];
        readonly Matrix4x4[] m_ShadowProjections = new Matrix4x4[2];
        readonly ShadowSplitData[] m_ShadowSplits = new ShadowSplitData[2];
        readonly bool m_PreviousBatcher, m_PreviousLinear, m_PreviousTemperature, m_PreviousKeyword;
        readonly SupportedRenderingFeatures m_PreviousFeatures;
        readonly string m_PreviousShaderTag;
        int m_MainLight = -1;
        Vector4 m_MainDirection, m_MainColor;
        bool m_Disposed;

        internal MobileRenderPipelineBackend(HDRenderPipelineAsset asset, Action<string> initializationCheckpoint = null)
        {
            m_Asset = asset;
            if (s_ActiveOwner != null)
                foreach (var entry in s_ActiveOwner.m_OriginalProbeTextures) m_OriginalProbeTextures.Add(entry.Key, entry.Value);
            m_PreviousBatcher = s_ActiveOwner != null ? s_ActiveOwner.m_PreviousBatcher : GraphicsSettings.useScriptableRenderPipelineBatching;
            m_PreviousLinear = s_ActiveOwner != null ? s_ActiveOwner.m_PreviousLinear : GraphicsSettings.lightsUseLinearIntensity;
            m_PreviousTemperature = s_ActiveOwner != null ? s_ActiveOwner.m_PreviousTemperature : GraphicsSettings.lightsUseColorTemperature;
            m_PreviousKeyword = s_ActiveOwner != null ? s_ActiveOwner.m_PreviousKeyword : Shader.IsKeywordEnabled(MobileKeyword);
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
                    rendersUIOverlay = false
                };
            }
            catch { Dispose(); throw; }
        }

        internal void EndFrame() { if (!m_Disposed) m_Graph.EndFrame(); }

        internal void Render(ScriptableRenderContext context, Camera camera)
        {
            if (m_Disposed || camera == null || camera.stereoEnabled || camera.cameraType == CameraType.Reflection)
                return;
            if (!camera.TryGetCullingParameters(out var parameters)) return;
            SynchronizeBakedReflectionProbes();
            var settings = m_Asset.mobileSettings;
            if (settings.drawUnsupportedMaterials) m_ErrorRenderer.Prepare(camera);
            parameters.shadowDistance = Mathf.Min(Mathf.Max(0.1f, settings.shadowDistance), camera.farClipPlane);
            parameters.cullingOptions &= ~CullingOptions.Stereo;
            parameters.cullingOptions |= CullingOptions.NeedsReflectionProbes;
#if UNITY_EDITOR
            if (camera.cameraType == CameraType.SceneView)
                ScriptableRenderContext.EmitWorldGeometryForSceneView(camera);
#endif
            var culling = context.Cull(ref parameters);
            PrepareLights(ref culling);
            bool shadows = PrepareShadows(ref culling, settings);
            camera.TryGetComponent<HDAdditionalCameraData>(out var additional);
            // Match HDCamera's Scene View Volume selection without allocating an HDCamera.
            var volumeCamera = additional;
            if (volumeCamera == null && camera.cameraType == CameraType.SceneView && Camera.main != null)
                Camera.main.TryGetComponent(out volumeCamera);
            var anchor = volumeCamera != null && volumeCamera.volumeAnchorOverride != null ? volumeCamera.volumeAnchorOverride : camera.transform;
            int mask = volumeCamera != null ? (int)volumeCamera.volumeLayerMask : -1;
            if (volumeCamera == null && camera.cameraType == CameraType.SceneView)
            {
                int skyMask = m_Asset.currentPlatformRenderPipelineSettings.lightLoopSettings.skyLightingOverrideLayerMask;
                mask = skyMask == -1 ? -1 : ~(skyMask | (1 << 31));
            }
            if (m_DefaultVolume != null) m_DefaultVolume.sharedProfile = HDRenderPipelineGlobalSettings.instance?.volumeProfile;
            VolumeManager.instance.Update(m_Volumes, anchor, mask);
            FrameSettings frameSettings = new FrameSettings();
            FrameSettings.AggregateFrameSettings(ref frameSettings, camera, additional, m_Asset);
            float exposure = MobileExposure.Evaluate(m_Volumes.GetComponent<Exposure>(), camera, frameSettings.IsEnabled(FrameSettingsField.ExposureControl));
            Vector2Int renderSize = settings.GetRenderSize(camera);
            int width = renderSize.x, height = renderSize.y;
            var cmd = CommandBufferPool.Get("HDRP Mobile");
            try
            {
                context.SetupCameraProperties(camera);
                using (m_Graph.RecordAndExecute(new RenderGraphParameters
                {
                    executionName = camera.name, currentFrameIndex = Time.frameCount,
                    scriptableRenderContext = context, commandBuffer = cmd
                }))
                {
                    var color = m_Graph.CreateTexture(new TextureDesc(width, height)
                    {
                        name = "Mobile HDR Color", colorFormat = GraphicsFormat.R16G16B16A16_SFloat,
                        clearBuffer = true, clearColor = additional != null ? additional.backgroundColorHDR : camera.backgroundColor.linear, filterMode = FilterMode.Bilinear
                    });
                    var depth = m_Graph.CreateTexture(new TextureDesc(width, height)
                    {
                        name = "Mobile Depth", depthBufferBits = DepthBits.Depth24, clearBuffer = true
                    });
                    int atlasSize = shadows ? Mathf.Clamp(Mathf.NextPowerOfTwo(settings.shadowAtlasSize), 256, 2048) : 1;
                    var atlas = m_Graph.CreateTexture(new TextureDesc(atlasSize, atlasSize)
                    {
                        name = "Mobile Main Shadow", depthBufferBits = DepthBits.Depth16,
                        isShadowMap = true, clearBuffer = true, filterMode = FilterMode.Bilinear
                    });
                    RecordShadows(camera, culling, atlas, atlasSize, shadows);
                    RecordScene(camera, culling, color, depth, atlas, atlasSize, shadows, width, height, exposure);
                    TextureHandle bloom = m_Graph.defaultResources.blackTexture;
                    var bloomVolume = m_Volumes.GetComponent<Bloom>();
                    float bloomIntensity = settings.bloom && bloomVolume.active ? bloomVolume.intensity.value : 0f;
                    if (bloomIntensity > 0f)
                    {
                        var down0 = RecordFilter(color, Mathf.Max(1, width / 2), Mathf.Max(1, height / 2), 1, "Mobile Bloom Half");
                        var down1 = RecordFilter(down0, Mathf.Max(1, width / 4), Mathf.Max(1, height / 4), 2, "Mobile Bloom Quarter");
                        bloom = RecordFilter(down1, Mathf.Max(1, width / 8), Mathf.Max(1, height / 8), 2, "Mobile Bloom Eighth");
                    }
                    var output = m_Graph.ImportBackbuffer(camera.targetTexture != null
                        ? new RenderTargetIdentifier(camera.targetTexture) : new RenderTargetIdentifier(BuiltinRenderTextureType.CameraTarget));
                    RecordComposite(camera, color, bloom, output, width, height, bloomIntensity);
                }
                context.ExecuteCommandBuffer(cmd);
                cmd.Clear();
                context.Submit();
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
                if (legacy.customBakedTexture != probe.bakedTexture) legacy.customBakedTexture = probe.bakedTexture;
            }
            m_ExpiredProbes.Clear();
            foreach (var entry in m_OriginalProbeTextures)
            {
                if (m_ActiveBakedProbes.Contains(entry.Key)) continue;
                if (entry.Key != null) entry.Key.customBakedTexture = entry.Value;
                m_ExpiredProbes.Add(entry.Key);
            }
            foreach (var probe in m_ExpiredProbes) m_OriginalProbeTextures.Remove(probe);
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
            culling.SetLightIndexMap(indexMap);
            indexMap.Dispose();
        }

        bool PrepareShadows(ref CullingResults culling, MobileRenderSettings settings)
        {
            if (!settings.mainLightShadows || m_MainLight < 0) return false;
            var light = culling.visibleLights[m_MainLight].light;
            if (light == null || light.shadows == LightShadows.None || light.shadowStrength <= 0f
                || !culling.GetShadowCasterBounds(m_MainLight, out _)) return false;
            int tileSize = Mathf.Clamp(Mathf.NextPowerOfTwo(settings.shadowAtlasSize), 256, 2048) / 2;
            for (int cascade = 0; cascade < 2; cascade++)
            {
                if (!culling.ComputeDirectionalShadowMatricesAndCullingPrimitives(m_MainLight, cascade, 2,
                    new Vector3(Mathf.Clamp(settings.cascadeSplit, 0.05f, 0.95f), 0, 0), tileSize, light.shadowNearPlane,
                    out m_ShadowViews[cascade], out m_ShadowProjections[cascade], out m_ShadowSplits[cascade])) return false;
                // Shrink the exclusion sphere of preceding splits so their outer
                // blend band is also rendered into the following cascade.
                m_ShadowSplits[cascade].shadowCascadeBlendCullingFactor = 1f - Mathf.Clamp(settings.cascadeBlend, 0f, 0.3f);
                var sphere = m_ShadowSplits[cascade].cullingSphere;
                sphere.w *= sphere.w;
                m_ShadowSpheres[cascade] = sphere;
                var matrix = m_ShadowProjections[cascade] * m_ShadowViews[cascade];
                if (SystemInfo.usesReversedZBuffer)
                    for (int column = 0; column < 4; column++) matrix[2, column] = -matrix[2, column];
                var bias = Matrix4x4.identity;
                bias.m00 = bias.m11 = bias.m22 = 0.5f;
                bias.m03 = bias.m13 = bias.m23 = 0.5f;
                var tile = Matrix4x4.identity;
                tile.m00 = tile.m11 = 0.5f;
                tile.m03 = cascade * 0.5f;
                m_ShadowMatrices[cascade] = tile * bias * matrix;
            }
            return true;
        }

        class ShadowData
        {
            internal Camera camera;
            internal CullingResults culling;
            internal int size, mainLight;
            internal float normalBias;
            internal Vector4 mainDirection;
            internal readonly Matrix4x4[] views = new Matrix4x4[2], projections = new Matrix4x4[2];
            internal readonly ShadowSplitData[] splits = new ShadowSplitData[2];
            internal bool enabled;
        }

        void RecordShadows(Camera camera, CullingResults culling, TextureHandle atlas, int size, bool enabled)
        {
            using (var builder = m_Graph.AddRenderPass<ShadowData>("Mobile Main Shadow", out var data))
            {
                data.camera = camera; data.culling = culling; data.size = size; data.enabled = enabled;
                data.mainLight = m_MainLight; data.mainDirection = m_MainDirection;
                data.normalBias = Mathf.Max(0f, m_Asset.mobileSettings.shadowNormalBias);
                Array.Copy(m_ShadowViews, data.views, 2); Array.Copy(m_ShadowProjections, data.projections, 2);
                Array.Copy(m_ShadowSplits, data.splits, 2);
                builder.UseDepthBuffer(atlas, DepthAccess.Write);
                builder.SetRenderFunc((ShadowData pass, RenderGraphContext ctx) =>
                {
                    if (!pass.enabled) return;
                    int tile = pass.size / 2;
                    ctx.cmd.EnableShaderKeyword(MobileKeyword);
                    ctx.cmd.SetGlobalFloat("_MobileShadowNormalBias", pass.normalBias);
                    ctx.cmd.SetGlobalVector("_MobileMainDirection", pass.mainDirection);
                    ctx.cmd.SetGlobalDepthBias(1f, 2.5f);
                    try
                    {
                        for (int i = 0; i < 2; i++)
                        {
                            ctx.cmd.SetViewport(new Rect(i * tile, 0, tile, tile));
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
            internal MobileRenderPipelineBackend owner;
            internal Camera camera;
            internal CullingResults culling;
            internal TextureHandle atlas;
            internal int width, height, atlasSize;
            internal bool shadows;
            internal Matrix4x4 vp;
            internal Vector4 position, forward, mainDirection, mainColor, shadowParams, fog;
            internal Color fogColor;
            internal int mainLight, lightsPerObject;
            internal bool drawErrors, drawSky;
            internal MobileSky sky;
            internal float exposure, deExposure;
            internal readonly Vector4[] positions = new Vector4[MaxVisibleLights], colors = new Vector4[MaxVisibleLights],
                spots = new Vector4[MaxVisibleLights], attenuations = new Vector4[MaxVisibleLights], spheres = new Vector4[2];
            internal readonly Matrix4x4[] shadowMatrices = new Matrix4x4[2];
        }

        void RecordScene(Camera camera, CullingResults culling, TextureHandle color, TextureHandle depth,
            TextureHandle atlas, int atlasSize, bool shadows, int width, int height, float exposure)
        {
            using (var builder = m_Graph.AddRenderPass<SceneData>("Mobile Forward", out var data))
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
                Array.Copy(m_ShadowMatrices, data.shadowMatrices, 2); Array.Copy(m_ShadowSpheres, data.spheres, 2);
                data.lightsPerObject = Mathf.Clamp(settings.additionalLightsPerObject, 0, 4);
                data.shadowParams = new Vector4(shadows ? culling.visibleLights[m_MainLight].light.shadowStrength : 0,
                    1f / atlasSize, Mathf.Min(settings.shadowDistance, camera.farClipPlane), Mathf.Clamp(settings.cascadeBlend, 0, 0.3f));
                data.fog = new Vector4(settings.fog ? 1 : 0, settings.fogStart, 1f / Mathf.Max(0.01f, settings.fogEnd - settings.fogStart), 0);
                data.fogColor = settings.fogColor.linear;
                data.drawErrors = settings.drawUnsupportedMaterials;
                camera.TryGetComponent<HDAdditionalCameraData>(out var additional);
                data.exposure = exposure;
                data.deExposure = additional != null ? additional.deExposureMultiplier : 1f;
                data.drawSky = additional != null ? additional.clearColorMode == HDAdditionalCameraData.ClearColorMode.Sky : camera.clearFlags == CameraClearFlags.Skybox;
                data.sky = MobileSky.Resolve(settings, m_Volumes, RenderSettings.skybox, Mathf.Max(m_MainColor.x, Mathf.Max(m_MainColor.y, m_MainColor.z)));
                data.atlas = builder.ReadTexture(atlas);
                builder.UseColorBuffer(color, 0); builder.UseDepthBuffer(depth, DepthAccess.ReadWrite);
                builder.SetRenderFunc((SceneData pass, RenderGraphContext ctx) => pass.owner.DrawScene(pass, ctx));
            }
        }

        void DrawScene(SceneData pass, RenderGraphContext ctx)
        {
            ctx.cmd.SetViewport(new Rect(0, 0, pass.width, pass.height));
            ctx.cmd.SetGlobalMatrix("_MobileVP", pass.vp);
            ctx.cmd.SetGlobalFloat("_MobileExposure", pass.exposure);
            ctx.cmd.SetGlobalFloat("_MobileDeExposure", pass.deExposure);
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
            ctx.cmd.SetGlobalMatrixArray("_MobileShadowMatrices", pass.shadowMatrices);
            ctx.cmd.SetGlobalVectorArray("_MobileShadowSpheres", pass.spheres);
            ctx.cmd.SetGlobalVector("_MobileShadowParams", pass.shadowParams);
            ctx.cmd.SetGlobalVector("_MobileFog", pass.fog);
            ctx.cmd.SetGlobalColor("_MobileFogColor", pass.fogColor);
            ctx.renderContext.ExecuteCommandBuffer(ctx.cmd); ctx.cmd.Clear();
            DrawObjects(ctx.renderContext, pass.camera, pass.culling, pass.mainLight, false);
            if (pass.drawErrors)
            {
                m_ErrorRenderer.Draw(ctx.cmd, pass.camera, m_Error, false);
                ctx.renderContext.ExecuteCommandBuffer(ctx.cmd); ctx.cmd.Clear();
            }
            if (pass.sky.mode != 0 && pass.drawSky)
            {
                if (pass.sky.cube != null) ctx.cmd.SetGlobalTexture("_MobileSky", pass.sky.cube);
                ctx.cmd.SetGlobalFloat("_MobileSkyIntensity", pass.sky.intensity * (pass.sky.cameraExposure ? pass.exposure : 1));
                ctx.cmd.SetGlobalVector("_MobileSkyOptions", new Vector4(pass.sky.mode, Mathf.Cos(pass.sky.rotation), Mathf.Sin(pass.sky.rotation), pass.sky.diffusion));
                ctx.cmd.SetGlobalFloat("_MobileSkySunSize", pass.sky.sunSize);
                ctx.cmd.SetGlobalColor("_MobileSkyTint", pass.sky.tint);
                ctx.cmd.SetGlobalColor("_MobileSkyTop", pass.sky.top);
                ctx.cmd.SetGlobalColor("_MobileSkyMiddle", pass.sky.middle);
                ctx.cmd.SetGlobalColor("_MobileSkyBottom", pass.sky.bottom);
                ctx.cmd.DrawProcedural(Matrix4x4.identity, m_Sky, 0, MeshTopology.Triangles, 3);
                ctx.renderContext.ExecuteCommandBuffer(ctx.cmd); ctx.cmd.Clear();
            }
            DrawObjects(ctx.renderContext, pass.camera, pass.culling, pass.mainLight, true);
            if (pass.drawErrors) m_ErrorRenderer.Draw(ctx.cmd, pass.camera, m_Error, true);
            ctx.renderContext.ExecuteCommandBuffer(ctx.cmd); ctx.cmd.Clear();
#if UNITY_EDITOR
            if (UnityEditor.Handles.ShouldRenderGizmos())
                ctx.renderContext.DrawGizmos(pass.camera, GizmoSubset.PreImageEffects);
#endif
        }

        void DrawObjects(ScriptableRenderContext context, Camera camera, CullingResults culling, int mainLight, bool transparent)
        {
            var sorting = new SortingSettings(camera) { criteria = transparent ? SortingCriteria.CommonTransparent : SortingCriteria.CommonOpaque };
            var drawing = new DrawingSettings(ForwardTag, sorting)
            {
                enableInstancing = true, enableDynamicBatching = false, fallbackMaterial = m_Error, mainLightIndex = mainLight,
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
                data.options = new Vector4(m_Asset.mobileSettings.fxaa && (!mobilePost.active || mobilePost.fxaa.value) ? 1 : 0,
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
                                    if (entry.Key != null) entry.Key.customBakedTexture = entry.Value;
                            }
                            finally
                            {
                                s_ActiveOwner = null;
                                GraphicsSettings.useScriptableRenderPipelineBatching = m_PreviousBatcher;
                                GraphicsSettings.lightsUseLinearIntensity = m_PreviousLinear;
                                GraphicsSettings.lightsUseColorTemperature = m_PreviousTemperature;
                                if (m_PreviousKeyword) Shader.EnableKeyword(MobileKeyword); else Shader.DisableKeyword(MobileKeyword);
                                SupportedRenderingFeatures.active = m_PreviousFeatures;
                                Shader.globalRenderPipeline = m_PreviousShaderTag;
                            }
                        }
                    }
                    finally
                    {
                        m_OriginalProbeTextures.Clear(); m_ActiveBakedProbes.Clear(); m_ExpiredProbes.Clear();
                    }
                }
            }
        }
    }
}
