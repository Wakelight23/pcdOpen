Shader "Custom/PcdPoint"
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
            float _PointSize; // 인터페이스 유지

            float4x4 _LocalToWorld;

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
                float3 lp = _Positions[id];
                // lp = float3(lp.x, lp.z, lp.y); // 좌표계 보정(필요시)

                float4 wp = mul(_LocalToWorld, float4(lp, 1.0)); // 모델→월드
                o.pos = UnityWorldToClipPos(wp.xyz);                    // 월드→클립
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

            // fixed4 Frag(v2f i):SV_Target { return fixed4(1,0,0,1); }

            fixed4 Frag(v2f i) : SV_Target
            {
                return (_HasColor == 1) ? UnpackRGBA8(_Colors[i.vid]) : fixed4(1, 1, 1, 1);
            }
            ENDCG
        }
    }
}
