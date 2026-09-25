// Core supplies the exact same quad placement and octahedral seam wrapping as
// HDRP's atlas blit. Filter in the raster pass so no compute cache is initialized.
#define PREFER_HALF 0
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Packing.hlsl"
#define TEXTURE2D_X TEXTURE2D
#define SAMPLE_TEXTURE2D_X_LOD SAMPLE_TEXTURE2D_LOD
#include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/ImageBasedLighting.hlsl"
#include "MobilePrecision.hlsl"
float _MobileReflectionSourceSize;
mobile_half4 MobileReflectionFilter(Varyings input) : SV_Target
{
    float2 uv = RepeatOctahedralUV(input.texcoord.x, input.texcoord.y);
    float3 n = normalize(UnpackNormalOctQuadEncode(uv * 2 - 1));
    if (_BlitMipLevel < .5)
        return mobile_half4(min(SAMPLE_TEXTURECUBE_LOD(_BlitCubeTexture, sampler_LinearRepeat, n, 0).rgb, 65504), 1);
    float roughness = MipmapLevelToPerceptualRoughness(_BlitMipLevel);
    float a = roughness * roughness, a2 = a * a;
    float3 tangent = normalize(cross(abs(n.z) < .999 ? float3(0,0,1) : float3(1,0,0), n));
    float3 bitangent = cross(n, tangent);
    float3 sum = 0; float weight = 0;
    for (uint i = 0; i < 64; ++i)
    {
        uint reversed = ((i & 1u) << 5) | ((i & 2u) << 3) | ((i & 4u) << 1)
                      | ((i & 8u) >> 1) | ((i & 16u) >> 3) | ((i & 32u) >> 5);
        float2 xi = float2((i + .5) / 64.0, reversed / 64.0);
        float phi = TWO_PI * xi.x;
        float cosTheta = sqrt((1 - xi.y) / (1 + (a2 - 1) * xi.y));
        float sinTheta = sqrt(saturate(1 - cosTheta * cosTheta));
        float3 h = tangent * (sinTheta * cos(phi)) + bitangent * (sinTheta * sin(phi)) + n * cosTheta;
        float3 l = reflect(-n, h);
        float nl = saturate(dot(n, l));
        if (nl > 0)
        {
            float denominator = cosTheta * cosTheta * (a2 - 1) + 1;
            float pdf = max(a2 / max(4 * PI * denominator * denominator, 1e-8), 1e-5);
            float solidAngle = 4 * PI / (6 * _MobileReflectionSourceSize * _MobileReflectionSourceSize);
            float lod = max(0, .5 * log2(1 / (64 * pdf * solidAngle)));
            sum += SAMPLE_TEXTURECUBE_LOD(_BlitCubeTexture, sampler_LinearRepeat, l, lod).rgb * nl;
            weight += nl;
        }
    }
    return mobile_half4(min(sum / max(weight, 1e-5), 65504), 1);
}
