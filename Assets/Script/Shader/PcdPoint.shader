Shader"Custom/PcdPoint"
{
    Properties
    {
        _PointSize("Point Size (world units)", Float) = 0.02
    }
    SubShader
    {
        Tags { "Queue"="Geometry" "RenderType"="Opaque" }
        Pass
        {
ZWrite On

ZTest LEqual

Cull Off
            // 필요 시 AlphaTest/Blend로 반투명 처리 가능

            CGPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 4.5

#include "UnityCG.cginc"

StructuredBuffer<float3> _Positions;
StructuredBuffer<uint> _Colors; // RGBA 8:8:8:8
int _HasColor;
float _PointSize;

struct appdata
{
    uint vertexID : SV_VertexID;
};

struct v2f
{
    float4 pos : SV_POSITION;
    float3 worldPos : TEXCOORD0;
    float2 ptCoord : TEXCOORD1; // point sprite 좌표 대체(픽셀에서 생성)
    uint vid : TEXCOORD2;
    float psize : TEXCOORD3; // clip-space point size
};

            // 카메라 거리 기반 화면 픽셀 크기를 계산하기 위해 클립공간 point size를 추정
float ComputePointSizePixels(float3 worldPos, float pointWorld)
{
                // 월드 길이(미터)를 뷰 공간 z로 나눠 화면 픽셀로 변환
    float3 view = UnityWorldToViewPos(worldPos);
    float dist = max(1e-4, abs(view.z));
                // 카메라 수직 FOV에 따른 픽셀 변환(대략적)
    float fovy = UNITY_PI / 180.0 * _ProjectionParams.x; // _ProjectionParams.x = 1/tan(fov/2)? 파이프라인마다 상이
                // 간단화: 프로젝트 매트릭스로 변환 후 ddx/ddy로 추정할 수도 있으나, 여기서는 고정 픽셀 크기 대신
                // 스크린 해상도와 투영행렬을 이용해 근사한다.
                // 더 정확히 하려면 geometry shader로 quad 확장 추천.
    return max(1.0, pointWorld * (1.0 / dist) * 1000.0); // 경험적 스케일, 필요 시 조정
}

v2f Vert(appdata v)
{
    v2f o;
    uint id = v.vertexID;
    float3 wp = _Positions[id];
    // 원하는 매핑
    wp = float3(wp.x, wp.z, wp.y);
    o.pos = UnityWorldToClipPos(wp);
    // float3 wp = _Positions[id];
    o.worldPos = wp;

    float4 clip = UnityWorldToClipPos(wp);
    o.pos = clip;
    o.vid = id;

                // SV_ViewportArrayIndex 등 멀티뷰포트 미사용 가정
                // 실제 point size 제어는 geometry shader가 가장 안정적이나,
                // 여기서는 픽셀 셰이더에서 거리 기반 마스크만 수행하고, point 원은 픽셀에서 만듦.
    o.psize = 1; // 사용 안 함(빌트인에서는 gl_PointSize가 제한적)
    return o;
}

fixed4 UnpackRGBA8(uint c)
{
    float r = ((c >> 0) & 0xFF) / 255.0;
    float g = ((c >> 8) & 0xFF) / 255.0;
    float b = ((c >> 16) & 0xFF) / 255.0;
    float a = ((c >> 24) & 0xFF) / 255.0;
    return fixed4(r, g, b, a);
}

fixed4 Frag(v2f i) : SV_Target
{
                // 화면 픽셀에서 원형 디스크 마스크
                // 가까운 근사: 스크린 공간에서 world pointSize 기반 반지름 픽셀 계산
    float ptPx = ComputePointSizePixels(i.worldPos, _PointSize);
                // 현재 픽셀의 스크린 좌표 구해서 중심까지 거리 계산은 추가 작업이 필요하므로,
                // 간단한 방식: 작은 크기의 포인트는 핀처럼 보임 → geometry shader 권장.
                // 여기서는 고정 alpha 소프트 원을 출력해 시각적으로 디스크처럼 보이게 함.

    fixed4 col = fixed4(1, 1, 1, 1);
    if (_HasColor == 1)
    {
        col = UnpackRGBA8(_Colors[i.vid]);
        col.a = 1.0;
    }

                // 소프트 에지: 깊이와 point size를 기반으로 페더링
    float alpha = saturate(ptPx * 0.02); // 가까울수록 굵게, 멀어질수록 얇게
    col.a *= alpha;

    return col;
}
            ENDCG
        }
    }
}
