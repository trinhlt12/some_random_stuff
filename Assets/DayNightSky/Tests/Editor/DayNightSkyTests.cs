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
            try
            {
                float t = 0f;
                void Step()
                {
                    t += 0.37f;
                    sky.TimeOfDay = t;
                    sky.Tick(0.016f);
                }
                for (int i = 0; i < 20; i++) Step(); // warm-up
                Assert.That(() => { for (int i = 0; i < 200; i++) Step(); }, Is.Not.AllocatingGCMemory());
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
            foreach (float t in Checkpoints) { sky.TimeOfDay = t; sky.Tick(0f); }
            Assert.AreEqual(before, asset.GetColor("_ZenithColor"));
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
