// Shell fur layer shader for the Universal Render Pipeline.
// One instance of this material is drawn per shell; ShellFur.cs sets _ShellIndex/_ShellCount.
Shader "Custom/ShellFur URP"
{
    Properties
    {
        _MainTex ("Albedo", 2D) = "white" {}
        _Color ("Tint", Color) = (1, 1, 1, 1)
        _FurLength ("Fur Length", Float) = 0.045
        _Density ("Strand Density", Float) = 220
        _Thickness ("Strand Thickness", Range(0.05, 1)) = 0.85
        _Gravity ("Gravity Droop", Float) = 0.5
        _RootShade ("Root Darkening", Range(0, 1)) = 0.45
        _BareBelow ("No Fur On Dark Albedo Below", Range(0, 0.5)) = 0.07
        _ShellIndex ("Shell Index", Float) = 0
        _ShellCount ("Shell Count", Float) = 24
        // Opt-in: scale fur length per vertex by vertex colour red (the fox and hare
        // carry short-face masks). Off by default so meshes without one are unchanged.
        _UseLengthMask ("Length From Vertex Colour", Float) = 0
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "Queue" = "AlphaTest" "RenderType" = "TransparentCutout" }
        Cull Off

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                half4 _Color;
                float _FurLength, _Density, _Thickness, _Gravity, _RootShade, _ShellIndex, _ShellCount, _BareBelow;
                float _UseLengthMask;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
                float4 color : COLOR;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 basePos : TEXCOORD1;
                float3 normalOS : TEXCOORD2;
                float3 normalWS : TEXCOORD3;
                float3 positionWS : TEXCOORD4;
                float h : TEXCOORD5;
            };

            float hash13(float3 p)
            {
                p = frac(p * 0.1031);
                p += dot(p, p.yzx + 33.33);
                return frac((p.x + p.y) * p.z);
            }

            float3 hash33(float3 p)
            {
                p = frac(p * float3(0.1031, 0.1030, 0.0973));
                p += dot(p, p.yxz + 33.33);
                return frac((p.xxy + p.yxx) * p.zyx);
            }

            Varyings vert(Attributes v)
            {
                Varyings o;
                float h = (_ShellIndex + 1.0) / _ShellCount;
                float3 n = normalize(v.normalOS);
                float3 downOS = normalize(TransformWorldToObjectDir(float3(0, -1, 0)));
                float len = _FurLength * lerp(1.0, v.color.r, _UseLengthMask);
                float3 offset = n * (len * h) + downOS * (len * _Gravity * h * h);
                float3 posOS = v.positionOS.xyz + offset;
                o.positionWS = TransformObjectToWorld(posOS);
                o.positionCS = TransformWorldToHClip(o.positionWS);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                o.basePos = v.positionOS.xyz;
                o.normalOS = n;
                o.normalWS = TransformObjectToWorldNormal(n);
                o.h = h;
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                float3 g = i.basePos * _Density;
                float3 cell = floor(g);
                float strandHeight = hash13(cell);
                clip(strandHeight - i.h);

                float3 center = cell + 0.5 + (hash33(cell) - 0.5) * 0.6;
                float3 d = g - center;
                float3 n = normalize(i.normalOS);
                d -= n * dot(d, n);
                float radius = 0.5 * _Thickness * (1.0 - 0.75 * i.h / max(strandHeight, 0.001));
                clip(radius - length(d));

                half4 albedo = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv) * _Color;
                clip(dot(albedo.rgb, float3(0.3, 0.59, 0.11)) - _BareBelow);   // eyes and nose stay bare
                float3 wn = normalize(i.normalWS);
                Light mainLight = GetMainLight(TransformWorldToShadowCoord(i.positionWS));
                float wrap = saturate(dot(wn, mainLight.direction) * 0.6 + 0.4);   // soft wrap lighting suits fur
                float3 light = mainLight.color * mainLight.shadowAttenuation * wrap + SampleSH(wn);
                float shade = lerp(1.0 - _RootShade, 1.0, i.h);
                return half4(albedo.rgb * light * shade, 1);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
