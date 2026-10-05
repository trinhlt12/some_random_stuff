using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;

namespace DayNightSky.Tests
{
    public class DayNightSkyPlayModeTests
    {
        const string ShaderName = "Skybox/Day Night Gradient";

        static int LiveInstances() => Resources.FindObjectsOfTypeAll<Material>()
            .Count(m => m.name.EndsWith("(Instance)") && m.shader != null && m.shader.name == ShaderName);

        static DayNightSkyController Create(out Material template)
        {
            template = new Material(Shader.Find(ShaderName)) { name = "SkyTemplate" };
            var go = new GameObject("Sky");
            go.SetActive(false);
            var sky = go.AddComponent<DayNightSkyController>();
            sky.skyMaterial = template;
            go.SetActive(true);
            return sky;
        }

        [UnityTest]
        public IEnumerator EnableDisableDestroy_InPlayMode_ReleasesInstances()
        {
            Assert.IsTrue(Application.isPlaying);
            int before = LiveInstances();
            var previous = RenderSettings.skybox;
            var sky = Create(out var template);
            yield return null;
            Assert.AreEqual(before + 1, LiveInstances());

            for (int i = 0; i < 20; i++)
            {
                sky.enabled = false;
                yield return null; // Destroy() is deferred to the end of the frame in Play Mode
                Assert.AreEqual(before, LiveInstances(), $"Leak after disable #{i}");
                Assert.AreSame(previous, RenderSettings.skybox);
                sky.enabled = true;
                yield return null;
            }

            Object.Destroy(sky.gameObject);
            yield return null;
            Assert.AreEqual(before, LiveInstances());
            Assert.AreSame(previous, RenderSettings.skybox);
            Object.Destroy(template);
        }

        [UnityTest]
        public IEnumerator AutoAdvance_MovesTimeSmoothlyAndWraps()
        {
            var sky = Create(out var template);
            sky.dayLengthSeconds = 24f; // one game hour per real second
            sky.TimeOfDay = 23.9f;
            float last = sky.TimeOfDay;
            float start = Time.time;
            bool wrapped = false;
            while (Time.time - start < 0.5f)
            {
                yield return null;
                float now = sky.TimeOfDay;
                if (now < last) wrapped = true;
                else Assert.Less(now - last, 0.2f, "Time jumped");
                last = now;
            }
            Assert.IsTrue(wrapped, "Time should wrap from 24 to 0");
            Assert.Less(last, 1f);
            Object.Destroy(sky.gameObject);
            Object.Destroy(template);
        }

        [UnityTest]
        public IEnumerator SteadyUpdate_DoesNotAllocate()
        {
            var sky = Create(out var template);
            var light = new GameObject("Sun").AddComponent<Light>();
            light.type = LightType.Directional;
            sky.sunLight = light;
            yield return null;
            for (int i = 0; i < 10; i++) sky.Tick(0.016f); // warm-up
            Assert.That(() => { for (int i = 0; i < 300; i++) sky.Tick(0.016f); }, Is.Not.AllocatingGCMemory());
            Object.Destroy(light.gameObject);
            Object.Destroy(sky.gameObject);
            Object.Destroy(template);
        }
    }
}
