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
            // Blit/Varyings�� Ǯ��ũ�� �ﰢ�� + UV/�ø�/XR ����
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            #pragma vertex   Vert
            #pragma fragment Frag
            #pragma multi_compile __ EDL_HIGH_QUALITY EDL_LOW_QUALITY

            // XR ���� ��ũ�� ����
            TEXTURE2D_X(_PcdColor); SAMPLER(sampler_PcdColor);
            TEXTURE2D_X(_PcdDepth); SAMPLER(sampler_PcdDepth);

            // �Ķ����
            float _EdlRadius;
            float _EdlStrength;
            float _BrightnessBoost;

            // Unity�� �ڵ� �¾��ϴ� �ؼ� ũ��(x=1/width, y=1/height)
            float4 _PcdColor_TexelSize;

            // �ȼ� ������(�ؼ� ����)
            inline float2 Px(float2 dir) { return dir * _PcdColor_TexelSize.xy; }

            inline float  SampleDepth(float2 uv) { return SAMPLE_TEXTURE2D_X(_PcdDepth, sampler_PcdDepth, uv).r; }
            inline float3 SampleColor(float2 uv) { return SAMPLE_TEXTURE2D_X(_PcdColor, sampler_PcdColor, uv).rgb; }

            // DepthProxy�� invDepth�� �����ϹǷ� ������ ���� depth01�� ������Ű�� ���� ����ȭ
            inline float LinearEyeDepthFromInv(float invD)
            {
                return 1.0 / max(invD, 1e-6);
            }

            inline float EdlWeight(float d0, float di)
            {
                float z0 = LinearEyeDepthFromInv(d0);
                float zi = LinearEyeDepthFromInv(di);
                return abs(zi - z0);
            }

            half4 Frag(Varyings i) : SV_Target
            {
                // Blit.hlsl�� i.texcoord�� �÷����� �ø�/XR�� �ݿ���
                float2 uv = i.texcoord;

                float3 baseCol = SampleColor(uv);
                float  d0      = SampleDepth(uv);

                // �� �ȼ�(����=0) �н�
                if (d0 <= 0.0)
                    return half4(baseCol, 1);

                // 8����(ǰ�� ��۷� 4/8 ����)
                static const float2 dirs8[8] = {
                    float2( 1.0,  0.0), float2(-1.0,  0.0),
                    float2( 0.0,  1.0), float2( 0.0, -1.0),
                    float2( 0.7071,  0.7071), float2(-0.7071,  0.7071),
                    float2( 0.7071, -0.7071), float2(-0.7071, -0.7071)
                };

                float acc = 0.0;

            #if defined(EDL_HIGH_QUALITY)
                [unroll] for (int k = 0; k < 8; k++)
                {
                    float2 uvk = uv + Px(dirs8[k] * _EdlRadius);
                    float  dk  = SampleDepth(uvk);
                    acc += EdlWeight(d0, dk);
                }
            #else
                [unroll] for (int k = 0; k < 4; k++)
                {
                    float2 uvk = uv + Px(dirs8[k] * _EdlRadius);
                    float  dk  = SampleDepth(uvk);
                    acc += EdlWeight(d0, dk);
                }
            #endif

                // ���� �� ���
                float shade   = exp(-_EdlStrength * acc);
                float3 outCol = baseCol * shade * _BrightnessBoost;
                return half4(outCol, 1);

                // �����:
                // return half4((d0 > 0 ? d0 : 0).xxx, 1);
                // return half4(acc.xxx, 1);
            }
            ENDHLSL
        }
    }
    FallBack Off
}

/*Shader "Shaders/EDL"
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
            TEXTURE2D(_PcdDepth); SAMPLER(sampler_PcdDepth);

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
            float sampleDepth(float2 uv) { return SAMPLE_TEXTURE2D(_PcdDepth, sampler_PcdDepth, uv).r; }
            float3 sampleColor(float2 uv) { return SAMPLE_TEXTURE2D(_PcdColor, sampler_PcdColor, uv).rgb; }
            float LinearEyeDepth(float d, float4x4 proj) {
                // URP���� ���� z ���ο� ���� �б� ����. ����ȭ ����:
                return 1.0 / max(1e-6, d);
            }

            float edlWeight(float d0, float di) {
                float z0 = LinearEyeDepth(d0, UNITY_MATRIX_P);
                float zi = LinearEyeDepth(di, UNITY_MATRIX_P);
                return abs(zi - z0);
            }

            float4 Frag(VSOut i) : SV_Target
            {
                float2 uv = i.uv;
                float3 baseCol = sampleColor(uv);
                float d0 = sampleDepth(uv);

                // �����
                return float4((d0 > 0 ? d0 : 0).xxx, 1);

                // ����ִ� �ȼ�(����=0) ó��: �״�� �н�
                if (d0 <= 0.0)
                {
                    return float4(baseCol, 1);
                }

                // ���� ���� (���� ��� �迭�� �ùٸ��� �ʱ�ȭ)
                static const float2 dirs8[8] = {
                    float2( 1.0,  0.0), float2(-1.0,  0.0),
                    float2( 0.0,  1.0), float2( 0.0, -1.0),
                    float2( 0.7071,  0.7071), float2(-0.7071,  0.7071),
                    float2( 0.7071, -0.7071), float2(-0.7071, -0.7071)
                };

                // ����
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

                // ����
                float shade = exp(-_EdlStrength * acc);
                float3 outCol = baseCol * shade * _BrightnessBoost;

                return float4(outCol, 1);

                 // ����׿�: ���� ���� �ð�ȭ
                // return float4(acc.xxx, 1);
            }
            ENDHLSL
        }
    }
    FallBack Off
}*/
