using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.TestTools;

namespace UnityEditor.Rendering.HighDefinition.Tests
{
    public partial class MobileImageTests
    {
        Color SampleAtlas(Material sample, int index, Vector3 direction, float mip)
        {
            var output = RenderTexture.GetTemporary(1, 1, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            var pixels = new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true);
            var previous = RenderTexture.active;
            var cmd = CommandBufferPool.Get("Read Mobile reflection atlas");
            try
            {
                sample.SetInt("_TestProbeIndex", index);
                sample.SetVector("_TestDirectionMip", new Vector4(direction.x, direction.y, direction.z, mip));
                cmd.SetRenderTarget(output);
                cmd.DrawProcedural(Matrix4x4.identity, sample, 0, MeshTopology.Triangles, 3);
                Graphics.ExecuteCommandBuffer(cmd);
                RenderTexture.active = output;
                pixels.ReadPixels(new Rect(0, 0, 1, 1), 0, 0); pixels.Apply();
                return pixels.GetPixel(0, 0);
            }
            finally
            {
                RenderTexture.active = previous; RenderTexture.ReleaseTemporary(output);
                Object.DestroyImmediate(pixels); CommandBufferPool.Release(cmd);
            }
        }

        [UnityTest]
        public IEnumerator HDReflectionAtlasSixDirectionsSeamsAndRoughMips()
        {
            var cube = ReflectionCube(Color.black);
            var colors = new[] { Color.red, Color.green, Color.blue, Color.yellow, Color.cyan, Color.magenta };
            var directions = new[] { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };
            for (int f = 0; f < 6; ++f)
            {
                var pixels = new Color[32 * 32];
                for (int p = 0; p < pixels.Length; ++p) pixels[p] = colors[f];
                cube.SetPixels(pixels, (CubemapFace)f);
            }
            cube.Apply();
            var hd = AtlasProbe(cube, ProbeSettings.Mode.Custom);
            m_Asset.mobileSettings.reflectionAtlasSize = 1024;
            yield return null;
            Capture();
            Assert.AreEqual(1, Shader.GetGlobalInt("_MobileReflectionCount16"));
            var sample = Own(new Material(Shader.Find("Hidden/HDRP/Tests/MobileReflectionAtlas")));
            for (int f = 0; f < 6; ++f)
            {
                var actual = SampleAtlas(sample, 0, directions[f], 0);
                Assert.That(actual.r, Is.EqualTo(colors[f].r).Within(.03f), "Face " + f);
                Assert.That(actual.g, Is.EqualTo(colors[f].g).Within(.03f), "Face " + f);
                Assert.That(actual.b, Is.EqualTo(colors[f].b).Within(.03f), "Face " + f);
            }
            var rough = SampleAtlas(sample, 0, Vector3.right, 6);
            Assert.Greater(rough.g + rough.b, .1f, "Rough mip must integrate other directions, not simply downsample the face.");
            hd.SetTexture(ProbeSettings.Mode.Custom, ReflectionCube(Color.red));
            AtlasProbe(ReflectionCube(Color.green), ProbeSettings.Mode.Custom);
            yield return null;
            Capture();
            Assert.AreEqual(2, Shader.GetGlobalInt("_MobileReflectionCount16"));
            for (int i = 0; i < 2; ++i)
            {
                var expected = SampleAtlas(sample, i, Vector3.forward, 0);
                foreach (float mip in new[] { 0f, 2.5f, 4.5f, 6f })
                    foreach (var direction in new[] { Vector3.back, new Vector3(.001f, .001f, -1), new Vector3(-.001f, -.001f, -1), Vector3.up })
                    {
                        var actual = SampleAtlas(sample, i, direction.normalized, mip);
                        Assert.That(actual.r, Is.EqualTo(expected.r).Within(.025f), "Tile/mip seam " + i + "/" + mip);
                        Assert.That(actual.g, Is.EqualTo(expected.g).Within(.025f));
                    }
            }
        }

        Cubemap ReflectionCube(Color color)
        {
            var cube = Own(new Cubemap(32, TextureFormat.RGBAHalf, true));
            var pixels = new Color[32 * 32];
            for (int i = 0; i < pixels.Length; ++i) pixels[i] = color;
            for (int f = 0; f < 6; ++f) cube.SetPixels(pixels, (CubemapFace)f);
            cube.Apply(); return cube;
        }

        HDAdditionalReflectionData AtlasProbe(Texture texture, ProbeSettings.Mode mode)
        {
            var obj = MakeObject("HDRP octahedral probe");
            obj.AddComponent<ReflectionProbe>();
            var hd = obj.AddComponent<HDAdditionalReflectionData>();
            hd.mode = mode;
            hd.influenceVolume.boxSize = Vector3.one * 4;
            hd.SetTexture(mode, texture);
            hd.PrepareCulling();
            return hd;
        }

        [UnityTest]
        public IEnumerator HDReflectionAtlasExistingRealtimeCubeUpdatesAndRecoversLostAtlas()
        {
            var cube = Own(new RenderTexture(32, 32, 0, RenderTextureFormat.ARGBHalf)
            { dimension = TextureDimension.Cube, useMipMap = true, autoGenerateMips = false });
            cube.Create();
            var probe = AtlasProbe(cube, ProbeSettings.Mode.Realtime);
            var sample = Own(new Material(Shader.Find("Hidden/HDRP/Tests/MobileReflectionAtlas")));
            yield return null;
            foreach (var color in new[] { Color.red, Color.blue })
            {
                var cmd = CommandBufferPool.Get("Update external realtime cube");
                try
                {
                    for (int f = 0; f < 6; ++f)
                    {
                        cmd.SetRenderTarget(cube, 0, (CubemapFace)f);
                        cmd.ClearRenderTarget(false, true, color);
                    }
                    cmd.GenerateMips(cube);
                    Graphics.ExecuteCommandBuffer(cmd);
                }
                finally { CommandBufferPool.Release(cmd); }
                Capture();
                Assert.AreEqual(1, Shader.GetGlobalInt("_MobileReflectionCount16"));
                var actual = SampleAtlas(sample, 0, Vector3.forward, 2);
                Assert.That(actual.r, Is.EqualTo(color.r).Within(.025f));
                Assert.That(actual.b, Is.EqualTo(color.b).Within(.025f));
            }
            var lost = (RenderTexture)Shader.GetGlobalTexture("_MobileReflectionAtlas");
            lost.Release();
            Capture();
            Assert.Greater(SampleAtlas(sample, 0, Vector3.forward, 2).b, .95f);
            m_Asset.mobileSettings.reflectionAtlasSize = 1024;
            Capture();
            Assert.AreEqual(1024, Shader.GetGlobalTexture("_MobileReflectionAtlas").width);
            Assert.Greater(SampleAtlas(sample, 0, Vector3.forward, 6).b, .95f);
        }

        [UnityTest]
        public IEnumerator HDReflectionAtlasCustomSupportsForwardDeferredAndInvalidation()
        {
            m_Sun.enabled = false;
            var material = Material(true, Color.white);
            material.SetFloat("_Metallic", 1); material.SetFloat("_Smoothness", .8f);
            Quad(material);
            var red = ReflectionCube(Color.red);
            var hd = AtlasProbe(red, ProbeSettings.Mode.Custom);
            m_Asset.mobileSettings.reflectionAtlasSize = 1024;
            yield return null;
            var forward = Center(Capture());
            Assert.Greater(Shader.GetGlobalInt("_MobileReflectionCount16"), 0, "Custom HDRP probe must enter the atlas without a legacy texture assignment.");
            var atlas = Shader.GetGlobalTexture("_MobileReflectionAtlas");
            Assert.AreEqual(TextureDimension.Tex2DArray, atlas.dimension);
            Assert.AreEqual(1024, atlas.width);
            Assert.Greater(forward.r, forward.g + .1f);
            m_Asset.mobileSettings.renderingPath = MobileRenderingPath.Deferred;
            m_Asset.mobileSettings.deferredExecution = MobileDeferredExecution.Sampled;
            var deferred = Center(Capture());
            Assert.That(deferred.r, Is.EqualTo(forward.r).Within(.02f));
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Vulkan)
            {
                m_Asset.mobileSettings.deferredExecution = MobileDeferredExecution.NativeRenderPass;
                Assert.That(Center(Capture()).r, Is.EqualTo(forward.r).Within(.02f));
            }
            hd.SetTexture(ProbeSettings.Mode.Custom, ReflectionCube(Color.green));
            var replaced = Center(Capture());
            Assert.Greater(replaced.g, replaced.r + .1f);
            hd.multiplier = .25f;
            var dimmed = Center(Capture());
            Assert.Less(dimmed.g, replaced.g * .5f);
            hd.weight = 0;
            Assert.Less(Center(Capture()).maxColorComponent, .02f, "Zero weight must not fall back to a native cubemap.");
            hd.gameObject.SetActive(false);
            yield return null;
            var removed = Center(Capture());
            Assert.AreEqual(0, Shader.GetGlobalInt("_MobileReflectionCount16"));
            Assert.Less(removed.maxColorComponent, .02f);
        }

        [UnityTest]
        public IEnumerator HDReflectionAtlasRotatedInfluenceAndSphereBoundary()
        {
            m_Sun.enabled = false;
            var material = Material(true, Color.white);
            material.SetFloat("_Metallic", 1); material.SetFloat("_Smoothness", .8f);
            Quad(material);
            var hd = AtlasProbe(ReflectionCube(Color.red), ProbeSettings.Mode.Baked);
            hd.transform.position = new Vector3(1, 0, 0);
            hd.influenceVolume.boxSize = new Vector3(.5f, 4, 4);
            yield return null;
            var outside = Center(Capture());
            hd.transform.rotation = Quaternion.Euler(0, 0, 90);
            var inside = Center(Capture());
            Assert.Greater(inside.r, outside.r + .1f, "Rotated influence is evaluated in probe space.");
            hd.influenceVolume.shape = InfluenceShape.Sphere;
            hd.influenceVolume.sphereRadius = .5f;
            var sphereOutside = Center(Capture());
            hd.influenceVolume.sphereRadius = 2;
            var sphereInside = Center(Capture());
            Assert.Greater(sphereInside.r, sphereOutside.r + .1f);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void HDReflectionNestedDarkProbeOverridesBrightOuterProbe(bool deferred)
        {
            m_Sun.enabled = false;
            m_Asset.mobileSettings.renderingPath = deferred ? MobileRenderingPath.Deferred : MobileRenderingPath.Forward;
            m_Asset.mobileSettings.deferredExecution = MobileDeferredExecution.Sampled;
            var material = Material(true, Color.white);
            material.SetFloat("_Metallic", 1); material.SetFloat("_Smoothness", .8f);
            Quad(material);
            var inner = AtlasProbe(ReflectionCube(Color.white * 4), ProbeSettings.Mode.Custom);
            inner.influenceVolume.boxSize = Vector3.one * 4;
            var baseline = Center(Capture()).r;
            Assert.Greater(baseline, .1f);

            var outer = AtlasProbe(ReflectionCube(Color.white * 1024), ProbeSettings.Mode.Custom);
            outer.influenceVolume.boxSize = Vector3.one * 20;
            outer.PrepareCulling();
            // Desktop HDRP sorts by influence volume, not the legacy importance field.
            foreach (int importance in new[] { 1, 100 })
            {
                outer.GetComponent<ReflectionProbe>().importance = importance;
                Assert.That(Center(Capture()).r, Is.EqualTo(baseline).Within(baseline * .03f),
                    "A fully weighted dark interior probe must exclude the bright exterior probe, importance=" + importance);
            }

            var middle = AtlasProbe(ReflectionCube(Color.white * 16), ProbeSettings.Mode.Custom);
            middle.influenceVolume.boxSize = Vector3.one * 8;
            middle.PrepareCulling();
            inner.weight = .25f; middle.weight = .5f;
            // Desktop consumes weights additively: .25 inner + .5 middle + .25 outer.
            float expected = baseline * (.25f * 4 + .5f * 16 + .25f * 1024) / 4;
            Assert.That(Center(Capture()).r, Is.EqualTo(expected).Within(expected * .03f),
                "Only the unused hierarchy weight may reach larger probes.");
            inner.weight = 1;
            inner.SetTexture(ProbeSettings.Mode.Custom, ReflectionCube(Color.black));
            Assert.Less(Center(Capture()).r, .001f, "A black interior probe must still occlude exterior reflection.");
        }
    }
}
