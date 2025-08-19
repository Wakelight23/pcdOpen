Shader"Custom/PointBillboard_Instanced"
{
    Properties
    {
        _PointSize ("World Point Size", Float) = 0.02
        _PointSizeMin ("Min Screen Size", Float) = 0.5
        _PointSizeMax ("Max Screen Size", Float) = 6.0
        _AlphaScale ("Alpha Scale", Float) = 1.0
        _LutTex ("LUT (1D)", 2D) = "white" {}
        _LutMin ("LUT Min", Float) = 0.0
        _LutMax ("LUT Max", Float) = 1.0
    }
    SubShader
    {
        Tags { "Queue"="Geometry" "RenderType"="Opaque" }
        Pass
        {
ZWrite On

ZTest LEqual

Cull Off

Blend Off

            CGPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing

#include "UnityCG.cginc"

StructuredBuffer<float3> _Positions;
StructuredBuffer<uint> _Colors;

float _PointSize;
float _PointSizeMin;
float _PointSizeMax;
float _AlphaScale;

sampler2D _LutTex;
float _LutMin, _LutMax;
int _HasColor; // 1: use _Colors, 0: use LUT->RGB or white

struct appdata
{
    float3 posOS : POSITION; // quad vertex (-0.5~0.5)
    float2 uv : TEXCOORD0;
    uint iid : SV_InstanceID;
};

struct v2f
{
    float4 posCS : SV_POSITION;
    float2 uv : TEXCOORD0;
    float alpha : TEXCOORD1;
    uint iid : TEXCOORD2;
};

            // 거리 기반 화면 픽셀 크기 근사: 카메라 거리로 world size를 스크린 스페이스로 환산
float ComputeScreenSize(float3 worldPos, float worldSize)
{
    float3 view = UnityWorldToViewPos(worldPos);
    float dist = max(1e-3, abs(view.z));
                // 대략적 스케일: fov/픽셀 변환 대신 상수 스케일로 근사
                // 필요 시 정확한 투영 기반 계산으로 개선 가능
    float px = worldSize / dist * 1200.0; // 경험적 상수, 해상도/카메라에 맞춰 튜닝
    return clamp(px, _PointSizeMin, _PointSizeMax);
}

v2f Vert(appdata v)
{
    v2f o;
    uint id = v.iid;
    float3 wp = _Positions[id];
                // 필요 시 좌표 재매핑 적용
                // wp = float3(wp.x, wp.z, wp.y);

                // 카메라 공간 기준 right/up으로 billboard 확장
    float3 camRight = UNITY_MATRIX_V._m00_m01_m02;
    float3 camUp = UNITY_MATRIX_V._m10_m11_m12;
    camRight = normalize(camRight);
    camUp = normalize(camUp);

    float sizePx = ComputeScreenSize(wp, _PointSize);
                // 화면 픽셀 크기를 NDC로 정확히 반영하려면 ScreenParams와 proj를 사용해 오프셋 계산 필요
                // 간소화: world에서 비율 오프셋(근사). 가까운 거리에서 자연스럽고, 멀리서 clamp에 의해 제한됨.
    float scaleWorld = _PointSize; // world 기반 기본 스케일
    float2 offs = v.posOS.xy; // -0.5~0.5

    float3 offset = camRight * offs.x * scaleWorld + camUp * offs.y * scaleWorld;
    float3 wv = wp + offset;

    o.posCS = UnityWorldToClipPos(wv);
    o.uv = v.uv;
    o.alpha = saturate(sizePx / _PointSizeMax) * _AlphaScale; // 멀수록 작아지는 알파
    o.iid = id;
    return o;
}

fixed4 UnpackRGBA8(uint c)
{
    return fixed4(
                    (c & 0xFF) * (1.0 / 255.0),
                    ((c >> 8) & 0xFF) * (1.0 / 255.0),
                    ((c >> 16) & 0xFF) * (1.0 / 255.0),
                    ((c >> 24) & 0xFF) * (1.0 / 255.0));
}

fixed4 Frag(v2f i) : SV_Target
{
    fixed4 col;
    if (_HasColor == 1)
    {
        col = UnpackRGBA8(_Colors[i.iid]);
    }
    else
    {
                    // LUT 기반 intensity → RGB (예: Colors buffer 대신 intensity buffer나 기존 RGBA의 R만 사용한다면 셰이더에 맞게 변경)
                    // 여기서는 LUT 샘플만 예시로 표기: u 좌표에 intensity 정규화된 값 전달 필요
        float u = 0.5; // TODO: 실제 intensity->0..1 전달
        col = tex2D(_LutTex, float2(u, 0.5));
        col.a = 1.0;
    }

                // 원형 마스크(clip)로 디스크 형태
    float2 p = i.uv * 2 - 1; // -1..1
    float r2 = dot(p, p);
    if (r2 > 1.0)
        discard;

    col.a *= i.alpha;
    return col;
}
            ENDCG
        }
    }
}
