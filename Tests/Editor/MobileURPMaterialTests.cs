using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityEditor.Rendering.HighDefinition.Tests
{
    public class MobileURPMaterialTests
    {
        [TestCase(0, TestName = "URP14_DirectBRDF")]
        [TestCase(1, TestName = "URP14_IndirectBRDF")]
        [TestCase(2, TestName = "URP14_AlphaPreserveSpecular")]
        [TestCase(3, TestName = "URP14_Alpha")]
        [TestCase(4, TestName = "URP14_AdditivePreserveSpecular")]
        [TestCase(5, TestName = "URP14_Additive")]
        [TestCase(6, TestName = "URP14_PremultipliedAsset")]
        [TestCase(7, TestName = "URP14_UnlitAlpha")]
        [TestCase(8, TestName = "URP14_UnlitPremultipliedAsset")]
        public void MatchesPinnedUpstreamOnGPU(int mode)
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                Assert.Ignore("GPU parity requires a graphics device.");
            var shader = AssetDatabase.LoadAssetAtPath<Shader>("Packages/com.unity.render-pipelines.high-definition/Tests/Editor/MobileURPReference/Parity.shader");
            Assert.IsNotNull(shader);
            var material = new Material(shader);
            var target = new RenderTexture(128, 64, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            var readback = new Texture2D(128, 64, TextureFormat.RGBAFloat, false, true);
            var previous = RenderTexture.active;
            bool asyncCompilation = ShaderUtil.allowAsyncCompilation;
            try
            {
                ShaderUtil.allowAsyncCompilation = false;
                material.SetInt("_Mode", mode);
                if (mode == 2 || mode == 4) material.EnableKeyword("_ALPHAPREMULTIPLY_ON");
                ShaderUtil.CompilePass(material, 0, true); ShaderUtil.CompilePass(material, 1, true);
                Assert.IsFalse(ShaderUtil.ShaderHasError(shader), "Parity shader compilation failed.");
                target.Create();
                Color[] Capture(int pass)
                {
                    Graphics.Blit(Texture2D.blackTexture, target, material, pass);
                    RenderTexture.active = target;
                    readback.ReadPixels(new Rect(0, 0, 128, 64), 0, 0); readback.Apply();
                    return readback.GetPixels();
                }
                var reference = Capture(0); var mobile = Capture(1);
                float maximumError = 0;
                int worstSample = 0, worstChannel = 0;
                for (int i = 0; i < reference.Length; i++)
                    for (int channel = 0; channel < 4; channel++)
                    {
                        float expected = reference[i][channel], actual = mobile[i][channel];
                        Assert.IsFalse(float.IsNaN(expected) || float.IsInfinity(expected) || float.IsNaN(actual) || float.IsInfinity(actual));
                        float normalizedError = Mathf.Abs(actual - expected) / Mathf.Max(1, Mathf.Abs(expected));
                        if (normalizedError > maximumError) { maximumError = normalizedError; worstSample = i; worstChannel = channel; }
                    }
                Debug.Log($"Mobile precision mode {mode}: maximum normalized error {maximumError}, sample {worstSample}, channel {worstChannel}");
                // Mobile explicitly uses min16float while the pinned desktop oracle
                // remains FP32. Allow ~two FP16 epsilons; keep finite checks and audit
                // the worst sample across the entire roughness/metallic/angle sweep.
                Assert.LessOrEqual(maximumError, 0.002f, $"Mode {mode}, sample {worstSample}, channel {worstChannel}");
            }
            finally
            {
                RenderTexture.active = previous; ShaderUtil.allowAsyncCompilation = asyncCompilation;
                target.Release(); Object.DestroyImmediate(target); Object.DestroyImmediate(readback); Object.DestroyImmediate(material);
            }
        }
    }
}
