// Shell fur layer shader for the Built-in Render Pipeline.
// One instance of this material is drawn per shell; ShellFur.cs sets _ShellIndex/_ShellCount.
Shader "Custom/ShellFur BuiltIn"
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
    }

    SubShader
    {
        Tags { "Queue" = "AlphaTest" "RenderType" = "TransparentCutout" "IgnoreProjector" = "True" }
        Cull Off

        Pass
        {
            Tags { "LightMode" = "ForwardBase" }

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fwdbase
            #include "UnityCG.cginc"
            #include "Lighting.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;
            fixed4 _Color;
            float _FurLength, _Density, _Thickness, _Gravity, _RootShade, _ShellIndex, _ShellCount, _BareBelow;

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 basePos : TEXCOORD1;    // un-offset object-space position: strand pattern stays aligned across shells
                float3 normalOS : TEXCOORD2;
                float3 normalWS : TEXCOORD3;
                float h : TEXCOORD4;           // 0 at the skin, 1 at the fur tips
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

            v2f vert(appdata v)
            {
                v2f o;
                float h = (_ShellIndex + 1.0) / _ShellCount;
                float3 n = normalize(v.normal);
                float3 downOS = normalize(mul((float3x3)unity_WorldToObject, float3(0, -1, 0)));
                float3 offset = n * (_FurLength * h) + downOS * (_FurLength * _Gravity * h * h);
                o.pos = UnityObjectToClipPos(v.vertex + float4(offset, 0));
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                o.basePos = v.vertex.xyz;
                o.normalOS = n;
                o.normalWS = UnityObjectToWorldNormal(n);
                o.h = h;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                // one strand per 3D cell of the un-offset surface
                float3 g = i.basePos * _Density;
                float3 cell = floor(g);
                float strandHeight = hash13(cell);
                clip(strandHeight - i.h);                        // strand too short to reach this shell

                float3 center = cell + 0.5 + (hash33(cell) - 0.5) * 0.6;
                float3 d = g - center;
                float3 n = normalize(i.normalOS);
                d -= n * dot(d, n);                              // distance measured across the surface
                float radius = 0.5 * _Thickness * (1.0 - 0.75 * i.h / max(strandHeight, 0.001)); // taper to the tip
                clip(radius - length(d));

                fixed4 albedo = tex2D(_MainTex, i.uv) * _Color;
                clip(dot(albedo.rgb, float3(0.3, 0.59, 0.11)) - _BareBelow);   // eyes and nose stay bare
                float3 wn = normalize(i.normalWS);
                float wrap = saturate(dot(wn, _WorldSpaceLightPos0.xyz) * 0.6 + 0.4);   // soft wrap lighting suits fur
                float3 light = _LightColor0.rgb * wrap + ShadeSH9(float4(wn, 1));
                float shade = lerp(1.0 - _RootShade, 1.0, i.h);
                return fixed4(albedo.rgb * light * shade, 1);
            }
            ENDCG
        }
    }
    Fallback Off
}
