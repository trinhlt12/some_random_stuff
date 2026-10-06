#ifndef DAYNIGHTSKY_WET_LIT_INPUT_INCLUDED
#define DAYNIGHTSKY_WET_LIT_INPUT_INCLUDED

// SurfaceInput declares _BaseMap and Alpha(), which URP's shadow/depth passes use.
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/SurfaceInput.hlsl"

// Every pass includes this file so the SRP Batcher sees one identical UnityPerMaterial buffer.
CBUFFER_START(UnityPerMaterial)
    float4 _BaseMap_ST;
    half4 _BaseColor;
    half _Smoothness;
    half _Cutoff;
    half _ReceiveWetness;
    half _WetExposure;
    half _WetDarkening;
    half _WetSmoothness;
CBUFFER_END

#include "WeatherWetness.hlsl"

#endif
