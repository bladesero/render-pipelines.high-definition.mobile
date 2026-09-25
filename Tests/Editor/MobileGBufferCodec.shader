Shader "Hidden/HDRP/MobileGBufferCodecTest"
{
    SubShader
    {
        Cull Off ZTest Always ZWrite Off
        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Packing.hlsl"
        #include "Packages/com.unity.render-pipelines.high-definition/Runtime/RenderPipeline/Mobile/Shaders/MobileGBuffer.hlsl"
        float4 Vert(uint id : SV_VertexID) : SV_POSITION { return GetFullScreenTriangleVertexPosition(id); }
        float3 Normal(uint i)
        {
            if (i < 6) return (i < 2 ? float3(1,0,0) : i < 4 ? float3(0,1,0) : float3(0,0,1)) * ((i & 1) ? -1 : 1);
            float z = 1 - 2 * ((i - 6 + 0.5) / 506.0), phi = i * 2.39996323;
            return float3(sqrt(1-z*z) * float2(cos(phi), sin(phi)), z);
        }
        float4 ColorValue(uint i) { float x = i / 511.0; return float4(x, 1-x, x*x, 0.1 + 0.75*x); }
        struct Encoded { float4 baseMetal : SV_Target0; float4 normalSmoothAO : SV_Target1; };
        Encoded Encode(float4 p : SV_POSITION)
        {
            uint i = (uint)p.x; float4 c = ColorValue(i);
            MobileGBufferOutput g = MobileEncodeGBuffer(c.rgb, c.a, Normal(i), c.r, c.g, 0);
            Encoded o; o.baseMetal = g.baseMetal; o.normalSmoothAO = g.normalSmoothAO; return o;
        }
        TEXTURE2D(_Base); TEXTURE2D(_Normal);
        float4 Decode(float4 p : SV_POSITION) : SV_Target
        {
            uint i = (uint)p.x;
            float4 c = ColorValue(i), b = LOAD_TEXTURE2D(_Base, uint2(i, 0)), n = LOAD_TEXTURE2D(_Normal, uint2(i, 0));
            float3 error = abs(b.rgb - c.rgb);
            float3 decoded = MobileDecodeNormal(n.rg);
            // FP16 normalization has a small length error. Measure direction
            // independently, while still rejecting length drift above 0.2%.
            float angularError = acos(clamp(dot(Normal(i), normalize(decoded)), -1, 1)) * 180 / PI;
            return float4(abs(length(decoded) - 1) > 0.002 ? 180 : angularError,
                Max3(error.r, error.g, error.b), abs(b.a - c.a), max(abs(n.b-c.r), abs(n.a-c.g)));
        }
        ENDHLSL
        Pass
        {
            HLSLPROGRAM
            #pragma target 4.5
            #pragma only_renderers d3d11 vulkan
            #pragma vertex Vert
            #pragma fragment Encode
            ENDHLSL
        }
        Pass
        {
            HLSLPROGRAM
            #pragma target 4.5
            #pragma only_renderers d3d11 vulkan
            #pragma vertex Vert
            #pragma fragment Decode
            ENDHLSL
        }
    }
}
