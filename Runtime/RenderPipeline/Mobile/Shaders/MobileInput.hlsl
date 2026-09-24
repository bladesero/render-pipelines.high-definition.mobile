#ifndef HDRP_MOBILE_INPUT_INCLUDED
#define HDRP_MOBILE_INPUT_INCLUDED
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Packing.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/EntityLighting.hlsl"

// Engine feature blocks; their ordering and full-precision storage are required by SRP Batcher.
CBUFFER_START(UnityPerDraw)
float4x4 unity_ObjectToWorld;
float4x4 unity_WorldToObject;
float4 unity_LODFade;
float4 unity_WorldTransformParams;
float4 unity_RenderingLayer;
float4 unity_LightData;
float4 unity_LightIndices[2];
float4 unity_ProbesOcclusion;
float4 unity_SpecCube0_HDR;
float4 unity_SpecCube1_HDR;
float4 unity_SpecCube0_BoxMax;
float4 unity_SpecCube0_BoxMin;
float4 unity_SpecCube0_ProbePosition;
float4 unity_SpecCube1_BoxMax;
float4 unity_SpecCube1_BoxMin;
float4 unity_SpecCube1_ProbePosition;
float4 unity_LightmapST;
float4 unity_DynamicLightmapST;
float4 unity_SHAr, unity_SHAg, unity_SHAb;
float4 unity_SHBr, unity_SHBg, unity_SHBb, unity_SHC;
CBUFFER_END

float4x4 _MobileVP, _MobileInvVP;
float4x4 unity_MatrixVP;
float4 _MobileCameraPosition;
float4 _MobileCameraForward;
#include "MobileExposure.hlsl"
float _MobileDeExposure;
float4 _MobileMainDirection, _MobileMainColor;
float4 _MobileLightPositions[32], _MobileLightColors[32];
float4 _MobileLightSpots[32], _MobileLightAttenuations[32];
int _MobileLightsPerObject;
float4x4 _MobileShadowMatrices[2];
float4 _MobileShadowSpheres[2], _MobileShadowParams;
float _MobileShadowNormalBias;
float4 _MobileFog, _MobileFogColor;
TEXTURE2D_SHADOW(_MobileShadowAtlas);
SAMPLER_CMP(sampler_MobileShadowAtlas);
TEXTURE2D(unity_Lightmap); SAMPLER(samplerunity_Lightmap);
TEXTURE2D(unity_LightmapInd);
TEXTURECUBE(unity_SpecCube0); SAMPLER(samplerunity_SpecCube0);
TEXTURECUBE(unity_SpecCube1);
TEXTURECUBE(_MobileEnvironmentDiffuse); SAMPLER(sampler_MobileEnvironmentDiffuse);
TEXTURECUBE(_MobileEnvironmentReflection); SAMPLER(sampler_MobileEnvironmentReflection);
float4 _MobileEnvironmentParams; // enabled, baked SH set available, reflection max mip, storage scale
float4 _MobileIndirectMultiplier; // diffuse, reflections, local probe intensity
float4 unity_Lightmap_HDR;

#define UNITY_MATRIX_M unity_ObjectToWorld
#define UNITY_MATRIX_I_M unity_WorldToObject
#define UNITY_MATRIX_VP _MobileVP
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/UnityInstancing.hlsl"
#endif
