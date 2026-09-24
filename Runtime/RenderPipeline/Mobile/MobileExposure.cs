namespace UnityEngine.Rendering.HighDefinition
{
    // Stateless current-camera exposure: no history, compute pass or desktop managers.
    internal static class MobileExposure
    {
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
            // Automatic/Histogram/Curve modes use the shared fixed EV on Mobile.
            // Keep the Volume mode intact so Desktop retains automatic metering.
            var parameters = exposure.GetFixedExposureParameters(camera, 0f, out bool physicalCamera);
            float ev100 = physicalCamera ? ColorUtils.ComputeEV100(parameters.y, parameters.z, parameters.w) : parameters.y;
            return FromEV100(ev100, parameters.x, LensScale);
        }
    }
}
