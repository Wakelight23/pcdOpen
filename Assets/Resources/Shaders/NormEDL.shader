Shader "Shaders/NormEDL"
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
    
                Name "NormEDL"
                HLSLPROGRAM
                #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
                #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

                #pragma vertex   Vert
                #pragma fragment Frag
                #pragma multi_compile __ EDL_HIGH_QUALITY EDL_LOW_QUALITY

                TEXTURE2D_X(_PcdDepth);      SAMPLER(sampler_PcdDepth);
                float _EdlRadius;            // ����: �ؼ� �����̽� ������(���� �ݰ� ���)[3]
                float _EdlStrength;
                float _BrightnessBoost;

                // �߰�: ���÷� ���/ũ��
                float _KernelShape;          // 0=circle, 1=square, 2=gaussian
                float _SplatPxRadius;        // �ȼ� �ݰ�(��� ����Ʈ px�� ���� ��)
                float _GaussianSigmaPx;      // ����þ� �ñ׸�(�ȼ�)

                float4 _BlitTexture_TexelSize; // x=1/w, y=1/h

                inline float2 PxFromPixels(float2 px) { return px * _BlitTexture_TexelSize.xy; }
                inline float4 SampleAccum(float2 uv) { return SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv); }
                inline float  SampleInvDepth(float2 uv){ return SAMPLE_TEXTURE2D_X(_PcdDepth, sampler_PcdDepth, uv).r; }
                inline float ToDepth01(float invD) { return 1.0 / max(invD, 1e-6); }

                // 8�� ���� ����(��/�밢)
                static const float2 DIRS8[8] = {
                    float2( 1, 0), float2(-1, 0),
                    float2( 0, 1), float2( 0,-1),
                    float2( 0.7071,  0.7071), float2(-0.7071,  0.7071),
                    float2( 0.7071, -0.7071), float2(-0.7071, -0.7071)
                };

                // �簢 ǲ����Ʈ�� ����ȭ ����(�밢�� 1/sqrt(2)�� �����Ͽ� ���� �ȼ� �Ÿ�)
                static const float2 DIRS8_SQUARE[8] = {
                    float2( 1, 0), float2(-1, 0),
                    float2( 0, 1), float2( 0,-1),
                    normalize(float2( 1, 1)), normalize(float2(-1, 1)),
                    normalize(float2( 1,-1)), normalize(float2(-1,-1))
                };

                // ����þ� ����ġ
                inline float GaussianW(float distPx, float sigmaPx)
                {
                    float s2 = max(1e-4, sigmaPx * sigmaPx);
                    return exp(-(distPx*distPx) / (2.0*s2));
                }

                half4 Frag(Varyings i) : SV_Target
                {
                    float2 uv = i.texcoord;

                    // 1) Normalize color from accum
                    float4 acc = SampleAccum(uv);
                    float  w   = acc.a;
                    if (w <= 1e-6) return half4(0,0,0,1);
                    float3 baseCol = acc.rgb / w;

                    // 2) EDL early outs
                    float d0 = SampleInvDepth(uv);
                    if (d0 <= 0.0) return half4(baseCol, 1);

                    float z0 = ToDepth01(d0);

                    // ���� �ݰ�(px) ���: EDL �ݰ� ��� �� ���÷� �ݰ�
                    // _EdlRadius�� �ؼ� �����̽� ��������Ƿ�, ���÷� �ȼ� �ݰ�� ���� ���� �������� �ȼ� ������ �����.
                    float splatPx = max(1.0, _SplatPxRadius);
                    float edlPxRadius = max(1.0, _EdlRadius * splatPx);

                    int K = (EDL_HIGH_QUALITY ? 8 : 4);

                    float accEdge = 0.0;

                    [unroll] for (int k=0; k<K; ++k)
                    {
                        float2 dir;

                        // Ŀ�� ���º� ���� ���� ����
                        if (_KernelShape < 0.5)        // circle
                            dir = DIRS8[k];
                        else if (_KernelShape < 1.5)   // square
                            dir = DIRS8_SQUARE[k];
                        else                           // gaussian
                            dir = DIRS8[k];

                        // �ȼ� ������ �Ÿ�
                        float distPx = edlPxRadius;

                        // ����þ��̸� �߽ɿ��� �ּ��� ����ġ�� ��ȭ
                        float wGauss = 1.0;
                        if (_KernelShape >= 1.5)
                        {
                            wGauss = GaussianW(distPx, max(0.5, _GaussianSigmaPx));
                        }

                        // uv ������: �ȼ����ؼ� ��ȯ
                        float2 uvk = uv + PxFromPixels(dir * distPx);

                        float dk = SampleInvDepth(uvk);
                        float zi = (dk > 0.0) ? ToDepth01(dk) : z0;

                        // ���� ���� �����ϵ�, ����þ��� �� �⿩�� wGauss�� ����
                        float di = max(0.0, z0 - zi);
                        di /= max(z0, 1e-4);
                        accEdge += di * wGauss;
                    }

                    float shade   = exp(-_EdlStrength * accEdge);
                    float3 outCol = baseCol * shade * _BrightnessBoost;
                    return half4(outCol, 1);
                }
                ENDHLSL
            }
        }
    FallBack Off
}


/*Shader "Shaders/NormEDL"
{
    Properties
    {
        _BorderThicknessPx("Border Thickness (px)", Range(0,8)) = 1
        _BorderColor("Border Color", Color) = (0,0,0,1)
    }
    SubShader
    {
        Tags{ "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" }
        Pass
        {
            ZTest Always
            ZWrite Off
            Cull Off

            Name "NormEDL"
            HLSLPROGRAM
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            #pragma vertex   Vert
            #pragma fragment Frag
            #pragma multi_compile __ EDL_HIGH_QUALITY EDL_LOW_QUALITY

            // Blitter source: Accum RGBA (sumRGB, weightA)
            // TEXTURE2D_X(_BlitTexture); SAMPLER(sampler_BlitTexture);

            // DepthProxy (invDepth)
            TEXTURE2D_X(_PcdDepth);  SAMPLER(sampler_PcdDepth);

            float _EdlRadius;
            float _EdlStrength;
            float _BrightnessBoost;

            float4 _BlitTexture_TexelSize; // x=1/w, y=1/h
            float  _BorderThicknessPx;
            float4 _BorderColor;

            inline float2 Px(float2 dir) { return dir * _BlitTexture_TexelSize.xy; }

            inline float4 SampleAccum(float2 uv) { return SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv); }
            inline float  SampleInvDepth(float2 uv){ return SAMPLE_TEXTURE2D_X(_PcdDepth, sampler_PcdDepth, uv).r; }

            inline float ToDepth01(float invD) { return 1.0 / max(invD, 1e-6); }

            half4 Frag(Varyings i) : SV_Target
            {
                float2 uv = i.texcoord;

                // 1) ����ȭ
                float4 acc = SampleAccum(uv);
                float  w   = acc.a;
                if (w <= 1e-6) return half4(0,0,0,1);
                float3 baseCol = acc.rgb / w;
                baseCol = baseCol / (1.0 + baseCol);

                // 2) EDL
                float d0 = SampleInvDepth(uv);
                if (d0 <= 0.0) 
                {
                    // ��� �׵θ� ó��(���� ���� ���� ���� ��Ģ ����)
                    float2 px = _BlitTexture_TexelSize.xy;
                    float tPx = max(1.0, _BorderThicknessPx);
                    float2 b = float2(tPx*px.x, tPx*px.y);
                    bool onBorder = (uv.x < b.x) || (uv.y < b.y) || (uv.x > 1.0 - b.x) || (uv.y > 1.0 - b.y);
                    float3 col = baseCol;
                    if (onBorder) col = _BorderColor.rgb;
                    return half4(col, 1);
                }

                float z0 = ToDepth01(d0);
                float accEdge = 0.0;
                static const float2 dirs8[8] = {
                    float2( 1.0,  0.0), float2(-1.0,  0.0),
                    float2( 0.0,  1.0), float2( 0.0, -1.0),
                    float2( 0.7071,  0.7071), float2(-0.7071,  0.7071),
                    float2( 0.7071, -0.7071), float2(-0.7071, -0.7071)
                };

                [unroll] for (int k=0; k < (EDL_HIGH_QUALITY ? 8 : 4); ++k)
                {
                    float2 uvk = uv + Px(dirs8[k] * _EdlRadius);
                    float dk = SampleInvDepth(uvk);
                    float zi = (dk > 0.0) ? ToDepth01(dk) : z0;
                    float di = max(0.0, z0 - zi);
                    di /= max(z0, 1e-4);
                    accEdge += di;
                }

                float shade   = exp(-_EdlStrength * accEdge);
                float3 outCol = baseCol * shade * _BrightnessBoost;
                outCol = min(outCol, 1.0.xxx);

                // 3) ȭ�� �׵θ�(�ȼ� ���� �β�) ������ ó��
                float2 px = _BlitTexture_TexelSize.xy;
                float tPx = max(1.0, _BorderThicknessPx);
                float2 b = float2(tPx*px.x, tPx*px.y);
                bool onBorder = (uv.x < b.x) || (uv.y < b.y) || (uv.x > 1.0 - b.x) || (uv.y > 1.0 - b.y);
                if (onBorder) outCol = _BorderColor.rgb;

                return half4(outCol, 1);
            }
            ENDHLSL
        }
    }
    FallBack Off
}*/