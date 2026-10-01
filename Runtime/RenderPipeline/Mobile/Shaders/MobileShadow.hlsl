#ifndef HDRP_MOBILE_SHADOW_INCLUDED
#define HDRP_MOBILE_SHADOW_INCLUDED

// Keep the complete shadow path in FP32: world/atlas coordinates, cascade sphere
// tests, filtering and blending. Do not narrow comparison results to mobile_half.

float3 MobileApplyShadowNormalBias(float3 positionWS, float3 normalWS, float3 lightDirection, float biasWS)
{
    // lightDirection points toward the light. Inset caster geometry along -N;
    // pushing the caster out along +N increases self-shadowing instead.
    float grazing = 1 - saturate(dot(normalWS, lightDirection));
    return positionWS - normalWS * (biasWS * grazing);
}

float MobileCascadeWeight(float distanceSquared, float radiusSquared, float blend)
{
    if (blend <= 0 || radiusSquared <= 0) return 0;
    float radiusFraction = sqrt(max(0, distanceSquared / radiusSquared));
    return smoothstep(1 - blend, 1, radiusFraction);
}

float4 MobileShadowTile(int slice, int grid, float texel)
{
    float size = floor(1.0 / texel / grid + 0.01) * texel;
    float2 minimum = float2(slice % grid, slice / grid) * size;
    return float4(minimum, minimum + size);
}

float4 MobileCascadeTile(int cascade)
{
    int grid = _MobileCascadeCount <= 1 ? 1 : (_MobileCascadeCount <= 4 ? 2 : 3);
    return MobileShadowTile(cascade, grid, _MobileShadowParams.y);
}

float MobileFilterShadow(TEXTURE2D_SHADOW_PARAM(atlas, comparisonSampler), float3 projected, float4 tile, float texel)
{
    if (projected.z <= 0 || projected.z >= 1 || any(projected.xy < tile.xy) || any(projected.xy > tile.zw)) return 1;
    int kernel = clamp(_MobileShadowFilterSize, 1, 4);
    float shadow = 0;
    [unroll] for (int y = 0; y < 4; y++)
        [unroll] for (int x = 0; x < 4; x++)
        {
            if (x < kernel && y < kernel)
            {
                float2 uv = clamp(projected.xy + (float2(x, y) - (kernel - 1) * 0.5) * texel,
                    tile.xy + texel * 0.5, tile.zw - texel * 0.5);
                shadow += SAMPLE_TEXTURE2D_SHADOW(atlas, comparisonSampler, float3(uv, projected.z));
            }
        }
    return shadow / (kernel * kernel);
}

float MobileSampleCascade(float3 positionWS, int cascade)
{
    float3 projected = mul(_MobileCSMShadowMatrices6[cascade], float4(positionWS, 1)).xyz;
    return MobileFilterShadow(TEXTURE2D_SHADOW_ARGS(_MobileShadowAtlas, sampler_MobileShadowAtlas),
        projected, MobileCascadeTile(cascade), _MobileShadowParams.y);
}

int MobileSelectCascade(float3 positionWS)
{
    [unroll] for (int cascade = 0; cascade < 6; cascade++)
    {
        if (cascade >= _MobileCascadeCount) break;
        float3 delta = positionWS - _MobileCSMShadowSpheres6[cascade].xyz;
        if (dot(delta, delta) < _MobileCSMShadowSpheres6[cascade].w) return cascade;
    }
    return -1;
}

float MobileShadowFade(float3 positionWS)
{
    return saturate((1 - distance(positionWS, _MobileCameraPosition.xyz) / max(_MobileShadowParams.z, 0.01)) * 10);
}

float MobileShadow(float3 positionWS)
{
    if (_MobileShadowParams.x <= 0) return 1;
    int cascade = MobileSelectCascade(positionWS);
    if (cascade < 0) return 1;
    float shadow = MobileSampleCascade(positionWS, cascade);
    if (cascade + 1 < _MobileCascadeCount)
    {
        float3 delta = positionWS - _MobileCSMShadowSpheres6[cascade].xyz;
        float3 nextDelta = positionWS - _MobileCSMShadowSpheres6[cascade + 1].xyz;
        float weight = MobileCascadeWeight(dot(delta, delta), _MobileCSMShadowSpheres6[cascade].w, _MobileShadowParams.w);
        [branch] if (weight > 0 && dot(nextDelta, nextDelta) < _MobileCSMShadowSpheres6[cascade + 1].w)
            shadow = lerp(shadow, MobileSampleCascade(positionWS, cascade + 1), weight);
    }
    return lerp(1, shadow, _MobileShadowParams.x * MobileShadowFade(positionWS));
}

int MobilePointShadowFace(float3 direction)
{
    float3 magnitude = abs(direction);
    if (magnitude.x >= magnitude.y && magnitude.x >= magnitude.z) return direction.x >= 0 ? 0 : 1;
    if (magnitude.y >= magnitude.z) return direction.y >= 0 ? 2 : 3;
    return direction.z >= 0 ? 4 : 5;
}

float MobileLocalShadow(float3 positionWS, uint lightIndex)
{
    float4 info = _MobileLocalShadowInfo[lightIndex];
    if (info.y <= 0) return 1;
    int slice = (int)info.x;
    if (info.z > 1) slice += MobilePointShadowFace(positionWS - _MobileLightPositions[lightIndex].xyz);
    float4 projected = mul(_MobileLocalShadowMatrices[slice], float4(positionWS, 1));
    if (projected.w <= 0) return 1;
    float shadow = MobileFilterShadow(TEXTURE2D_SHADOW_ARGS(_MobileLocalShadowAtlas, sampler_MobileLocalShadowAtlas),
        projected.xyz / projected.w, MobileShadowTile(slice, (int)_MobileLocalShadowParams.y, _MobileLocalShadowParams.x), _MobileLocalShadowParams.x);
    return lerp(1, shadow, info.y * MobileShadowFade(positionWS));
}
#endif
