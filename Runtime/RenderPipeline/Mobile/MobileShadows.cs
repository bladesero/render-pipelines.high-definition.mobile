using System;

namespace UnityEngine.Rendering.HighDefinition
{
    /// <summary>Per-camera mobile shadow overrides. Unchecked fields use the pipeline asset.</summary>
    [Serializable, VolumeComponentMenuForRenderPipeline("Lighting/Mobile Shadows", typeof(HDRenderPipeline))]
    public sealed class MobileShadows : VolumeComponent
    {
        public BoolParameter mainLightShadows = new BoolParameter(true);
        public ClampedIntParameter shadowAtlasSize = new ClampedIntParameter(4096, 256, 4096);
        public MinFloatParameter shadowDistance = new MinFloatParameter(30f, 0.1f);
        public ClampedIntParameter cascadeCount = new ClampedIntParameter(6, 1, 6);
        [Tooltip("First split / shadow distance. Later splits distribute the remaining distance evenly.")]
        public ClampedFloatParameter cascadeSplit = new ClampedFloatParameter(0.1f, 0.01f, 0.95f);
        public ClampedFloatParameter cascadeBlend = new ClampedFloatParameter(0.1f, 0f, 0.3f);
        public MinFloatParameter shadowNormalBias = new MinFloatParameter(0.02f, 0f);
        public BoolParameter localLightShadows = new BoolParameter(true);
        public ClampedIntParameter localShadowAtlasSize = new ClampedIntParameter(4096, 256, 4096);
        [Tooltip("PCF kernel width: 1, 2, 3 or 4 texels. Applies to directional and local shadows.")]
        public ClampedIntParameter shadowFilterSize = new ClampedIntParameter(4, 1, 4);
    }
}
