using System;
using UnityEngine;

namespace DayNightSky
{
    public enum RainQuality { Low, Medium, High }

    /// <summary>Particle budget and area for one quality level. Display only: never affects the weather.</summary>
    [Serializable]
    public struct RainQualitySettings
    {
        [Tooltip("Hard cap on live drops (ParticleSystem.maxParticles).")]
        [Min(1)] public int maxParticles;
        [Tooltip("Drops per second at rain intensity 1, before the budget clamp.")]
        [Min(0f)] public float maxEmissionPerSecond;
        [Tooltip("Width of the rain area across the camera's view (m).")]
        [Min(2f)] public float areaWidth;
        [Tooltip("Depth of the rain area along the camera's horizontal forward (m).")]
        [Min(2f)] public float areaDepth;

        public RainQualitySettings(int maxParticles, float maxEmissionPerSecond, float areaWidth, float areaDepth)
        {
            this.maxParticles = maxParticles;
            this.maxEmissionPerSecond = maxEmissionPerSecond;
            this.areaWidth = areaWidth;
            this.areaDepth = areaDepth;
        }
    }

    /// <summary>Emission numbers after the budget clamp (see <see cref="RainController.ComputeBudget"/>).</summary>
    public struct RainBudget
    {
        public int maxParticles;
        public float lifetime;              // seconds a drop lives (fall height / fall speed)
        public float requestedEmission;     // drops/s at intensity 1, as configured
        public float effectiveEmission;     // drops/s at intensity 1, after the clamp
        public float steadyStateParticles;  // live drops at intensity 1 ≈ effective emission × lifetime
        public bool clamped;
    }

    /// <summary>
    /// Rain around a camera with one Particle System. Reads the blended weather from the sky (rain
    /// intensity, wind, pause, day/night colours) and never writes to it, so the sky stays the only
    /// owner of the weather. Particles simulate in world space; the emitter only follows the camera.
    ///
    /// Velocity is set in exactly one place (Velocity over Lifetime, world space): straight down at
    /// Fall Speed plus a clamped horizontal wind. Changing the wind turns live drops smoothly, they
    /// never jump. Drop motion uses real (scaled) frame time, so a fast game clock changes the
    /// weather faster but never makes drops fall faster.
    ///
    /// Limitation (this milestone): no collision. Rain falls through roofs and appears indoors.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(100)]  // after typical camera scripts
    [RequireComponent(typeof(ParticleSystem))]
    public sealed class RainController : MonoBehaviour
    {
        /// <summary>Absolute ceiling on live drops for any quality level (mobile safety net).</summary>
        public const int HardParticleLimit = 4000;
        /// <summary>Share of maxParticles the steady state may use; the rest absorbs frame-time spikes.</summary>
        public const float BudgetHeadroom = 0.9f;

        [Header("Sources")]
        [Tooltip("Sky to read rain intensity, wind, pause and colours from. Found once if empty.")]
        public DayNightSkyController sky;
        [Tooltip("Camera the rain follows. Uses Camera.main if empty (looked up only when missing, at most once per second).")]
        public Camera targetCamera;
        [Tooltip("Shared streak material (DayNightSky/Rain Streak). Never modified; colours go through a property block.")]
        public Material material;

        [Header("Quality (display only)")]
        public RainQuality quality = RainQuality.Medium;
        public RainQualitySettings low = new RainQualitySettings(500, 330f, 16f, 18f);
        public RainQualitySettings medium = new RainQualitySettings(1000, 650f, 20f, 24f);
        public RainQualitySettings high = new RainQualitySettings(1800, 1200f, 26f, 30f);

        [Header("Area (m, world space, follows the camera)")]
        [Tooltip("Height of the emitter above the camera. Higher = covers steeper upward views, but the area takes longer to fill.")]
        [Min(1f)] public float heightAboveCamera = 9f;
        [Tooltip("How far below the camera drops keep falling before they die.")]
        [Min(0f)] public float depthBelowCamera = 4f;
        [Tooltip("Shifts the area forward, as a fraction of its depth, so most drops are where the camera looks.")]
        [Range(0f, 0.5f)] public float forwardBias = 0.3f;
        [Tooltip("Thickness of the emitter slab; breaks up visible layers of drops.")]
        [Min(0.1f)] public float emitterThickness = 2f;
        [Tooltip("How much the area leads a moving camera (1 = drops land where the camera will be).")]
        [Range(0f, 1.5f)] public float velocityLead = 1f;
        [Tooltip("A camera jump larger than this in one frame counts as a teleport: drops are cleared and refilled at the new place.")]
        [Min(1f)] public float teleportDistance = 25f;
        [Tooltip("Drops placed at once after a teleport, as a fraction of the current steady amount (bounded by the budget).")]
        [Range(0f, 1f)] public float teleportRefill = 0.6f;

        [Header("Look")]
        [Tooltip("Streak length (m).")]
        [Min(0.05f)] public float streakLength = 0.55f;
        [Tooltip("Streak width (m). Keep thin; very thin streaks shimmer on low-resolution screens.")]
        [Min(0.002f)] public float streakWidth = 0.03f;
        [Tooltip("Daytime colour; alpha = opacity. Keep alpha low for a cozy look that never hides the scene.")]
        public Color rainColor = new Color(0.80f, 0.86f, 0.96f, 0.42f);
        [Tooltip("Falling speed (m/s, real time).")]
        [Min(1f)] public float fallSpeed = 10f;
        [Tooltip("Brightness multiplier at night (0..1).")]
        [Range(0f, 1f)] public float nightBrightness = 0.45f;
        [Tooltip("How much the colour leans towards the sky's horizon colour (warm at sunset, blue at night).")]
        [Range(0f, 1f)] public float skyTint = 0.25f;
        [Tooltip("Drops closer than this (m) are invisible; they fade in over Near Fade Range.")]
        [Min(0f)] public float nearFadeStart = 0.6f;
        [Min(0.05f)] public float nearFadeRange = 1.2f;

        [Header("Wind")]
        [Tooltip("Metres per second of sideways drift per unit of the sky's Wind Speed (cloud-plane units/s). " +
                 "Default 150: Cloudy 0.01 → 1.5 m/s, Rain 0.02 → 3 m/s.")]
        [Min(0f)] public float windToRain = 150f;
        [Tooltip("Largest tilt from vertical. Caps strong wind so drops never leave the area or the view.")]
        [Range(0f, 45f)] public float maxTiltDegrees = 25f;

        [Header("Preview (Play Mode, visual only)")]
        [Tooltip("Show Preview Intensity instead of the weather's rain. Does not change the weather or schedule.")]
        public bool previewIntensity;
        [Range(0f, 1f)] public float previewValue = 1f;

        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        static readonly int NearFadeId = Shader.PropertyToID("_NearFade");
        const float CameraSearchInterval = 1f;
        const int TeleportRefillCap = 1500;

        ParticleSystem ps;
        ParticleSystemRenderer psRenderer;
        MaterialPropertyBlock block;
        RainBudget budget;
        bool settingsDirty = true;
        bool idle = true;
        bool pausedByClock;
        bool suppressEmissionOneFrame;
        float nextCameraSearch, nextSkySearch;
        Vector3 lastCameraPosition, cameraVelocity, windVelocity;
        bool hasLastCameraPosition;
        Color lastColor = new Color(-1f, 0f, 0f, 0f);
        float currentIntensity;
        uint rng = 0x9E3779B9u;

        public ParticleSystem Particles => ps;
        public RainBudget Budget => budget;
        public float CurrentIntensity => currentIntensity;
        public bool IsIdle => idle;
        public bool IsPausedByClock => pausedByClock;
        public int AliveParticles => ps != null ? ps.particleCount : 0;
        /// <summary>World-space drop velocity currently applied (down + clamped wind).</summary>
        public Vector3 DropVelocity => new Vector3(windVelocity.x, -fallSpeed, windVelocity.z);
        public int TeleportCount { get; private set; }

        public RainQualitySettings ActiveSettings => quality switch
        {
            RainQuality.Low => low,
            RainQuality.High => high,
            _ => medium,
        };

        /// <summary>
        /// Live drops ≈ emission (drops/s) × lifetime (s), with lifetime = (height above + depth below) /
        /// fall speed. The emission is clamped so the steady state stays within
        /// <see cref="BudgetHeadroom"/> of maxParticles (itself capped at <see cref="HardParticleLimit"/>).
        /// </summary>
        public static RainBudget ComputeBudget(in RainQualitySettings q, float heightAbove, float depthBelow, float fallSpeed)
        {
            var b = new RainBudget
            {
                maxParticles = Mathf.Clamp(q.maxParticles, 1, HardParticleLimit),
                lifetime = (Mathf.Max(1f, heightAbove) + Mathf.Max(0f, depthBelow)) / Mathf.Max(1f, fallSpeed),
                requestedEmission = Mathf.Max(0f, q.maxEmissionPerSecond),
            };
            float allowed = b.maxParticles * BudgetHeadroom / b.lifetime;
            b.clamped = b.requestedEmission > allowed;
            b.effectiveEmission = Mathf.Min(b.requestedEmission, allowed);
            b.steadyStateParticles = b.effectiveEmission * b.lifetime;
            return b;
        }

        public void SetQuality(RainQuality q)
        {
            quality = q;
            settingsDirty = true;
        }

        void OnEnable()
        {
            ps = GetComponent<ParticleSystem>();
            psRenderer = GetComponent<ParticleSystemRenderer>();
            block ??= new MaterialPropertyBlock();
            ConfigureParticleSystem();
            ResolveSources(force: true);
            hasLastCameraPosition = false;
            pausedByClock = false;
            GoIdle();
        }

        void OnDisable()
        {
            if (ps != null) GoIdle();
        }

        void OnValidate() => settingsDirty = true;

        void LateUpdate() => UpdateRain();

        /// <summary>One frame of rain (called from LateUpdate). Allocation free in steady state.</summary>
        public void UpdateRain()
        {
            if (ps == null) return;
            if (settingsDirty) ConfigureParticleSystem();
            if (!ResolveSources(force: false)) return;

            bool clockPaused = sky != null && sky.ClockPaused;
            FollowCamera(clockPaused);

            if (clockPaused)
            {
                // Hold every drop where it is; nothing is emitted, so Resume cannot burst.
                if (!idle && !pausedByClock) { ps.Pause(true); pausedByClock = true; }
                return;
            }
            if (pausedByClock)
            {
                pausedByClock = false;
                if (!idle) ps.Play(true);
            }

            currentIntensity = previewIntensity ? previewValue : (sky != null ? sky.RainIntensity : 0f);
            UpdateWind();
            UpdateColor();
            UpdateEmission();
        }

        bool ResolveSources(bool force)
        {
            float now = Time.unscaledTime;
            if (targetCamera == null && (force || now >= nextCameraSearch))
            {
                targetCamera = Camera.main;
                nextCameraSearch = now + CameraSearchInterval;
            }
            if (sky == null && (force || now >= nextSkySearch))
            {
                sky = FindFirstObjectByType<DayNightSkyController>();
                nextSkySearch = now + CameraSearchInterval;
            }
            return targetCamera != null;
        }

        // ------------------------------------------------------------------------------------
        // Emitter placement

        void FollowCamera(bool clockPaused)
        {
            var camT = targetCamera.transform;
            Vector3 camPos = camT.position;
            float dt = Time.deltaTime;

            bool teleported = false;
            if (hasLastCameraPosition)
            {
                Vector3 delta = camPos - lastCameraPosition;
                if (delta.sqrMagnitude > teleportDistance * teleportDistance) teleported = true;
                else if (dt > 0f)
                {
                    // Smoothed camera velocity, used to lead the area for a moving camera.
                    float k = 1f - Mathf.Exp(-dt * 4f);
                    cameraVelocity = Vector3.Lerp(cameraVelocity, delta / dt, k);
                }
            }
            lastCameraPosition = camPos;
            hasLastCameraPosition = true;
            if (teleported) cameraVelocity = Vector3.zero;

            transform.SetPositionAndRotation(EmitterCenter(camT, out var yaw), yaw);

            if (teleported)
            {
                TeleportCount++;
                ps.Clear(true);
                // Unity interpolates emission between last and current emitter position; skip a frame
                // so no drops are spawned along the jump, and place a bounded refill instead.
                suppressEmissionOneFrame = true;
                var emission = ps.emission;
                emission.enabled = false;
                if (!clockPaused && !idle) Refill();
            }
            else if (suppressEmissionOneFrame)
            {
                suppressEmissionOneFrame = false;
                var emission = ps.emission;
                emission.enabled = true;
            }
        }

        /// <summary>Emitter centre above the camera: forward-biased, leading camera motion, shifted upwind.</summary>
        Vector3 EmitterCenter(Transform camT, out Quaternion yaw)
        {
            var q = ActiveSettings;
            Vector3 fwd = camT.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-4f) { fwd = camT.up; fwd.y = 0f; }       // looking straight up/down
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            fwd.Normalize();
            yaw = Quaternion.LookRotation(fwd, Vector3.up);                    // yaw only: pitch/roll never tilt the rain

            float fallTime = heightAboveCamera / Mathf.Max(1f, fallSpeed);
            Vector3 shift = fwd * (q.areaDepth * forwardBias)
                            + Horizontal(cameraVelocity) * (fallTime * velocityLead)
                            - windVelocity * fallTime;                          // drops drift back over the camera
            float maxShift = 0.5f * Mathf.Max(q.areaWidth, q.areaDepth);
            if (shift.sqrMagnitude > maxShift * maxShift) shift = shift.normalized * maxShift;
            return camT.position + Vector3.up * heightAboveCamera + shift;
        }

        static Vector3 Horizontal(Vector3 v) => new Vector3(v.x, 0f, v.z);

        void Refill()
        {
            int count = Mathf.Min(Mathf.RoundToInt(budget.steadyStateParticles * currentIntensity * teleportRefill),
                                  Mathf.Min(budget.maxParticles, TeleportRefillCap));
            if (count <= 0) return;
            var q = ActiveSettings;
            Vector3 top = transform.position;
            Vector3 right = transform.right, fwd = transform.forward;
            float column = heightAboveCamera + depthBelowCamera;
            var p = new ParticleSystem.EmitParams { applyShapeToPosition = false, velocity = Vector3.zero };
            for (int i = 0; i < count; i++)
            {
                float fallen = NextFloat() * column;                            // distance already fallen
                float t = fallen / fallSpeed;
                p.position = top + right * ((NextFloat() - 0.5f) * q.areaWidth) + fwd * ((NextFloat() - 0.5f) * q.areaDepth)
                             + Vector3.down * fallen + windVelocity * t;
                p.startLifetime = Mathf.Max(0.05f, (column - fallen) / fallSpeed);
                ps.Emit(p, 1);
            }
        }

        float NextFloat()
        {
            // xorshift32: private stream, never touches UnityEngine.Random.
            rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5;
            return (rng >> 8) * (1f / 16777216f);
        }

        // ------------------------------------------------------------------------------------
        // Per-frame state

        void UpdateWind()
        {
            float speed = sky != null ? sky.WindSpeed * windToRain : 0f;
            float maxHorizontal = fallSpeed * Mathf.Tan(maxTiltDegrees * Mathf.Deg2Rad);
            speed = Mathf.Min(speed, maxHorizontal);
            float rad = (sky != null ? sky.WindDirection : 0f) * Mathf.Deg2Rad;
            windVelocity = new Vector3(Mathf.Sin(rad) * speed, 0f, Mathf.Cos(rad) * speed);

            var vel = ps.velocityOverLifetime;
            vel.xMultiplier = windVelocity.x;
            vel.yMultiplier = -fallSpeed;
            vel.zMultiplier = windVelocity.z;
        }

        void UpdateColor()
        {
            Color c = rainColor;
            if (sky != null)
            {
                var s = sky.CurrentState;
                float day = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.42f, 0.62f, s.phase));
                float brightness = Mathf.Lerp(nightBrightness, 1f, day);
                float alpha = c.a;
                c = Color.Lerp(c * brightness, s.horizon, skyTint);
                c.a = alpha;
            }
            if (Mathf.Abs(c.r - lastColor.r) + Mathf.Abs(c.g - lastColor.g) + Mathf.Abs(c.b - lastColor.b) + Mathf.Abs(c.a - lastColor.a) < 0.002f)
                return;
            lastColor = c;
            psRenderer.GetPropertyBlock(block);
            block.SetColor(BaseColorId, c);
            block.SetVector(NearFadeId, new Vector4(nearFadeStart, 1f / nearFadeRange, 0f, 0f));
            psRenderer.SetPropertyBlock(block);
        }

        void UpdateEmission()
        {
            var emission = ps.emission;
            emission.rateOverTimeMultiplier = currentIntensity * budget.effectiveEmission;

            if (currentIntensity > 1e-4f)
            {
                if (idle)
                {
                    // Reuse the same system: just switch it back on.
                    idle = false;
                    psRenderer.enabled = true;
                    ps.Play(true);
                }
            }
            else if (!idle && ps.particleCount == 0)
            {
                GoIdle();   // the last drops have landed: stop simulating and drawing
            }
            // Between the two: no new drops, the live ones finish their fall naturally.
        }

        void GoIdle()
        {
            idle = true;
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            if (psRenderer != null) psRenderer.enabled = false;
            var emission = ps.emission;
            emission.rateOverTimeMultiplier = 0f;
        }

        // ------------------------------------------------------------------------------------
        // Setup (on enable and when settings change; may allocate)

        /// <summary>Applies every particle setting from this component. Also used by the Editor to build the prefab.</summary>
        public void ConfigureParticleSystem()
        {
            if (ps == null) ps = GetComponent<ParticleSystem>();
            if (psRenderer == null) psRenderer = GetComponent<ParticleSystemRenderer>();
            settingsDirty = false;

            var q = ActiveSettings;
            budget = ComputeBudget(q, heightAboveCamera, depthBelowCamera, fallSpeed);

            var main = ps.main;
            main.loop = true;
            main.playOnAwake = false;
            main.startSpeed = 0f;
            main.startLifetime = budget.lifetime;
            main.startSize = streakWidth;
            main.startRotation = 0f;
            main.startColor = Color.white;
            main.gravityModifier = 0f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.scalingMode = ParticleSystemScalingMode.Shape;
            main.maxParticles = budget.maxParticles;
            main.cullingMode = ParticleSystemCullingMode.AlwaysSimulate;   // the system is always around the camera
            main.ringBufferMode = ParticleSystemRingBufferMode.Disabled;
            main.emitterVelocityMode = ParticleSystemEmitterVelocityMode.Transform;

            var emission = ps.emission;
            emission.enabled = true;
            emission.rateOverTime = 0f;
            emission.rateOverDistance = 0f;
            emission.burstCount = 0;

            var shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.position = Vector3.zero;
            shape.rotation = Vector3.zero;
            shape.scale = new Vector3(q.areaWidth, emitterThickness, q.areaDepth);
            shape.randomDirectionAmount = 0f;
            shape.randomPositionAmount = 0f;
            shape.alignToDirection = false;

            // The only module that moves drops (start speed and gravity are 0).
            var vel = ps.velocityOverLifetime;
            vel.enabled = true;
            vel.space = ParticleSystemSimulationSpace.World;
            vel.x = new ParticleSystem.MinMaxCurve(windVelocity.x);
            vel.y = new ParticleSystem.MinMaxCurve(-fallSpeed);
            vel.z = new ParticleSystem.MinMaxCurve(windVelocity.z);

            // Short fade in/out so drops never pop.
            var col = ps.colorOverLifetime;
            col.enabled = true;
            var g = new Gradient();
            g.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                      new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.1f), new GradientAlphaKey(1f, 0.85f), new GradientAlphaKey(0f, 1f) });
            col.color = g;

            // Everything expensive stays off.
            var collision = ps.collision; collision.enabled = false;
            var trails = ps.trails; trails.enabled = false;
            var lights = ps.lights; lights.enabled = false;
            var noise = ps.noise; noise.enabled = false;
            var subEmitters = ps.subEmitters; subEmitters.enabled = false;
            var inherit = ps.inheritVelocity; inherit.enabled = false;
            var force = ps.forceOverLifetime; force.enabled = false;
            var limit = ps.limitVelocityOverLifetime; limit.enabled = false;
            var size = ps.sizeOverLifetime; size.enabled = false;
            var sheet = ps.textureSheetAnimation; sheet.enabled = false;

            psRenderer.renderMode = ParticleSystemRenderMode.Stretch;
            psRenderer.lengthScale = streakLength / Mathf.Max(streakWidth, 1e-4f);   // length in metres, independent of speed
            psRenderer.velocityScale = 0f;
            psRenderer.cameraVelocityScale = 0f;
            psRenderer.sharedMaterial = material;
            psRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            psRenderer.receiveShadows = false;
            psRenderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            psRenderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            psRenderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            psRenderer.sortMode = ParticleSystemSortMode.None;
            psRenderer.minParticleSize = 0f;
            psRenderer.maxParticleSize = 0.5f;

            lastColor = new Color(-1f, 0f, 0f, 0f);  // force a colour refresh
        }

        // ------------------------------------------------------------------------------------
        // Gizmos

        void OnDrawGizmosSelected()
        {
            var cam = targetCamera != null ? targetCamera : Camera.main;
            if (cam == null) return;
            var q = ActiveSettings;
            Vector3 center = EmitterCenter(cam.transform, out var yaw);
            var old = Gizmos.matrix;

            // Emitter slab.
            Gizmos.color = new Color(0.4f, 0.7f, 1f, 0.9f);
            Gizmos.matrix = Matrix4x4.TRS(center, yaw, Vector3.one);
            Gizmos.DrawWireCube(Vector3.zero, new Vector3(q.areaWidth, emitterThickness, q.areaDepth));

            // Expected fall volume: the slab swept along the current drop velocity down to its lowest point.
            float column = heightAboveCamera + depthBelowCamera;
            Vector3 drift = windVelocity * (column / Mathf.Max(1f, fallSpeed));
            Gizmos.color = new Color(0.4f, 0.7f, 1f, 0.35f);
            Gizmos.matrix = Matrix4x4.TRS(center + 0.5f * (Vector3.down * column + drift), yaw, Vector3.one);
            Gizmos.DrawWireCube(Vector3.zero, new Vector3(q.areaWidth, column, q.areaDepth));
            Gizmos.matrix = old;
            Gizmos.DrawLine(center, center + Vector3.down * column + drift);
            Gizmos.DrawWireSphere(cam.transform.position, nearFadeStart);
        }
    }
}
