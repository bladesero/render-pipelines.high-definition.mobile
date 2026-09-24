namespace UnityEngine.Rendering.HighDefinition
{
    public partial class HDRenderPipelineAsset
    {
        [SerializeField] HDRendererMode m_RendererMode = HDRendererMode.Desktop;
        [SerializeField] MobileRenderSettings m_MobileSettings = new MobileRenderSettings();
        [SerializeField] MobileRenderResources m_MobileResources = new MobileRenderResources();

        /// <summary>Changing the renderer reconstructs the pipeline.</summary>
        public HDRendererMode rendererMode
        {
            get => m_RendererMode;
            set { if (m_RendererMode == value) return; m_RendererMode = value; OnValidate(); }
        }

        public MobileRenderSettings mobileSettings => m_MobileSettings;
        public MobileRenderResources mobileResources => m_MobileResources;
    }
}
