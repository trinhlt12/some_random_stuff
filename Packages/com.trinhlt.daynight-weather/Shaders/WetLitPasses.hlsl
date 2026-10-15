#ifndef DAYNIGHTSKY_WET_LIT_PASSES_INCLUDED
#define DAYNIGHTSKY_WET_LIT_PASSES_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

// ---------------------------------------------------------------------------------------------
// Forward pass: URP's standard PBR lighting (main light, shadows, ambient SH, environment
// reflection, fog) with the wet albedo and smoothness. Additional lights are not evaluated.
// ---------------------------------------------------------------------------------------------

struct WetAttributes
{
    float4 positionOS : POSITION;
    float3 normalOS   : NORMAL;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct WetVaryings
{
    float4 positionCS : SV_POSITION;
    float3 positionWS : TEXCOORD0;
    float3 normalWS   : TEXCOORD1;
    half fogFactor    : TEXCOORD2;
    UNITY_VERTEX_INPUT_INSTANCE_ID
    UNITY_VERTEX_OUTPUT_STEREO
};

WetVaryings WetVertex(WetAttributes input)
{
    WetVaryings output = (WetVaryings)0;
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

    VertexPositionInputs positions = GetVertexPositionInputs(input.positionOS.xyz);
    output.positionCS = positions.positionCS;
    output.positionWS = positions.positionWS;
    output.normalWS = TransformObjectToWorldNormal(input.normalOS);
    output.fogFactor = ComputeFogFactor(positions.positionCS.z);
    return output;
}

half4 WetFragment(WetVaryings input) : SV_Target
{
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

    half wet = WeatherWetAmount(_WetExposure, _ReceiveWetness);

    SurfaceData surface = (SurfaceData)0;
    surface.albedo = WeatherWetAlbedo(_BaseColor.rgb, wet, _WetDarkening);
    surface.alpha = 1.0h;
    surface.metallic = 0.0h;
    surface.specular = half3(0.0h, 0.0h, 0.0h);
    surface.smoothness = WeatherWetSmoothness(_Smoothness, wet, _WetSmoothness);
    surface.occlusion = 1.0h;
    surface.normalTS = half3(0.0h, 0.0h, 1.0h);

    InputData inputData = (InputData)0;
    inputData.positionWS = input.positionWS;
    inputData.positionCS = input.positionCS;
    inputData.normalWS = NormalizeNormalPerPixel(input.normalWS);
    inputData.viewDirectionWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
    inputData.shadowCoord = TransformWorldToShadowCoord(input.positionWS);
    inputData.fogCoord = input.fogFactor;
    inputData.bakedGI = SampleSH(inputData.normalWS);
    inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
    inputData.shadowMask = half4(1.0h, 1.0h, 1.0h, 1.0h);

    half4 color = UniversalFragmentPBR(inputData, surface);
    color.rgb = MixFog(color.rgb, input.fogFactor);
    color.a = 1.0h;
    return color;
}

// ---------------------------------------------------------------------------------------------
// Depth + normals (SSAO and other effects that read the normals texture)
// ---------------------------------------------------------------------------------------------

struct WetDepthNormalsAttributes
{
    float4 positionOS : POSITION;
    float3 normalOS   : NORMAL;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct WetDepthNormalsVaryings
{
    float4 positionCS : SV_POSITION;
    float3 normalWS   : TEXCOORD0;
    UNITY_VERTEX_INPUT_INSTANCE_ID
    UNITY_VERTEX_OUTPUT_STEREO
};

WetDepthNormalsVaryings WetDepthNormalsVertex(WetDepthNormalsAttributes input)
{
    WetDepthNormalsVaryings output = (WetDepthNormalsVaryings)0;
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
    output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
    output.normalWS = TransformObjectToWorldNormal(input.normalOS);
    return output;
}

void WetDepthNormalsFragment(WetDepthNormalsVaryings input,
    out half4 outNormalWS : SV_Target0
#ifdef _WRITE_RENDERING_LAYERS
    , out uint outRenderingLayers : SV_Target1
#endif
)
{
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
    float3 normalWS = normalize(input.normalWS);

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
