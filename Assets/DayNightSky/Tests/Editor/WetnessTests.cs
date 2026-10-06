using System.Collections.Generic;
using System.Linq;
using DayNightSky.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;

namespace DayNightSky.Tests
{
    /// <summary>The wetness model on its own (no scene).</summary>
    public class WetnessModelTests
    {
        static readonly WetnessSettings Defaults = new WetnessSettings();
        static WetnessConditions Rain(float r) => new WetnessConditions(r, 1f, 0.02f, 0.3f);
        static WetnessConditions Dry => new WetnessConditions(0f, 0.2f, 0.01f, 0.6f);

        static float Run(float w, double hours, double step, System.Func<double, WetnessConditions> at)
        {
            int n = Mathf.Max(1, (int)System.Math.Round(hours / step));
            double h = hours / n;
            for (int i = 0; i < n; i++) w = WetnessModel.Step(w, h, Defaults, at((i + 0.5) * h));
            return w;
        }

        [Test]
        public void ConstantRain_IsIndependentOfStepSize()
        {
            // Exact solution per step: any step size gives the same value (only float rounding differs).
            float reference = WetnessModel.Step(0.1f, 3.0, Defaults, Rain(0.7f));
            foreach (double step in new[] { 1.0 / 600.0, 0.01, 0.1, 0.5, 1.5 })
            {
                float w = Run(0.1f, 3.0, step, _ => Rain(0.7f));
                Assert.AreEqual(reference, w, 1e-4f, $"step {step} h");
            }
        }

        [Test]
        public void RampingRain_GivesCloseResultsForDifferentStepSizes()
        {
            // Rain eases from 0 to 0.75 over 1.5 h (like a scheduled transition), holds 1 h, then stops for 2 h.
            WetnessConditions At(double t)
            {
                float r = t < 1.5 ? WeatherMath.Ease((float)(t / 1.5)) * 0.75f : t < 2.5 ? 0.75f : 0f;
                return new WetnessConditions(r, 1f, 0.02f, 0.2f);
            }
            float fine = Run(0f, 4.5, 1.0 / 3600.0, At);
            foreach (double step in new[] { 1.0 / 600.0, 1.0 / 60.0, 0.05, 0.25 })
            {
                float w = Run(0f, 4.5, step, At);
                Debug.Log($"step {step:0.0000} h: W = {w:0.00000} (fine {fine:0.00000})");
                Assert.AreEqual(fine, w, 0.005f, $"step {step} h (tolerance 0.005)");
            }
        }

        [Test]
        public void Wetness_StaysInRange_EvenWithInvalidInput()
        {
            var bad = new WetnessSettings { wettingRatePerHour = -3f, dryingRatePerHour = float.NaN, windReference = 0f };
            var warnings = new List<string>();
            Assert.IsFalse(bad.Validate(warnings));
            Assert.IsTrue(warnings.Any(w => w.Contains("wettingRatePerHour")));
            Assert.IsTrue(warnings.Any(w => w.Contains("dryingRatePerHour")));
            Assert.IsTrue(warnings.Any(w => w.Contains("windReference")));

            var odd = new[]
            {
                new WetnessConditions(5f, -1f, -2f, 3f),
                new WetnessConditions(float.NaN, float.NaN, float.NaN, float.NaN),
                new WetnessConditions(-1f, 2f, 100f, -1f),
            };
            float w = 0.5f;
            foreach (var settings in new[] { Defaults, bad })
                foreach (var c in odd)
                    foreach (double h in new[] { -1.0, 0.0, 0.01, 1.0, 1e6, double.NaN })
                    {
                        w = WetnessModel.Step(w, h, settings, c);
                        Assert.That(w, NUnit.Framework.Is.InRange(0f, 1f));
                        Assert.IsFalse(float.IsNaN(w));
                    }
            Assert.AreEqual(0.4f, WetnessModel.Step(0.4f, 0.0, Defaults, Dry), "Zero time changes nothing");
            Assert.AreEqual(0.4f, WetnessModel.Step(0.4f, -2.0, Defaults, Dry), "Negative time changes nothing");
            Assert.AreEqual(1f, WetnessModel.Step(1.5f, 0.0, Defaults, Dry), "Out-of-range wetness is clamped");
        }

        [Test]
        public void LightAndHeavyRain_DifferAndLightRainStaysBelowOne()
        {
            float light = WetnessModel.SteadyState(Defaults, Rain(0.2f));
            float rain = WetnessModel.SteadyState(Defaults, Rain(0.75f));
            float heavy = WetnessModel.SteadyState(Defaults, Rain(1f));
            Debug.Log($"steady wetness: light {light:0.00}, rain {rain:0.00}, heavy {heavy:0.00}");
            Assert.Less(light, rain - 0.1f);
            Assert.Less(rain, heavy);
            Assert.Less(heavy, 1f);
            Assert.Less(light, 0.7f, "Light rain should not soak everything");

            // Rising under rain, never above 1.
            float w = 0f, prev = 0f;
            for (int i = 0; i < 200; i++)
            {
                w = WetnessModel.Step(w, 0.05, Defaults, Rain(1f));
                Assert.GreaterOrEqual(w, prev);
                Assert.LessOrEqual(w, 1f);
                prev = w;
            }
        }

        [Test]
        public void AfterRain_SurfacesStayWetForAWhileThenDry()
        {
            float w = 0.8f;
            float after10min = WetnessModel.Step(w, 10.0 / 60.0, Defaults, Dry);
            Assert.Greater(after10min, 0.6f, "Must not dry instantly");
            float prev = w;
            for (int i = 0; i < 120; i++)
            {
                float next = WetnessModel.Step(prev, 0.1, Defaults, Dry);
                Assert.LessOrEqual(next, prev, "Drying must only decrease");
                prev = next;
            }
            Assert.Less(prev, 0.01f, "Dry after 12 game hours");

            // Sun and wind dry faster; overcast night slower.
            var night = new WetnessConditions(0f, 1f, 0f, -0.5f);
            var sunnyWindy = new WetnessConditions(0f, 0f, 0.03f, 0.9f);
            Assert.Greater(WetnessModel.DryingRate(Defaults, sunnyWindy), WetnessModel.DryingRate(Defaults, night) * 2f);
            Assert.AreEqual(Defaults.dryingRatePerHour, WetnessModel.DryingRate(Defaults, night), 1e-6f);
        }
    }

    /// <summary>Wetness on a real sky + schedule (Edit Mode, clock moved by hand).</summary>
    public class WetnessIntegrationTests
    {
        GameObject go;
        DayNightSkyController sky;
        WeatherScheduleController weather;
        WetnessController wet;
        WeatherScheduleConfig config;
        Material originalSkybox;
        static readonly int GlobalId = Shader.PropertyToID(WetnessController.GlobalProperty);

        [SetUp]
        public void SetUp()
        {
            originalSkybox = RenderSettings.skybox;
            config = WeatherScheduleConfig.CreateDefault();
            go = new GameObject("Sky + Weather + Wetness");
            go.SetActive(false);
            sky = go.AddComponent<DayNightSkyController>();
            sky.skyMaterial = DayNightSkyMenu.GetOrCreateMaterial();
            weather = go.AddComponent<WeatherScheduleController>();
            weather.config = config;
            wet = go.AddComponent<WetnessController>();
            wet.sky = sky;
            go.SetActive(true);
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(go);
            Object.DestroyImmediate(config);
            RenderSettings.skybox = originalSkybox;
        }

        void SetClock(double hours)
        {
            sky.TotalHours = hours;
            sky.Tick(0f);
        }

        void StepClock(double hours)
        {
            sky.AdvanceClock(hours);
            sky.Tick(0f);
            wet.Simulate();
        }

        /// <summary>A schedule span that goes from dry, through a rain segment, to dry again.</summary>
        (double start, double end, WeatherSegment rain) RainSpan()
        {
            var s = weather.Schedule;
            int rain = s.FindPreset("rain");
            double t = 0.0;
            for (int guard = 0; guard < 3000; guard++)
            {
                var seg = s.SegmentAt(t);
                if (seg.toPreset == rain && seg.transitionStartHours > 6.0)
                    return (seg.transitionStartHours - 3.0, seg.EndHours + 4.0, seg);
                t = seg.EndHours + 1e-6;
            }
            Assert.Fail("No rain in the schedule");
            return default;
        }

        [Test]
        public void RainThenDry_NormalRunAndFastForwardAgree()
        {
            var (start, end, rainSeg) = RainSpan();

            // Normal run: small frames (1.2 game minutes) following the displayed weather.
            SetClock(start);
            wet.Simulate();
            wet.ResetDry();
            float peak = 0f, prev = 0f;
            bool rose = false, fell = false;
            while (sky.TotalHours < end - 1e-9)
            {
                StepClock(System.Math.Min(0.02, end - sky.TotalHours));
                peak = Mathf.Max(peak, wet.Wetness);
                if (wet.Wetness > prev + 1e-6f) rose = true;
                if (sky.TotalHours > rainSeg.EndHours + 0.5 && wet.Wetness < prev - 1e-6f) fell = true;
                Assert.That(wet.Wetness, NUnit.Framework.Is.InRange(0f, 1f));
                prev = wet.Wetness;
            }
            float normal = wet.Wetness;

            // Fast-forward: one jump over the whole span, integrated from the schedule.
            SetClock(start);
            wet.Simulate();
            wet.ResetDry();
            SetClock(end);
            wet.Simulate();
            Assert.AreEqual(WetnessEvent.Jumped, wet.LastEvent);
            float jumped = wet.Wetness;

            Debug.Log($"rain {rainSeg.transitionStartHours:0.0}-{rainSeg.EndHours:0.0} h: peak {peak:0.000}, normal end {normal:0.0000}, fast-forward end {jumped:0.0000}");
            Assert.IsTrue(rose, "Wetness should rise in rain");
            Assert.IsTrue(fell, "Wetness should fall after rain");
            Assert.Greater(peak, 0.5f);
            Assert.AreEqual(normal, jumped, 0.01f, "Normal and fast-forward runs must agree (tolerance 0.01)");
        }

        [Test]
        public void Rewind_FollowsThePolicyExplicitly()
        {
            var (start, end, _) = RainSpan();
            double mid = 0.5 * (start + end);
            SetClock(end);
            wet.Simulate();
            wet.Recompute();
            float atEnd = wet.Wetness;

            wet.rewindPolicy = WetnessRewindPolicy.KeepCurrent;
            SetClock(mid);
            wet.Simulate();
            Assert.AreEqual(WetnessEvent.Rewound, wet.LastEvent);
            Assert.AreEqual(atEnd, wet.Wetness, "Keep Current keeps the value");

            wet.rewindPolicy = WetnessRewindPolicy.ResetDry;
            SetClock(mid - 1.0);
            wet.Simulate();
            Assert.AreEqual(0f, wet.Wetness);

            wet.rewindPolicy = WetnessRewindPolicy.RecomputeFromSchedule;
            SetClock(end);
            wet.Simulate();
            SetClock(mid);
            wet.Simulate();
            Assert.AreEqual(WetnessEvent.Recomputed, wet.LastEvent);
            float recomputed = wet.Wetness;
            wet.ResetDry();
            wet.Recompute();
            Assert.AreEqual(recomputed, wet.Wetness, 1e-6f, "Recompute must be repeatable");
        }

        [Test]
        public void Recompute_IsUnavailableAndSaysSoDuringOverride()
        {
            weather.ForcePreset("rain");
            wet.SetFullyWet();
            Assert.IsFalse(wet.Recompute());
            Assert.AreEqual(WetnessEvent.RecomputeUnavailable, wet.LastEvent);
            Assert.AreEqual(1f, wet.Wetness, "Value unchanged when recompute is impossible");

            // A jump without a running schedule uses the weather shown now and is flagged.
            SetClock(sky.TotalHours);
            wet.Simulate();
            StepClock(3.0);
            Assert.AreEqual(WetnessEvent.JumpedApproximate, wet.LastEvent);
        }

        [Test]
        public void ManualPreview_StopsTheSimulationAndReturnContinuesFromIt()
        {
            weather.ForcePreset("rain");
            sky.Tick(10f);              // blend finished: raining
            SetClock(sky.TotalHours);
            wet.Simulate();
            wet.ResetDry();

            wet.SetManualPreview(0.8f);
            Assert.AreEqual(0.8f, Shader.GetGlobalFloat(GlobalId), 1e-6f);
            for (int i = 0; i < 20; i++) StepClock(0.05);
            Assert.AreEqual(0f, wet.Wetness, "Simulation must stop during preview");
            Assert.AreEqual(0.8f, wet.DisplayedWetness);
            Assert.AreEqual(0.8f, Shader.GetGlobalFloat(GlobalId), 1e-6f);

            wet.ReturnToSimulation();
            Assert.AreEqual(0.8f, wet.Wetness, "Return continues from the shown value");
            StepClock(1.0 / 60.0);
            Assert.AreEqual(0.8f, wet.Wetness, 0.02f, "No jump after returning");
            Assert.AreEqual(wet.Wetness, Shader.GetGlobalFloat(GlobalId), 1e-5f);
        }

        [Test]
        public void Controls_DoNotTouchTheScheduleOrForecast()
        {
            SetClock(50.0);
            double scheduleHours = weather.ScheduleHours;
            var next = weather.Schedule.SegmentAfter(scheduleHours, 1);
            wet.SetFullyWet();
            wet.SetManualPreview(0.3f);
            wet.ReturnToSimulation();
            wet.ResetDry();
            wet.Recompute();
            Assert.AreEqual(scheduleHours, weather.ScheduleHours);
            Assert.AreEqual(WeatherMode.Automatic, weather.Mode);
            var after = weather.Schedule.SegmentAfter(scheduleHours, 1);
            Assert.AreEqual(next.index, after.index);
            Assert.AreEqual(next.holdHours, after.holdHours);
        }

        [Test]
        public void GlobalIsClearedWhenDisabledOrDestroyed()
        {
            wet.SetFullyWet();
            Assert.AreEqual(1f, Shader.GetGlobalFloat(GlobalId));
            wet.enabled = false;
            Assert.AreEqual(0f, Shader.GetGlobalFloat(GlobalId), "Disabling must leave surfaces dry");
            wet.enabled = true;
            wet.SetFullyWet();
            Object.DestroyImmediate(wet);
            Assert.AreEqual(0f, Shader.GetGlobalFloat(GlobalId), "Destroying must leave surfaces dry");
        }

        [Test]
        public void SteadyUpdates_DoNotAllocate()
        {
            SetClock(100.0);
            wet.Simulate();
            void Small() { StepClock(0.01); }
            void Large() { StepClock(2.0); }   // fast-forward frames, integrated from the schedule
            for (int i = 0; i < 50; i++) { Small(); Large(); }
            Assert.That(() => { for (int i = 0; i < 300; i++) Small(); }, Is.Not.AllocatingGCMemory());
            Assert.That(() => { for (int i = 0; i < 50; i++) Large(); }, Is.Not.AllocatingGCMemory());
        }

        [Test]
        public void WetLitShader_CompilesForMobileAndDesktopApis()
        {
            var shader = Shader.Find(WetnessDemoBuilder.LitShader);
            Assert.IsNotNull(shader);
            Assert.IsFalse(ShaderUtil.ShaderHasError(shader));
            var targets = new[]
            {
                (ShaderCompilerPlatform.Vulkan, BuildTarget.Android),
                (ShaderCompilerPlatform.GLES3x, BuildTarget.Android),
                (ShaderCompilerPlatform.Metal, BuildTarget.iOS),
                (ShaderCompilerPlatform.D3D, BuildTarget.StandaloneWindows64),
            };
            var data = ShaderUtil.GetShaderData(shader).GetSubshader(0);
            for (int p = 0; p < data.PassCount; p++)
                foreach (var (platform, target) in targets)
                    foreach (var keywords in new[] { new string[0], new[] { "_MAIN_LIGHT_SHADOWS_CASCADE", "_SHADOWS_SOFT", "FOG_EXP2" } })
                        foreach (var stage in new[] { ShaderType.Vertex, ShaderType.Fragment })
                        {
                            var info = data.GetPass(p).CompileVariant(stage, keywords, platform, target);
                            string messages = string.Join("\n", info.Messages.Select(m => m.message));
                            Assert.IsTrue(info.Success, $"pass {p} {platform} {stage} [{string.Join(",", keywords)}]: {messages}");
                        }

            var pastel = Shader.Find("Pastel/Low Poly Pastel");
            Assert.IsNotNull(pastel);
            Assert.IsFalse(ShaderUtil.ShaderHasError(pastel), "Pastel shader must still compile");
            var pastelMat = new Material(pastel);
            Assert.AreEqual(0f, pastelMat.GetFloat("_WetExposure"), "Pastel wetness must be off by default");
            Object.DestroyImmediate(pastelMat);
        }
    }
}
