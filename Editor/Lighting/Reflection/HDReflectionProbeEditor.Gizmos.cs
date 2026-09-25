using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace UnityEditor.Rendering.HighDefinition
{
    sealed partial class HDReflectionProbeEditor
    {
        static Mesh sphere;
        static Material material;

        [DrawGizmo(GizmoType.Selected)]
        static void DrawSelectedGizmo(ReflectionProbe reflectionProbe, GizmoType gizmoType)
        {
            var e = (HDReflectionProbeEditor)GetEditorFor(reflectionProbe);
            if (e == null)
                return;

            var mat = Matrix4x4.TRS(reflectionProbe.transform.position, reflectionProbe.transform.rotation, Vector3.one);
            var hdprobe = reflectionProbe.GetComponent<HDAdditionalReflectionData>();
            InfluenceVolumeUI.DrawGizmos(
                hdprobe.influenceVolume,
                mat,
                InfluenceVolumeUI.HandleType.None,
                InfluenceVolumeUI.HandleType.Base | InfluenceVolumeUI.HandleType.Influence
            );

            if (e.showChromeGizmo)
                Gizmos_CapturePoint(reflectionProbe);
        }

        static void Gizmos_CapturePoint(ReflectionProbe target)
        {
            if (sphere == null)
                sphere = Resources.GetBuiltinResource<Mesh>("New-Sphere.fbx");
            if (material == null)
                material = new Material(Shader.Find("Hidden/Debug/ReflectionProbePreview"));
            var probe = target.GetComponent<HDAdditionalReflectionData>();
            var probePositionSettings = ProbeCapturePositionSettings.ComputeFrom(probe, null);
            HDRenderUtilities.ComputeCameraSettingsFromProbeSettings(
                probe.settings, probePositionSettings,
                out _, out var cameraPositionSettings, 0
            );
            var capturePosition = cameraPositionSettings.position;

            material.SetTexture("_Cubemap", probe.texture);
            bool mobile = GraphicsSettings.currentRenderPipeline is HDRenderPipelineAsset asset && asset.rendererMode == HDRendererMode.Mobile;
            if (mobile)
            {
                var camera = Camera.current;
                if (camera == null) return;
                // Desktop's preview shader reads camera-relative HDRP constant
                // buffers. Mobile never populates those buffers, so use the
                // current gizmo camera and an absolute world-space transform.
                material.SetMatrix("_MobileProbePreviewVP", GL.GetGPUProjectionMatrix(camera.projectionMatrix, camera.targetTexture != null) * camera.worldToCameraMatrix);
                material.SetVector("_MobileProbePreviewCamera", camera.transform.position);
                material.SetFloat("_MobileProbePreviewIntensity", probe.multiplier * Mathf.Max(probe.rangeCompressionFactor, 1e-6f));
            }
            if (!material.SetPass(mobile ? material.FindPass("MobileReflectionProbePreview") : 0)) return;
            Graphics.DrawMeshNow(sphere, Matrix4x4.TRS(capturePosition, Quaternion.identity, Vector3.one * capturePointPreviewSize));

            var ray = new Ray(capturePosition, Vector3.down);
            if (Physics.Raycast(ray, out RaycastHit hit))
            {
                var startPoint = capturePosition - Vector3.up * 0.5f * capturePointPreviewSize;
                var c = InfluenceVolumeUI.k_GizmoThemeColorBase;
                c.a = 0.8f;
                Handles.color = c;
                Handles.zTest = UnityEngine.Rendering.CompareFunction.LessEqual;
                Handles.DrawLine(startPoint, hit.point);
                Handles.DrawWireDisc(hit.point, hit.normal, 0.5f);

                c.a = 0.25f;
                Handles.color = c;
                Handles.zTest = UnityEngine.Rendering.CompareFunction.Greater;
                Handles.DrawLine(capturePosition, hit.point);
                Handles.DrawWireDisc(hit.point, hit.normal, 0.5f);
            }
        }
    }
}
