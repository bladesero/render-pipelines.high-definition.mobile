using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace UnityEditor.Rendering.HighDefinition.Tests
{
    public class MobileExposureTests
    {
        [TestCase(ExposureMode.Automatic)]
        [TestCase(ExposureMode.AutomaticHistogram)]
        [TestCase(ExposureMode.CurveMapping)]
        public void AutomaticModesFallBackWithoutChangingDesktopMode(ExposureMode mode)
        {
            var exposure = ScriptableObject.CreateInstance<Exposure>();
            var go = new GameObject("Mobile automatic fallback test");
            try
            {
                var camera = go.AddComponent<Camera>();
                exposure.mode.Override(mode);
                exposure.fixedExposure.Override(10f);
                exposure.compensation.Override(1f);
                Assert.That(MobileExposure.Evaluate(exposure, camera), Is.EqualTo(MobileExposure.FromEV100(10, 1, MobileExposure.LensScale)).Within(1e-7f));
                Assert.AreEqual(mode, exposure.mode.value);
                Assert.IsTrue(exposure.mode.overrideState);
                Assert.AreEqual(1f, MobileExposure.Evaluate(exposure, camera, false));
                exposure.active = false;
                Assert.AreEqual(1f, MobileExposure.Evaluate(exposure, camera));
            }
            finally { Object.DestroyImmediate(go); Object.DestroyImmediate(exposure); }
        }

        [TestCase(false)] [TestCase(true)]
        public void SharedVolumeParametersMatchDesktopKernel(bool physical)
        {
            var exposure = ScriptableObject.CreateInstance<Exposure>();
            var go = new GameObject("Shared exposure test");
            var camera = go.AddComponent<Camera>();
            var shader = AssetDatabase.LoadAssetAtPath<ComputeShader>("Packages/com.unity.render-pipelines.high-definition/Runtime/PostProcessing/Shaders/Exposure.compute");
            var target = new RenderTexture(1, 1, 0, RenderTextureFormat.RGFloat, RenderTextureReadWrite.Linear) { enableRandomWrite = true };
            var read = new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true);
            var previous = RenderTexture.active;
            try
            {
                exposure.mode.value = physical ? ExposureMode.UsePhysicalCamera : ExposureMode.Fixed;
                camera.aperture = 4; camera.shutterSpeed = 0.125f; camera.iso = 100;
                target.Create();
                foreach (float ev in new[] { -4f, 0f, 7f, 20f })
                foreach (float compensation in new[] { -2f, 0f, 2f })
                {
                    exposure.fixedExposure.value = ev; exposure.compensation.value = compensation;
                    var parameters = exposure.GetFixedExposureParameters(camera, 0, out bool usePhysical);
                    Assert.AreEqual(physical, usePhysical);
                    int kernel = shader.FindKernel(usePhysical ? "KManualCameraExposure" : "KFixedExposure");
                    shader.SetTexture(kernel, "_OutputTexture", target);
                    shader.SetVector("_ExposureParams", parameters);
                    shader.SetVector("_ExposureParams2", new Vector4(0, 0, MobileExposure.LensScale, 12.5f));
                    shader.Dispatch(kernel, 1, 1, 1);
                    RenderTexture.active = target; read.ReadPixels(new Rect(0, 0, 1, 1), 0, 0); read.Apply();
                    float expected = read.GetPixel(0, 0).r;
                    Assert.That(MobileExposure.Evaluate(exposure, camera), Is.EqualTo(expected).Within(Mathf.Max(1e-9f, expected * 1e-5f)));
                    Assert.AreEqual(1f, MobileExposure.Evaluate(exposure, camera, false), "Disabled Exposure Control must bypass the Volume.");
                }
            }
            finally
            {
                RenderTexture.active = previous; target.Release(); Object.DestroyImmediate(target);
                Object.DestroyImmediate(read); Object.DestroyImmediate(go); Object.DestroyImmediate(exposure);
            }
        }

        [TestCase(1.2f)] [TestCase(1f)]
        public void FixedEVMatchesDesktopExposureCompute(float lensScale)
        {
            Assert.AreNotEqual(GraphicsDeviceType.Null, SystemInfo.graphicsDeviceType);
            var shader = AssetDatabase.LoadAssetAtPath<ComputeShader>("Packages/com.unity.render-pipelines.high-definition/Runtime/PostProcessing/Shaders/Exposure.compute");
            int kernel = shader.FindKernel("KFixedExposure");
            var target = new RenderTexture(1, 1, 0, RenderTextureFormat.RGFloat, RenderTextureReadWrite.Linear) { enableRandomWrite = true };
            var read = new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true);
            var previous = RenderTexture.active;
            try
            {
                target.Create(); shader.SetTexture(kernel, "_OutputTexture", target);
                shader.SetVector("_ExposureParams2", new Vector4(0, 0, lensScale, 12.5f));
                foreach (float ev in new[] { -4f, 0f, 5f, 14f, 20f }) foreach (float compensation in new[] { -2f, 0f, 2f })
                {
                    shader.SetVector("_ExposureParams", new Vector4(compensation, ev, 0, 0)); shader.Dispatch(kernel, 1, 1, 1);
                    RenderTexture.active = target; read.ReadPixels(new Rect(0, 0, 1, 1), 0, 0); read.Apply();
                    float expected = read.GetPixel(0, 0).r;
                    Assert.That(MobileExposure.FromEV100(ev, compensation, lensScale), Is.EqualTo(expected).Within(Mathf.Max(1e-9f, expected * 1e-5f)));
                }
            }
            finally { RenderTexture.active = previous; target.Release(); Object.DestroyImmediate(target); Object.DestroyImmediate(read); }
        }
    }
}
