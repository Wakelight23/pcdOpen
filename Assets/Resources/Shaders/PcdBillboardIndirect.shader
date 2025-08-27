Shader "Custom/PcdBillboardIndirect"
{
    Properties
    {
        _MainTex ("MainTex", 2D) = "white" {}
        _Tint ("Tint", Color) = (1,1,1,1)
        [Toggle] _RoundMask ("Round Mask", Float) = 1
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" "IgnoreProjector"="True" "RenderPipeline"="UniversalPipeline" }
        Pass
    {
        ZWrite Off
        ZTest LEqual
        Blend One OneMinusSrcAlpha

        Name "PcdBillboardIndirect"
        HLSLPROGRAM
        #pragma vertex Vert
        #pragma fragment Frag
        #pragma multi_compile __ POINTSIZE_FIXED POINTSIZE_ATTEN POINTSIZE_ADAPTIVE
        #pragma target 4.5

        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        // Structured buffers
        StructuredBuffer<float3> _Positions;
        StructuredBuffer<uint>   _Colors;     // optional, enabled via _HasColor

        // Textures
        TEXTURE2D(_MainTex); SAMPLER(sampler_MainTex);

        // Params
        float4x4 _LocalToWorld;
        float    _PointSize;         // base pixel size
        float    _SoftEdge;          // 0..1
        float    _RoundMask;         // 0/1
        int      _HasColor;          // 0/1

        // Sizing params
        float    _MinPixel;          // clamp min
        float    _MaxPixel;          // clamp max
        float    _AttenScale;        // attenuation scale
        float    _AdaptiveScale;     // extra boost for adaptive
        //float4   _PcdScreenSize;        // (w,h,0,0)
        float4x4 _View;
        float4x4 _Proj;

        float2 _DistRange; // d0,d1
        float2 _ColorAtten; // kSat(채도감쇠), kLum(밝기감쇠)


        // Per-vertex instance id via SV_InstanceID; quad corner via vertex id
        struct VSIn {
            uint vertexID  : SV_VertexID;
            uint instID    : SV_InstanceID;
        };

        struct VSOut {
            float4 posCS : SV_POSITION;
            float2 uv    : TEXCOORD0;
            float  fade  : TEXCOORD1;
            float dist : TEXCOORD2;
            float4 col   : COLOR0;
        };

        float3 DecodeRGB(uint u)
        {
            float r = (float)((u >> 16) & 255) / 255.0;
            float g = (float)((u >> 8)  & 255) / 255.0;
            float b = (float)(u & 255) / 255.0;
            return float3(r,g,b);
        }

        // Build billboard quad corner from vertexID (0..5 -> 2 tris from a quad)
        float2 QuadCorner(uint vid)
        {
            // indices: 0,1,2, 2,1,3
            uint quadIndex = (vid == 0) ? 0 : (vid == 1) ? 1 : (vid == 2) ? 2 : (vid == 3) ? 2 : (vid == 4) ? 1 : 3;
            float2 corners[4] = {
                float2(-0.5,-0.5),
                float2( 0.5,-0.5),
                float2(-0.5, 0.5),
                float2( 0.5, 0.5)
            };
            return corners[quadIndex];
        }

        float ComputePixelSize(float3 worldPos)
        {
            // camera position from inverse view
            float4x4 invV = UNITY_MATRIX_I_V;
            float3 camPosWS = invV._m03_m13_m23;

            float d = max(1e-4, distance(worldPos, camPosWS));

            // base in pixels
            float pix = _PointSize;

            #if defined(POINTSIZE_ATTEN) || defined(POINTSIZE_ADAPTIVE)
                // perspective-friendly attenuation ~ 1/d
                pix *= (_AttenScale / d);
            #endif

            #if defined(POINTSIZE_ADAPTIVE)
                pix += _AdaptiveScale;
            #endif

            return clamp(pix, _MinPixel, _MaxPixel);
        }

        VSOut Vert(VSIn i)
        {
            VSOut o;

            // Fetch point position
            float3 posLS = _Positions[i.instID];
            float3 posWS = mul(_LocalToWorld, float4(posLS,1)).xyz;

            // Corner and desired pixel size
            float2 corner = QuadCorner(i.vertexID);
            float pix = ComputePixelSize(posWS);

            // Convert pixel offset to clip-space offset
            // First, project center
            float4 posVS = mul(_View, float4(posWS,1));
            float4 posCS = mul(_Proj, posVS);

            // Pixel to NDC scale
            float2 ndcPerPixel = 2.0 / _ScreenParams.xy; // NDC delta per pixel
            float2 ndcOffset = corner * pix * ndcPerPixel;

            // Convert NDC offset to clip-space delta: delta_clip = delta_ndc * w
            posCS.xy += ndcOffset * posCS.w;

            o.posCS = posCS;

            // UV for round mask/soft edge (centered at 0.5)
            float2 uv = corner + 0.5;
            o.uv = uv;

            // Alpha falloff near edge for disc/square splat
            float2 d = abs(uv - 0.5) * 2.0;               // [-1..1] box
            float r2 = dot(d, d);                         // radius^2
            float alpha = 1.0;
            if (_RoundMask > 0.5)
            {
                // circular
                float r = sqrt(r2);
                float t = saturate((1.0 - r) / max(1e-5, _SoftEdge)); // fade over soft edge
                alpha = t;
            }
            else
            {
                // square soft edge
                float m = max(d.x, d.y);
                float t = saturate((1.0 - m) / max(1e-5, _SoftEdge));
                alpha = t;
            }
            o.fade = alpha;

            // Color
            float4 col = 1;
            if (_HasColor != 0)
            {
                uint u = _Colors[i.instID];
                col.rgb = DecodeRGB(u);
            }
            col.a = alpha;
            o.col = col;

                // 카메라 거리(옵션)
            float3 camPosWS = UNITY_MATRIX_I_V._m03_m13_m23;
            o.dist = distance(posWS, camPosWS);

            return o;
        }

        float3 Luminance3(float3 c) { float l = dot(c, float3(0.299, 0.587, 0.114)); return float3(l,l,l); }

        float4 Frag(VSOut i) : SV_Target
        {
            // Optional texture modulation
            float4 tex = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv);
            float4 c = i.col * tex;

            // 프리멀티 출력(렌더 상태: Blend One OneMinusSrcAlpha 권장)
            c.a = 1.0;
            c.rgb *= c.a;

            // 거리 정규화
            float d0 = _DistRange.x, d1 = _DistRange.y;
            float t = saturate((i.dist - d0) / max(1e-5, (d1 - d0)));

            // 채도 감쇠: 원색 → 부분 탈채
            float3 desat = lerp(Luminance3(c.rgb), c.rgb, 0.5);    
            c.rgb = lerp(c.rgb, desat, _ColorAtten.x * t);

            // 밝기 감쇠
            c.rgb *= 1.0 / (1.0 + _ColorAtten.y * t);

            return c;
        }
            ENDHLSL
        }
    }
    FallBack Off
}
