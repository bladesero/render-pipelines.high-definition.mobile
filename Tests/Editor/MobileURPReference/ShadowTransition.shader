Shader "Hidden/HDRP/Tests/MobileShadowTransition"
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
            #include "Packages/com.unity.render-pipelines.high-definition/Runtime/RenderPipeline/Mobile/Shaders/MobileInput.hlsl"
            #include "Packages/com.unity.render-pipelines.high-definition/Runtime/RenderPipeline/Mobile/Shaders/MobileShadow.hlsl"
            struct Varyings { float4 positionCS : SV_POSITION; };
            Varyings Vert(uint id : SV_VertexID)
            { Varyings o; o.positionCS = GetFullScreenTriangleVertexPosition(id); return o; }
            float4 Frag(Varyings input) : SV_Target
            {
                float radius = (input.positionCS.x - 0.5) / 100;
                float weight = MobileCascadeWeight(radius * radius, 1, _MobileShadowParams.w);
                return float4(weight, MobileShadow(float3(radius, 0, 0)), 0, 1);
            }
            ENDHLSL
        }
    }
}
