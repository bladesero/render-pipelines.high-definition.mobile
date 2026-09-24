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
            TEXTURECUBE(_MobileSky); SAMPLER(sampler_MobileSky);
            float4x4 _MobileInvVP;
            float4 _MobileCameraPosition;
            float4 _MobileCameraForward;
            float _MobileSkyIntensity;
            float4 _MobileSkyOptions, _MobileSkyTint, _MobileSkyTop, _MobileSkyMiddle, _MobileSkyBottom, _MobileMainDirection, _MobileMainColor;
            float _MobileSkySunSize;
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
                float3 color;
                if (_MobileSkyOptions.x == 1)
                {
                    float2 rotated = float2(_MobileSkyOptions.y * direction.x - _MobileSkyOptions.z * direction.z,
                        _MobileSkyOptions.z * direction.x + _MobileSkyOptions.y * direction.z);
                    direction.xz = rotated;
                    color = SAMPLE_TEXTURECUBE(_MobileSky, sampler_MobileSky, direction).rgb * _MobileSkyTint.rgb;
                }
                else
                {
                    float height = direction.y * _MobileSkyOptions.w;
                    color = lerp(_MobileSkyMiddle.rgb, _MobileSkyBottom.rgb, saturate(-height));
                    color = lerp(color, _MobileSkyTop.rgb, saturate(height));
                    if (_MobileSkyOptions.x == 3 && any(_MobileMainColor.rgb > 0))
                    {
                        float radius = max(0.001, _MobileSkySunSize);
                        float disk = smoothstep(cos(radius), cos(radius * 0.8), dot(direction, _MobileMainDirection.xyz));
                        float3 tint = _MobileMainColor.rgb / max(0.0001, max(_MobileMainColor.r, max(_MobileMainColor.g, _MobileMainColor.b)));
                        color += disk * tint * 10;
                    }
                }
                return half4(clamp(color * _MobileSkyIntensity, 0, 65504), 1);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
