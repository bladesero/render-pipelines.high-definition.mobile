Shader "Hidden/HDRP/Tests/URP14MaterialParity"
{
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        HLSLINCLUDE
        #pragma target 4.5
        #pragma only_renderers d3d11 vulkan
        #pragma multi_compile_local _ _ALPHAPREMULTIPLY_ON
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
        #include "Packages/com.unity.render-pipelines.high-definition/Runtime/RenderPipeline/Mobile/Shaders/MobileBRDF.hlsl"
        #include "BRDF.hlsl"
        int _Mode;
        struct Varyings { float4 positionCS : SV_POSITION; };
        Varyings Vert(uint id : SV_VertexID)
        { Varyings o; o.positionCS = GetFullScreenTriangleVertexPosition(id); return o; }
        float4 Evaluate(Varyings input, bool reference)
        {
            uint2 cell = uint2(input.positionCS.xy);
            half smoothness = (cell.x % 16) / 15.0h;
            half metallic = (cell.x / 16) / 7.0h;
            half alpha = (cell.y % 4) / 3.0h;
            float angle = (cell.y / 4) * (1.56 / 15);
            half3 n = half3(0,0,1), v = half3(sin(angle),0,cos(angle));
            half3 l = normalize(half3(sin(angle * 0.7), sin(angle * 0.3), cos(angle * 0.7)));
            half3 albedo = half3(0.7,0.23,0.05);
            half3 irradiance = half3(0.15,0.28,0.4), reflection = half3(0.8,0.7,0.3);
            half3 emission = half3(0.02,0.04,0.08);
            half ao = 0.2h + 0.8h * alpha;
            bool preserve = _Mode == 2 || _Mode == 4;
            bool premultiply = _Mode == 6 || _Mode == 8;
            float3 direct, gi;
            if (reference)
            {
                BRDFData brdf;
                InitializeBRDFData(albedo, metallic, 0, smoothness, alpha, brdf);
                direct = DirectBRDF(brdf, n, l, v) * saturate(dot(n,l));
                gi = EnvironmentBRDF(brdf, irradiance, reflection, Pow4(1.0h - saturate(dot(n,v)))) * ao;
            }
            else
            {
                MobileBRDFData brdf = MobileInitializeBRDF(albedo, metallic, smoothness, preserve ? alpha : 1.0h);
                direct = MobileDirectBRDF(brdf, n, v, l);
                gi = MobileEnvironmentBRDF(brdf, irradiance, reflection, dot(n,v), ao);
            }
            if (_Mode == 0) return float4(direct,1);
            if (_Mode == 1) return float4(gi,1);
            float3 color = _Mode >= 7 ? albedo : direct + gi + emission;
            // Effective source contribution after URP's One or SrcAlpha render state.
            half weight = reference ? (premultiply || preserve ? 1 : alpha)
                : MobileOutputWeight(alpha, premultiply ? 4 : 0, preserve);
            return float4(color * weight, alpha);
        }
        float4 Reference(Varyings input) : SV_Target { return Evaluate(input,true); }
        float4 Mobile(Varyings input) : SV_Target { return Evaluate(input,false); }
        ENDHLSL
        Pass
        {
            Name "URP14Reference"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Reference
            ENDHLSL
        }
        Pass
        {
            Name "Mobile"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Mobile
            ENDHLSL
        }
    }
}
