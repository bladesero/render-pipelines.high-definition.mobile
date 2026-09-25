#ifndef HDRP_MOBILE_SHADOW_INCLUDED
#define HDRP_MOBILE_SHADOW_INCLUDED

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

half MobileSampleCascade(float3 positionWS, int cascade)
{
    float3 projected = mul(_MobileShadowMatrices[cascade], float4(positionWS, 1)).xyz;
    float2 tileMin = float2(cascade * 0.5, 0);
    float2 tileMax = tileMin + 0.5;
    if (projected.z <= 0 || projected.z >= 1 || any(projected.xy < tileMin) || any(projected.xy > tileMax)) return 1;
    float texel = _MobileShadowParams.y;
    half shadow = 0;
    [unroll] for (int y = -1; y <= 1; y += 2)
        [unroll] for (int x = -1; x <= 1; x += 2)
        {
            float2 uv = clamp(projected.xy + float2(x, y) * texel * 0.5, tileMin + texel, tileMax - texel);
            shadow += SAMPLE_TEXTURE2D_SHADOW(_MobileShadowAtlas, sampler_MobileShadowAtlas, float3(uv, projected.z));
        }
    return shadow * 0.25;
}

half MobileShadow(float3 positionWS)
{
    if (_MobileShadowParams.x <= 0) return 1;
    float3 d0 = positionWS - _MobileShadowSpheres[0].xyz;
    float3 d1 = positionWS - _MobileShadowSpheres[1].xyz;
    float distance0 = dot(d0, d0);
    bool inFirst = distance0 < _MobileShadowSpheres[0].w;
    bool inSecond = dot(d1, d1) < _MobileShadowSpheres[1].w;
    if (!inFirst && !inSecond) return 1;
    half shadow = MobileSampleCascade(positionWS, inFirst ? 0 : 1);
    if (inFirst && inSecond)
    {
        float weight = MobileCascadeWeight(distance0, _MobileShadowSpheres[0].w, _MobileShadowParams.w);
        [branch] if (weight > 0) shadow = lerp(shadow, MobileSampleCascade(positionWS, 1), weight);
    }
    float fade = saturate((1 - distance(positionWS, _MobileCameraPosition.xyz) / max(_MobileShadowParams.z, 0.01)) * 10);
    return lerp(1, shadow, _MobileShadowParams.x * fade);
}
#endif
