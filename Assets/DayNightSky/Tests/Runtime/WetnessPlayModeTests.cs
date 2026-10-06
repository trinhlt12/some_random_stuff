using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace DayNightSky.Tests
{
    public class WetnessPlayModeTests
    {
        static readonly int GlobalId = Shader.PropertyToID(WetnessController.GlobalProperty);
        readonly List<Object> created = new List<Object>();

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            foreach (var o in created) if (o != null) Object.Destroy(o);
            created.Clear();
            yield return null;
            Assert.AreEqual(0f, Shader.GetGlobalFloat(GlobalId), "Destroying the controller must leave surfaces dry");
        }

        (DayNightSkyController sky, WetnessController wet) Create(bool withSchedule, out WeatherScheduleController weather)
        {
            var template = new Material(Shader.Find("Skybox/Day Night Gradient"));
            created.Add(template);
            var go = new GameObject("Sky");
            created.Add(go);
            go.SetActive(false);
            var sky = go.AddComponent<DayNightSkyController>();
            sky.skyMaterial = template;
            weather = null;
            if (withSchedule)
            {
                var config = WeatherScheduleConfig.CreateDefault();
                created.Add(config);
                weather = go.AddComponent<WeatherScheduleController>();
                weather.config = config;
            }
            var wet = go.AddComponent<WetnessController>();
            wet.sky = sky;
            wet.recomputeOnStart = false;
            go.SetActive(true);
            return (sky, wet);
        }

        [UnityTest]
        public IEnumerator PauseHoldsWetnessAndResumeContinues()
        {
            var (sky, wet) = Create(true, out var weather);
            sky.dayLengthSeconds = 24f;     // 1 game hour per real second
            weather.manualBlendSeconds = 0.5f;
            weather.ForcePreset("rain");
            float end = Time.time + 1.5f;
            while (Time.time < end) yield return null;
            Assert.Greater(wet.Wetness, 0.3f, "Should be getting wet in the rain");

            weather.Pause();
            yield return null;
            float held = wet.Wetness;
            double clock = sky.TotalHours;
            for (int i = 0; i < 30; i++) yield return null;
            Assert.AreEqual(held, wet.Wetness, "Pause must hold wetness");
            Assert.AreEqual(clock, sky.TotalHours);
            Assert.AreEqual(held, Shader.GetGlobalFloat(GlobalId), 1e-5f);

            weather.Resume();
            end = Time.time + 0.5f;
            while (Time.time < end) yield return null;
            Assert.AreNotEqual(held, wet.Wetness, "Wetness continues after Resume");
            Assert.Less(Mathf.Abs(wet.Wetness - held), 0.3f, "No jump on Resume");
        }

        [UnityTest]
        public IEnumerator RainQuality_DoesNotChangeWetness()
        {
            var results = new List<float>[2] { new List<float>(), new List<float>() };
            var qualities = new[] { RainQuality.Low, RainQuality.High };
            for (int run = 0; run < 2; run++)
            {
                var (sky, wet) = Create(false, out _);
                sky.autoAdvance = false;
                var cam = new GameObject("Cam").AddComponent<Camera>();
                created.Add(cam.gameObject);
                var rainGo = new GameObject("Rain");
                created.Add(rainGo);
                rainGo.SetActive(false);
                rainGo.AddComponent<ParticleSystem>();
                var rain = rainGo.AddComponent<RainController>();
                rain.sky = sky;
                rain.targetCamera = cam;
                rain.quality = qualities[run];
                rainGo.SetActive(true);

                sky.TotalHours = 30.0;
                sky.SetWeatherState(new CloudWeather(0.95f, 0.6f, 1f, 0.02f, 0.75f), 120f);
                wet.ResetDry();   // start dry at this time, same for both runs
                yield return null;
                for (int frame = 0; frame < 60; frame++)
                {
                    if (frame == 30) sky.SetWeatherState(new CloudWeather(0.9f, 0.6f, 1f, 0.016f, 0f), 110f);   // rain stops
                    sky.AdvanceClock(0.02);
                    yield return null;
                    results[run].Add(wet.Wetness);
                }
                Debug.Log($"{qualities[run]}: {rain.AliveParticles} drops alive, final wetness {wet.Wetness:0.0000}");
                foreach (var o in created) if (o != null) Object.Destroy(o);
                created.Clear();
                yield return null;
            }
            Assert.AreEqual(results[0].Count, results[1].Count);
            for (int i = 0; i < results[0].Count; i++)
                Assert.AreEqual(results[0][i], results[1][i], 1e-6f, $"frame {i}: rain quality changed the wetness");
            Assert.Greater(results[0][29], results[0][0], "Wet during rain");
            Assert.Less(results[0][59], results[0][30], "Drying after rain");
        }

        [UnityTest]
        public IEnumerator FastClock_IntegratesFromTheScheduleAndStaysInRange()
        {
            var (sky, wet) = Create(true, out _);
            sky.dayLengthSeconds = 24f;
            sky.clockSpeed = 600f;          // ~10 game hours per real second
            float end = Time.time + 2f;
            bool jumped = false;
            while (Time.time < end)
            {
                yield return null;
                Assert.That(wet.Wetness, Is.InRange(0f, 1f));
                if (wet.LastEvent == WetnessEvent.Jumped) jumped = true;
            }
            Assert.IsTrue(jumped, "Large clock steps should be integrated from the schedule");
            Assert.AreEqual(wet.DisplayedWetness, Shader.GetGlobalFloat(GlobalId), 1e-5f);
        }
    }
}
