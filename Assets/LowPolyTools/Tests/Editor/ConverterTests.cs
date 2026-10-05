using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace LowPolyTools.Tests
{
    public class ConverterTests
    {
        const string TempFolder = "Assets/__LowPolyToolsTestTemp";

        [SetUp]
        public void SetUp()
        {
            if (AssetDatabase.IsValidFolder(TempFolder)) AssetDatabase.DeleteAsset(TempFolder);
            AssetDatabase.CreateFolder("Assets", "__LowPolyToolsTestTemp");
        }

        [TearDown]
        public void TearDown()
        {
            AssetDatabase.DeleteAsset(TempFolder);
        }

        static T Save<T>(T asset, string name) where T : Object
        {
            AssetDatabase.CreateAsset(asset, $"{TempFolder}/{name}");
            return asset;
        }

        static GameObject BuildSourcePrefab(out Mesh sphere, out Mesh grid, out Material[] materials)
        {
            sphere = Save(TestMeshes.UvSphere(16, 32), "sphere.asset");
            grid = Save(TestMeshes.Grid(12, true), "grid.asset");
            var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            materials = new[]
            {
                Save(new Material(shader) { name = "MatA" }, "MatA.mat"),
                Save(new Material(shader) { name = "MatB" }, "MatB.mat"),
            };

            var root = new GameObject("Source");
            var a = new GameObject("A");
            a.transform.SetParent(root.transform, false);
            a.transform.localPosition = new Vector3(1, 2, 3);
            a.transform.localRotation = Quaternion.Euler(0, 30, 10);
            a.transform.localScale = new Vector3(1, 2, 1);
            a.AddComponent<MeshFilter>().sharedMesh = sphere;
            a.AddComponent<MeshRenderer>().sharedMaterial = materials[0];
            a.AddComponent<MeshCollider>().sharedMesh = sphere;

            var b = new GameObject("B");
            b.transform.SetParent(a.transform, false);
            b.transform.localPosition = new Vector3(-2, 0, 0);
            b.AddComponent<MeshFilter>().sharedMesh = sphere;
            b.AddComponent<MeshRenderer>().sharedMaterial = materials[1];

            var c = new GameObject("C");
            c.transform.SetParent(root.transform, false);
            c.transform.localPosition = new Vector3(0, 0, -3);
            c.AddComponent<MeshFilter>().sharedMesh = grid;
            c.AddComponent<MeshRenderer>().sharedMaterials = materials;

            var skinned = new GameObject("Skinned");
            skinned.transform.SetParent(root.transform, false);
            skinned.AddComponent<SkinnedMeshRenderer>().sharedMesh = sphere;

            var prefab = PrefabUtility.SaveAsPrefabAsset(root, $"{TempFolder}/Source.prefab");
            Object.DestroyImmediate(root);
            return prefab;
        }

        static LowPolySettings Settings() => new LowPolySettings
        {
            targetRatio = 0.4f,
            maxError = 0.02f,
            outputFolder = TempFolder + "/Out",
        };

        [Test]
        public void Analyze_ReportsSkippedAndSharedMeshes()
        {
            var prefab = BuildSourcePrefab(out var sphere, out _, out _);
            var analysis = LowPolyConverter.Analyze(prefab);

            Assert.AreEqual(3, analysis.entries.Count);
            Assert.AreEqual(2, analysis.uniqueMeshes.Count);
            Assert.AreEqual(1, analysis.skipped.Count);
            StringAssert.Contains("SkinnedMeshRenderer", analysis.skipped[0].message);
            Assert.IsTrue(analysis.notes.Any(n => n.Contains("MeshCollider")));
            Assert.IsTrue(analysis.notes.Any(n => n.Contains(sphere.name) && n.Contains("dùng chung")));
        }

        [Test]
        public void Export_CreatesVariantAndLeavesSourceUntouched()
        {
            var prefab = BuildSourcePrefab(out var sphere, out var grid, out var materials);
            string sourceHash = AssetDatabase.GetAssetDependencyHash(AssetDatabase.GetAssetPath(prefab)).ToString();

            var analysis = LowPolyConverter.Analyze(prefab);
            var results = LowPolyConverter.Convert(analysis, Settings());
            var export = LowPolyConverter.Export(analysis, results, Settings());
            LowPolyConverter.DestroyResults(results);

            Assert.IsTrue(export.isVariant);
            Assert.AreEqual(2, export.meshPaths.Count, "Shared mesh must produce a single asset");

            var output = AssetDatabase.LoadAssetAtPath<GameObject>(export.prefabPath);
            var srcA = prefab.transform.Find("A");
            var outA = output.transform.Find("A");
            var outB = output.transform.Find("A/B");
            var outC = output.transform.Find("C");

            Assert.AreEqual(srcA.localPosition, outA.localPosition);
            Assert.AreEqual(srcA.localRotation, outA.localRotation);
            Assert.AreEqual(srcA.localScale, outA.localScale);
            Assert.AreEqual(prefab.transform.Find("A/B").localPosition, outB.localPosition);

            var meshA = outA.GetComponent<MeshFilter>().sharedMesh;
            Assert.AreNotEqual(sphere, meshA);
            Assert.AreSame(meshA, outB.GetComponent<MeshFilter>().sharedMesh, "Shared mesh reference lost");
            Assert.IsTrue(EditorUtility.IsPersistent(meshA));
            Assert.Less(meshA.triangles.Length, sphere.triangles.Length);

            var meshC = outC.GetComponent<MeshFilter>().sharedMesh;
            Assert.AreEqual(2, meshC.subMeshCount);
            CollectionAssert.AreEqual(materials, outC.GetComponent<MeshRenderer>().sharedMaterials);
            Assert.AreEqual(materials[1], outB.GetComponent<MeshRenderer>().sharedMaterial);

            Assert.AreSame(sphere, outA.GetComponent<MeshCollider>().sharedMesh, "MeshCollider should keep the source mesh");
            Assert.AreSame(sphere, output.transform.Find("Skinned").GetComponent<SkinnedMeshRenderer>().sharedMesh);

            // Source prefab and meshes are unchanged.
            Assert.AreSame(sphere, srcA.GetComponent<MeshFilter>().sharedMesh);
            Assert.AreSame(grid, prefab.transform.Find("C").GetComponent<MeshFilter>().sharedMesh);
            Assert.AreEqual(sourceHash, AssetDatabase.GetAssetDependencyHash(AssetDatabase.GetAssetPath(prefab)).ToString());
        }

        [Test]
        public void Export_FromSceneObject_MakesPlainPrefab()
        {
            var sphere = Save(TestMeshes.UvSphere(16, 32), "sphere.asset");
            var go = new GameObject("SceneThing");
            go.transform.position = new Vector3(5, 0, 0);
            go.AddComponent<MeshFilter>().sharedMesh = sphere;
            go.AddComponent<MeshRenderer>();
            int sceneRoots = go.scene.rootCount;
            try
            {
                var analysis = LowPolyConverter.Analyze(go);
                var results = LowPolyConverter.Convert(analysis, Settings());
                var export = LowPolyConverter.Export(analysis, results, Settings());
                LowPolyConverter.DestroyResults(results);

                Assert.IsFalse(export.isVariant);
                var output = AssetDatabase.LoadAssetAtPath<GameObject>(export.prefabPath);
                Assert.AreEqual(new Vector3(5, 0, 0), output.transform.localPosition);
                Assert.AreSame(sphere, go.GetComponent<MeshFilter>().sharedMesh, "Scene object was modified");
                Assert.AreEqual(sceneRoots, go.scene.rootCount, "Export left objects in the open scene");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void Cancel_WritesNothing()
        {
            var prefab = BuildSourcePrefab(out _, out _, out _);
            var analysis = LowPolyConverter.Analyze(prefab);
            var results = LowPolyConverter.Convert(analysis, Settings(), (label, f) => f > 0.3f);
            Assert.IsNull(results);
            Assert.IsFalse(AssetDatabase.IsValidFolder(TempFolder + "/Out"));
        }
    }
}
