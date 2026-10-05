using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
#endif

namespace DayNightSky
{
    /// <summary>
    /// Drives the "Skybox/Day Night Gradient" shader. Everything that depends on the time of day
    /// (sun direction, colours, visibility) is computed here once per change and passed to a
    /// private material instance; the shader only does per-view-direction work.
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
        }

        [Header("Time")]
        [Tooltip("Hours, 0–24. 24 is the same moment as 0.")]
        [Range(0f, 24f)] public float timeOfDay = 9f;
        [Tooltip("Advance time automatically in Play Mode.")]
        public bool autoAdvance = true;
        [Tooltip("Real seconds for a full 24 h cycle.")]
        [Min(1f)] public float dayLengthSeconds = 240f;

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

        static readonly int ZenithColorId = Shader.PropertyToID("_ZenithColor");
        static readonly int HorizonColorId = Shader.PropertyToID("_HorizonColor");
        static readonly int GroundColorId = Shader.PropertyToID("_GroundColor");
        static readonly int GlowColorId = Shader.PropertyToID("_GlowColor");
        static readonly int SunColorId = Shader.PropertyToID("_SunColor");
        static readonly int SunDirectionId = Shader.PropertyToID("_SunDirection");
        static readonly int SkyParamsId = Shader.PropertyToID("_SkyParams");
        static readonly int SunParamsId = Shader.PropertyToID("_SunParams");

        Material instance;
        Material instanceSource;
        Material previousSkybox;
        bool appliedAsSkybox;
        bool dirty = true;

        /// <summary>Material instance currently used (null when disabled or no template).</summary>
        public Material MaterialInstance => instance;

        public float TimeOfDay
        {
            get => timeOfDay;
            set
            {
                timeOfDay = Wrap(value);
                dirty = true;
            }
        }

        void OnEnable()
        {
            dirty = true;
            EnsureMaterial();
            Apply();
#if UNITY_EDITOR
            EditorSceneManager.sceneSaving += OnSceneSaving;
            EditorSceneManager.sceneSaved += OnSceneSaved;
#endif
        }

        void OnDisable()
        {
#if UNITY_EDITOR
            EditorSceneManager.sceneSaving -= OnSceneSaving;
            EditorSceneManager.sceneSaved -= OnSceneSaved;
#endif
            ReleaseMaterial();
        }

        void OnValidate()
        {
            dirty = true;
#if UNITY_EDITOR
            // Edit Mode only runs Update when something changes; ask for one so the preview follows.
            if (!Application.isPlaying) EditorApplication.QueuePlayerLoopUpdate();
#endif
        }

        void Update() => Tick(Application.isPlaying ? Time.deltaTime : 0f);

        /// <summary>Advances time (Play Mode auto-advance) and pushes values only when something changed.</summary>
        public void Tick(float deltaTime)
        {
            if (autoAdvance && Application.isPlaying && deltaTime > 0f)
            {
                timeOfDay = Wrap(timeOfDay + deltaTime * 24f / Mathf.Max(1f, dayLengthSeconds));
                dirty = true;
            }

            if (instanceSource != skyMaterial || appliedAsSkybox != (applyAsSkybox && instance != null))
            {
                ReleaseMaterial();
                EnsureMaterial();
                dirty = true;
            }

            if (dirty) Apply();
        }

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
            s.lightIntensity = sunLightIntensity * lightFade * lightFade * (3f - 2f * lightFade);
            return s;
        }

        void Apply()
        {
            dirty = false;
            var s = Evaluate(timeOfDay);

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
            }

            if (sunLight != null)
            {
                // A directional light shines along its forward axis: from the sun towards the scene.
                if (s.sunDirection.sqrMagnitude > 0f)
                    sunLight.transform.rotation = Quaternion.LookRotation(-s.sunDirection,
                        Mathf.Abs(s.sunDirection.y) > 0.999f ? Vector3.forward : Vector3.up);
                sunLight.color = s.sunLight;
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
    }
}
