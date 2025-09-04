Shader "Custom/PcdSplatAccum"
{
    Properties
    {
        _PointSize       ("Point Size (pixels)", Float) = 2.0
        _KernelSharpness ("Kernel Sharpness", Range(0.5,3)) = 1.5
        [Toggle]_Gaussian("Gaussian Kernel", Float) = 1

        _DepthMatchEps   ("Depth Match Eps", Float) = 0.001
        _PcdDepthRT      ("Front invDepth RT", 2D) = "black" {}

        // EDL
        _EdlStrength     ("EDL Strength", Range(0,4)) = 1.0

        // Distance color
        [Toggle]_UseDistanceColor ("Use Distance Color", Float) = 1
        _NearColor ("Near Color", Color) = (1,1,1,1)
        _FarColor  ("Far Color",  Color) = (0.6,0.9,1,1)
        _NearDist  ("Near Distance", Float) = 2.0
        _FarDist   ("Far Distance",  Float) = 25.0
        [KeywordEnum(Replace,Multiply,Overlay)] _DistMode ("Distance Color Mode", Float) = 0
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "Queue"="Geometry" "RenderType"="Opaque" }
        Pass
        {
            ZTest LEqual
            ZWrite On
            Cull Off
            // Blend One One
            Blend Off
            ColorMask RGBA

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex   Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #include "Assets/Resources/Shaders/PcdSplatAccum_Common.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct FragOut { float4 colorAccum:SV_Target0; float4 weightAccum:SV_Target1; };

            float3 ApplyDistanceColor(float3 baseCol, float3 nearCol, float3 farCol, float t, float mode)
            {
                float3 distCol = lerp(nearCol, farCol, t);
                // mode: 0=Replace, 1=Multiply, 2=Overlay (simple screen-like blend)
                if (mode < 0.5) return distCol;
                if (mode < 1.5) return baseCol * distCol;
                // overlay-ish: 1 - (1-base)*(1-dist)
                return 1.0 - (1.0 - baseCol) * (1.0 - distCol);
            }

            float4 Frag(v2f i) : SV_Target0
            {
                FragOut o;
                float2 p = i.uv * 0.5 + 0.5;
                float2 d = p - 0.5;
                float r2 = dot(d,d) * 4.0;
                // 원형 풋프린트 밖 프래그먼트 제거(깊이/컬러 패스 일치)
                if (r2 > 1.0) discard;

                // 가중치 계산은 가장자리 소프트닝에만 사용
                float weight;
                if (_Gaussian > 0.5) { float sigma2 = 0.25 / max(_KernelSharpness, 1e-3); weight = exp(-r2 / sigma2); }
                else { float dlin = saturate(1.0 - sqrt(r2)); weight = pow(dlin, max(_KernelSharpness, 1.0)); }

                weight *= saturate(_NodeFade);

                float3 col = (_HasColor == 1) ? UnpackRGBA8(_Colors[i.pid]) : 1.0.xxx;

                if (_UseDistanceColor > 0.5) {
                    float3 camWS = _WorldSpaceCameraPos.xyz;
                    float dCam = distance(camWS, i.posWS);
                    float nearD = max(1e-4, _NearDist);
                    float farD  = max(nearD + 1e-4, _FarDist);
                    float t = saturate((dCam - nearD) / (farD - nearD));
                    col = ApplyDistanceColor(col, _NearColor.rgb, _FarColor.rgb, t, _DistMode);
                }

                // 불투명 출력
                o.colorAccum = float4(col, 1.0);
                return float4(col, 1.0);
            }
            ENDHLSL
        }
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode"="DepthOnly" }
            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull Off

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex   Vert
            #pragma fragment FragDepth
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Assets/Resources/Shaders/PcdSplatAccum_Common.hlsl"

            void FragDepth(v2f i)
            {
                // 원형 컷아웃 동일 적용
                float2 p = i.uv * 0.5 + 0.5;
                float2 d = p - 0.5;
                float r2 = dot(d,d) * 4.0;
                if (r2 > 1.0) discard;
            }
            ENDHLSL
        }
    }
}
