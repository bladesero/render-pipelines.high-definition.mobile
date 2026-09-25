namespace UnityEngine.Rendering.HighDefinition
{
    public partial class HDRenderPipelineAsset
    {
        [SerializeField] HDRendererMode m_RendererMode = HDRendererMode.Desktop;
        [SerializeField] MobileRenderSettings m_MobileSettings = new MobileRenderSettings();
        static readonly MobileRenderResources s_UnavailableMobileResources = new MobileRenderResources();

        /// <summary>Changing the renderer reconstructs the pipeline.</summary>
        public HDRendererMode rendererMode
        {
            get => m_RendererMode;
            set { if (m_RendererMode == value) return; m_RendererMode = value; OnValidate(); }
        }

        public MobileRenderSettings mobileSettings => m_MobileSettings;
        public MobileRenderResources mobileResources
        {
            get
            {
                var settings = HDRenderPipelineGlobalSettings.instance;
#if UNITY_EDITOR
                if (settings == null) settings = HDRenderPipelineGlobalSettings.Ensure();
#endif
                var resources = settings != null ? settings.renderPipelineResources : null;
#if UNITY_EDITOR
                if (resources != null && (resources.mobile == null || !resources.mobile.isValid || resources.mobile.deferredShader == null))
                    settings.EnsureRuntimeResources(forceReload: true);
#endif
                return resources != null && resources.mobile != null ? resources.mobile : s_UnavailableMobileResources;
            }
        }
    }
}
