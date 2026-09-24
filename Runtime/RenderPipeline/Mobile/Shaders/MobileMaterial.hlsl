#ifndef HDRP_MOBILE_MATERIAL_INCLUDED
#define HDRP_MOBILE_MATERIAL_INCLUDED
#include "MobileInput.hlsl"
#include "MobileBRDF.hlsl"
#include "MobileShadow.hlsl"
#if defined(MOBILE_LIT)
#include "Packages/com.unity.render-pipelines.high-definition/Runtime/Material/Lit/LitProperties.hlsl"
#else
#include "Packages/com.unity.render-pipelines.high-definition/Runtime/Material/Unlit/UnlitProperties.hlsl"
#endif

struct MobileAttributes
{
    float3 positionOS : POSITION;
    float3 normalOS : NORMAL;
    float4 tangentOS : TANGENT;
    float2 uv : TEXCOORD0;
    float2 lightmapUV : TEXCOORD1;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};
struct MobileVaryings
{
    float4 positionCS : SV_POSITION;
    float3 positionWS : TEXCOORD0;
    float3 normalWS : TEXCOORD1;
    float4 tangentWS : TEXCOORD2;
    float2 uv : TEXCOORD3;
    float2 lightmapUV : TEXCOORD4;
    float2 detailUV : TEXCOORD5;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

MobileVaryings MobileVert(MobileAttributes input)
{
    MobileVaryings output = (MobileVaryings)0;
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);
    output.positionWS = mul(UNITY_MATRIX_M, float4(input.positionOS, 1)).xyz;
    output.normalWS = SafeNormalize(mul(input.normalOS, (float3x3)UNITY_MATRIX_I_M));
    output.tangentWS = float4(SafeNormalize(mul((float3x3)UNITY_MATRIX_M, input.tangentOS.xyz)), input.tangentOS.w * unity_WorldTransformParams.w);
#if defined(MOBILE_SHADOW)
    output.positionWS += output.normalWS * _MobileShadowNormalBias * (1 - saturate(dot(output.normalWS, _MobileMainDirection.xyz)));
#endif
    #if defined(MOBILE_PICKING) || defined(MOBILE_SELECTION) || defined(MOBILE_SHADOW)
        output.positionCS = mul(unity_MatrixVP, float4(output.positionWS, 1));
    #else
        output.positionCS = mul(_MobileVP, float4(output.positionWS, 1));
    #endif
#if defined(MOBILE_SHADOW)
    #if UNITY_REVERSED_Z
        output.positionCS.z = min(output.positionCS.z, output.positionCS.w * UNITY_NEAR_CLIP_VALUE);
    #else
        output.positionCS.z = max(output.positionCS.z, output.positionCS.w * UNITY_NEAR_CLIP_VALUE);
    #endif
#endif
    output.uv = input.uv;
    output.detailUV = input.lightmapUV;
    output.lightmapUV = input.lightmapUV * unity_LightmapST.xy + unity_LightmapST.zw;
    return output;
}

half4 MobileBase(float2 uv)
{
#if defined(MOBILE_LIT)
    half4 color = SAMPLE_TEXTURE2D(_BaseColorMap, sampler_BaseColorMap, uv * _BaseColorMap_ST.xy + _BaseColorMap_ST.zw) * _BaseColor;
#else
    half4 color = SAMPLE_TEXTURE2D(_UnlitColorMap, sampler_UnlitColorMap, uv * _UnlitColorMap_ST.xy + _UnlitColorMap_ST.zw) * _UnlitColor;
#endif
    color.a = lerp(_AlphaRemapMin, _AlphaRemapMax, color.a);
#if defined(_ALPHATEST_ON)
    float cutoff = _AlphaCutoff;
    #if defined(MOBILE_SHADOW) && defined(MOBILE_LIT)
        cutoff = _UseShadowThreshold > 0.5 ? _AlphaCutoffShadow : cutoff;
    #endif
    clip(color.a - cutoff);
#endif
    return color;
}

float3 MobileBaked(float2 uv, half3 normal)
{
#if defined(LIGHTMAP_ON)
    half4 encoded = SAMPLE_TEXTURE2D(unity_Lightmap, samplerunity_Lightmap, uv);
    #if defined(UNITY_LIGHTMAP_FULL_HDR)
        float3 lighting = encoded.rgb;
    #else
        float3 lighting = DecodeLightmap(encoded, float4(LIGHTMAP_HDR_MULTIPLIER, LIGHTMAP_HDR_EXPONENT, 0, 0));
    #endif
    #if defined(DIRLIGHTMAP_COMBINED)
        half4 direction = SAMPLE_TEXTURE2D(unity_LightmapInd, samplerunity_Lightmap, uv);
        lighting *= (dot(normal, direction.xyz - 0.5) + 0.5) / max(direction.w, 0.0001);
    #endif
    return lighting;
#else
    if (_MobileEnvironmentParams.x > 0 && _MobileEnvironmentParams.y < 0.5)
        return SAMPLE_TEXTURECUBE_LOD(_MobileEnvironmentDiffuse, sampler_MobileEnvironmentDiffuse, normal, 0).rgb * _MobileEnvironmentParams.w;
    float4 coefficients[7] = { unity_SHAr, unity_SHAg, unity_SHAb, unity_SHBr, unity_SHBg, unity_SHBb, unity_SHC };
    return max(0, SampleSH9(coefficients, normal));
#endif
}

float3 MobileBoxDirection(float3 direction, float3 position, float4 probePosition, float4 boxMin, float4 boxMax)
{
    if (probePosition.w <= 0) return direction;
    float3 safeDirection = (direction >= 0 ? 1 : -1) * max(abs(direction), 1e-6);
    float3 distance = ((direction > 0 ? boxMax.xyz : boxMin.xyz) - position) / safeDirection;
    return position - probePosition.xyz + direction * min(distance.x, min(distance.y, distance.z));
}

float MobileEnvironmentProbeWeight(float3 position, float4 boxMin, float4 boxMax)
{
    // Default sky bindings have no finite influence box. Never detect validity by
    // sampled brightness: a black local probe can be intentional (e.g. an interior).
    // Unity's default sky probe uses an effectively infinite AABB. It is not a
    // local probe and must leave its contribution to our environment cache.
    if (any(abs(boxMax.xyz) > 1e19) || any(abs(boxMin.xyz) > 1e19) || any(boxMax.xyz <= boxMin.xyz)) return 0;
    float3 distances = min(position - boxMin.xyz, boxMax.xyz - position);
    float distance = min(distances.x, min(distances.y, distances.z));
    return boxMax.w > 0 ? saturate(distance / boxMax.w) : (distance >= 0 ? 1 : 0);
}

half4 MobileFrag(MobileVaryings input, FRONT_FACE_TYPE frontFace : FRONT_FACE_SEMANTIC) : SV_Target
{
    UNITY_SETUP_INSTANCE_ID(input);
#if (defined(MOBILE_SELECTION) || defined(MOBILE_PICKING)) && !defined(_DOUBLESIDED_ON)
    // Shared editor passes use Cull Off for desktop selection. Match the mobile
    // single-sided surface without changing desktop ShaderLab render state.
    if (!IS_FRONT_VFACE(frontFace, true, false)) discard;
#endif
    half4 color = MobileBase(input.uv);
#if defined(MOBILE_SHADOW)
    return 0;
#elif defined(MOBILE_PICKING)
    return _SelectionID;
#elif defined(MOBILE_SELECTION)
    return float4(_ObjectId, _PassValue, 1, 1);
#else
    half outputWeight = 1;
    bool preserveSpecular = false;
#if defined(_SURFACE_TYPE_TRANSPARENT)
    #if defined(MOBILE_LIT)
        preserveSpecular = _BlendMode != 4 && _EnableBlendModePreserveSpecularLighting > 0;
    #endif
    outputWeight = MobileOutputWeight(color.a, _BlendMode, preserveSpecular);
#endif
    float3 emission = _EmissiveColor;
    #if defined(_EMISSIVE_COLOR_MAP)
        emission *= SAMPLE_TEXTURE2D(_EmissiveColorMap, sampler_EmissiveColorMap, input.uv * _EmissiveColorMap_ST.xy + _EmissiveColorMap_ST.zw).rgb;
    #endif
    #if defined(MOBILE_LIT)
        emission *= lerp(half3(1, 1, 1), color.rgb, _AlbedoAffectEmissive);
        half3 n = input.normalWS;
        half3 normalTS = half3(0, 0, 1);
        #if defined(_NORMALMAP) || defined(_DETAIL_MAP)
            half3 t = input.tangentWS.xyz;
            // Missing/parallel tangents have no meaningful UV frame. Use a stable
            // orthogonal frame instead of normalizing zero (also safe for skinned meshes).
            if (dot(cross(n, t), cross(n, t)) < 1e-6 || abs(input.tangentWS.w) < 0.5)
                t = SafeNormalize(cross(abs(n.z) < 0.999 ? float3(0, 0, 1) : float3(0, 1, 0), n));
            half handedness = input.tangentWS.w < 0 ? -1 : 1;
            half3 b = cross(n, t) * handedness;
            #if defined(_NORMALMAP)
                normalTS = UnpackNormalScale(SAMPLE_TEXTURE2D(_NormalMap, sampler_NormalMap, input.uv * _BaseColorMap_ST.xy + _BaseColorMap_ST.zw), _NormalScale);
            #endif
        #endif
        #if defined(_DETAIL_MAP)
            float2 detailUV = input.uv * _UVDetailsMappingMask.x + input.detailUV * _UVDetailsMappingMask.y;
            detailUV = detailUV * _DetailMap_ST.xy + _DetailMap_ST.zw;
            if (_LinkDetailsWithBase > 0) detailUV = detailUV * _BaseColorMap_ST.xy + _BaseColorMap_ST.zw;
            half4 detail = SAMPLE_TEXTURE2D(_DetailMap, sampler_DetailMap, detailUV);
            half detailMask = 1;
            #if defined(_MASKMAP)
                detailMask = SAMPLE_TEXTURE2D(_MaskMap, sampler_MaskMap, input.uv * _BaseColorMap_ST.xy + _BaseColorMap_ST.zw).b;
            #endif
            half detailAlbedo = detail.r * 2 - 1;
            half speed = saturate(abs(detailAlbedo) * _DetailAlbedoScale);
            half3 overlay = lerp(sqrt(max(color.rgb, 0)), detailAlbedo < 0 ? 0 : 1, speed * speed);
            color.rgb = lerp(color.rgb, saturate(overlay * overlay), detailMask);
            half3 detailNormal = UnpackNormalAG(detail, _DetailNormalScale);
            normalTS = lerp(normalTS, BlendNormalRNM(normalTS, detailNormal), detailMask);
        #endif
        #if defined(_DOUBLESIDED_ON)
            normalTS *= IS_FRONT_VFACE(frontFace, half3(1, 1, 1), _DoubleSidedConstants.xyz);
        #endif
        #if defined(_NORMALMAP) || defined(_DETAIL_MAP)
            n = SafeNormalize(t * normalTS.x + b * normalTS.y + n * normalTS.z);
        #else
            n = SafeNormalize(n * normalTS.z);
        #endif
        half metallic = _Metallic, smoothness = _Smoothness, ao = 1;
        #if defined(_MASKMAP)
            half4 mask = SAMPLE_TEXTURE2D(_MaskMap, sampler_MaskMap, input.uv * _BaseColorMap_ST.xy + _BaseColorMap_ST.zw);
            metallic = lerp(_MetallicRemapMin, _MetallicRemapMax, mask.r);
            smoothness = lerp(_SmoothnessRemapMin, _SmoothnessRemapMax, mask.a);
            ao = lerp(_AORemapMin, _AORemapMax, mask.g);
        #endif
        #if defined(_DETAIL_MAP)
            half ds = detail.b * 2 - 1;
            smoothness = lerp(smoothness, saturate(lerp(smoothness, ds < 0 ? 0 : 1, saturate(abs(ds) * _DetailSmoothnessScale))), detailMask);
        #endif
        half3 v = _MobileCameraForward.w > 0 ? -_MobileCameraForward.xyz : SafeNormalize(_MobileCameraPosition.xyz - input.positionWS);
        MobileBRDFData brdf = MobileInitializeBRDF(color.rgb, metallic, smoothness, preserveSpecular ? color.a : 1.0h);
        half3 reflected = reflect(-v, n);
        float mip = PerceptualRoughnessToMipmapLevel(brdf.perceptualRoughness);
        float3 direction0 = MobileBoxDirection(reflected, input.positionWS, unity_SpecCube0_ProbePosition, unity_SpecCube0_BoxMin, unity_SpecCube0_BoxMax);
        half4 env = SAMPLE_TEXTURECUBE_LOD(unity_SpecCube0, samplerunity_SpecCube0, direction0, mip);
        float3 indirectSpecular = DecodeHDREnvironment(env, unity_SpecCube0_HDR);
        float blend = unity_SpecCube1_HDR.x > 0 ? saturate(unity_SpecCube0_BoxMin.w) : 1;
        if (_MobileEnvironmentParams.x < 0.5 && blend < 0.99999)
        {
            float3 direction1 = MobileBoxDirection(reflected, input.positionWS, unity_SpecCube1_ProbePosition, unity_SpecCube1_BoxMin, unity_SpecCube1_BoxMax);
            half4 env1 = SAMPLE_TEXTURECUBE_LOD(unity_SpecCube1, samplerunity_SpecCube0, direction1, mip);
            indirectSpecular = lerp(DecodeHDREnvironment(env1, unity_SpecCube1_HDR), indirectSpecular, blend);
        }
        if (_MobileEnvironmentParams.x > 0)
        {
            float w0 = MobileEnvironmentProbeWeight(input.positionWS, unity_SpecCube0_BoxMin, unity_SpecCube0_BoxMax);
            float w1 = MobileEnvironmentProbeWeight(input.positionWS, unity_SpecCube1_BoxMin, unity_SpecCube1_BoxMax);
            // Native metadata stores the relative importance sign in BoxMin1.w.
            if (unity_SpecCube1_BoxMin.w > 0) w1 *= 1 - w0;
            else if (unity_SpecCube1_BoxMin.w < 0) w0 *= 1 - w1;
            float sum = w0 + w1, normalization = max(sum, 1);
            float3 local0 = DecodeHDREnvironment(env, unity_SpecCube0_HDR);
            float3 direction1 = MobileBoxDirection(reflected, input.positionWS, unity_SpecCube1_ProbePosition, unity_SpecCube1_BoxMin, unity_SpecCube1_BoxMax);
            float3 local1 = DecodeHDREnvironment(SAMPLE_TEXTURECUBE_LOD(unity_SpecCube1, samplerunity_SpecCube0, direction1, mip), unity_SpecCube1_HDR);
            float3 sky = SAMPLE_TEXTURECUBE_LOD(_MobileEnvironmentReflection, sampler_MobileEnvironmentReflection, reflected,
                brdf.perceptualRoughness * _MobileEnvironmentParams.z).rgb * _MobileEnvironmentParams.w;
            indirectSpecular = (local0 * w0 + local1 * w1) / normalization * _MobileIndirectMultiplier.z + sky * (1 - saturate(sum));
        }
        else indirectSpecular *= _MobileIndirectMultiplier.z;
        float3 result = MobileEnvironmentBRDF(brdf, MobileBaked(input.lightmapUV, n) * _MobileIndirectMultiplier.x, indirectSpecular * _MobileIndirectMultiplier.y, dot(n, v), ao);
        result += MobileDirectBRDF(brdf, n, v, _MobileMainDirection.xyz) * _MobileMainColor.rgb * MobileShadow(input.positionWS);
        uint count = min((uint)unity_LightData.y, (uint)_MobileLightsPerObject);
        for (uint i = 0; i < count; i++)
        {
            uint index = (uint)unity_LightIndices[0][i];
            float3 delta = _MobileLightPositions[index].xyz - input.positionWS;
            float distanceSquared = max(dot(delta, delta), HALF_MIN);
            half3 l = delta * rsqrt(distanceSquared);
            float rangeFade = saturate(1 - pow(distanceSquared * _MobileLightPositions[index].w, 2));
            float spot = saturate(dot(l, _MobileLightSpots[index].xyz) * _MobileLightAttenuations[index].x + _MobileLightAttenuations[index].y);
            result += MobileDirectBRDF(brdf, n, v, l) * _MobileLightColors[index].rgb
                * (rangeFade * rangeFade * spot * spot / distanceSquared);
        }
        result *= MobileCurrentExposure();
    #else
        // HDRP Unlit base color is display-referred; only emission has an
        // exposure weight. Applying camera EV to the whole Unlit result is wrong.
        float3 result = color.rgb * _MobileDeExposure;
    #endif
    result += emission * lerp(1.0, MobileCurrentExposure(), _EmissiveExposureWeight);
    float fog = _MobileFog.x * saturate((distance(input.positionWS, _MobileCameraPosition.xyz) - _MobileFog.y) * _MobileFog.z);
    #if defined(_SURFACE_TYPE_TRANSPARENT)
        result = lerp(result, _MobileFogColor.rgb * MobileCurrentExposure(), fog);
        result *= outputWeight;
    #else
        result = lerp(result, _MobileFogColor.rgb * MobileCurrentExposure(), fog);
        color.a = 1;
    #endif
    // Physical intensities can exceed the storage range at specular peaks. Accumulate
    // in float and bound the final value before writing the fixed RGBA16F target.
    return half4(clamp(result, 0, 65504), color.a);
#endif
}
#endif
