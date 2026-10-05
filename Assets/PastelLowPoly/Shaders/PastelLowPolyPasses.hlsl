#ifndef PASTEL_LOW_POLY_PASSES_INCLUDED
#define PASTEL_LOW_POLY_PASSES_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

// ---------------------------------------------------------------------------------------------
// Forward pass
// ---------------------------------------------------------------------------------------------

struct PastelAttributes
{
    float4 positionOS : POSITION;
    float3 normalOS   : NORMAL;
    float2 uv         : TEXCOORD0;
    half4 color       : COLOR;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct PastelVaryings
{
    float4 positionCS : SV_POSITION;
    float3 positionWS : TEXCOORD0;
    float3 normalWS   : TEXCOORD1;
    float2 uv         : TEXCOORD2;
    half4 color       : TEXCOORD3;
    half fogFactor    : TEXCOORD4;
    UNITY_VERTEX_INPUT_INSTANCE_ID
    UNITY_VERTEX_OUTPUT_STEREO
};

PastelVaryings PastelVertex(PastelAttributes input)
{
    PastelVaryings output = (PastelVaryings)0;
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

    VertexPositionInputs positions = GetVertexPositionInputs(input.positionOS.xyz);
    output.positionCS = positions.positionCS;
    output.positionWS = positions.positionWS;
    // Inverse-transpose transform: correct under non-uniform scale.
    output.normalWS = TransformObjectToWorldNormal(input.normalOS);
    output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
    output.color = input.color;
    output.fogFactor = ComputeFogFactor(positions.positionCS.z);
    return output;
}

// Source colour of the surface: material colour x vertex colour x base map, then optionally
// turned into a pastel of the same hue. Alpha is only used for clipping.
half4 PastelAlbedo(PastelVaryings input)
{
    half4 albedo = _BaseColor;

    #if defined(_USE_VERTEX_COLOR)
        half4 vc = input.color;
        // A mesh without a colour stream can read back as all zeros on some platforms; treat that
        // as "no vertex colour" so enabling the option never turns the object black.
        if (all(vc == 0))
            vc = 1;
        #if !defined(UNITY_COLORSPACE_GAMMA)
            if (_VertexColorIsSRGB > 0.5)
                vc.rgb = SRGBToLinear(vc.rgb);
        #endif
        albedo *= vc;
    #endif

    #if defined(_USE_BASE_MAP) || defined(_ALPHATEST_ON)
        // Positive bias reads a smaller mip: averages away fine texture detail (wood grain, fabric).
        half4 tex = SAMPLE_TEXTURE2D_BIAS(_BaseMap, sampler_BaseMap, input.uv, _TextureDetailBlur);
        #if defined(_USE_BASE_MAP)
            albedo.rgb *= tex.rgb;
        #endif
        albedo.a *= tex.a;
    #endif

    #if defined(_ALPHATEST_ON)
        clip(albedo.a - _Cutoff);
    #endif

    #if defined(_PASTELIZE)
        albedo.rgb = Pastelize(albedo.rgb);
    #endif

    return albedo;
}

// Soft, optionally banded response to the main light. Bands are blended, never hard steps.
half PastelDiffuse(half nDotL)
{
    half wrapped = saturate((nDotL + _Wrap) / (1.0 + _Wrap));
    if (_Bands >= 1.0)
    {
        half x = wrapped * _Bands;
        half f = frac(x);
        half halfWidth = max(_BandSoftness, 0.001) * 0.5;
        wrapped = (floor(x) + smoothstep(0.5 - halfWidth, 0.5 + halfWidth, f)) / _Bands;
    }
    return wrapped;
}

// Compress values above the knee towards 1 by scaling all channels together, so bright faces
// keep their hue and saturation instead of clipping to white.
half3 SoftHighlight(half3 c)
{
    half m = max(c.r, max(c.g, c.b));
    half knee = _HighlightKnee;
    if (m <= knee) return c;
    half range = max(1.0 - knee, 1e-3);
    half compressed = knee + range * (1.0 - exp(-(m - knee) / range));
    return c * (compressed / m);
}

half4 PastelFragment(PastelVaryings input, FRONT_FACE_TYPE isFrontFace : FRONT_FACE_SEMANTIC) : SV_Target
{
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

    half3 albedo = PastelAlbedo(input).rgb;

    #if defined(_NORMALMODE_FLAT)
        float3 normalWS = FlatNormalWS(input.positionWS);
    #else
        // With Cull Off the back side of a thin surface uses the opposite normal.
        float3 normalWS = normalize(input.normalWS) * IS_FRONT_VFACE(isFrontFace, 1.0, -1.0);
    #endif

    // Main directional light. With no directional light URP reports a black light, leaving ambient.
    float4 shadowCoord = TransformWorldToShadowCoord(input.positionWS);
    Light mainLight = GetMainLight(shadowCoord, input.positionWS, half4(1, 1, 1, 1));

    // Two separate terms, combined with min() so a face that is both turned away and shadowed is
    // not darkened twice.
    half facing = PastelDiffuse(dot(normalWS, mainLight.direction));
    half shadow = lerp(1.0, mainLight.shadowAttenuation, _ShadowStrength);
    half lit = min(facing, shadow);

    half3 ambient = _AmbientColor.rgb * _AmbientIntensity;
    ambient += SampleSH(normalWS) * _SceneAmbient;

    half directOcclusion = 1.0;
    #if defined(_SCREEN_SPACE_OCCLUSION)
        AmbientOcclusionFactor ao = GetScreenSpaceAmbientOcclusion(GetNormalizedScreenSpaceUV(input.positionCS));
        ambient *= lerp(1.0, ao.indirectAmbientOcclusion, _OcclusionStrength);
        directOcclusion = lerp(1.0, ao.directAmbientOcclusion, _OcclusionStrength);
    #endif

    // The unlit part of the ambient is tinted; fully lit surfaces (lit == 1) get no tint at all.
    half3 tint = lerp(half3(1, 1, 1), _ShadowTint.rgb, _ShadowTintStrength);
    half3 lighting = ambient * lerp(tint, half3(1, 1, 1), lit)
                   + mainLight.color * (_LightInfluence * lit * directOcclusion);

    // Gentle contrast around mid grey, then brightness.
    lighting = max(0.0, (lighting - 0.5) * _Contrast + 0.5) * _Brightness;

    half3 color = SoftHighlight(albedo * lighting);
    color = MixFog(color, input.fogFactor);
    return half4(color, 1.0);
}

// ---------------------------------------------------------------------------------------------
// Depth + normals (SSAO and other effects that read the normals texture)
// ---------------------------------------------------------------------------------------------

struct PastelDepthNormalsAttributes
{
    float4 positionOS : POSITION;
    float3 normalOS   : NORMAL;
    float2 uv         : TEXCOORD0;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct PastelDepthNormalsVaryings
{
    float4 positionCS : SV_POSITION;
    float3 positionWS : TEXCOORD0;
    float3 normalWS   : TEXCOORD1;
    float2 uv         : TEXCOORD2;
    UNITY_VERTEX_INPUT_INSTANCE_ID
    UNITY_VERTEX_OUTPUT_STEREO
};

PastelDepthNormalsVaryings PastelDepthNormalsVertex(PastelDepthNormalsAttributes input)
{
    PastelDepthNormalsVaryings output = (PastelDepthNormalsVaryings)0;
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

    VertexPositionInputs positions = GetVertexPositionInputs(input.positionOS.xyz);
    output.positionCS = positions.positionCS;
    output.positionWS = positions.positionWS;
    output.normalWS = TransformObjectToWorldNormal(input.normalOS);
    output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
    return output;
}

void PastelDepthNormalsFragment(PastelDepthNormalsVaryings input, FRONT_FACE_TYPE isFrontFace : FRONT_FACE_SEMANTIC,
    out half4 outNormalWS : SV_Target0
#ifdef _WRITE_RENDERING_LAYERS
    , out uint outRenderingLayers : SV_Target1
#endif
)
{
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

    #if defined(_ALPHATEST_ON)
        Alpha(SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv).a, _BaseColor, _Cutoff);
    #endif

    #if defined(_NORMALMODE_FLAT)
        float3 normalWS = FlatNormalWS(input.positionWS);
    #else
        float3 normalWS = normalize(input.normalWS) * IS_FRONT_VFACE(isFrontFace, 1.0, -1.0);
    #endif

    #if defined(_GBUFFER_NORMALS_OCT)
        float2 octNormalWS = PackNormalOctQuadEncode(normalWS);
        float2 remapped = saturate(octNormalWS * 0.5 + 0.5);
        outNormalWS = half4(PackFloat2To888(remapped), 0.0);
    #else
        outNormalWS = half4(normalWS, 0.0);
    #endif

    #ifdef _WRITE_RENDERING_LAYERS
        outRenderingLayers = EncodeMeshRenderingLayer();
    #endif
}

#endif
