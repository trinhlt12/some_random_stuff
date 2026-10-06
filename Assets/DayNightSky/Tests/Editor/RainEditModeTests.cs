using System.Collections.Generic;
using System.Linq;
using DayNightSky.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;

namespace DayNightSky.Tests
{
    public class RainEditModeTests
    {
        WeatherScheduleConfig config;

        [SetUp] public void SetUp() => config = WeatherScheduleConfig.CreateDefault();
        [TearDown] public void TearDown() => Object.DestroyImmediate(config);

        WeatherSchedule Create(int seed)
        {
            var errors = new List<string>();
            Assert.IsTrue(WeatherSchedule.TryCreate(config, seed, errors, out var s), string.Join("\n", errors));
            return s;
        }

        static List<WeatherSegment> Walk(WeatherSchedule s, int segments)
        {
            var list = new List<WeatherSegment>();
            double t = 0.0;
            for (int i = 0; i < segments; i++)
            {
                var seg = s.SegmentAt(t);
                list.Add(seg);
                t = seg.EndHours + 1e-6;
            }
            return list;
        }

        [Test]
        public void DefaultConfig_HasRainOnlyInTheRainPreset()
        {
            Assert.AreEqual(2, config.configVersion);
            Assert.IsTrue(config.Validate(new List<string>()));
            foreach (var p in config.presets)
                if (p.id == "rain") Assert.Greater(p.rainIntensity, 0.3f);
                else Assert.AreEqual(0f, p.rainIntensity, $"{p.id} must be dry");
            var rain = config.presets.First(p => p.id == "rain");
            var overcast = config.presets.First(p => p.id == "overcast");
            Assert.GreaterOrEqual(rain.cloudCoverage, overcast.cloudCoverage);
            Assert.AreEqual(1f, rain.overcastAmount);
        }

        [Test]
        public void Schedule_RunsTheFullRainCycleAndOvercastDoesNotAlwaysRain()
        {
            var s = Create(config.seed);
            var segs = Walk(s, 4000);
            int clear = s.FindPreset("clear"), cloudy = s.FindPreset("cloudy"), overcast = s.FindPreset("overcast"), rain = s.FindPreset("rain");
            int[] cycle = { clear, cloudy, overcast, rain, overcast, cloudy, clear };
            int found = -1;
            for (int i = 0; i + cycle.Length <= segs.Count && found < 0; i++)
            {
                bool match = true;
                for (int k = 0; k < cycle.Length && match; k++) match = segs[i + k].toPreset == cycle[k];
                if (match) found = i;
            }
            Assert.GreaterOrEqual(found, 0, "Expected Clear → Cloudy → Overcast → Rain → Overcast → Cloudy → Clear somewhere");
            Debug.Log($"Full cycle starts at segment {found}, schedule hour {segs[found].transitionStartHours:0.0}");

            int overcastToRain = 0, overcastToCloudy = 0;
            for (int i = 1; i < segs.Count; i++)
            {
                if (segs[i].toPreset == rain) Assert.AreEqual(overcast, segs[i].fromPreset, "Rain only follows Overcast");
                if (segs[i].fromPreset == rain) Assert.AreEqual(overcast, segs[i].toPreset, "Rain only leads to Overcast");
                if (segs[i].fromPreset == overcast)
                {
                    if (segs[i].toPreset == rain) overcastToRain++; else overcastToCloudy++;
                }
            }
            Assert.Greater(overcastToRain, 0);
            Assert.Greater(overcastToCloudy, 0, "Overcast must not always lead to rain");
        }

        [Test]
        public void ForecastedRain_MatchesWhatRunsAndRampsSmoothly()
        {
            var s = Create(config.seed);
            int rain = s.FindPreset("rain");
            double t = 0.0;
            WeatherSegment next = default;
            for (int guard = 0; guard < 2000; guard++)
            {
                next = s.SegmentAfter(t, 1);
                if (next.toPreset == rain) break;
                t = next.transitionStartHours + 1e-6;
            }
            Assert.AreEqual(rain, next.toPreset, "No rain forecast found");

            // The forecast segment is exactly the one that runs.
            var running = s.SegmentAt(next.transitionStartHours + 1e-6);
            Assert.AreEqual(next.index, running.index);
            Assert.AreEqual(rain, running.toPreset);

            // Rain intensity rises from 0 to the preset value smoothly, without steps.
            float target = config.presets[rain].rainIntensity, prev = 0f;
            for (int i = 0; i <= 200; i++)
            {
                double h = next.transitionStartHours + next.transitionHours * i / 200.0;
                float r = s.Evaluate(h).rain;
                Assert.GreaterOrEqual(r, prev - 1e-6f, "Rain must only increase while it starts");
                Assert.Less(r - prev, 0.02f, "Rain jumped");
                prev = r;
            }
            Assert.AreEqual(target, prev, 1e-4f);
            Assert.AreEqual(target, s.Evaluate(next.TransitionEndHours + next.holdHours * 0.5).rain, 1e-4f);
        }

        [Test]
        public void ForceRainAndReturn_BlendRainIntensityThroughTheSky()
        {
            var original = RenderSettings.skybox;
            var go = new GameObject("Sky");
            go.SetActive(false);
            var sky = go.AddComponent<DayNightSkyController>();
            sky.skyMaterial = DayNightSkyMenu.GetOrCreateMaterial();
            var weather = go.AddComponent<WeatherScheduleController>();
            weather.config = config;
            weather.manualBlendSeconds = 2f;
            go.SetActive(true);
            try
            {
                sky.TotalHours = 10.0;
                sky.Tick(0f);
                float before = sky.RainIntensity;
                Assert.IsTrue(weather.ForcePreset("rain"));
                float prev = before;
                for (int i = 0; i < 125; i++)
                {
                    sky.Tick(0.02f);
                    Assert.Less(Mathf.Abs(sky.RainIntensity - prev), 0.03f, "Rain intensity jumped");
                    prev = sky.RainIntensity;
                }
                Assert.AreEqual(0.75f, sky.RainIntensity, 1e-4f);

                weather.ReturnToAutomatic();
                for (int i = 0; i < 125; i++)
                {
                    sky.Tick(0.02f);
                    Assert.Less(Mathf.Abs(sky.RainIntensity - prev), 0.03f, "Rain intensity jumped on return");
                    prev = sky.RainIntensity;
                }
                Assert.AreEqual(weather.Schedule.Evaluate(weather.ScheduleHours).rain, sky.RainIntensity, 1e-4f);
            }
            finally
            {
                Object.DestroyImmediate(go);
                RenderSettings.skybox = original;
            }
        }

        [Test]
        public void Budget_ClampsEmissionToEveryQualityLimit()
        {
            var go = new GameObject("Rain");
            go.AddComponent<ParticleSystem>();
            var rain = go.AddComponent<RainController>();
            try
            {
                foreach (var q in new[] { rain.low, rain.medium, rain.high })
                {
                    var b = RainController.ComputeBudget(q, rain.heightAboveCamera, rain.depthBelowCamera, rain.fallSpeed);
                    Debug.Log($"max {b.maxParticles}, lifetime {b.lifetime:0.00}s, emission {b.effectiveEmission:0}/s (requested {b.requestedEmission:0}), steady {b.steadyStateParticles:0}");
                    Assert.LessOrEqual(b.steadyStateParticles, b.maxParticles * RainController.BudgetHeadroom + 1e-3f);
                    Assert.IsFalse(b.clamped, "Shipped quality levels should fit their own budget");
                }

                var greedy = new RainQualitySettings(300, 5000f, 20f, 20f);
                var c = RainController.ComputeBudget(greedy, 9f, 4f, 10f);
                Assert.IsTrue(c.clamped);
                Assert.AreEqual(300 * RainController.BudgetHeadroom / c.lifetime, c.effectiveEmission, 1e-3f);
                Assert.AreEqual(1.3f, c.lifetime, 1e-4f);

                var huge = RainController.ComputeBudget(new RainQualitySettings(100000, 100f, 20f, 20f), 9f, 4f, 10f);
                Assert.AreEqual(RainController.HardParticleLimit, huge.maxParticles);

                // Quality only changes the particle system, never the weather.
                rain.SetQuality(RainQuality.Low);
                rain.ConfigureParticleSystem();
                Assert.AreEqual(rain.low.maxParticles, go.GetComponent<ParticleSystem>().main.maxParticles);
                rain.SetQuality(RainQuality.High);
                rain.ConfigureParticleSystem();
                Assert.AreEqual(rain.high.maxParticles, go.GetComponent<ParticleSystem>().main.maxParticles);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void ParticleSetup_IsCheapAndWorldSpace()
        {
            var go = new GameObject("Rain");
            var ps = go.AddComponent<ParticleSystem>();
            var rain = go.AddComponent<RainController>();
            rain.material = RainAssets.GetOrCreateMaterial();
            try
            {
                rain.ConfigureParticleSystem();
                var r = go.GetComponent<ParticleSystemRenderer>();
                Assert.AreEqual(ParticleSystemSimulationSpace.World, ps.main.simulationSpace);
                Assert.AreEqual(ParticleSystemSimulationSpace.World, ps.velocityOverLifetime.space);
                Assert.IsTrue(ps.velocityOverLifetime.enabled);
                Assert.AreEqual(0f, ps.main.startSpeed.constant, "Velocity comes from one module only");
                Assert.AreEqual(0f, ps.main.gravityModifier.constant);
                Assert.IsFalse(ps.collision.enabled);
                Assert.IsFalse(ps.trails.enabled);
                Assert.IsFalse(ps.lights.enabled);
                Assert.IsFalse(ps.noise.enabled);
                Assert.IsFalse(ps.inheritVelocity.enabled);
                Assert.IsFalse(ps.main.playOnAwake);
                Assert.AreEqual(ParticleSystemRenderMode.Stretch, r.renderMode);
                Assert.AreEqual(UnityEngine.Rendering.ShadowCastingMode.Off, r.shadowCastingMode);
                Assert.IsFalse(r.receiveShadows);
                Assert.AreSame(rain.material, r.sharedMaterial);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void RainShader_CompilesForMobileAndDesktopApis()
        {
            var shader = Shader.Find(RainAssets.ShaderName);
            Assert.IsNotNull(shader);
            Assert.IsFalse(ShaderUtil.ShaderHasError(shader));
            Assert.AreEqual(1, shader.passCount);
            var pass = ShaderUtil.GetShaderData(shader).GetSubshader(0).GetPass(0);
            var targets = new[]
            {
                (ShaderCompilerPlatform.Vulkan, BuildTarget.Android),
                (ShaderCompilerPlatform.GLES3x, BuildTarget.Android),
                (ShaderCompilerPlatform.Metal, BuildTarget.iOS),
                (ShaderCompilerPlatform.D3D, BuildTarget.StandaloneWindows64),
            };
            foreach (var (platform, target) in targets)
                foreach (var stage in new[] { ShaderType.Vertex, ShaderType.Fragment })
                {
                    var info = pass.CompileVariant(stage, new string[0], platform, target);
                    string messages = string.Join("\n", info.Messages.Select(m => m.message));
                    Assert.IsTrue(info.Success, $"{platform} {stage}: {messages}");
                }
        }

        [Test]
        public void SamplePrefab_IsReadyToUse()
        {
            var prefab = RainAssets.GetOrCreatePrefab();
            Assert.IsNotNull(prefab);
            var rain = prefab.GetComponent<RainController>();
            Assert.IsNotNull(rain);
            Assert.AreSame(RainAssets.GetOrCreateMaterial(), rain.material);
            Assert.AreSame(rain.material, prefab.GetComponent<ParticleSystemRenderer>().sharedMaterial);
            Assert.AreEqual(1, prefab.GetComponentsInChildren<ParticleSystem>(true).Length, "One particle system only");
        }
    }
}
