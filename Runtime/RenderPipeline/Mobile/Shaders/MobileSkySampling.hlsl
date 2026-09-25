#ifndef MOBILE_SKY_SAMPLING_INCLUDED
#define MOBILE_SKY_SAMPLING_INCLUDED
TEXTURECUBE(_MobileSky); SAMPLER(sampler_MobileSky);
float _MobileSkyIntensity, _MobileSkySunSize;
float4 _MobileSkyOptions, _MobileSkyTint, _MobileSkyTop, _MobileSkyMiddle, _MobileSkyBottom;
float4 _MobileMainDirection, _MobileMainColor;
float4 _MobileSkyHorizonTint, _MobileSkyZenithTint, _MobileSkyPhysicalParameters;

// Near-ground, flat-horizon approximation. No atmospheric LUTs or extra texture samples.
float3 MobilePhysicalSkyRadiance(float3 direction)
{
    float elevation = clamp(direction.y, -1, 1);
    float sunHeight = clamp(_MobileMainDirection.y, -1, 1);
    float daylight = smoothstep(-0.1, 0.1, sunHeight);
    float3 sunTint = _MobileMainColor.rgb / max(0.0001, max(_MobileMainColor.r, max(_MobileMainColor.g, _MobileMainColor.b)));

    // Concentrate haze near the horizon instead of stretching it over the entire hemisphere.
    float haze = exp(-4.0 * max(elevation, 0));
    float3 sky = lerp(_MobileSkyTop.rgb, _MobileSkyMiddle.rgb, haze) * daylight;
    float luminance = dot(sky, float3(0.2126, 0.7152, 0.0722));
    sky = lerp(luminance.xxx, sky, _MobileSkyPhysicalParameters.z);

    // Desktop's angular Horizon/Zenith tint interpolation, with a stable linear limit.
    float angle = 1.0 - acos(saturate(elevation)) * (2.0 / PI);
    float power = _MobileSkyPhysicalParameters.x;
    float weight = abs(power) < 0.001 ? angle : (exp(power * angle) - 1.0) * _MobileSkyPhysicalParameters.y;
    sky *= lerp(_MobileSkyHorizonTint.rgb, _MobileSkyZenithTint.rgb, saturate(weight));

    // Approximate Lambertian ground illuminated by the sun plus diffuse sky.
    // Resolve scales sky radiance by 0.05 * sunlight; undo that scale for E * albedo / PI.
    float3 irradiance = sunTint * (0.75 * saturate(sunHeight)) + _MobileSkyTop.rgb * (0.08 * daylight);
    float3 ground = _MobileSkyBottom.rgb * irradiance / (PI * 0.05);
    float groundVisibility = 1.0 - exp(-24.0 * max(-elevation, 0));
    float3 color = lerp(sky, ground, groundVisibility);

    // The ground occludes the sun; do not paint a disk onto the lower hemisphere.
    if (elevation >= 0 && any(_MobileMainColor.rgb > 0))
    {
        float radius = max(0.001, _MobileSkySunSize);
        float disk = smoothstep(cos(radius), cos(radius * 0.8), dot(direction, _MobileMainDirection.xyz));
        color += disk * sunTint * 10;
    }
    return color;
}

float3 MobileSkyRadiance(float3 direction)
{
    float3 color = 0;
    if (_MobileSkyOptions.x == 1)
    {
        direction.xz = float2(_MobileSkyOptions.y * direction.x - _MobileSkyOptions.z * direction.z,
            _MobileSkyOptions.z * direction.x + _MobileSkyOptions.y * direction.z);
        color = SAMPLE_TEXTURECUBE_LOD(_MobileSky, sampler_MobileSky, direction, 0).rgb * _MobileSkyTint.rgb;
    }
    else if (_MobileSkyOptions.x == 4)
    {
        color = MobilePhysicalSkyRadiance(direction);
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
