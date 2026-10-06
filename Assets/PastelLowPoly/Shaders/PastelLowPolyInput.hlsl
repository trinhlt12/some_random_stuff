#ifndef PASTEL_LOW_POLY_INPUT_INCLUDED
#define PASTEL_LOW_POLY_INPUT_INCLUDED

// SurfaceInput declares _BaseMap and Alpha(), which URP's shadow/depth passes use for alpha clipping.
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/SurfaceInput.hlsl"

// Every pass includes this file so the SRP Batcher sees one identical UnityPerMaterial buffer.
CBUFFER_START(UnityPerMaterial)
    float4 _BaseMap_ST;
    half4 _BaseColor;
    half _VertexColorIsSRGB;
    half _TextureDetailBlur;
    half _Cutoff;
    half _PastelAmount;
    half _PastelLightMin;
    half _PastelLightMax;
    half _PastelSaturation;
    half _PastelChromaMax;
    half4 _ShadowTint;
    half _ShadowTintStrength;
    half4 _AmbientColor;
    half _AmbientIntensity;
    half _SceneAmbient;
    half _LightInfluence;
    half _Wrap;
    half _Bands;
    half _BandSoftness;
    half _ShadowStrength;
    half _OcclusionStrength;
    half _Brightness;
    half _Contrast;
    half _HighlightKnee;
    half _WetExposure;
    half _WetDarkening;
CBUFFER_END

// Shared weather wetness (0..1), a global written by DayNightSky's WetnessController; unset = 0 = dry.
float _WeatherWetness;

// World-space normal of the triangle under this pixel, from screen-space derivatives of the
// camera-relative position (camera-relative keeps precision far from the origin). The sign of a
// derivative cross product depends on the graphics API's screen orientation, so the result is
// oriented towards the viewer: every rasterised face is seen from its visible side.
float3 FlatNormalWS(float3 positionWS)
{
    float3 p = positionWS - GetCameraPositionWS();
    float3 n = normalize(cross(ddy(p), ddx(p)));
    float3 toViewer = -p;
    // Orthographic cameras look along a fixed direction, not towards the camera position.
    if (unity_OrthoParams.w > 0.5)
        toViewer = -GetViewForwardDir();
    return dot(n, toViewer) < 0.0 ? -n : n;
}

// --- OKLab (Björn Ottosson). Operates on linear sRGB. Perceptual lightness L in [0,1]. ---

float3 LinearToOklab(float3 c)
{
    float l = 0.4122214708 * c.r + 0.5363325363 * c.g + 0.0514459929 * c.b;
    float m = 0.2119034982 * c.r + 0.6806995451 * c.g + 0.1073969566 * c.b;
    float s = 0.0883024619 * c.r + 0.2817188376 * c.g + 0.6299787005 * c.b;
    l = pow(max(l, 0.0), 1.0 / 3.0);
    m = pow(max(m, 0.0), 1.0 / 3.0);
    s = pow(max(s, 0.0), 1.0 / 3.0);
    return float3(
        0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s,
        1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s,
        0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s);
}

float3 OklabToLinear(float3 c)
{
    float l = c.x + 0.3963377774 * c.y + 0.2158037573 * c.z;
    float m = c.x - 0.1055613458 * c.y - 0.0638541728 * c.z;
    float s = c.x - 0.0894841775 * c.y - 1.2914855480 * c.z;
    l = l * l * l;
    m = m * m * m;
    s = s * s * s;
    return float3(
        4.0767416621 * l - 3.3077118913 * m + 0.2309699292 * s,
        -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s,
        -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s);
}

// Turns any source colour into a pastel version of the same hue: lightness is remapped into a
// light range (darker parts stay relatively darker) and chroma is scaled and capped.
half3 Pastelize(half3 linearColor)
{
    float3 lab = LinearToOklab(linearColor);
    float chroma = length(lab.yz);
    float newChroma = min(chroma * _PastelSaturation, _PastelChromaMax);
    float2 ab = chroma > 1e-5 ? lab.yz * (newChroma / chroma) : float2(0.0, 0.0);
    float lightness = lerp(_PastelLightMin, _PastelLightMax, saturate(lab.x));

    float3 result = OklabToLinear(float3(lightness, ab));
    // Very light, saturated colours can fall outside sRGB; pull chroma in until they fit.
    [unroll] for (int i = 0; i < 3; i++)
    {
        if (all(result >= 0.0) && all(result <= 1.0)) break;
        ab *= 0.7;
        result = OklabToLinear(float3(lightness, ab));
    }
    return lerp(linearColor, saturate(result), _PastelAmount);
}

#endif
