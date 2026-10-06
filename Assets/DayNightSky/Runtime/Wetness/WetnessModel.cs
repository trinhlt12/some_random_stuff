using System;
using System.Collections.Generic;
using UnityEngine;

namespace DayNightSky
{
    /// <summary>
    /// Rates of the wetness model, per GAME HOUR of the shared clock (DayNightSkyController.TotalHours).
    /// The clock speed is already in the game hours, so these are never multiplied by it again.
    /// </summary>
    [Serializable]
    public sealed class WetnessSettings
    {
        [Tooltip("Per game hour. How fast surfaces soak at rain intensity 1 (scaled by the intensity).")]
        [Min(0f)] public float wettingRatePerHour = 4f;
        [Tooltip("Per game hour. Base drying rate (night, overcast, no wind). Half-life after rain ≈ 0.69 / rate hours.")]
        [Min(0f)] public float dryingRatePerHour = 0.6f;
        [Tooltip("Optional: extra drying under a high, unclouded sun. Drying × (1 + this × sun height × (1 − overcast)). " +
                 "A visual factor from the sky, not a temperature.")]
        [Range(0f, 3f)] public float sunDryingBoost = 1f;
        [Tooltip("Optional: extra drying with wind. Drying × (1 + this × min(wind / Wind Reference, 1)).")]
        [Range(0f, 3f)] public float windDryingBoost = 0.5f;
        [Tooltip("Sky wind speed (cloud-plane units/s) that gives the full wind boost.")]
        [Min(1e-4f)] public float windReference = 0.02f;

        /// <summary>Reports values that will be clamped (negative, NaN). Allocates; for setup/Inspector only.</summary>
        public bool Validate(List<string> warnings)
        {
            int before = warnings.Count;
            Check(warnings, wettingRatePerHour, nameof(wettingRatePerHour));
            Check(warnings, dryingRatePerHour, nameof(dryingRatePerHour));
            Check(warnings, sunDryingBoost, nameof(sunDryingBoost));
            Check(warnings, windDryingBoost, nameof(windDryingBoost));
            if (!(windReference > 0f)) warnings.Add($"{nameof(windReference)} must be > 0 (got {windReference}); 1e-4 is used.");
            if (dryingRatePerHour <= 0f) warnings.Add("Drying rate is 0: surfaces never dry.");
            return warnings.Count == before;
        }

        static void Check(List<string> warnings, float v, string name)
        {
            if (float.IsNaN(v) || float.IsInfinity(v)) warnings.Add($"{name} is not a number; 0 is used.");
            else if (v < 0f) warnings.Add($"{name} is negative ({v}); 0 is used.");
        }
    }

    /// <summary>Weather at one moment, as the wetness model sees it. Out-of-range values are clamped.</summary>
    public struct WetnessConditions
    {
        public float rain;        // 0..1, blended weather rain intensity
        public float overcast;    // 0..1
        public float windSpeed;   // sky wind speed (cloud-plane units/s)
        public float sunHeight;   // sine of the sun elevation, -1..1

        public WetnessConditions(float rain, float overcast, float windSpeed, float sunHeight)
        {
            this.rain = rain;
            this.overcast = overcast;
            this.windSpeed = windSpeed;
            this.sunHeight = sunHeight;
        }
    }

    /// <summary>
    /// dW/dt = wetting × rain × (1 − W) − drying × W, time in game hours.
    /// Each step is solved exactly for conditions held constant over the step:
    ///   k = wetting × rain + drying,  W∞ = wetting × rain / k,  W(t + h) = W∞ + (W − W∞) × e^(−k h).
    /// So W always stays in [0, 1], light rain settles below 1, and with constant weather the result
    /// does not depend on the step size at all. Pure maths: no Unity objects, no allocation.
    /// </summary>
    public static class WetnessModel
    {
        static float Safe(float v) => float.IsNaN(v) || float.IsInfinity(v) ? 0f : v;
        static float Safe01(float v) => Mathf.Clamp01(Safe(v));

        public static float WettingRate(WetnessSettings s, in WetnessConditions c) =>
            Mathf.Max(0f, Safe(s.wettingRatePerHour)) * Safe01(c.rain);

        public static float DryingRate(WetnessSettings s, in WetnessConditions c)
        {
            float sun = Mathf.Clamp01(Safe(c.sunHeight)) * (1f - Safe01(c.overcast));
            float wind = Mathf.Clamp01(Mathf.Max(0f, Safe(c.windSpeed)) / Mathf.Max(1e-4f, Safe(s.windReference)));
            return Mathf.Max(0f, Safe(s.dryingRatePerHour))
                   * (1f + Mathf.Max(0f, Safe(s.sunDryingBoost)) * sun)
                   * (1f + Mathf.Max(0f, Safe(s.windDryingBoost)) * wind);
        }

        /// <summary>Wetness the surface tends to under constant conditions.</summary>
        public static float SteadyState(WetnessSettings s, in WetnessConditions c)
        {
            float a = WettingRate(s, c), k = a + DryingRate(s, c);
            return k > 0f ? a / k : 0f;
        }

        /// <summary>Advances wetness by <paramref name="hours"/> game hours under constant conditions.</summary>
        public static float Step(float wetness, double hours, WetnessSettings s, in WetnessConditions c)
        {
            float w = Safe01(wetness);
            if (!(hours > 0.0)) return w;
            float a = WettingRate(s, c);
            float k = a + DryingRate(s, c);
            if (!(k > 0f)) return w;
            float target = a / k;
            return Mathf.Clamp01(target + (w - target) * (float)Math.Exp(-k * hours));
        }
    }
}
