#ifndef HDRP_MOBILE_INSTANCE_LIGHTING_INCLUDED
#define HDRP_MOBILE_INSTANCE_LIGHTING_INCLUDED

// Explicit draws never depend on the previous native renderer's UnityPerDraw data.
#if defined(MOBILE_INSTANCE_DATA)
UNITY_INSTANCING_BUFFER_START(MobileInstanceData)
    UNITY_DEFINE_INSTANCED_PROP(float4, _MobileInstanceLights)
    UNITY_DEFINE_INSTANCED_PROP(float4, _MobileInstanceInfo)
    UNITY_DEFINE_INSTANCED_PROP(float4, _MobileInstanceLightmapST)
UNITY_INSTANCING_BUFFER_END(MobileInstanceData)
// Split the payload to keep each constant buffer below Vulkan's 16 KiB minimum.
UNITY_INSTANCING_BUFFER_START(MobileInstanceSH)
    UNITY_DEFINE_INSTANCED_PROP(float4, _MobileInstanceSHAr)
    UNITY_DEFINE_INSTANCED_PROP(float4, _MobileInstanceSHAg)
    UNITY_DEFINE_INSTANCED_PROP(float4, _MobileInstanceSHAb)
    UNITY_DEFINE_INSTANCED_PROP(float4, _MobileInstanceSHBr)
    UNITY_DEFINE_INSTANCED_PROP(float4, _MobileInstanceSHBg)
    UNITY_DEFINE_INSTANCED_PROP(float4, _MobileInstanceSHBb)
    UNITY_DEFINE_INSTANCED_PROP(float4, _MobileInstanceSHC)
UNITY_INSTANCING_BUFFER_END(MobileInstanceSH)
#define MOBILE_INSTANCE(prop) UNITY_ACCESS_INSTANCED_PROP(MobileInstanceData, prop)
#define MOBILE_INSTANCE_SH(prop) UNITY_ACCESS_INSTANCED_PROP(MobileInstanceSH, prop)
#undef unity_LightmapST
#define unity_LightmapST MOBILE_INSTANCE(_MobileInstanceLightmapST)
#undef unity_SHAr
#undef unity_SHAg
#undef unity_SHAb
#undef unity_SHBr
#undef unity_SHBg
#undef unity_SHBb
#undef unity_SHC
#define unity_SHAr MOBILE_INSTANCE_SH(_MobileInstanceSHAr)
#define unity_SHAg MOBILE_INSTANCE_SH(_MobileInstanceSHAg)
#define unity_SHAb MOBILE_INSTANCE_SH(_MobileInstanceSHAb)
#define unity_SHBr MOBILE_INSTANCE_SH(_MobileInstanceSHBr)
#define unity_SHBg MOBILE_INSTANCE_SH(_MobileInstanceSHBg)
#define unity_SHBb MOBILE_INSTANCE_SH(_MobileInstanceSHBb)
#define unity_SHC MOBILE_INSTANCE_SH(_MobileInstanceSHC)
TEXTURECUBE(_MobileInstanceDefaultReflection);
SAMPLER(sampler_MobileInstanceDefaultReflection);
float4 _MobileInstanceDefaultReflectionHDR;
#endif

float3 MobileInstanceMainColor()
{
#if defined(MOBILE_INSTANCE_DATA)
    return _MobileMainColor.rgb * MOBILE_INSTANCE(_MobileInstanceInfo).x;
#else
    return _MobileMainColor.rgb;
#endif
}
uint MobileInstanceLightCount()
{
#if defined(MOBILE_INSTANCE_DATA)
    return min(4u, (uint)_MobileLightsPerObject);
#else
    return min((uint)unity_LightData.y, (uint)_MobileLightsPerObject);
#endif
}
int MobileInstanceLightIndex(uint index)
{
#if defined(MOBILE_INSTANCE_DATA)
    return (int)MOBILE_INSTANCE(_MobileInstanceLights)[index];
#else
    return (int)unity_LightIndices[0][index];
#endif
}
#endif
