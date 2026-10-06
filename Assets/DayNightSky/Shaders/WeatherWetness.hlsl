#ifndef DAYNIGHTSKY_WEATHER_WETNESS_INCLUDED
#define DAYNIGHTSKY_WEATHER_WETNESS_INCLUDED

// Shared environment wetness, 0..1. Global (outside UnityPerMaterial, so SRP batching is kept);
// written only by WetnessController and reset to 0 when it is disabled. Unset = 0 = dry.
float _WeatherWetness;

// Highest smoothness wetness may give: wet soil and stone look glossy, never like a mirror.
#define WEATHER_WET_SMOOTHNESS_MAX 0.82h

// How wet this material looks: global wetness scaled by the material's rain exposure.
half WeatherWetAmount(half exposure, half receive)
{
    return saturate((half)_WeatherWetness * exposure * receive);
}

// Wet surfaces absorb more light: a mild, uniform darkening (hue unchanged).
half3 WeatherWetAlbedo(half3 albedo, half wet, half darkening)
{
    return albedo * (1.0h - darkening * wet);
}

// Smoother when wet, capped; a surface that is already glossier when dry keeps its own value.
half WeatherWetSmoothness(half smoothness, half wet, half boost)
{
    return max(smoothness, min(smoothness + boost * wet, WEATHER_WET_SMOOTHNESS_MAX));
}

#endif
