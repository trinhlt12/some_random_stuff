using System.Collections.Generic;
using UnityEngine;

namespace DayNightSky
{
    public enum WeatherMode
    {
        /// <summary>The weather follows the seeded schedule on the shared clock.</summary>
        Automatic,
        /// <summary>The shared clock stops: day/night, schedule, transitions and cloud drift all hold.</summary>
        Paused,
        /// <summary>A chosen preset is shown; the schedule waits, day/night keeps running.</summary>
        ManualOverride,
    }

    /// <summary>
    /// Runs a seeded weather schedule on the sky's shared game clock (no clock of its own) and shows it
    /// through <see cref="DayNightSkyController.ApplyTransitionAt"/>. Registered as the sky's weather
    /// driver, so the sky stays the only writer of the material and the light.
    ///
    /// Schedule time = sky.TotalHours − offset. The offset only changes on explicit actions (returning
    /// from an override, Advance, Regenerate), never per frame, so the two can never drift apart.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(DayNightSkyController))]
    public sealed class WeatherScheduleController : MonoBehaviour, ISkyWeatherDriver
    {
        [Tooltip("Presets, rules and seed. Read only: never modified at runtime.")]
        public WeatherScheduleConfig config;
        [Tooltip("Use Seed Override instead of the config's seed (handy for testing other seeds).")]
        public bool useSeedOverride;
        public int seedOverride = 1;
        [Tooltip("Real seconds for the blends started by Force, Return to Automatic, Advance and Regenerate. " +
                 "Scheduled transitions use the config's game-time durations instead.")]
        [Min(0.1f)] public float manualBlendSeconds = 4f;
        [Tooltip("Show the scheduled weather in Edit Mode too (follows the sky's Day / Time Of Day).")]
        public bool previewInEditMode = true;

        DayNightSkyController sky;
        WeatherSchedule schedule;
        readonly List<string> errors = new List<string>();
        WeatherScheduleConfig builtFrom;
        bool registered;

        WeatherMode mode = WeatherMode.Automatic;
        WeatherMode modeBeforePause = WeatherMode.Automatic;
        double offsetHours;          // schedule time = clock - offset (while the schedule runs)
        double frozenScheduleHours;  // schedule time while it waits (override, returning)
        bool returning;              // Automatic, blending back before the schedule resumes
        int forcedPreset = -1;

        bool blending;
        WeatherSample blendFrom;
        float blendElapsed;

        WeatherSample displayed;
        bool hasDisplayed;

        public DayNightSkyController Sky => sky != null ? sky : sky = GetComponent<DayNightSkyController>();
        public WeatherSchedule Schedule => schedule;
        public IReadOnlyList<string> Errors => errors;
        public WeatherMode Mode => mode;
        public bool IsReturningToAutomatic => returning;
        public bool IsBlending => blending;
        public float BlendProgress => blending ? Mathf.Clamp01(blendElapsed / manualBlendSeconds) : 1f;
        public int ForcedPreset => forcedPreset;
        public int SeedInUse => useSeedOverride || config == null ? seedOverride : config.seed;
        public WeatherSample Displayed => displayed;
        public bool IsAnimating => blending;

        /// <summary>True while schedule time follows the shared clock (Automatic and not returning).</summary>
        public bool ScheduleRunning => mode == WeatherMode.Automatic && !returning;

        /// <summary>Current position on the schedule, in schedule hours.</summary>
        public double ScheduleHours
        {
            get
            {
                if (mode == WeatherMode.Paused && modeBeforePause == WeatherMode.Automatic && !returning)
                    return Sky.TotalHours - offsetHours;
                return ScheduleRunning ? Sky.TotalHours - offsetHours : frozenScheduleHours;
            }
        }

        /// <summary>Shared-clock time at which a schedule time is reached, if the schedule keeps running from now.</summary>
        public double ScheduleToClockHours(double scheduleHours) => Sky.TotalHours + (scheduleHours - ScheduleHours);

        /// <summary>
        /// Schedule time that corresponds to a shared-clock time, assuming the schedule keeps running
        /// with its current offset. False while it waits (override, returning, paused from those) or has no schedule.
        /// </summary>
        public bool TryGetScheduleHours(double clockHours, out double scheduleHours)
        {
            scheduleHours = 0.0;
            bool running = ScheduleRunning || (mode == WeatherMode.Paused && modeBeforePause == WeatherMode.Automatic && !returning);
            if (schedule == null || !running) return false;
            scheduleHours = ScheduleHours + (clockHours - Sky.TotalHours);
            return true;
        }

        void OnEnable()
        {
            sky = GetComponent<DayNightSkyController>();
            registered = sky.RegisterWeatherDriver(this);
            if (!registered)
                Debug.LogError($"{name}: the sky already has another weather driver; this schedule stays idle.", this);
            Build();
            Refresh();
        }

        void OnDisable()
        {
            if (sky != null)
            {
                sky.UnregisterWeatherDriver(this);
                if (mode == WeatherMode.Paused) sky.ClockPaused = false;
            }
            registered = false;
            if (mode == WeatherMode.Paused) mode = modeBeforePause;
        }

        // OnValidate deliberately does not rebuild: regenerating is an explicit action.

        bool Build()
        {
            errors.Clear();
            schedule = null;
            builtFrom = config;
            if (!WeatherSchedule.TryCreate(config, SeedInUse, errors, out schedule))
            {
                // A missing config just means "not set up yet" (shown in the Inspector); bad data is an error.
                if (config != null)
                    foreach (var e in errors) Debug.LogError($"{name}: weather schedule disabled: {e}", this);
                return false;
            }
            return true;
        }

        // ------------------------------------------------------------------------------------
        // Driver

        public void DriveWeather(DayNightSkyController driven, float deltaTime)
        {
            if (!registered) return;
            if (!Application.isPlaying && !previewInEditMode) return;
            // A config assigned after enabling is picked up once; edits to the same asset wait for Regenerate.
            if (schedule == null && config != null && builtFrom != config) Build();
            if (schedule == null || mode == WeatherMode.Paused) return;

            WeatherSample target;
            if (mode == WeatherMode.ManualOverride)
                target = schedule.GetPresetSample(forcedPreset);
            else
                target = schedule.Evaluate(returning ? frozenScheduleHours : driven.TotalHours - offsetHours);

            if (blending && deltaTime > 0f) blendElapsed += deltaTime;
            var sample = target;
            if (blending)
            {
                float t = Mathf.Clamp01(blendElapsed / manualBlendSeconds);
                sample = WeatherSample.Lerp(blendFrom, target, WeatherMath.Ease(t));
                if (t >= 1f)
                {
                    blending = false;
                    if (returning)
                    {
                        // Resume the schedule exactly where it waited.
                        returning = false;
                        offsetHours = driven.TotalHours - frozenScheduleHours;
                    }
                }
            }

            Show(driven, sample);
        }

        void Show(DayNightSkyController driven, in WeatherSample s)
        {
            // The schedule already eased the sample; progress 1 shows it as is.
            driven.ApplyTransitionAt(s.Clouds, s.Clouds, 1f, s.windDirection, s.windDirection);
            displayed = s;
            hasDisplayed = true;
        }

        void StartBlend()
        {
            if (!hasDisplayed)
            {
                blending = false;
                return;
            }
            blendFrom = displayed;
            blendElapsed = 0f;
            blending = true;
        }

        // ------------------------------------------------------------------------------------
        // Controls

        /// <summary>Stops the shared clock (day/night, schedule, transitions and cloud drift).</summary>
        public void Pause()
        {
            if (mode == WeatherMode.Paused) return;
            modeBeforePause = mode;
            mode = WeatherMode.Paused;
            Sky.ClockPaused = true;
        }

        /// <summary>Restarts the clock and returns to the mode that was active before pausing.</summary>
        public void Resume()
        {
            if (mode != WeatherMode.Paused) return;
            mode = modeBeforePause;
            Sky.ClockPaused = false;
            Refresh();
        }

        /// <summary>Blends to a preset and holds it; the schedule waits where it is.</summary>
        public bool ForcePreset(int preset)
        {
            if (schedule == null || preset < 0 || preset >= schedule.PresetCount) return false;
            if (mode == WeatherMode.Paused) Resume();
            if (mode == WeatherMode.Automatic)
            {
                frozenScheduleHours = ScheduleHours;
                returning = false;
            }
            mode = WeatherMode.ManualOverride;
            forcedPreset = preset;
            StartBlend();
            Refresh();
            return true;
        }

        public bool ForcePreset(string presetId) => schedule != null && ForcePreset(schedule.FindPreset(presetId));

        /// <summary>
        /// Blends from what is shown now to the schedule's state at the moment it was left, then lets the
        /// schedule continue from there. The forecast does not change.
        /// </summary>
        public void ReturnToAutomatic()
        {
            if (mode == WeatherMode.Paused) Resume();
            if (mode != WeatherMode.ManualOverride) return;
            mode = WeatherMode.Automatic;
            forcedPreset = -1;
            returning = true;
            StartBlend();
            if (!blending)
            {
                returning = false;
                offsetHours = Sky.TotalHours - frozenScheduleHours;
            }
            Refresh();
        }

        /// <summary>Jumps the schedule to the start of the next segment's transition and blends there.</summary>
        public void AdvanceToNextSegment()
        {
            if (schedule == null || mode == WeatherMode.Paused) return;
            double next = schedule.SegmentAfter(ScheduleHours, 1).transitionStartHours;
            if (ScheduleRunning) offsetHours = Sky.TotalHours - next;
            else frozenScheduleHours = next;
            if (mode == WeatherMode.Automatic) StartBlend();
            Refresh();
        }

        /// <summary>
        /// Explicit reset: rebuilds the schedule from the seed (and the config's current values), puts
        /// schedule time back on the shared clock and returns to Automatic with a blend.
        /// </summary>
        public void RegenerateSchedule()
        {
            if (mode == WeatherMode.Paused) Resume();
            if (!Build()) return;
            mode = WeatherMode.Automatic;
            forcedPreset = -1;
            returning = false;
            offsetHours = 0.0;
            StartBlend();
            Refresh();
        }

        void Refresh()
        {
            if (sky != null && sky.isActiveAndEnabled) sky.Tick(0f);
        }
    }
}
