using System.Collections.Generic;
using System.Linq;
using CottonDoll.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace CottonDoll.Tests
{
    public class CottonDollTests
    {
        static List<DollPart> defaults;

        static List<DollPart> Defaults()
        {
            if (defaults != null) return defaults;
            // Only the mesh data is shared between tests; each test makes its own settings object
            // (scene changes in other tests unload loose ScriptableObjects).
            var settings = ScriptableObject.CreateInstance<CottonDollSettings>();
            defaults = CottonDollGenerator.Generate(settings);
            Object.DestroyImmediate(settings);
            return defaults;
        }

        static CottonDollSettings Extreme(bool max)
        {
            var s = ScriptableObject.CreateInstance<CottonDollSettings>();
            foreach (var f in typeof(CottonDollSettings).GetFields())
            {
                if (f.FieldType != typeof(float)) continue;
                var r = (UnityEngine.RangeAttribute)System.Attribute.GetCustomAttribute(f, typeof(UnityEngine.RangeAttribute));
                if (r != null) f.SetValue(s, max ? r.max : r.min);
            }
            s.height = 1f;
            return s;
        }

        // Folded (inside-out) triangles allowed: 0.2% by default. At the extreme corners of the ranges a
        // crease between two cloth surfaces can fold one or two hidden-size triangles, so 0.5% there.
        static void AssertValid(DollPart p, string context, int foldDivisor = 500)
        {
            var d = p.data;
            string what = $"{context} {p.name}";
            Assert.Greater(d.TriangleCount, 0, what);
            Assert.AreEqual(0, d.triangles.Length % 3, what);
            Assert.AreEqual(d.positions.Length, d.normals.Length, what);
            foreach (int i in d.triangles) Assert.That(i, Is.InRange(0, d.VertexCount - 1), what);
            for (int v = 0; v < d.VertexCount; v++)
            {
                Vector3 pos = d.positions[v], n = d.normals[v];
                Assert.IsFalse(float.IsNaN(pos.x + pos.y + pos.z) || float.IsInfinity(pos.x + pos.y + pos.z), $"{what}: bad position");
                Assert.AreEqual(1f, n.magnitude, 1e-3f, $"{what}: normal not unit length");
            }
            int against = 0;
            for (int t = 0; t < d.triangles.Length; t += 3)
            {
                Vector3 a = d.positions[d.triangles[t]], b = d.positions[d.triangles[t + 1]], c = d.positions[d.triangles[t + 2]];
                Vector3 face = Vector3.Cross(b - a, c - a);
                Assert.Greater(face.magnitude * 0.5f, 1e-9f, $"{what}: zero-area triangle");
                Vector3 avg = d.normals[d.triangles[t]] + d.normals[d.triangles[t + 1]] + d.normals[d.triangles[t + 2]];
                if (Vector3.Dot(face, avg) <= 0f) against++;
            }
            // Faces must point the way the smooth normals do (outwards).
            Assert.LessOrEqual(against, d.TriangleCount / foldDivisor, $"{what}: {against} triangles face against their normals");
        }

        static Dictionary<long, int> EdgeUse(int[] tris)
        {
            var edges = new Dictionary<long, int>();
            for (int t = 0; t < tris.Length; t += 3)
                for (int e = 0; e < 3; e++)
                {
                    int a = tris[t + e], b = tris[t + (e + 1) % 3];
                    long k = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                    edges.TryGetValue(k, out int n);
                    edges[k] = n + 1;
                }
            return edges;
        }

        [Test]
        public void DefaultDoll_HasValidMeshesWithinBudget()
        {
            var parts = Defaults();
            foreach (var p in parts) AssertValid(p, "default");
            int tris = CottonDollGenerator.TriangleCount(parts);
            int materials = parts.Select(p => p.slot).Distinct().Count();
            Debug.Log("Parts: " + string.Join(", ", parts.Select(p => $"{p.name} {p.data.TriangleCount}t/{p.data.VertexCount}v")) + $"; total {tris} triangles, {materials} materials");
            Assert.That(tris, Is.InRange(3000, 6000), "Triangle budget");
            Assert.LessOrEqual(materials, 4);
            CollectionAssert.AreEquivalent(new[] { "Body", "Hair", "Shirt", "Shorts", "Shoes", "Face" }, parts.Select(p => p.name).ToArray());
        }

        [Test]
        public void Body_IsOneClosedSymmetricMesh()
        {
            var body = Defaults().First(p => p.name == "Body").data;
            var edges = EdgeUse(body.triangles);
            Assert.AreEqual(0, edges.Count(e => e.Value != 2), "Body must be closed and manifold (no holes)");

            var keys = new HashSet<Vector3Int>(body.positions.Select(Key));
            foreach (var p in body.positions)
                Assert.IsTrue(keys.Contains(Key(new Vector3(-p.x, p.y, p.z))), "Body must be left/right symmetric");

            // One connected piece: neck, shoulders and hips are blended, not separate blobs.
            Assert.AreEqual(1, Components(body), "Body must be a single connected mesh");
        }

        static Vector3Int Key(Vector3 p) => new Vector3Int(Mathf.RoundToInt(p.x * 1e5f), Mathf.RoundToInt(p.y * 1e5f), Mathf.RoundToInt(p.z * 1e5f));

        static int Components(MeshData d)
        {
            var parent = Enumerable.Range(0, d.VertexCount).ToArray();
            int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
            for (int t = 0; t < d.triangles.Length; t += 3)
            {
                parent[Find(d.triangles[t])] = Find(d.triangles[t + 1]);
                parent[Find(d.triangles[t + 1])] = Find(d.triangles[t + 2]);
            }
            return d.triangles.Select(Find).Distinct().Count();
        }

        [Test]
        public void Doll_StandsOnThePivotFacingPlusZ()
        {
            var parts = Defaults();
            Bounds all = default;
            bool first = true;
            foreach (var p in parts)
                foreach (var v in p.data.positions)
                {
                    if (first) { all = new Bounds(v, Vector3.zero); first = false; }
                    else all.Encapsulate(v);
                }
            Assert.AreEqual(0f, all.min.y, 0.004f, "Soles at y = 0");
            Assert.AreEqual(0f, all.center.x, 1e-4f, "Centred between the feet");
            var body = parts.First(p => p.name == "Body").data;
            Assert.AreEqual(1f, body.positions.Max(v => v.y), 0.01f, "Top of the head at the set height");
            var face = parts.First(p => p.name == "Face").data;
            Assert.Greater(face.positions.Min(v => v.z), 0.05f, "The face looks towards +Z");

            // Arms and legs separated (easier to rig): the gap between the legs and between arm and torso.
            Assert.IsTrue(body.positions.Any(v => Mathf.Abs(v.x) < 0.001f && v.y > 0.12f && v.y < 0.2f) == false,
                          "There must be space between the legs below the crotch");
        }

        [Test]
        public void SameSettings_GiveTheSameMesh()
        {
            var again = CottonDollGenerator.Generate(ScriptableObject.CreateInstance<CottonDollSettings>());
            var first = Defaults();
            for (int i = 0; i < first.Count; i++)
            {
                CollectionAssert.AreEqual(first[i].data.triangles, again[i].data.triangles, first[i].name);
                CollectionAssert.AreEqual(first[i].data.positions, again[i].data.positions, first[i].name);
            }
        }

        [Test]
        public void ExtremeSettings_StayValid()
        {
            foreach (bool max in new[] { false, true })
            {
                var s = Extreme(max);
                var parts = CottonDollGenerator.Generate(s);
                foreach (var p in parts) AssertValid(p, max ? "max" : "min", 200);
                var body = parts.First(p => p.name == "Body").data;
                Assert.AreEqual(0, EdgeUse(body.triangles).Count(e => e.Value != 2), $"{(max ? "max" : "min")}: body must stay closed");
                Assert.AreEqual(1, Components(body), $"{(max ? "max" : "min")}: body must stay one piece");
                int tris = CottonDollGenerator.TriangleCount(parts);
                Debug.Log($"{(max ? "max" : "min")} settings: {tris} triangles");
                Assert.Less(tris, 7000);
                Object.DestroyImmediate(s);
            }
        }

        [Test]
        public void Sanitize_ClampsBadInput()
        {
            var s = ScriptableObject.CreateInstance<CottonDollSettings>();
            s.height = float.NaN;
            s.headSize = 10f;
            s.legThickness = -3f;
            s.clothesGapPercent = 99f;
            s.Sanitize();
            Assert.AreEqual(1f, s.height);
            Assert.AreEqual(1.2f, s.headSize);
            Assert.AreEqual(0.75f, s.legThickness);
            Assert.AreEqual(3f, s.clothesGapPercent);
            Object.DestroyImmediate(s);
        }

        [Test]
        public void Clothes_And_Hair_CoverTheBodyWithAGap()
        {
            // Every body vertex in the zone a garment covers must lie inside that garment's solid,
            // with room to spare: then the body cannot poke through.
            var s = ScriptableObject.CreateInstance<CottonDollSettings>();
            Defaults();
            var shape = new CottonDollShapes(s);
            var body = Defaults().First(p => p.name == "Body").data;
            float margin = 0.004f;
            int shirtChecked = 0, shortsChecked = 0, hairChecked = 0;
            float shirtWorst = float.MinValue, shortsWorst = float.MinValue, hairWorst = float.MinValue;
            foreach (var v in body.positions)
            {
                Vector3 q = new Vector3(Mathf.Abs(v.x), v.y, v.z);
                bool nearArm = (q - shape.shoulder).magnitude > 0.06f && Vector3.Dot(q - shape.shoulder, shape.armDir) > 0f
                               && Vector3.Cross(q - shape.shoulder, shape.armDir).magnitude < 0.07f;
                // Torso band under the shirt (away from the neck opening and the arms).
                if (q.y > shape.hemY + 0.03f && q.y < shape.shoulderY - 0.06f && !nearArm && q.x < shape.shoulder.x)
                {
                    shirtWorst = Mathf.Max(shirtWorst, shape.Shirt(v));
                    shirtChecked++;
                }
                // Hips and thighs under the shorts.
                if (q.y > shape.shortsEnd.y + 0.03f && q.y < shape.waistY - 0.03f && !nearArm && q.x < 0.11f)
                {
                    shortsWorst = Mathf.Max(shortsWorst, shape.Shorts(v));
                    shortsChecked++;
                }
                // Top and back of the head under the hair.
                // (above the hairline; the forehead below it is covered by the fringe, not the cap)
                bool nearEar = (q - shape.earC).magnitude < shape.earR.y + 0.035f;   // the ear shows through its opening on purpose
                if (!nearEar && (q.y > shape.hairlineY + 0.02f || (q.z < shape.headC.z - 0.3f * shape.headR.z && q.y > shape.hairBottomY + 0.03f)))
                {
                    hairWorst = Mathf.Max(hairWorst, shape.Hair(v));
                    hairChecked++;
                }
            }
            Debug.Log($"Coverage (most exposed body vertex, negative = inside): shirt {shirtWorst:0.0000} on {shirtChecked}, shorts {shortsWorst:0.0000} on {shortsChecked}, hair {hairWorst:0.0000} on {hairChecked}");
            Assert.Greater(shirtChecked, 30); Assert.Greater(shortsChecked, 20); Assert.Greater(hairChecked, 30);
            Assert.Less(shirtWorst, -margin, "Body pokes through the shirt");
            Assert.Less(shortsWorst, -margin, "Body pokes through the shorts");
            Assert.Less(hairWorst, -margin, "Head pokes through the hair");
        }

        // ------------------------------------------------------------------------------------
        // Scene preview and saving

        const string TestFolder = "Assets/CottonDollTestTmp";

        [TearDown]
        public void TearDown()
        {
            if (AssetDatabase.IsValidFolder(TestFolder)) AssetDatabase.DeleteAsset(TestFolder);
        }

        [Test]
        public void Preview_GenerateAndClearRepeatedly_DoesNotPileUp()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var other = new GameObject("Someone Else's Object");
            var s = ScriptableObject.CreateInstance<CottonDollSettings>();
            var parts = Defaults();
            int baseline = CottonDollAssets.TemporaryMeshCount();
            for (int i = 0; i < 4; i++)
            {
                var root = CottonDollAssets.CreateOrUpdatePreview(s, parts, scene, Vector3.zero, Quaternion.identity);
                Assert.AreEqual(1, scene.GetRootGameObjects().Count(g => g.name == CottonDollAssets.PreviewRootName));
                Assert.AreEqual(parts.Count, root.transform.childCount);
                Assert.AreNotEqual(0, root.hideFlags & HideFlags.DontSaveInEditor, "The preview must not be saved with the scene");
                Assert.IsNull(root.GetComponentInChildren<Collider>(), "No colliders");
            }
            Assert.LessOrEqual(CottonDollAssets.TemporaryMeshCount(), baseline + parts.Count, "Regenerating reuses the preview meshes");
            for (int i = 0; i < 3; i++)
            {
                CottonDollAssets.ClearPreview(scene);
                CottonDollAssets.CreateOrUpdatePreview(s, parts, scene, Vector3.zero, Quaternion.identity);
            }
            CottonDollAssets.ClearPreview(scene);
            CottonDollAssets.SweepTemporaryResources();
            Assert.IsNull(CottonDollAssets.FindPreview(scene));
            Assert.LessOrEqual(CottonDollAssets.TemporaryMeshCount(), baseline);
            Assert.IsTrue(other != null, "Other objects must be left alone");
        }

        [Test]
        public void SavedPrefab_IsCompleteAndIndependentOfThePreview()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var s = ScriptableObject.CreateInstance<CottonDollSettings>();
            var parts = Defaults();
            CottonDollAssets.CreateOrUpdatePreview(s, parts, scene, Vector3.zero, Quaternion.identity);
            var prefab = CottonDollAssets.SaveAsAssets(s, parts, TestFolder, "TestDoll", false);
            Assert.IsNotNull(prefab);
            CottonDollAssets.ClearPreview(scene);
            CottonDollAssets.SweepTemporaryResources();

            prefab = AssetDatabase.LoadAssetAtPath<GameObject>(CottonDollAssets.PrefabPath(TestFolder, "TestDoll"));
            var filters = prefab.GetComponentsInChildren<MeshFilter>();
            Assert.AreEqual(parts.Count, filters.Length);
            foreach (var f in filters)
            {
                Assert.IsNotNull(f.sharedMesh, $"{f.name} lost its mesh");
                Assert.IsTrue(EditorUtility.IsPersistent(f.sharedMesh), $"{f.name} must use a saved mesh asset");
                Assert.Greater(f.sharedMesh.triangles.Length, 0);
            }
            var mats = prefab.GetComponentsInChildren<MeshRenderer>().Select(r => r.sharedMaterial).Distinct().ToList();
            Assert.LessOrEqual(mats.Count, 4);
            Assert.IsTrue(mats.All(m => m != null && EditorUtility.IsPersistent(m)));
            Assert.IsEmpty(prefab.GetComponentsInChildren<Component>().Where(c => !(c is Transform || c is MeshFilter || c is MeshRenderer)),
                           "The prefab must not depend on the generator or carry colliders");

            // No silent overwrite.
            string meshPath = $"{TestFolder}/TestDoll_Body.asset";
            string guid = AssetDatabase.AssetPathToGUID(meshPath);
            Assert.IsNull(CottonDollAssets.SaveAsAssets(s, parts, TestFolder, "TestDoll", false), "Must refuse to overwrite");
            Assert.IsNotNull(CottonDollAssets.SaveAsAssets(s, parts, TestFolder, "TestDoll", true));
            Assert.AreEqual(guid, AssetDatabase.AssetPathToGUID(meshPath), "Overwrite keeps the GUID");
        }
    }
}
