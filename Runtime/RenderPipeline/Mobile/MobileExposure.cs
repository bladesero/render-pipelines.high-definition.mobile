namespace UnityEngine.Rendering.HighDefinition
{
    // Shared fixed/physical conversion and automatic-history seed parameters.
    internal static class MobileExposure
    {
        internal static bool IsAutomatic(Exposure e) => e.active && e.mode.value != ExposureMode.Fixed && e.mode.value != ExposureMode.UsePhysicalCamera;

        internal static void GetVolumeContext(Camera camera, HDRenderPipelineAsset asset, out Transform anchor, out int mask)
        {
            camera.TryGetComponent<HDAdditionalCameraData>(out var data);
            if (data == null && camera.cameraType == CameraType.SceneView && Camera.main != null)
                Camera.main.TryGetComponent(out data);
            anchor = data != null && data.volumeAnchorOverride != null ? data.volumeAnchorOverride : camera.transform;
            mask = data != null ? (int)data.volumeLayerMask : -1;
            if (data == null && camera.cameraType == CameraType.SceneView)
            {
                int skyMask = asset.currentPlatformRenderPipelineSettings.lightLoopSettings.skyLightingOverrideLayerMask;
                mask = skyMask == -1 ? -1 : ~(skyMask | (1 << 31));
            }
        }
        internal static float LensScale
        {
            get
            {
                var settings = HDRenderPipelineGlobalSettings.instance;
                return settings != null && settings.lensAttenuationMode == LensAttenuationMode.PerfectLens ? 1f : 1.2f;
            }
        }

        internal static float FromEV100(float ev100, float compensation, float lensScale)
            => 1f / (lensScale * Mathf.Pow(2, ev100 - compensation));

        internal static float Evaluate(Exposure exposure, Camera camera, bool exposureControl = true)
        {
            if (!exposureControl) return 1;
            bool sceneOverride = false;
#if UNITY_EDITOR
            sceneOverride = camera.cameraType == CameraType.SceneView && HDAdditionalSceneViewSettings.sceneExposureOverriden;
#endif
            if (!sceneOverride && !exposure.active) return 1;
            // Automatic modes use this as their seed; GPU history is owned separately.
            // Keep the Volume mode intact for Desktop.
            var parameters = exposure.GetFixedExposureParameters(camera, 0f, out bool physicalCamera);
            float ev100 = physicalCamera ? ColorUtils.ComputeEV100(parameters.y, parameters.z, parameters.w) : parameters.y;
            return FromEV100(ev100, parameters.x, LensScale);
        }
    }
}
