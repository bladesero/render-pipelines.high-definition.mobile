using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace UnityEditor.Rendering.HighDefinition.Tests
{
    public class MobileMeterTests
    {
        [TestCase(0.125f, 12.5f, 0f, 0f, false, false)]
        [TestCase(1000f, 18f, 2f, 10f, false, false)]
        [TestCase(0.0001f, 14f, -1f, -5f, false, false)]
        [TestCase(5f, 12.5f, 1f, 0f, true, false)]
        [TestCase(100f, 12.5f, 0f, -2f, true, true)]
        [TestCase(0.1f, 12.5f, 0f, 10f, true, true)]
        public void MeterMatchesDesktopEVAndAdaptation(float luminance, float calibration, float compensation, float previousEV, bool pattern, bool progressive)
        {
            var shader = Shader.Find("Hidden/HDRP/Mobile/Composite");
            Assert.NotNull(shader);
            var material = new Material(shader);
            var source = new Texture2D(2, 2, TextureFormat.RGBAFloat, false, true);
            var previous = new Texture2D(1, 1, TextureFormat.RGFloat, false, true);
            var read = new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true);
            var targets = new System.Collections.Generic.List<RenderTexture>();
            var cmd = new CommandBuffer();
            var active = RenderTexture.active;
            try
            {
                float scale = MobileExposure.FromEV100(previousEV, 0, 1.2f);
                previous.SetPixel(0, 0, new Color(scale, previousEV, 0)); previous.Apply();
                for (int y = 0; y < 2; ++y) for (int x = 0; x < 2; ++x) source.SetPixel(x, y, Color.white * (luminance * scale * (pattern && x == y ? 0.01f : 1f)));
                source.wrapMode = TextureWrapMode.Clamp;
                source.Apply();
                Texture input = source;
                for (int size = 64; size >= 1; size /= 2)
                {
                    var target = new RenderTexture(size, size, 0, RenderTextureFormat.RGFloat, RenderTextureReadWrite.Linear) { filterMode = FilterMode.Point };
                    target.Create(); targets.Add(target);
                    cmd.SetRenderTarget(target); cmd.SetViewport(new Rect(0, 0, size, size));
                    cmd.SetGlobalTexture("_MobileSource", input);
                    cmd.SetGlobalTexture("_MobileExposureTexture", previous);
                    cmd.SetGlobalVector("_MobileMeterParams", new Vector4(compensation, -20, 24, calibration));
                    cmd.SetGlobalVector("_MobileAdaptation", new Vector4(3, 1, 0.25f, progressive ? 1 : 0));
                    cmd.SetGlobalVector("_MobileMeterSize", new Vector4(1f / (size * 2), 1f / (size * 2), pattern ? 1 : 0, 1.2f));
                    cmd.DrawProcedural(Matrix4x4.identity, material, size == 64 ? 3 : size == 1 ? 5 : 4, MeshTopology.Triangles, 3);
                    Graphics.ExecuteCommandBuffer(cmd); cmd.Clear(); input = target;
                }
                RenderTexture.active = targets[targets.Count - 1]; read.ReadPixels(new Rect(0, 0, 1, 1), 0, 0); read.Apply();
                float sum = 0, weights = 0;
                for (int y = 0; y < 64; y++) for (int x = 0; x < 64; x++)
                {
                    var uv = new Vector2((x + 0.5f) / 64, (y + 0.5f) / 64);
                    float weight = pattern ? 1 - Mathf.Clamp01((uv - Vector2.one * 0.5f).magnitude) : 1;
                    // Match GPU texel-center coordinates explicitly; GetPixelBilinear's CPU coordinates differ.
                    float tx = Mathf.Clamp01(uv.x * 2 - 0.5f), ty = Mathf.Clamp01(uv.y * 2 - 0.5f);
                    float sampled = pattern ? luminance * Mathf.Lerp(Mathf.Lerp(0.01f, 1f, tx), Mathf.Lerp(1f, 0.01f, tx), ty) : luminance;
                    sum += Mathf.Log(Mathf.Max(sampled, 1e-4f) * 100 / calibration, 2) * weight;
                    weights += weight;
                }
                float expected = sum / weights - compensation;
                if (progressive) expected = previousEV + (expected - previousEV) * (1 - Mathf.Pow(2, -0.25f * (expected > previousEV ? 3 : 1)));
                expected = Mathf.Clamp(expected, -20, 24);
                Assert.That(read.GetPixel(0, 0).g, Is.EqualTo(expected).Within(0.05f));
                Assert.That(read.GetPixel(0, 0).r, Is.EqualTo(MobileExposure.FromEV100(expected, 0, 1.2f)).Within(Mathf.Max(1e-7f, MobileExposure.FromEV100(expected, 0, 1.2f) * 0.01f)));
            }
            finally
            {
                RenderTexture.active = active; cmd.Release();
                foreach (var rt in targets) { rt.Release(); Object.DestroyImmediate(rt); }
                Object.DestroyImmediate(material); Object.DestroyImmediate(source); Object.DestroyImmediate(previous); Object.DestroyImmediate(read);
            }
        }

        [Test]
        public void EditorRepaintGapsAndParameterEditsPreserveHistory()
        {
            Assert.IsFalse(Application.isPlaying);
            var history = new MobileExposureHistory();
            var obj = new GameObject("On-demand exposure camera");
            var e = ScriptableObject.CreateInstance<Exposure>(); e.mode.value = ExposureMode.Automatic;
            try
            {
                var camera = obj.AddComponent<Camera>(); camera.enabled = false;
                var state = history.Prepare(camera, e, true); state.initialized = true;
                state.frame = Time.frameCount - 100;
                history.Prune();
                e.compensation.value = 2; e.fixedExposure.value = -5;
                var next = history.Prepare(camera, e, true);
                Assert.AreSame(state, next); Assert.IsTrue(next.initialized);
                e.mode.value = ExposureMode.Fixed;
                Assert.IsNull(history.Prepare(camera, e, true));
            }
            finally { history.Dispose(); Object.DestroyImmediate(obj); Object.DestroyImmediate(e); }
        }

        [Test]
        public void HistoriesAreIsolatedAndReleasedOnModeChanges()
        {
            var history = new MobileExposureHistory();
            var a = new GameObject("Meter A"); var b = new GameObject("Meter B");
            var e = ScriptableObject.CreateInstance<Exposure>(); e.mode.value = ExposureMode.Automatic;
            try
            {
                var ca = a.AddComponent<Camera>(); var cb = b.AddComponent<Camera>();
                var sa = history.Prepare(ca, e, true); var sb = history.Prepare(cb, e, true);
                Assert.AreNotSame(sa.read, sb.read);
                history.Remove(ca); Assert.IsNull(history.GetTexture(ca)); Assert.IsNotNull(history.GetTexture(cb));
                Assert.IsNull(history.Prepare(cb, e, false)); Assert.IsNull(history.GetTexture(cb));
                e.mode.value = ExposureMode.Fixed; Assert.IsNull(history.Prepare(ca, e, true));
            }
            finally { history.Dispose(); Object.DestroyImmediate(a); Object.DestroyImmediate(b); Object.DestroyImmediate(e); }
        }
    }
}
