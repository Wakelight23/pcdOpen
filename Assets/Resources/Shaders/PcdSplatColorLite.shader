Shader "Custom/PcdSplatColorLite"
{
    Properties
    {
        _PointSize ("Point Size (pixels)", Float) = 4.0
        _HasSRGB ("Has SRGB", Float) = 1.0
        _DepthMatchEps ("Depth Match Eps", Float) = 0.001
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "Queue"="Transparent" "RenderType"="Transparent" }
        Pass
        {
            ZTest LEqual
            ZWrite Off
            Cull Off
            Blend One Zero

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            StructuredBuffer<float3> _Positions;
            StructuredBuffer<uint>   _Colors;
            int   _HasColor;
            float _PointSize;
            float4x4 _LocalToWorld;
            float _HasSRGB;

            // 전면 가시성용 깊이 프록시 (invDepth 저장됨)
            TEXTURE2D(_PcdDepthRT);
            SAMPLER(sampler_PcdDepthRT);
            float _DepthMatchEps;

            struct appdata
            {
                uint vertexID : SV_VertexID;
                uint instID   : SV_InstanceID;
            };

            struct v2f
            {
                float4 posCS : SV_POSITION;
                float2 uv    : TEXCOORD0;
                nointerpolation float invDepth : TEXCOORD1;
                float2 uvTex : TEXCOORD2;
                nointerpolation uint pid : TEXCOORD3;
            };

            float3 DecodeRGB(uint u)
            {
                float r = (float)((u >> 16) & 255) / 255.0;
                float g = (float)((u >> 8) & 255) / 255.0;
                float b = (float)(u & 255) / 255.0;
                return float3(r,g,b);
            }

            v2f Vert(appdata v)
            {
                v2f o;
                uint pid = v.instID;                   // ← 인스턴스 ID
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

                o.uv = corner;
                o.uvTex = ndc * 0.5 + 0.5;

                float depth01 = saturate(centerCS.z / centerCS.w * 0.5 + 0.5);
                #if UNITY_REVERSED_Z
                    depth01 = 1.0 - depth01;
                #endif
                o.invDepth = (depth01 <= 1e-6) ? 0.0 : (1.0 / depth01);

                o.pid = pid;                           // ← 전달
                return o;
            }

            struct FragOut { float4 col : SV_Target; };

            FragOut Frag(v2f i)
            {
                // 원형 마스크
                float2 p = i.uv * 0.5 + 0.5;
                float2 d = p - 0.5;
                float r2 = dot(d,d) * 4.0;
                if (r2 > 1.0) discard;

                // 전면 깊이 일치 검사
                float invDepthFront = SAMPLE_TEXTURE2D(_PcdDepthRT, sampler_PcdDepthRT, i.uvTex).r;
                float invDMax = invDepthFront;
                [unroll] for (int oy=-1; oy<=1; ++oy)
                [unroll] for (int ox=-1; ox<=1; ++ox) {
                    float2 off = float2(ox, oy) / _ScreenParams.xy;
                    invDMax = max(invDMax, SAMPLE_TEXTURE2D(_PcdDepthRT, sampler_PcdDepthRT, i.uvTex + off).r);
                }
                invDepthFront = invDMax;

                if (abs(i.invDepth - invDepthFront) > _DepthMatchEps)
                    discard;

                // 실제 포인트 색 인덱싱
                float3 rgb = (_HasColor != 0) ? DecodeRGB(_Colors[i.pid]) : float3(1,1,1); // ← 수정

                FragOut o;
                o.col = float4(rgb, 1);
                return o;
            }
            ENDHLSL
        }
    }
}
