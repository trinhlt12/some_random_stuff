using System.Collections.Generic;
using UnityEngine;

namespace DayNightSky
{
    public enum WetnessMode
    {
        /// <summary>Wetness follows the weather on the shared clock.</summary>
        Simulating,
        /// <summary>Simulation stopped; the chosen preview value is shown.</summary>
        ManualPreview,
    }

    /// <summary>What to do when the shared clock goes backwards (scrubbing time, loading an earlier time).</summary>
    public enum WetnessRewindPolicy
    {
        /// <summary>Rebuild from the schedule (dry at Recompute Hours before, then integrate). Falls back to Keep Current without a running schedule.</summary>
        RecomputeFromSchedule,
        /// <summary>Keep the current value and continue from the new time.</summary>
        KeepCurrent,
        /// <summary>Start dry from the new time.</summary>
        ResetDry,
    }

    public enum WetnessEvent { None, Started, Jumped, JumpedApproximate, Rewound, Recomputed, RecomputeUnavailable, Reset, PreviewStarted, Resumed }

    /// <summary>
    /// One shared environment wetness (0..1), written to the global shader float _WeatherWetness.
    /// The only writer of that global; it is set back to 0 when this component is disabled, so
    /// leaving Play Mode or turning it off never leaves surfaces wet.
    ///
    /// Time is the sky's shared clock (TotalHours, game hours): the model rates are per game hour,
    /// and Pause stops the clock, so wetness holds. Rain is the blended weather's RainIntensity (never
    /// the particle count, so rain quality has no effect). Frames use the average rain of their start
    /// and end; a frame longer than Large Step Hours (fast-forward) is integrated from the schedule in
    /// small sub-steps instead of applying the final intensity to the whole span.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(50)]   // after the sky has advanced the clock (Update)
    public sealed class WetnessController : MonoBehaviour
    {
        public const string GlobalProperty = "_WeatherWetness";
        static readonly int WetnessId = Shader.PropertyToID(GlobalProperty);

        [Tooltip("Sky providing the clock, rain, overcast, wind and sun. Found once if empty.")]
        public DayNightSkyController sky;
        public WetnessSettings settings = new WetnessSettings();

        [Header("Time")]
        [Tooltip("A frame advancing the clock more than this (game hours) is treated as a jump and integrated from the schedule.")]
        [Min(0.01f)] public float largeStepHours = 0.25f;
        [Tooltip("Sub-step (game hours) used when integrating jumps and recomputes from the schedule.")]
        [Min(0.005f)] public float subStepHours = 0.05f;
        [Tooltip("Recompute starts dry this many game hours earlier. With the default drying rate, what happened " +
                 "earlier changes the result by less than 1e-6.")]
        [Min(1f)] public float recomputeHours = 36f;
        public WetnessRewindPolicy rewindPolicy = WetnessRewindPolicy.RecomputeFromSchedule;
        [Tooltip("On entering Play Mode, rebuild wetness from the schedule so a scene starting in rain starts wet.")]
        public bool recomputeOnStart = true;

        WeatherScheduleController weather;
        float wetness;
        float previewValue;
        WetnessMode mode = WetnessMode.Simulating;
        bool anchored;
        double lastHours;
        float lastRain;
        float lastPushed = -1f;
        WetnessConditions lastConditions;
        readonly List<string> warnings = new List<string>();

        /// <summary>Simulated wetness (the value the simulation continues from).</summary>
        public float Wetness => wetness;
        /// <summary>Value written to the shaders.</summary>
        public float DisplayedWetness => mode == WetnessMode.ManualPreview ? previewValue : wetness;
        public WetnessMode Mode => mode;
        public float PreviewValue => previewValue;
        public WetnessEvent LastEvent { get; private set; }
        public WetnessConditions LastConditions => lastConditions;
        public float CurrentWettingRate => WetnessModel.WettingRate(settings, lastConditions);
        public float CurrentDryingRate => WetnessModel.DryingRate(settings, lastConditions);
        public float SteadyState => WetnessModel.SteadyState(settings, lastConditions);
        public bool ClockPaused => sky != null && sky.ClockPaused;
        public IReadOnlyList<string> Warnings => warnings;
        public WeatherScheduleController Weather => weather;

        void OnEnable()
        {
            ResolveSky();
            warnings.Clear();
            settings.Validate(warnings);
            anchored = false;
            lastPushed = -1f;
            if (Application.isPlaying && sky != null)
            {
                Anchor();
                if (recomputeOnStart) Recompute();
                else LastEvent = WetnessEvent.Started;
            }
            Push();
        }

        void OnDisable()
        {
            Shader.SetGlobalFloat(WetnessId, 0f);
            lastPushed = -1f;
        }

        void OnValidate()
        {
            warnings.Clear();
            settings.Validate(warnings);
        }

        void ResolveSky()
        {
            if (sky == null) sky = GetComponent<DayNightSkyController>();
            if (sky == null) sky = FindFirstObjectByType<DayNightSkyController>();
            weather = sky != null ? sky.GetComponent<WeatherScheduleController>() : null;
        }

        void LateUpdate()
        {
            // Edit Mode: no simulation (the clock does not run); shows the stored or previewed value.
            if (Application.isPlaying) Simulate();
            else Push();
        }

        /// <summary>One update from the shared clock (LateUpdate in Play Mode). No allocation in steady state.</summary>
        public void Simulate()
        {
            if (sky == null) { ResolveSky(); if (sky == null) return; }
            if (!anchored) { Anchor(); Push(); return; }

            double now = sky.TotalHours;
            float rainNow = sky.RainIntensity;
            double dt = now - lastHours;

            if (mode == WetnessMode.Simulating)
            {
                if (dt > 0.0)
                {
                    if (dt > largeStepHours) Jump(lastHours, now);
                    else
                    {
                        // Average of the frame's start and end: a transition counts with its actual intensity.
                        var state = sky.CurrentState;
                        lastConditions = new WetnessConditions(0.5f * (lastRain + rainNow), sky.OvercastAmount, sky.WindSpeed, state.sunElevationSin);
                        wetness = WetnessModel.Step(wetness, dt, settings, lastConditions);
                    }
                }
                else if (dt < 0.0)
                {
                    lastHours = now;
                    lastRain = rainNow;
                    Rewind();
                }
                // dt == 0: paused or clock stopped; wetness holds.
            }

            lastHours = now;
            lastRain = rainNow;
            Push();
        }

        void Anchor()
        {
            lastHours = sky.TotalHours;
            lastRain = sky.RainIntensity;
            anchored = true;
        }

        /// <summary>Integrates [from, to] using the weather schedule, or constant current weather without one.</summary>
        void Jump(double from, double to)
        {
            if (weather != null && weather.TryGetScheduleHours(from, out _))
            {
                double start = from;
                float w = wetness;
                // Older history no longer matters (it decays below 1e-6); start dry instead of integrating it all.
                if (to - from > recomputeHours) { start = to - recomputeHours; w = 0f; }
                wetness = IntegrateSchedule(w, start, to);
                LastEvent = WetnessEvent.Jumped;
            }
            else
            {
                // No running schedule (manual weather, override): the weather shown now is all we know.
                lastConditions = new WetnessConditions(sky.RainIntensity, sky.OvercastAmount, sky.WindSpeed, sky.SunHeightAt(sky.TimeOfDay));
                wetness = WetnessModel.Step(wetness, to - from, settings, lastConditions);
                LastEvent = WetnessEvent.JumpedApproximate;
            }
        }

        float IntegrateSchedule(float w, double from, double to)
        {
            var schedule = weather.Schedule;
            int steps = Mathf.Max(1, Mathf.CeilToInt((float)((to - from) / subStepHours)));
            double h = (to - from) / steps;
            for (int i = 0; i < steps; i++)
            {
                double mid = from + (i + 0.5) * h;
                weather.TryGetScheduleHours(mid, out double scheduleHours);
                var sample = schedule.Evaluate(scheduleHours);
                lastConditions = new WetnessConditions(sample.rain, sample.overcast, sample.windSpeed,
                                                       sky.SunHeightAt((float)(mid - System.Math.Floor(mid / 24.0) * 24.0)));
                w = WetnessModel.Step(w, h, settings, lastConditions);
            }
            return w;
        }

        void Rewind()
        {
            switch (rewindPolicy)
            {
                case WetnessRewindPolicy.ResetDry:
                    wetness = 0f;
                    LastEvent = WetnessEvent.Rewound;
                    break;
                case WetnessRewindPolicy.KeepCurrent:
                    LastEvent = WetnessEvent.Rewound;
                    break;
                default:
                    if (!Recompute()) LastEvent = WetnessEvent.Rewound;   // kept the current value
                    break;
            }
        }

        // ------------------------------------------------------------------------------------
        // Controls (none of them touch the weather schedule or forecast)

        /// <summary>
        /// Rebuilds wetness at the current time from the schedule: dry Recompute Hours earlier, then
        /// integrated. Returns false (value unchanged) when no running schedule is available.
        /// </summary>
        public bool Recompute()
        {
            if (sky == null) ResolveSky();
            if (weather == null && sky != null) weather = sky.GetComponent<WeatherScheduleController>();
            if (sky == null || weather == null || !weather.TryGetScheduleHours(sky.TotalHours, out _))
            {
                LastEvent = WetnessEvent.RecomputeUnavailable;
                return false;
            }
            double now = sky.TotalHours;
            wetness = IntegrateSchedule(0f, now - recomputeHours, now);
            Anchor();
            LastEvent = WetnessEvent.Recomputed;
            Push();
            return true;
        }

        /// <summary>Sets the simulated wetness to 0 and keeps simulating from there.</summary>
        public void ResetDry() => SetSimulated(0f);

        /// <summary>Sets the simulated wetness to 1 and keeps simulating from there (watch it dry).</summary>
        public void SetFullyWet() => SetSimulated(1f);

        void SetSimulated(float value)
        {
            wetness = Mathf.Clamp01(value);
            mode = WetnessMode.Simulating;
            if (sky != null) Anchor();
            LastEvent = WetnessEvent.Reset;
            Push();
        }

        /// <summary>Stops the simulation and shows <paramref name="value"/>.</summary>
        public void SetManualPreview(float value)
        {
            previewValue = Mathf.Clamp01(float.IsNaN(value) ? 0f : value);
            if (mode != WetnessMode.ManualPreview) LastEvent = WetnessEvent.PreviewStarted;
            mode = WetnessMode.ManualPreview;
            Push();
        }

        /// <summary>Continues simulating from the value shown now (no jump, no reset).</summary>
        public void ReturnToSimulation()
        {
            if (mode != WetnessMode.ManualPreview) return;
            wetness = previewValue;
            mode = WetnessMode.Simulating;
            if (sky != null) Anchor();
            LastEvent = WetnessEvent.Resumed;
            Push();
        }

        void Push()
        {
            float value = DisplayedWetness;
            if (Mathf.Abs(value - lastPushed) < 1e-5f) return;
            Shader.SetGlobalFloat(WetnessId, value);
            lastPushed = value;
        }
    }
}
