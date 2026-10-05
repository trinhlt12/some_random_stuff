using System.Linq;
using NUnit.Framework;
using PastelLowPoly.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace PastelLowPoly.Tests
{
    public class PastelConverterTests
    {
        const string Temp = "Assets/__PastelConverterTestTemp";
        const string StylooChairTable = "Assets/StylooClassroomAssetPack GLTF & FBX/classroom/GLTF/chairtable.glb";

        [SetUp]
        public void SetUp()
        {
            if (AssetDatabase.IsValidFolder(Temp)) AssetDatabase.DeleteAsset(Temp);
            AssetDatabase.CreateFolder("Assets", "__PastelConverterTestTemp");
        }

        [TearDown]
        public void TearDown() => AssetDatabase.DeleteAsset(Temp);

        static Material Lit(string name, Color color)
        {
            var m = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = name };
            m.SetColor("_BaseColor", color);
            AssetDatabase.CreateAsset(m, $"{Temp}/{name}.mat");
            return m;
        }

        static void AssertColor(Color expected, Color actual)
        {
            Assert.Less(Mathf.Abs(expected.r - actual.r) + Mathf.Abs(expected.g - actual.g) + Mathf.Abs(expected.b - actual.b) + Mathf.Abs(expected.a - actual.a), 1e-4f, $"{expected} != {actual}");
        }

        static PastelConvertSettings Settings() => new PastelConvertSettings { outputFolder = Temp + "/Out" };

        [Test]
        public void Shader_CompilesWithoutErrors()
        {
            var shader = PastelMaterialConverter.PastelShader;
            Assert.IsNotNull(shader);
            Assert.IsFalse(ShaderUtil.ShaderHasError(shader));
            Assert.IsTrue(shader.isSupported);
        }

        [Test]
        public void StylooGltfMaterial_KeepsPaletteTexture()
        {
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(StylooChairTable);
            Assume.That(model, Is.Not.Null, "Styloo GLB not imported");
            var infos = PastelMaterialConverter.Analyze(model, null);
            Assert.IsNotEmpty(infos);
            foreach (var info in infos)
            {
                Assert.IsNotNull(info.texture, $"{info.source.name}: palette texture not found");
                StringAssert.Contains("Imphenzia", info.texture.name);
                var pastel = PastelMaterialConverter.CreatePastelMaterial(info, Settings());
                Assert.AreSame(info.texture, pastel.GetTexture("_BaseMap"));
                Assert.IsTrue(pastel.IsKeywordEnabled("_USE_BASE_MAP"));
                Assert.IsTrue(pastel.IsKeywordEnabled("_PASTELIZE"));
                Object.DestroyImmediate(pastel);
            }
        }

        [Test]
        public void MultiMaterialPrefab_GetsVariantWithMatchingSlots()
        {
            var red = Lit("Red", new Color(0.9f, 0.1f, 0.1f));
            var blue = Lit("Blue", new Color(0.1f, 0.2f, 0.9f));
            var root = new GameObject("Toy");
            var a = GameObject.CreatePrimitive(PrimitiveType.Cube);
            a.transform.SetParent(root.transform);
            a.GetComponent<MeshRenderer>().sharedMaterials = new[] { red, blue };
            var b = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            b.transform.SetParent(root.transform);
            b.GetComponent<MeshRenderer>().sharedMaterial = blue;
            var prefab = PrefabUtility.SaveAsPrefabAsset(root, $"{Temp}/Toy.prefab");
            Object.DestroyImmediate(root);

            var result = PastelMaterialConverter.Convert(prefab, Settings());

            Assert.AreEqual(2, result.materialPaths.Count, "One pastel material per source material");
            var variant = AssetDatabase.LoadAssetAtPath<GameObject>(result.prefabPath);
            Assert.AreEqual(PrefabAssetType.Variant, PrefabUtility.GetPrefabAssetType(variant));

            var slots = variant.transform.GetChild(0).GetComponent<MeshRenderer>().sharedMaterials;
            Assert.AreEqual(2, slots.Length);
            AssertColor(red.GetColor("_BaseColor"), slots[0].GetColor("_BaseColor"));
            AssertColor(blue.GetColor("_BaseColor"), slots[1].GetColor("_BaseColor"));
            Assert.AreSame(slots[1], variant.transform.GetChild(1).GetComponent<MeshRenderer>().sharedMaterial, "Shared source material must map to one pastel material");
            Assert.IsTrue(slots.All(m => m.shader == PastelMaterialConverter.PastelShader));

            // Source untouched.
            Assert.AreEqual(AssetDatabase.GetAssetPath(red), AssetDatabase.GetAssetPath(prefab.transform.GetChild(0).GetComponent<MeshRenderer>().sharedMaterials[0]), "Source prefab was modified");
            Assert.AreEqual("Universal Render Pipeline/Lit", red.shader.name);
        }

        [Test]
        public void CutoutAndTransparentMaterials_BecomeAlphaClip()
        {
            var cutout = Lit("Leaf", Color.green);
            cutout.SetFloat("_AlphaClip", 1);
            cutout.SetFloat("_Cutoff", 0.3f);
            cutout.EnableKeyword("_ALPHATEST_ON");
            cutout.SetFloat("_Cull", (float)CullMode.Off);

            var glass = Lit("Glass", new Color(1, 1, 1, 0.3f));
            glass.SetFloat("_Surface", 1);
            glass.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");

            var leaf = PastelMaterialConverter.Inspect(cutout, false);
            Assert.IsTrue(leaf.cutout);
            Assert.AreEqual(0.3f, leaf.cutoff, 1e-6f);
            Assert.IsTrue(leaf.doubleSided);
            var leafPastel = PastelMaterialConverter.CreatePastelMaterial(leaf, Settings());
            Assert.IsTrue(leafPastel.IsKeywordEnabled("_ALPHATEST_ON"));
            Assert.AreEqual((float)CullMode.Off, leafPastel.GetFloat("_Cull"));
            Assert.AreEqual((int)RenderQueue.AlphaTest, leafPastel.renderQueue);

            var g = PastelMaterialConverter.Inspect(glass, false);
            Assert.IsTrue(g.wasTransparent);
            Assert.IsTrue(g.cutout);
            Assert.IsTrue(g.notes.Any(n => n.Contains("trong suốt")));
        }

        [Test]
        public void SceneObject_IsChangedWithUndo()
        {
            var red = Lit("Red", Color.red);
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.GetComponent<MeshRenderer>().sharedMaterial = red;
            try
            {
                var result = PastelMaterialConverter.Convert(go, Settings());
                Assert.IsTrue(result.appliedToScene);
                Assert.AreEqual(PastelMaterialConverter.PastelShader, go.GetComponent<MeshRenderer>().sharedMaterial.shader);
                Undo.PerformUndo();
                Assert.AreEqual(AssetDatabase.GetAssetPath(red), AssetDatabase.GetAssetPath(go.GetComponent<MeshRenderer>().sharedMaterial), "Undo did not restore the source material");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }
    }
}
