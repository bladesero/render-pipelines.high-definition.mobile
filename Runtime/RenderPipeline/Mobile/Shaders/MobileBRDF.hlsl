#ifndef HDRP_MOBILE_BRDF_INCLUDED
#define HDRP_MOBILE_BRDF_INCLUDED
#include "MobilePrecision.hlsl"

// Metallic-workflow equations adapted from Unity URP 14.0.9 BRDF.hlsl.
// Unity Technologies, Unity Companion License. Pinned source and test oracle:
// Tests/Editor/MobileURPReference/README.md. No URP runtime package dependency.
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/CommonMaterial.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/ImageBasedLighting.hlsl"

struct MobileBRDFData
{
    mobile_half3 diffuse, specular;
    mobile_half perceptualRoughness, roughness, roughness2, grazingTerm;
    mobile_half normalizationTerm;
    // FP16 would round sharp lobes to -1, losing the GGX denominator's floor.
    float roughness2MinusOne;
};

MobileBRDFData MobileInitializeBRDF(mobile_half3 albedo, mobile_half metallic, mobile_half smoothness, mobile_half diffuseAlpha)
{
    MobileBRDFData data;
    mobile_half oneMinusReflectivity = 0.96h - metallic * 0.96h;
    data.diffuse = albedo * oneMinusReflectivity * diffuseAlpha;
    data.specular = lerp(mobile_half3(0.04h, 0.04h, 0.04h), albedo, metallic);
    data.perceptualRoughness = PerceptualSmoothnessToPerceptualRoughness(smoothness);
    data.roughness = max(PerceptualRoughnessToRoughness(data.perceptualRoughness), HALF_MIN_SQRT);
    data.roughness2 = max(data.roughness * data.roughness, HALF_MIN);
    data.grazingTerm = saturate(smoothness + (1.0h - oneMinusReflectivity));
    data.normalizationTerm = data.roughness * 4.0h + 2.0h;
    data.roughness2MinusOne = float(data.roughness2) - 1.0f;
    return data;
}

mobile_half MobileDirectSpecular(MobileBRDFData data, mobile_half3 normal, mobile_half3 light, mobile_half3 view)
{
    float3 halfDirection = SafeNormalize(float3(light) + float3(view));
    float noH = saturate(dot(float3(normal), halfDirection));
    mobile_half loH = mobile_half(saturate(dot(float3(light), halfDirection)));
    float d = noH * noH * data.roughness2MinusOne + 1.00001f;
    // Clamp before narrowing, independently of Core's REAL_IS_HALF setting.
    float term = data.roughness2 / ((d * d) * max(0.1h, loH * loH) * data.normalizationTerm);
    return mobile_half(clamp(term, 0.0, HALF_MAX));
}

mobile_half3 MobileDirectBRDF(MobileBRDFData data, mobile_half3 normal, mobile_half3 view, mobile_half3 light, mobile_half specularHighlights)
{
    // URP's direct BRDF omits PI from both diffuse and its GGX approximation.
    // HDRP supplies physical lux/candela, so normalize the complete direct term.
    // IBL, lightmaps and emission have separate radiance conventions.
    return (data.diffuse + data.specular * MobileDirectSpecular(data, normal, light, view) * specularHighlights)
        * (mobile_half(INV_PI) * saturate(dot(normal, light)));
}

mobile_half3 MobileDirectBRDF(MobileBRDFData data, mobile_half3 normal, mobile_half3 view, mobile_half3 light)
{
    return MobileDirectBRDF(data, normal, view, light, 1);
}

float3 MobileEnvironmentBRDF(MobileBRDFData data, float3 irradiance, float3 reflection, mobile_half noV, mobile_half occlusion)
{
    mobile_half fresnel = mobile_half(1) - saturate(noV);
    fresnel *= fresnel;
    fresnel *= fresnel;
    mobile_half reduction = mobile_half(1) / (data.roughness2 + mobile_half(1));
    mobile_half3 specular = mobile_half3(reduction * lerp(data.specular, data.grazingTerm, fresnel));
    return (irradiance * data.diffuse + reflection * specular) * occlusion;
}

// The HDRP material's render state uses One for source RGB. Emulate URP's
// SrcAlpha modes here; URP Premultiply expects already-premultiplied assets.
mobile_half MobileOutputWeight(mobile_half alpha, float blendMode, bool preserveSpecular)
{
    return blendMode == 4 || preserveSpecular ? 1.0h : alpha;
}
#endif
