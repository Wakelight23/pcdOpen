Shader "Shaders/NormEDL"
{
    Properties { }
    SubShader
    {
        Tags{ "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" }

        // Pass 0 : Normalize + EDL (accum에서 읽기)
        Pass
        {
            Name "NormEDL"
            ZTest Always
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            #pragma vertex   Vert
            #pragma fragment Frag
            #pragma multi_compile __ EDL_HIGH_QUALITY EDL_LOW_QUALITY

            // EDL params
            float _EdlRadius;
            float _EdlStrength;
            float _BrightnessBoost;

            // Splat params
            float _KernelShape;        // 0=circle,1=square,2=gaussian
            float _SplatPxRadius;      // pixel radius
            float _GaussianSigmaPx;    // gaussian sigma (px)

            float4 _BlitTexture_TexelSize; // x=1/w, y=1/h

            inline float2 PxFromPixels(float2 px) { return px * _BlitTexture_TexelSize.xy; }
            inline float4 SampleAccum(float2 uv) { return SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv); }

            static const float2 DIRS8[8] = {
                float2( 1, 0), float2(-1, 0),
                float2( 0, 1), float2( 0,-1),
                float2( 0.7071,  0.7071), float2(-0.7071,  0.7071),
                float2( 0.7071, -0.7071), float2(-0.7071, -0.7071)
            };
            static const float2 DIRS8_SQUARE[8] = {
                float2( 1, 0), float2(-1, 0),
                float2( 0, 1), float2( 0,-1),
                normalize(float2( 1, 1)), normalize(float2(-1, 1)),
                normalize(float2( 1,-1)), normalize(float2(-1,-1))
            };

            inline float GaussianW(float distPx, float sigmaPx)
            {
                float s2 = max(1e-4, sigmaPx * sigmaPx);
                return exp(-(distPx*distPx) / (2.0*s2));
            }

            half4 Frag(Varyings i) : SV_Target
            {
                float2 uv = i.texcoord;

                // 1) Normalize color from accum (acc.rgb = sum, acc.a = weight)
                float4 acc = SampleAccum(uv);
                float  w   = acc.a;
                if (w <= 1e-6) return half4(0,0,0,1);
                float3 baseCol = acc.rgb / w;

                // 2) EDL using camera depth
                float raw0 = SampleSceneDepth(uv);
                float z0   = Linear01Depth(raw0, _ZBufferParams);
                if (z0 <= 0.0) return half4(baseCol, 1);

                float splatPx     = max(1.0, _SplatPxRadius);
                float edlPxRadius = max(1.0, _EdlRadius * splatPx);
                int   K           = (EDL_HIGH_QUALITY ? 8 : 4);

                float accEdge = 0.0;

                [unroll] for (int k=0; k<K; ++k)
                {
                    float2 dir;
                    if      (_KernelShape < 0.5) dir = DIRS8[k];         // circle
                    else if (_KernelShape < 1.5) dir = DIRS8_SQUARE[k];  // square
                    else                         dir = DIRS8[k];         // gaussian

                    float  distPx  = edlPxRadius;
                    float  wGauss  = (_KernelShape >= 1.5) ? GaussianW(distPx, max(0.5, _GaussianSigmaPx)) : 1.0;
                    float2 uvk     = uv + PxFromPixels(dir * distPx);

                    float rawi = SampleSceneDepth(uvk);
                    float zi   = Linear01Depth(rawi, _ZBufferParams);
                    zi = (zi > 0.0) ? zi : z0;

                    float di = max(0.0, z0 - zi) / max(z0, 1e-4);
                    accEdge += di * wGauss;
                }

                float shade   = exp(-_EdlStrength * accEdge);
                float3 outCol = baseCol * shade * _BrightnessBoost;
                return half4(outCol, 1);
            }
            ENDHLSL
        }

        // Pass 1 : EDL-only (카메라 컬러 기반)
        Pass
        {
            Name "EDLOnly"
            ZTest Always
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            #pragma vertex   Vert
            #pragma fragment FragEdlOnly
            #pragma multi_compile __ EDL_HIGH_QUALITY EDL_LOW_QUALITY

            float _EdlRadius, _EdlStrength, _BrightnessBoost;
            float _KernelShape, _SplatPxRadius, _GaussianSigmaPx;
            float4 _BlitTexture_TexelSize;

            static const float2 DIRS8[8] = {
                float2( 1,0), float2(-1,0), float2(0,1), float2(0,-1),
                float2(0.7071,0.7071), float2(-0.7071,0.7071),
                float2(0.7071,-0.7071), float2(-0.7071,-0.7071)
            };
            static const float2 DIRS8_SQUARE[8] = {
                float2(1,0), float2(-1,0), float2(0,1), float2(0,-1),
                normalize(float2(1,1)), normalize(float2(-1,1)),
                normalize(float2(1,-1)), normalize(float2(-1,-1))
            };

            inline float GaussianW(float distPx, float sigmaPx)
            {
                float s2 = max(1e-4, sigmaPx*sigmaPx);
                return exp(-(distPx*distPx) / (2.0*s2));
            }
            inline float2 PxToUV(float2 px) { return px * _BlitTexture_TexelSize.xy; }

            half4 FragEdlOnly(Varyings i) : SV_Target
            {
                float2 uv = i.texcoord;

                float3 baseCol = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv).rgb;

                float raw0 = SampleSceneDepth(uv);
                float z0   = Linear01Depth(raw0, _ZBufferParams);
                if (z0 <= 0.0) return half4(baseCol, 1);

                float splatPx     = max(1.0, _SplatPxRadius);
                float edlPxRadius = max(1.0, _EdlRadius * splatPx);

                int K = (EDL_HIGH_QUALITY ? 8 : 4);
                float accEdge = 0.0;

                [unroll] for (int k=0; k<K; ++k)
                {
                    float2 dir = (_KernelShape < 1.5) ? ((_KernelShape < 0.5) ? DIRS8[k] : DIRS8_SQUARE[k]) : DIRS8[k];
                    float  distPx = edlPxRadius;
                    float  wGauss = (_KernelShape >= 1.5) ? GaussianW(distPx, max(0.5, _GaussianSigmaPx)) : 1.0;

                    float2 uvk = uv + PxToUV(dir * distPx);

                    float rawi = SampleSceneDepth(uvk);
                    float zi   = Linear01Depth(rawi, _ZBufferParams);
                    zi = (zi > 0.0) ? zi : z0;

                    float di = max(0.0, z0 - zi) / max(z0, 1e-4);
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