using System;
using System.Collections.Generic;
using UnityEngine;

namespace DayNightSky
{
    /// <summary>One weather state the schedule can visit. Values feed DayNightSkyController's cloud settings.</summary>
    [Serializable]
    public sealed class WeatherPresetDefinition
    {
        [Tooltip("Stable identifier used by the rules (and by saves later). Do not rename once in use.")]
        public string id = "clear";
        [Tooltip("Name shown in the Inspector.")]
        public string displayName = "Clear";
        [Range(0f, 1f)] public float cloudCoverage = 0.12f;
        [Range(0f, 1f)] public float cloudSoftness = 0.4f;
        [Range(0f, 1f)] public float overcastAmount = 0f;
        [Tooltip("Default wind speed, cloud-plane units per real second (see the sky controller).")]
        [Min(0f)] public float windSpeed = 0.006f;
        [Tooltip("Default wind direction in degrees on the world XZ plane: 0 = +Z, 90 = +X.")]
        [Range(-180f, 180f)] public float windDirection = 60f;
        [Tooltip("Each visit picks a wind direction within ± this many degrees of the default.")]
        [Range(0f, 180f)] public float windDirectionJitter = 20f;
        [Tooltip("Each visit shifts the coverage by up to ± this amount, so no two visits look identical.")]
        [Range(0f, 0.3f)] public float coverageJitter = 0.04f;
        [Tooltip("0 = dry, 1 = heaviest rain (RainController's full emission). Blended like the other values.")]
        [Range(0f, 1f)] public float rainIntensity = 0f;

        public WeatherPresetDefinition() { }

        public WeatherPresetDefinition(string id, string displayName, float coverage, float softness, float overcast,
                                       float windSpeed, float windDirection, float windJitter, float coverageJitter,
                                       float rainIntensity = 0f)
        {
            this.rainIntensity = rainIntensity;
            this.id = id;
            this.displayName = displayName;
            cloudCoverage = coverage;
            cloudSoftness = softness;
            overcastAmount = overcast;
            this.windSpeed = windSpeed;
            this.windDirection = windDirection;
            windDirectionJitter = windJitter;
            this.coverageJitter = coverageJitter;
        }
    }

    [Serializable]
    public sealed class WeatherNextOption
    {
        [Tooltip("ID of a preset this state may change into.")]
        public string presetId;
        [Tooltip("Relative chance. 0 disables the option; negative is invalid.")]
        [Min(0f)] public float weight = 1f;

        public WeatherNextOption() { }
        public WeatherNextOption(string presetId, float weight) { this.presetId = presetId; this.weight = weight; }
    }

    /// <summary>How long a state lasts once reached and what it may change into.</summary>
    [Serializable]
    public sealed class WeatherRule
    {
        [Tooltip("ID of the preset this rule applies to.")]
        public string fromPresetId;
        [Tooltip("Shortest time the weather stays once reached, in game hours.")]
        [Min(0f)] public float minHoldHours = 2f;
        [Tooltip("Longest time the weather stays once reached, in game hours.")]
        [Min(0f)] public float maxHoldHours = 6f;
        public WeatherNextOption[] next = Array.Empty<WeatherNextOption>();

        public WeatherRule() { }

        public WeatherRule(string from, float minHold, float maxHold, params WeatherNextOption[] next)
        {
            fromPresetId = from;
            minHoldHours = minHold;
            maxHoldHours = maxHold;
            this.next = next;
        }
    }

    /// <summary>
    /// Data for the seeded weather schedule. Durations are in game time (hours for holds, minutes
    /// for transitions); the sky's shared clock converts them to real time. The asset is only read.
    /// </summary>
    [CreateAssetMenu(menuName = "Day Night Sky/Weather Schedule Config", fileName = "WeatherScheduleConfig")]
    public sealed class WeatherScheduleConfig : ScriptableObject
    {
        /// <summary>Holds shorter than this are rejected, so generation always moves forward.</summary>
        public const float MinimumHoldHours = 1f / 60f;
        /// <summary>Version of <see cref="SetDefaults"/>. 2 added Rain (Overcast -> Cloudy or Rain, Rain -> Overcast).</summary>
        public const int DefaultsVersion = 2;

        [Tooltip("Bump when presets or rules change. A schedule is reproducible only for the same seed, " +
                 "config contents and WeatherSchedule.AlgorithmVersion; changing rules changes the schedule of old seeds.")]
        public int configVersion = DefaultsVersion;
        [Tooltip("Same seed + same config = same schedule.")]
        public int seed = 20261006;
        [Tooltip("Preset shown at game time 0 (day 0, 00:00).")]
        public string startPresetId = "clear";
        [Tooltip("Shortest transition between two states, in game minutes.")]
        [Min(0f)] public float minTransitionMinutes = 45f;
        [Tooltip("Longest transition between two states, in game minutes.")]
        [Min(0f)] public float maxTransitionMinutes = 120f;
        public WeatherPresetDefinition[] presets = Array.Empty<WeatherPresetDefinition>();
        public WeatherRule[] rules = Array.Empty<WeatherRule>();

        void Reset() => SetDefaults();

        /// <summary>
        /// Clear -> Cloudy, Cloudy -> Clear or Overcast, Overcast -> Cloudy or Rain, Rain -> Overcast.
        /// Clear/Cloudy/Overcast match the sky controller's accepted presets (no rain); Rain is a heavier,
        /// darker overcast with rain. Wind directions vary per state.
        /// </summary>
        public void SetDefaults()
        {
            configVersion = DefaultsVersion;
            seed = 20261006;
            startPresetId = "clear";
            minTransitionMinutes = 45f;
            maxTransitionMinutes = 120f;
            presets = new[]
            {
                new WeatherPresetDefinition("clear", "Clear", 0.12f, 0.4f, 0f, 0.006f, 60f, 25f, 0.04f),
                new WeatherPresetDefinition("cloudy", "Cloudy", 0.42f, 0.35f, 0f, 0.01f, 75f, 25f, 0.06f),
                new WeatherPresetDefinition("overcast", "Overcast", 0.9f, 0.6f, 1f, 0.016f, 110f, 20f, 0.03f),
                new WeatherPresetDefinition("rain", "Rain", 0.97f, 0.65f, 1f, 0.02f, 125f, 20f, 0.02f, 0.75f),
            };
            rules = new[]
            {
                new WeatherRule("clear", 3f, 8f, new WeatherNextOption("cloudy", 1f)),
                new WeatherRule("cloudy", 2f, 6f, new WeatherNextOption("clear", 1f), new WeatherNextOption("overcast", 0.7f)),
                new WeatherRule("overcast", 1.5f, 4f, new WeatherNextOption("cloudy", 1f), new WeatherNextOption("rain", 0.8f)),
                new WeatherRule("rain", 1.5f, 4f, new WeatherNextOption("overcast", 1f)),
            };
        }

        public static WeatherScheduleConfig CreateDefault()
        {
            var c = CreateInstance<WeatherScheduleConfig>();
            c.SetDefaults();
            return c;
        }

        /// <summary>Checks the data; appends one readable message per problem. Allocates (call on setup only).</summary>
        public bool Validate(List<string> errors)
        {
            int before = errors.Count;
            var ids = new HashSet<string>();
            if (presets == null || presets.Length == 0) errors.Add("No presets defined.");
            else
                for (int i = 0; i < presets.Length; i++)
                {
                    var p = presets[i];
                    if (p == null || string.IsNullOrWhiteSpace(p.id)) errors.Add($"Preset #{i} has no ID.");
                    else if (!ids.Add(p.id)) errors.Add($"Duplicate preset ID '{p.id}'.");
                    else if (p.windSpeed < 0f) errors.Add($"Preset '{p.id}': wind speed is negative.");
                    else if (!(p.rainIntensity >= 0f && p.rainIntensity <= 1f)) errors.Add($"Preset '{p.id}': rain intensity must be within 0..1.");
                }

            if (string.IsNullOrWhiteSpace(startPresetId)) errors.Add("Start preset is not set.");
            else if (!ids.Contains(startPresetId)) errors.Add($"Start preset '{startPresetId}' does not exist.");

            if (!(minTransitionMinutes >= 0f) || !(maxTransitionMinutes >= minTransitionMinutes))
                errors.Add($"Transition minutes must satisfy 0 <= min <= max (got {minTransitionMinutes}..{maxTransitionMinutes}).");

            var ruled = new HashSet<string>();
            if (rules != null)
                for (int i = 0; i < rules.Length; i++)
                {
                    var r = rules[i];
                    string name = r == null || string.IsNullOrWhiteSpace(r.fromPresetId) ? $"Rule #{i}" : $"Rule '{r.fromPresetId}'";
                    if (r == null || string.IsNullOrWhiteSpace(r.fromPresetId)) { errors.Add($"{name} has no source preset."); continue; }
                    if (!ids.Contains(r.fromPresetId)) errors.Add($"{name}: preset '{r.fromPresetId}' does not exist.");
                    if (!ruled.Add(r.fromPresetId)) errors.Add($"{name}: more than one rule for this preset.");
                    if (!(r.minHoldHours >= MinimumHoldHours) || !(r.maxHoldHours >= r.minHoldHours))
                        errors.Add($"{name}: hold hours must satisfy {MinimumHoldHours:0.###} <= min <= max (got {r.minHoldHours}..{r.maxHoldHours}).");

                    float total = 0f;
                    if (r.next != null)
                        foreach (var o in r.next)
                        {
                            if (o == null) continue;
                            if (o.weight < 0f || float.IsNaN(o.weight)) errors.Add($"{name}: option '{o.presetId}' has a negative weight.");
                            else if (string.IsNullOrWhiteSpace(o.presetId) || !ids.Contains(o.presetId))
                                errors.Add($"{name}: next preset '{o.presetId}' does not exist.");
                            else total += o.weight;
                        }
                    if (!(total > 0f)) errors.Add($"{name}: no next state with a positive weight.");
                }

            // Every preset the schedule can be in needs somewhere to go.
            if (presets != null)
                foreach (var p in presets)
                    if (p != null && !string.IsNullOrWhiteSpace(p.id) && !ruled.Contains(p.id))
                        errors.Add($"Preset '{p.id}' has no rule, so the schedule could not leave it.");

            return errors.Count == before;
        }
    }
}
