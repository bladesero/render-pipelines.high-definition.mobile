#ifndef HDRP_MOBILE_BRDF_INCLUDED
#define HDRP_MOBILE_BRDF_INCLUDED

// Metallic-workflow equations adapted from Unity URP 14.0.9 BRDF.hlsl.
// Unity Technologies, Unity Companion License. Pinned source and test oracle:
// Tests/Editor/MobileURPReference/README.md. No URP runtime package dependency.
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/CommonMaterial.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/ImageBasedLighting.hlsl"

struct MobileBRDFData
{
    half3 diffuse, specular;
    half perceptualRoughness, roughness, roughness2, grazingTerm;
    half normalizationTerm, roughness2MinusOne;
};

MobileBRDFData MobileInitializeBRDF(half3 albedo, half metallic, half smoothness, half diffuseAlpha)
{
    MobileBRDFData data;
    half oneMinusReflectivity = 0.96h - metallic * 0.96h;
    data.diffuse = albedo * oneMinusReflectivity * diffuseAlpha;
    data.specular = lerp(half3(0.04h, 0.04h, 0.04h), albedo, metallic);
    data.perceptualRoughness = PerceptualSmoothnessToPerceptualRoughness(smoothness);
    data.roughness = max(PerceptualRoughnessToRoughness(data.perceptualRoughness), HALF_MIN_SQRT);
    data.roughness2 = max(data.roughness * data.roughness, HALF_MIN);
    data.grazingTerm = saturate(smoothness + (1.0h - oneMinusReflectivity));
    data.normalizationTerm = data.roughness * 4.0h + 2.0h;
    data.roughness2MinusOne = data.roughness2 - 1.0h;
    return data;
}

half MobileDirectSpecular(MobileBRDFData data, half3 normal, half3 light, half3 view)
{
    float3 halfDirection = SafeNormalize(float3(light) + float3(view));
    float noH = saturate(dot(float3(normal), halfDirection));
    half loH = half(saturate(dot(float3(light), halfDirection)));
    float d = noH * noH * data.roughness2MinusOne + 1.00001f;
    half term = data.roughness2 / ((d * d) * max(0.1h, loH * loH) * data.normalizationTerm);
#if REAL_IS_HALF
    term = clamp(term - HALF_MIN, 0.0h, 1000.0h);
#endif
    return term;
}

half3 MobileDirectBRDF(MobileBRDFData data, half3 normal, half3 view, half3 light)
{
    return (data.diffuse + data.specular * MobileDirectSpecular(data, normal, light, view))
        * saturate(dot(normal, light));
}

float3 MobileEnvironmentBRDF(MobileBRDFData data, float3 irradiance, float3 reflection, half noV, half occlusion)
{
    half fresnel = Pow4(1.0h - saturate(noV));
    float reduction = 1.0 / (data.roughness2 + 1.0);
    half3 specular = half3(reduction * lerp(data.specular, data.grazingTerm, fresnel));
    return (irradiance * data.diffuse + reflection * specular) * occlusion;
}

// The HDRP material's render state uses One for source RGB. Emulate URP's
// SrcAlpha modes here; URP Premultiply expects already-premultiplied assets.
half MobileOutputWeight(half alpha, float blendMode, bool preserveSpecular)
{
    return blendMode == 4 || preserveSpecular ? 1.0h : alpha;
}
#endif
