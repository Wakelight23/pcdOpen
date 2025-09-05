#ifndef PCD_SPLATACCUM_COMMON_INCLUDED
#define PCD_SPLATACCUM_COMMON_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

StructuredBuffer<float3> _Positions;
StructuredBuffer<uint> _Colors;
int _HasColor;

float _PointSize;
float4x4 _LocalToWorld;
float _NodeFade;
float _Gaussian; // 0=none,1=gaussian
float _KernelSharpness; // 1..10 (or more)
float _KernelShape; // 0=circle,1=square,2=gaussian
float _GaussianSigma; // in pixels mapped to r-space
float _GaussianHardK; // hard threshold 0..1 (e.g., 0.01~0.1)

// distance-color params (필요 시)
float _UseDistanceColor;
float4 _NearColor;
float4 _FarColor;
float _NearDist;
float _FarDist;
float _DistMode;

struct appdata
{
    uint vertexID : SV_VertexID;
    uint instID : SV_InstanceID;
};

struct v2f
{
    float4 posCS : SV_POSITION;
    float2 uv : TEXCOORD0;
    nointerpolation uint pid : TEXCOORD1;
    float2 uvTex : TEXCOORD2;
    nointerpolation float invDepth : TEXCOORD3;
    float3 posWS : TEXCOORD4;
};

inline float3 UnpackRGBA8(uint c)
{
    const float s = 1.0 / 255.0;
    return float3((c & 0xFF) * s, ((c >> 8) & 0xFF) * s, ((c >> 16) & 0xFF) * s);
}

inline void ComputeKernelHard(float2 uvCorner, out float hardMask, out float softWeight)
{
    float2 p01 = uvCorner * 0.5 + 0.5;
    float2 d = p01 - 0.5;
    float r2 = dot(d, d) * 4.0;

    // softWeight: 가장자리 소프트닝(조명/색 보정용)
    if (_Gaussian > 0.5)
    {
        float sigma2 = max(1e-3, 0.25 / max(_KernelSharpness, 1e-3));
        softWeight = exp(-r2 / sigma2);
    }
    else
    {
        float dlin = saturate(1.0 - sqrt(r2));
        softWeight = pow(dlin, max(_KernelSharpness, 1.0));
    }

    // hardMask: 불투명 유지용 하드 컷아웃
    if (_KernelShape < 0.5)
    {
        // circle
        hardMask = (r2 <= 1.0) ? 1.0 : 0.0;
    }
    else if (_KernelShape < 1.5)
    {
        // square
        float ax = abs(uvCorner.x);
        float ay = abs(uvCorner.y);
        hardMask = (ax <= 1.0 && ay <= 1.0) ? 1.0 : 0.0;
    }
    else
    {
        // gaussian footprint as hard cut with threshold τ
        float sigmaPx2 = max(1e-4, _GaussianSigma * _GaussianSigma);
        float g = exp(-r2 / (2.0 * sigmaPx2)); // 0..1
        float tau = saturate(_GaussianHardK); // e.g., 0.05
        hardMask = (g >= tau) ? 1.0 : 0.0;
    }
}

inline v2f Vert(appdata v)
{
    v2f o;
    uint pid = v.instID;

    float3 lp = _Positions[pid];
    float4 wp = mul(_LocalToWorld, float4(lp, 1));
    o.posWS = wp.xyz;

    float3 vp = mul(UNITY_MATRIX_V, wp).xyz;
    float4 cs = mul(UNITY_MATRIX_P, float4(vp, 1));

    float2 ndcPerPixel = 2.0 / _ScreenParams.xy;
    float2 corner = float2((v.vertexID & 1) ? +1 : -1, (v.vertexID & 2) ? +1 : -1);
    float2 quadOffsetNdc = corner * (_PointSize * 0.5) * ndcPerPixel;

    float2 ndc = cs.xy / max(cs.w, 1e-5);
    float4 posCS;
    posCS.xy = (ndc + quadOffsetNdc) * cs.w;
    posCS.zw = cs.zw;

    o.posCS = posCS;
    o.uv = corner;
    o.pid = pid;

    float2 uv01 = ndc * 0.5 + 0.5;
    o.uvTex = uv01;

    float depth01 = saturate(cs.z / cs.w * 0.5 + 0.5);
#if UNITY_REVERSED_Z
        depth01 = 1.0 - depth01;
#endif
    o.invDepth = (depth01 <= 1e-6) ? 0.0 : (1.0 / depth01);
    return o;
}

#endif // PCD_SPLATACCUM_COMMON_INCLUDED
