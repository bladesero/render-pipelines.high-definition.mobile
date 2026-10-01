using NUnit.Framework;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace UnityEditor.Rendering.HighDefinition.Tests
{
    public partial class MobileImageTests
    {
        [TestCase(MobileUpscalingMode.SGSR2Lite, false, MobileRenderingPath.Forward)]
        [TestCase(MobileUpscalingMode.SGSR2Lite, true, MobileRenderingPath.Forward)]
        [TestCase(MobileUpscalingMode.SGSR2Ultra, false, MobileRenderingPath.Forward)]
        [TestCase(MobileUpscalingMode.SGSR2Ultra, true, MobileRenderingPath.Forward)]
        [TestCase(MobileUpscalingMode.SGSR2Lite, false, MobileRenderingPath.Deferred)]
        [TestCase(MobileUpscalingMode.SGSR2Ultra, false, MobileRenderingPath.Deferred)]
        public void MobileSgsrReconstructsColorAndRestoresCamera(MobileUpscalingMode mode, bool half, MobileRenderingPath path)
        {
            m_Camera.orthographic = false;
            m_Asset.mobileSettings.renderingPath = path;
            m_Asset.mobileSettings.renderScale = 0.5f;
            m_Asset.mobileSettings.upscalingMode = mode;
            m_Asset.mobileSettings.sgsr2HalfPrecision = half;
            Assert.IsNotNull(m_Asset.mobileResources.sgsr2Shader);
            var color = new Color(0.2f, 0.4f, 0.6f);
            Quad(Material(false, color)).transform.localScale = Vector3.one * 10;
            var projection = m_Camera.projectionMatrix;
            for (int i = 0; i < 3; i++)
            {
                var pixels = Capture();
                var center = pixels[(Height / 2) * Width + Width / 2];
                for (int c = 0; c < 3; c++) Assert.That(center[c], Is.EqualTo(color[c]).Within(0.035f));
                Assert.AreEqual(projection, m_Camera.projectionMatrix);
                var backend = typeof(HDRenderPipeline).GetField("m_MobileBackend",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(RenderPipelineManager.currentPipeline);
                var histories = (System.Collections.IDictionary)typeof(MobileRenderPipelineBackend).GetField("m_SgsrHistory",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(backend);
                Assert.IsTrue(histories.Contains(m_Camera), "SGSR2 must run rather than silently fall back. Shader=" + m_Asset.mobileResources.sgsr2Shader.isSupported + ", guides Render=" + SystemInfo.IsFormatSupported(UnityEngine.Experimental.Rendering.GraphicsFormat.R32G32B32A32_UInt, UnityEngine.Experimental.Rendering.FormatUsage.Render) + ", Sample=" + SystemInfo.IsFormatSupported(UnityEngine.Experimental.Rendering.GraphicsFormat.R32G32B32A32_UInt, UnityEngine.Experimental.Rendering.FormatUsage.Sample));
                var state = histories[m_Camera];
                int frame = (int)state.GetType().GetField("frame",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(state);
                Assert.AreEqual(i + 1, frame, "History must advance across repeated camera renders.");
            }
            m_Asset.mobileSettings.upscalingMode = MobileUpscalingMode.Bilinear;
            Capture();
            Assert.AreEqual(projection, m_Camera.projectionMatrix);
        }
        [TestCase(MobileUpscalingMode.SGSR2Lite)]
        [TestCase(MobileUpscalingMode.SGSR2Ultra)]
        public void MobileSgsrPreservesOrientationAfterMotionAndResize(MobileUpscalingMode mode)
        {
            m_Camera.orthographic = false;
            m_Asset.mobileSettings.renderScale = 0.5f;
            var texture = Own(new Texture2D(2, 2, TextureFormat.RGBAFloat, false, true));
            texture.filterMode = FilterMode.Point;
            texture.SetPixels(new[] { new Color(0.6f, 0.1f, 0.1f), new Color(0.1f, 0.6f, 0.1f),
                new Color(0.1f, 0.1f, 0.6f), new Color(0.6f, 0.6f, 0.1f) });
            texture.Apply();
            var material = Material(false, Color.white);
            material.SetTexture("_UnlitColorMap", texture);
            HDShaderUtils.ResetMaterialKeywords(material);
            Quad(material).transform.localScale = Vector3.one * 10;
            var reference = Capture();
            m_Asset.mobileSettings.upscalingMode = mode;
            for (int i = 0; i < 6; i++) Capture();
            var reconstructed = Capture();
            foreach (int y in new[] { Height / 4, Height * 3 / 4 })
            foreach (int x in new[] { Width / 4, Width * 3 / 4 })
            for (int c = 0; c < 3; c++)
                Assert.That(reconstructed[y * Width + x][c], Is.EqualTo(reference[y * Width + x][c]).Within(0.04f),
                    $"Output orientation / history mismatch at {x},{y}, channel {c}");
            m_Camera.transform.position += new Vector3(0.1f, 0.1f, 0);
            Capture();
            m_Camera.targetTexture = Own(new RenderTexture(96, 80, 24, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear));
            m_Camera.targetTexture.Create();
            Assert.AreEqual(96 * 80, Capture().Length);
            m_Asset.mobileSettings.renderScale = 1f;
            Capture();
        }
        [TestCase(MobileUpscalingMode.SGSR2Lite, false)]
        [TestCase(MobileUpscalingMode.SGSR2Lite, true)]
        [TestCase(MobileUpscalingMode.SGSR2Ultra, false)]
        [TestCase(MobileUpscalingMode.SGSR2Ultra, true)]
        public void MobileSgsrRejectsAnimatedColorWithoutCameraMotion(MobileUpscalingMode mode, bool half)
        {
            m_Camera.orthographic = false;
            m_Asset.mobileSettings.renderScale = 0.5f;
            m_Asset.mobileSettings.upscalingMode = mode;
            m_Asset.mobileSettings.sgsr2HalfPrecision = half;
            var material = Material(false, new Color(0.1f, 0.1f, 0.8f));
            Quad(material).transform.localScale = Vector3.one * 10;
            for (int i = 0; i < 20; i++) Capture();
            var changed = new Color(0.8f, 0.1f, 0.1f);
            material.SetColor("_UnlitColor", changed.gamma);
            var pixels = Capture();
            var center = pixels[(Height / 2) * Width + Width / 2];
            for (int c = 0; c < 3; c++)
                Assert.That(center[c], Is.EqualTo(changed[c]).Within(0.035f), "First changed frame must not retain the old surface color.");
        }

        [TestCase(MobileUpscalingMode.SGSR2Lite, false, MobileRenderingPath.Forward)]
        [TestCase(MobileUpscalingMode.SGSR2Ultra, false, MobileRenderingPath.Forward)]
        [TestCase(MobileUpscalingMode.SGSR2Lite, true, MobileRenderingPath.Forward)]
        [TestCase(MobileUpscalingMode.SGSR2Ultra, true, MobileRenderingPath.Forward)]
        [TestCase(MobileUpscalingMode.SGSR2Lite, false, MobileRenderingPath.Deferred)]
        [TestCase(MobileUpscalingMode.SGSR2Ultra, false, MobileRenderingPath.Deferred)]
        [TestCase(MobileUpscalingMode.SGSR2Lite, false, MobileRenderingPath.Forward, 0.5f)]
        [TestCase(MobileUpscalingMode.SGSR2Ultra, false, MobileRenderingPath.Forward, 0.5f)]
        public void MobileSgsrClearsMovingObjectTrail(MobileUpscalingMode mode, bool movingCamera, MobileRenderingPath path, float verticalOffset = 0)
        {
            m_Camera.orthographic = false;
            m_Asset.mobileSettings.renderScale = 0.5f;
            m_Asset.mobileSettings.upscalingMode = mode;
            m_Asset.mobileSettings.renderingPath = path;
            var moving = Quad(Material(false, new Color(0.9f, 0.2f, 0.1f)));
            moving.transform.localScale = Vector3.one * 0.4f;
            moving.transform.position += Vector3.up * verticalOffset;
            for (int i = 0; i < 20; i++) Capture();
            var old = Capture();
            moving.transform.position += new Vector3(0.15f, 0, 0);
            if (movingCamera) m_Camera.transform.position += new Vector3(0.015f, 0, 0);
            var moved = Capture();
            m_Asset.mobileSettings.upscalingMode = MobileUpscalingMode.Bilinear;
            var reference = Capture();
            int vacated = 0;
            for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                int p = y * Width + x;
                if (old[p].r > 0.5f && reference[p].r < 0.02f)
                {
                    vacated++;
                    Assert.Less(moved[p].r, 0.08f, $"Moving object left a trail at {x},{y}");
                }
            }
            Assert.Greater(vacated, 0, "Test must cover vacated foreground pixels.");
        }
        [TestCase(MobileUpscalingMode.SGSR2Lite)]
        [TestCase(MobileUpscalingMode.SGSR2Ultra)]
        public void MobileSgsrRejectsTransparentColorChanges(MobileUpscalingMode mode)
        {
            m_Camera.orthographic = false;
            m_Asset.mobileSettings.renderScale = 0.5f;
            m_Asset.mobileSettings.upscalingMode = mode;
            var material = Material(false, new Color(0.1f, 0.1f, 0.8f, 0.5f));
            material.SetFloat("_SurfaceType", 1); material.SetFloat("_BlendMode", 0);
            material.SetFloat("_EnableBlendModePreserveSpecularLighting", 0);
            HDShaderUtils.ResetMaterialKeywords(material);
            Quad(material).transform.localScale = Vector3.one * 10;
            for (int i = 0; i < 20; i++) Capture();
            material.SetColor("_UnlitColor", new Color(0.8f, 0.1f, 0.1f, 0.5f).gamma);
            var changed = Capture();
            m_Asset.mobileSettings.upscalingMode = MobileUpscalingMode.Bilinear;
            var reference = Capture();
            int center = (Height / 2) * Width + Width / 2;
            Assert.Greater(reference[center].r, 0.1f, "The transparent surface must be visible.");
            for (int c = 0; c < 3; c++)
                Assert.That(changed[center][c], Is.EqualTo(reference[center][c]).Within(0.035f),
                    "Transparent changes must reject history even when depth is unchanged.");
        }
        [TestCase(MobileUpscalingMode.SGSR2Lite, false, 0.5f)]
        [TestCase(MobileUpscalingMode.SGSR2Lite, true, 0.5f)]
        [TestCase(MobileUpscalingMode.SGSR2Ultra, false, 0.5f)]
        [TestCase(MobileUpscalingMode.SGSR2Ultra, true, 0.5f)]
        [TestCase(MobileUpscalingMode.SGSR2Lite, false, 0.75f)]
        [TestCase(MobileUpscalingMode.SGSR2Ultra, false, 0.75f)]
        [TestCase(MobileUpscalingMode.SGSR2Lite, false, 0.5f, 1.5f)]
        [TestCase(MobileUpscalingMode.SGSR2Ultra, false, 0.5f, 1.5f)]
        [TestCase(MobileUpscalingMode.SGSR2Lite, false, 0.5f, 0.055f, true)]
        [TestCase(MobileUpscalingMode.SGSR2Ultra, false, 0.5f, 0.055f, true)]
        [TestCase(MobileUpscalingMode.SGSR2Lite, false, 0.5f, 2.4f, false, true)]
        [TestCase(MobileUpscalingMode.SGSR2Ultra, false, 0.5f, 2.4f, false, true)]
        [TestCase(MobileUpscalingMode.SGSR2Lite, false, 0.75f, 2.4f, false, true)]
        [TestCase(MobileUpscalingMode.SGSR2Ultra, false, 0.75f, 2.4f, false, true)]
        public void MobileSgsrStaticThinEdgesRemainStable(MobileUpscalingMode mode, bool half, float scale, float lineWidth = 0.055f, bool skinned = false, bool textured = false)
        {
            m_Camera.orthographic = false;
            m_Asset.mobileSettings.renderScale = scale;
            m_Asset.mobileSettings.upscalingMode = mode;
            m_Asset.mobileSettings.sgsr2HalfPrecision = half;
            var surface = Material(false, Color.white);
            if (textured)
            {
                var texture = Own(new Texture2D(64, 64, TextureFormat.RGBA32, false, true));
                texture.filterMode = FilterMode.Bilinear;
                var texels = new Color[64 * 64];
                for (int y = 0; y < 64; y++)
                for (int x = 0; x < 64; x++)
                    texels[y * 64 + x] = ((x / 4 + y / 4) & 1) == 0 ? Color.white : Color.black;
                texture.SetPixels(texels); texture.Apply();
                surface.SetTexture("_UnlitColorMap", texture);
                HDShaderUtils.ResetMaterialKeywords(surface);
            }
            var line = Quad(surface);
            line.transform.localScale = new Vector3(lineWidth, 2.4f, 1);
            line.transform.rotation = Quaternion.Euler(0, 0, 23);
            if (skinned)
            {
                var mesh = line.GetComponent<MeshFilter>().sharedMesh;
                var material = line.sharedMaterial;
                var skin = line.gameObject.AddComponent<SkinnedMeshRenderer>();
                skin.sharedMesh = mesh;
                skin.sharedMaterial = material;
            }
            for (int i = 0; i < 64; i++) Capture();
            var previous = Capture();
            float visiblePeak = 0;
            foreach (var pixel in previous) visiblePeak = Mathf.Max(visiblePeak, pixel.r);
            Assert.Greater(visiblePeak, 0.1f, "The static geometry must actually be rendered.");
            double sum = 0;
            float peak = 0;
            int samples = 0;
            for (int frame = 0; frame < 32; frame++)
            {
                var current = Capture();
                for (int y = 10; y < Height - 10; y++)
                for (int x = Width / 3; x < Width * 2 / 3; x++)
                {
                    int p = y * Width + x;
                    float delta = Mathf.Abs(current[p].r - previous[p].r);
                    sum += delta; peak = Mathf.Max(peak, delta); samples++;
                }
                previous = current;
            }
            Debug.Log($"SGSR stability {mode} half={half} scale={scale} width={lineWidth} skin={skinned} texture={textured}: mean={sum / samples:F6}, peak={peak:F6}");
            Assert.Less(peak, 0.15f, "Stationary subpixel edges must not jump between bright and dark phases.");
            Assert.Less(sum / samples, 0.015, "Stationary scene must converge across a full Halton cycle.");
        }
    }

    public class MobileSGSR2ShaderTests
    {
        [TestCase(ShaderCompilerPlatform.D3D)]
        [TestCase(ShaderCompilerPlatform.Vulkan)]
        public void MobileSgsrVariantsCompile(ShaderCompilerPlatform platform)
        {
            var shader = Shader.Find("Hidden/HDRP/Mobile/SGSR2");
            Assert.IsNotNull(shader);
            var subshader = ShaderUtil.GetShaderData(shader).GetSubshader(0);
            foreach (bool ultra in new[] { false, true })
            foreach (bool half in new[] { false, true })
            foreach (bool still in new[] { false, true })
            {
                var keywords = new System.Collections.Generic.List<string>();
                keywords.Add(ultra ? "SGSR_ULTRA" : "SGSR_LITE");
                if (half) keywords.Add("SGSR_FP16");
                if (still) keywords.Add("SGSR_STILL");
                foreach (int pass in ultra ? new[] { 0, 2, 3, 4 } : new[] { 0, 3, 4 })
                foreach (var stage in new[] { ShaderType.Vertex, ShaderType.Fragment })
                {
                    var result = subshader.GetPass(pass).CompileVariant(stage, keywords.ToArray(), platform,
                        platform == ShaderCompilerPlatform.Vulkan ? BuildTarget.Android : BuildTarget.StandaloneWindows64, true);
                    Assert.IsTrue(result.Success, $"{platform}, ultra={ultra}, half={half}, still={still}, pass={pass}: " +
                        string.Join("\n", result.Messages));
                    Assert.Greater(result.ShaderData.Length, 0);
                }
            }
        }
    }
}
