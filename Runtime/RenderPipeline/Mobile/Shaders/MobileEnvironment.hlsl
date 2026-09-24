// Included by the existing Sky shader, so previously serialized Mobile assets
// automatically retain this raster-only shader in Player builds.
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
#include "MobileSkySampling.hlsl"
TEXTURECUBE(_MobileEnvironmentSource); SAMPLER(sampler_MobileEnvironmentSource);
float4 _MobileEnvironmentFilter; // face, perceptual roughness, source resolution, unused
struct EnvironmentVaryings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };
EnvironmentVaryings EnvironmentVert(uint id : SV_VertexID)
{
    EnvironmentVaryings o;
    o.positionCS = GetFullScreenTriangleVertexPosition(id);
    o.uv = GetFullScreenTriangleTexCoord(id);
    return o;
}
float3 EnvironmentDirection(float2 uv)
{
    float2 p = uv * 2 - 1;
    uint face = (uint)_MobileEnvironmentFilter.x;
    if (face == 0) return normalize(float3(1, -p.y, -p.x));
    if (face == 1) return normalize(float3(-1, -p.y, p.x));
    if (face == 2) return normalize(float3(p.x, 1, p.y));
    if (face == 3) return normalize(float3(p.x, -1, -p.y));
    if (face == 4) return normalize(float3(p.x, -p.y, 1));
    return normalize(float3(-p.x, -p.y, -1));
}
float3 EnvironmentLocalToWorld(float3 v, float3 n)
{
    float3 t = normalize(cross(abs(n.z) < 0.999 ? float3(0,0,1) : float3(1,0,0), n));
    return t * v.x + cross(n, t) * v.y + n * v.z;
}
float2 EnvironmentXi(uint i)
{
    // Hammersley with a fixed 64-sample budget, no stochastic frame noise.
    uint reversed = ((i & 1u) << 5) | ((i & 2u) << 3) | ((i & 4u) << 1)
                  | ((i & 8u) >> 1) | ((i & 16u) >> 3) | ((i & 32u) >> 5);
    return float2((i + 0.5) / 64.0, reversed / 64.0);
}
half4 EnvironmentCapture(EnvironmentVaryings input) : SV_Target
{ return half4(min(MobileSkyRadiance(EnvironmentDirection(input.uv)) / 1024, 65504), 1); }
half4 EnvironmentDiffuse(EnvironmentVaryings input) : SV_Target
{
    float3 n = EnvironmentDirection(input.uv), sum = 0;
    for (uint i = 0; i < 64; ++i)
    {
        float2 xi = EnvironmentXi(i);
        float phi = TWO_PI * xi.y, r = sqrt(xi.x);
        float3 l = EnvironmentLocalToWorld(float3(r * cos(phi), r * sin(phi), sqrt(1 - xi.x)), n);
        sum += SAMPLE_TEXTURECUBE_LOD(_MobileEnvironmentSource, sampler_MobileEnvironmentSource, l, 1).rgb;
    }
    // Irradiance / PI, matching MobileEnvironmentBRDF's diffuse input.
    return half4(min(sum / 64, 65504), 1);
}
half4 EnvironmentSpecular(EnvironmentVaryings input) : SV_Target
{
    float3 n = EnvironmentDirection(input.uv);
    float roughness = _MobileEnvironmentFilter.y;
    if (roughness < 0.001) return half4(SAMPLE_TEXTURECUBE_LOD(_MobileEnvironmentSource, sampler_MobileEnvironmentSource, n, 0).rgb, 1);
    float a = roughness * roughness, a2 = a * a;
    float3 sum = 0; float weight = 0;
    for (uint i = 0; i < 64; ++i)
    {
        float2 xi = EnvironmentXi(i);
        float phi = TWO_PI * xi.x;
        float cosTheta = sqrt((1 - xi.y) / (1 + (a2 - 1) * xi.y));
        float sinTheta = sqrt(saturate(1 - cosTheta * cosTheta));
        float3 h = EnvironmentLocalToWorld(float3(sinTheta * cos(phi), sinTheta * sin(phi), cosTheta), n);
        float3 l = reflect(-n, h);
        float nl = saturate(dot(n, l));
        if (nl > 0)
        {
            float d = a2 / max(PI * pow(cosTheta * cosTheta * (a2 - 1) + 1, 2), 1e-8);
            float pdf = max(d * 0.25, 1e-5);
            float solidAngle = 4 * PI / (6 * _MobileEnvironmentFilter.z * _MobileEnvironmentFilter.z);
            float lod = max(0, 0.5 * log2(1 / (64 * pdf * solidAngle)));
            sum += SAMPLE_TEXTURECUBE_LOD(_MobileEnvironmentSource, sampler_MobileEnvironmentSource, l, lod).rgb * nl;
            weight += nl;
        }
    }
    return half4(min(sum / max(weight, 1e-5), 65504), 1);
}
