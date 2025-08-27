Shader "Custom/PcdSplatNormalize"
{
    Properties
    {
        // �ν����� ������ �ʿ� ������ ���� ����
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

            // URP �ؽ�ó/���÷� ����
            TEXTURE2D(_ColorAccum);
            SAMPLER(sampler_ColorAccum);
            TEXTURE2D(_WeightAccum);
            SAMPLER(sampler_WeightAccum);

            // Ÿ��/�������� �� ��ȹ�̸� ST ����
            CBUFFER_START(UnityPerMaterial)
                float4 _ColorAccum_ST;
                float4 _WeightAccum_ST;
            CBUFFER_END

            v2f Vert(appdata v)
            {
                v2f o;
                o.pos = TransformObjectToHClip(v.vertex.xyz);
                // Ÿ��/�������� �����Ϸ��� TRANSFORM_TEX ���
                o.uv  = v.uv; // TRANSFORM_TEX(v.uv, _ColorAccum);
                return o;
            }

            half4 Frag(v2f i) : SV_Target
            {
                // �ʿ� �� ���� uv�� �� �ؽ�ó�� ���(Ÿ��/������ �и� �� ���� ��ȯ)
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
