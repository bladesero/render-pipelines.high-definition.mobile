Shader "Hidden/HDRP/Mobile/Sky"
{
    SubShader
    {
        Tags { "RenderPipeline"="HDRenderPipeline" }
        Pass
        {
            Name "MobileSky"
            Cull Off ZWrite Off ZTest LEqual
            HLSLPROGRAM
            #pragma target 3.5
            #pragma only_renderers d3d11 vulkan
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
            #include "MobileExposure.hlsl"
            float _MobileSkyCameraExposure;
            #include "MobileSkySampling.hlsl"
            float4x4 _MobileInvVP;
            float4 _MobileCameraPosition;
            float4 _MobileCameraForward;
            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };
            Varyings Vert(uint id : SV_VertexID)
            {
                Varyings o;
                o.positionCS = GetFullScreenTriangleVertexPosition(id, UNITY_RAW_FAR_CLIP_VALUE);
                o.uv = GetFullScreenTriangleTexCoord(id);
                return o;
            }
            half4 Frag(Varyings input) : SV_Target
            {
                float2 ndc = input.uv * 2 - 1;
                #if UNITY_UV_STARTS_AT_TOP
                    ndc.y = -ndc.y;
                #endif
                float4 world = mul(_MobileInvVP, float4(ndc, 0.5, 1));
                float3 direction = _MobileCameraForward.w > 0 ? _MobileCameraForward.xyz : normalize(world.xyz / world.w - _MobileCameraPosition.xyz);
                return half4(clamp(MobileSkyRadiance(direction) * (_MobileSkyCameraExposure > 0 ? MobileCurrentExposure() : 1), 0, 65504), 1);
            }
            ENDHLSL
        }
        Pass
        {
            Name "MobileEnvironmentCapture"
            Cull Off ZWrite Off ZTest Always
            HLSLPROGRAM
            #pragma target 3.5
            #pragma only_renderers d3d11 vulkan
            #pragma vertex EnvironmentVert
            #pragma fragment EnvironmentCapture
            #include "MobileEnvironment.hlsl"
            ENDHLSL
        }
        Pass
        {
            Name "MobileEnvironmentDiffuse"
            Cull Off ZWrite Off ZTest Always
            HLSLPROGRAM
            #pragma target 3.5
            #pragma only_renderers d3d11 vulkan
            #pragma vertex EnvironmentVert
            #pragma fragment EnvironmentDiffuse
            #include "MobileEnvironment.hlsl"
            ENDHLSL
        }
        Pass
        {
            Name "MobileEnvironmentSpecular"
            Cull Off ZWrite Off ZTest Always
            HLSLPROGRAM
            #pragma target 3.5
            #pragma only_renderers d3d11 vulkan
            #pragma vertex EnvironmentVert
            #pragma fragment EnvironmentSpecular
            #include "MobileEnvironment.hlsl"
            ENDHLSL
        }
    }
    Fallback Off
}
