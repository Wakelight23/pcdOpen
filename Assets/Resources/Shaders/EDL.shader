Shader "Shaders/EDL"
{
    Properties { }
    SubShader
    {
        Tags{ "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" }
        Pass
        {
            ZTest Always
            ZWrite Off
            Cull Off

            Name "EDL"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile __ EDL_HIGH_QUALITY EDL_LOW_QUALITY
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_PcdColor); SAMPLER(sampler_PcdColor);
            TEXTURE2D_X_FLOAT(_CameraDepthTexture); SAMPLER(sampler_CameraDepthTexture); 

            float4 _ScreenPx;       // (w,h,0,0)
            float _EdlRadius;
            float _EdlStrength;
            float _BrightnessBoost;

            struct VSIn { float4 posOS : POSITION; float2 uv : TEXCOORD0; };
            struct VSOut{ float4 posCS : SV_POSITION; float2 uv : TEXCOORD0; };

            VSOut Vert(VSIn v)
            {
                VSOut o;
                o.posCS = TransformObjectToHClip(v.posOS.xyz);
                o.uv = v.uv;
                return o;
            }

            float2 px(float2 d) { return d / _ScreenPx.xy; }
            float sampleDepth(float2 uv) { return SAMPLE_TEXTURE2D_X(_CameraDepthTexture, sampler_CameraDepthTexture, uv).r; }
            float3 sampleColor(float2 uv) { return SAMPLE_TEXTURE2D(_PcdColor, sampler_PcdColor, uv).rgb; }
            float LinearEyeDepth(float d, float4x4 proj) {
                // URP에서 반전 z 여부에 따라 분기 가능. 간단화 버전:
                return 1.0 / max(1e-6, d);
            }

            float edlWeight(float d0, float di) {
                float zi = Linear01Depth(d0, _ZBufferParams);
                float z0 = LinearEyeDepth(di, _ZBufferParams);
                return abs(zi - z0);
            }

            float4 Frag(VSOut i) : SV_Target
            {
                float2 uv = i.uv;
                float3 baseCol = sampleColor(uv);
                float d0 = sampleDepth(uv);

                // 비어있는 픽셀(깊이=0) 처리: 그대로 패스
                if (d0 <= 0.0)
                {
                    return float4(baseCol, 1);
                }

                // 샘플 방향 (정적 상수 배열로 올바르게 초기화)
                static const float2 dirs8[8] = {
                    float2( 1.0,  0.0), float2(-1.0,  0.0),
                    float2( 0.0,  1.0), float2( 0.0, -1.0),
                    float2( 0.7071,  0.7071), float2(-0.7071,  0.7071),
                    float2( 0.7071, -0.7071), float2(-0.7071, -0.7071)
                };

                // 누적
                float acc = 0.0;
            #if defined(EDL_HIGH_QUALITY)
                [unroll] for (int k = 0; k < 8; k++)
                {
                    float2 uvk = uv + px(dirs8[k] * _EdlRadius);
                    float dk = sampleDepth(uvk);
                    acc += edlWeight(d0, dk);
                }
            #else
                [unroll] for (int k = 0; k < 4; k++)
                {
                    float2 uvk = uv + px(dirs8[k] * _EdlRadius);
                    float dk = sampleDepth(uvk);
                    acc += edlWeight(d0, dk);
                }
            #endif

                // 감쇠
                float shade = exp(-_EdlStrength * acc);
                float3 outCol = baseCol * shade * _BrightnessBoost;

                return float4(outCol, 1);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
