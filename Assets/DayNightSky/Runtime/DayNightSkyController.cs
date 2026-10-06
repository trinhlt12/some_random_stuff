using System;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
#endif

namespace DayNightSky
{
    public enum CloudPreset { Clear, Cloudy, Overcast }

    /// <summary>The part of the weather that presets blend between.</summary>
    [Serializable]
    public struct CloudWeather
    {
        [Tooltip("0 = no clouds at all, 1 = the whole sky is covered.")]
        [Range(0f, 1f)] public float coverage;
        [Tooltip("Width of the soft cloud edge. Low = crisp cartoon puffs, high = hazy.")]
        [Range(0f, 1f)] public float softness;
        [Tooltip("0 = normal sky, 1 = muted, darker sky and clouds, dimmer sun light.")]
        [Range(0f, 1f)] public float overcast;
        [Tooltip("Cloud-plane units per second (see Wind Speed on the controller).")]
        [Min(0f)] public float windSpeed;
        [Tooltip("0 = dry, 1 = heaviest rain. Drawn by RainController, not by the sky.")]
        [Range(0f, 1f)] public float rain;

        public CloudWeather(float coverage, float softness, float overcast, float windSpeed, float rain = 0f)
        {
            this.coverage = coverage;
            this.softness = softness;
            this.overcast = overcast;
            this.windSpeed = windSpeed;
            this.rain = rain;
        }

        public static CloudWeather Lerp(CloudWeather a, CloudWeather b, float t) => new CloudWeather(
            Mathf.Lerp(a.coverage, b.coverage, t),
            Mathf.Lerp(a.softness, b.softness, t),
            Mathf.Lerp(a.overcast, b.overcast, t),
            Mathf.Lerp(a.windSpeed, b.windSpeed, t),
            Mathf.Lerp(a.rain, b.rain, t));

        public bool Approximately(CloudWeather o) =>
            Mathf.Abs(coverage - o.coverage) + Mathf.Abs(softness - o.softness) +
            Mathf.Abs(overcast - o.overcast) + Mathf.Abs(windSpeed - o.windSpeed) +
            Mathf.Abs(rain - o.rain) < 1e-5f;
    }

    /// <summary>Shared transition maths, so every weather blend eases the same way.</summary>
    public static class WeatherMath
    {
        /// <summary>Smoothstep easing used by all weather transitions.</summary>
        public static float Ease(float t)
        {
            t = Mathf.Clamp01(t);
            return t * t * (3f - 2f * t);
        }

        /// <summary>Wraps degrees to [-180, 180).</summary>
        public static float WrapAngle(float degrees) => Mathf.Repeat(degrees + 180f, 360f) - 180f;

        /// <summary>Blends angles along the shorter arc (350° -> 10° passes through 0°).</summary>
        public static float LerpAngle(float a, float b, float t) => WrapAngle(a + Mathf.DeltaAngle(a, b) * t);
    }

    /// <summary>
    /// Something that decides the weather from the shared clock (e.g. a schedule). The sky calls it
    /// once per Tick, after advancing the clock and before writing the material and light, so the
    /// sky stays the only writer. At most one driver per sky.
    /// </summary>
    public interface ISkyWeatherDriver
    {
        /// <param name="deltaTime">Real seconds since the last tick, 0 while the clock is paused.</param>
        void DriveWeather(DayNightSkyController sky, float deltaTime);
        /// <summary>True while the driver needs Edit Mode repaints (e.g. during a blend).</summary>
        bool IsAnimating { get; }
    }

    /// <summary>
    /// Drives the "Skybox/Day Night Gradient" shader. Everything that depends on the time of day
    /// or the weather (sun direction, colours, visibility, cloud thresholds, wind) is computed here
    /// and passed to a private material instance; the shader only does per-view-direction work.
    /// This is the only writer of the sky material and of the synced directional light.
    /// World up is +Y. Sun rises at 06:00, is highest at 12:00 and sets at 18:00.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public sealed class DayNightSkyController : MonoBehaviour
    {
        public struct SkyState
        {
            public Vector3 sunDirection;   // world, pointing towards the sun
            public float sunElevationSin;  // y of sunDirection
            public float phase;            // 0 = midnight, 0.5 = sunrise/sunset, 1 = noon
            public Color zenith, horizon, ground, glow, sunLight;
            public float sunVisibility, lightIntensity;
            public Color cloudLit, cloudShade;
            public float clearLightIntensity;  // light intensity before the overcast dimming
        }

        [Header("Time")]
        [Tooltip("Hours, 0–24. 24 is the same moment as 0.")]
        [Range(0f, 24f)] public float timeOfDay = 9f;
        [Tooltip("Advance time automatically in Play Mode.")]
        public bool autoAdvance = true;
        [Tooltip("Real seconds for a full 24 h cycle.")]
        [Min(1f)] public float dayLengthSeconds = 240f;
        [Tooltip("Day counter, +1 each time the running clock passes midnight. Day and Time Of Day form the " +
                 "shared game clock (Total Hours = Day × 24 + Time Of Day) that the weather schedule follows.")]
        [Min(0)] public int day = 0;
        [Tooltip("Clock speed multiplier for testing: 10 = days pass 10× faster. Moves day/night and the " +
                 "weather schedule together; the cloud drift (wind) stays in real time.")]
        [Min(0f)] public float clockSpeed = 1f;

        [Header("Sun path")]
        [Tooltip("Rotates the sun's path around world up (0 = rises at +X, sets at -X).")]
        [Range(-180f, 180f)] public float sunPathAzimuth = 0f;
        [Tooltip("Sun elevation at 12:00.")]
        [Range(10f, 90f)] public float noonElevation = 60f;

        [Header("Colours (by sun height)")]
        [Tooltip("Left = sun far below the horizon (night), middle = sun on the horizon (sunrise/sunset), right = sun at its noon height.")]
        public Gradient zenithColor = DefaultZenith();
        [Tooltip("Same axis as Zenith Color.")]
        public Gradient horizonColor = DefaultHorizon();
        [Tooltip("Warm glow near the horizon on the sun's side. Alpha = glow strength.")]
        public Gradient sunriseSunsetGlow = DefaultGlow();
        [Tooltip("How much darker the area below the horizon is than the horizon colour.")]
        [Range(0f, 1f)] public float belowHorizonBrightness = 0.8f;
        public Color sunDiscColor = new Color(1f, 0.97f, 0.88f);

        [Header("Shape")]
        [Tooltip("Height (fraction of straight up) at which the horizon colour has fully turned into the zenith colour.")]
        [Range(0.05f, 1f)] public float gradientHeight = 0.45f;
        [Tooltip("Angular diameter of the sun disc in degrees.")]
        [Range(0.5f, 15f)] public float sunSize = 4f;
        [Tooltip("Width of the soft edge, as a fraction of the sun radius.")]
        [Range(0f, 1f)] public float sunSoftness = 0.3f;
        [Tooltip("How far around the horizon the sunrise/sunset glow spreads.")]
        [Range(0.05f, 1f)] public float glowWidth = 0.45f;
        [Tooltip("How high above the horizon the glow reaches.")]
        [Range(0.05f, 1f)] public float glowHeight = 0.3f;

        [Header("Output")]
        [Tooltip("Template material using the Skybox/Day Night Gradient shader. It is copied, never modified.")]
        public Material skyMaterial;
        [Tooltip("Put the material instance in RenderSettings.skybox while enabled; the previous skybox is restored on disable.")]
        public bool applyAsSkybox = true;

        [Header("Directional Light (optional)")]
        public Light sunLight;
        [Min(0f)] public float sunLightIntensity = 1.2f;
        [Tooltip("Light colour, same axis as the sky colours.")]
        public Gradient sunLightColor = DefaultLightColor();
        [Tooltip("Disable the light while the sun is below the horizon (saves the shadow pass at night).")]
        public bool disableLightAtNight = true;

        [Header("Clouds")]
        [Tooltip("0 = no clouds at all, 1 = the whole sky is covered.")]
        [Range(0f, 1f)] public float cloudCoverage = 0.42f;
        [Tooltip("Width of the soft cloud edge. Low = crisp cartoon puffs, high = hazy.")]
        [Range(0f, 1f)] public float cloudSoftness = 0.35f;
        [Tooltip("Size of the cloud shapes. 2 = twice as big (and half as many).")]
        [Range(0.25f, 4f)] public float cloudScale = 1f;
        [Tooltip("How much the small detail breaks up the cloud edges.")]
        [Range(0f, 1f)] public float cloudDetail = 0.4f;
        [Tooltip("Compass direction the clouds drift towards, in degrees on the world XZ plane: 0 = +Z, 90 = +X.")]
        [Range(-180f, 180f)] public float windDirection = 60f;
        [Tooltip("Cloud-plane units per second. The clouds lie on a virtual ceiling 1 unit above the viewer, " +
                 "so at 0.01 a cloud overhead needs about 100 s to drift from the zenith to 45° elevation. " +
                 "Real time (Time.deltaTime), independent of the day length.")]
        [Min(0f)] public float windSpeed = 0.01f;
        [Tooltip("Lit side of the clouds, same axis as the sky colours. Alpha = how much thin clouds near the sun catch its light.")]
        public Gradient cloudLitColor = DefaultCloudLit();
        [Tooltip("Thick cloud cores, same axis as the sky colours.")]
        public Gradient cloudShadeColor = DefaultCloudShade();
        [Tooltip("Drift the clouds in Edit Mode too (repaints the Scene and Game views ~30 times per second).")]
        public bool previewCloudMotionInEditMode = false;

        [Header("Overcast")]
        [Tooltip("0 = normal sky, 1 = muted, darker sky and clouds, dimmer sun light.")]
        [Range(0f, 1f)] public float overcastAmount = 0f;
        [Tooltip("Grey tint the sky and clouds move towards when overcast.")]
        public Color overcastTint = new Color(0.88f, 0.90f, 0.96f);
        [Tooltip("At full overcast: how far the sky colours move towards the muted cloud colours.")]
        [Range(0f, 1f)] public float overcastSkyMuting = 0.65f;
        [Tooltip("At full overcast: how much darker the sky gets.")]
        [Range(0f, 1f)] public float overcastSkyDarkening = 0.25f;
        [Tooltip("At full overcast: how far the cloud colours move towards the grey tint.")]
        [Range(0f, 1f)] public float overcastCloudDesaturation = 0.55f;
        [Tooltip("At full overcast: how much darker the clouds get.")]
        [Range(0f, 1f)] public float overcastCloudDarkening = 0.3f;
        [Tooltip("At full overcast: how much the sun disc, sunrise/sunset glow and cloud sun-glow fade.")]
        [Range(0f, 1f)] public float overcastSunFade = 0.7f;
        [Tooltip("Directional light intensity multiplier at full overcast (always applied to the day/night value, never accumulated).")]
        [Range(0f, 1f)] public float overcastLightMultiplier = 0.5f;

        [Header("Rain")]
        [Tooltip("0 = dry, 1 = heaviest rain. Read by RainController (the sky shader draws no rain). " +
                 "Driven by the weather schedule when one is attached.")]
        [Range(0f, 1f)] public float rainIntensity = 0f;

        [Header("Weather Presets")]
        [Tooltip("Seconds used by TransitionToPreset when no duration is given.")]
        [Min(0f)] public float transitionDuration = 8f;
        public CloudWeather clearPreset = new CloudWeather(0.12f, 0.4f, 0f, 0.006f);
        public CloudWeather cloudyPreset = new CloudWeather(0.42f, 0.35f, 0f, 0.01f);
        public CloudWeather overcastPreset = new CloudWeather(0.9f, 0.6f, 1f, 0.016f);

        static readonly int ZenithColorId = Shader.PropertyToID("_ZenithColor");
        static readonly int HorizonColorId = Shader.PropertyToID("_HorizonColor");
        static readonly int GroundColorId = Shader.PropertyToID("_GroundColor");
        static readonly int GlowColorId = Shader.PropertyToID("_GlowColor");
        static readonly int SunColorId = Shader.PropertyToID("_SunColor");
        static readonly int SunDirectionId = Shader.PropertyToID("_SunDirection");
        static readonly int SkyParamsId = Shader.PropertyToID("_SkyParams");
        static readonly int SunParamsId = Shader.PropertyToID("_SunParams");
        static readonly int CloudLitColorId = Shader.PropertyToID("_CloudLitColor");
        static readonly int CloudShadeColorId = Shader.PropertyToID("_CloudShadeColor");
        static readonly int CloudOffsetId = Shader.PropertyToID("_CloudOffset");
        static readonly int CloudShapeId = Shader.PropertyToID("_CloudShape");
        static readonly int CloudDensityId = Shader.PropertyToID("_CloudDensity");
        static readonly int CloudLightingId = Shader.PropertyToID("_CloudLighting");

        // Cloud layer mapping, shared with the shader (see DayNightSky.shader).
        const float ShapeTilesPerUnit = 0.35f;   // noise tiles per cloud-plane unit at cloudScale 1
        const float DetailScaleRatio = 2.7f;     // detail is sampled this much finer than the shape
        const float DetailDrift = 1.2f;          // detail drifts slightly faster: slow, gentle morphing
        const float DomeCurvature = 0.2f;        // keeps dir.xz / (y + c) finite at the horizon
        const float HorizonFadeHeight = 0.16f;   // sin(elevation) at which clouds are fully visible
        const float SunHaloAngle = 28f;          // degrees around the sun where thin clouds catch light

        Material instance;
        Material instanceSource;
        Material previousSkybox;
        bool appliedAsSkybox;
        bool dirty = true;

        // Wind offsets in noise UV (shape xy, detail zw). Wrapped to [0, 1): the noise tiles with
        // period 1, so wrapping never moves the clouds and the values never lose precision.
        Vector4 cloudOffset;
        bool cloudOffsetDirty;

        bool transitionActive;
        CloudWeather transitionFrom, transitionTo;
        float transitionElapsed, transitionLength;
        CloudWeather lastWrittenWeather;
        ISkyWeatherDriver weatherDriver;
        SkyState currentState;
#if UNITY_EDITOR
        double lastEditorTime;
        double lastEditorRepaint;
#endif

        /// <summary>Material instance currently used (null when disabled or no template).</summary>
        public Material MaterialInstance => instance;

        /// <summary>Hours, 0–24. Setting it does not change <see cref="Day"/>.</summary>
        public float TimeOfDay
        {
            get => timeOfDay;
            set
            {
                timeOfDay = Wrap(value);
                dirty = true;
            }
        }

        public int Day { get => day; set { day = Mathf.Max(0, value); dirty = true; } }

        /// <summary>The shared game clock in game hours since day 0, 00:00 (Day × 24 + Time Of Day).</summary>
        public double TotalHours
        {
            get => day * 24.0 + timeOfDay;
            set
            {
                double h = Math.Max(0.0, value);
                day = (int)Math.Floor(h / 24.0);
                timeOfDay = Mathf.Min((float)(h - day * 24.0), 23.99999f);
                dirty = true;
            }
        }

        /// <summary>
        /// Stops the shared clock, the cloud drift and the sky's own preset transitions. The image
        /// holds exactly as it is. Not saved with the scene.
        /// </summary>
        public bool ClockPaused { get; set; }

        /// <summary>Real seconds per game hour at the current clock speed (infinity when stopped).</summary>
        public double RealSecondsPerGameHour =>
            clockSpeed > 0f ? Mathf.Max(1f, dayLengthSeconds) / 24.0 / clockSpeed : double.PositiveInfinity;

        /// <summary>The driver deciding the weather, if any.</summary>
        public ISkyWeatherDriver WeatherDriver => weatherDriver;

        /// <summary>Lets one driver decide the weather. Returns false if another driver owns it.</summary>
        public bool RegisterWeatherDriver(ISkyWeatherDriver driver)
        {
            if (weatherDriver != null && !ReferenceEquals(weatherDriver, driver)) return false;
            weatherDriver = driver;
            return true;
        }

        public void UnregisterWeatherDriver(ISkyWeatherDriver driver)
        {
            if (ReferenceEquals(weatherDriver, driver)) weatherDriver = null;
        }

        public float CloudCoverage { get => cloudCoverage; set { cloudCoverage = Mathf.Clamp01(value); dirty = true; } }
        public float CloudSoftness { get => cloudSoftness; set { cloudSoftness = Mathf.Clamp01(value); dirty = true; } }
        public float CloudScale { get => cloudScale; set { cloudScale = Mathf.Clamp(value, 0.25f, 4f); dirty = true; } }
        /// <summary>Degrees on the world XZ plane the clouds drift towards: 0 = +Z, 90 = +X.</summary>
        public float WindDirection { get => windDirection; set { windDirection = Mathf.Repeat(value + 180f, 360f) - 180f; dirty = true; } }
        /// <summary>Cloud-plane units per second (real time). See the field tooltip.</summary>
        public float WindSpeed { get => windSpeed; set { windSpeed = Mathf.Max(0f, value); dirty = true; } }
        public float OvercastAmount { get => overcastAmount; set { overcastAmount = Mathf.Clamp01(value); dirty = true; } }
        /// <summary>0..1, read by RainController. Part of the blended weather like coverage and overcast.</summary>
        public float RainIntensity { get => rainIntensity; set { rainIntensity = Mathf.Clamp01(value); dirty = true; } }
        /// <summary>The sky state last written to the material and light (colours, sun, phase). No allocation.</summary>
        public SkyState CurrentState => currentState;
        public float TransitionDuration { get => transitionDuration; set => transitionDuration = Mathf.Max(0f, value); }

        /// <summary>The weather currently shown (changes every frame during a transition).</summary>
        public CloudWeather CurrentWeather
        {
            get => new CloudWeather(cloudCoverage, cloudSoftness, overcastAmount, windSpeed, rainIntensity);
            set => SetWeather(value);
        }

        public bool IsTransitioning => transitionActive;
        /// <summary>0..1 progress of the running transition (1 when idle).</summary>
        public float TransitionProgress => transitionActive ? Mathf.Clamp01(transitionElapsed / transitionLength) : 1f;
        /// <summary>Current wind offsets in noise UV: shape xy, detail zw, each in [0, 1).</summary>
        public Vector4 CloudOffset => cloudOffset;

        public CloudWeather GetPreset(CloudPreset preset) => preset switch
        {
            CloudPreset.Clear => clearPreset,
            CloudPreset.Overcast => overcastPreset,
            _ => cloudyPreset,
        };

        /// <summary>Jumps to a preset, cancelling any running transition.</summary>
        public void ApplyPresetImmediate(CloudPreset preset)
        {
            transitionActive = false;
            SetWeather(GetPreset(preset));
        }

        /// <summary>Blends to a preset over <see cref="transitionDuration"/> seconds.</summary>
        public void TransitionToPreset(CloudPreset preset) => TransitionToWeather(GetPreset(preset), transitionDuration);

        public void TransitionToPreset(CloudPreset preset, float duration) => TransitionToWeather(GetPreset(preset), duration);

        /// <summary>
        /// Blends from the weather shown right now (also mid-way through another transition) to
        /// <paramref name="target"/>. Wind direction and cloud scale are not part of the blend.
        /// </summary>
        public void TransitionToWeather(CloudWeather target, float duration)
        {
            if (duration <= 0f)
            {
                transitionActive = false;
                SetWeather(target);
                return;
            }
            transitionFrom = CurrentWeather;
            transitionTo = target;
            transitionElapsed = 0f;
            transitionLength = duration;
            transitionActive = true;
#if UNITY_EDITOR
            lastEditorTime = EditorApplication.timeSinceStartup;
            if (!Application.isPlaying) EditorApplication.QueuePlayerLoopUpdate();
#endif
        }

        /// <summary>
        /// Shows the transition <paramref name="from"/> -> <paramref name="to"/> at a given progress
        /// (0..1), with the same easing as <see cref="TransitionToWeather"/>. For callers that drive
        /// transitions from an absolute clock instead of deltaTime. Wind direction takes the short
        /// way round. Cancels a running time-based transition; does nothing if the result is unchanged.
        /// </summary>
        public void ApplyTransitionAt(CloudWeather from, CloudWeather to, float progress,
                                      float fromWindDirection, float toWindDirection)
        {
            float t = WeatherMath.Ease(progress);
            SetWeatherState(CloudWeather.Lerp(from, to, t), WeatherMath.LerpAngle(fromWindDirection, toWindDirection, t));
        }

        /// <summary>Shows exactly this weather and wind direction (cancels a running transition).</summary>
        public void SetWeatherState(CloudWeather weather, float windDirectionDegrees)
        {
            transitionActive = false;
            float dir = WeatherMath.WrapAngle(windDirectionDegrees);
            if (CurrentWeather.Approximately(weather) && Mathf.Abs(Mathf.DeltaAngle(windDirection, dir)) < 1e-4f) return;
            SetWeather(weather);
            // Only the direction changes: the accumulated cloud offset keeps the clouds in place.
            windDirection = dir;
        }

        void SetWeather(CloudWeather w)
        {
            cloudCoverage = Mathf.Clamp01(w.coverage);
            cloudSoftness = Mathf.Clamp01(w.softness);
            overcastAmount = Mathf.Clamp01(w.overcast);
            windSpeed = Mathf.Max(0f, w.windSpeed);
            rainIntensity = Mathf.Clamp01(w.rain);
            lastWrittenWeather = CurrentWeather;
            dirty = true;
        }

        void OnEnable()
        {
            dirty = true;
            lastWrittenWeather = CurrentWeather;
            EnsureMaterial();
            Apply();
#if UNITY_EDITOR
            lastEditorTime = EditorApplication.timeSinceStartup;
            EditorSceneManager.sceneSaving += OnSceneSaving;
            EditorSceneManager.sceneSaved += OnSceneSaved;
            EditorApplication.update += OnEditorUpdate;
#endif
        }

        void OnDisable()
        {
#if UNITY_EDITOR
            EditorSceneManager.sceneSaving -= OnSceneSaving;
            EditorSceneManager.sceneSaved -= OnSceneSaved;
            EditorApplication.update -= OnEditorUpdate;
#endif
            ReleaseMaterial();
        }

        void OnValidate()
        {
            dirty = true;
            // Editing the weather by hand (Inspector, Undo) wins over a running transition.
            if (transitionActive && !CurrentWeather.Approximately(lastWrittenWeather)) transitionActive = false;
#if UNITY_EDITOR
            // Edit Mode only runs Update when something changes; ask for one so the preview follows.
            if (!Application.isPlaying) EditorApplication.QueuePlayerLoopUpdate();
#endif
        }

        void Update() => Tick(FrameDeltaTime());

        float FrameDeltaTime()
        {
            if (Application.isPlaying) return Time.deltaTime;
#if UNITY_EDITOR
            // Edit Mode has no reliable Time.deltaTime; clamp so a pause does not cause a jump.
            double now = EditorApplication.timeSinceStartup;
            float dt = Mathf.Clamp((float)(now - lastEditorTime), 0f, 0.1f);
            lastEditorTime = now;
            return dt;
#else
            return 0f;
#endif
        }

        /// <summary>
        /// Advances the shared clock (Play Mode auto-advance), weather transitions and wind, lets the
        /// weather driver decide the weather, and pushes values only when something changed. Wind only
        /// moves in Play Mode or with the Edit Mode preview on. Nothing advances while ClockPaused.
        /// </summary>
        public void Tick(float deltaTime)
        {
            if (ClockPaused) deltaTime = 0f;

            if (autoAdvance && Application.isPlaying && deltaTime > 0f && clockSpeed > 0f)
                AdvanceClock(deltaTime * clockSpeed * 24f / Mathf.Max(1f, dayLengthSeconds));

            if (transitionActive && deltaTime > 0f) AdvanceTransition(deltaTime);

            if ((Application.isPlaying || previewCloudMotionInEditMode) && deltaTime > 0f)
                AdvanceWind(deltaTime);

            weatherDriver?.DriveWeather(this, deltaTime);

            if (instanceSource != skyMaterial || appliedAsSkybox != (applyAsSkybox && instance != null))
            {
                ReleaseMaterial();
                EnsureMaterial();
                dirty = true;
            }

            if (dirty) Apply();
            else if (cloudOffsetDirty && instance != null)
            {
                instance.SetVector(CloudOffsetId, cloudOffset);
                cloudOffsetDirty = false;
            }
        }

        /// <summary>Moves the shared clock forward by game hours, counting days across midnight.</summary>
        public void AdvanceClock(double hours)
        {
            if (!(hours > 0.0)) return;
            TotalHours = TotalHours + hours;
        }

        void AdvanceTransition(float deltaTime)
        {
            transitionElapsed += deltaTime;
            float t = Mathf.Clamp01(transitionElapsed / transitionLength);
            SetWeather(CloudWeather.Lerp(transitionFrom, transitionTo, WeatherMath.Ease(t)));
            if (t >= 1f)
            {
                transitionActive = false;
#if UNITY_EDITOR
                // Keep the previewed result: field writes from code do not mark the scene dirty.
                if (!Application.isPlaying) EditorUtility.SetDirty(this);
#endif
            }
        }

        void AdvanceWind(float deltaTime)
        {
            if (windSpeed <= 0f || cloudScale <= 0f) return;
            // Clouds move towards windDirection. uv = plane * tiles + offset, so a feature moves by
            // +v when the offset moves by -v * tiles.
            float rad = windDirection * Mathf.Deg2Rad;
            float step = windSpeed * deltaTime * ShapeTilesPerUnit / cloudScale;
            float dx = -Mathf.Sin(rad) * step, dz = -Mathf.Cos(rad) * step;
            float detail = DetailScaleRatio * DetailDrift;
            cloudOffset.x = Mathf.Repeat(cloudOffset.x + dx, 1f);
            cloudOffset.y = Mathf.Repeat(cloudOffset.y + dz, 1f);
            cloudOffset.z = Mathf.Repeat(cloudOffset.z + dx * detail, 1f);
            cloudOffset.w = Mathf.Repeat(cloudOffset.w + dz * detail, 1f);
            cloudOffsetDirty = true;
        }

#if UNITY_EDITOR
        void OnEditorUpdate()
        {
            if (Application.isPlaying) return;
            bool animating = transitionActive || (weatherDriver != null && weatherDriver.IsAnimating) ||
                             (previewCloudMotionInEditMode && windSpeed > 0f && cloudCoverage > 0f);
            if (!animating) return;
            double now = EditorApplication.timeSinceStartup;
            if (now - lastEditorRepaint < 1.0 / 30.0) return;
            lastEditorRepaint = now;
            EditorApplication.QueuePlayerLoopUpdate();
            SceneView.RepaintAll();
        }
#endif

        static float Wrap(float hours) => Mathf.Repeat(hours, 24f);

        /// <summary>Pure evaluation of the sky for a time; no side effects, no allocations.</summary>
        public SkyState Evaluate(float hours)
        {
            // Angle around the sun's circle: 0 at 06:00 (rising), 90° at 12:00, 270° at 00:00.
            float angle = (Wrap(hours) / 24f) * 2f * Mathf.PI - 0.5f * Mathf.PI;
            float cos = Mathf.Cos(angle), sin = Mathf.Sin(angle);
            float elev = noonElevation * Mathf.Deg2Rad;

            // Path: east (+X) -> up/south (tilted by noon elevation) -> west (-X) -> below.
            var local = new Vector3(cos, sin * Mathf.Sin(elev), -sin * Mathf.Cos(elev));
            var dir = Quaternion.Euler(0f, sunPathAzimuth, 0f) * local;

            var s = new SkyState
            {
                sunDirection = dir,
                sunElevationSin = dir.y,
                // Phase follows the sun's height, so the colours are continuous across midnight.
                phase = 0.5f + 0.5f * sin,
            };
            s.zenith = zenithColor.Evaluate(s.phase);
            s.horizon = horizonColor.Evaluate(s.phase);
            s.ground = s.horizon * belowHorizonBrightness;
            s.ground.a = 1f;
            s.glow = sunriseSunsetGlow.Evaluate(s.phase);
            s.sunLight = sunLightColor.Evaluate(s.phase);

            // The shader cuts the disc at the horizon line; this only hides it once it is fully below.
            float radius = Mathf.Sin(sunSize * 0.5f * Mathf.Deg2Rad);
            s.sunVisibility = Mathf.Clamp01((dir.y + radius * 2f) / Mathf.Max(radius, 1e-4f));

            // Light fades to 0 as the sun reaches the horizon.
            float lightFade = Mathf.Clamp01(dir.y / 0.08f);
            s.clearLightIntensity = sunLightIntensity * lightFade * lightFade * (3f - 2f * lightFade);

            s.cloudLit = cloudLitColor.Evaluate(s.phase);
            s.cloudShade = cloudShadeColor.Evaluate(s.phase);

            // Overcast: muted, darker sky and clouds, faded sun. Always derived from the clear values
            // above, so nothing accumulates between frames. The sky moves towards the muted cloud
            // colours (lighter at the horizon), so gaps in a heavy cover stay soft instead of
            // showing as dark cracks.
            float ov = overcastAmount;
            float skyMul = 1f - overcastSkyDarkening * ov, cloudMul = 1f - overcastCloudDarkening * ov;
            var mutedShade = Mute(s.cloudShade, overcastCloudDesaturation);
            var mutedMid = Mute(Color.Lerp(s.cloudShade, s.cloudLit, 0.5f), overcastCloudDesaturation);
            float skyBlend = overcastSkyMuting * ov;
            s.zenith = Color.Lerp(s.zenith, mutedShade, skyBlend) * skyMul;
            s.horizon = Color.Lerp(s.horizon, mutedMid, skyBlend) * skyMul;
            s.zenith.a = s.horizon.a = 1f;
            s.ground = s.horizon * belowHorizonBrightness;
            s.ground.a = 1f;
            float sunFade = 1f - overcastSunFade * ov;
            s.glow.a *= sunFade;
            s.sunVisibility *= sunFade;
            float silver = s.cloudLit.a * s.sunVisibility;
            s.cloudLit = Mute(s.cloudLit, overcastCloudDesaturation * ov) * cloudMul;
            s.cloudShade = Mute(s.cloudShade, overcastCloudDesaturation * ov) * cloudMul;
            s.cloudLit.a = silver;
            s.cloudShade.a = 1f;
            s.lightIntensity = s.clearLightIntensity * Mathf.Lerp(1f, overcastLightMultiplier, ov);
            return s;
        }

        /// <summary>Moves a colour towards the overcast tint at the same luminance.</summary>
        Color Mute(Color c, float amount)
        {
            float luma = 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;
            var grey = new Color(luma * overcastTint.r, luma * overcastTint.g, luma * overcastTint.b, c.a);
            return Color.Lerp(c, grey, amount);
        }

        /// <summary>
        /// Shader constants for the cloud layer (see DayNightSky.shader). n is the noise value:
        /// shape + (detail - 0.5) * shape.w, with shape and detail equalised to [0, 1].
        /// </summary>
        public void GetCloudParams(out Vector4 shape, out Vector4 density, out Vector4 lighting)
        {
            float shapeTiles = ShapeTilesPerUnit / Mathf.Max(cloudScale, 0.01f);
            // Little detail at low coverage keeps the few clear-sky clouds clean puffs instead of specks.
            float detailStrength = cloudDetail * 0.5f * Mathf.Lerp(0.4f, 1f, Mathf.SmoothStep(0f, 0.4f, cloudCoverage));
            shape = new Vector4(shapeTiles, shapeTiles * DetailScaleRatio, DomeCurvature, detailStrength);

            // Soft threshold: density rises from 0 at edge0 to 1 at edge0 + soft. Coverage 0 puts
            // edge0 at the largest possible n (no clouds at all); coverage 1 puts the full-density
            // edge below the smallest n (sky fully covered).
            float nMax = 1f + 0.5f * detailStrength, nMin = -0.5f * detailStrength;
            float soft = Mathf.Lerp(0.04f, 0.5f, cloudSoftness);
            float edge0 = Mathf.Lerp(nMax, nMin - soft, cloudCoverage);
            float opacity = cloudCoverage > 0f ? 1f : 0f;
            density = new Vector4(1f / soft, -edge0 / soft, opacity, 1f / HorizonFadeHeight);

            // Thickness 0 at the full-density edge, 1 at the densest noise: keeps variation even in
            // a fully overcast sky.
            float edge1 = edge0 + soft;
            float depth = Mathf.Max(nMax - edge1, 0.1f);
            float cosHalo = Mathf.Cos(SunHaloAngle * Mathf.Deg2Rad);
            lighting = new Vector4(1f / depth, -edge1 / depth, 1f / (1f - cosHalo), -cosHalo / (1f - cosHalo));
        }

        void Apply()
        {
            dirty = false;
            var s = Evaluate(timeOfDay);
            currentState = s;

            if (instance != null)
            {
                instance.SetColor(ZenithColorId, s.zenith);
                instance.SetColor(HorizonColorId, s.horizon);
                instance.SetColor(GroundColorId, s.ground);
                instance.SetColor(GlowColorId, s.glow);
                var sun = sunDiscColor;
                sun.a = s.sunVisibility;
                instance.SetColor(SunColorId, sun);
                instance.SetVector(SunDirectionId, s.sunDirection);

                instance.SetVector(SkyParamsId, new Vector4(1f / gradientHeight, 12f, 1f / glowHeight, 0f));

                float outer = sunSize * 0.5f * Mathf.Deg2Rad;
                float inner = outer * (1f - sunSoftness);
                float cosOuter = Mathf.Cos(outer), cosInner = Mathf.Cos(inner);
                float edge = Mathf.Max(cosInner - cosOuter, 1e-6f);
                // Glow side mask: saturate(d01 * scale + offset) is 1 facing the sun, 0 beyond the width.
                instance.SetVector(SunParamsId, new Vector4(cosOuter, 1f / edge, 1f / glowWidth, 1f - 1f / glowWidth));

                instance.SetColor(CloudLitColorId, s.cloudLit);
                instance.SetColor(CloudShadeColorId, s.cloudShade);
                GetCloudParams(out var cloudShape, out var cloudDensity, out var cloudLighting);
                instance.SetVector(CloudShapeId, cloudShape);
                instance.SetVector(CloudDensityId, cloudDensity);
                instance.SetVector(CloudLightingId, cloudLighting);
                instance.SetVector(CloudOffsetId, cloudOffset);
                cloudOffsetDirty = false;
            }

            if (sunLight != null)
            {
                // A directional light shines along its forward axis: from the sun towards the scene.
                if (s.sunDirection.sqrMagnitude > 0f)
                    sunLight.transform.rotation = Quaternion.LookRotation(-s.sunDirection,
                        Mathf.Abs(s.sunDirection.y) > 0.999f ? Vector3.forward : Vector3.up);
                sunLight.color = s.sunLight;
                // Already includes the overcast dimming, computed from the day/night value every time.
                sunLight.intensity = s.lightIntensity;
                if (disableLightAtNight)
                {
                    bool on = s.lightIntensity > 0f;
                    if (sunLight.enabled != on) sunLight.enabled = on;
                }
            }
        }

        void EnsureMaterial()
        {
            if (instance == null && skyMaterial != null)
            {
                instance = new Material(skyMaterial) { name = skyMaterial.name + " (Instance)", hideFlags = HideFlags.DontSave };
                instanceSource = skyMaterial;
            }

            if (applyAsSkybox && instance != null && !appliedAsSkybox)
            {
                previousSkybox = RenderSettings.skybox;
                RenderSettings.skybox = instance;
                appliedAsSkybox = true;
            }
        }

        void ReleaseMaterial()
        {
            if (appliedAsSkybox)
            {
                // Only restore if nobody replaced our skybox in the meantime.
                if (RenderSettings.skybox == instance) RenderSettings.skybox = previousSkybox;
                appliedAsSkybox = false;
                previousSkybox = null;
            }

            if (instance != null)
            {
                if (Application.isPlaying) Destroy(instance);
                else DestroyImmediate(instance);
            }
            instance = null;
            instanceSource = null;
        }

#if UNITY_EDITOR
        // The instance is not an asset: never save a scene that points at it.
        void OnSceneSaving(UnityEngine.SceneManagement.Scene scene, string path)
        {
            if (appliedAsSkybox && RenderSettings.skybox == instance) RenderSettings.skybox = previousSkybox;
        }

        void OnSceneSaved(UnityEngine.SceneManagement.Scene scene)
        {
            if (appliedAsSkybox && instance != null) RenderSettings.skybox = instance;
        }
#endif

        // ---------------------------------------------------------------------------------------
        // Defaults (cozy palette). Axis: 0 = night, 0.5 = sun on the horizon, 1 = noon.

        static Gradient Make(GradientColorKey[] colors, GradientAlphaKey[] alphas)
        {
            var g = new Gradient();
            g.SetKeys(colors, alphas);
            return g;
        }

        static GradientAlphaKey[] Opaque => new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) };

        static Gradient DefaultZenith() => Make(new[]
        {
            new GradientColorKey(new Color(0.09f, 0.11f, 0.25f), 0.00f),
            new GradientColorKey(new Color(0.12f, 0.15f, 0.33f), 0.38f),
            new GradientColorKey(new Color(0.31f, 0.33f, 0.58f), 0.50f),
            new GradientColorKey(new Color(0.38f, 0.58f, 0.88f), 0.62f),
            new GradientColorKey(new Color(0.33f, 0.60f, 0.92f), 1.00f),
        }, Opaque);

        static Gradient DefaultHorizon() => Make(new[]
        {
            new GradientColorKey(new Color(0.17f, 0.20f, 0.38f), 0.00f),
            new GradientColorKey(new Color(0.27f, 0.25f, 0.46f), 0.40f),
            new GradientColorKey(new Color(0.90f, 0.64f, 0.66f), 0.50f),
            new GradientColorKey(new Color(0.96f, 0.82f, 0.78f), 0.58f),
            new GradientColorKey(new Color(0.80f, 0.90f, 0.98f), 0.70f),
            new GradientColorKey(new Color(0.78f, 0.89f, 0.98f), 1.00f),
        }, Opaque);

        static Gradient DefaultGlow() => Make(new[]
        {
            new GradientColorKey(new Color(0.95f, 0.55f, 0.45f), 0.00f),
            new GradientColorKey(new Color(1.00f, 0.55f, 0.36f), 0.50f),
            new GradientColorKey(new Color(1.00f, 0.78f, 0.55f), 0.62f),
            new GradientColorKey(new Color(1.00f, 0.85f, 0.65f), 1.00f),
        }, new[]
        {
            new GradientAlphaKey(0f, 0.00f),
            new GradientAlphaKey(0f, 0.40f),
            new GradientAlphaKey(0.85f, 0.50f),
            new GradientAlphaKey(0.35f, 0.60f),
            new GradientAlphaKey(0f, 0.74f),
            new GradientAlphaKey(0f, 1.00f),
        });

        static Gradient DefaultLightColor() => Make(new[]
        {
            new GradientColorKey(new Color(1.00f, 0.60f, 0.40f), 0.00f),
            new GradientColorKey(new Color(1.00f, 0.60f, 0.40f), 0.50f),
            new GradientColorKey(new Color(1.00f, 0.86f, 0.70f), 0.60f),
            new GradientColorKey(new Color(1.00f, 0.96f, 0.90f), 0.80f),
            new GradientColorKey(new Color(1.00f, 0.97f, 0.92f), 1.00f),
        }, Opaque);

        // Night clouds stay a little lighter than the night sky so their shapes remain readable.
        static Gradient DefaultCloudLit() => Make(new[]
        {
            new GradientColorKey(new Color(0.25f, 0.27f, 0.42f), 0.00f),
            new GradientColorKey(new Color(0.31f, 0.31f, 0.48f), 0.40f),
            new GradientColorKey(new Color(1.00f, 0.74f, 0.64f), 0.50f),
            new GradientColorKey(new Color(1.00f, 0.89f, 0.82f), 0.58f),
            new GradientColorKey(new Color(1.00f, 0.98f, 0.96f), 0.70f),
            new GradientColorKey(new Color(1.00f, 1.00f, 1.00f), 1.00f),
        }, new[]
        {
            new GradientAlphaKey(0.0f, 0.00f),
            new GradientAlphaKey(0.0f, 0.40f),
            new GradientAlphaKey(0.6f, 0.50f),
            new GradientAlphaKey(0.35f, 0.70f),
            new GradientAlphaKey(0.35f, 1.00f),
        });

        static Gradient DefaultCloudShade() => Make(new[]
        {
            new GradientColorKey(new Color(0.15f, 0.16f, 0.30f), 0.00f),
            new GradientColorKey(new Color(0.20f, 0.20f, 0.36f), 0.40f),
            new GradientColorKey(new Color(0.64f, 0.52f, 0.64f), 0.50f),
            new GradientColorKey(new Color(0.80f, 0.72f, 0.78f), 0.58f),
            new GradientColorKey(new Color(0.76f, 0.82f, 0.92f), 0.70f),
            new GradientColorKey(new Color(0.79f, 0.85f, 0.95f), 1.00f),
        }, Opaque);
    }
}
