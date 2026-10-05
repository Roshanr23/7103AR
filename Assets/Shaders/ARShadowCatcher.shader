// Invisible floor that only shows the shadows falling on it, so the animals'
// real-time shadows land on the camera image of your actual floor.
// Draws black with alpha = how much of the main light is blocked here, fading
// out toward the edge of the quad (UV radius) so its outline never shows.
Shader "Custom/AR Shadow Catcher"
{
    Properties
    {
        _ShadowStrength ("Shadow Strength", Range(0, 1)) = 0.5
        _EdgeFade ("Edge Fade (UV radius)", Range(0.01, 1)) = 0.35
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent-20" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }

        Pass
        {
            Name "ShadowCatcher"
            Tags { "LightMode" = "UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float _ShadowStrength;
                float _EdgeFade;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; };
            struct Varyings   { float4 positionCS : SV_POSITION; float3 positionWS : TEXCOORD0; float2 uv : TEXCOORD1; };

            Varyings vert(Attributes i)
            {
                Varyings o;
                o.positionWS = TransformObjectToWorld(i.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(o.positionWS);
                o.uv = i.uv;
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                float4 coord = TransformWorldToShadowCoord(i.positionWS);
                Light light = GetMainLight(coord);
                half blocked = 1.0h - light.shadowAttenuation;
                float r = length(i.uv * 2.0 - 1.0);
                half edge = saturate((1.0 - r) / _EdgeFade);
                return half4(0, 0, 0, blocked * _ShadowStrength * edge);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
