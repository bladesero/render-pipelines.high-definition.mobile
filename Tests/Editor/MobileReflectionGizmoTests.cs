using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityEditor.Rendering.HighDefinition.Tests
{
    public partial class MobileImageTests
    {
        [Test]
        public void MobileInspectorProbePreviewStartsAtDesktopExposure()
        {
            var cube = ReflectionCube(Color.red);
            var probe = AtlasProbe(cube, UnityEngine.Rendering.HighDefinition.ProbeSettings.Mode.Custom);
            var editor = Own(Editor.CreateEditor(probe.GetComponent<ReflectionProbe>(), typeof(HDReflectionProbeEditor)));
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            float mobileExposure = (float)typeof(HDReflectionProbeEditor).GetField("m_MobilePreviewExposure", flags).GetValue(editor);
            var desktopEditor = Own(Editor.CreateEditor(cube));
            System.Reflection.FieldInfo exposureField = null;
            for (var type = desktopEditor.GetType(); type != null && exposureField == null; type = type.BaseType)
                exposureField = type.GetField("m_ExposureSliderValue", flags);
            Assert.IsNotNull(exposureField, "Desktop texture inspector exposure must be available for the parity check.");
            Assert.AreEqual((float)exposureField.GetValue(desktopEditor), mobileExposure,
                "Switching renderer must not introduce a preview exposure offset.");
        }

        [Test]
        public void MobileInspectorProbePreviewRendersHdrWithoutPipelineCamera()
        {
            var cube = ReflectionCube(Color.red * 4096);
            var preview = new MobileReflectionProbePreview();
            var read = Own(new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true));
            var previous = RenderTexture.active;
            try
            {
                foreach (float exposure in new[] { 0f, -12f, -13f })
                {
                    var target = preview.Render(cube, 128, 96, Quaternion.identity, 0, exposure);
                    RenderTexture.active = target;
                    read.ReadPixels(new Rect(64, 48, 1, 1), 0, 0); read.Apply();
                    float expected = Mathf.Pow(2, exposure + 12);
                    Assert.That(read.GetPixel(0, 0).r, Is.EqualTo(expected).Within(expected * .02f));
                }
                cube.SetPixels(new Color[32 * 32], CubemapFace.NegativeZ); cube.Apply();
                var black = preview.Render(cube, 64, 64, Quaternion.identity, 0, -12);
                RenderTexture.active = black;
                read.ReadPixels(new Rect(32, 32, 1, 1), 0, 0); read.Apply();
                Assert.Less(read.GetPixel(0, 0).r, .01f);
                var rotated = preview.Render(cube, 64, 64, Quaternion.Euler(0, 180, 0), 2, -12);
                RenderTexture.active = rotated;
                read.ReadPixels(new Rect(32, 32, 1, 1), 0, 0); read.Apply();
                Assert.Greater(read.GetPixel(0, 0).r, .9f, "Inspector drag rotation must expose another cubemap face.");
            }
            finally { RenderTexture.active = previous; preview.Dispose(); }
        }

        [Test]
        public void MobileReflectionGizmoTracksWorldPositionAndRespectsDepth()
        {
            var material = Own(new Material(Shader.Find("Hidden/Debug/ReflectionProbePreview")));
            material.SetTexture("_Cubemap", ReflectionCube(Color.red * 4096));
            material.SetFloat("_MobileExposure", 1f / 4096);
            material.SetFloat("_MobileAutomaticExposure", 0);
            int pass = material.FindPass("MobileReflectionProbePreview");
            Assert.GreaterOrEqual(pass, 0);
            ShaderUtil.CompilePass(material, pass, true);
            var sphere = Resources.GetBuiltinResource<Mesh>("New-Sphere.fbx");
            var target = m_Camera.targetTexture;
            Vector2 Draw(Vector3 position, bool occluded)
            {
                material.SetMatrix("_MobileProbePreviewVP", GL.GetGPUProjectionMatrix(m_Camera.projectionMatrix, true) * m_Camera.worldToCameraMatrix);
                material.SetVector("_MobileProbePreviewCamera", m_Camera.transform.position);
                var cmd = CommandBufferPool.Get("Mobile reflection gizmo oracle");
                var read = new Texture2D(Width, Height, TextureFormat.RGBAFloat, false, true);
                var previous = RenderTexture.active;
                try
                {
                    cmd.SetRenderTarget(target);
                    cmd.SetViewport(new Rect(0, 0, Width, Height));
                    cmd.ClearRenderTarget(true, true, Color.black, occluded ? 0 : 1);
                    cmd.DrawMesh(sphere, Matrix4x4.TRS(position, Quaternion.identity, Vector3.one * .5f), material, 0, pass);
                    Graphics.ExecuteCommandBuffer(cmd);
                    RenderTexture.active = target;
                    read.ReadPixels(new Rect(0, 0, Width, Height), 0, 0); read.Apply();
                    var pixels = read.GetPixels();
                    int count = 0; Vector2 center = Vector2.zero;
                    for (int y = 0; y < Height; ++y)
                        for (int x = 0; x < Width; ++x)
                            if (pixels[y * Width + x].r > .5f)
                            {
                                Assert.That(pixels[y * Width + x].r, Is.EqualTo(1).Within(.02f), "Physical HDR probe preview must use camera exposure.");
                                ++count; center += new Vector2(x + .5f, y + .5f);
                            }
                    if (occluded) Assert.AreEqual(0, count, "Probe sphere must obey scene depth.");
                    else Assert.Greater(count, 20, "Probe sphere must render in the current camera.");
                    return count > 0 ? center / count : Vector2.zero;
                }
                finally { RenderTexture.active = previous; UnityEngine.Object.DestroyImmediate(read); CommandBufferPool.Release(cmd); }
            }
            var original = Draw(Vector3.zero, false);
            var movedProbe = Draw(new Vector3(.5f, 0, 0), false);
            Assert.That(movedProbe.x - original.x, Is.EqualTo(24).Within(2), "Moving probe must move the gizmo in world space.");
            m_Camera.transform.position += Vector3.right * .5f;
            var movedCamera = Draw(Vector3.zero, false);
            Assert.That(movedCamera.x - original.x, Is.EqualTo(-24).Within(2), "Moving camera must not leave the gizmo fixed to the screen.");
            Draw(Vector3.zero, true);
            var exposureTexture = Own(new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true));
            exposureTexture.SetPixel(0, 0, Color.white / 4096); exposureTexture.Apply();
            material.SetTexture("_MobileExposureTexture", exposureTexture);
            material.SetFloat("_MobileAutomaticExposure", 1);
            material.SetFloat("_MobileExposure", 1);
            Draw(Vector3.zero, false);
        }
    }
}
