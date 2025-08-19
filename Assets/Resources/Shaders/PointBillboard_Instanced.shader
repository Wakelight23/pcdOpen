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

            // �Ÿ� ��� ȭ�� �ȼ� ũ�� �ٻ�: ī�޶� �Ÿ��� world size�� ��ũ�� �����̽��� ȯ��
float ComputeScreenSize(float3 worldPos, float worldSize)
{
    float3 view = UnityWorldToViewPos(worldPos);
    float dist = max(1e-3, abs(view.z));
                // �뷫�� ������: fov/�ȼ� ��ȯ ��� ��� �����Ϸ� �ٻ�
                // �ʿ� �� ��Ȯ�� ���� ��� ������� ���� ����
    float px = worldSize / dist * 1200.0; // ������ ���, �ػ�/ī�޶� ���� Ʃ��
    return clamp(px, _PointSizeMin, _PointSizeMax);
}

v2f Vert(appdata v)
{
    v2f o;
    uint id = v.iid;
    float3 wp = _Positions[id];
                // �ʿ� �� ��ǥ ����� ����
                // wp = float3(wp.x, wp.z, wp.y);

                // ī�޶� ���� ���� right/up���� billboard Ȯ��
    float3 camRight = UNITY_MATRIX_V._m00_m01_m02;
    float3 camUp = UNITY_MATRIX_V._m10_m11_m12;
    camRight = normalize(camRight);
    camUp = normalize(camUp);

    float sizePx = ComputeScreenSize(wp, _PointSize);
                // ȭ�� �ȼ� ũ�⸦ NDC�� ��Ȯ�� �ݿ��Ϸ��� ScreenParams�� proj�� ����� ������ ��� �ʿ�
                // ����ȭ: world���� ���� ������(�ٻ�). ����� �Ÿ����� �ڿ�������, �ָ��� clamp�� ���� ���ѵ�.
    float scaleWorld = _PointSize; // world ��� �⺻ ������
    float2 offs = v.posOS.xy; // -0.5~0.5

    float3 offset = camRight * offs.x * scaleWorld + camUp * offs.y * scaleWorld;
    float3 wv = wp + offset;

    o.posCS = UnityWorldToClipPos(wv);
    o.uv = v.uv;
    o.alpha = saturate(sizePx / _PointSizeMax) * _AlphaScale; // �ּ��� �۾����� ����
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
                    // LUT ��� intensity �� RGB (��: Colors buffer ��� intensity buffer�� ���� RGBA�� R�� ����Ѵٸ� ���̴��� �°� ����)
                    // ���⼭�� LUT ���ø� ���÷� ǥ��: u ��ǥ�� intensity ����ȭ�� �� ���� �ʿ�
        float u = 0.5; // TODO: ���� intensity->0..1 ����
        col = tex2D(_LutTex, float2(u, 0.5));
        col.a = 1.0;
    }

                // ���� ����ũ(clip)�� ��ũ ����
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
