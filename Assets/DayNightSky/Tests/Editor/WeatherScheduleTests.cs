using System.Collections.Generic;
using DayNightSky.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;

namespace DayNightSky.Tests
{
    /// <summary>The schedule algorithm on its own: no scene, no material.</summary>
    public class WeatherScheduleAlgorithmTests
    {
        WeatherScheduleConfig config;

        [SetUp] public void SetUp() => config = WeatherScheduleConfig.CreateDefault();
        [TearDown] public void TearDown() => Object.DestroyImmediate(config);

        static WeatherSchedule Create(WeatherScheduleConfig c, int seed)
        {
            var errors = new List<string>();
            Assert.IsTrue(WeatherSchedule.TryCreate(c, seed, errors, out var s), string.Join("\n", errors));
            return s;
        }

        /// <summary>Walks segment by segment (each query lands inside the next segment).</summary>
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

        static void AssertSame(WeatherSegment a, WeatherSegment b, string what)
        {
            Assert.AreEqual(a.index, b.index, what);
            Assert.AreEqual(a.fromPreset, b.fromPreset, what);
            Assert.AreEqual(a.toPreset, b.toPreset, what);
            Assert.AreEqual(a.transitionStartHours, b.transitionStartHours, what);
            Assert.AreEqual(a.transitionHours, b.transitionHours, what);
            Assert.AreEqual(a.holdHours, b.holdHours, what);
            Assert.AreEqual(a.toWindDirection, b.toWindDirection, what);
            Assert.AreEqual(a.toCoverageOffset, b.toCoverageOffset, what);
        }

        [Test]
        public void SameSeedAndConfig_GiveTheSameSchedule()
        {
            var a = Walk(Create(config, 42), 300);
            var b = Walk(Create(config, 42), 300);
            for (int i = 0; i < a.Count; i++) AssertSame(a[i], b[i], $"segment {i}");

            // UnityEngine.Random is neither used nor disturbed.
            Random.InitState(5);
            float expected = Random.value;
            Random.InitState(5);
            Walk(Create(config, 42), 50);
            Assert.AreEqual(expected, Random.value, "Global UnityEngine.Random state changed");
        }

        [Test]
        public void DifferentSeeds_GiveDifferentSchedules()
        {
            var sequences = new HashSet<string>();
            for (int seed = 1; seed <= 12; seed++)
            {
                var sb = new System.Text.StringBuilder();
                foreach (var seg in Walk(Create(config, seed), 15)) sb.Append(seg.toPreset).Append(':').Append(seg.holdHours.ToString("0.00")).Append(' ');
                sequences.Add(sb.ToString());
            }
            Assert.Greater(sequences.Count, 10, "Seeds should produce (almost always) different schedules");
        }

        [Test]
        public void Schedule_FollowsRulesAndDurationLimits()
        {
            var s = Create(config, 7);
            var segments = Walk(s, 3000);
            int clear = s.FindPreset("clear"), cloudy = s.FindPreset("cloudy"), overcast = s.FindPreset("overcast");
            Assert.AreEqual(clear, segments[0].toPreset, "Starts with the start preset");
            Assert.AreEqual(0.0, segments[0].transitionStartHours);

            int cloudyToClear = 0, cloudyToOvercast = 0;
            for (int i = 0; i < segments.Count; i++)
            {
                var seg = segments[i];
                var rule = System.Array.Find(config.rules, r => r.fromPresetId == s.GetPresetId(seg.toPreset));
                Assert.That(seg.holdHours, NUnit.Framework.Is.InRange(rule.minHoldHours, rule.maxHoldHours), $"hold of #{i}");
                if (i == 0) continue;

                var prev = segments[i - 1];
                Assert.AreEqual(prev.EndHours, seg.transitionStartHours, 1e-9, "Segments must be contiguous");
                Assert.AreEqual(prev.toPreset, seg.fromPreset);
                Assert.AreEqual(prev.toWindDirection, seg.fromWindDirection, "Wind must continue from the previous state");
                Assert.AreEqual(prev.toCoverageOffset, seg.fromCoverageOffset);
                Assert.That(seg.transitionHours * 60.0, NUnit.Framework.Is.InRange(config.minTransitionMinutes - 1e-3, config.maxTransitionMinutes + 1e-3));

                if (seg.fromPreset == clear) Assert.AreEqual(cloudy, seg.toPreset, "Clear -> Cloudy only");
                if (seg.fromPreset == overcast) Assert.AreEqual(cloudy, seg.toPreset, "Overcast -> Cloudy only");
                if (seg.fromPreset == cloudy)
                {
                    Assert.AreNotEqual(cloudy, seg.toPreset, "Cloudy -> Clear or Overcast");
                    if (seg.toPreset == clear) cloudyToClear++; else cloudyToOvercast++;
                }

                var p = config.presets[seg.toPreset];
                Assert.LessOrEqual(Mathf.Abs(Mathf.DeltaAngle(p.windDirection, seg.toWindDirection)), p.windDirectionJitter + 1e-3f);
                Assert.LessOrEqual(Mathf.Abs(seg.toCoverageOffset), p.coverageJitter + 1e-6f);
            }
            // Weights 1 : 0.7.
            float ratio = cloudyToOvercast / (float)cloudyToClear;
            Debug.Log($"Cloudy -> Clear {cloudyToClear}, -> Overcast {cloudyToOvercast} (ratio {ratio:0.00})");
            Assert.That(ratio, NUnit.Framework.Is.InRange(0.55f, 0.85f));
        }

        [Test]
        public void ResultAtATime_DoesNotDependOnUpdateSteps()
        {
            const double target = 523.37;
            var direct = Create(config, 99).Evaluate(target, out var directSeg, out float directProgress);

            foreach (int pattern in new[] { 0, 1, 2 })
            {
                var stepped = Create(config, 99);
                var rnd = new System.Random(pattern);
                double t = 0.0;
                while (t < target)
                {
                    double step = pattern == 0 ? 1.0 / 60.0 : pattern == 1 ? rnd.NextDouble() * 40.0 : 0.25;
                    t = System.Math.Min(target, t + step);
                    stepped.Evaluate(t);
                }
                var sample = stepped.Evaluate(target, out var seg, out float progress);
                AssertSame(directSeg, seg, $"pattern {pattern}");
                Assert.AreEqual(directProgress, progress);
                Assert.AreEqual(direct.coverage, sample.coverage);
                Assert.AreEqual(direct.windDirection, sample.windDirection);
                Assert.AreEqual(direct.overcast, sample.overcast);
            }
        }

        [Test]
        public void SeekingAcrossManySegments_LandsOnTheRightStateAndProgress()
        {
            var reference = Walk(Create(config, 3), 400);
            var target = reference[357];
            double t = target.transitionStartHours + target.transitionHours * 0.25;

            var s = Create(config, 3);
            s.Evaluate(1.0);
            var sample = s.Evaluate(t, out var seg, out float progress);  // one call, hundreds of segments later
            AssertSame(target, seg, "seek");
            Assert.AreEqual(0.25f, progress, 1e-4f);
            var from = s.GetPresetSample(target.fromPreset, target.fromCoverageOffset, target.fromWindDirection);
            var to = s.GetPresetSample(target.toPreset, target.toCoverageOffset, target.toWindDirection);
            var expected = WeatherSample.Lerp(from, to, WeatherMath.Ease(0.25f));
            Assert.AreEqual(expected.coverage, sample.coverage, 1e-6f);
            Assert.LessOrEqual(s.BufferedCount, WeatherSchedule.Capacity, "Buffer must stay bounded");

            // Seeking back before the buffer regenerates the same segments.
            AssertSame(reference[2], s.SegmentAt(reference[2].transitionStartHours + 1e-6), "seek back");
        }

        [Test]
        public void ManyDays_NeverResetAndKeepTheBufferBounded()
        {
            var s = Create(config, 11);
            long lastIndex = -1;
            double lastEnd = 0.0;
            for (double t = 0.0; t < 24.0 * 400; t += 0.37)  // 400 days in small steps
            {
                var seg = s.SegmentAt(t);
                Assert.GreaterOrEqual(seg.index, lastIndex, "The schedule went backwards");
                if (seg.index != lastIndex)
                {
                    if (lastIndex >= 0) Assert.AreEqual(lastEnd, seg.transitionStartHours, 1e-9);
                    lastIndex = seg.index;
                    lastEnd = seg.EndHours;
                }
                Assert.LessOrEqual(s.BufferedCount, WeatherSchedule.Capacity);
            }
            Assert.Greater(lastIndex, 400, "Several changes per day expected");
            Assert.Less(s.GeneratedCount, lastIndex + 20, "Each segment should be generated once");
        }

        [Test]
        public void Forecast_MatchesTheSegmentsThatRun()
        {
            var s = Create(config, 5);
            for (double t = 3.0; t < 300.0; t += 17.3)
            {
                var next1 = s.SegmentAfter(t, 1);
                var next2 = s.SegmentAfter(t, 2);
                AssertSame(next1, s.SegmentAt(next1.transitionStartHours + 1e-6), "forecast +1");
                AssertSame(next2, s.SegmentAt(next2.transitionStartHours + 1e-6), "forecast +2");
                Assert.AreEqual(s.SegmentAt(t).index + 1, next1.index);
                Assert.AreEqual(next1.EndHours, next2.transitionStartHours, 1e-9);
            }
            // Reading the forecast does not consume randomness.
            var fresh = Create(config, 5);
            AssertSame(fresh.SegmentAt(250.0), s.SegmentAt(250.0), "after many forecast reads");
        }

        [Test]
        public void AngleBlend_TakesTheShortWay()
        {
            Assert.AreEqual(0f, WeatherMath.LerpAngle(350f, 10f, 0.5f), 1e-4f);
            Assert.AreEqual(-5f, WeatherMath.LerpAngle(-10f, 10f, 0.25f), 1e-4f);
            Assert.AreEqual(180f, Mathf.Abs(WeatherMath.LerpAngle(170f, -170f, 0.5f)), 1e-4f);
            var a = new WeatherSample { windDirection = 350f };
            var b = new WeatherSample { windDirection = 10f };
            for (float t = 0f; t <= 1f; t += 0.1f)
                Assert.LessOrEqual(Mathf.Abs(Mathf.DeltaAngle(0f, WeatherSample.Lerp(a, b, t).windDirection)), 10.001f, "Went the long way round");
        }

        // ------------------------------------------------------------------------------------
        // Invalid data

        static string ErrorsFor(System.Action<WeatherScheduleConfig> breakIt)
        {
            var c = WeatherScheduleConfig.CreateDefault();
            try
            {
                breakIt(c);
                var errors = new List<string>();
                bool ok = WeatherSchedule.TryCreate(c, 1, errors, out var s);
                Assert.IsFalse(ok);
                Assert.IsNull(s);
                Assert.IsNotEmpty(errors);
                return string.Join("\n", errors);
            }
            finally
            {
                Object.DestroyImmediate(c);
            }
        }

        [Test]
        public void InvalidConfigs_AreRejectedWithClearMessages()
        {
            StringAssert.Contains("Duplicate preset ID 'cloudy'", ErrorsFor(c => c.presets[0].id = "cloudy"));
            StringAssert.Contains("'stormy' does not exist", ErrorsFor(c => c.rules[0].next[0].presetId = "stormy"));
            StringAssert.Contains("Start preset 'sunny' does not exist", ErrorsFor(c => c.startPresetId = "sunny"));
            StringAssert.Contains("hold hours", ErrorsFor(c => c.rules[1].minHoldHours = 0f));
            StringAssert.Contains("hold hours", ErrorsFor(c => { c.rules[1].minHoldHours = 5f; c.rules[1].maxHoldHours = 2f; }));
            StringAssert.Contains("Transition minutes", ErrorsFor(c => c.minTransitionMinutes = -1f));
            StringAssert.Contains("negative weight", ErrorsFor(c => c.rules[1].next[0].weight = -1f));
            StringAssert.Contains("no next state", ErrorsFor(c => { foreach (var o in c.rules[1].next) o.weight = 0f; }));
            StringAssert.Contains("has no rule", ErrorsFor(c => c.rules = new[] { c.rules[0], c.rules[1] }));
            StringAssert.Contains("more than one rule", ErrorsFor(c => c.rules = new[] { c.rules[0], c.rules[1], c.rules[2], c.rules[2] }));
            StringAssert.Contains("No presets", ErrorsFor(c => c.presets = new WeatherPresetDefinition[0]));

            var errors = new List<string>();
            Assert.IsFalse(WeatherSchedule.TryCreate(null, 1, errors, out _));
            StringAssert.Contains("No weather schedule config", errors[0]);
        }

        [Test]
        public void SingleStateLoop_StillAdvances()
        {
            // A valid but degenerate config: one state that only leads to itself. Must not hang.
            config.presets = new[] { config.presets[0] };
            config.rules = new[] { new WeatherRule("clear", WeatherScheduleConfig.MinimumHoldHours, WeatherScheduleConfig.MinimumHoldHours, new WeatherNextOption("clear", 1f)) };
            config.minTransitionMinutes = config.maxTransitionMinutes = 0f;
            var s = Create(config, 1);
            var seg = s.SegmentAt(48.0);
            Assert.Greater(seg.index, 2000);
            Assert.LessOrEqual(s.BufferedCount, WeatherSchedule.Capacity);
        }
    }

    /// <summary>The schedule running on a real sky: modes, blends, clock, single writer, allocations.</summary>
    public class WeatherScheduleIntegrationTests
    {
        GameObject go;
        DayNightSkyController sky;
        WeatherScheduleController weather;
        WeatherScheduleConfig config;
        Material originalSkybox;

        [SetUp]
        public void SetUp()
        {
            originalSkybox = RenderSettings.skybox;
            config = WeatherScheduleConfig.CreateDefault();
            go = new GameObject("Sky + Weather");
            go.SetActive(false);
            sky = go.AddComponent<DayNightSkyController>();
            sky.skyMaterial = DayNightSkyMenu.GetOrCreateMaterial();
            sky.previewCloudMotionInEditMode = true;
            weather = go.AddComponent<WeatherScheduleController>();
            weather.config = config;
            weather.manualBlendSeconds = 2f;
            go.SetActive(true);
            sky.TotalHours = 30.0;   // day 1, 06:00
            sky.Tick(0f);
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(go);
            Object.DestroyImmediate(config);
            RenderSettings.skybox = originalSkybox;
        }

        void AssertShowing(WeatherSample s, string what)
        {
            var w = sky.CurrentWeather;
            Assert.AreEqual(s.coverage, w.coverage, 1e-4f, what + " coverage");
            Assert.AreEqual(s.overcast, w.overcast, 1e-4f, what + " overcast");
            Assert.AreEqual(s.windSpeed, w.windSpeed, 1e-5f, what + " wind speed");
            Assert.AreEqual(0f, Mathf.DeltaAngle(s.windDirection, sky.WindDirection), 1e-3f, what + " wind direction");
        }

        /// <summary>Ticks in small real-time steps, failing if the shown weather jumps.</summary>
        void TickSmoothly(float seconds, float dt = 0.02f, double gameHoursPerStep = 0.0)
        {
            var prev = sky.CurrentWeather;
            float prevDir = sky.WindDirection;
            for (float t = 0f; t < seconds; t += dt)
            {
                if (gameHoursPerStep > 0.0) sky.AdvanceClock(gameHoursPerStep);
                sky.Tick(dt);
                var w = sky.CurrentWeather;
                Assert.Less(Mathf.Abs(w.coverage - prev.coverage), 0.03f, $"Coverage jumped at {t:0.00}s");
                Assert.Less(Mathf.Abs(w.overcast - prev.overcast), 0.03f, $"Overcast jumped at {t:0.00}s");
                Assert.Less(Mathf.Abs(Mathf.DeltaAngle(prevDir, sky.WindDirection)), 3f, $"Wind direction jumped at {t:0.00}s");
                prev = w;
                prevDir = sky.WindDirection;
            }
        }

        [Test]
        public void SkyHasExactlyOneWeatherWriter()
        {
            Assert.AreSame(weather, sky.WeatherDriver);
            Assert.IsFalse(sky.RegisterWeatherDriver(new DummyDriver()), "A second driver must be refused");
            Assert.AreSame(weather, sky.WeatherDriver);
        }

        sealed class DummyDriver : ISkyWeatherDriver
        {
            public void DriveWeather(DayNightSkyController s, float dt) { }
            public bool IsAnimating => false;
        }

        [Test]
        public void Automatic_ShowsTheScheduleAtTheSharedClock()
        {
            Assert.AreEqual(WeatherMode.Automatic, weather.Mode);
            foreach (double h in new[] { 30.0, 31.5, 47.99, 48.01, 200.0, 1234.5 })
            {
                sky.TotalHours = h;
                sky.Tick(0f);
                Assert.AreEqual(h, weather.ScheduleHours, 1e-4, "Schedule time must be the shared clock");
                AssertShowing(weather.Schedule.Evaluate(sky.TotalHours), $"at {h} h");
            }
        }

        [Test]
        public void Clock_CountsDaysAcrossMidnightWithoutResettingTheSchedule()
        {
            sky.TotalHours = 23.9;
            sky.Tick(0f);
            var before = weather.Schedule.SegmentAt(weather.ScheduleHours);
            sky.AdvanceClock(0.2);
            sky.Tick(0f);
            Assert.AreEqual(1, sky.Day);
            Assert.AreEqual(0.1f, sky.TimeOfDay, 1e-3f);
            Assert.AreEqual(24.1, weather.ScheduleHours, 1e-4);
            Assert.GreaterOrEqual(weather.Schedule.SegmentAt(weather.ScheduleHours).index, before.index, "Midnight must not reset the schedule");

            sky.AdvanceClock(24.0 * 3 + 5.0);
            Assert.AreEqual(4, sky.Day);
            Assert.AreEqual(5.1f, sky.TimeOfDay, 1e-3f);

            // Setting Time Of Day (the accepted preview slider) keeps the day.
            sky.TimeOfDay = 12f;
            Assert.AreEqual(4, sky.Day);
        }

        [Test]
        public void Pause_HoldsClockWeatherAndClouds()
        {
            sky.Tick(0.1f);
            weather.Pause();
            Assert.AreEqual(WeatherMode.Paused, weather.Mode);
            Assert.IsTrue(sky.ClockPaused);
            var shown = sky.CurrentWeather;
            var offset = sky.CloudOffset;
            double clock = sky.TotalHours;
            for (int i = 0; i < 50; i++) sky.Tick(0.1f);
            Assert.AreEqual(clock, sky.TotalHours);
            Assert.AreEqual(offset, sky.CloudOffset, "Cloud drift must hold while paused");
            Assert.IsTrue(shown.Approximately(sky.CurrentWeather));

            weather.Resume();
            Assert.AreEqual(WeatherMode.Automatic, weather.Mode);
            Assert.IsFalse(sky.ClockPaused);
            sky.Tick(0.1f);
            Assert.AreNotEqual(offset, sky.CloudOffset);
        }

        [Test]
        public void Override_BlendsToThePresetWhileTheScheduleWaits()
        {
            double scheduleBefore = weather.ScheduleHours;
            Assert.IsTrue(weather.ForcePreset("overcast"));
            Assert.AreEqual(WeatherMode.ManualOverride, weather.Mode);
            TickSmoothly(2.5f, 0.02f, 0.01);   // day/night keeps running during the override
            AssertShowing(weather.Schedule.GetPresetSample(weather.Schedule.FindPreset("overcast")), "forced overcast");
            Assert.AreEqual(scheduleBefore, weather.ScheduleHours, 1e-9, "The weather clock must wait");
            Assert.Greater(sky.TotalHours, 30.5, "Day/night keeps going");

            // Forcing another preset mid-blend starts from what is shown.
            weather.ForcePreset("clear");
            sky.Tick(0.5f);
            weather.ForcePreset("cloudy");
            TickSmoothly(2.5f);
            AssertShowing(weather.Schedule.GetPresetSample(weather.Schedule.FindPreset("cloudy")), "forced cloudy");
        }

        [Test]
        public void ReturnToAutomatic_BlendsBackAndKeepsTheForecast()
        {
            double waitAt = weather.ScheduleHours;
            var forecast1 = weather.Schedule.SegmentAfter(waitAt, 1);
            var forecast2 = weather.Schedule.SegmentAfter(waitAt, 2);

            weather.ForcePreset("overcast");
            TickSmoothly(3f, 0.02f, 0.05);
            double clockAtReturn = sky.TotalHours;

            weather.ReturnToAutomatic();
            Assert.IsTrue(weather.IsReturningToAutomatic);
            TickSmoothly(1f, 0.02f, 0.0);            // half of the blend; schedule still waiting
            Assert.AreEqual(waitAt, weather.ScheduleHours, 1e-9);
            TickSmoothly(1.2f, 0.02f, 0.0);
            Assert.IsFalse(weather.IsReturningToAutomatic);
            Assert.AreEqual(WeatherMode.Automatic, weather.Mode);
            AssertShowing(weather.Schedule.Evaluate(waitAt), "back on schedule");

            // The schedule continues from where it waited; the forecast is unchanged.
            sky.AdvanceClock(1.0);
            sky.Tick(0f);
            Assert.AreEqual(waitAt + 1.0, weather.ScheduleHours, 1e-6);
            Assert.AreEqual(forecast1.index, weather.Schedule.SegmentAfter(waitAt, 1).index);
            Assert.AreEqual(forecast1.toPreset, weather.Schedule.SegmentAfter(waitAt, 1).toPreset);
            Assert.AreEqual(forecast2.holdHours, weather.Schedule.SegmentAfter(waitAt, 2).holdHours);
            Assert.Greater(sky.TotalHours, clockAtReturn);
        }

        [Test]
        public void AdvanceToNextSegment_JumpsTheScheduleAndBlends()
        {
            var next = weather.Schedule.SegmentAfter(weather.ScheduleHours, 1);
            double clock = sky.TotalHours;
            weather.AdvanceToNextSegment();
            Assert.AreEqual(next.transitionStartHours, weather.ScheduleHours, 1e-6);
            Assert.AreEqual(clock, sky.TotalHours, "Advance must not move day/night");
            TickSmoothly(2.5f);
            Assert.AreEqual(next.index, weather.Schedule.SegmentAt(weather.ScheduleHours).index);
        }

        [Test]
        public void Regenerate_IsAnExplicitResetToTheSeed()
        {
            weather.ForcePreset("overcast");
            sky.Tick(5f);
            sky.TotalHours = 100.0;
            weather.useSeedOverride = true;
            weather.seedOverride = 777;
            sky.Tick(0.1f);
            Assert.AreEqual(WeatherMode.ManualOverride, weather.Mode, "Changing the seed field alone must not regenerate");

            weather.RegenerateSchedule();
            Assert.AreEqual(777, weather.Schedule.Seed);
            Assert.AreEqual(WeatherMode.Automatic, weather.Mode);
            Assert.AreEqual(100.0, weather.ScheduleHours, 1e-6);
            TickSmoothly(2.5f);
            var errors = new List<string>();
            WeatherSchedule.TryCreate(config, 777, errors, out var reference);
            AssertShowing(reference.Evaluate(100.0), "regenerated");
        }

        [Test]
        public void ScheduledTransitions_DoNotJumpAndCloudsDoNotJump()
        {
            // Run ~4 game days at 2 game minutes per tick: every transition is played through.
            float maxOffsetStep = 0f;
            var prevOffset = sky.CloudOffset;
            for (int i = 0; i < 4 * 24 * 30; i++)
            {
                var before = sky.CurrentWeather;
                sky.AdvanceClock(2.0 / 60.0);
                sky.Tick(0.02f);
                var w = sky.CurrentWeather;
                Assert.Less(Mathf.Abs(w.coverage - before.coverage), 0.06f, $"coverage jump at {sky.TotalHours:0.00} h");
                var o = sky.CloudOffset;
                for (int k = 0; k < 4; k++)
                {
                    float d = Mathf.Abs(o[k] - prevOffset[k]);
                    maxOffsetStep = Mathf.Max(maxOffsetStep, Mathf.Min(d, 1f - d));
                }
                prevOffset = o;
            }
            Assert.Less(maxOffsetStep, 0.001f, "The cloud offset must only drift, never jump");
        }

        [Test]
        public void ConfigAsset_IsNeverModified()
        {
            string before = EditorJsonUtility.ToJson(config);
            weather.ForcePreset("overcast");
            sky.Tick(1f);
            weather.ReturnToAutomatic();
            weather.AdvanceToNextSegment();
            weather.RegenerateSchedule();
            sky.AdvanceClock(500.0);
            sky.Tick(0.1f);
            Assert.AreEqual(before, EditorJsonUtility.ToJson(config));
        }

        [Test]
        public void SteadyUpdate_WithSchedule_DoesNotAllocate()
        {
            var light = new GameObject("Sun").AddComponent<Light>();
            light.type = LightType.Directional;
            sky.sunLight = light;
            try
            {
                void Step()
                {
                    sky.AdvanceClock(0.37);   // crosses many segments and midnights
                    sky.Tick(0.016f);
                }
                for (int i = 0; i < 300; i++) Step(); // warm-up (fills the ring buffer)
                Assert.That(() => { for (int i = 0; i < 2000; i++) Step(); }, Is.Not.AllocatingGCMemory());
                weather.ForcePreset("overcast");
                Assert.That(() => { for (int i = 0; i < 200; i++) Step(); }, Is.Not.AllocatingGCMemory());
            }
            finally
            {
                Object.DestroyImmediate(light.gameObject);
            }
        }

        [Test]
        public void InvalidConfig_LeavesTheSkyAloneAndReportsErrors()
        {
            var bad = WeatherScheduleConfig.CreateDefault();
            bad.rules[0].next[0].weight = -2f;
            try
            {
                var shown = sky.CurrentWeather;
                weather.config = bad;
                UnityEngine.TestTools.LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("negative weight"));
                UnityEngine.TestTools.LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("no next state"));
                weather.RegenerateSchedule();
                Assert.IsNull(weather.Schedule);
                Assert.IsNotEmpty(weather.Errors);
                for (int i = 0; i < 10; i++) sky.Tick(0.1f);
                Assert.AreEqual(shown.coverage, sky.CurrentWeather.coverage, 1e-5f);
            }
            finally
            {
                Object.DestroyImmediate(bad);
            }
        }
    }
}
