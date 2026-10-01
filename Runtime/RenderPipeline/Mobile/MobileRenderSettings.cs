using System;
using UnityEngine.Experimental.Rendering;

namespace UnityEngine.Rendering.HighDefinition
{
    /// <summary>Selects the independently initialized HDRP renderer.</summary>
    public enum HDRendererMode { Desktop = 0, Mobile = 1 }

    /// <summary>Opaque lighting path. Existing assets retain Forward.</summary>
    public enum MobileRenderingPath { Forward = 0, Deferred = 1 }
    /// <summary>Opt-in renderer submission for MobileInstanceGroup components.</summary>
    public enum MobileInstanceMode { Disabled = 0, CpuInstanced = 1 }
    /// <summary>Compact deferred attachment execution.</summary>
    public enum MobileDeferredExecution { Auto = 0, NativeRenderPass = 1, Sampled = 2 }

    /// <summary>Resolution of the raster exposure meter.</summary>
    public enum MobileMeteringResolution { Low = 64, Medium = 128, High = 256 }
    /// <summary>Face resolution of the raster environment reflection cache.</summary>
    public enum MobileEnvironmentResolution { Low = 32, High = 64 }
    /// <summary>Diffuse source for objects without a lightmap.</summary>
    public enum MobileEnvironmentDiffuseSource { PreferBakedProbes = 0, RuntimeSky = 1 }

    /// <summary>Settings for the experimental Mobile backend on Android Vulkan and Windows DirectX.</summary>
    [Serializable]
    public sealed class MobileRenderSettings
    {
        public MobileRenderingPath renderingPath = MobileRenderingPath.Forward;
        public MobileInstanceMode instanceMode = MobileInstanceMode.Disabled;
        [Min(0), Tooltip("Maximum instance distance. Zero uses the camera far plane.")]
        public float instanceDrawDistance = 100;
        [Min(0), Tooltip("Maximum native caster distance for managed instances. Zero uses Mobile shadow distance.")]
        public float instanceShadowDistance;
        [Range(1, 128), Tooltip("Instance payload is bounded to 128 entries per draw, including matrices and lighting.")]
        public int instanceBatchSize = 128;
        public MobileDeferredExecution deferredExecution = MobileDeferredExecution.Auto;
        [Tooltip("Allow one extra RGBA8 Fresnel/Coat attachment for deferred SpecularColor and clear coat. Other Lit types use Forward, without screen-space SSS.")]
        public bool extendedLitMaterials;
        [Tooltip("Render Game cameras targeting the display at 1920x1080 before upscaling. Preview cameras and explicit RenderTextures retain their own dimensions.")]
        public bool fixed1080p = true;
        [Range(0.5f, 1f)] public float renderScale = 1f;
        [Range(0, 4)] public int additionalLightsPerObject = 4;
        public bool mainLightShadows = true;
        [Range(256, 4096)] public int shadowAtlasSize = 4096;
        [Range(1, 6)] public int cascadeCount = 6;
        public bool localLightShadows = true;
        [Range(256, 4096)] public int localShadowAtlasSize = 4096;
        [Range(1, 4)] public int shadowFilterSize = 4;
        [Min(0.1f)] public float shadowDistance = 30f;
        [Range(0.01f, 0.95f)] public float cascadeSplit = 0.1f;
        [Tooltip("Fraction of each cascade radius used to blend into the next cascade. Zero disables blending.")]
        [Range(0f, 0.3f)] public float cascadeBlend = 0.1f;
        [Tooltip("Inset shadow casters along the negative surface normal, in shadow-map texels. Converted to world units separately for each cascade and reduced on surfaces facing the light.")]
        [Min(0f)] public float shadowNormalBias = 0.02f;
        public MobileMeteringResolution meteringResolution = MobileMeteringResolution.Medium;
        [Tooltip("Draw magenta for shaders without MobileForward. This diagnostic scans loaded renderers once per frame; disable after content validation for production profiling.")]
        public bool drawUnsupportedMaterials = true;
        public Cubemap sky;
        [Tooltip("When no explicit cubemap is assigned, use Volume HDRI/Gradient sky or a lightweight procedural / URP skybox approximation. Environment Lighting also uses this resolved sky.")]
        public bool skyFallback = true;
        [Tooltip("Generate diffuse irradiance and GGX reflection from the resolved sky. Baked lightmaps/probes retain priority.")]
        public bool environmentLighting = true;
        public MobileEnvironmentResolution environmentResolution = MobileEnvironmentResolution.Low;
        [Tooltip("HDRP octahedral reflection atlas. Up to 16 visible probes share 4x4 tiles, with seven filtered mip levels. Rounded up to a power of two.")]
        [Range(1024, 4096)] public int reflectionAtlasSize = 2048;
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

    /// <summary>Built-in raster shaders, owned by HDRP runtime resources.</summary>
    [Serializable, ReloadGroup]
    public sealed class MobileRenderResources
    {
        [Reload("Runtime/RenderPipeline/Mobile/Shaders/MobileComposite.shader")]
        public Shader compositeShader;
        [Reload("Runtime/RenderPipeline/Mobile/Shaders/MobileSky.shader")]
        public Shader skyShader;
        [Reload("Runtime/RenderPipeline/Mobile/Shaders/MobileError.shader")]
        public Shader errorShader;
        [Reload("Runtime/RenderPipeline/Mobile/Shaders/MobileDeferred.shader")]
        public Shader deferredShader;

        public bool isValid => compositeShader != null && skyShader != null && errorShader != null;
    }

    internal static class MobileRenderCapabilities
    {
        internal static bool IsDeviceSupported(GraphicsDeviceType device, RuntimePlatform platform, bool editor)
        {
            bool directX = device == GraphicsDeviceType.Direct3D11 || device == GraphicsDeviceType.Direct3D12;
            if (editor) return device == GraphicsDeviceType.Vulkan || (platform == RuntimePlatform.WindowsEditor && directX);
            return (platform == RuntimePlatform.Android && device == GraphicsDeviceType.Vulkan)
                || (platform == RuntimePlatform.WindowsPlayer && directX);
        }

        internal static bool PreferNativeDeferred(GraphicsDeviceType device, RuntimePlatform platform)
            => device == GraphicsDeviceType.Vulkan && platform == RuntimePlatform.Android;

        internal static string Validate(HDRenderPipelineAsset asset)
        {
            if (!IsDeviceSupported(SystemInfo.graphicsDeviceType, Application.platform, Application.isEditor))
                return "HDRP Mobile requires Android Vulkan or Windows D3D11/D3D12 (Vulkan is also available for Editor validation).";
            if (QualitySettings.activeColorSpace != ColorSpace.Linear)
                return "HDRP Mobile requires Linear color space.";
            if (!SystemInfo.IsFormatSupported(GraphicsFormat.R16G16B16A16_SFloat, FormatUsage.Render)
                || !SystemInfo.IsFormatSupported(GraphicsFormat.R16G16B16A16_SFloat, FormatUsage.Sample)
                || !SystemInfo.IsFormatSupported(GraphicsFormat.R16G16B16A16_SFloat, FormatUsage.Linear))
                return "HDRP Mobile requires a renderable, filterable RGBA16F color target.";
            if (!asset.mobileResources.isValid)
                return "HDRP built-in Mobile shader resources are unavailable. Check the HDRP package and Global Settings runtime resources.";
            if (!SystemInfo.IsFormatSupported(GraphicsFormat.R32G32_SFloat, FormatUsage.Render)
                || !SystemInfo.IsFormatSupported(GraphicsFormat.R32G32_SFloat, FormatUsage.Sample))
                return "HDRP Mobile exposure requires a renderable and sampleable RG32F target.";
            return null;
        }
    }
}
