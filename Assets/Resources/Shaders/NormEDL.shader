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
                float _EdlRadius;            // 기존: 텍셀 스페이스 스케일(기준 반경 계수)[3]
                float _EdlStrength;
                float _BrightnessBoost;

                // 추가: 스플랫 모양/크기
                float _KernelShape;          // 0=circle, 1=square, 2=gaussian
                float _SplatPxRadius;        // 픽셀 반경(평균 포인트 px의 절반 등)
                float _GaussianSigmaPx;      // 가우시안 시그마(픽셀)

                float4 _BlitTexture_TexelSize; // x=1/w, y=1/h

                inline float2 PxFromPixels(float2 px) { return px * _BlitTexture_TexelSize.xy; }
                inline float4 SampleAccum(float2 uv) { return SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv); }
                inline float  SampleInvDepth(float2 uv){ return SAMPLE_TEXTURE2D_X(_PcdDepth, sampler_PcdDepth, uv).r; }
                inline float ToDepth01(float invD) { return 1.0 / max(invD, 1e-6); }

                // 8방 단위 벡터(축/대각)
                static const float2 DIRS8[8] = {
                    float2( 1, 0), float2(-1, 0),
                    float2( 0, 1), float2( 0,-1),
                    float2( 0.7071,  0.7071), float2(-0.7071,  0.7071),
                    float2( 0.7071, -0.7071), float2(-0.7071, -0.7071)
                };

                // 사각 풋프린트용 정규화 방향(대각은 1/sqrt(2)로 보정하여 동일 픽셀 거리)
                static const float2 DIRS8_SQUARE[8] = {
                    float2( 1, 0), float2(-1, 0),
                    float2( 0, 1), float2( 0,-1),
                    normalize(float2( 1, 1)), normalize(float2(-1, 1)),
                    normalize(float2( 1,-1)), normalize(float2(-1,-1))
                };

                // 가우시안 가중치
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

                    // 샘플 반경(px) 계산: EDL 반경 계수 × 스플랫 반경
                    // _EdlRadius는 텍셀 스페이스 계수였으므로, 스플랫 픽셀 반경과 곱해 실제 오프셋을 픽셀 단위로 만든다.
                    float splatPx = max(1.0, _SplatPxRadius);
                    float edlPxRadius = max(1.0, _EdlRadius * splatPx);

                    int K = (EDL_HIGH_QUALITY ? 8 : 4);

                    float accEdge = 0.0;

                    [unroll] for (int k=0; k<K; ++k)
                    {
                        float2 dir;

                        // 커널 형태별 방향 벡터 선택
                        if (_KernelShape < 0.5)        // circle
                            dir = DIRS8[k];
                        else if (_KernelShape < 1.5)   // square
                            dir = DIRS8_SQUARE[k];
                        else                           // gaussian
                            dir = DIRS8[k];

                        // 픽셀 오프셋 거리
                        float distPx = edlPxRadius;

                        // 가우시안이면 중심에서 멀수록 가중치로 약화
                        float wGauss = 1.0;
                        if (_KernelShape >= 1.5)
                        {
                            wGauss = GaussianW(distPx, max(0.5, _GaussianSigmaPx));
                        }

                        // uv 오프셋: 픽셀→텍셀 변환
                        float2 uvk = uv + PxFromPixels(dir * distPx);

                        float dk = SampleInvDepth(uvk);
                        float zi = (dk > 0.0) ? ToDepth01(dk) : z0;

                        // 원본 로직 유지하되, 가우시안일 때 기여를 wGauss로 가중
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

                // 1) 정규화
                float4 acc = SampleAccum(uv);
                float  w   = acc.a;
                if (w <= 1e-6) return half4(0,0,0,1);
                float3 baseCol = acc.rgb / w;
                baseCol = baseCol / (1.0 + baseCol);

                // 2) EDL
                float d0 = SampleInvDepth(uv);
                if (d0 <= 0.0) 
                {
                    // 경계 테두리 처리(깊이 없을 때도 동일 규칙 적용)
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

                // 3) 화면 테두리(픽셀 단위 두께) 검은색 처리
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