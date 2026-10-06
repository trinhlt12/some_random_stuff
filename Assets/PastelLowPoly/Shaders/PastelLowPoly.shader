// Matte pastel shading for low-poly scenes (URP 17 / Unity 6).
// The shader only changes how surfaces are lit. It does not reduce polygons or change a model's
// shape; "Flat" normal mode shades each triangle with its own normal but keeps every triangle.
Shader "Pastel/Low Poly Pastel"
{
    Properties
    {
        [Header(Colour)]
        [MainColor] _BaseColor("Base Color", Color) = (0.98, 0.76, 0.69, 1)
        [Toggle(_USE_VERTEX_COLOR)] _UseVertexColor("Use Vertex Color", Float) = 0
        [ToggleUI] _VertexColorIsSRGB("Vertex Color Is sRGB", Float) = 1
        [Toggle(_USE_BASE_MAP)] _UseBaseMap("Use Base Map", Float) = 0
        [MainTexture] _BaseMap("Base Map", 2D) = "white" {}
        _TextureDetailBlur("Texture Detail Blur (mip bias, keep 0 for palette textures)", Range(0, 8)) = 0

        [Header(Pastelize source colours)]
        [Toggle(_PASTELIZE)] _Pastelize("Pastelize (keep each part's hue, make it pastel)", Float) = 0
        _PastelAmount("Pastel Amount", Range(0, 1)) = 1
        _PastelLightMin("Lightness of darkest parts", Range(0, 1)) = 0.6
        _PastelLightMax("Lightness of brightest parts", Range(0, 1)) = 0.93
        _PastelSaturation("Colour Strength", Range(0, 2)) = 0.75
        _PastelChromaMax("Max Colour Strength", Range(0, 0.3)) = 0.11

        [Header(Alpha)]
        [Toggle(_ALPHATEST_ON)] _AlphaClip("Alpha Clip (cutout)", Float) = 0
        _Cutoff("Alpha Cutoff", Range(0, 1)) = 0.5

        [Header(Shape)]
        [KeywordEnum(Mesh, Flat)] _NormalMode("Normals", Float) = 1

        [Header(Light)]
        _LightInfluence("Directional Light Influence", Range(0, 2)) = 0.75
        _Wrap("Light Wrap (soft falloff)", Range(0, 1)) = 0.45
        [IntRange] _Bands("Light Bands (0 = smooth)", Range(0, 6)) = 0
        _BandSoftness("Band Softness", Range(0.01, 1)) = 0.6

        [Header(Ambient and Shadow)]
        _AmbientColor("Ambient Color", Color) = (0.86, 0.82, 0.88, 1)
        _AmbientIntensity("Ambient Intensity", Range(0, 1.5)) = 0.7
        _SceneAmbient("Scene Ambient (Lighting window) Mix", Range(0, 1)) = 0
        _ShadowTint("Shadow Tint", Color) = (0.62, 0.55, 0.82, 1)
        _ShadowTintStrength("Shadow Tint Strength", Range(0, 1)) = 0.5
        _ShadowStrength("Received Shadow Strength", Range(0, 1)) = 0.55
        _OcclusionStrength("SSAO Strength", Range(0, 1)) = 0.35

        [Header(Tone)]
        _Brightness("Brightness", Range(0.5, 1.5)) = 1
        _Contrast("Contrast", Range(0.5, 1.5)) = 0.85
        _HighlightKnee("Highlight Soft Clip Start", Range(0.5, 1)) = 0.85

        [Header(Weather Wetness)]
        // Matte shader: wetness only darkens the colour (no specular, so no gloss). 0 = never wet.
        _WetExposure("Rain Exposure (0 = never wet)", Range(0, 1)) = 0
        _WetDarkening("Wet Darkening", Range(0, 1)) = 0.3

        [Header(Advanced)]
        [Enum(UnityEngine.Rendering.CullMode)] _Cull("Cull", Float) = 2
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
            Name "PastelForward"
            // ForwardOnly: drawn in Forward, Forward+ and Deferred (as a forward-only material).
            // All lighting happens here; no URP lit pass runs on top of it.
            Tags { "LightMode" = "UniversalForwardOnly" }

            Cull [_Cull]
            ZWrite On

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex PastelVertex
            #pragma fragment PastelFragment

            #pragma shader_feature_local _NORMALMODE_MESH _NORMALMODE_FLAT
            #pragma shader_feature_local _USE_VERTEX_COLOR
            #pragma shader_feature_local_fragment _USE_BASE_MAP
            #pragma shader_feature_local_fragment _PASTELIZE
            #pragma shader_feature_local _ALPHATEST_ON

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fragment _ _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile_fog
            #pragma multi_compile_instancing
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"

            #include "PastelLowPolyInput.hlsl"
            #include "PastelLowPolyPasses.hlsl"
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull [_Cull]

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex ShadowPassVertex
            #pragma fragment ShadowPassFragment

            #pragma shader_feature_local _ALPHATEST_ON

            #pragma multi_compile_instancing
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            #include "PastelLowPolyInput.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/Shaders/ShadowCasterPass.hlsl"
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            ZWrite On
            ColorMask R
            Cull [_Cull]

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex DepthOnlyVertex
            #pragma fragment DepthOnlyFragment

            #pragma shader_feature_local _ALPHATEST_ON

            #pragma multi_compile_instancing
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"

            #include "PastelLowPolyInput.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/Shaders/DepthOnlyPass.hlsl"
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }

            ZWrite On
            Cull [_Cull]

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex PastelDepthNormalsVertex
            #pragma fragment PastelDepthNormalsFragment

            #pragma shader_feature_local _NORMALMODE_MESH _NORMALMODE_FLAT
            #pragma shader_feature_local _ALPHATEST_ON
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RenderingLayers.hlsl"
            #pragma multi_compile_instancing
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"

            #include "PastelLowPolyInput.hlsl"
            #include "PastelLowPolyPasses.hlsl"
            ENDHLSL
        }

        Pass
        {
            // Used instead of DepthNormals when the renderer runs in Deferred mode.
            Name "DepthNormalsOnly"
            Tags { "LightMode" = "DepthNormalsOnly" }

            ZWrite On
            Cull [_Cull]

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex PastelDepthNormalsVertex
            #pragma fragment PastelDepthNormalsFragment

            #pragma shader_feature_local _NORMALMODE_MESH _NORMALMODE_FLAT
            #pragma shader_feature_local _ALPHATEST_ON
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RenderingLayers.hlsl"
            #pragma multi_compile_instancing
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"

            #include "PastelLowPolyInput.hlsl"
            #include "PastelLowPolyPasses.hlsl"
            ENDHLSL
        }
    }

    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}
