using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using RockGen.Editor;
using UnityEditor;
using UnityEngine;

namespace RockGen.Tests
{
    public class RockGenTests
    {
        static RockSettings S(int seed) { var s = RockSettings.Default; s.seed = seed; return s; }

        static void AssertValid(RockMeshData data)
        {
            Assert.IsTrue(RockValidation.Validate(data, out string error), error);
        }

        [Test]
        public void SameSeed_SameMesh()
        {
            var a = RockGenerator.Generate(S(123));
            var b = RockGenerator.Generate(S(123));
            CollectionAssert.AreEqual(a.vertices, b.vertices);
            CollectionAssert.AreEqual(a.normals, b.normals);
            CollectionAssert.AreEqual(a.triangles, b.triangles);
        }

        [Test]
        public void DifferentSeeds_GiveVisiblyDifferentShapes()
        {
            var shapes = Enumerable.Range(1, 12).Select(i => RockGenerator.Generate(S(i))).ToList();
            for (int i = 0; i < shapes.Count; i++)
                for (int j = i + 1; j < shapes.Count; j++)
                {
                    float d = ShapeDistance(shapes[i], shapes[j]);
                    Assert.Greater(d, 0.02f, $"Seeds {i + 1} and {j + 1} look the same (distance {d})");
                }
        }

        // Mean distance from each vertex of a to the nearest vertex of b, relative to the rock size.
        static float ShapeDistance(RockMeshData a, RockMeshData b)
        {
            float sum = 0f;
            foreach (var p in a.vertices.Distinct())
                sum += b.vertices.Min(q => (p - q).magnitude);
            return sum / a.vertices.Distinct().Count() / a.settings.size.magnitude;
        }

        [Test]
        public void ManySeeds_AreValidClosedOutwardAndFlatShaded()
        {
            for (int seed = 0; seed < 200; seed++)
            {
                var s = S(seed);
                s.roughness = (seed % 5) / 4f;
                s.asymmetry = (seed % 7) / 6f;
                s.bottomFlatten = (seed % 3) / 2f;
                s.triangleBudget = 20 + (seed * 37) % 400;
                AssertValid(RockGenerator.Generate(s));
            }
        }

        [Test]
        public void ExtremeInputs_AreClampedAndValid()
        {
            var cases = new[]
            {
                new RockSettings { seed = int.MinValue, size = Vector3.zero, roughness = -5, asymmetry = -5, bottomFlatten = -5, triangleBudget = -10 },
                new RockSettings { seed = int.MaxValue, size = Vector3.one * 1e9f, roughness = 9, asymmetry = 9, bottomFlatten = 9, triangleBudget = 1 << 30 },
                new RockSettings { seed = 0, size = new Vector3(float.NaN, float.PositiveInfinity, -3f), roughness = float.NaN, asymmetry = float.NaN, bottomFlatten = float.NaN, triangleBudget = 0 },
                new RockSettings { seed = 5, size = new Vector3(50f, 0.05f, 0.05f), roughness = 1, asymmetry = 1, bottomFlatten = 1, triangleBudget = RockSettings.MaxTriangles },
                new RockSettings { seed = 6, size = new Vector3(0.05f, 50f, 0.05f), roughness = 0, asymmetry = 0, bottomFlatten = 0, triangleBudget = RockSettings.MinTriangles },
            };
            foreach (var c in cases)
            {
                var data = RockGenerator.Generate(c);
                AssertValid(data);
                Assert.Greater(data.TriangleCount, 3);
                Assert.LessOrEqual(data.TriangleCount, data.settings.triangleBudget);
            }
        }

        [Test]
        public void SizeAndPivot_MatchSettings()
        {
            var s = S(9);
            s.size = new Vector3(1.3f, 0.6f, 0.8f);
            var data = RockGenerator.Generate(s);
            var b = new Bounds(data.vertices[0], Vector3.zero);
            foreach (var v in data.vertices) b.Encapsulate(v);
            Assert.AreEqual(s.size.x, b.size.x, 1e-4f);
            Assert.AreEqual(s.size.y, b.size.y, 1e-4f);
            Assert.AreEqual(s.size.z, b.size.z, 1e-4f);
            Assert.AreEqual(0f, b.min.y, 1e-5f, "Pivot must be at the bottom");
            Assert.AreEqual(0f, b.center.x, 1e-4f, "Pivot must be centred");
            Assert.AreEqual(0f, b.center.z, 1e-4f, "Pivot must be centred");
        }

        [Test]
        public void FlattenedBottom_HasLargeFlatBase()
        {
            float FlatBaseArea(float flatten)
            {
                var s = S(31);
                s.bottomFlatten = flatten;
                var d = RockGenerator.Generate(s);
                float area = 0f;
                for (int i = 0; i < d.triangles.Length; i += 3)
                {
                    Vector3 a = d.vertices[i], b = d.vertices[i + 1], c = d.vertices[i + 2];
                    if (a.y < 1e-5f && b.y < 1e-5f && c.y < 1e-5f)
                        area += Vector3.Cross(b - a, c - a).magnitude * 0.5f;
                }
                return area / (s.size.x * s.size.z);
            }
            float flat = FlatBaseArea(1f), round = FlatBaseArea(0f);
            Debug.Log($"Flat base area ratio: flatten=1 -> {flat:0.00}, flatten=0 -> {round:0.00}");
            Assert.Greater(flat, 0.3f);
            Assert.Greater(flat, round * 2f);
        }

        [Test]
        public void TriangleBudget_IsRespected()
        {
            foreach (int budget in new[] { 20, 40, 80, 160, 320, 600 })
            {
                var s = S(77);
                s.triangleBudget = budget;
                var d = RockGenerator.Generate(s);
                Assert.LessOrEqual(d.TriangleCount, budget);
                Assert.Greater(d.TriangleCount, budget / 4, "Budget should be mostly used");
            }
        }

        [Test]
        public void GlobalRandomState_IsUntouched()
        {
            Random.InitState(999);
            float expected = Random.value;

            Random.InitState(999);
            RockGenerator.Generate(S(4));
            Assert.AreEqual(expected, Random.value, "Generator consumed or changed UnityEngine.Random");
        }

        [Test]
        public void SavedPrefab_WorksAfterPreviewIsCleared()
        {
            const string folder = "Assets/__RockGenTestTemp";
            var preview = new RockPreview();
            try
            {
                preview.Show(new List<RockSettings> { S(55) });
                var rock = preview.Rocks[0];
                var saved = RockAssets.Save(rock.GetComponent<MeshFilter>().sharedMesh, rock.name, folder);
                var second = RockAssets.Save(rock.GetComponent<MeshFilter>().sharedMesh, rock.name, folder);
                Assert.AreNotEqual(saved.prefabPath, second.prefabPath, "Saving twice must not overwrite");

                preview.Clear();

                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(saved.prefabPath);
                var mesh = prefab.GetComponent<MeshFilter>().sharedMesh;
                Assert.IsNotNull(mesh);
                Assert.IsTrue(EditorUtility.IsPersistent(mesh));
                Assert.AreEqual(saved.meshPath, AssetDatabase.GetAssetPath(mesh));
                Assert.Greater(mesh.triangles.Length, 0);
                Assert.AreEqual(RockAssets.MaterialPath, AssetDatabase.GetAssetPath(prefab.GetComponent<MeshRenderer>().sharedMaterial));
                Assert.IsNull(prefab.GetComponent<Collider>(), "No collider by default");
            }
            finally
            {
                preview.Clear();
                AssetDatabase.DeleteAsset(folder);
            }
        }

        [Test]
        public void RepeatedGenerateAndClear_LeavesNothingBehind()
        {
            int MeshCount() => Resources.FindObjectsOfTypeAll<Mesh>().Count(m => m.name.StartsWith("Rock_") && !EditorUtility.IsPersistent(m));
            int RootCount() => Resources.FindObjectsOfTypeAll<GameObject>().Count(g => g.name == RockPreview.RootName);
            int meshesBefore = MeshCount();

            var preview = new RockPreview();
            var batch = Enumerable.Range(0, 9).Select(S).ToList();
            for (int i = 0; i < 10; i++)
            {
                preview.Show(batch);
                Assert.AreEqual(9, preview.Rocks.Count);
                Assert.AreEqual(meshesBefore + 9, MeshCount(), "Old preview meshes leaked on regenerate");
            }
            preview.Clear();
            Assert.AreEqual(meshesBefore, MeshCount());
            Assert.AreEqual(0, RootCount());
        }

        [Test]
        public void Clear_DoesNotTouchOtherObjects()
        {
            var preview = new RockPreview();
            var mine = new GameObject("UserObject");
            try
            {
                preview.Show(new List<RockSettings> { S(1) });
                var root = preview.Rocks[0].transform.parent.gameObject;
                var child = new GameObject("UserChildUnderPreview");
                child.transform.SetParent(root.transform);
                preview.Clear();
                Assert.IsTrue(mine != null, "Unrelated object was deleted");
                Assert.IsTrue(child != null, "User object under the preview root was deleted");
                Object.DestroyImmediate(child.transform.root.gameObject);
            }
            finally
            {
                preview.Clear();
                Object.DestroyImmediate(mine);
            }
        }
    }
}
