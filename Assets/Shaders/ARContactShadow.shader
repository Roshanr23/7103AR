// Soft dark patch right under an animal (ambient occlusion where it meets the
// floor). Real light from a room is mostly diffuse, so this soft contact
// darkening grounds an animal more than any sharp shadow does.
// Alpha comes from a radial falloff computed here -- no texture needed.
Shader "Custom/AR Contact Shadow"
{
    Properties
    {
        _Opacity ("Opacity", Range(0, 1)) = 0.5
        _Softness ("Softness", Range(0.5, 4)) = 1.6
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent+10" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }

        Pass
        {
            Name "ContactShadow"
            Tags { "LightMode" = "UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float _Opacity;
                float _Softness;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; };
            struct Varyings   { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };

            Varyings vert(Attributes i)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(i.positionOS.xyz);
                o.uv = i.uv;
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                float r = saturate(length(i.uv * 2.0 - 1.0));
                // dense in the middle, long soft tail: (1 - r^2)^softness
                half a = pow(saturate(1.0 - r * r), _Softness);
                return half4(0, 0, 0, a * _Opacity);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
