Shader "Hidden/HDRP/Mobile/Composite"
{
    SubShader
    {
        Tags { "RenderPipeline"="HDRenderPipeline" }
        Cull Off ZWrite Off ZTest Always
        HLSLINCLUDE
        #pragma target 3.5
        #pragma only_renderers d3d11 vulkan
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/PhysicalCamera.hlsl"
        #include "MobileExposure.hlsl"
        float4 _MobileMeterParams, _MobileAdaptation, _MobileMeterSize;
        TEXTURE2D(_MobileSource); SAMPLER(sampler_LinearClamp);
        TEXTURE2D(_MobileBloom);
        float4 _MobileSourceSize, _MobileAdjustments, _MobilePostOptions, _MobileColorFilter;
        float _MobileHue, _MobileBloomIntensity;
        TEXTURE2D(_MobileEditorDepth); SAMPLER(sampler_PointClamp);
        float _MobileEditorDepthFlip;
        struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };
        Varyings Vert(uint id : SV_VertexID)
        {
            Varyings o;
            o.positionCS = GetFullScreenTriangleVertexPosition(id);
            o.uv = GetFullScreenTriangleTexCoord(id);
            return o;
        }
        half3 Grade(float2 uv)
        {
            float3 color = SAMPLE_TEXTURE2D(_MobileSource, sampler_LinearClamp, uv).rgb;
            color += SAMPLE_TEXTURE2D(_MobileBloom, sampler_LinearClamp, uv).rgb * _MobileBloomIntensity;
            color *= _MobileAdjustments.x * _MobileColorFilter.rgb;
            color = max(0, (color - 0.18) * _MobileAdjustments.y + 0.18);
            float3 hsv = RgbToHsv(color);
            hsv.x = frac(hsv.x + _MobileHue);
            color = HsvToRgb(hsv);
            color = max(0, lerp(Luminance(color).xxx, color, _MobileAdjustments.z));
            if (_MobilePostOptions.y == 1) color = NeutralTonemap(color);
            else if (_MobilePostOptions.y == 2) color = AcesTonemap(unity_to_ACES(color));
            return color;
        }
        half4 Composite(Varyings input) : SV_Target
        {
            float2 uv = input.uv;
            if (_MobilePostOptions.z > 0) uv.y = 1 - uv.y;
            half3 center = Grade(uv);
            if (_MobilePostOptions.x > 0)
            {
                float2 pixel = _MobileSourceSize.xy;
                half3 nw = Grade(uv + float2(-1, -1) * pixel);
                half3 ne = Grade(uv + float2(1, -1) * pixel);
                half3 sw = Grade(uv + float2(-1, 1) * pixel);
                half3 se = Grade(uv + pixel);
                float lc = Luminance(center), lnw = Luminance(nw), lne = Luminance(ne), lsw = Luminance(sw), lse = Luminance(se);
                float low = min(lc, min(min(lnw, lne), min(lsw, lse)));
                float high = max(lc, max(max(lnw, lne), max(lsw, lse)));
                if (high - low > max(0.0312, high * 0.125))
                {
                    float2 direction = float2(-((lnw + lne) - (lsw + lse)), (lnw + lsw) - (lne + lse));
                    float reduce = max((lnw + lne + lsw + lse) * (0.25 * 0.125), 1.0 / 128);
                    direction = clamp(direction / (min(abs(direction.x), abs(direction.y)) + reduce), -8, 8) * pixel;
                    half3 a = 0.5 * (Grade(uv + direction * (-1.0 / 6)) + Grade(uv + direction * (1.0 / 6)));
                    half3 b = a * 0.5 + 0.25 * (Grade(uv - direction * 0.5) + Grade(uv + direction * 0.5));
                    float lb = Luminance(b);
                    center = lb < low || lb > high ? a : b;
                }
            }
            return half4(center, 1);
        }
        float3 Filter(float2 uv)
        {
            float2 p = _MobileSourceSize.xy;
            return 0.25 * (SAMPLE_TEXTURE2D(_MobileSource, sampler_LinearClamp, uv + p * float2(-1,-1)).rgb
                + SAMPLE_TEXTURE2D(_MobileSource, sampler_LinearClamp, uv + p * float2(1,-1)).rgb
                + SAMPLE_TEXTURE2D(_MobileSource, sampler_LinearClamp, uv + p * float2(-1,1)).rgb
                + SAMPLE_TEXTURE2D(_MobileSource, sampler_LinearClamp, uv + p).rgb);
        }
        half4 Threshold(Varyings input) : SV_Target
        {
            float3 color = Filter(input.uv);
            return half4(max(color - 1, 0), 1);
        }
        half4 Downsample(Varyings input) : SV_Target { return half4(Filter(input.uv), 1); }
        float2 Meter(Varyings input) : SV_Target
        {
            float exposure = max(LOAD_TEXTURE2D(_MobileExposureTexture, int2(0, 0)).r, 1e-20);
            float3 color = SAMPLE_TEXTURE2D_LOD(_MobileSource, sampler_LinearClamp, input.uv, 0).rgb;
            float luminance = clamp(Luminance(color) / exposure, 1e-4, 1e20);
            float ev = ComputeEV100FromAvgLuminance(luminance, _MobileMeterParams.w);
            float weight = _MobileMeterSize.z > 0 ? 1 - saturate(length(input.uv - 0.5)) : 1;
            return float2(ev * weight, weight);
        }
        float2 ReduceMeter(float2 uv)
        {
            int2 basePixel = int2(uv / _MobileMeterSize.xy) & ~1;
            return 0.25 * (LOAD_TEXTURE2D(_MobileSource, basePixel).rg
                + LOAD_TEXTURE2D(_MobileSource, basePixel + int2(1,0)).rg
                + LOAD_TEXTURE2D(_MobileSource, basePixel + int2(0,1)).rg
                + LOAD_TEXTURE2D(_MobileSource, basePixel + int2(1,1)).rg);
        }
        float2 Reduce(Varyings input) : SV_Target { return ReduceMeter(input.uv); }
        float2 Adapt(Varyings input) : SV_Target
        {
            float2 meter = ReduceMeter(input.uv);
            float previousEV = LOAD_TEXTURE2D(_MobileExposureTexture, int2(0,0)).g;
            float ev = meter.x / max(meter.y, 1e-8) - _MobileMeterParams.x;
            if (_MobileAdaptation.w > 0)
                ev = ComputeLuminanceAdaptation(previousEV, ev, _MobileAdaptation.x, _MobileAdaptation.y, _MobileAdaptation.z);
            ev = clamp(ev, _MobileMeterParams.y, _MobileMeterParams.z);
            return float2(ConvertEV100ToExposure(ev, _MobileMeterSize.w), ev);
        }
        float CopyEditorDepth(Varyings input) : SV_Depth
        {
            float2 uv = input.uv;
            if (_MobileEditorDepthFlip > 0) uv.y = 1 - uv.y;
            return SAMPLE_TEXTURE2D_LOD(_MobileEditorDepth, sampler_PointClamp, uv, 0).r;
        }
        ENDHLSL
        Pass
        {
            Name "MobileComposite"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Composite
            ENDHLSL
        }
        Pass
        {
            Name "MobileBloomThreshold"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Threshold
            ENDHLSL
        }
        Pass
        {
            Name "MobileBloomDownsample"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Downsample
            ENDHLSL
        }
        Pass
        {
            Name "MobileExposureMeter"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Meter
            ENDHLSL
        }
        Pass
        {
            Name "MobileExposureReduce"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Reduce
            ENDHLSL
        }
        Pass
        {
            Name "MobileExposureAdapt"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Adapt
            ENDHLSL
        }
        Pass
        {
            Name "MobileEditorDepth"
            ColorMask 0 ZWrite On ZTest Always
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment CopyEditorDepth
            ENDHLSL
        }
    }
    Fallback Off
}
