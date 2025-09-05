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

            v2f Vert(uint vertexID : SV_VertexID)
            {
                v2f o;
                float2 uv = float2((vertexID << 1) & 2, vertexID & 2);
                o.pos = float4(uv * 2.0 - 1.0, 0.0, 1.0);
    
                // UV �ø� ó��
                #if UNITY_UV_STARTS_AT_TOP
                uv.y = 1.0 - uv.y;
                #endif
    
                o.uv = uv;
                return o;
            }

            half4 Frag(v2f i) : SV_Target
            {
                // 1�ܰ�: �ܼ� ���� �׽�Ʈ
                // return half4(1, 0, 0, 1); // ������ - ������ ��ü�� �Ǵ��� Ȯ��
    
                // 2�ܰ�: UV ��ǥ �׽�Ʈ
                // return half4(i.uv, 0, 1); // UV�� �������� ǥ��
    
                // 3�ܰ�: ����ġ �ؽ�ó �׽�Ʈ
                // half w = SAMPLE_TEXTURE2D(_WeightAccum, sampler_WeightAccum, i.uv).r;
                // return half4(w, w, w, 1);
    
                // 4�ܰ�: ���� ���� �ؽ�ó �׽�Ʈ
                // half3 sum = SAMPLE_TEXTURE2D(_ColorAccum, sampler_ColorAccum, i.uv).rgb;
                // return half4(sum, 1);
    
                // 5�ܰ�: ���� ����ȭ �׽�Ʈ
                half w = SAMPLE_TEXTURE2D(_WeightAccum, sampler_WeightAccum, i.uv).r;
                half3 sum = SAMPLE_TEXTURE2D(_ColorAccum, sampler_ColorAccum, i.uv).rgb;
                return half4(w > 1e-6 ? sum/w : half3(0,0,0), 1);
            }

            /*half4 Frag(v2f i) : SV_Target
            {
                // �ʿ� �� ���� uv�� �� �ؽ�ó�� ���(Ÿ��/������ �и� �� ���� ��ȯ)
                half w = SAMPLE_TEXTURE2D(_WeightAccum, sampler_WeightAccum, i.uv).r;
                half3 sum = SAMPLE_TEXTURE2D(_ColorAccum, sampler_ColorAccum, i.uv).rgb;
                if (w <= 1e-6) 
                {
                    return half4(0, 0, 0, 0); // �Ǵ� discard;
                }
    
                // ����ȭ�� ���� ���
                half3 normalizedColor = sum / w;
    
                return half4(normalizedColor, 1.0);
            }*/
            ENDHLSL
        }
    }
}
