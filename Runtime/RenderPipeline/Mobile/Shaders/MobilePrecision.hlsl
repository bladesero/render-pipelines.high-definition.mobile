#ifndef HDRP_MOBILE_PRECISION_INCLUDED
#define HDRP_MOBILE_PRECISION_INCLUDED

// Explicit minimum-precision types for Mobile arithmetic only. Do not redefine
// half/float/real or change shared Core library functions and constant buffers.
#if defined(MOBILE_SHADOW)
// Caster coverage (including alpha remapping/clipping) must stay FP32 alongside
// its world position, normal bias and clip position. Only Mobile caster variants
// opt out of minimum precision; surface lighting retains its FP16 arithmetic.
typedef float mobile_half;
typedef float2 mobile_half2;
typedef float3 mobile_half3;
typedef float4 mobile_half4;
#else
typedef min16float mobile_half;
typedef min16float2 mobile_half2;
typedef min16float3 mobile_half3;
typedef min16float4 mobile_half4;
#endif

mobile_half3 MobileNormalizeSurface(mobile_half3 direction)
{
    return direction * rsqrt(max(dot(direction, direction), mobile_half(6.103515625e-5)));
}

mobile_half3 MobileFresnel(mobile_half3 f0, mobile_half cosine)
{
    mobile_half x = mobile_half(1) - saturate(cosine);
    mobile_half x2 = x * x;
    return f0 + (mobile_half3(1, 1, 1) - f0) * (x2 * x2 * x);
}

#endif
