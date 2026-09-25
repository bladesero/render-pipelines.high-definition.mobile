#ifndef HDRP_MOBILE_EXTENDED_LIT_INCLUDED
#define HDRP_MOBILE_EXTENDED_LIT_INCLUDED
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/BSDF.hlsl"

float4 _MobileDiffusionShape[16], _MobileDiffusionTint[16], _MobileDiffusionWorld[16], _MobileDiffusionMeta[16];
int _MobileDiffusionCount;

struct MobileExtendedSurface
{
    float3 fresnel0;
    float coat;
    float3 tangent;
    float anisotropy;
    // SSS/Transmission: radius mask, normalized thickness, profile index, transmission mask.
    // Iridescence: mask, film thickness (micrometers), unused, unused.
    float4 features;
};

uint MobileDiffusionIndex(uint hash)
{
    uint2 splitHash = uint2(hash & 65535u, hash >> 16);
    for (uint i = 1; i < (uint)_MobileDiffusionCount; i++)
        if (all(splitHash == (uint2)_MobileDiffusionMeta[i].xy)) return i;
    return 0;
}

float3 MobileOrthonormalTangent(float3 tangent, float3 normal)
{
    tangent -= normal * dot(tangent, normal);
    if (dot(tangent, tangent) < 1e-6)
        tangent = cross(abs(normal.z) < 0.999 ? float3(0, 0, 1) : float3(0, 1, 0), normal);
    return normalize(tangent);
}

float3 MobileSSSPreColor(float3 albedo, uint profile)
{
    return _MobileDiffusionMeta[profile].z > 0 ? 1 : sqrt(saturate(albedo));
}

float3 MobileSSSPostColor(float3 albedo, uint profile)
{
    return _MobileDiffusionMeta[profile].z > 0 ? albedo : sqrt(saturate(albedo));
}

float MobileTransmissionThickness(float3 positionWS, uint profile, float authoredThickness, bool mainLight, bool receiveShadows)
{
    // Thin objects use authored thickness and ordinary shadowing. Regular objects
    // estimate thickness along the directional light using the existing atlas.
    if (_MobileDiffusionMeta[profile].w > 0.5 || !mainLight || !receiveShadows || _MobileShadowParams.x <= 0)
        return authoredThickness;
    int cascade = MobileSelectCascade(positionWS);
    if (cascade < 0) return authoredThickness;
    float3 projected = mul(_MobileCSMShadowMatrices6[cascade], float4(positionWS, 1)).xyz;
    float4 tile = MobileCascadeTile(cascade);
    float2 tileMin = tile.xy, tileMax = tile.zw;
    if (projected.z <= 0 || projected.z >= 1 || any(projected.xy < tileMin) || any(projected.xy >= tileMax))
        return authoredThickness;
    float sampled = LOAD_TEXTURE2D(_MobileShadowAtlas, int2(projected.xy / _MobileShadowParams.y)).r;
    #if UNITY_REVERSED_Z
        float separation = sampled - projected.z;
    #else
        float separation = projected.z - sampled;
    #endif
    float depthPerWorldUnit = length(_MobileCSMShadowMatrices6[cascade][2].xyz);
    float thickness = max(0, separation) / max(depthPerWorldUnit, 1e-8) * _MobileDiffusionWorld[profile].x * 1000;
    return max(authoredThickness, thickness);
}

float MobileExtendedMainShadow(MobileExtendedSurface surface, int type, float3 normal, float3 positionWS, bool receiveShadows)
{
    if (!receiveShadows) return 1;
    if ((type == 0 || type == 5) && _MobileDiffusionMeta[(uint)surface.features.z].w < 0.5
        && dot(normal, _MobileMainDirection.xyz) < 0) return 1;
    return MobileShadow(positionWS);
}

// Split diffuse and specular so raster diffusion never blurs emission or highlights.
void MobileEvaluateExtendedDirect(MobileBRDFData brdf, MobileExtendedSurface surface, int type,
    float3 normal, float3 view, float3 light, float specularEnabled, out float3 diffuse, out float3 specular,
    float3 positionWS, bool mainLight, bool receiveShadows)
{
    float nl = dot(normal, light), nv = max(saturate(dot(normal, view)), 1e-4);
    float3 halfDirection = SafeNormalize(light + view);
    float nh = saturate(dot(normal, halfDirection)), lh = saturate(dot(light, halfDirection));
    float3 fresnel = F_Schlick(surface.fresnel0, lh);
    if (type == 3)
        fresnel = lerp(fresnel, EvalIridescence(1, lh, surface.features.y, surface.fresnel0), surface.features.x);
    float roughness = max((float)brdf.roughness, 0.002);
    float distributionVisibility;
    if (type == 2)
    {
        float3 tangent = MobileOrthonormalTangent(surface.tangent, normal);
        float3 bitangent = cross(normal, tangent);
        float rt, rb;
        ConvertAnisotropyToClampRoughness(brdf.perceptualRoughness, surface.anisotropy, rt, rb);
        distributionVisibility = DV_SmithJointGGXAniso(dot(tangent, halfDirection), dot(bitangent, halfDirection), nh,
            dot(tangent, view), dot(bitangent, view), nv, dot(tangent, light), dot(bitangent, light), saturate(nl), rt, rb);
    }
    else distributionVisibility = DV_SmithJointGGX(nh, saturate(nl), nv, roughness);
    specular = fresnel * distributionVisibility * saturate(nl) * specularEnabled;
    diffuse = brdf.diffuse * DisneyDiffuse(nv, saturate(nl), dot(light, view), brdf.perceptualRoughness) * saturate(nl);
    if (type == 1)
    {
        // Enabling extra attachments must not change the established Standard BRDF.
        diffuse = brdf.diffuse * (INV_PI * saturate(nl));
        specular = brdf.specular * MobileDirectSpecular(brdf, normal, light, view) * (INV_PI * saturate(nl)) * specularEnabled;
    }
    if (type == 0 || type == 5)
    {
        uint profile = (uint)surface.features.z;
        float thickness = max(0, _MobileDiffusionWorld[profile].z + _MobileDiffusionWorld[profile].w * surface.features.y);
        thickness = MobileTransmissionThickness(positionWS, profile, thickness, mainLight, receiveShadows);
        // Burley transmission: the cached tint already includes the 1/4 factor.
        float3 exponent = exp(-thickness * _MobileDiffusionShape[profile].rgb / 3);
        float3 transmittance = _MobileDiffusionTint[profile].rgb * (exponent * exponent * exponent + 3 * exponent);
        diffuse += brdf.diffuse * transmittance * surface.features.w * INV_PI * saturate(-nl);
    }
    if (surface.coat > 0)
    {
        float coatFresnel = F_Schlick(0.04, lh) * surface.coat;
        diffuse *= 1 - coatFresnel;
        specular = specular * Sq(1 - coatFresnel)
            + coatFresnel * DV_SmithJointGGX(nh, saturate(nl), nv, 0.01) * saturate(nl) * specularEnabled;
    }
}
#endif
