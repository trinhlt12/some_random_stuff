// Sample lit shader (URP PBR) with weather wetness, for the wetness demo objects.
// Wetness comes from one global float, _WeatherWetness (0..1), written only by WetnessController.
// Per material: rain exposure, darkening and smoothness boost, or no wetness at all. At wetness 0
// the result is the plain dry material. Dielectric only (metallic 0) and the wet smoothness is
// capped, so soil and stone never turn into metal, glass or mirrors. No textures, no extra passes,
// no wetness keywords.
Shader "DayNightSky/Wet Lit"
{
    Properties
    {
        [MainColor] _BaseColor("Base Color", Color) = (0.6, 0.6, 0.6, 1)
        _Smoothness("Smoothness (dry)", Range(0, 1)) = 0.2

        [Header(Weather Wetness)]
        [ToggleUI] _ReceiveWetness("Receive Wetness", Float) = 1
        _WetExposure("Rain Exposure (how much of the global wetness reaches it)", Range(0, 1)) = 1
        _WetDarkening("Wet Darkening", Range(0, 1)) = 0.3
        _WetSmoothness("Wet Smoothness Boost", Range(0, 1)) = 0.45

        // Unused; lets URP's shadow/depth passes compile against SurfaceInput.
        [HideInInspector] _BaseMap("Base Map", 2D) = "white" {}
        [HideInInspector] _Cutoff("Alpha Cutoff", Range(0, 1)) = 0.5
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Geometry"
        }

        Pass
        {
            Name "WetForward"
            // ForwardOnly: drawn in Forward, Forward+ and Deferred.
            Tags { "LightMode" = "UniversalForwardOnly" }

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex WetVertex
            #pragma fragment WetFragment

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fragment _ _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile_fog
            #pragma multi_compile_instancing
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"

            #include "WetLitInput.hlsl"
            #include "WetLitPasses.hlsl"
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex ShadowPassVertex
            #pragma fragment ShadowPassFragment

            #pragma multi_compile_instancing
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            #include "WetLitInput.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/Shaders/ShadowCasterPass.hlsl"
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            ZWrite On
            ColorMask R

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex DepthOnlyVertex
            #pragma fragment DepthOnlyFragment

            #pragma multi_compile_instancing
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"

            #include "WetLitInput.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/Shaders/DepthOnlyPass.hlsl"
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }

            ZWrite On

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex WetDepthNormalsVertex
            #pragma fragment WetDepthNormalsFragment

            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RenderingLayers.hlsl"
            #pragma multi_compile_instancing
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"

            #include "WetLitInput.hlsl"
            #include "WetLitPasses.hlsl"
            ENDHLSL
        }

        Pass
        {
            // Used instead of DepthNormals when the renderer runs in Deferred mode.
            Name "DepthNormalsOnly"
            Tags { "LightMode" = "DepthNormalsOnly" }

            ZWrite On

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex WetDepthNormalsVertex
            #pragma fragment WetDepthNormalsFragment

            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RenderingLayers.hlsl"
            #pragma multi_compile_instancing
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"

            #include "WetLitInput.hlsl"
            #include "WetLitPasses.hlsl"
            ENDHLSL
        }
    }

    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}
