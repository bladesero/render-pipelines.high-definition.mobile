using System;

namespace UnityEngine.Rendering.HighDefinition
{
    /// <summary>Legacy serialized Volume data. Anti-aliasing is now configured on the camera.</summary>
    [Serializable, VolumeComponentMenuForRenderPipeline("Post-processing/Mobile Rendering", typeof(HDRenderPipeline))]
    public sealed class MobilePostProcessing : VolumeComponent
    {
        /// <summary>Retained for existing profiles; ignored by the renderer.</summary>
        [Tooltip("Legacy setting (ignored). Configure Anti-aliasing on the HDRP camera or in HDRP Scene View settings.")]
        public BoolParameter fxaa = new BoolParameter(true);
    }
}
