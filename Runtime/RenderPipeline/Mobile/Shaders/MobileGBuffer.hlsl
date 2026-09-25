#ifndef HDRP_MOBILE_GBUFFER_INCLUDED
#define HDRP_MOBILE_GBUFFER_INCLUDED

struct MobileGBufferOutput
{
    float4 baseMetal : SV_Target0;
    float4 normalSmoothAO : SV_Target1;
    float3 lighting : SV_Target2;
};

struct MobileExtendedGBufferOutput
{
    float4 baseMetal : SV_Target0;
    float4 normalSmoothAO : SV_Target1;
    float3 lighting : SV_Target2;
    float4 fresnelCoat : SV_Target3;
};

float2 MobileEncodeNormal(float3 normalWS)
{
    return saturate(PackNormalOctQuadEncode(normalize(normalWS)) * 0.5 + 0.5);
}

float3 MobileDecodeNormal(float2 encoded)
{
    return normalize(UnpackNormalOctQuadEncode(encoded * 2 - 1));
}

MobileGBufferOutput MobileEncodeGBuffer(float3 baseColor, float metallic, float3 normalWS,
    float smoothness, float ao, float3 lighting)
{
    MobileGBufferOutput output;
    // Hardware applies sRGB only to RGB; metallic stays linear.
    output.baseMetal = float4(saturate(baseColor), saturate(metallic));
    output.normalSmoothAO = float4(MobileEncodeNormal(normalWS), saturate(smoothness), saturate(ao));
    // R/G have 6 mantissa bits, B has 5. Bound each channel to a finite value.
    output.lighting = clamp(lighting, 0, float3(65024, 65024, 64512));
    return output;
}
#endif
