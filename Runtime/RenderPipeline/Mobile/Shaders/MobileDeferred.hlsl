#include "MobileInput.hlsl"
#include "MobileBRDF.hlsl"
#include "MobileShadow.hlsl"
#include "MobileGBuffer.hlsl"
#if defined(MOBILE_EXTENDED_LIT)
#include "MobileExtendedLit.hlsl"
#endif

#if defined(MOBILE_NATIVE_INPUT)
FRAMEBUFFER_INPUT_FLOAT(0);
FRAMEBUFFER_INPUT_FLOAT(1);
FRAMEBUFFER_INPUT_FLOAT(2);
#if defined(MOBILE_EXTENDED_LIT)
FRAMEBUFFER_INPUT_FLOAT(3);
#endif
#else
TEXTURE2D(_MobileGBuffer0);
TEXTURE2D(_MobileGBuffer1);
TEXTURE2D_FLOAT(_MobileDepthCopy);
#endif
TEXTURE2D(_MobileGBuffer3);
TEXTURE2D_FLOAT(_MobileDepthSource);
float4 _MobileBufferSize;
float4x4 _MobileLightVolumeTransform;
int _MobileLightIndex;
#if defined(MOBILE_RECEIVE_SHADOWS_OFF)
    #define MOBILE_SHADOW_FLAG 1
#else
    #define MOBILE_SHADOW_FLAG 0
#endif
#if defined(MOBILE_SPECULAR_HIGHLIGHTS_OFF)
    #define MOBILE_SPECULAR_FLAG 2
#else
    #define MOBILE_SPECULAR_FLAG 0
#endif
#define _MobileLightFlags (MOBILE_SHADOW_FLAG | MOBILE_SPECULAR_FLAG)
int _MobileMaterialType;
float _MobileUseLightVolume;

struct DeferredAttributes { float3 positionOS : POSITION; uint vertexID : SV_VertexID; };
struct DeferredVaryings { float4 positionCS : SV_POSITION; };
DeferredVaryings DeferredVert(uint vertexID : SV_VertexID)
{
    DeferredVaryings output;
    output.positionCS = GetFullScreenTriangleVertexPosition(vertexID);
    return output;
}

DeferredVaryings DeferredVolumeVert(DeferredAttributes input)
{
    DeferredVaryings output;
    output.positionCS = mul(_MobileVP, mul(_MobileLightVolumeTransform, float4(input.positionOS, 1)));
    return output;
}

void MobileReadGBuffer(float2 pixel, out float4 baseMetal, out float4 normalSmoothAO, out float depth)
{
#if defined(MOBILE_NATIVE_INPUT)
    baseMetal = LOAD_FRAMEBUFFER_INPUT(0, pixel);
    normalSmoothAO = LOAD_FRAMEBUFFER_INPUT(1, pixel);
    depth = LOAD_FRAMEBUFFER_INPUT(2, pixel).r;
#else
    baseMetal = LOAD_TEXTURE2D(_MobileGBuffer0, uint2(pixel));
    normalSmoothAO = LOAD_TEXTURE2D(_MobileGBuffer1, uint2(pixel));
    depth = LOAD_TEXTURE2D(_MobileDepthCopy, uint2(pixel)).r;
#endif
}

float3 MobilePositionFromDepth(float2 pixel, float depth)
{
    return ComputeWorldSpacePosition(pixel * _MobileBufferSize.zw, depth, _MobileInvVP);
}

float4 DeferredFrag(DeferredVaryings input) : SV_Target
{
    float4 baseMetal, normalSmoothAO; float depth;
    MobileReadGBuffer(input.positionCS.xy, baseMetal, normalSmoothAO, depth);
    float3 positionWS = MobilePositionFromDepth(input.positionCS.xy, depth);
    float3 normalWS = MobileDecodeNormal(normalSmoothAO.rg);
    float3 view = _MobileCameraForward.w > 0 ? -_MobileCameraForward.xyz : SafeNormalize(_MobileCameraPosition.xyz - positionWS);
    MobileBRDFData brdf = MobileInitializeBRDF(baseMetal.rgb, baseMetal.a, normalSmoothAO.b, 1);
    float3 light = _MobileMainDirection.xyz;
    float3 radiance = _MobileMainColor.rgb;
    if (_MobileLightIndex >= 0)
    {
        uint index = (uint)_MobileLightIndex;
        float3 delta = _MobileLightPositions[index].xyz - positionWS;
        float distanceSquared = max(dot(delta, delta), HALF_MIN);
        float normalizedDistance = distanceSquared * _MobileLightPositions[index].w;
        if (normalizedDistance >= 1) discard;
        light = delta * rsqrt(distanceSquared);
        float spot = saturate(dot(light, _MobileLightSpots[index].xyz) * _MobileLightAttenuations[index].x + _MobileLightAttenuations[index].y);
        if (spot <= 0) discard;
        float rangeFade = saturate(1 - normalizedDistance * normalizedDistance);
        radiance = _MobileLightColors[index].rgb * (rangeFade * rangeFade * spot * spot / distanceSquared);
        if ((_MobileLightFlags & 1) == 0) radiance *= MobileLocalShadow(positionWS, index);
    }
#if defined(MOBILE_EXTENDED_LIT)
    float4 fresnelCoat;
    #if defined(MOBILE_NATIVE_INPUT)
        fresnelCoat = LOAD_FRAMEBUFFER_INPUT(3, input.positionCS.xy);
    #else
        fresnelCoat = LOAD_TEXTURE2D(_MobileGBuffer3, uint2(input.positionCS.xy));
    #endif
    MobileExtendedSurface surface = (MobileExtendedSurface)0;
    // Standard F0 is reconstructed without an extra RGBA8 quantization step.
    surface.fresnel0 = _MobileMaterialType == 4 ? fresnelCoat.rgb : brdf.specular;
    surface.coat = fresnelCoat.a;
    if (_MobileLightIndex < 0)
        radiance *= MobileExtendedMainShadow(surface, _MobileMaterialType, normalWS, positionWS, (_MobileLightFlags & 1) == 0);
    brdf.specular = surface.fresnel0;
    if (_MobileMaterialType == 4) brdf.diffuse = baseMetal.rgb;
    float3 diffuse, specular;
    MobileEvaluateExtendedDirect(brdf, surface, _MobileMaterialType, normalWS, view, light,
        (_MobileLightFlags & 2) == 0 ? 1 : 0, diffuse, specular, positionWS, _MobileLightIndex < 0, (_MobileLightFlags & 1) == 0);
    float3 scale = radiance * MobileCurrentExposure();
    return float4(clamp((specular + diffuse) * scale, 0, float3(65024, 65024, 64512)), 0);
#else
    if (_MobileLightIndex < 0 && (_MobileLightFlags & 1) == 0) radiance *= MobileShadow(positionWS);
    float3 result = MobileDirectBRDF(brdf, normalWS, view, light, (_MobileLightFlags & 2) == 0 ? 1 : 0)
        * radiance * MobileCurrentExposure();
    return float4(clamp(result, 0, float3(65024, 65024, 64512)), 0);
#endif
}

float4 DeferredFog(DeferredVaryings input) : SV_Target
{
    float4 unused0, unused1; float depth;
    MobileReadGBuffer(input.positionCS.xy, unused0, unused1, depth);
    float3 positionWS = MobilePositionFromDepth(input.positionCS.xy, depth);
    float fog = _MobileFog.x * saturate((distance(positionWS, _MobileCameraPosition.xyz) - _MobileFog.y) * _MobileFog.z);
    return float4(clamp(_MobileFogColor.rgb * MobileCurrentExposure(), 0, float3(65024, 65024, 64512)), fog);
}

float DeferredCopyDepth(DeferredVaryings input) : SV_Target
{
    return LOAD_TEXTURE2D(_MobileDepthSource, uint2(input.positionCS.xy)).r;
}

#if defined(MOBILE_NATIVE_INPUT)
float4 DeferredProbe(DeferredVaryings input) : SV_Target
{
    return float4(LOAD_FRAMEBUFFER_INPUT(0, input.positionCS.xy).r,
        LOAD_FRAMEBUFFER_INPUT(1, input.positionCS.xy).g, LOAD_FRAMEBUFFER_INPUT(2, input.positionCS.xy).r, 1);
}
#endif
