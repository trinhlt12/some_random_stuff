using System.Linq;
using DayNightSky.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DayNightSky.Tests
{
    public class WetnessDemoTests
    {
        Scene scene;
        Camera cam;

        [SetUp]
        public void SetUp()
        {
            // A fresh empty scene; the Test Framework restores the open scenes after the run.
            scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
            cam = camGo.AddComponent<Camera>();
            cam.fieldOfView = 60f;
            // Same view as the demo scene's camera.
            camGo.transform.SetPositionAndRotation(new Vector3(0f, 1.7f, 0f), Quaternion.Euler(-12f, -120f, 0f));
        }

        [TearDown]
        public void TearDown() => EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        GameObject[] Roots() => scene.GetRootGameObjects().Where(g => g.name == WetnessDemoBuilder.RootName).ToArray();

        static int MaterialAssets() => AssetDatabase.FindAssets("t:Material", new[] { WetnessDemoBuilder.Folder }).Length;

        [Test]
        public void RunningTwice_KeepsOneDemoAndTheSameAssets()
        {
            var other = new GameObject("Not Part Of The Demo");
            var first = WetnessDemoBuilder.CreateOrUpdate(scene);
            string prefabGuid = AssetDatabase.AssetPathToGUID(WetnessDemoBuilder.PrefabPath);
            int materials = MaterialAssets();
            Assert.AreEqual(5, materials);

            var second = WetnessDemoBuilder.CreateOrUpdate(scene);
            Assert.AreEqual(1, Roots().Length, "Running again must not duplicate the demo");
            Assert.AreSame(first, second);
            Assert.IsTrue(PrefabUtility.IsAnyPrefabInstanceRoot(second));
            Assert.AreEqual(prefabGuid, AssetDatabase.AssetPathToGUID(WetnessDemoBuilder.PrefabPath), "Prefab GUID must stay");
            Assert.AreEqual(materials, MaterialAssets(), "No duplicate materials");
            Assert.IsTrue(other != null, "Other objects must be left alone");
            Assert.IsTrue(cam != null);
        }

        [Test]
        public void Demo_HasTheExpectedPartsAndDistinctMaterials()
        {
            var root = WetnessDemoBuilder.CreateOrUpdate(scene);
            foreach (string part in new[] { "Ground (wet)", "Rock (wet)", "Bench (light wet)", "Lantern (no wet)" })
                Assert.IsNotNull(root.transform.Find(part), $"Missing {part}");

            var mats = root.GetComponentsInChildren<MeshRenderer>().Select(r => r.sharedMaterial).Distinct().ToArray();
            Assert.AreEqual(5, mats.Length);
            foreach (var m in mats)
            {
                Assert.IsTrue(EditorUtility.IsPersistent(m), $"{m.name} must be an asset");
                Assert.AreEqual(WetnessDemoBuilder.LitShader, m.shader.name);
            }
            for (int i = 0; i < mats.Length; i++)
                for (int j = i + 1; j < mats.Length; j++)
                {
                    Color a = mats[i].GetColor("_BaseColor"), b = mats[j].GetColor("_BaseColor");
                    Assert.Greater(Mathf.Abs(a.r - b.r) + Mathf.Abs(a.g - b.g) + Mathf.Abs(a.b - b.b), 0.1f, $"{mats[i].name} vs {mats[j].name}");
                }
            Assert.AreEqual(0, root.GetComponentsInChildren<Light>().Length, "The demo must not add lights");
        }

        [Test]
        public void DemoMaterials_HaveTheirWetnessRoles()
        {
            var root = WetnessDemoBuilder.CreateOrUpdate(scene);
            Material Mat(string part) => root.transform.Find(part).GetComponentInChildren<MeshRenderer>().sharedMaterial;
            var ground = Mat("Ground (wet)");
            var wood = Mat("Bench (light wet)");
            var painted = Mat("Lantern (no wet)");
            Assert.AreEqual(1f, ground.GetFloat("_ReceiveWetness"));
            Assert.AreEqual(1f, ground.GetFloat("_WetExposure"));
            Assert.That(wood.GetFloat("_WetExposure"), NUnit.Framework.Is.InRange(0.1f, 0.6f), "Wood only gets lightly wet");
            Assert.AreEqual(0f, painted.GetFloat("_ReceiveWetness"), "The painted lantern must not take wetness");
        }

        [Test]
        public void Demo_IsInFrontOfTheCameraAndObjectsAreApart()
        {
            var root = WetnessDemoBuilder.CreateOrUpdate(scene);
            Vector3[] centers = new Vector3[3];
            string[] parts = { "Rock (wet)", "Bench (light wet)", "Lantern (no wet)" };
            for (int i = 0; i < parts.Length; i++)
            {
                var renderers = root.transform.Find(parts[i]).GetComponentsInChildren<Renderer>();
                var bounds = renderers[0].bounds;
                foreach (var r in renderers) bounds.Encapsulate(r.bounds);
                centers[i] = bounds.center;
                foreach (var corner in new[] { bounds.min, bounds.max, bounds.center })
                {
                    var v = cam.WorldToViewportPoint(corner);
                    Assert.Greater(v.z, 0f, $"{parts[i]} is behind the camera");
                    Assert.That(v.x, NUnit.Framework.Is.InRange(0f, 1f), $"{parts[i]} is outside the view horizontally");
                    Assert.That(v.y, NUnit.Framework.Is.InRange(0f, 1f), $"{parts[i]} is outside the view vertically");
                }
            }
            Assert.Greater(Vector3.Distance(centers[0], centers[1]), 2.5f);
            Assert.Greater(Vector3.Distance(centers[1], centers[2]), 2.5f);

            // The ground reaches under the camera.
            var ground = root.transform.Find("Ground (wet)").GetComponent<Renderer>().bounds;
            Assert.IsTrue(ground.min.x < cam.transform.position.x && ground.max.x > cam.transform.position.x);
            Assert.IsTrue(ground.min.z < cam.transform.position.z && ground.max.z > cam.transform.position.z);
        }

        [Test]
        public void Update_KeepsAMovedDemoInPlaceAndReplacesStrayCopies()
        {
            var root = WetnessDemoBuilder.CreateOrUpdate(scene);
            root.transform.position += new Vector3(1f, 0f, 2f);
            var moved = root.transform.position;
            var stray = new GameObject(WetnessDemoBuilder.RootName);   // e.g. an old hand-made copy

            var again = WetnessDemoBuilder.CreateOrUpdate(scene);
            Assert.AreSame(root, again);
            Assert.AreEqual(moved, again.transform.position, "A moved demo keeps its place");
            Assert.AreEqual(1, Roots().Length);
            Assert.IsTrue(stray == null, "The stray copy is replaced");
        }

        [Test]
        public void Creation_CanBeUndone()
        {
            var keep = new GameObject("Keep");
            WetnessDemoBuilder.CreateOrUpdate(scene);
            Assert.AreEqual(1, Roots().Length);
            Undo.PerformUndo();
            Assert.AreEqual(0, Roots().Length, "Undo should remove the created demo");
            Assert.IsTrue(keep != null);
        }
    }
}
