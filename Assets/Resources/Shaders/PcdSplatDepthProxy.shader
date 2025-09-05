Shader "Custom/PcdSplatDepthProxy"
{
    Properties
    {
        _PointSize ("Point Size (pixels)", Float) = 2.0
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "Queue"="Transparent" "RenderType"="Transparent" }
        Pass
        {
            ZTest LEqual
            ZWrite Off
            Cull Off
            Blend One One
            BlendOp Max
            ColorMask R

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing

            #include "UnityCG.cginc"

            StructuredBuffer<float3> _Positions;
            float _PointSize;
            float4x4 _LocalToWorld;

            struct appdata
            {
                uint vertexID : SV_VertexID;
                uint instID   : SV_InstanceID;
            };

            struct v2f
            {
                float4 posCS : SV_POSITION;
                float2 uv    : TEXCOORD0;
                nointerpolation float depth01 : TEXCOORD1;
            };

            v2f Vert(appdata v)
            {
                v2f o;
                uint pid = v.instID;

                float3 lp = _Positions[pid];
                float4 wp = mul(_LocalToWorld, float4(lp, 1));
                float3 vp = mul(UNITY_MATRIX_V, wp).xyz;
                float4 centerCS = mul(UNITY_MATRIX_P, float4(vp, 1));

                float2 ndcPerPixel = 2.0 / _ScreenParams.xy;
                float2 corner = float2((v.vertexID & 1) ? +1 : -1,
                                       (v.vertexID & 2) ? +1 : -1);
                float2 quadOffsetNdc = corner * (_PointSize * 0.5) * ndcPerPixel;

                float2 ndc = centerCS.xy / max(centerCS.w, 1e-5);
                float4 posCS;
                posCS.xy = (ndc + quadOffsetNdc) * centerCS.w;
                posCS.zw = centerCS.zw;

                o.posCS = posCS;
                o.uv    = corner;

                float depth01 = saturate(centerCS.z / centerCS.w * 0.5 + 0.5);
                #if UNITY_REVERSED_Z
                    depth01 = 1.0 - depth01;
                #endif
                o.depth01 = depth01;
                return o;
            }

            struct FragOut { float4 v : SV_Target; };

            FragOut Frag(v2f i)
            {
                float2 p = i.uv * 0.5 + 0.5;
                float2 d = p - 0.5;
                float r2 = dot(d,d) * 4.0;
                if (r2 > 1.0) discard;

                float invDepth = (i.depth01 <= 1e-6) ? 0.0 : (1.0 / i.depth01);
                FragOut o;
                o.v = float4(invDepth, 0, 0, 0);
                return o;
            }
            ENDHLSL
        }
    }
}
