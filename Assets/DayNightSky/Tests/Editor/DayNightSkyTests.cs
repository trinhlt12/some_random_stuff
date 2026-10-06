using System.Collections;
using System.Linq;
using DayNightSky.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;

namespace DayNightSky.Tests
{
    public class DayNightSkyTests
    {
        static readonly float[] Checkpoints = { 0f, 5f, 6f, 12f, 18f, 19f, 23f + 59f / 60f };

        GameObject go;
        DayNightSkyController sky;
        Material originalSkybox;
        Material placeholderSkybox;

        [SetUp]
        public void SetUp()
        {
            originalSkybox = RenderSettings.skybox;
            placeholderSkybox = new Material(Shader.Find("Skybox/Procedural")) { name = "Placeholder" };
            RenderSettings.skybox = placeholderSkybox;
            go = new GameObject("Sky Test");
            sky = go.AddComponent<DayNightSkyController>();
            sky.skyMaterial = DayNightSkyMenu.GetOrCreateMaterial();
            sky.Tick(0f);
        }

        [TearDown]
        public void TearDown()
        {
            if (go != null) Object.DestroyImmediate(go);
            RenderSettings.skybox = originalSkybox;
            Object.DestroyImmediate(placeholderSkybox);
        }

        static float Diff(Color a, Color b) => Mathf.Abs(a.r - b.r) + Mathf.Abs(a.g - b.g) + Mathf.Abs(a.b - b.b) + Mathf.Abs(a.a - b.a);

        [Test]
        public void Shader_CompilesForMobileAndDesktopApis()
        {
            var shader = Shader.Find(DayNightSkyMenu.ShaderName);
            Assert.IsNotNull(shader);
            Assert.IsFalse(ShaderUtil.ShaderHasError(shader));
            Assert.AreEqual(1, shader.passCount, "Sky must stay a single pass");

            var pass = ShaderUtil.GetShaderData(shader).GetSubshader(0).GetPass(0);
            var targets = new[]
            {
                (ShaderCompilerPlatform.Vulkan, BuildTarget.Android),
                (ShaderCompilerPlatform.GLES3x, BuildTarget.Android),
                (ShaderCompilerPlatform.Metal, BuildTarget.iOS),
                (ShaderCompilerPlatform.D3D, BuildTarget.StandaloneWindows64),
            };
            foreach (var (platform, target) in targets)
                foreach (var keywords in new[] { new string[0], new[] { "_DAYNIGHTSKY_DITHER" } })
                    foreach (var stage in new[] { ShaderType.Vertex, ShaderType.Fragment })
                    {
                        var info = pass.CompileVariant(stage, keywords, platform, target);
                        string messages = string.Join("\n", info.Messages.Select(m => m.message));
                        Debug.Log($"Compile {platform} {stage} [{string.Join(",", keywords)}]: {info.Success} {messages}");
                        Assert.IsTrue(info.Success, $"{platform} {stage} [{string.Join(",", keywords)}]: {messages}");
                    }
        }

        [Test]
        public void Checkpoints_SunAndColoursAreSensible()
        {
            sky.sunPathAzimuth = 0f;
            sky.noonElevation = 60f;
            foreach (float t in Checkpoints)
            {
                var s = sky.Evaluate(t);
                Debug.Log($"{t:00.00}h sun={s.sunDirection} phase={s.phase:0.000} zenith={s.zenith} horizon={s.horizon} glowA={s.glow.a:0.00} sunVis={s.sunVisibility:0.00} light={s.lightIntensity:0.00}");
            }

            var dawn = sky.Evaluate(6f);
            Assert.AreEqual(0f, dawn.sunDirection.y, 1e-4f, "Sun must be on the horizon at 06:00");
            Assert.Greater(dawn.sunDirection.x, 0.99f, "Sun rises in +X (east) with azimuth 0");
            var dusk = sky.Evaluate(18f);
            Assert.AreEqual(0f, dusk.sunDirection.y, 1e-4f, "Sun must be on the horizon at 18:00");
            Assert.Less(dusk.sunDirection.x, -0.99f, "Sun sets in -X (west), the opposite side");
            Assert.AreEqual(Mathf.Sin(60f * Mathf.Deg2Rad), sky.Evaluate(12f).sunDirection.y, 1e-4f);

            foreach (float night in new[] { 0f, 5f, 19f, 23f + 59f / 60f })
            {
                var s = sky.Evaluate(night);
                Assert.Less(s.sunDirection.y, 0f, $"{night}h: sun should be below the horizon");
                Assert.AreEqual(0f, s.sunVisibility, $"{night}h: sun disc must be hidden");
                Assert.AreEqual(0f, s.lightIntensity, $"{night}h: light must be off");
                // Night keeps a readable gradient, not black.
                Assert.Greater(s.zenith.maxColorComponent, 0.15f);
                Assert.Greater(s.horizon.maxColorComponent, 0.2f);
            }

            Assert.Greater(dawn.glow.a, 0.5f, "Warm glow expected at sunrise");
            Assert.Greater(dusk.glow.a, 0.5f, "Warm glow expected at sunset");
            Assert.AreEqual(0f, sky.Evaluate(12f).glow.a, 1e-3f, "No sunset glow at noon");
            Assert.Greater(dawn.horizon.r, dawn.horizon.b, "Horizon should be warm at sunrise");
            Assert.Greater(sky.Evaluate(12f).sunVisibility, 0.99f);
        }

        [Test]
        public void WholeDay_IsContinuousIncludingMidnight()
        {
            float worstColour = 0f, worstSun = 0f;
            var prev = sky.Evaluate(0f);
            for (int minute = 1; minute <= 24 * 60; minute++)
            {
                var s = sky.Evaluate(minute / 60f); // the last step is 24:00 == 00:00
                worstColour = Mathf.Max(worstColour, Diff(prev.zenith, s.zenith), Diff(prev.horizon, s.horizon), Diff(prev.glow, s.glow));
                worstSun = Mathf.Max(worstSun, (prev.sunDirection - s.sunDirection).magnitude);
                prev = s;
            }
            Debug.Log($"Largest change per minute: colour {worstColour:0.0000}, sun direction {worstSun:0.0000}");
            Assert.Less(worstColour, 0.05f);
            Assert.Less(worstSun, 0.01f);

            var a = sky.Evaluate(23.999f);
            var b = sky.Evaluate(0f);
            Assert.Less(Diff(a.zenith, b.zenith) + Diff(a.horizon, b.horizon), 0.01f, "Jump at midnight");
            Assert.AreEqual(sky.Evaluate(24f).sunDirection, sky.Evaluate(0f).sunDirection);
        }

        [Test]
        public void SunPathAzimuth_RotatesAroundWorldUp()
        {
            sky.sunPathAzimuth = 90f;
            var s = sky.Evaluate(6f);
            Assert.AreEqual(0f, s.sunDirection.y, 1e-4f);
            Assert.AreEqual(-1f, s.sunDirection.z, 1e-3f, "Euler(0, 90, 0) turns +X into -Z");
        }

        [Test]
        public void Update_DoesNotAllocate()
        {
            var light = new GameObject("Sun").AddComponent<Light>();
            light.type = LightType.Directional;
            sky.sunLight = light;
            sky.previewCloudMotionInEditMode = true;
            try
            {
                float t = 0f;
                int step = 0;
                void Step()
                {
                    t += 0.37f;
                    sky.TimeOfDay = t;
                    if (++step % 50 == 0) sky.TransitionToPreset((CloudPreset)(step / 50 % 3), 0.5f);
                    sky.Tick(0.016f);
                }
                for (int i = 0; i < 60; i++) Step(); // warm-up
                Assert.That(() => { for (int i = 0; i < 200; i++) Step(); }, Is.Not.AllocatingGCMemory());
                // Steady state: only the wind moves.
                Assert.That(() => { for (int i = 0; i < 200; i++) sky.Tick(0.016f); }, Is.Not.AllocatingGCMemory());
            }
            finally
            {
                Object.DestroyImmediate(light.gameObject);
            }
        }

        static int LiveInstances() => Resources.FindObjectsOfTypeAll<Material>()
            .Count(m => m.name.EndsWith("(Instance)") && m.shader != null && m.shader.name == DayNightSkyMenu.ShaderName && !EditorUtility.IsPersistent(m));

        [Test]
        public void EnableDisable_DoesNotLeakAndRestoresSkybox()
        {
            Assert.AreSame(sky.MaterialInstance, RenderSettings.skybox);
            Assert.AreEqual(1, LiveInstances());
            Assert.AreNotSame(sky.skyMaterial, RenderSettings.skybox, "The shared material asset must not be used directly");

            for (int i = 0; i < 25; i++)
            {
                sky.enabled = false;
                Assert.AreSame(placeholderSkybox, RenderSettings.skybox, "Previous skybox not restored");
                Assert.AreEqual(0, LiveInstances());
                sky.enabled = true;
                Assert.AreEqual(1, LiveInstances());
            }

            Object.DestroyImmediate(go);
            go = null;
            Assert.AreEqual(0, LiveInstances());
            Assert.AreSame(placeholderSkybox, RenderSettings.skybox);
        }

        [Test]
        public void SharedMaterialAsset_IsNeverModified()
        {
            var asset = sky.skyMaterial;
            var before = asset.GetColor("_ZenithColor");
            var offsetBefore = asset.GetVector("_CloudOffset");
            var densityBefore = asset.GetVector("_CloudDensity");
            var litBefore = asset.GetColor("_CloudLitColor");
            sky.previewCloudMotionInEditMode = true;
            sky.TransitionToPreset(CloudPreset.Overcast, 1f);
            foreach (float t in Checkpoints) { sky.TimeOfDay = t; sky.Tick(0.2f); }
            Assert.AreEqual(before, asset.GetColor("_ZenithColor"));
            Assert.AreEqual(offsetBefore, asset.GetVector("_CloudOffset"));
            Assert.AreEqual(densityBefore, asset.GetVector("_CloudDensity"));
            Assert.AreEqual(litBefore, asset.GetColor("_CloudLitColor"));
            Assert.AreNotEqual(offsetBefore, sky.MaterialInstance.GetVector("_CloudOffset"), "The instance should receive the wind");
            Assert.AreSame(asset.GetTexture("_CloudNoise"), sky.MaterialInstance.GetTexture("_CloudNoise"), "Noise must be shared, not copied");
        }

        // ------------------------------------------------------------------------------------
        // Clouds

        static Color32[] noise;
        static Color32[] Noise => noise ??= CloudNoiseGenerator.Generate(CloudNoiseGenerator.DefaultSize, CloudNoiseGenerator.DefaultSeed);

        /// <summary>CPU copy of the shader's density (shape and detail sampled at the same texel).</summary>
        static float CoveredFraction(DayNightSkyController c, out float maxDensity)
        {
            c.GetCloudParams(out var shape, out var density, out _);
            int covered = 0;
            maxDensity = 0f;
            foreach (var p in Noise)
            {
                float n = p.r / 255f + (p.g / 255f - 0.5f) * shape.w;
                float d = Mathf.Clamp01(n * density.x + density.y) * density.z;
                maxDensity = Mathf.Max(maxDensity, d);
                if (d > 0.5f) covered++;
            }
            return covered / (float)Noise.Length;
        }

        [Test]
        public void CloudNoise_IsSeamlessDeterministicAndEqualised()
        {
            int size = CloudNoiseGenerator.DefaultSize;
            var a = Noise;
            CollectionAssert.AreEqual(a, CloudNoiseGenerator.Generate(size, CloudNoiseGenerator.DefaultSeed), "Same seed must give the same texture");
            CollectionAssert.AreNotEqual(a, CloudNoiseGenerator.Generate(size, 7), "Seed must change the texture");

            foreach (int channel in new[] { 0, 1 })
            {
                int Get(int x, int y) => channel == 0 ? a[y * size + x].r : a[y * size + x].g;
                double inner = 0, edge = 0, sum = 0;
                for (int i = 0; i < size; i++)
                {
                    // Neighbour differences across the wrap must look like any other neighbours.
                    edge += Mathf.Abs(Get(size - 1, i) - Get(0, i)) + Mathf.Abs(Get(i, size - 1) - Get(i, 0));
                    inner += Mathf.Abs(Get(size / 2 - 1, i) - Get(size / 2, i)) + Mathf.Abs(Get(i, size / 2 - 1) - Get(i, size / 2));
                }
                foreach (var p in a) sum += channel == 0 ? p.r : p.g;
                Debug.Log($"Noise channel {channel}: mean {sum / a.Length:0.0}, wrap step {edge / (2 * size):0.00}, inner step {inner / (2 * size):0.00}");
                Assert.Less(edge, inner * 1.6 + size, $"Channel {channel} has a seam");
                Assert.AreEqual(127.5, sum / a.Length, 2.0, $"Channel {channel} is not equalised");
            }
        }

        [Test]
        public void NoiseTextureAsset_IsLinearRepeatingAndMipmapped()
        {
            var tex = CloudNoiseGenerator.GetOrCreateTexture();
            Assert.IsNotNull(tex);
            Assert.AreEqual(TextureWrapMode.Repeat, tex.wrapModeU);
            Assert.AreEqual(TextureWrapMode.Repeat, tex.wrapModeV);
            Assert.Greater(tex.mipmapCount, 1);
            Assert.IsFalse(tex.isDataSRGB);
            var importer = (TextureImporter)AssetImporter.GetAtPath(CloudNoiseGenerator.TexturePath);
            Assert.AreEqual(TextureImporterFormat.RG16, importer.GetPlatformTextureSettings("Android").format);
            Assert.AreSame(tex, sky.skyMaterial.GetTexture("_CloudNoise"));
        }

        [Test]
        public void Coverage_ControlsHowMuchSkyIsCovered()
        {
            sky.cloudCoverage = 0f;
            Assert.AreEqual(0f, CoveredFraction(sky, out float maxAtZero));
            Assert.AreEqual(0f, maxAtZero, "Coverage 0 must remove every cloud");

            float previous = 0f;
            for (int i = 1; i <= 10; i++)
            {
                sky.cloudCoverage = i / 10f;
                float f = CoveredFraction(sky, out _);
                Assert.GreaterOrEqual(f, previous, $"Coverage {i / 10f} covers less than the step before");
                previous = f;
            }
            Assert.AreEqual(1f, previous, 1e-3f, "Coverage 1 must cover everything");

            sky.ApplyPresetImmediate(CloudPreset.Clear);
            float clear = CoveredFraction(sky, out _);
            sky.ApplyPresetImmediate(CloudPreset.Cloudy);
            float cloudy = CoveredFraction(sky, out _);
            sky.ApplyPresetImmediate(CloudPreset.Overcast);
            float overcast = CoveredFraction(sky, out _);
            Debug.Log($"Covered: clear {clear:P0}, cloudy {cloudy:P0}, overcast {overcast:P0}");
            Assert.Less(clear, 0.08f);
            Assert.That(cloudy, NUnit.Framework.Is.InRange(0.25f, 0.6f), "Cloudy should leave plenty of sky");
            Assert.Greater(overcast, 0.85f);
        }

        [Test]
        public void Overcast_KeepsVariation()
        {
            sky.ApplyPresetImmediate(CloudPreset.Overcast);
            sky.GetCloudParams(out var shape, out _, out var lighting);
            float min = 1f, max = 0f;
            foreach (var p in Noise)
            {
                float n = p.r / 255f + (p.g / 255f - 0.5f) * shape.w;
                float thick = Mathf.Clamp01(n * lighting.x + lighting.y);
                min = Mathf.Min(min, thick);
                max = Mathf.Max(max, thick);
            }
            Assert.Greater(max - min, 0.8f, "Overcast must not become one flat colour");
            var s = sky.Evaluate(12f);
            Assert.Greater(Diff(s.cloudLit, s.cloudShade), 0.1f);
        }

        [Test]
        public void Presets_ApplyImmediatelyAndTransitionSmoothly()
        {
            sky.ApplyPresetImmediate(CloudPreset.Overcast);
            Assert.IsTrue(sky.CurrentWeather.Approximately(sky.overcastPreset));
            Assert.IsFalse(sky.IsTransitioning);

            sky.ApplyPresetImmediate(CloudPreset.Clear);
            sky.TransitionToPreset(CloudPreset.Overcast, 4f);
            var prev = sky.CurrentWeather;
            for (int i = 0; i < 20; i++) // 2 of 4 seconds
            {
                sky.Tick(0.1f);
                Assert.Less(Mathf.Abs(sky.CloudCoverage - prev.coverage), 0.05f, "Coverage jumped");
                prev = sky.CurrentWeather;
            }
            Assert.That(sky.CloudCoverage, NUnit.Framework.Is.InRange(0.3f, 0.7f), "Half-way coverage expected");

            // Switching mid-way starts from what is displayed now.
            var shown = sky.CurrentWeather;
            sky.TransitionToPreset(CloudPreset.Cloudy, 4f);
            Assert.IsTrue(sky.CurrentWeather.Approximately(shown), "Starting a transition must not change the sky");
            sky.Tick(0.05f);
            Assert.Less(Mathf.Abs(sky.CloudCoverage - shown.coverage), 0.01f);
            Assert.Less(Mathf.Abs(sky.OvercastAmount - shown.overcast), 0.01f);

            for (int i = 0; i < 100; i++) sky.Tick(0.1f);
            Assert.IsFalse(sky.IsTransitioning);
            Assert.IsTrue(sky.CurrentWeather.Approximately(sky.cloudyPreset));

            sky.TransitionToPreset(CloudPreset.Clear, 0f);
            Assert.IsTrue(sky.CurrentWeather.Approximately(sky.clearPreset), "Zero duration applies at once");
        }

        static float WrappedStep(float a, float b)
        {
            float d = Mathf.Abs(a - b);
            return Mathf.Min(d, 1f - d);
        }

        [Test]
        public void Wind_ChangesNeverJumpAndOffsetStaysBounded()
        {
            sky.previewCloudMotionInEditMode = true;
            sky.ApplyPresetImmediate(CloudPreset.Cloudy);
            sky.WindSpeed = 0.02f;
            sky.WindDirection = 0f;
            const float dt = 0.02f;
            // Largest legitimate detail step per tick at the highest speed used below.
            float maxStep = 0.2f * dt * 0.35f / sky.cloudScale * 2.7f * 1.2f * 1.5f;

            var prev = sky.CloudOffset;
            for (int i = 0; i < 600; i++)
            {
                if (i == 150) sky.WindSpeed = 0.2f;
                if (i == 300) sky.WindDirection = 135f;
                if (i == 450) { sky.WindDirection = -90f; sky.WindSpeed = 0.05f; }
                sky.Tick(dt);
                var o = sky.CloudOffset;
                for (int k = 0; k < 4; k++)
                    Assert.Less(WrappedStep(o[k], prev[k]), maxStep, $"Offset jumped at step {i}");
                prev = o;
            }

            // Direction is world based: 90° drifts the clouds towards +X, so the shape offset moves to -X.
            sky.WindDirection = 90f;
            var before = sky.CloudOffset;
            sky.Tick(dt);
            Assert.Less(WrappedStep(sky.CloudOffset.y, before.y), 1e-6f, "No Z motion for a +X wind");
            Assert.Greater(WrappedStep(sky.CloudOffset.x, before.x), 0f);

            // Hours of play at a strong wind: offsets stay in [0, 1), so precision never degrades.
            sky.WindSpeed = 5f;
            for (int i = 0; i < 100000; i++) sky.Tick(0.1f);
            var far = sky.CloudOffset;
            for (int k = 0; k < 4; k++) Assert.That(far[k], NUnit.Framework.Is.InRange(0f, 1f));
            Assert.AreEqual(far, sky.MaterialInstance.GetVector("_CloudOffset"));
        }

        [Test]
        public void Wind_DoesNotMoveInEditModeWithoutPreview()
        {
            sky.previewCloudMotionInEditMode = false;
            var before = sky.CloudOffset;
            for (int i = 0; i < 10; i++) sky.Tick(0.1f);
            Assert.AreEqual(before, sky.CloudOffset);
        }

        [Test]
        public void Overcast_DimsLightFromTheDayNightValueWithoutAccumulating()
        {
            var light = new GameObject("Sun").AddComponent<Light>();
            light.type = LightType.Directional;
            sky.sunLight = light;
            try
            {
                sky.TimeOfDay = 12f;
                sky.OvercastAmount = 0f;
                sky.Tick(0f);
                float clear = light.intensity;
                Assert.AreEqual(sky.sunLightIntensity, clear, 1e-4f);

                sky.OvercastAmount = 1f;
                for (int i = 0; i < 50; i++) { sky.TimeOfDay = 12f; sky.Tick(0.016f); }
                Assert.AreEqual(clear * sky.overcastLightMultiplier, light.intensity, 1e-4f, "Dimming must not accumulate");

                sky.OvercastAmount = 0f;
                sky.Tick(0f);
                Assert.AreEqual(clear, light.intensity, 1e-4f, "Clearing up must restore the full light");
            }
            finally
            {
                Object.DestroyImmediate(light.gameObject);
            }
        }

        [Test]
        public void Overcast_MutesSkyButKeepsDayNightCharacter()
        {
            sky.OvercastAmount = 0f;
            var clearNoon = sky.Evaluate(12f);
            var clearDusk = sky.Evaluate(18f);
            sky.OvercastAmount = 1f;
            var greyNoon = sky.Evaluate(12f);
            var greyNight = sky.Evaluate(0f);

            float Saturation(Color c) => c.maxColorComponent - Mathf.Min(c.r, Mathf.Min(c.g, c.b));
            Assert.Less(Saturation(greyNoon.zenith), Saturation(clearNoon.zenith) * 0.6f, "Overcast should mute the sky");
            Assert.Less(greyNoon.zenith.maxColorComponent, clearNoon.zenith.maxColorComponent);
            Assert.Greater(greyNoon.zenith.maxColorComponent, 0.3f, "Overcast day should stay bright enough to be cozy");
            Assert.Less(greyNoon.sunVisibility, clearNoon.sunVisibility);

            // Clouds: bright by day, warm at sunset, darker but visible against the night sky.
            Assert.Greater(clearNoon.cloudLit.grayscale, 0.9f);
            Assert.Greater(clearDusk.cloudLit.r, clearDusk.cloudLit.b + 0.1f, "Sunset clouds should be warm");
            Assert.Less(greyNight.cloudLit.grayscale, 0.4f);
            Assert.Greater(greyNight.cloudLit.grayscale, greyNight.zenith.grayscale + 0.04f, "Night clouds must remain readable");
            Assert.AreEqual(0f, greyNight.cloudLit.a, "No sun glow on clouds at night");
        }

        [Test]
        public void LightSync_FollowsSun()
        {
            var light = new GameObject("Sun").AddComponent<Light>();
            light.type = LightType.Directional;
            sky.sunLight = light;
            try
            {
                sky.TimeOfDay = 12f;
                sky.Tick(0f);
                var sun = sky.Evaluate(12f).sunDirection;
                Assert.Greater(Vector3.Dot(light.transform.forward, -sun), 0.9999f, "Light must shine from the sun towards the scene");
                Assert.Greater(light.intensity, 1f);
                Assert.IsTrue(light.enabled);

                sky.TimeOfDay = 0f;
                sky.Tick(0f);
                Assert.AreEqual(0f, light.intensity);
                Assert.IsFalse(light.enabled, "Light should be disabled at night");

                sky.TimeOfDay = 7f;
                sky.Tick(0f);
                Assert.IsTrue(light.enabled);
                Assert.Greater(light.intensity, 0f);
            }
            finally
            {
                Object.DestroyImmediate(light.gameObject);
            }
        }

    }
}
