Shader "Hidden/HDRP/Tests/MobileDepthCoverage"
{
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            HLSLPROGRAM
            #pragma target 3.5
            #pragma only_renderers d3d11 vulkan
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
            TEXTURE2D(_TestDepth); SAMPLER(sampler_TestDepth);
            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };
            Varyings Vert(uint id : SV_VertexID)
            { Varyings o; o.positionCS = GetFullScreenTriangleVertexPosition(id); o.uv = GetFullScreenTriangleTexCoord(id); return o; }
            float4 Frag(Varyings input) : SV_Target
            {
                float depth = SAMPLE_TEXTURE2D(_TestDepth, sampler_TestDepth, input.uv).r;
                float coverage = abs(depth - UNITY_RAW_FAR_CLIP_VALUE) > 0.00001 ? 1 : 0;
                return float4(coverage.xxx, 1);
            }
            ENDHLSL
        }
    }
}
