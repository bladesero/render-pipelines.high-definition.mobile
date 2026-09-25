Shader "Hidden/HDRP/Mobile/Deferred"
{
    Properties { _MobileStencilRef("Stencil", Int) = 16 }
    SubShader
    {
        Tags { "RenderPipeline"="HDRenderPipeline" }
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            Name "MobileDeferredSampled"
            Blend One One
            Stencil { Ref [_MobileStencilRef] ReadMask 127 WriteMask 0 Comp Equal Pass Keep }
            HLSLPROGRAM
            #pragma multi_compile_local _ MOBILE_EXTENDED_LIT
            #pragma multi_compile_local _ MOBILE_RECEIVE_SHADOWS_OFF
            #pragma multi_compile_local _ MOBILE_SPECULAR_HIGHLIGHTS_OFF
            #pragma target 3.5
            #pragma only_renderers d3d11 vulkan
            #pragma vertex DeferredVert
            #pragma fragment DeferredFrag
            #include "MobileDeferred.hlsl"
            ENDHLSL
        }
        Pass
        {
            Name "MobileDeferredNative"
            Blend One One
            Stencil { Ref [_MobileStencilRef] ReadMask 127 WriteMask 0 Comp Equal Pass Keep }
            HLSLPROGRAM
            #pragma multi_compile_local _ MOBILE_EXTENDED_LIT
            #pragma multi_compile_local _ MOBILE_RECEIVE_SHADOWS_OFF
            #pragma multi_compile_local _ MOBILE_SPECULAR_HIGHLIGHTS_OFF
            #pragma target 4.5
            #pragma only_renderers d3d11 vulkan
            #pragma vertex DeferredVert
            #pragma fragment DeferredFrag
            #define MOBILE_NATIVE_INPUT 1
            #include "MobileDeferred.hlsl"
            ENDHLSL
        }
        Pass
        {
            Name "MobileDeferredFogSampled"
            Blend SrcAlpha OneMinusSrcAlpha
            Stencil { Ref [_MobileStencilRef] ReadMask 112 WriteMask 0 Comp Equal Pass Keep }
            HLSLPROGRAM
            #pragma target 3.5
            #pragma only_renderers d3d11 vulkan
            #pragma vertex DeferredVert
            #pragma fragment DeferredFog
            #include "MobileDeferred.hlsl"
            ENDHLSL
        }
        Pass
        {
            Name "MobileDeferredFogNative"
            Blend SrcAlpha OneMinusSrcAlpha
            Stencil { Ref [_MobileStencilRef] ReadMask 112 WriteMask 0 Comp Equal Pass Keep }
            HLSLPROGRAM
            #pragma target 4.5
            #pragma only_renderers d3d11 vulkan
            #pragma vertex DeferredVert
            #pragma fragment DeferredFog
            #define MOBILE_NATIVE_INPUT 1
            #include "MobileDeferred.hlsl"
            ENDHLSL
        }
        Pass
        {
            Name "MobileDeferredCopyDepth"
            HLSLPROGRAM
            #pragma target 3.5
            #pragma only_renderers d3d11 vulkan
            #pragma vertex DeferredVert
            #pragma fragment DeferredCopyDepth
            #include "MobileDeferred.hlsl"
            ENDHLSL
        }
        Pass
        {
            Name "MobileDeferredVolumeSampled"
            Cull Front
            Blend One One
            Stencil { Ref [_MobileStencilRef] ReadMask 127 WriteMask 0 Comp Equal Pass Keep }
            HLSLPROGRAM
            #pragma multi_compile_local _ MOBILE_EXTENDED_LIT
            #pragma multi_compile_local _ MOBILE_RECEIVE_SHADOWS_OFF
            #pragma multi_compile_local _ MOBILE_SPECULAR_HIGHLIGHTS_OFF
            #pragma target 3.5
            #pragma only_renderers d3d11 vulkan
            #pragma vertex DeferredVolumeVert
            #pragma fragment DeferredFrag
            #include "MobileDeferred.hlsl"
            ENDHLSL
        }
        Pass
        {
            Name "MobileDeferredVolumeNative"
            Cull Front
            Blend One One
            Stencil { Ref [_MobileStencilRef] ReadMask 127 WriteMask 0 Comp Equal Pass Keep }
            HLSLPROGRAM
            #pragma multi_compile_local _ MOBILE_EXTENDED_LIT
            #pragma multi_compile_local _ MOBILE_RECEIVE_SHADOWS_OFF
            #pragma multi_compile_local _ MOBILE_SPECULAR_HIGHLIGHTS_OFF
            #pragma target 4.5
            #pragma only_renderers d3d11 vulkan
            #pragma vertex DeferredVolumeVert
            #pragma fragment DeferredFrag
            #define MOBILE_NATIVE_INPUT 1
            #include "MobileDeferred.hlsl"
            ENDHLSL
        }
        Pass
        {
            Name "MobileNativeInputProbe"
            HLSLPROGRAM
            #pragma target 4.5
            #pragma only_renderers d3d11 vulkan
            #pragma vertex DeferredVert
            #pragma fragment DeferredProbe
            #define MOBILE_NATIVE_INPUT 1
            #include "MobileDeferred.hlsl"
            ENDHLSL
        }
    }
}
