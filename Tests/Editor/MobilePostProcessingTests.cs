using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace UnityEditor.Rendering.HighDefinition.Tests
{
    public partial class MobileImageTests
    {
        [TestCase(HDAdditionalCameraData.AntialiasingMode.None, false)]
        [TestCase(HDAdditionalCameraData.AntialiasingMode.FastApproximateAntialiasing, true)]
        [TestCase(HDAdditionalCameraData.AntialiasingMode.SubpixelMorphologicalAntiAliasing, false)]
        [TestCase(HDAdditionalCameraData.AntialiasingMode.TemporalAntialiasing, false)]
        public void MobileAntialiasingRespectsCameraModeAndFrameSettings(HDAdditionalCameraData.AntialiasingMode mode, bool fxaa)
        {
            var frame = FrameSettings.NewDefaultCamera();
            m_Camera.GetComponent<HDAdditionalCameraData>().antialiasing = mode;
            Assert.AreEqual(fxaa, MobileRenderPipelineBackend.IsFXAAEnabled(m_Camera, frame));
            frame.SetEnabled(FrameSettingsField.Antialiasing, false);
            Assert.IsFalse(MobileRenderPipelineBackend.IsFXAAEnabled(m_Camera, frame));
            frame.SetEnabled(FrameSettingsField.Antialiasing, true);
            frame.SetEnabled(FrameSettingsField.Postprocess, false);
            Assert.IsFalse(MobileRenderPipelineBackend.IsFXAAEnabled(m_Camera, frame));
        }

        [Test]
        public void MobileSceneViewAntialiasingUsesHDRPSceneViewSettings()
        {
            var previous = HDAdditionalSceneViewSettings.sceneViewAntialiasing;
            var view = ScriptableObject.CreateInstance<SceneView>();
            bool registered = SceneView.sceneViews.Contains(view);
            if (!registered) SceneView.sceneViews.Add(view);
            try
            {
                var frame = FrameSettings.NewDefaultCamera();
                var camera = view.camera;
                var data = camera.GetComponent<HDAdditionalCameraData>();
                if (data == null) data = camera.gameObject.AddComponent<HDAdditionalCameraData>();
                data.antialiasing = HDAdditionalCameraData.AntialiasingMode.None;
                view.sceneViewState.showImageEffects = true;
                HDAdditionalSceneViewSettings.sceneViewAntialiasing = HDAdditionalCameraData.AntialiasingMode.FastApproximateAntialiasing;
                Assert.IsTrue(MobileRenderPipelineBackend.IsFXAAEnabled(camera, frame));
                view.sceneViewState.showImageEffects = false;
                Assert.IsFalse(MobileRenderPipelineBackend.IsFXAAEnabled(camera, frame));
                view.sceneViewState.showImageEffects = true;
                HDAdditionalSceneViewSettings.sceneViewAntialiasing = HDAdditionalCameraData.AntialiasingMode.None;
                data.antialiasing = HDAdditionalCameraData.AntialiasingMode.FastApproximateAntialiasing;
                Assert.IsFalse(MobileRenderPipelineBackend.IsFXAAEnabled(camera, frame));
            }
            finally
            {
                HDAdditionalSceneViewSettings.sceneViewAntialiasing = previous;
                if (!registered) SceneView.sceneViews.Remove(view);
                Object.DestroyImmediate(view);
            }
        }

        [Test]
        public void EditorRebuildsLutWhenCachedContentsAreCleared()
        {
            var profile = CameraProfile();
            profile.Add<Bloom>().intensity.Override(0);
            profile.Add<Tonemapping>().mode.Override(TonemappingMode.Neutral);
            Quad(Material(false, new Color(0.3f, 0.5f, 0.7f)));
            var expected = Capture();
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var backend = typeof(HDRenderPipeline).GetField("m_MobileBackend", flags).GetValue(RenderPipelineManager.currentPipeline);
            var lut = (RTHandle)backend.GetType().GetField("m_MobileGradingLut", flags).GetValue(backend);
            var previous = RenderTexture.active;
            try
            {
                RenderTexture.active = lut.rt;
                GL.Clear(false, true, Color.black);
            }
            finally { RenderTexture.active = previous; }
            Assert.IsTrue(lut.rt.IsCreated(), "Cleared content must not be confused with a released allocation.");
            Same(expected, Capture());
        }

        [Test]
        public void LutFirstRenderWithAsyncShaderCompilationIsNotCachedBlack()
        {
            var profile = CameraProfile();
            profile.Add<Bloom>().intensity.Override(0);
            profile.Add<WhiteBalance>().temperature.Override(35);
            profile.Add<Tonemapping>().mode.Override(TonemappingMode.Neutral);
            Quad(Material(false, new Color(0.3f, 0.5f, 0.7f)));
            var expected = Capture();
            var backend = typeof(HDRenderPipeline).GetField("m_MobileBackend",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(RenderPipelineManager.currentPipeline);
            var backendType = backend.GetType();
            var material = (Material)backendType.GetField("m_Composite",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(backend);
            var valid = backendType.GetField("m_MobileGradingValid",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            string package = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(AssetDatabase.GetAssetPath(material.shader)).resolvedPath;
            string source = System.IO.File.ReadAllText(System.IO.Path.Combine(package, "Runtime/RenderPipeline/Mobile/Shaders/MobileComposite.shader"));
            source = source.Replace("Hidden/HDRP/Mobile/Composite", "Hidden/HDRP/Tests/ColdLut/" + System.Guid.NewGuid().ToString("N"));
            source = source.Replace("#include \"Mobile", "#include \"Packages/com.unity.render-pipelines.high-definition/Runtime/RenderPipeline/Mobile/Shaders/Mobile")
                .Replace("#include \"../../../PostProcessing/", "#include \"Packages/com.unity.render-pipelines.high-definition/Runtime/PostProcessing/");
            material.shader = Own(ShaderUtil.CreateShaderAsset(source, false));
            material.shaderKeywords = new[] { "TONEMAPPING_NEUTRAL", "GRADE_IN_SRGB" };
            int lutPass = material.FindPass("MobileColorGradingLut");
            for (int i = 0; i < material.passCount; i++)
                if (i != lutPass) ShaderUtil.CompilePass(material, i, true);
            Assert.IsFalse(ShaderUtil.IsPassCompiled(material, lutPass), "Test must start with a cold LUT variant.");
            valid.SetValue(backend, false);
            ShaderUtil.allowAsyncCompilation = true;
            try
            {
                Same(expected, Capture());
                Same(expected, Capture());
            }
            finally { ShaderUtil.allowAsyncCompilation = false; }
        }

        [Test]
        public void ExternalTonemappingTracksTextureContentsAndRejectsInvalidLut()
        {
            var profile = CameraProfile();
            profile.Add<Bloom>().intensity.Override(0);
            var tone = profile.Add<Tonemapping>();
            tone.mode.Override(TonemappingMode.None);
            Quad(Material(false, Color.gray));
            var baseline = Capture();
            int size = m_Asset.currentPlatformRenderPipelineSettings.postProcessSettings.lutSize;
            var lut = Own(new Texture3D(size, size, size, TextureFormat.RGBAFloat, false)
                { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp });
            var pixels = new Color[size * size * size];
            tone.lutTexture.Override(lut);
            tone.lutContribution.Override(1);
            tone.mode.Override(TonemappingMode.External);
            int center = Width * (Height / 2) + Width / 2;
            foreach (var color in new[] { new Color(0.2f, 0.4f, 0.6f), new Color(0.6f, 0.3f, 0.1f) })
            {
                for (int i = 0; i < pixels.Length; i++) pixels[i] = color;
                lut.SetPixels(pixels); lut.Apply();
                Near(color, Capture()[center], "External LUT content update");
            }
            tone.lutTexture.Override(null);
            Same(baseline, Capture());
        }

        [Test]
        public void BloomUsesVolumeThresholdAndTint()
        {
            var profile = CameraProfile();
            var bloom = profile.Add<Bloom>(); bloom.intensity.Override(0);
            var material = Material(false, Color.gray);
            material.SetColor("_EmissiveColor", Color.white * 4);
            Quad(material);
            var baseline = Capture();
            bloom.intensity.Override(1);
            bloom.threshold.Override(100);
            Same(baseline, Capture());
            bloom.threshold.Override(0);
            bloom.tint.Override(Color.red);
            int center = Width * (Height / 2) + Width / 2;
            var tinted = Capture();
            Assert.Greater(tinted[center].r, baseline[center].r + 0.1f);
            Assert.That(tinted[center].g, Is.EqualTo(baseline[center].g).Within(0.002f));
        }

        [TestCase("TONEMAPPING_NONE", "GRADE_IN_SRGB")]
        [TestCase("TONEMAPPING_NEUTRAL", "GRADE_IN_SRGB")]
        [TestCase("TONEMAPPING_ACES_APPROX", "GRADE_IN_SRGB")]
        [TestCase("TONEMAPPING_ACES_FULL", "GRADE_IN_SRGB")]
        [TestCase("TONEMAPPING_CUSTOM", "GRADE_IN_SRGB")]
        [TestCase("TONEMAPPING_NONE", "GRADE_IN_ACESCG")]
        [TestCase("TONEMAPPING_NEUTRAL", "GRADE_IN_ACESCG")]
        public void MobileRasterLutMatchesDesktopCompute(string keyword, string gradingSpace)
        {
            var profile = CameraProfile();
            profile.Add<WhiteBalance>().temperature.Override(30);
            profile.Add<ColorAdjustments>().contrast.Override(25);
            profile.Add<ChannelMixer>().redOutBlueIn.Override(15);
            profile.Add<Tonemapping>().mode.Override(TonemappingMode.Custom);
            Quad(Material(false, Color.gray));
            Capture(); // Populate shared grading uniforms using the real mobile camera path.
            var material = Own(new Material(Shader.Find("Hidden/HDRP/Mobile/Composite")));
            material.shaderKeywords = new[] { keyword, gradingSpace };
            var raster = Own(new RenderTexture(1024, 32, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear));
            raster.Create();
            var previous = RenderTexture.active;
            Graphics.Blit(null, raster, material, material.FindPass("MobileColorGradingLut"));
            RenderTexture.active = previous;
            var compute = Own(Object.Instantiate(AssetDatabase.LoadAssetAtPath<ComputeShader>(
                "Packages/com.unity.render-pipelines.high-definition/Runtime/PostProcessing/Shaders/LutBuilder3D.compute")));
            compute.shaderKeywords = new[] { keyword, gradingSpace };
            int kernel = compute.FindKernel("KBuild");
            foreach (string name in new[] { "_ColorBalance", "_ColorFilter", "_ChannelMixerRed", "_ChannelMixerGreen", "_ChannelMixerBlue",
                "_HueSatCon", "_Lift", "_Gamma", "_Gain", "_Shadows", "_Midtones", "_Highlights", "_ShaHiLimits", "_SplitShadows",
                "_SplitHighlights", "_Params", "_CustomToneCurve", "_ToeSegmentA", "_ToeSegmentB", "_MidSegmentA", "_MidSegmentB", "_ShoSegmentA", "_ShoSegmentB" })
                compute.SetVector(name, Shader.GetGlobalVector(name));
            foreach (string name in new[] { "_CurveMaster", "_CurveRed", "_CurveGreen", "_CurveBlue", "_CurveHueVsHue", "_CurveHueVsSat", "_CurveLumVsSat", "_CurveSatVsSat" })
                compute.SetTexture(kernel, name, Shader.GetGlobalTexture(name));
            var volume = Own(new RenderTexture(32, 32, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear)
            { dimension = TextureDimension.Tex3D, volumeDepth = 32, enableRandomWrite = true });
            volume.Create();
            compute.SetTexture(kernel, "_OutputTexture", volume);
            compute.SetVector("_Size", new Vector4(32, 1f / 31, 0, 0));
            compute.Dispatch(kernel, 8, 8, 8);
            var rasterRead = UnityEngine.Rendering.AsyncGPUReadback.Request(raster);
            var computeRead = UnityEngine.Rendering.AsyncGPUReadback.Request(volume);
            rasterRead.WaitForCompletion(); computeRead.WaitForCompletion();
            Assert.IsFalse(rasterRead.hasError || computeRead.hasError);
            var a = rasterRead.GetData<Color>();
            for (int z = 0; z < 32; z += 3)
            {
                var b = computeRead.GetData<Color>(z);
                for (int y = 0; y < 32; y += 3)
                    for (int x = 0; x < 32; x += 3)
                        Near(b[x + y * 32], a[x + z * 32 + y * 1024], keyword);
            }
        }

        [TestCase("WhiteBalance")]
        [TestCase("ChannelMixer")]
        [TestCase("LiftGammaGain")]
        [TestCase("ShadowsMidtonesHighlights")]
        [TestCase("SplitToning")]
        [TestCase("ColorCurves")]
        [TestCase("ColorAdjustments")]
        public void DesktopGradingVolumesAffectMobileAndReset(string effect)
        {
            var profile = CameraProfile();
            profile.Add<Exposure>().mode.Override(ExposureMode.Fixed);
            profile.Add<Bloom>().intensity.Override(0);
            profile.Add<Tonemapping>().mode.Override(TonemappingMode.None);
            Quad(Material(false, new Color(0.25f, 0.4f, 0.65f)));
            var baseline = Capture();
            switch (effect)
            {
                case "WhiteBalance": profile.Add<WhiteBalance>().temperature.Override(60); break;
                case "ChannelMixer": profile.Add<ChannelMixer>().redOutBlueIn.Override(80); break;
                case "LiftGammaGain": profile.Add<LiftGammaGain>().gain.Override(new Vector4(1, 1, 1, 0.5f)); break;
                case "ShadowsMidtonesHighlights": profile.Add<ShadowsMidtonesHighlights>().midtones.Override(new Vector4(1, 1, 1, 0.5f)); break;
                case "SplitToning": profile.Add<SplitToning>().highlights.Override(Color.red); break;
                case "ColorCurves":
                    profile.Add<ColorCurves>().master.Override(new TextureCurve(AnimationCurve.Linear(0, 0, 1, 0.5f), 0, false, new Vector2(0, 1)));
                    break;
                case "ColorAdjustments": profile.Add<ColorAdjustments>().contrast.Override(45); break;
            }
            int center = Width * (Height / 2) + Width / 2;
            var graded = Capture();
            Assert.Greater(Vector3.Distance((Vector4)baseline[center], (Vector4)graded[center]), 0.01f, effect);
            var cameraData = m_Camera.GetComponent<HDAdditionalCameraData>();
            cameraData.customRenderingSettings = true;
            cameraData.renderingPathCustomFrameSettingsOverrideMask.mask[(uint)FrameSettingsField.ColorGrading] = true;
            cameraData.renderingPathCustomFrameSettings.SetEnabled(FrameSettingsField.ColorGrading, false);
            Same(baseline, Capture());
            cameraData.renderingPathCustomFrameSettings.SetEnabled(FrameSettingsField.ColorGrading, true);
            Same(graded, Capture());
        }

        [TestCase(TonemappingMode.Neutral, false)]
        [TestCase(TonemappingMode.ACES, false)]
        [TestCase(TonemappingMode.ACES, true)]
        [TestCase(TonemappingMode.Custom, false)]
        public void DesktopTonemappingModesRenderOnMobile(TonemappingMode mode, bool fullACES)
        {
            var profile = CameraProfile();
            profile.Add<Bloom>().intensity.Override(0);
            var tone = profile.Add<Tonemapping>();
            tone.mode.Override(TonemappingMode.None);
            var material = Material(false, Color.white);
            material.SetColor("_EmissiveColor", new Color(4, 2, 1));
            Quad(material);
            var baseline = Capture();
            tone.mode.Override(mode);
            tone.useFullACES.Override(fullACES);
            var mapped = Capture();
            int center = Width * (Height / 2) + Width / 2;
            Assert.Less(mapped[center].r, baseline[center].r - 0.1f);
            tone.mode.Override(TonemappingMode.None);
            Same(baseline, Capture());
        }

        [Test]
        public void MobilePostProcessFrameSettingDisablesBloomAndGrading()
        {
            var profile = CameraProfile();
            var bloom = profile.Add<Bloom>(); bloom.intensity.Override(0);
            var color = profile.Add<ColorAdjustments>(); color.postExposure.Override(0);
            var material = Material(false, Color.white);
            material.SetColor("_EmissiveColor", Color.white * 5);
            Quad(material);
            var baseline = Capture();
            bloom.intensity.Override(2);
            color.postExposure.Override(2);
            var hd = m_Camera.GetComponent<HDAdditionalCameraData>();
            hd.customRenderingSettings = true;
            hd.renderingPathCustomFrameSettingsOverrideMask.mask[(uint)FrameSettingsField.Postprocess] = true;
            hd.renderingPathCustomFrameSettings.SetEnabled(FrameSettingsField.Postprocess, false);
            Same(baseline, Capture());
        }
    }
}
