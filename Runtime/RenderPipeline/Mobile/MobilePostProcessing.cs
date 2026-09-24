using System;

namespace UnityEngine.Rendering.HighDefinition
{
    /// <summary>Volume overrides specific to the independent mobile forward backend.</summary>
    [Serializable, VolumeComponentMenuForRenderPipeline("Post-processing/Mobile Rendering", typeof(HDRenderPipeline))]
    public sealed class MobilePostProcessing : VolumeComponent
    {
        /// <summary>Disable FXAA locally when the mobile asset allows it.</summary>
        public BoolParameter fxaa = new BoolParameter(true);
    }
}
