Shader "Hidden/HDRP/Tests/MobileReflectionAtlas"
{
    SubShader
    {
        Pass
        {
            ZWrite Off ZTest Always Cull Off
            HLSLPROGRAM
            #pragma target 3.5
            #pragma only_renderers d3d11 vulkan
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.high-definition/Runtime/RenderPipeline/Mobile/Shaders/MobileInput.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/ImageBasedLighting.hlsl"
            #include "Packages/com.unity.render-pipelines.high-definition/Runtime/RenderPipeline/Mobile/Shaders/MobileReflection.hlsl"
            float4 _TestDirectionMip;
            int _TestProbeIndex;
            float4 Vert(uint id : SV_VertexID) : SV_POSITION { return GetFullScreenTriangleVertexPosition(id); }
            float4 Frag() : SV_Target { return float4(MobileSampleHDProbe(_TestProbeIndex, _TestDirectionMip.xyz, _TestDirectionMip.w), 1); }
            ENDHLSL
        }
    }
}
