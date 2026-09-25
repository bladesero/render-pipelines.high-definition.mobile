using System;
using UnityEngine.Experimental.Rendering;

namespace UnityEngine.Rendering.HighDefinition
{
    /// <summary>Selects the independently initialized HDRP renderer.</summary>
    public enum HDRendererMode { Desktop = 0, Mobile = 1 }

    /// <summary>Resolution of the raster exposure meter.</summary>
    public enum MobileMeteringResolution { Low = 64, Medium = 128, High = 256 }
    /// <summary>Face resolution of the raster environment reflection cache.</summary>
    public enum MobileEnvironmentResolution { Low = 32, High = 64 }
    /// <summary>Diffuse source for objects without a lightmap.</summary>
    public enum MobileEnvironmentDiffuseSource { PreferBakedProbes = 0, RuntimeSky = 1 }

    /// <summary>Settings for the experimental Android Vulkan forward renderer.</summary>
    [Serializable]
    public sealed class MobileRenderSettings
    {
        [Tooltip("Render Game cameras targeting the display at 1920x1080 before upscaling. Preview cameras and explicit RenderTextures retain their own dimensions.")]
        public bool fixed1080p = true;
        [Range(0.5f, 1f)] public float renderScale = 1f;
        [Range(0, 4)] public int additionalLightsPerObject = 4;
        public bool mainLightShadows = true;
        [Range(256, 2048)] public int shadowAtlasSize = 1024;
        [Min(0.1f)] public float shadowDistance = 30f;
        [Range(0.05f, 0.95f)] public float cascadeSplit = 0.35f;
        [Tooltip("Fraction of the first cascade radius used to blend into the second cascade. Zero disables blending.")]
        [Range(0f, 0.3f)] public float cascadeBlend = 0.1f;
        [Tooltip("Inset shadow casters along the negative surface normal, in shadow-map texels. Converted to world units separately for each cascade and reduced on surfaces facing the light.")]
        [Min(0f)] public float shadowNormalBias = 0.02f;
        public bool fxaa = true;
        public MobileMeteringResolution meteringResolution = MobileMeteringResolution.Medium;
        public bool bloom = false;
        [Tooltip("Draw magenta for shaders without MobileForward. This diagnostic scans loaded renderers once per frame; disable after content validation for production profiling.")]
        public bool drawUnsupportedMaterials = true;
        public Cubemap sky;
        [Tooltip("When no explicit cubemap is assigned, use Volume HDRI/Gradient sky or a lightweight procedural / URP skybox approximation. Environment Lighting also uses this resolved sky.")]
        public bool skyFallback = true;
        [Tooltip("Generate diffuse irradiance and GGX reflection from the resolved sky. Baked lightmaps/probes retain priority.")]
        public bool environmentLighting = true;
        public MobileEnvironmentResolution environmentResolution = MobileEnvironmentResolution.Low;
        [Tooltip("PreferBakedProbes retains native SH when the scene has a baked probe set. RuntimeSky uses sky irradiance on all non-lightmapped objects. Neither mode adds sky a second time to a lightmap.")]
        public MobileEnvironmentDiffuseSource environmentDiffuseSource = MobileEnvironmentDiffuseSource.PreferBakedProbes;
        [Tooltip("Faces/mips filtered per update after initial warm-up. The old complete cache remains visible until the new generation is ready.")]
        [Range(1, 48)] public int environmentJobsPerFrame = 6;
        [Min(0f)] public float skyIntensity = 1f;
        public Color fogColor = Color.gray;
        [Min(0f)] public float fogStart = 30f;
        [Min(0.1f)] public float fogEnd = 100f;
        public bool fog;

        internal Vector2Int GetRenderSize(Camera camera)
        {
            bool fixedSize = fixed1080p && camera.cameraType == CameraType.Game && camera.targetTexture == null;
            float scale = Mathf.Clamp(renderScale, 0.5f, 1f);
            return new Vector2Int(Mathf.Max(1, Mathf.RoundToInt((fixedSize ? 1920 : camera.pixelWidth) * scale)),
                Mathf.Max(1, Mathf.RoundToInt((fixedSize ? 1080 : camera.pixelHeight) * scale)));
        }
    }

    /// <summary>Only raster shaders are referenced by mobile assets.</summary>
    [Serializable]
    public sealed class MobileRenderResources
    {
        public Shader compositeShader;
        public Shader skyShader;
        public Shader errorShader;

        public bool isValid => compositeShader != null && skyShader != null && errorShader != null;
    }

    internal static class MobileRenderCapabilities
    {
        internal static bool IsDeviceSupported(GraphicsDeviceType device, bool editor)
            => device == GraphicsDeviceType.Vulkan || (editor && device == GraphicsDeviceType.Direct3D11);

        internal static string Validate(HDRenderPipelineAsset asset)
        {
            if (!IsDeviceSupported(SystemInfo.graphicsDeviceType, Application.isEditor))
                return "HDRP Mobile requires Android Vulkan (D3D11/Vulkan for Windows Editor preview).";
            if (!Application.isEditor && Application.platform != RuntimePlatform.Android)
                return "HDRP Mobile player support is limited to Android.";
            if (QualitySettings.activeColorSpace != ColorSpace.Linear)
                return "HDRP Mobile requires Linear color space.";
            if (!SystemInfo.IsFormatSupported(GraphicsFormat.R16G16B16A16_SFloat, FormatUsage.Render)
                || !SystemInfo.IsFormatSupported(GraphicsFormat.R16G16B16A16_SFloat, FormatUsage.Sample)
                || !SystemInfo.IsFormatSupported(GraphicsFormat.R16G16B16A16_SFloat, FormatUsage.Linear))
                return "HDRP Mobile requires a renderable, filterable RGBA16F color target.";
            if (!asset.mobileResources.isValid)
                return "HDRP Mobile shader references are missing. Use Assign Mobile Resources in the asset inspector.";
            if (!SystemInfo.IsFormatSupported(GraphicsFormat.R32G32_SFloat, FormatUsage.Render)
                || !SystemInfo.IsFormatSupported(GraphicsFormat.R32G32_SFloat, FormatUsage.Sample))
                return "HDRP Mobile exposure requires a renderable and sampleable RG32F target.";
            return null;
        }
    }
}
