using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityEditor.Rendering.HighDefinition.Tests
{
    public class MobileShadowTests
    {
        [Test]
        public void ProductionCascadeSamplingBlendsContinuouslyAndRejectsInvalidCoordinates()
        {
            Assert.AreNotEqual(GraphicsDeviceType.Null, SystemInfo.graphicsDeviceType);
            var material = new Material(AssetDatabase.LoadAssetAtPath<Shader>("Packages/com.unity.render-pipelines.high-definition/Tests/Editor/MobileURPReference/ShadowTransition.shader"));
            var target = new RenderTexture(121, 1, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            var atlas = new RenderTexture(64, 64, 16, RenderTextureFormat.Shadowmap);
            var readback = new Texture2D(121, 1, TextureFormat.RGBAFloat, false, true);
            var previous = RenderTexture.active;
            try
            {
                atlas.Create(); target.Create();
                RenderTexture.active = atlas; GL.Clear(true, false, Color.black, 0.5f);
                ShaderUtil.CompilePass(material, 0, true);
                material.SetTexture("_MobileShadowAtlas", atlas);
                material.SetVector("_MobileCameraPosition", Vector4.zero);
                material.SetVectorArray("_MobileShadowSpheres", new[] { new Vector4(0, 0, 0, 1), new Vector4(0, 0, 0, 4) });
                var first = Matrix4x4.zero; first.m03 = 0.25f; first.m13 = 0.25f; first.m23 = SystemInfo.usesReversedZBuffer ? 0.25f : 0.75f; first.m33 = 1;
                var second = first; second.m03 = 0.75f; second.m23 = 1 - first.m23;
                material.SetMatrixArray("_MobileShadowMatrices", new[] { first, second });
                Color[] Capture(float strength, float blend, float distance = 100)
                {
                    material.SetVector("_MobileShadowParams", new Vector4(strength, 1f / 64, distance, blend));
                    Graphics.Blit(Texture2D.blackTexture, target, material);
                    RenderTexture.active = target; readback.ReadPixels(new Rect(0, 0, 121, 1), 0, 0); readback.Apply(); return readback.GetPixels();
                }
                var pixels = Capture(1, 0.1f);
                Assert.That(pixels[90].g, Is.EqualTo(0).Within(0.002f));
                Assert.That(pixels[95].g, Is.EqualTo(0.5f).Within(0.002f));
                Assert.That(pixels[100].g, Is.EqualTo(1).Within(0.002f));
                for (int i = 91; i <= 100; i++)
                { Assert.GreaterOrEqual(pixels[i].g, pixels[i - 1].g); Assert.Less(pixels[i].g - pixels[i - 1].g, 0.16f); }
                Assert.AreEqual(0, Capture(1, 0)[99].g);
                foreach (var pixel in Capture(0, 0.1f)) Assert.AreEqual(1, pixel.g);
                // Beyond the second sphere and beyond the projection must be neutral.
                material.SetVectorArray("_MobileShadowSpheres", new[] { new Vector4(0, 0, 0, 1), new Vector4(0, 0, 0, 1.21f) });
                Assert.AreEqual(1, Capture(1, 0.1f)[120].g);
                first.m03 = -1; material.SetMatrixArray("_MobileShadowMatrices", new[] { first, second });
                Assert.AreEqual(1, Capture(1, 0.1f)[0].g);
                first.m03 = 0.25f; first.m23 = -1; material.SetMatrixArray("_MobileShadowMatrices", new[] { first, second });
                Assert.AreEqual(1, Capture(1, 0.1f)[0].g);
                first.m23 = SystemInfo.usesReversedZBuffer ? 0.25f : 0.75f;
                material.SetMatrixArray("_MobileShadowMatrices", new[] { first, second });
                Assert.AreEqual(1, Capture(1, 0, 0.8f)[90].g);
            }
            finally
            {
                RenderTexture.active = previous; target.Release(); atlas.Release();
                Object.DestroyImmediate(target); Object.DestroyImmediate(atlas); Object.DestroyImmediate(readback); Object.DestroyImmediate(material);
            }
        }
    }
}
