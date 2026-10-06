// Thin rain streaks for stretched-billboard particles (URP, mobile friendly).
// Unlit, alpha blended, no texture: the streak shape is an analytic soft profile on the quad UVs
// (the same on both axes, so it works whichever UV axis the particle stretch maps to). The quad's
// length/width ratio comes from the particle renderer. Drops very close to the camera fade out so
// they never cover the view. No lighting, shadows, soft particles or refraction.
Shader "DayNightSky/Rain Streak"
{
    Properties
    {
        // Overridden per renderer by RainController (MaterialPropertyBlock) to follow day/night.
        _BaseColor("Color (A = opacity)", Color) = (0.78, 0.84, 0.95, 0.42)
        // x: distance (m) where drops start to appear, y: 1 / fade range (m)
        _NearFade("Near Fade (x start, y 1/range)", Vector) = (0.6, 0.83, 0, 0)
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent"
            "RenderType" = "Transparent"
            "IgnoreProjector" = "True"
            "PreviewType" = "Plane"
            "RenderPipeline" = "UniversalPipeline"
        }

        Pass
        {
            Name "RainStreak"
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
                float4 _NearFade;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                half4 color : COLOR;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                half4 color : COLOR;
                half2 uv : TEXCOORD0;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.positionCS = TransformWorldToHClip(positionWS);
                half nearFade = (half)saturate((distance(positionWS, GetCameraPositionWS()) - _NearFade.x) * _NearFade.y);
                output.color = half4(input.color.rgb * _BaseColor.rgb, input.color.a * _BaseColor.a * nearFade);
                output.uv = (half2)(input.uv * 2.0 - 1.0);
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                // Soft across the width, tapered towards both ends.
                half2 d = input.uv * input.uv;
                half shape = saturate(1.0h - d.x) * saturate(1.0h - d.y);
                return half4(input.color.rgb, input.color.a * shape);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
