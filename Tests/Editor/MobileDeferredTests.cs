using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace UnityEditor.Rendering.HighDefinition.Tests
{
    public class MobileDeferredContractTests
    {
        [Test]
        public void MobileShadersComeFromBuiltInRuntimeResourcesWithoutAssetConfiguration()
        {
            var asset = ScriptableObject.CreateInstance<HDRenderPipelineAsset>();
            try
            {
                EditorJsonUtility.FromJsonOverwrite("{\"m_MobileResources\":{\"compositeShader\":null,\"deferredShader\":null}}", asset);
                var builtIn = HDRenderPipelineGlobalSettings.instance.renderPipelineResources.mobile;
                Assert.AreSame(builtIn, asset.mobileResources);
                Assert.IsTrue(builtIn.isValid);
                Assert.IsNotNull(builtIn.deferredShader);
                Assert.IsNull(new SerializedObject(asset).FindProperty("m_MobileResources"));
                var reloaded = new MobileRenderResources();
                ResourceReloader.ReloadAllNullIn(reloaded, HDUtils.GetHDRenderPipelinePath());
                Assert.AreEqual(builtIn.compositeShader, reloaded.compositeShader);
                Assert.AreEqual(builtIn.skyShader, reloaded.skyShader);
                Assert.AreEqual(builtIn.errorShader, reloaded.errorShader);
                Assert.AreEqual(builtIn.deferredShader, reloaded.deferredShader);
            }
            finally { Object.DestroyImmediate(asset); }
        }

        [TestCase(GraphicsDeviceType.Direct3D11, RuntimePlatform.WindowsPlayer, false)]
        [TestCase(GraphicsDeviceType.Direct3D12, RuntimePlatform.WindowsPlayer, false)]
        [TestCase(GraphicsDeviceType.Vulkan, RuntimePlatform.WindowsEditor, false)]
        [TestCase(GraphicsDeviceType.Vulkan, RuntimePlatform.Android, true)]
        public void AutoUsesNativeAttachmentsOnlyOnAndroid(GraphicsDeviceType api, RuntimePlatform platform, bool native)
            => Assert.AreEqual(native, MobileRenderCapabilities.PreferNativeDeferred(api, platform));
        [Test]
        public void CompactCodecRoundTripsThroughActualGpuFormats()
        {
            var material = new Material(Shader.Find("Hidden/HDRP/MobileGBufferCodecTest"));
            var baseMetal = new RenderTexture(512, 1, 0) { graphicsFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_SRGB };
            var normal = new RenderTexture(512, 1, 0) { graphicsFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_UNorm };
            var error = new RenderTexture(512, 1, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            var pixels = new Texture2D(512, 1, TextureFormat.RGBAFloat, false, true);
            var cmd = new CommandBuffer(); var previous = RenderTexture.active;
            try
            {
                baseMetal.Create(); normal.Create(); error.Create();
                cmd.SetRenderTarget(new RenderTargetIdentifier[] { baseMetal, normal }, BuiltinRenderTextureType.None);
                cmd.DrawProcedural(Matrix4x4.identity, material, 0, MeshTopology.Triangles, 3);
                cmd.SetGlobalTexture("_Base", baseMetal); cmd.SetGlobalTexture("_Normal", normal);
                cmd.SetRenderTarget(error); cmd.DrawProcedural(Matrix4x4.identity, material, 1, MeshTopology.Triangles, 3);
                Graphics.ExecuteCommandBuffer(cmd); RenderTexture.active = error;
                pixels.ReadPixels(new Rect(0, 0, 512, 1), 0, 0); pixels.Apply();
                foreach (var value in pixels.GetPixels())
                {
                    Assert.Less(value.r, 1.0f, "Oct 8+8 angular error, including axes and folded hemisphere");
                    // Allow one sRGB code step across hardware encode/decode (the
                    // largest linear step is near white), rather than two ideal CPU conversions.
                    Assert.Less(value.g, 0.009f, "sRGB base-color round trip");
                    Assert.Less(value.b, 1f / 255 + 0.00001f, "Metallic alpha must remain linear within one code step");
                    Assert.Less(value.a, 1f / 255 + 0.00001f, "Smoothness/AO UNorm quantization within one code step");
                }
            }
            finally
            {
                RenderTexture.active = previous; cmd.Dispose();
                Object.DestroyImmediate(material); Object.DestroyImmediate(baseMetal); Object.DestroyImmediate(normal);
                Object.DestroyImmediate(error); Object.DestroyImmediate(pixels);
            }
        }

        [Test]
        public void StencilContractPreservesAllMaterialIdsAndUserBits()
        {
            for (int type = 0; type < 8; type++) for (int flags = 0; flags < 4; flags++) for (int user = 0; user < 2; user++)
            {
                int value = MobileDeferredMaterial.Encode(type, (flags & 1) == 0, (flags & 2) == 0, user == 1);
                Assert.AreEqual(type, (value & 0x70) >> 4);
                Assert.AreEqual(flags, value & 0xf);
                Assert.AreEqual(user, value >> 7);
            }
        }

        [Test]
        public void ExistingAssetsRemainForwardAndAuto()
        {
            var settings = new MobileRenderSettings();
            Assert.AreEqual(MobileRenderingPath.Forward, settings.renderingPath);
            Assert.AreEqual(MobileDeferredExecution.Auto, settings.deferredExecution);
        }

        [Test]
        public void UnsupportedMaterialIsNotEnabledForGBuffer()
        {
            string previousShaderTag = Shader.globalRenderPipeline;
            Shader.globalRenderPipeline = HDRenderPipeline.k_ShaderTagName;
            var material = new Material(Shader.Find("HDRP/Lit"));
            try
            {
                int forwardOnlyPasses = 0;
                for (int pass = 0; pass < material.passCount; pass++)
                    if (string.Equals(material.GetPassName(pass), "MobileForwardOnly", System.StringComparison.OrdinalIgnoreCase)) forwardOnlyPasses++;
                Assert.AreEqual(1, forwardOnlyPasses, "Fallback pass must be unique.");
                for (int id = 0; id < 6; id++)
                {
                    material.SetFloat("_MaterialID", id); material.SetFloat("_MobileUserStencilBit", 1);
                    HDShaderUtils.ResetMaterialKeywords(material);
                    Assert.AreEqual(id == 1, material.GetShaderPassEnabled("MobileGBuffer"));
                    Assert.AreEqual((id << 4) | 128 | (id == 4 ? 8 : 0), material.GetInt("_MobileStencilRef"));
                    Assert.AreEqual(id == 1 || id == 4, material.GetShaderPassEnabled("MobileGBufferExtended"));
                    Assert.AreEqual(id != 1, material.GetShaderPassEnabled("MobileForwardOnly"));
                    Assert.AreEqual(id != 1 && id != 4, material.GetShaderPassEnabled("MobileForwardOnlyExtended"));
                    Assert.AreEqual(0xe0 | (id == 4 ? 8 : 0), material.GetInt("_MobileForwardStencilRef"));
                }
            }
            finally { Object.DestroyImmediate(material); Shader.globalRenderPipeline = previousShaderTag; }
        }

        [TestCase("HDRP/Lit")] [TestCase("HDRP/Unlit")]
        public void DeferredRoutingRemainsExclusiveAcrossMaterialChanges(string shaderName)
        {
            string previousShaderTag = Shader.globalRenderPipeline;
            Shader.globalRenderPipeline = HDRenderPipeline.k_ShaderTagName;
            var material = new Material(Shader.Find(shaderName));
            try
            {
                Assert.GreaterOrEqual(material.FindPass("MobileForwardOnlyExtended"), 0);
                bool lit = shaderName == "HDRP/Lit";
                for (int surface = 0; surface < 2; surface++)
                for (int coat = 0; coat < 2; coat++)
                for (int id = 0; id < 6; id++)
                {
                    material.SetFloat("_SurfaceType", surface);
                    if (lit)
                    {
                        material.SetFloat("_MaterialID", id);
                        material.SetFloat("_CoatMask", coat);
                    }
                    HDShaderUtils.ResetMaterialKeywords(material);
                    bool compact = lit && id == 1 && coat == 0;
                    bool extended = lit && (id == 1 || id == 4);
                    Assert.AreEqual(!lit || (surface == 0 && !compact), material.GetShaderPassEnabled("MobileForwardOnly"));
                    Assert.AreEqual(!lit || (surface == 0 && !extended), material.GetShaderPassEnabled("MobileForwardOnlyExtended"));
                    if (lit)
                    {
                        Assert.AreEqual(surface == 0 && compact, material.GetShaderPassEnabled("MobileGBuffer"));
                        Assert.AreEqual(surface == 0 && extended, material.GetShaderPassEnabled("MobileGBufferExtended"));
                    }
                }
            }
            finally { Object.DestroyImmediate(material); Shader.globalRenderPipeline = previousShaderTag; }
        }

        [Test]
        public void DeferredLightContributionUsesRgbWithoutAnIntensityThreshold()
        {
            Assert.IsFalse(MobileRenderPipelineBackend.HasDeferredLightContribution(new Vector4(0, 0, 0, 1)));
            Assert.IsTrue(MobileRenderPipelineBackend.HasDeferredLightContribution(new Vector4(1e-20f, 0, 0, 0)));
            Assert.IsTrue(MobileRenderPipelineBackend.HasDeferredLightContribution(new Vector4(0, 1e-20f, 0, 0)));
            Assert.IsTrue(MobileRenderPipelineBackend.HasDeferredLightContribution(new Vector4(0, 0, 1e-20f, 0)));
        }
    }

    public partial class MobileImageTests
    {
        MobileRenderPipelineBackend DeferredBackend => (MobileRenderPipelineBackend)typeof(HDRenderPipeline)
            .GetField("m_MobileBackend", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            .GetValue(RenderPipelineManager.currentPipeline);

        [TestCase(false)] [TestCase(true)]
        public void DeferredSkipsZeroLightsWithoutLosingEmissionOrFog(bool native)
        {
            if (native && SystemInfo.graphicsDeviceType != GraphicsDeviceType.Vulkan) Assert.Ignore("Native requires Vulkan");
            var material = Material(true, Color.gray);
            material.SetColor("_EmissiveColor", new Color(0.3f, 0.2f, 0.1f));
            HDShaderUtils.ResetMaterialKeywords(material); Quad(material);
            m_Asset.mobileSettings.fog = true; m_Asset.mobileSettings.fogStart = 0; m_Asset.mobileSettings.fogEnd = 10;
            m_Sun.color = Color.black;
            UseDeferred();
            m_Asset.mobileSettings.deferredExecution = native ? MobileDeferredExecution.NativeRenderPass : MobileDeferredExecution.Sampled;
            var blackSun = Capture();
            Assert.AreEqual(0, DeferredBackend.lastCameraDeferredLightingDraws);
            m_Sun.enabled = false;
            Same(blackSun, Capture());
            Assert.AreEqual(0, DeferredBackend.lastCameraDeferredLightingDraws);
            Assert.Greater(Center(blackSun).r, 0.01f);
        }

        [TestCase(false)] [TestCase(true)]
        public void DeferredSkippedLocalLightsPreservePackedIndices(bool native)
        {
            if (native && SystemInfo.graphicsDeviceType != GraphicsDeviceType.Vulkan) Assert.Ignore("Native requires Vulkan");
            m_Sun.intensity = 0; Quad(Material(true, Color.gray));
            var lights = new Light[3];
            for (int i = 0; i < lights.Length; i++)
            {
                var light = MakeObject("Packed local " + i).AddComponent<Light>(); lights[i] = light;
                light.type = i == 1 ? LightType.Spot : LightType.Point;
                light.cullingMask = m_Camera.cullingMask; light.range = 5; light.intensity = 2;
                light.spotAngle = 90; light.innerSpotAngle = 60;
                light.transform.position = new Vector3(0, 0, -1);
            }
            for (int active = 0; active < lights.Length; active++)
            {
                for (int i = 0; i < lights.Length; i++) lights[i].color = i == active ? Color.white : Color.black;
                m_Asset.mobileSettings.renderingPath = MobileRenderingPath.Forward;
                var forward = Center(Capture()); Assert.Greater(forward.r, 0.01f);
                UseDeferred();
                m_Asset.mobileSettings.deferredExecution = native ? MobileDeferredExecution.NativeRenderPass : MobileDeferredExecution.Sampled;
                CompactNear(forward, Center(Capture()), "Skipped lights must not renumber the packed buffers");
                Assert.AreEqual(1, DeferredBackend.lastCameraDeferredLightingDraws);
            }
            foreach (var light in lights) light.color = Color.black;
            Capture(); Assert.AreEqual(0, DeferredBackend.lastCameraDeferredLightingDraws);
        }

        [TestCase(false, false)] [TestCase(true, false)]
        [TestCase(false, true)] [TestCase(true, true)]
        public void DeferredUsesOnlyCameraMaterialCombinations(bool extended, bool instanced)
        {
            m_Asset.mobileSettings.extendedLitMaterials = extended;
            var first = Material(true, Color.gray);
            var renderer = Quad(first);
            var unused = Material(true, Color.gray);
            unused.SetFloat("_MobileReceiveShadows", 0); unused.SetFloat("_MobileSpecularHighlights", 0);
            HDShaderUtils.ResetMaterialKeywords(unused);
            var excluded = Quad(unused); excluded.gameObject.layer = 29;
            if (instanced)
            {
                InstanceGroup(renderer); m_Asset.mobileSettings.instanceMode = MobileInstanceMode.CpuInstanced;
            }
            UseDeferred(); Capture();
            Assert.AreEqual(1, DeferredBackend.lastCameraDeferredVariantMask);
            Assert.AreEqual(1, DeferredBackend.lastCameraDeferredLightingDraws);
            for (int flags = 0; flags < 4; flags++)
            {
                first.SetFloat("_MaterialID", extended ? 4 : 1);
                first.SetFloat("_MobileReceiveShadows", (flags & 1) == 0 ? 1 : 0);
                first.SetFloat("_MobileSpecularHighlights", (flags & 2) == 0 ? 1 : 0);
                first.SetFloat("_MobileUserStencilBit", 1);
                HDShaderUtils.ResetMaterialKeywords(first);
                Capture();
                Assert.AreEqual(1 << (flags + (extended ? 4 : 0)), DeferredBackend.lastCameraDeferredVariantMask);
                Assert.AreEqual(1, DeferredBackend.lastCameraDeferredLightingDraws);
            }
            renderer.sharedMaterial = unused;
            if (instanced) m_Asset.mobileSettings.instanceMode = MobileInstanceMode.Disabled;
            Capture();
            Assert.AreEqual(8, DeferredBackend.lastCameraDeferredVariantMask, "Same-frame material reassignment");
            excluded.sharedMaterial = first; excluded.gameObject.layer = 30;
            Capture();
            Assert.AreEqual(8 | (1 << (extended ? 7 : 3)), DeferredBackend.lastCameraDeferredVariantMask);
            Assert.AreEqual(extended ? 2 : 1, DeferredBackend.lastCameraDeferredLightingDraws);
            excluded.gameObject.layer = 30; excluded.transform.position = new Vector3(1000, 0, 0);
            renderer.enabled = false; Capture();
            Assert.AreEqual(0, DeferredBackend.lastCameraDeferredLightingDraws, "Disabled/out-of-frustum geometry has no lighting draws");
        }

        [Test]
        public void DeferredMaterialCensusMatchesNormalSceneCandidates()
        {
            // Use the runner's ordinary scene; an additive scene cannot be opened
            // while its untitled test scene is unsaved.
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            var previousScene = m_Camera.scene;
            Renderer renderer = null, added = null;
            try
            {
                var first = Material(true, Color.gray); renderer = Quad(first);
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(renderer.gameObject, scene);
                renderer.gameObject.layer = 29; m_Camera.cullingMask = 1 << 29;
                m_Camera.scene = default;
                var usage = new MobileDeferredMaterialUsage();
                Assert.AreEqual(1, usage.Gather(m_Camera, false));
                first.SetFloat("_MobileSpecularHighlights", 0); HDShaderUtils.ResetMaterialKeywords(first);
                Assert.AreEqual(4, usage.Gather(m_Camera, false));
                var second = Material(true, Color.gray); added = Quad(second);
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(added.gameObject, scene);
                added.gameObject.layer = 29;
                Assert.AreEqual(5, usage.Gather(m_Camera, false), "Same-frame spawned renderer");
                renderer.enabled = false; added.enabled = false;
                Assert.AreEqual(0, usage.Gather(m_Camera, false), "Loaded but unused materials must not submit lighting");
            }
            finally
            {
                m_Camera.scene = previousScene;
                if (renderer != null) UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(renderer.gameObject, m_Scene);
                if (added != null) UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(added.gameObject, m_Scene);
            }
        }

        [TestCase(false)] [TestCase(true)]
        public void DeferredFallbackOccludesGBufferAndClipsBeforeFog(bool extraRT)
        {
            m_Asset.mobileSettings.extendedLitMaterials = extraRT;
            m_Asset.mobileSettings.fog = true; m_Asset.mobileSettings.fogStart = 0; m_Asset.mobileSettings.fogEnd = 10;
            var deferredMaterial = Material(true, new Color(0.15f, 0.3f, 0.6f));
            deferredMaterial.SetFloat("_MaterialID", extraRT ? 4 : 1);
            HDShaderUtils.ResetMaterialKeywords(deferredMaterial); Quad(deferredMaterial);
            var fallback = Material(true, new Color(0.7f, 0.4f, 0.1f));
            fallback.SetFloat("_MaterialID", 2); fallback.SetFloat("_Anisotropy", 0.7f);
            fallback.SetFloat("_AlphaCutoffEnable", 1); fallback.SetFloat("_AlphaCutoff", 0.5f);
            var texture = Own(new Texture2D(2, 2, TextureFormat.RGBA32, false, true));
            texture.SetPixels(new[] { Color.white, Color.clear, Color.white, Color.clear }); texture.Apply(); texture.filterMode = FilterMode.Point;
            fallback.SetTexture("_BaseColorMap", texture); HDShaderUtils.ResetMaterialKeywords(fallback);
            var front = Quad(fallback, -0.2f); front.transform.localScale = Vector3.one * 0.75f;
            // Two newly prepared variants of one shader can transiently share
            // stale SRP material data on their first Editor draw. Establish the
            // prepared Forward baseline before comparing the Deferred paths.
            Capture();
            var forward = Capture(); UseDeferred(); var sampled = Capture();
            for (int i = 0; i < forward.Length; i++) CompactNear(forward[i], sampled[i], "Mixed material depth/clip/fog pixel " + i);
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Vulkan)
            {
                m_Asset.mobileSettings.deferredExecution = MobileDeferredExecution.NativeRenderPass;
                var native = Capture();
                for (int i = 0; i < native.Length; i++) CompactNear(sampled[i], native[i], "Mixed native pixel " + i);
            }
        }

        [TestCase(0, false)] [TestCase(2, false)] [TestCase(3, false)] [TestCase(4, false)] [TestCase(5, false)] [TestCase(1, false)]
        [TestCase(0, true)] [TestCase(2, true)] [TestCase(3, true)] [TestCase(5, true)]
        public void DeferredBudgetRoutesUnsupportedMaterialsToForward(int type, bool extraRT)
        {
            var material = Material(true, Color.gray); material.SetFloat("_MaterialID", type);
            material.SetFloat("_CoatMask", 0.5f);
            HDShaderUtils.ResetMaterialKeywords(material); Quad(material);
            m_Asset.mobileSettings.extendedLitMaterials = extraRT;
            var forward = Capture(); UseDeferred(); var deferred = Capture();
            for (int i = 0; i < forward.Length; i++) CompactNear(forward[i], deferred[i], "Forward fallback pixel " + i);
            var backend = (MobileRenderPipelineBackend)typeof(HDRenderPipeline).GetField("m_MobileBackend", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(RenderPipelineManager.currentPipeline);
            Assert.IsTrue(backend.lastCameraUsedDeferred, "Only the material should fall back, not the camera.");
            Assert.AreEqual(extraRT ? 4 : 3, backend.lastCameraColorAttachmentCount);
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Vulkan)
            {
                m_Asset.mobileSettings.deferredExecution = MobileDeferredExecution.NativeRenderPass;
                var native = Capture();
                for (int i = 0; i < native.Length; i++) CompactNear(deferred[i], native[i], "Native fallback pixel " + i);
                Assert.AreEqual(extraRT ? 4 : 3, backend.lastCameraColorAttachmentCount);
                Assert.IsTrue(backend.lastCameraUsedNativeDeferred);
            }
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public void DesktopImageSurvivesMobileDeferredRoundTrip(bool extended, bool lit)
        {
            Quad(Material(lit, Color.gray));
            if (lit)
            {
                m_Sun.gameObject.AddComponent<HDAdditionalLightData>().SetIntensity(4, LightUnit.Lux);
                m_Sun.shadows = LightShadows.Soft;
            }
            var hd = m_Camera.GetComponent<HDAdditionalCameraData>();
            hd.customRenderingSettings = true;
            hd.renderingPathCustomFrameSettings.SetEnabled(FrameSettingsField.ExposureControl, false);
            hd.renderingPathCustomFrameSettingsOverrideMask.mask[(uint)FrameSettingsField.ExposureControl] = true;
            void Switch(HDRendererMode mode)
            {
                m_Asset.rendererMode = mode;
                typeof(RenderPipelineManager).GetMethod("PrepareRenderPipeline", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
                    .Invoke(null, new object[] { m_Asset });
            }
            Switch(HDRendererMode.Desktop);
            Capture(); var baseline = Capture();
            Assert.Greater(Center(baseline).r, 0.01f, "Desktop baseline must render geometry.");
            m_Asset.mobileSettings.renderingPath = MobileRenderingPath.Deferred;
            m_Asset.mobileSettings.extendedLitMaterials = extended;
            // Editing dormant Mobile settings must not affect Desktop.
            Same(baseline, Capture());
            Switch(HDRendererMode.Mobile);
            Capture();
            Switch(HDRendererMode.Desktop);
            Assert.IsFalse(Shader.IsKeywordEnabled("HDRP_MOBILE_RENDERER"));
            Assert.IsFalse(Shader.IsKeywordEnabled("MOBILE_EXTENDED_LIT"));
            Capture(); Same(baseline, Capture());
        }

        [TestCase(false)] [TestCase(true)]
        public void DeferredDesktopAutoUsesStoredTextures(bool extended)
        {
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D11 && SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D12)
                Assert.Ignore("DirectX desktop path test");
            m_Asset.mobileSettings.extendedLitMaterials = extended;
            Quad(Material(true, Color.gray)); UseDeferred(); var sampled = Capture();
            m_Asset.mobileSettings.deferredExecution = MobileDeferredExecution.Auto;
            Same(sampled, Capture());
            var pipeline = RenderPipelineManager.currentPipeline;
            var backend = (MobileRenderPipelineBackend)pipeline.GetType().GetField("m_MobileBackend", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(pipeline);
            Assert.IsTrue(backend.lastCameraUsedDeferred);
            Assert.IsFalse(backend.lastCameraUsedNativeDeferred);
        }
        void UseDeferred()
        {
            Assert.IsTrue(MobileRenderPipelineBackend.CompactFormatsSupported(), "Tests require the exact compact formats.");
            Assert.IsTrue(m_Asset.mobileResources.deferredShader.isSupported, "Deferred shader must be supported, not silently fall back.");
            m_Asset.mobileSettings.renderingPath = MobileRenderingPath.Deferred;
            m_Asset.mobileSettings.deferredExecution = MobileDeferredExecution.Sampled;
        }

        static void CompactNear(Color expected, Color actual, string context)
        {
            for (int channel = 0; channel < 3; channel++)
                Assert.That(actual[channel], Is.EqualTo(expected[channel]).Within(0.008f + Mathf.Abs(expected[channel]) * 0.055f), context);
        }

        [TestCase(0)] [TestCase(1)] [TestCase(2)] [TestCase(3)]
        public void DeferredStandardMatchesForwardAndUserBitDoesNotAffectLighting(int flags)
        {
            var material = Material(true, new Color(0.3f, 0.5f, 0.7f));
            material.SetFloat("_MobileReceiveShadows", (flags & 1) == 0 ? 1 : 0);
            material.SetFloat("_MobileSpecularHighlights", (flags & 2) == 0 ? 1 : 0);
            material.SetFloat("_Metallic", 0.6f);
            material.SetColor("_EmissiveColor", new Color(0.03f, 0.02f, 0.01f));
            HDShaderUtils.ResetMaterialKeywords(material); Quad(material);
            var forward = Center(Capture());
            UseDeferred(); var deferred = Center(Capture());
            CompactNear(forward, deferred, "Standard Forward/Deferred");
            material.SetFloat("_MobileUserStencilBit", 1); HDShaderUtils.ResetMaterialKeywords(material);
            Near(deferred, Center(Capture()), "User bit must be ignored by lighting");
        }

        [TestCase(false)] [TestCase(true)]
        public void DeferredLocalLightVolumeAndCameraInsideMatchForward(bool spot)
        {
            m_Sun.intensity = 0;
            Quad(Material(true, Color.gray));
            var light = MakeObject("Deferred local light").AddComponent<Light>();
            light.type = spot ? LightType.Spot : LightType.Point; light.cullingMask = m_Camera.cullingMask;
            light.spotAngle = 70; light.innerSpotAngle = 40; light.intensity = 3; light.shadows = LightShadows.None;
            light.transform.position = new Vector3(0.1f, 0.1f, -0.5f);
            foreach (float range in new[] { 1.5f, 10f })
            {
                light.range = range;
                m_Asset.mobileSettings.renderingPath = MobileRenderingPath.Forward;
                var forward = Center(Capture());
                Assert.Greater(forward.r, 0.01f);
                UseDeferred(); CompactNear(forward, Center(Capture()), "Local volume/near-plane fallback");
            }
        }

        [TestCase(8)] [TestCase(32)]
        public void DeferredAccumulatesMoreThanFourLocalLights(int count)
        {
            m_Sun.intensity = 0; Quad(Material(true, Color.gray)); UseDeferred();
            for (int i = 0; i < count; i++)
            {
                var light = MakeObject("Local light " + i).AddComponent<Light>();
                light.type = LightType.Point; light.cullingMask = m_Camera.cullingMask;
                light.transform.position = new Vector3(0, 0, -1); light.range = 5; light.intensity = 0.2f;
                if (i != 0) light.enabled = false;
            }
            var one = Center(Capture());
            foreach (var obj in m_Owned) if (obj is GameObject go && go.TryGetComponent<Light>(out var light) && light != m_Sun) light.enabled = true;
            var many = Center(Capture());
            Assert.Greater(one.r, 0.001f);
            Assert.Greater(many.r, one.r * count * 0.8f, "Deferred must not inherit the per-object four-light limit");
        }

        [Test]
        public void DeferredUnlitTransparentFogAndCameraResizeRemainStable()
        {
            var lit = Material(true, Color.gray); Quad(lit, 0.2f);
            var unlit = Material(false, new Color(0.2f, 0.4f, 0.6f));
            var renderer = Quad(unlit); renderer.transform.localScale = Vector3.one * 0.5f;
            m_Asset.mobileSettings.fog = true; m_Asset.mobileSettings.fogStart = 1; m_Asset.mobileSettings.fogEnd = 10;
            var forward = Center(Capture()); UseDeferred(); CompactNear(forward, Center(Capture()), "Unlit must overwrite deferred and fog once");
            var cameraB = MakeCamera(64, 48);
            Capture(cameraB); CompactNear(forward, Center(Capture()), "A/B/A depth reallocation");
            unlit.SetFloat("_SurfaceType", 1); unlit.SetFloat("_BlendMode", 0);
            unlit.SetColor("_UnlitColor", new Color(0.2f, 0.4f, 0.6f, 0.5f)); HDShaderUtils.ResetMaterialKeywords(unlit);
            m_Asset.mobileSettings.renderingPath = MobileRenderingPath.Forward; forward = Center(Capture());
            UseDeferred(); CompactNear(forward, Center(Capture()), "Transparent forward tail");
        }

        [Test]
        public void DeferredNativeSubpassesMatchSampledOnVulkan()
        {
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Vulkan) Assert.Ignore("Native input attachments require Vulkan.");
            Quad(Material(true, new Color(0.3f, 0.5f, 0.7f)));
            m_Asset.mobileSettings.fog = true; m_Asset.mobileSettings.fogStart = 1; m_Asset.mobileSettings.fogEnd = 20;
            UseDeferred(); var sampled = Capture();
            m_Asset.mobileSettings.deferredExecution = MobileDeferredExecution.NativeRenderPass;
            var native = Capture();
            for (int i = 0; i < sampled.Length; i++) CompactNear(sampled[i], native[i], "Native/Sampled pixel " + i);
        }

        [TestCase(false, 1f)] [TestCase(true, 1f)]
        [TestCase(false, 0.5f)] [TestCase(true, 0.5f)]
        public void DeferredSceneViewResizeKeepsCameraDepthAliveAndRecoversLostTexture(bool native, float scale)
        {
            if (native && SystemInfo.graphicsDeviceType != GraphicsDeviceType.Vulkan)
                Assert.Ignore("Native input attachments require Vulkan.");
            UseDeferred();
            m_Asset.mobileSettings.deferredExecution = native ? MobileDeferredExecution.NativeRenderPass : MobileDeferredExecution.Sampled;
            m_Asset.mobileSettings.renderScale = scale;
            m_Camera.cameraType = CameraType.SceneView;
            var material = Material(true, Color.gray);
            Quad(material).transform.localScale = Vector3.one * 20;
            Capture(); // Initialize Unity's newly created preview-scene lighting before resizing.
            var reference = Center(Capture());
            Assert.Greater(reference.maxColorComponent, 0.01f);
            var initialColors = new System.Collections.Generic.List<RenderTexture>();
            foreach (var texture in Resources.FindObjectsOfTypeAll<RenderTexture>())
                if (texture.name.StartsWith("Mobile HDR Color") && texture.width == Mathf.RoundToInt(Width * scale)
                    && texture.height == Mathf.RoundToInt(Height * scale)) initialColors.Add(texture);
            Assert.IsNotEmpty(initialColors);
            var backend = (MobileRenderPipelineBackend)typeof(HDRenderPipeline).GetField("m_MobileBackend",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(RenderPipelineManager.currentPipeline);
            var depths = (System.Collections.Generic.Dictionary<Camera, RTHandle>)typeof(MobileRenderPipelineBackend).GetField("m_DeferredDepths",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(backend);
            var other = MakeCamera(73, 51);
            other.Render();
            var otherDepth = depths[other];
            var target = m_Camera.targetTexture;
            for (int frame = 0; frame < 12; frame++)
            {
                var oldDepth = depths[m_Camera].rt;
                target.Release();
                target.width = 91 + frame * 7; target.height = 67 + frame * 3;
                Assert.IsTrue(target.Create());
                other.Render(); // No CPU readback between the cameras.
                var pixels = Capture();
                Assert.IsTrue(oldDepth == null, "Resized attachment must be destroyed exactly once.");
                Assert.AreSame(otherDepth, depths[other], "Scene View resize must not reallocate another camera's depth.");
                Assert.IsTrue(otherDepth.rt.IsCreated());
                Assert.AreEqual(Mathf.RoundToInt(target.width * scale), depths[m_Camera].rt.width);
                Assert.AreEqual(Mathf.RoundToInt(target.height * scale), depths[m_Camera].rt.height);
                // Check every pixel, including newly exposed edges on the first resized frame.
                foreach (var pixel in pixels) CompactNear(reference, pixel, "Resize frame " + frame);
                depths[m_Camera].rt.Release();
                foreach (var pixel in Capture()) CompactNear(reference, pixel, "Lost depth recovery " + frame);
                Assert.IsTrue(depths[m_Camera].rt.IsCreated());
            }
            Object.DestroyImmediate(other.gameObject);
            backend.EndFrame();
            foreach (var texture in initialColors)
                Assert.IsTrue(texture == null, "Old sizes must leave the RenderGraph pool even when edit-mode Time.frameCount does not advance.");
            Assert.IsNull(otherDepth.rt, "Closing a camera releases its attachment.");
            Assert.AreEqual(1, depths.Count);
            m_Asset.mobileSettings.renderingPath = MobileRenderingPath.Forward;
            Capture();
            Assert.AreEqual(0, depths.Count, "Switching to Forward releases deferred depth.");
        }

        [Test]
        public void DeferredCutoutAndCascadeShadowsUseProductionPasses()
        {
            UseDeferred();
            ForwardShadowSelectionAndPickingShareCutoutCoverage(true);
            RealCascadeSceneCastsShadowsDuringCameraMotion();
        }

        [TestCase(1f)] [TestCase(0.5f)]
        public void DeferredSceneViewPreservesOverlayDepth(float scale)
        {
            UseDeferred();
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Vulkan)
                m_Asset.mobileSettings.deferredExecution = MobileDeferredExecution.NativeRenderPass;
            SceneViewFinalDepthOccludesEditorOverlays(scale);
        }

        [Test]
        public void DeferredMirroredDoubleSidedNormalsMatchForward()
        {
            var material = Material(true, Color.gray); material.SetFloat("_DoubleSidedEnable", 1);
            HDShaderUtils.ResetMaterialKeywords(material); var renderer = Quad(material);
            renderer.transform.localScale = new Vector3(-1, 1, 1);
            renderer.transform.rotation = Quaternion.Euler(0, 180, 0);
            var forward = Capture(); UseDeferred(); var deferred = Capture();
            Assert.Greater(Center(forward).maxColorComponent, 0.001f);
            for (int i = 0; i < forward.Length; i++) CompactNear(forward[i], deferred[i], "Mirrored double-sided pixel " + i);
        }

        [TestCase(false)] [TestCase(true)]
        public void DeferredReconstructsCustomPerspectiveAndOrthographicViews(bool orthographic)
        {
            m_Camera.orthographic = orthographic;
            var view = m_Camera.worldToCameraMatrix * Matrix4x4.Translate(new Vector3(-0.2f, -0.15f, 0));
            m_Camera.transform.position += Vector3.right * 10; m_Camera.worldToCameraMatrix = view;
            m_Sun.intensity = 0; Quad(Material(true, Color.gray));
            var light = MakeObject("Offset point light").AddComponent<Light>();
            light.type = LightType.Point; light.cullingMask = m_Camera.cullingMask;
            light.transform.position = new Vector3(0.4f, 0.3f, -1); light.range = 3; light.intensity = 0.5f;
            m_Asset.mobileSettings.fog = true; m_Asset.mobileSettings.fogStart = 1; m_Asset.mobileSettings.fogEnd = 7;
            var forward = Capture(); UseDeferred(); var sampled = Capture();
            for (int i = 0; i < forward.Length; i++) CompactNear(forward[i], sampled[i], "Custom view pixel " + i);
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Vulkan)
            {
                m_Asset.mobileSettings.deferredExecution = MobileDeferredExecution.NativeRenderPass;
                var native = Capture();
                for (int i = 0; i < sampled.Length; i++) CompactNear(sampled[i], native[i], "Native custom view pixel " + i);
            }
        }

        [TestCase(0)] [TestCase(1)] [TestCase(2)] [TestCase(3)] [TestCase(4)] [TestCase(5)]
        public void DeferredExtendedLitPreservesMaterialParameters(int type)
        {
            m_Asset.mobileSettings.extendedLitMaterials = true;
            var material = Material(true, new Color(0.35f, 0.5f, 0.65f));
            material.SetFloat("_MaterialID", type); material.SetFloat("_Smoothness", 0.65f);
            material.SetFloat("_Anisotropy", 0.8f); material.SetFloat("_IridescenceThickness", 0.4f);
            material.SetColor("_SpecularColor", new Color(0.2f, 0.45f, 0.7f));
            HDShaderUtils.ResetMaterialKeywords(material); Quad(material);
            m_Sun.transform.rotation = Quaternion.Euler(15, 20, 0);
            var forward = Center(Capture()); Assert.Greater(forward.maxColorComponent, 0.001f);
            UseDeferred(); var deferred = Center(Capture());
            CompactNear(forward, deferred, "Extended material " + type);
            material.SetFloat("_MobileUserStencilBit", 1); HDShaderUtils.ResetMaterialKeywords(material);
            Near(deferred, Center(Capture()), "Extended user stencil bit");
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Vulkan)
            {
                m_Asset.mobileSettings.deferredExecution = MobileDeferredExecution.NativeRenderPass;
                CompactNear(deferred, Center(Capture()), "Extended native attachments " + type);
            }
        }

        [Test]
        public void DeferredTransmissionRespondsToProfileThicknessAndTint()
        {
            m_Asset.mobileSettings.extendedLitMaterials = true; UseDeferred();
            var profile = Own(ScriptableObject.CreateInstance<DiffusionProfileSettings>());
            profile.profile.hash = 12345; profile.profile.scatteringDistance = new Color(2, 1, 0.5f);
            profile.profile.thicknessRemap = new Vector2(0, 5); profile.profile.Validate(); profile.UpdateCache();
            CameraProfile().Add<DiffusionProfileList>().diffusionProfiles.Override(new[] { profile });
            var material = Material(true, Color.white); material.SetFloat("_MaterialID", 5);
            material.SetFloat("_DiffusionProfileHash", HDShadowUtils.Asfloat(profile.profile.hash));
            material.SetFloat("_Thickness", 0); HDShaderUtils.ResetMaterialKeywords(material);
            material.SetFloat("_Thickness", 0); Quad(material);
            m_Sun.transform.rotation = Quaternion.Euler(0, 180, 0);
            var thin = Center(Capture());
            material.SetFloat("_Thickness", 1); var thick = Center(Capture());
            Assert.Greater(thin.r, thick.r + 0.01f, "Transmittance must depend on profile and thickness");
            Assert.Greater(thick.r, thick.b, "Longer red scattering distance must preserve more red transmission");
        }

        [Test]
        public void DeferredRegularTransmissionUsesShadowThickness()
        {
            m_Asset.mobileSettings.extendedLitMaterials = true; UseDeferred();
            var profile = Own(ScriptableObject.CreateInstance<DiffusionProfileSettings>());
            profile.profile.hash = 34567; profile.profile.scatteringDistance = new Color(2, 2, 2);
            profile.profile.worldScale = 0.001f; profile.profile.thicknessRemap = new Vector2(0, 5);
            profile.profile.transmissionMode = DiffusionProfile.TransmissionMode.ThinObject;
            profile.profile.Validate(); profile.UpdateCache();
            CameraProfile().Add<DiffusionProfileList>().diffusionProfiles.Override(new[] { profile });
            var material = Material(true, Color.white); material.SetFloat("_MaterialID", 5);
            material.SetFloat("_DiffusionProfileHash", HDShadowUtils.Asfloat(profile.profile.hash));
            material.SetFloat("_Thickness", 0); HDShaderUtils.ResetMaterialKeywords(material); Quad(material);
            var blockerMaterial = Material(false, Color.black); blockerMaterial.SetFloat("_DoubleSidedEnable", 1);
            HDShaderUtils.ResetMaterialKeywords(blockerMaterial);
            var blocker = Quad(blockerMaterial, 0.2f); blocker.transform.localScale = Vector3.one * 2;
            m_Sun.transform.rotation = Quaternion.Euler(0, 180, 0); m_Sun.shadows = LightShadows.Hard;
            // Let newly enabled Editor shadow casters settle before comparing modes.
            Capture();
            var thin = Center(Capture());
            profile.profile.transmissionMode = DiffusionProfile.TransmissionMode.Regular; profile.UpdateCache();
            Capture();
            var regular = Center(Capture());
            Assert.Greater(regular.r, thin.r + 0.005f, "Regular transmission must use thickness instead of binary back-face shadowing");
            blocker.transform.position = new Vector3(0, 0, 8);
            Assert.Less(Center(Capture()).r, regular.r, "Greater shadow-derived thickness must attenuate transmission");
        }

        [TestCase(2, "_Anisotropy", 0, 0.9f)]
        [TestCase(3, "_IridescenceThickness", 0.1f, 0.8f)]
        [TestCase(1, "_CoatMask", 0, 1)]
        public void DeferredExtendedParametersChangeActualLighting(int type, string property, float first, float second)
        {
            m_Asset.mobileSettings.extendedLitMaterials = true; UseDeferred();
            var material = Material(true, new Color(0.4f, 0.2f, 0.1f));
            material.SetFloat("_MaterialID", type); material.SetFloat("_Smoothness", 0.7f);
            material.SetFloat("_IridescenceMask", 1); HDShaderUtils.ResetMaterialKeywords(material); Quad(material);
            m_Sun.transform.rotation = Quaternion.Euler(10, 20, 0);
            material.SetFloat(property, first); var a = Capture();
            material.SetFloat(property, second); var b = Capture();
            float difference = 0;
            for (int i = 0; i < a.Length; i++) difference += Mathf.Abs(a[i].r-b[i].r) + Mathf.Abs(a[i].g-b[i].g) + Mathf.Abs(a[i].b-b[i].b);
            Assert.Greater(difference, 0.01f, property + " must affect rendered lighting");
        }

        [Test]
        public void DeferredClearCoatReflectsTheEnvironmentWithoutDirectLights()
        {
            m_Asset.mobileSettings.extendedLitMaterials = true; m_Asset.mobileSettings.environmentLighting = true; UseDeferred();
            var profile = CameraProfile(); profile.Add<VisualEnvironment>().skyType.Override((int)SkyType.Gradient);
            var sky = profile.Add<GradientSky>(); sky.top.Override(Color.white); sky.middle.Override(Color.white); sky.bottom.Override(Color.white);
            profile.Add<Exposure>().fixedExposure.Override(0); m_Sun.enabled = false;
            var material = Material(true, Color.black); material.SetFloat("_Smoothness", 0); Quad(material);
            material.SetFloat("_CoatMask", 0); var uncoated = Center(Capture());
            material.SetFloat("_CoatMask", 1); var coated = Center(Capture());
            Assert.Greater(coated.r, uncoated.r + 0.001f, "Coat must sample its own environment lobe");
        }

        [Test]
        public void DeferredObjectLayerFilteringFallsBackWithReason()
        {
            Quad(Material(true, Color.gray)); m_Camera.cullingMask |= 1 << 29;
            var forward = Center(Capture()); UseDeferred();
            UnityEngine.TestTools.LogAssert.Expect(LogType.Warning, "HDRP Mobile deferred fallback to Forward: Per-light object layer filtering requires Forward.");
            Near(forward, Center(Capture()), "Fallback preserves per-object light filtering");
        }

        [Test]
        public void DeferredExtendedAttachmentsKeepTheStandardBrdf()
        {
            var material = Material(true, new Color(0.3f, 0.4f, 0.5f));
            material.SetFloat("_Metallic", 0.7f); material.SetFloat("_Smoothness", 0.6f); Quad(material);
            m_Sun.transform.rotation = Quaternion.Euler(12, 25, 0); UseDeferred();
            var compact = Center(Capture()); m_Asset.mobileSettings.extendedLitMaterials = true;
            CompactNear(compact, Center(Capture()), "Enabling extended Lit must preserve Standard lighting");
        }

        [Test]
        public void DeferredSubsurfaceUsesForwardWithoutDiffusionAttachments()
        {
            m_Asset.mobileSettings.extendedLitMaterials = true; UseDeferred();
            var profile = Own(ScriptableObject.CreateInstance<DiffusionProfileSettings>());
            profile.profile.hash = 23456; profile.profile.scatteringDistance = new Color(0.2f, 0.1f, 0.05f);
            profile.profile.worldScale = 0.01f; profile.profile.Validate(); profile.UpdateCache();
            CameraProfile().Add<DiffusionProfileList>().diffusionProfiles.Override(new[] { profile });
            var texture = Own(new Texture2D(8, 8, TextureFormat.RGBAFloat, false, true));
            var texels = new Color[64];
            for (int i = 0; i < 64; i++) texels[i] = i % 8 < 4 ? Color.white : new Color(0.03f, 0.03f, 0.03f, 1);
            texture.SetPixels(texels); texture.Apply(); texture.filterMode = FilterMode.Point;
            var material = Material(true, Color.white); material.SetFloat("_MaterialID", 0);
            material.SetFloat("_DiffusionProfileHash", HDShadowUtils.Asfloat(profile.profile.hash));
            material.SetTexture("_BaseColorMap", texture); material.SetFloat("_MobileSpecularHighlights", 0);
            HDShaderUtils.ResetMaterialKeywords(material); Quad(material);
            material.SetFloat("_SubsurfaceMask", 0); var unfiltered = Capture();
            material.SetFloat("_SubsurfaceMask", 1); var filtered = Capture();
            m_Asset.mobileSettings.renderingPath = MobileRenderingPath.Forward;
            var forward = Capture();
            for (int i = 0; i < forward.Length; i++) CompactNear(forward[i], filtered[i], "SSS Forward fallback packed HDR pixel " + i);
            UseDeferred();
            m_Sun.intensity = 0; material.SetColor("_EmissiveColor", Color.white); material.SetTexture("_EmissiveColorMap", texture);
            HDShaderUtils.ResetMaterialKeywords(material);
            material.SetFloat("_SubsurfaceMask", 0); unfiltered = Capture();
            material.SetFloat("_SubsurfaceMask", 1); filtered = Capture();
            Same(unfiltered, filtered);
        }
    }
}
