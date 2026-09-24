#ifndef MOBILE_SKY_SAMPLING_INCLUDED
#define MOBILE_SKY_SAMPLING_INCLUDED
TEXTURECUBE(_MobileSky); SAMPLER(sampler_MobileSky);
float _MobileSkyIntensity, _MobileSkySunSize;
float4 _MobileSkyOptions, _MobileSkyTint, _MobileSkyTop, _MobileSkyMiddle, _MobileSkyBottom;
float4 _MobileMainDirection, _MobileMainColor;

float3 MobileSkyRadiance(float3 direction)
{
    float3 color = 0;
    if (_MobileSkyOptions.x == 1)
    {
        direction.xz = float2(_MobileSkyOptions.y * direction.x - _MobileSkyOptions.z * direction.z,
            _MobileSkyOptions.z * direction.x + _MobileSkyOptions.y * direction.z);
        color = SAMPLE_TEXTURECUBE_LOD(_MobileSky, sampler_MobileSky, direction, 0).rgb * _MobileSkyTint.rgb;
    }
    else if (_MobileSkyOptions.x > 1)
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
    return max(color * _MobileSkyIntensity, 0);
}
#endif
