Shader "Custom/PcdSplatNormalize"
{
    Properties
    {
        // 인스펙터 노출이 필요 없으면 생략 가능
        _ColorAccum  ("Color Accum", 2D) = "black" {}
        _WeightAccum ("Weight Accum", 2D) = "black" {}
    }
    SubShader
    {
        Tags{ "RenderPipeline"="UniversalPipeline" "Queue"="Transparent" }
        Pass
        {
            ZWrite Off
            ZTest Always
            Cull Off
            Blend One Zero
            ColorMask RGBA

            HLSLPROGRAM
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            #pragma vertex Vert
            #pragma fragment Frag

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv  : TEXCOORD0;
            };

            // URP 텍스처/샘플러 선언
            TEXTURE2D(_ColorAccum);
            SAMPLER(sampler_ColorAccum);
            TEXTURE2D(_WeightAccum);
            SAMPLER(sampler_WeightAccum);

            // 타일/오프셋을 쓸 계획이면 ST 선언
            CBUFFER_START(UnityPerMaterial)
                float4 _ColorAccum_ST;
                float4 _WeightAccum_ST;
            CBUFFER_END

            v2f Vert(uint vertexID : SV_VertexID)
            {
                v2f o;
                float2 uv = float2((vertexID << 1) & 2, vertexID & 2);
                o.pos = float4(uv * 2.0 - 1.0, 0.0, 1.0);
    
                // UV 플립 처리
                #if UNITY_UV_STARTS_AT_TOP
                uv.y = 1.0 - uv.y;
                #endif
    
                o.uv = uv;
                return o;
            }

            half4 Frag(v2f i) : SV_Target
            {
                // 1단계: 단순 색상 테스트
                // return half4(1, 0, 0, 1); // 빨간색 - 렌더링 자체가 되는지 확인
    
                // 2단계: UV 좌표 테스트
                // return half4(i.uv, 0, 1); // UV를 색상으로 표시
    
                // 3단계: 가중치 텍스처 테스트
                // half w = SAMPLE_TEXTURE2D(_WeightAccum, sampler_WeightAccum, i.uv).r;
                // return half4(w, w, w, 1);
    
                // 4단계: 색상 누적 텍스처 테스트
                // half3 sum = SAMPLE_TEXTURE2D(_ColorAccum, sampler_ColorAccum, i.uv).rgb;
                // return half4(sum, 1);
    
                // 5단계: 최종 정규화 테스트
                half w = SAMPLE_TEXTURE2D(_WeightAccum, sampler_WeightAccum, i.uv).r;
                half3 sum = SAMPLE_TEXTURE2D(_ColorAccum, sampler_ColorAccum, i.uv).rgb;
                return half4(w > 1e-6 ? sum/w : half3(0,0,0), 1);
            }

            /*half4 Frag(v2f i) : SV_Target
            {
                // 필요 시 동일 uv를 두 텍스처에 사용(타일/오프셋 분리 시 각각 변환)
                half w = SAMPLE_TEXTURE2D(_WeightAccum, sampler_WeightAccum, i.uv).r;
                half3 sum = SAMPLE_TEXTURE2D(_ColorAccum, sampler_ColorAccum, i.uv).rgb;
                if (w <= 1e-6) 
                {
                    return half4(0, 0, 0, 0); // 또는 discard;
                }
    
                // 정규화된 색상 계산
                half3 normalizedColor = sum / w;
    
                return half4(normalizedColor, 1.0);
            }*/
            ENDHLSL
        }
    }
}
