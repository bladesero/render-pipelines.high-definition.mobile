using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace UnityEditor.Rendering.HighDefinition
{
    sealed partial class HDReflectionProbeEditor
    {
        Editor m_CubemapEditor;
        MobileReflectionProbePreview m_MobilePreview;
        Vector2 m_MobilePreviewAngles;
        float m_MobilePreviewExposure = -10, m_MobilePreviewMip;
        static bool useMobilePreview => GraphicsSettings.currentRenderPipeline is HDRenderPipelineAsset asset && asset.rendererMode == HDRendererMode.Mobile;

        public override bool HasPreviewGUI()
        {
            if (targets.Length > 1)
                return false;  // We only handle one preview for reflection probes

            // Ensure valid cube map editor (if possible)
            Texture texture = GetTexture(this, target);
            if (m_CubemapEditor != null && m_CubemapEditor.target as Texture != texture)
            {
                DestroyImmediate(m_CubemapEditor);
                m_CubemapEditor = null;
            }
            if (!useMobilePreview && ValidPreviewSetup() && m_CubemapEditor == null)
            {
                Editor editor = m_CubemapEditor;
                m_CubemapEditor = CreateEditor(GetTexture(this, target));
            }

            // If having one probe selected we always want preview (to prevent preview window from popping)
            return true;
        }

        public override void OnPreviewSettings()
        {
            if (useMobilePreview && ValidPreviewSetup())
            {
                GUILayout.Label("Exposure", GUILayout.Width(58));
                m_MobilePreviewExposure = GUILayout.HorizontalSlider(m_MobilePreviewExposure, -20, 20, GUILayout.Width(80));
                GUILayout.Label("Mip", GUILayout.Width(24));
                m_MobilePreviewMip = GUILayout.HorizontalSlider(m_MobilePreviewMip, 0, Mathf.Max(0, GetTexture(this, target).mipmapCount - 1), GUILayout.Width(60));
                return;
            }
            if (!ValidPreviewSetup()
                || (!useMobilePreview && m_CubemapEditor == null))
                return;

            m_CubemapEditor.OnPreviewSettings();
        }

        public override void OnPreviewGUI(Rect position, GUIStyle style)
        {
            if (!ValidPreviewSetup()
                || (!useMobilePreview && m_CubemapEditor == null))
            {
                GUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                Color prevColor = GUI.color;
                GUI.color = new Color(1, 1, 1, 0.5f);
                GUILayout.Label("There is no Texture available for the Reflection Probe. Either use Baked and bake a Texture in, use Custom and assign a Texture, or enable Realtime.");
                GUI.color = prevColor;
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                return;
            }

            Texture tex = GetTexture(this, target);
            if (useMobilePreview && tex != null && targets.Length == 1)
            {
                var evt = Event.current;
                if (evt.type == EventType.MouseDrag && position.Contains(evt.mousePosition))
                {
                    m_MobilePreviewAngles += evt.delta * .5f;
                    evt.Use(); Repaint();
                }
                if (evt.type == EventType.Repaint)
                {
                    if (m_MobilePreview == null) m_MobilePreview = new MobileReflectionProbePreview();
                    var probe = GetTarget(target);
                    var image = m_MobilePreview.Render(tex, Mathf.CeilToInt(position.width), Mathf.CeilToInt(position.height),
                        Quaternion.Euler(m_MobilePreviewAngles.y, m_MobilePreviewAngles.x, 0), m_MobilePreviewMip,
                        m_MobilePreviewExposure, probe.multiplier * Mathf.Max(probe.rangeCompressionFactor, 1e-6f));
                    GUI.DrawTexture(position, image, ScaleMode.StretchToFill, false);
                }
                return;
            }
            if (tex != null && targets.Length == 1)
                m_CubemapEditor.DrawPreview(position);
        }

        bool ValidPreviewSetup()
        {
            return GetTexture(this, target) != null;
        }

        static Texture GetTexture(HDReflectionProbeEditor e, Object target)
        {
            HDProbe probe = e.GetTarget(target);
            return probe.texture;
        }

        private void OnDestroy()
        {
            DestroyImmediate(m_CubemapEditor);
            m_MobilePreview?.Dispose();
        }
    }

    // Draw directly: Unity's built-in cubemap preview materials do not have a
    // MobileForward pass, so routing their preview camera through Mobile loses
    // the sphere. This also keeps Inspector exposure independent of Scene views.
    internal sealed class MobileReflectionProbePreview : System.IDisposable
    {
        Material m_Material;
        RenderTexture m_Target;
        internal RenderTexture Render(Texture cube, int width, int height, Quaternion rotation, float mip, float exposure, float intensity)
        {
            width = Mathf.Clamp(width, 16, 2048); height = Mathf.Clamp(height, 16, 2048);
            if (m_Material == null) m_Material = CoreUtils.CreateEngineMaterial(Shader.Find("Hidden/Debug/ReflectionProbePreview"));
            if (m_Target == null || m_Target.width != width || m_Target.height != height || !m_Target.IsCreated())
            {
                if (m_Target != null) { m_Target.Release(); CoreUtils.Destroy(m_Target); }
                m_Target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)
                { name = "Mobile Reflection Inspector Preview", hideFlags = HideFlags.HideAndDontSave };
                m_Target.Create();
            }
            var position = rotation * new Vector3(0, 0, -1.6f);
            var view = Matrix4x4.Scale(new Vector3(1, 1, -1)) * Matrix4x4.TRS(position, rotation, Vector3.one).inverse;
            var projection = Matrix4x4.Perspective(45, (float)width / height, .01f, 10);
            m_Material.SetMatrix("_MobileProbePreviewVP", GL.GetGPUProjectionMatrix(projection, true) * view);
            m_Material.SetVector("_MobileProbePreviewCamera", position);
            m_Material.SetTexture("_Cubemap", cube);
            m_Material.SetFloat("_MobileProbePreviewIntensity", intensity);
            m_Material.SetFloat("_MipLevel", mip);
            m_Material.SetFloat("_Exposure", exposure);
            m_Material.SetFloat("_MobileExposure", 1);
            m_Material.SetFloat("_MobileAutomaticExposure", 0);
            var cmd = CommandBufferPool.Get("Mobile Reflection Inspector Preview");
            var previous = RenderTexture.active;
            try
            {
                cmd.SetRenderTarget(m_Target);
                cmd.SetViewport(new Rect(0, 0, width, height));
                cmd.ClearRenderTarget(true, true, new Color(.035f, .035f, .035f, 1));
                cmd.DrawMesh(Resources.GetBuiltinResource<Mesh>("New-Sphere.fbx"), Matrix4x4.identity, m_Material, 0,
                    m_Material.FindPass("MobileReflectionProbePreview"));
                Graphics.ExecuteCommandBuffer(cmd);
            }
            finally { RenderTexture.active = previous; CommandBufferPool.Release(cmd); }
            return m_Target;
        }
        public void Dispose()
        {
            if (m_Target != null) { m_Target.Release(); CoreUtils.Destroy(m_Target); m_Target = null; }
            CoreUtils.Destroy(m_Material); m_Material = null;
        }
    }
}
