using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace UnityEditor.Rendering.HighDefinition.Tests
{
    public class MobileEnvironmentTests
    {
        MobileEnvironmentCache cache;
        GameObject a, b;
        Material material;
        CommandBuffer cmd;
        MobileRenderSettings settings;
        [SetUp] public void Setup()
        {
            Assert.AreNotEqual(GraphicsDeviceType.Null, SystemInfo.graphicsDeviceType);
            cache = new MobileEnvironmentCache(); cmd = new CommandBuffer();
            a = new GameObject("Environment A", typeof(Camera)); b = new GameObject("Environment B", typeof(Camera));
            a.GetComponent<Camera>().enabled = b.GetComponent<Camera>().enabled = false;
            material = new Material(Shader.Find("Hidden/HDRP/Mobile/Sky"));
            settings = new MobileRenderSettings { environmentLighting = true, environmentJobsPerFrame = 1 };
        }
        [TearDown] public void Cleanup()
        {
            cache.Dispose(); cmd.Release(); Object.DestroyImmediate(material); Object.DestroyImmediate(a); Object.DestroyImmediate(b);
        }
        static MobileSky Sky(Color color) => new MobileSky { mode = 2, top = color, middle = color, bottom = color, intensity = 1, diffusion = 1, tint = Color.white };
        MobileEnvironmentCache.State Update(MobileSky sky, GameObject camera = null)
        {
            var state = cache.Prepare(cmd, (camera != null ? camera : a).GetComponent<Camera>(), sky, Vector4.zero, Vector4.zero, settings, material);
            Graphics.ExecuteCommandBuffer(cmd); cmd.Clear(); return state;
        }
        internal static Color Pixel(RenderTexture texture, int face = 0, int mip = 0)
        {
            var active = RenderTexture.active; var read = new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true);
            int width = Mathf.Max(1, texture.width >> mip);
            var copy = RenderTexture.GetTemporary(width, width, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
            try
            {
                Graphics.CopyTexture(texture, face, mip, copy, 0, 0);
                RenderTexture.active = copy;
                int center = width / 2;
                read.ReadPixels(new Rect(center, center, 1, 1), 0, 0); read.Apply(); return read.GetPixel(0, 0) * 1024;
            }
            finally { RenderTexture.active = active; RenderTexture.ReleaseTemporary(copy); Object.DestroyImmediate(read); }
        }
        static void Near(Color expected, Color actual, float tolerance = 0.015f)
        { for (int i = 0; i < 3; i++) Assert.That(actual[i], Is.EqualTo(expected[i]).Within(tolerance * Mathf.Max(1, expected[i]))); }
        [TestCase(MobileEnvironmentResolution.Low)]
        [TestCase(MobileEnvironmentResolution.High)]
        public void UniformSkyPreservesEnergyAcrossEveryFaceAndGGXMip(MobileEnvironmentResolution resolution)
        {
            settings.environmentResolution = resolution;
            var color = new Color(3, 1.5f, 0.25f); var state = Update(Sky(color));
            Assert.IsTrue(state.ready);
            for (int face = 0; face < 6; face++)
            {
                Near(color, Pixel(state.diffuse, face));
                for (int mip = 0; mip < state.mips; mip++) Near(color, Pixel(state.reflection, face, mip));
            }
            Assert.IsFalse(ShaderUtil.ShaderHasError(material.shader));
        }
        [Test]
        public void CapturePreservesCubemapOrientationAndSkyRotation()
        {
            var cube = new Cubemap(8, TextureFormat.RGBAHalf, false);
            var colors = new[] { Color.red, Color.green, Color.blue, Color.yellow, Color.magenta, Color.cyan };
            try
            {
                for (int f = 0; f < 6; f++) { var pixels = new Color[64]; for (int i = 0; i < 64; i++) pixels[i] = colors[f]; cube.SetPixels(pixels, (CubemapFace)f); }
                cube.Apply();
                var sky = new MobileSky { mode = 1, cube = cube, tint = Color.white, intensity = 1 };
                var state = Update(sky);
                for (int face = 0; face < 6; face++) Near(colors[face], Pixel(state.reflection, face));
                sky.rotation = Mathf.PI * 0.5f; settings.environmentJobsPerFrame = 48;
                state = Update(sky); Near(Color.green, Pixel(state.reflection, 4));
            }
            finally { Object.DestroyImmediate(cube); }
        }
        [Test]
        public void RoughGGXSpreadsDirectionalSkyEnergy()
        {
            var cube = new Cubemap(16, TextureFormat.RGBAHalf, false);
            try
            {
                for (int f = 0; f < 6; f++)
                {
                    var pixels = new Color[256];
                    for (int i = 0; i < pixels.Length; i++) pixels[i] = f == 0 ? Color.white : Color.black;
                    cube.SetPixels(pixels, (CubemapFace)f);
                }
                cube.Apply();
                var state = Update(new MobileSky { mode = 1, cube = cube, tint = Color.white, intensity = 1 });
                Assert.Greater(Pixel(state.reflection, 0, 0).r, 0.95f);
                Assert.Less(Pixel(state.reflection, 2, 0).r, 0.01f);
                Assert.Less(Pixel(state.reflection, 0, state.mips - 1).r, 0.8f);
                string values = "";
                for (int f = 0; f < 6; f++) for (int m = 0; m < state.mips; m++) values += f + "/" + m + "=" + Pixel(state.reflection, f, m).r + ";";
                Assert.Greater(Pixel(state.reflection, 2, state.mips - 1).r, 0.02f, values);
            }
            finally { Object.DestroyImmediate(cube); }
        }
        [Test]
        public void PartialUpdatesPublishOnlyCompleteSnapshotsAndDoNotStarve()
        {
            var state = Update(Sky(Color.blue)); var original = state.reflection;
            Update(Sky(Color.red));
            Assert.AreSame(original, state.reflection); Assert.AreEqual(1, state.generation);
            for (int i = 1; i < state.totalJobs; i++) Update(Sky(Color.green));
            Assert.AreEqual(2, state.generation); Near(Color.red, Pixel(state.reflection));
            settings.environmentJobsPerFrame = 48; Update(Sky(Color.green));
            Near(Color.green, Pixel(state.reflection));
            int generation = state.generation; Update(Sky(Color.green)); Assert.AreEqual(generation, state.generation);
        }
        [Test]
        public void CamerasOwnIndependentCachesAndReleaseOnDisableOrDestruction()
        {
            var first = Update(Sky(Color.red)); var second = Update(Sky(Color.green), b);
            Assert.AreNotSame(first.reflection, second.reflection);
            Near(Color.red, Pixel(first.reflection)); Near(Color.green, Pixel(second.reflection));
            settings.environmentLighting = false; Assert.IsNull(Update(Sky(Color.red))); Assert.IsTrue(first.reflection == null);
            Object.DestroyImmediate(b); cache.Prune(); Assert.IsTrue(second.reflection == null);
        }
    }
}
