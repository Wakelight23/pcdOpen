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
            // �ʿ� �� AlphaTest/Blend�� ������ ó�� ����

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
    float2 ptCoord : TEXCOORD1; // point sprite ��ǥ ��ü(�ȼ����� ����)
    uint vid : TEXCOORD2;
    float psize : TEXCOORD3; // clip-space point size
};

            // ī�޶� �Ÿ� ��� ȭ�� �ȼ� ũ�⸦ ����ϱ� ���� Ŭ������ point size�� ����
float ComputePointSizePixels(float3 worldPos, float pointWorld)
{
                // ���� ����(����)�� �� ���� z�� ���� ȭ�� �ȼ��� ��ȯ
    float3 view = UnityWorldToViewPos(worldPos);
    float dist = max(1e-4, abs(view.z));
                // ī�޶� ���� FOV�� ���� �ȼ� ��ȯ(�뷫��)
    float fovy = UNITY_PI / 180.0 * _ProjectionParams.x; // _ProjectionParams.x = 1/tan(fov/2)? ���������θ��� ����
                // ����ȭ: ������Ʈ ��Ʈ������ ��ȯ �� ddx/ddy�� ������ ���� ������, ���⼭�� ���� �ȼ� ũ�� ���
                // ��ũ�� �ػ󵵿� ��������� �̿��� �ٻ��Ѵ�.
                // �� ��Ȯ�� �Ϸ��� geometry shader�� quad Ȯ�� ��õ.
    return max(1.0, pointWorld * (1.0 / dist) * 1000.0); // ������ ������, �ʿ� �� ����
}

v2f Vert(appdata v)
{
    v2f o;
    uint id = v.vertexID;
    float3 wp = _Positions[id];
    // ���ϴ� ����
    wp = float3(wp.x, wp.z, wp.y);
    o.pos = UnityWorldToClipPos(wp);
    // float3 wp = _Positions[id];
    o.worldPos = wp;

    float4 clip = UnityWorldToClipPos(wp);
    o.pos = clip;
    o.vid = id;

                // SV_ViewportArrayIndex �� ��Ƽ����Ʈ �̻�� ����
                // ���� point size ����� geometry shader�� ���� �������̳�,
                // ���⼭�� �ȼ� ���̴����� �Ÿ� ��� ����ũ�� �����ϰ�, point ���� �ȼ����� ����.
    o.psize = 1; // ��� �� ��(��Ʈ�ο����� gl_PointSize�� ������)
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
                // ȭ�� �ȼ����� ���� ��ũ ����ũ
                // ����� �ٻ�: ��ũ�� �������� world pointSize ��� ������ �ȼ� ���
    float ptPx = ComputePointSizePixels(i.worldPos, _PointSize);
                // ���� �ȼ��� ��ũ�� ��ǥ ���ؼ� �߽ɱ��� �Ÿ� ����� �߰� �۾��� �ʿ��ϹǷ�,
                // ������ ���: ���� ũ���� ����Ʈ�� ��ó�� ���� �� geometry shader ����.
                // ���⼭�� ���� alpha ����Ʈ ���� ����� �ð������� ��ũó�� ���̰� ��.

    fixed4 col = fixed4(1, 1, 1, 1);
    if (_HasColor == 1)
    {
        col = UnpackRGBA8(_Colors[i.vid]);
        col.a = 1.0;
    }

                // ����Ʈ ����: ���̿� point size�� ������� �����
    float alpha = saturate(ptPx * 0.02); // �������� ����, �־������� ���
    col.a *= alpha;

    return col;
}
            ENDCG
        }
    }
}
