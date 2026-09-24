Shader "Hidden/HDRP/Mobile/Error"
{
    SubShader
    {
        Tags { "RenderPipeline"="HDRenderPipeline" }
        Pass
        {
            Name "MobileForward"
            Tags { "LightMode"="MobileForward" }
            HLSLPROGRAM
            #pragma target 3.5
            #pragma only_renderers d3d11 vulkan
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #include "MobileInput.hlsl"
            struct Attributes { float3 position : POSITION; UNITY_VERTEX_INPUT_INSTANCE_ID };
            float4 Vert(Attributes input) : SV_POSITION
            {
                UNITY_SETUP_INSTANCE_ID(input);
                return mul(_MobileVP, mul(UNITY_MATRIX_M, float4(input.position, 1)));
            }
            half4 Frag() : SV_Target { return half4(1, 0, 1, 1); }
            ENDHLSL
        }
    }
    Fallback Off
}
