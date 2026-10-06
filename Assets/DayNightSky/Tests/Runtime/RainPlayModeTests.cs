using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;

namespace DayNightSky.Tests
{
    public class RainPlayModeTests
    {
        GameObject camGo, skyGo, rainGo;
        Camera cam;
        DayNightSkyController sky;
        RainController rain;
        ParticleSystem ps;
        Material skyTemplate, rainMaterial;
        ParticleSystem.Particle[] buffer;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            camGo = new GameObject("Test Camera");
            cam = camGo.AddComponent<Camera>();
            camGo.transform.SetPositionAndRotation(new Vector3(0f, 1.7f, 0f), Quaternion.Euler(-10f, 180f, 0f));

            skyTemplate = new Material(Shader.Find("Skybox/Day Night Gradient")) { name = "SkyTemplate" };
            skyGo = new GameObject("Sky");
            skyGo.SetActive(false);
            sky = skyGo.AddComponent<DayNightSkyController>();
            sky.skyMaterial = skyTemplate;
            sky.autoAdvance = false;
            skyGo.SetActive(true);
            SetRain(0f, 0f, 0f);

            rainMaterial = new Material(Shader.Find("DayNightSky/Rain Streak")) { name = "RainTest" };
            rainGo = new GameObject("Rain");
            rainGo.SetActive(false);
            ps = rainGo.AddComponent<ParticleSystem>();
            rain = rainGo.AddComponent<RainController>();
            rain.sky = sky;
            rain.targetCamera = cam;
            rain.material = rainMaterial;
            rainGo.SetActive(true);
            buffer = new ParticleSystem.Particle[RainController.HardParticleLimit];
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            Object.Destroy(rainGo);
            Object.Destroy(skyGo);
            Object.Destroy(camGo);
            Object.Destroy(skyTemplate);
            Object.Destroy(rainMaterial);
            yield return null;
        }

        void SetRain(float intensity, float windSpeed, float windDirection) =>
            sky.SetWeatherState(new CloudWeather(0.95f, 0.6f, 1f, windSpeed, intensity), windDirection);

        static IEnumerator Wait(float seconds)
        {
            float end = Time.time + seconds;
            while (Time.time < end) yield return null;
        }

        [UnityTest]
        public IEnumerator Rain_StartsStopsGoesIdleAndReusesTheSameSystem()
        {
            yield return null;
            Assert.IsTrue(rain.IsIdle);
            Assert.IsFalse(rainGo.GetComponent<ParticleSystemRenderer>().enabled, "Renderer off while dry");
            Assert.IsFalse(ps.isPlaying, "No particle work while dry");
            Assert.AreEqual(0, rain.AliveParticles);
            int systems = Object.FindObjectsByType<ParticleSystem>(FindObjectsSortMode.None).Length;
            int psId = ps.GetInstanceID();

            SetRain(1f, 0f, 0f);
            yield return Wait(2f);
            Assert.IsFalse(rain.IsIdle);
            Assert.Greater(rain.AliveParticles, 100);
            Assert.LessOrEqual(rain.AliveParticles, rain.Budget.maxParticles);

            // Stop: no new drops, the live ones finish and the system goes idle.
            SetRain(0f, 0f, 0f);
            int prev = rain.AliveParticles;
            float end = Time.time + rain.Budget.lifetime + 1f;
            while (!rain.IsIdle && Time.time < end)
            {
                yield return null;
                Assert.LessOrEqual(rain.AliveParticles, prev, "New drops appeared after the rain stopped");
                prev = rain.AliveParticles;
            }
            Assert.IsTrue(rain.IsIdle, "Should go idle once the last drop has landed");
            Assert.AreEqual(0, rain.AliveParticles);
            Assert.IsFalse(rainGo.GetComponent<ParticleSystemRenderer>().enabled);

            // Start again: same system, nothing created.
            SetRain(0.5f, 0f, 0f);
            yield return Wait(1f);
            Assert.Greater(rain.AliveParticles, 0);
            Assert.AreEqual(psId, rain.Particles.GetInstanceID());
            Assert.AreEqual(systems, Object.FindObjectsByType<ParticleSystem>(FindObjectsSortMode.None).Length);
        }

        [UnityTest]
        public IEnumerator EachQuality_StaysWithinItsParticleLimit()
        {
            SetRain(1f, 0.02f, 45f);
            foreach (var q in new[] { RainQuality.Low, RainQuality.Medium, RainQuality.High })
            {
                rain.SetQuality(q);
                yield return null;
                int max = 0;
                float end = Time.time + 2.5f;
                while (Time.time < end)
                {
                    yield return null;
                    max = Mathf.Max(max, rain.AliveParticles);
                    Assert.LessOrEqual(rain.AliveParticles, rain.ActiveSettings.maxParticles, $"{q} over budget");
                }
                Debug.Log($"{q}: peak {max} live drops (limit {rain.ActiveSettings.maxParticles}, steady ≈ {rain.Budget.steadyStateParticles:0})");
                Assert.Greater(max, rain.Budget.steadyStateParticles * 0.5f, $"{q} should actually rain");
            }
        }

        [UnityTest]
        public IEnumerator PauseFreezesDropsAndResumeDoesNotBurst()
        {
            SetRain(1f, 0.01f, 30f);
            yield return Wait(2f);
            sky.ClockPaused = true;   // what WeatherScheduleController.Pause does
            yield return null;
            Assert.IsTrue(rain.IsPausedByClock);
            Assert.IsTrue(ps.isPaused);
            int n = ps.GetParticles(buffer);
            var p0 = buffer[0].position;
            var pn = buffer[n - 1].position;
            for (int i = 0; i < 10; i++) yield return null;
            Assert.AreEqual(n, ps.GetParticles(buffer));
            Assert.AreEqual(p0, buffer[0].position, "Drops must hold while paused");
            Assert.AreEqual(pn, buffer[n - 1].position);

            int before = rain.AliveParticles;
            sky.ClockPaused = false;
            yield return null;
            float allowed = rain.Budget.effectiveEmission * Time.deltaTime * 2f + 3f;
            Assert.LessOrEqual(rain.AliveParticles - before, allowed, "Resume burst");
            Assert.IsFalse(ps.isPaused);
        }

        [UnityTest]
        public IEnumerator Teleport_ClearsAndRefillsAtTheNewPlaceWithoutAStreak()
        {
            SetRain(1f, 0f, 0f);
            yield return Wait(2f);
            camGo.transform.position += new Vector3(500f, 0f, 0f);
            yield return null;
            Assert.AreEqual(1, rain.TeleportCount);
            int n = ps.GetParticles(buffer);
            Assert.Greater(n, 0, "Refill expected");
            Assert.LessOrEqual(n, rain.Budget.maxParticles);
            float reach = rain.ActiveSettings.areaWidth + rain.ActiveSettings.areaDepth;
            for (int i = 0; i < n; i++)
                Assert.Less(Mathf.Abs(buffer[i].position.x - camGo.transform.position.x), reach, "A drop was left between the two places");
            for (int f = 0; f < 5; f++)
            {
                yield return null;
                n = ps.GetParticles(buffer);
                for (int i = 0; i < n; i++)
                    Assert.Less(Mathf.Abs(buffer[i].position.x - camGo.transform.position.x), reach);
            }
        }

        [UnityTest]
        public IEnumerator CameraRotationAndRoll_DoNotTiltTheRain()
        {
            SetRain(1f, 0f, 0f);
            camGo.transform.rotation = Quaternion.Euler(-35f, 70f, 45f);
            yield return Wait(0.5f);
            Assert.Greater(Vector3.Dot(rain.transform.up, Vector3.up), 0.9999f, "Emitter must stay level");
            Assert.AreEqual(new Vector3(0f, -rain.fallSpeed, 0f), rain.DropVelocity);
            Assert.AreEqual(ParticleSystemSimulationSpace.World, ps.velocityOverLifetime.space);

            camGo.transform.rotation = Quaternion.Euler(-89f, 0f, 90f);   // looking almost straight up
            yield return null;
            Assert.Greater(Vector3.Dot(rain.transform.up, Vector3.up), 0.9999f);
        }

        [UnityTest]
        public IEnumerator Wind_TiltsWithinTheLimitAndTurnsDropsWithoutJumps()
        {
            SetRain(1f, 5f, 90f);   // far stronger than any preset
            yield return Wait(1f);
            float maxH = rain.fallSpeed * Mathf.Tan(rain.maxTiltDegrees * Mathf.Deg2Rad);
            Assert.AreEqual(maxH, rain.DropVelocity.x, 1e-3f, "Strong wind must be clamped");
            Assert.AreEqual(0f, rain.DropVelocity.z, 1e-3f);

            // Turn the wind over one second; tracked drops must move continuously.
            int n = ps.GetParticles(buffer);
            var tracked = new Vector3[Mathf.Min(n, 50)];
            var seeds = new uint[tracked.Length];
            for (int i = 0; i < tracked.Length; i++) { tracked[i] = buffer[i].position; seeds[i] = buffer[i].randomSeed; }
            float speedLimit = new Vector2(maxH, rain.fallSpeed).magnitude * 1.5f;
            for (float t = 0f; t < 1f; t += Time.deltaTime)
            {
                SetRain(1f, 5f, Mathf.Lerp(90f, 270f, t));
                // Particles simulate after LateUpdate, so the step seen next frame used this frame's deltaTime.
                float simulatedDt = Time.deltaTime;
                yield return null;
                n = ps.GetParticles(buffer);
                for (int i = 0; i < n; i++)
                    for (int k = 0; k < tracked.Length; k++)
                        if (buffer[i].randomSeed == seeds[k])
                        {
                            Assert.Less((buffer[i].position - tracked[k]).magnitude, speedLimit * Mathf.Max(simulatedDt, Time.deltaTime) + 0.01f, "A drop jumped");
                            tracked[k] = buffer[i].position;
                        }
            }

            SetRain(1f, 0f, 0f);
            yield return null;
            Assert.AreEqual(0f, rain.DropVelocity.x, 1e-5f);
            Assert.AreEqual(0f, rain.DropVelocity.z, 1e-5f);
        }

        [UnityTest]
        public IEnumerator FastClock_ChangesWeatherNotDropSpeed()
        {
            SetRain(1f, 0f, 0f);
            sky.autoAdvance = true;
            sky.clockSpeed = 1000f;
            yield return Wait(0.5f);
            Assert.AreEqual(-rain.fallSpeed, rain.DropVelocity.y);
            int n = ps.GetParticles(buffer);
            var p = buffer[0].position;
            uint seed = buffer[0].randomSeed;
            float simulatedDt = Time.deltaTime;
            yield return null;
            n = ps.GetParticles(buffer);
            for (int i = 0; i < n; i++)
                if (buffer[i].randomSeed == seed)
                    Assert.Less((buffer[i].position - p).magnitude, rain.fallSpeed * Mathf.Max(simulatedDt, Time.deltaTime) * 1.5f + 0.01f);
        }

        [UnityTest]
        public IEnumerator Colour_FollowsDayAndNight()
        {
            SetRain(1f, 0f, 0f);
            var block = new MaterialPropertyBlock();
            var r = rainGo.GetComponent<ParticleSystemRenderer>();
            Color Read(float hour)
            {
                sky.TimeOfDay = hour;
                sky.Tick(0f);
                rain.UpdateRain();
                r.GetPropertyBlock(block);
                return block.GetColor("_BaseColor");
            }
            yield return null;
            var noon = Read(12f);
            var dusk = Read(18.2f);
            var night = Read(0f);
            Debug.Log($"rain colour noon {noon}, dusk {dusk}, night {night}");
            Assert.Greater(noon.grayscale, night.grayscale + 0.2f, "Night rain should be darker");
            Assert.Greater(night.grayscale, 0.15f, "Night rain must stay visible");
            Assert.AreEqual(rain.rainColor.a, noon.a, 1e-4f);
            Assert.AreEqual(rain.rainColor.a, night.a, 1e-4f);
            Assert.Greater(dusk.r - dusk.b, noon.r - noon.b, "Sunset should warm the rain slightly");
        }

        [UnityTest]
        public IEnumerator SteadyUpdate_DoesNotAllocate()
        {
            SetRain(0.8f, 0.02f, 120f);
            yield return Wait(1f);
            for (int i = 0; i < 20; i++) rain.UpdateRain();
            Assert.That(() => { for (int i = 0; i < 300; i++) rain.UpdateRain(); }, Is.Not.AllocatingGCMemory());
            SetRain(0f, 0.02f, 120f);
            Assert.That(() => { for (int i = 0; i < 300; i++) rain.UpdateRain(); }, Is.Not.AllocatingGCMemory());
        }

        [UnityTest]
        public IEnumerator EnableDisable_DoesNotLeakOrInstanceTheMaterial()
        {
            SetRain(1f, 0f, 0f);
            yield return Wait(0.5f);
            int materials = Resources.FindObjectsOfTypeAll<Material>().Length;
            int systems = Object.FindObjectsByType<ParticleSystem>(FindObjectsSortMode.None).Length;
            var r = rainGo.GetComponent<ParticleSystemRenderer>();
            for (int i = 0; i < 20; i++)
            {
                rain.enabled = false;
                yield return null;
                Assert.AreEqual(0, rain.AliveParticles, "Disabling clears the drops");
                Assert.IsFalse(r.enabled);
                rain.enabled = true;
                yield return null;
            }
            yield return Wait(0.5f);
            Assert.Greater(rain.AliveParticles, 0, "Rain resumes after re-enabling");
            Assert.AreEqual(materials, Resources.FindObjectsOfTypeAll<Material>().Length, "Materials leaked");
            Assert.AreEqual(systems, Object.FindObjectsByType<ParticleSystem>(FindObjectsSortMode.None).Length);
            Assert.AreSame(rainMaterial, r.sharedMaterial, "The shared material must not be instanced");
        }
    }
}
