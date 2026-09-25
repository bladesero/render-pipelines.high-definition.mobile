using System;

namespace UnityEngine.Rendering.HighDefinition
{
    /// <summary>Volume overrides shared by the Mobile Forward and Deferred backends.</summary>
    [Serializable, VolumeComponentMenuForRenderPipeline("Post-processing/Mobile Rendering", typeof(HDRenderPipeline))]
    public sealed class MobilePostProcessing : VolumeComponent
    {
        /// <summary>Enable FXAA for cameras affected by this Volume.</summary>
        public BoolParameter fxaa = new BoolParameter(true);
    }
}
