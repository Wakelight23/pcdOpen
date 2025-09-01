Shader "Custom/PcdSplatAccum"
{
    Properties
    {
        _PointSize       ("Point Size (pixels)", Float) = 2.0
        _KernelSharpness ("Kernel Sharpness", Range(0.5,3)) = 1.5
        [Toggle]_Gaussian("Gaussian Kernel", Float) = 0

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
        Tags { "RenderPipeline"="UniversalPipeline" "Queue"="Transparent" "RenderType"="Transparent" }
        Pass
        {
            ZTest LEqual
            ZWrite Off
            Cull Off
            Blend One One
            ColorMask RGBA

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex   Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            StructuredBuffer<float3> _Positions;
            StructuredBuffer<uint>   _Colors;
            int   _HasColor;

            float _PointSize;
            float _KernelSharpness;
            float _Gaussian;
            float4x4 _LocalToWorld;
            float _NodeFade;

            TEXTURE2D(_PcdDepthRT);
            SAMPLER(sampler_PcdDepthRT);
            float _DepthMatchEps;

            // EDL
            float _EdlStrength;

            // Distance color params
            float _UseDistanceColor;
            float4 _NearColor;
            float4 _FarColor;
            float _NearDist;
            float _FarDist;
            // Keyword enum resolved as integers 0,1,2 (Replace,Multiply,Overlay)
            // We'll read via material property _DistMode even if keyword enum; keep as float
            float _DistMode;

            struct appdata { uint vertexID:SV_VertexID; uint instID:SV_InstanceID; };
            struct v2f
            {
                float4 posCS:SV_POSITION;
                float2 uv:TEXCOORD0;
                nointerpolation uint pid:TEXCOORD1;
                float2 uvTex:TEXCOORD2;
                nointerpolation float invDepth:TEXCOORD3;
                float3 posWS:TEXCOORD4; // world position of point center
            };

            inline float3 UnpackRGBA8(uint c)
            {
                const float s = 1.0/255.0;
                return float3(( c & 0xFF)*s, ((c>>8)&0xFF)*s, ((c>>16)&0xFF)*s);
            }

            v2f Vert(appdata v)
            {
                v2f o;
                uint pid = v.instID;

                float3 lp = _Positions[pid];
                float4 wp = mul(_LocalToWorld, float4(lp,1));
                o.posWS = wp.xyz;

                float3 vp = mul(UNITY_MATRIX_V, wp).xyz;
                float4 cs = mul(UNITY_MATRIX_P, float4(vp,1));

                float2 ndcPerPixel = 2.0 / _ScreenParams.xy;
                float2 corner = float2((v.vertexID & 1)?+1:-1, (v.vertexID & 2)?+1:-1);
                float2 quadOffsetNdc = corner * (_PointSize * 0.5) * ndcPerPixel;

                float2 ndc = cs.xy / max(cs.w, 1e-5);
                float4 posCS;
                posCS.xy = (ndc + quadOffsetNdc) * cs.w;
                posCS.zw = cs.zw;

                o.posCS = posCS;
                o.uv    = corner;
                o.pid   = pid;

                float2 uv01 = ndc * 0.5 + 0.5;
                o.uvTex = uv01;

                float depth01 = saturate(cs.z / cs.w * 0.5 + 0.5);
                #if UNITY_REVERSED_Z
                    depth01 = 1.0 - depth01;
                #endif
                o.invDepth = (depth01 <= 1e-6) ? 0.0 : (1.0 / depth01);
                return o;
            }

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

            FragOut Frag(v2f i)
            {
                FragOut o;

                // 1) Kernel weight (disc or gaussian)
                float2 p = i.uv * 0.5 + 0.5;
                float2 d = p - 0.5;
                float r2 = dot(d,d) * 4.0;

                float weight;
                if (_Gaussian > 0.5)
                {
                    float sigma2 = 0.25 / max(_KernelSharpness, 1e-3);
                    weight = exp(-r2 / sigma2);
                }
                else
                {
                    float dlin = saturate(1.0 - sqrt(r2));
                    weight = pow(dlin, max(_KernelSharpness, 1.0));
                }

                // 2) Front-visibility match with scaled epsilon
                float invFront = SAMPLE_TEXTURE2D(_PcdDepthRT, sampler_PcdDepthRT, i.uvTex).r;
                float epsScaled = _DepthMatchEps * (1.0 + 0.5 * _PointSize);
                if (_DepthMatchEps > 0.0 && invFront > 0.0 && abs(i.invDepth - invFront) > epsScaled)
                {
                    weight = 0.0;
                }

                // 3) Node fade
                weight *= saturate(_NodeFade);

                // 4) Base color
                float3 col = (_HasColor == 1) ? UnpackRGBA8(_Colors[i.pid]) : 1.0.xxx;

                // 4.1) Distance-based color override/mix
                if (_UseDistanceColor > 0.5)
                {
                    float3 camWS = _WorldSpaceCameraPos.xyz;
                    float dCam = distance(camWS, i.posWS);
                    float nearD = max(1e-4, _NearDist);
                    float farD  = max(nearD + 1e-4, _FarDist);
                    float t = saturate((dCam - nearD) / (farD - nearD));
                    col = ApplyDistanceColor(col, _NearColor.rgb, _FarColor.rgb, t, _DistMode);
                }

                // 5) EDL shading (per-fragment, color-only attenuation)
                float z0 = (i.invDepth > 0.0) ? (1.0 / i.invDepth) : 1.0;
                float distFalloff = saturate(pow(1.0 - z0, 0.25));
                float pxFalloff   = saturate(_PointSize / (_PointSize + 2.0));
                weight *= distFalloff * pxFalloff;
                weight = min(weight, 1.0); // ป๓วั

                static const float2 dirs4[4] = {
                    float2(1,0), float2(-1,0), float2(0,1), float2(0,-1)
                };
                float2 texel = 1.0 / _ScreenParams.xy;
                float radiusPx = max(1.0, 0.5 * _PointSize);

                float accEdge = 0.0;
                [unroll] for (int k=0; k<4; ++k)
                {
                    float2 uvk = i.uvTex + dirs4[k] * texel * radiusPx;
                    float invk = SAMPLE_TEXTURE2D(_PcdDepthRT, sampler_PcdDepthRT, uvk).r;
                    float zk = (invk > 0.0) ? (1.0 / invk) : z0;
                    float di = max(0.0, zk - z0);
                    di /= max(z0, 1e-4);
                    accEdge += di;
                }

                float shade = exp(-_EdlStrength * accEdge);

                // 6) Accumulate: apply EDL to color only, preserve weight
                o.colorAccum  = float4(col * (weight * shade), weight);
                o.weightAccum = float4(weight, 0, 0, 0);
                return o;
            }
            ENDHLSL
        }
    }
}


/*Shader "Custom/PcdSplatAccum"
{
    Properties
    {
        _PointSize       ("Point Size (pixels)", Float) = 2.0
        _KernelSharpness ("Kernel Sharpness", Range(0.5,3)) = 1.5
        [Toggle]_Gaussian("Gaussian Kernel", Float) = 0
        _DepthMatchEps   ("Depth Match Eps", Float) = 0.001
        _PcdDepthRT      ("Front invDepth RT", 2D) = "black" {}
        // LOD Attribute
        _NodeLevel       ("Node Level", Float) = 0
        _MaxDepth        ("Max Depth", Float) = 8

    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "Queue"="Transparent" "RenderType"="Transparent" }
        Pass
        {
            ZTest LEqual
            ZWrite Off
            Cull Off
            Blend One One
            ColorMask RGBA

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex   Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            StructuredBuffer<float3> _Positions;
            StructuredBuffer<uint>   _Colors;
            int   _HasColor;

            float _PointSize;
            float _KernelSharpness;
            float _Gaussian;
            float4x4 _LocalToWorld;
            float _NodeFade;

            // Attribute-LOD
            float _NodeLevel;
            float _MaxDepth;

            TEXTURE2D(_PcdDepthRT);
            SAMPLER(sampler_PcdDepthRT);
            float _DepthMatchEps;

            struct appdata { uint vertexID:SV_VertexID; uint instID:SV_InstanceID; };
            struct v2f
            {
                float4 posCS:SV_POSITION;
                float2 uv:TEXCOORD0;
                nointerpolation uint pid:TEXCOORD1;
                float2 uvTex:TEXCOORD2;
                nointerpolation float invDepth:TEXCOORD3;
            };

            inline float3 UnpackRGBA8(uint c)
            {
                const float s = 1.0/255.0;
                return float3(( c & 0xFF)*s, ((c>>8)&0xFF)*s, ((c>>16)&0xFF)*s);
            }

            v2f Vert(appdata v)
            {
                v2f o;
                uint pid = v.instID;

                float3 lp = _Positions[pid];
                float4 wp = mul(_LocalToWorld, float4(lp,1));
                float3 vp = mul(UNITY_MATRIX_V, wp).xyz;
                float4 cs = mul(UNITY_MATRIX_P, float4(vp,1));

                float2 ndcPerPixel = 2.0 / _ScreenParams.xy;
                float2 corner = float2((v.vertexID & 1)?+1:-1, (v.vertexID & 2)?+1:-1);
                float2 quadOffsetNdc = corner * (_PointSize * 0.5) * ndcPerPixel;

                float2 ndc = cs.xy / max(cs.w, 1e-5);
                float4 posCS;
                posCS.xy = (ndc + quadOffsetNdc) * cs.w;
                posCS.zw = cs.zw;

                o.posCS = posCS;
                o.uv    = corner;
                o.pid   = pid;
                o.uvTex = ndc * 0.5 + 0.5;

                float depth01 = saturate(cs.z / cs.w * 0.5 + 0.5);
                #if UNITY_REVERSED_Z
                    depth01 = 1.0 - depth01;
                #endif
                o.invDepth = (depth01 <= 1e-6) ? 0.0 : (1.0 / depth01);
                return o;
            }

            float2 Rotate45(float2 p){ return float2((p.x-p.y)*0.70710678,(p.x+p.y)*0.70710678); }

            float4 Frag(v2f i) : SV_Target
            {
                // 0) Attribute-LOD radius scaling
                float basePx   = _PointSize;
                float tLevel   = saturate(_NodeLevel / max(1e-3,_MaxDepth)); // 0..1
                float levelScale = lerp(1.0, 1.75, tLevel);                  // deeper level => slightly larger kernel
                float rr       = max(1.0, levelScale);
                float radiusPx = basePx * rr;

                // 1) Kernel weight in unit quad space, normalized by rr
                float2 p = i.uv * 0.5 + 0.5;
                float2 d = p - 0.5;
                // scale footprint down when rr>1 so kernel area grows in screen-space
                float r2 = dot(d,d) * 4.0 / (rr*rr);

                float weight;
                if (_Gaussian > 0.5)
                {
                    float sigma2 = 0.25 / max(_KernelSharpness, 1e-3);
                    weight = exp(-r2 / sigma2);
                }
                else
                {
                    float dlin = saturate(1.0 - sqrt(r2));
                    weight = pow(dlin, max(_KernelSharpness, 1.0));
                }

                // 2) Front-visibility match with scaled epsilon
                float invFront = SAMPLE_TEXTURE2D(_PcdDepthRT, sampler_PcdDepthRT, i.uvTex).r;
                float epsScaled = _DepthMatchEps * (1.0 + 0.5 * radiusPx);
                if (_DepthMatchEps > 0.0 && invFront > 0.0 && abs(i.invDepth - invFront) > epsScaled)
                {
                    weight = 0.0;
                }

                // 3) Node fade
                weight *= saturate(_NodeFade);

                // 4) LOD energy compensation to avoid overbright when kernel grows
                // simple first-order: divide by (1 + k*(rr-1))
                weight *= rcp(1.0 + 0.35 * (rr - 1.0));

                // 5) Base color
                float3 col = (_HasColor == 1) ? UnpackRGBA8(_Colors[i.pid]) : 1.0.xxx;

                // 6) Accumulate: RGB=sum(col*weight), A=sum(weight)
                return float4(col * weight, weight);
            }
            ENDHLSL
        }
    }
}*/

/*Shader "Custom/PcdSplatAccum"
{
    Properties
    {
        _PointSize       ("Point Size (pixels)", Float) = 2.0
        _KernelSharpness ("Kernel Sharpness", Range(0.5,3)) = 1.5
        [Toggle]_Gaussian("Gaussian Kernel", Float) = 0

        _DepthMatchEps   ("Depth Match Eps", Float) = 0.001
        _PcdDepthRT      ("Front invDepth RT", 2D) = "black" {}
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "Queue"="Transparent" "RenderType"="Transparent" }
        Pass
        {
            ZTest LEqual
            ZWrite Off
            Cull Off
            Blend One One
            ColorMask RGBA

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex   Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            StructuredBuffer<float3> _Positions;
            StructuredBuffer<uint>   _Colors;
            int   _HasColor;

            float _PointSize;
            float _KernelSharpness;
            float _Gaussian;
            float4x4 _LocalToWorld;
            float _NodeFade;

            TEXTURE2D(_PcdDepthRT);
            SAMPLER(sampler_PcdDepthRT);
            float _DepthMatchEps;

            struct appdata { uint vertexID:SV_VertexID; uint instID:SV_InstanceID; };
            struct v2f
            {
                float4 posCS:SV_POSITION;
                float2 uv:TEXCOORD0;
                nointerpolation uint pid:TEXCOORD1;
                float2 uvTex:TEXCOORD2;
                nointerpolation float invDepth:TEXCOORD3;
            };

            inline float3 UnpackRGBA8(uint c)
            {
                const float s = 1.0/255.0;
                return float3(( c & 0xFF)*s, ((c>>8)&0xFF)*s, ((c>>16)&0xFF)*s);
            }

            v2f Vert(appdata v)
            {
                v2f o;
                uint pid = v.instID;

                float3 lp = _Positions[pid];
                float4 wp = mul(_LocalToWorld, float4(lp,1));
                float3 vp = mul(UNITY_MATRIX_V, wp).xyz;
                float4 cs = mul(UNITY_MATRIX_P, float4(vp,1));

                float2 ndcPerPixel = 2.0 / _ScreenParams.xy;
                float2 corner = float2((v.vertexID & 1)?+1:-1, (v.vertexID & 2)?+1:-1);
                float2 quadOffsetNdc = corner * (_PointSize * 0.5) * ndcPerPixel;

                float2 ndc = cs.xy / max(cs.w, 1e-5);
                float4 posCS;
                posCS.xy = (ndc + quadOffsetNdc) * cs.w;
                posCS.zw = cs.zw;

                o.posCS = posCS;
                o.uv    = corner;
                o.pid   = pid;

                float2 uv01 = ndc * 0.5 + 0.5;
                o.uvTex = uv01;

                float depth01 = saturate(cs.z / cs.w * 0.5 + 0.5);
                #if UNITY_REVERSED_Z
                    depth01 = 1.0 - depth01;
                #endif
                o.invDepth = (depth01 <= 1e-6) ? 0.0 : (1.0 / depth01);
                return o;
            }

            struct FragOut { float4 colorAccum:SV_Target0; float4 weightAccum:SV_Target1; };

            FragOut Frag(v2f i)
            {
                FragOut o;

                float2 p = i.uv * 0.5 + 0.5;
                float2 d = p - 0.5;
                float r2 = dot(d,d) * 4.0;

                float weight;
                if (_Gaussian > 0.5)
                {
                    float sigma2 = 0.25 / max(_KernelSharpness, 1e-3);
                    weight = exp(-r2 / sigma2);
                }
                else
                {
                    float dlin = saturate(1.0 - sqrt(r2));
                    weight = pow(dlin, max(_KernelSharpness, 1.0));
                }

                // Front-visibility match with scaled epsilon
                float invFront = SAMPLE_TEXTURE2D(_PcdDepthRT, sampler_PcdDepthRT, i.uvTex).r;
                float epsScaled = _DepthMatchEps * (1.0 + 0.5 * _PointSize);
                if (_DepthMatchEps > 0.0 && invFront > 0.0 && abs(i.invDepth - invFront) > epsScaled)
                {
                    weight = 0.0;
                }

                weight *= saturate(_NodeFade);

                float3 col = (_HasColor == 1) ? UnpackRGBA8(_Colors[i.pid]) : 1.0.xxx;

                o.colorAccum  = float4(col * weight, weight);
                o.weightAccum = float4(weight, 0, 0, 0);
                return o;
            }
            ENDHLSL
        }
    }
}*/
