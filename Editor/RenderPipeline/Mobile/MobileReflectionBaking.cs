using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace UnityEditor.Rendering.HighDefinition
{
    internal static class MobileReflectionBaking
    {
        // Asset imports can temporarily dispose the current pipeline instance.
        internal static bool active => GraphicsSettings.currentRenderPipeline is HDRenderPipelineAsset asset && asset.rendererMode == HDRendererMode.Mobile;

        internal static void Render(HDProbe probe, RenderTexture target, out CameraSettings settings, out CameraPositionSettings position)
        {
            if (!(probe is HDAdditionalReflectionData) || target.dimension != TextureDimension.Cube)
                throw new NotSupportedException("Mobile reflection baking supports cubemap Reflection Probes only.");
            var capturePosition = ProbeCapturePositionSettings.ComputeFrom(probe, null);
            HDRenderUtilities.ComputeCameraSettingsFromProbeSettings(probe.settings, capturePosition, out settings, out position, 0);
            var cameras = new List<CameraSettings>();
            var positions = new List<CameraPositionSettings>();
            var faces = new List<CubemapFace>();
            HDRenderUtilities.GenerateRenderingSettingsFor(probe.settings, capturePosition, cameras, positions, faces, 0, ProbeRenderSteps.ReflectionProbeMask);
            var go = new GameObject("Mobile Reflection Bake Camera") { hideFlags = HideFlags.HideAndDontSave };
            var camera = go.AddComponent<Camera>(); camera.enabled = false;
            if (UnityEditor.SceneManagement.EditorSceneManager.IsPreviewScene(probe.gameObject.scene)) camera.scene = probe.gameObject.scene;
            go.AddComponent<HDAdditionalCameraData>();
            var previousCapture = MobileRenderPipelineBackend.reflectionCaptureCamera;
            var previousRT = RenderTexture.active;
            var hidden = new List<Renderer>();
            var terrains = new List<(Terrain terrain, bool heightmap, bool foliage)>();
            var face = new RenderTexture(target.width, target.height, 24, target.graphicsFormat)
            { hideFlags = HideFlags.HideAndDontSave, name = "Mobile Reflection Bake Face" };
            try
            {
                // Same ReflectionProbeStatic rule as HDRP's EditorCameraUtils bake.
                foreach (var renderer in Resources.FindObjectsOfTypeAll<Renderer>())
                    if (renderer.gameObject.scene.IsValid() && !renderer.forceRenderingOff && !GameObjectUtility.AreStaticEditorFlagsSet(renderer.gameObject, StaticEditorFlags.ReflectionProbeStatic))
                    { hidden.Add(renderer); renderer.forceRenderingOff = true; }
                foreach (var terrain in Resources.FindObjectsOfTypeAll<Terrain>())
                    if (terrain.gameObject.scene.IsValid() && !GameObjectUtility.AreStaticEditorFlagsSet(terrain.gameObject, StaticEditorFlags.ReflectionProbeStatic))
                    {
                        terrains.Add((terrain, terrain.drawHeightmap, terrain.drawTreesAndFoliage));
                        terrain.drawHeightmap = false; terrain.drawTreesAndFoliage = false;
                    }
                face.Create();
                MobileRenderPipelineBackend.reflectionCaptureCamera = camera;
                for (int i = 0; i < faces.Count; ++i)
                {
                    camera.ApplySettings(cameras[i]); camera.ApplySettings(positions[i]);
                    camera.cameraType = CameraType.Reflection;
                    camera.targetTexture = face;
                    camera.aspect = 1; camera.fieldOfView = 90;
                    camera.Render();
                    Graphics.CopyTexture(face, 0, 0, target, (int)faces[i], 0);
                }
                target.IncrementUpdateCount();
            }
            finally
            {
                MobileRenderPipelineBackend.reflectionCaptureCamera = previousCapture;
                foreach (var renderer in hidden) if (renderer != null) renderer.forceRenderingOff = false;
                foreach (var entry in terrains) if (entry.terrain != null)
                { entry.terrain.drawHeightmap = entry.heightmap; entry.terrain.drawTreesAndFoliage = entry.foliage; }
                RenderTexture.active = previousRT;
                face.Release(); CoreUtils.Destroy(face); CoreUtils.Destroy(go);
            }
        }
    }
}
