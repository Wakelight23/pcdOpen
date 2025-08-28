Shader "Custom/PcdSplatAccum"
{
    Properties
    {
        _PointSize ("Point Size (pixels)", Float) = 2.0
        _KernelSharpness ("Kernel Sharpness", Range(0.5,3)) = 1.5
        [Toggle]_Gaussian ("Gaussian Kernel", Float) = 0
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "Queue"="Transparent" "RenderType"="Transparent" }
        Pass
        {
            // MRT 누적용 Additive
            ZTest LEqual
            ZWrite On
            Cull Off
            Blend One One
            ColorMask RGBA

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing

            #include "UnityCG.cginc"

            StructuredBuffer<float3> _Positions;
            StructuredBuffer<uint>   _Colors;
            int   _HasColor;
            float _PointSize;
            float _KernelSharpness;
            float _Gaussian;
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
                nointerpolation uint pid : TEXCOORD1;
            };

            inline float3 UnpackRGBA8(uint c)
            {
                const float s = 1.0/255.0;
                return float3(
                    ( c       & 0xFF) * s,
                    ((c>>8)  & 0xFF) * s,
                    ((c>>16) & 0xFF) * s
                );
            }

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
                o.uv    = corner; // -1..+1
                o.pid   = pid;
                return o;
            }

            struct FragOut {
                half4 colorAccum : SV_Target0; // RGB = color * weight
                half4 weightAccum: SV_Target1; // R = weight
            };

            FragOut Frag(v2f i)
            {
                FragOut o;
                // gl_PointCoord를 대체: quad uv를 0..1로
                float2 p = i.uv * 0.5 + 0.5;
                float2 d = p - 0.5;
                float r2 = dot(d,d) * 4.0; // (2*|d|)^2 ; 반지름1 기준

                float weight;
                if (_Gaussian > 0.5)
                {
                    // 가우시안 커널
                    float sigma2 = 0.25 / max(_KernelSharpness, 1e-3);
                    weight = exp(-r2 / sigma2);
                }
                else
                {
                    // 원판형 가중치
                    float dlin = saturate(1.0 - sqrt(r2)); // 1 - (2*|d|)
                    weight = pow(dlin, max(_KernelSharpness, 1.0));
                }

                half3 col = half3(1,1,1);
                if (_HasColor == 1)
                {
                    col = UnpackRGBA8(_Colors[i.pid]);
                }

                // 중요: 컬러와 가중치 모두 명시적으로 설정
                o.colorAccum = half4(col * weight, 1.0);  // Alpha를 1.0으로 설정
                o.weightAccum = half4(weight, 0, 0, 1.0); // 가중치는 R 채널에만
    
                return o;
            }
            ENDHLSL
        }
    }
}
