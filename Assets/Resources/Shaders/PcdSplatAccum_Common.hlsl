#ifndef PCD_SPLATACCUM_COMMON_INCLUDED
#define PCD_SPLATACCUM_COMMON_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

StructuredBuffer<float3> _Positions;
StructuredBuffer<uint> _Colors;
int _HasColor;

float _PointSize;
float _KernelSharpness;
float _Gaussian;
float4x4 _LocalToWorld;
float _NodeFade;

// distance-color params (ÇÊ¿ä ½Ã)
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
