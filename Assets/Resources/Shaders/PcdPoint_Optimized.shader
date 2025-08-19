Shader"Custom/PcdPoint_Optimized"
{
    SubShader
    {
        Tags { "Queue"="Geometry" "RenderType"="Opaque" }
        Pass
        {
ZWrite On

ZTest LEqual

Cull Off

            CGPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 4.0

#include "UnityCG.cginc"

StructuredBuffer<float3> _Positions;
StructuredBuffer<uint> _Colors; // optional
int _HasColor;

struct appdata
{
    uint vertexID : SV_VertexID;
};
struct v2f
{
    float4 pos : SV_POSITION;
    uint vid : TEXCOORD0;
};

v2f Vert(appdata v)
{
    v2f o;
    uint id = v.vertexID;
    float3 wp = _Positions[id];
                // 좌표 재매핑이 필요하면 유지
    wp = float3(wp.x, wp.z, wp.y);
    o.pos = UnityWorldToClipPos(wp);
    o.vid = id;
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
    return (_HasColor == 1) ? UnpackRGBA8(_Colors[i.vid]) : fixed4(1, 1, 1, 1);
}
            ENDCG
        }
    }
}
