#ifndef HDRP_MOBILE_REFLECTION_INCLUDED
#define HDRP_MOBILE_REFLECTION_INCLUDED
TEXTURE2D_ARRAY(_MobileReflectionAtlas);
SAMPLER(sampler_MobileReflectionAtlas);
int _MobileReflectionCount16;
int _MobileReflectionHasHDRPProbes;
float4 _MobileReflectionAtlasCubeData;
float4 _MobileReflectionScaleOffset16[16], _MobileReflectionExtents16[16];
float4 _MobileReflectionBlendPositive16[16], _MobileReflectionBlendNegative16[16];
float4 _MobileReflectionCapture16[16], _MobileReflectionProxyExtents16[16], _MobileReflectionParams16[16];
float4x4 _MobileReflectionWorldToInfluence16[16], _MobileReflectionWorldToProxy16[16];

float MobileHDProbeWeight(int i, float3 position)
{
    float3 p = mul(_MobileReflectionWorldToInfluence16[i], float4(position, 1)).xyz;
    float4 e = _MobileReflectionExtents16[i];
    float3 positive = _MobileReflectionBlendPositive16[i].xyz;
    float weight;
    if (e.w > 0)
    {
        float distance = e.x - length(p);
        weight = distance >= 0 ? saturate(distance / max(positive.x, 1e-5)) : 0;
        if (positive.x <= 0) weight = distance >= 0 ? 1 : 0;
    }
    else
    {
        float3 distance = e.xyz - abs(p);
        float3 blend = p >= 0 ? positive : _MobileReflectionBlendNegative16[i].xyz;
        float3 weights = blend > 0 ? saturate(distance / max(blend, 1e-5)) : (distance >= 0 ? 1 : 0);
        weight = min(weights.x, min(weights.y, weights.z));
    }
    return weight * _MobileReflectionParams16[i].x;
}

float3 MobileHDProbeDirection(int i, float3 position, float3 direction)
{
    float4 extents = _MobileReflectionProxyExtents16[i];
    if (extents.w < 0) return direction;
    float3 p = mul(_MobileReflectionWorldToProxy16[i], float4(position, 1)).xyz;
    float3 d = mul((float3x3)_MobileReflectionWorldToProxy16[i], direction);
    float distance;
    if (extents.w > 0)
    {
        float b = dot(p, d);
        distance = -b + sqrt(max(0, b * b - dot(p, p) + extents.x * extents.x));
    }
    else
    {
        float3 safeDirection = (d >= 0 ? 1 : -1) * max(abs(d), 1e-6);
        float3 intersection = ((d > 0 ? extents.xyz : -extents.xyz) - p) / safeDirection;
        distance = min(intersection.x, min(intersection.y, intersection.z));
    }
    return position + direction * max(0, distance) - _MobileReflectionCapture16[i].xyz;
}

float3 MobileSampleHDProbe(int i, float3 direction, float lod)
{
    // Keep in sync with HDRP LightLoopDef.GetReflectionAtlasCoordsCube.
    float2 uv = saturate(PackNormalOctQuadEncode(direction) * .5 + .5);
    float2 padding = _MobileReflectionAtlasCubeData.xy;
    padding *= exp2(max(lod - _MobileReflectionAtlasCubeData.z, 0));
    float4 scaleOffset = _MobileReflectionScaleOffset16[i];
    uv = uv * (scaleOffset.xy - padding) + scaleOffset.zw + .5 * padding;
    return SAMPLE_TEXTURE2D_ARRAY_LOD(_MobileReflectionAtlas, sampler_MobileReflectionAtlas, uv, 0, lod).rgb
        * _MobileReflectionCapture16[i].w;
}

float3 MobileSampleHDRPReflection(float3 position, float3 reflected, float roughness)
{
    float mip = min(6, PerceptualRoughnessToMipmapLevel(roughness));
    float3 result = 0;
    float remaining = 1;
    for (int i = 0; i < _MobileReflectionCount16; ++i)
    {
        // CPU orders local/small volumes first, like Desktop LightLoop.
        // Match UpdateLightingHierarchyWeights: consume weight additively,
        // rather than averaging overlapping indoor and outdoor probes.
        float weight = min(MobileHDProbeWeight(i, position), remaining);
        if (weight > 0)
        {
            result += weight * MobileSampleHDProbe(i, MobileHDProbeDirection(i, position, reflected), mip);
            remaining = max(0, remaining - weight);
        }
    }
    result *= _MobileIndirectMultiplier.z;
    if (_MobileEnvironmentParams.x > 0)
        result += remaining * SAMPLE_TEXTURECUBE_LOD(_MobileEnvironmentReflection, sampler_MobileEnvironmentReflection,
            reflected, roughness * _MobileEnvironmentParams.z).rgb * _MobileEnvironmentParams.w;
    return result;
}
#endif
