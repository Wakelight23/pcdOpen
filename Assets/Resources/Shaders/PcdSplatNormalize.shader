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

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv     : TEXCOORD0;
            };

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

            v2f Vert(appdata v)
            {
                v2f o;
                o.pos = TransformObjectToHClip(v.vertex.xyz);
                // 타일/오프셋을 적용하려면 TRANSFORM_TEX 사용
                o.uv  = v.uv; // TRANSFORM_TEX(v.uv, _ColorAccum);
                return o;
            }

            half4 Frag(v2f i) : SV_Target
            {
                // 필요 시 동일 uv를 두 텍스처에 사용(타일/오프셋 분리 시 각각 변환)
                half w = SAMPLE_TEXTURE2D(_WeightAccum, sampler_WeightAccum, i.uv).r;
                if (w <= 0.0h) discard;

                half3 sum = SAMPLE_TEXTURE2D(_ColorAccum, sampler_ColorAccum, i.uv).rgb;
                half3 c = sum / max(w, 1e-6h);
                return half4(c, 1);
            }
            ENDHLSL
        }
    }
}
