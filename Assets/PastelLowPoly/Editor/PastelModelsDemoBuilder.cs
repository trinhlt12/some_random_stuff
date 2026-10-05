using System.Collections.Generic;
using LowPolyTools;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace PastelLowPoly.Editor
{
    /// <summary>
    /// Second demo: downloaded-style models (Styloo palette-textured GLBs plus generic multi-material,
    /// textured and cutout models) shown as original next to the pastel conversion.
    /// </summary>
    public static class PastelModelsDemoBuilder
    {
        public const string ScenePath = PastelDemoBuilder.DemoFolder + "/PastelModelsDemo.unity";
        public const string PastelModelFolder = PastelDemoBuilder.DemoFolder + "/PastelModels";
        const string SourceModelFolder = PastelDemoBuilder.DemoFolder + "/SourceModels";
        const string Styloo = "Assets/StylooClassroomAssetPack GLTF & FBX/";

        static readonly string[] StylooModels =
        {
            "classroom/GLTF/chairtable.glb", "classroom/GLTF/TV.glb", "classroom/GLTF/locker.glb",
            "classroom/GLTF/lamp.glb", "chemestry lab/GLTF/CHEMISTRYmicroscope.glb", "chemestry lab/GLTF/CHEMISTRYglobe_1.glb",
        };

        [MenuItem("Tools/Pastel Low Poly/Rebuild Models Demo Scene")]
        public static void Build()
        {
            if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;

            PastelDemoBuilder.CreateMaterialPresets();
            var models = new List<(GameObject original, GameObject pastel)>();
            foreach (var rel in StylooModels)
            {
                var source = AssetDatabase.LoadAssetAtPath<GameObject>(Styloo + rel);
                if (source == null)
                {
                    Debug.LogWarning($"Không tìm thấy {rel}; bỏ qua.");
                    continue;
                }
                models.Add((source, LowPolyPastelPrefab(source)));
            }
            foreach (var generic in GenericSourceModels())
                models.Add((generic, PastelPrefab(generic)));

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            PastelDemoBuilder.SetupEnvironment(orthographicSize: 6.4f, focus: new Vector3(0.3f, 0.4f, 0f));

            var floorMat = AssetDatabase.LoadAssetAtPath<Material>($"{PastelDemoBuilder.MaterialsFolder}/Ivory Floor.mat");
            var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            floor.name = "Floor";
            Object.DestroyImmediate(floor.GetComponent<Collider>());
            floor.transform.position = new Vector3(0, -0.1f, 0);
            floor.transform.localScale = new Vector3(18, 0.2f, 13);
            floor.GetComponent<MeshRenderer>().sharedMaterial = floorMat;

            // 3 x 3 grid of pairs: original on the left, pastel copy on the right.
            for (int i = 0; i < models.Count; i++)
            {
                int col = i % 3, row = i / 3;
                var cell = new GameObject($"{models[i].original.name}").transform;
                cell.position = new Vector3(-5.6f + col * 5.4f, 0, 3.6f - row * 3.8f);
                Place(models[i].original, cell, new Vector3(-1.0f, 0, 0), "Original");
                Place(models[i].pastel, cell, new Vector3(1.0f, 0, 0), "Pastel");
            }

            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.SaveAssets();
            Debug.Log($"Pastel models demo saved to {ScenePath}");
        }

        /// <summary>Instantiates a prefab scaled to a common size, resting on the floor.</summary>
        static void Place(GameObject prefab, Transform parent, Vector3 localPosition, string label)
        {
            var holder = new GameObject(label).transform;
            holder.SetParent(parent, false);
            holder.localPosition = localPosition;

            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            go.transform.SetParent(holder, false);

            var renderers = go.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0) return;
            var b = renderers[0].bounds;
            foreach (var r in renderers) b.Encapsulate(r.bounds);
            float scale = 1.6f / Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z));
            holder.localScale = Vector3.one * scale;
            // Centre on the cell and stand on y = 0 (bounds are in world space at scale 1).
            var offset = new Vector3(b.center.x - holder.position.x, b.min.y, b.center.z - holder.position.z);
            go.transform.position -= offset * scale;
        }

        /// <summary>Low-poly copy (medium preset) of a model, then pastel materials on top. Cached.</summary>
        public static GameObject LowPolyPastelPrefab(GameObject source)
        {
            string name = source.name;
            string pastelPath = $"{PastelModelFolder}/{name}_LowPoly_Pastel/{name}_LowPoly_Pastel.prefab";
            var cached = AssetDatabase.LoadAssetAtPath<GameObject>(pastelPath);
            if (cached != null) return cached;

            string lowPath = $"{PastelDemoBuilder.DemoModelFolder}/{name}_LowPoly/{name}_LowPoly.prefab";
            var lowPoly = AssetDatabase.LoadAssetAtPath<GameObject>(lowPath);
            if (lowPoly == null)
            {
                var settings = new LowPolySettings { targetRatio = 0.5f, maxError = 0.01f, outputFolder = PastelDemoBuilder.DemoModelFolder };
                var analysis = LowPolyConverter.Analyze(source);
                var results = LowPolyConverter.Convert(analysis, settings);
                var export = LowPolyConverter.Export(analysis, results, settings);
                LowPolyConverter.DestroyResults(results);
                lowPoly = AssetDatabase.LoadAssetAtPath<GameObject>(export.prefabPath);
            }
            return PastelPrefab(lowPoly);
        }

        public static GameObject PastelPrefab(GameObject source)
        {
            string path = $"{PastelModelFolder}/{source.name}_Pastel/{source.name}_Pastel.prefab";
            var cached = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (cached != null) return cached;
            var result = PastelMaterialConverter.Convert(source, new PastelConvertSettings { outputFolder = PastelModelFolder });
            return AssetDatabase.LoadAssetAtPath<GameObject>(result.prefabPath);
        }

        // ------------------------------------------------------------------------------------
        // Generic "downloaded" models built from URP Lit materials: saturated multi-material toy,
        // realistic textured crate, and a plant with alpha-cutout double-sided leaves.

        static List<GameObject> GenericSourceModels()
        {
            EnsureFolder(SourceModelFolder);
            return new List<GameObject> { Toy(), Crate(), Plant() };
        }

        static GameObject Toy()
        {
            string path = $"{SourceModelFolder}/ToyRobot.prefab";
            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (existing != null) return existing;

            var red = LitMaterial("Toy Red", new Color(0.85f, 0.15f, 0.12f));
            var blue = LitMaterial("Toy Blue", new Color(0.1f, 0.3f, 0.85f));
            var black = LitMaterial("Toy Black", new Color(0.06f, 0.06f, 0.07f));
            var white = LitMaterial("Toy White", new Color(0.95f, 0.95f, 0.95f));
            var yellow = LitMaterial("Toy Yellow", new Color(1f, 0.8f, 0.05f));

            var root = new GameObject("ToyRobot");
            Part(root, PrimitiveType.Cube, new Vector3(0, 0.15f, 0), new Vector3(1.0f, 0.3f, 0.7f), black);
            Part(root, PrimitiveType.Cube, new Vector3(0, 0.75f, 0), new Vector3(0.8f, 0.9f, 0.55f), red);
            Part(root, PrimitiveType.Cube, new Vector3(0, 1.45f, 0), new Vector3(0.6f, 0.5f, 0.5f), blue);
            Part(root, PrimitiveType.Sphere, new Vector3(-0.14f, 1.5f, -0.26f), Vector3.one * 0.14f, white);
            Part(root, PrimitiveType.Sphere, new Vector3(0.14f, 1.5f, -0.26f), Vector3.one * 0.14f, white);
            Part(root, PrimitiveType.Cylinder, new Vector3(0, 1.85f, 0), new Vector3(0.05f, 0.15f, 0.05f), black);
            Part(root, PrimitiveType.Sphere, new Vector3(0, 2.05f, 0), Vector3.one * 0.14f, yellow);
            Part(root, PrimitiveType.Cylinder, new Vector3(-0.55f, 0.8f, 0), new Vector3(0.18f, 0.35f, 0.18f), yellow);
            Part(root, PrimitiveType.Cylinder, new Vector3(0.55f, 0.8f, 0), new Vector3(0.18f, 0.35f, 0.18f), yellow);
            return SavePrefab(root, path);
        }

        static GameObject Crate()
        {
            string path = $"{SourceModelFolder}/WoodCrate.prefab";
            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (existing != null) return existing;

            var tex = new Texture2D(256, 256, TextureFormat.RGBA32, true) { name = "WoodGrain" };
            for (int y = 0; y < 256; y++)
                for (int x = 0; x < 256; x++)
                {
                    float grain = Mathf.PerlinNoise(x * 0.02f, y * 0.25f) * 0.6f + Mathf.PerlinNoise(x * 0.15f, y * 0.9f) * 0.4f;
                    bool plankGap = y % 64 < 3;
                    var c = Color.Lerp(new Color(0.42f, 0.25f, 0.12f), new Color(0.72f, 0.5f, 0.28f), grain);
                    tex.SetPixel(x, y, plankGap ? new Color(0.2f, 0.12f, 0.06f) : c);
                }
            tex.Apply(true);
            AssetDatabase.CreateAsset(tex, $"{SourceModelFolder}/WoodGrain.asset");

            var wood = LitMaterial("Crate Wood", Color.white);
            wood.SetTexture("_BaseMap", tex);
            var metal = LitMaterial("Crate Metal", new Color(0.35f, 0.37f, 0.4f));
            metal.SetFloat("_Metallic", 0.9f);

            var root = new GameObject("WoodCrate");
            Part(root, PrimitiveType.Cube, new Vector3(0, 0.5f, 0), Vector3.one, wood);
            foreach (float y in new[] { 0.08f, 0.92f })
                Part(root, PrimitiveType.Cube, new Vector3(0, y, 0), new Vector3(1.04f, 0.08f, 1.04f), metal);
            return SavePrefab(root, path);
        }

        static GameObject Plant()
        {
            string path = $"{SourceModelFolder}/PottedPlant.prefab";
            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (existing != null) return existing;

            // Leaf texture: green ellipse on a transparent background.
            var tex = new Texture2D(128, 128, TextureFormat.RGBA32, true) { name = "LeafCutout", wrapMode = TextureWrapMode.Clamp };
            for (int y = 0; y < 128; y++)
                for (int x = 0; x < 128; x++)
                {
                    float u = (x - 64f) / 30f, v = (y - 64f) / 60f;
                    bool inside = u * u + v * v < 1f;
                    float vein = Mathf.Abs(x - 64f) < 2f ? 0.75f : 1f;
                    tex.SetPixel(x, y, inside ? new Color(0.15f * vein, 0.55f * vein, 0.18f * vein, 1f) : new Color(0, 0, 0, 0));
                }
            tex.Apply(true);
            AssetDatabase.CreateAsset(tex, $"{SourceModelFolder}/LeafCutout.asset");

            var leaf = LitMaterial("Plant Leaf (cutout)", Color.white);
            leaf.SetTexture("_BaseMap", tex);
            leaf.SetFloat("_AlphaClip", 1);
            leaf.SetFloat("_Cutoff", 0.5f);
            leaf.EnableKeyword("_ALPHATEST_ON");
            leaf.SetFloat("_Cull", (float)CullMode.Off);
            leaf.renderQueue = (int)RenderQueue.AlphaTest;
            var pot = LitMaterial("Plant Pot", new Color(0.72f, 0.33f, 0.2f));
            var soil = LitMaterial("Plant Soil", new Color(0.18f, 0.12f, 0.08f));

            var root = new GameObject("PottedPlant");
            Part(root, PrimitiveType.Cylinder, new Vector3(0, 0.3f, 0), new Vector3(0.6f, 0.3f, 0.6f), pot);
            Part(root, PrimitiveType.Cylinder, new Vector3(0, 0.58f, 0), new Vector3(0.52f, 0.02f, 0.52f), soil);
            for (int i = 0; i < 6; i++)
            {
                var q = Part(root, PrimitiveType.Quad, new Vector3(0, 1.05f, 0), new Vector3(0.45f, 0.9f, 1f), leaf);
                q.transform.rotation = Quaternion.Euler(-20f, i * 60f, 0f);
                q.transform.position += q.transform.up * 0.0f + Quaternion.Euler(0, i * 60f, 0) * Vector3.forward * 0.12f;
            }
            return SavePrefab(root, path);
        }

        static Material LitMaterial(string name, Color color)
        {
            string path = $"{SourceModelFolder}/{name}.mat";
            var m = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = name };
            m.SetColor("_BaseColor", color);
            m.SetFloat("_Smoothness", 0.55f);
            AssetDatabase.CreateAsset(m, path);
            return m;
        }

        static GameObject Part(GameObject root, PrimitiveType type, Vector3 pos, Vector3 scale, Material mat)
        {
            var go = GameObject.CreatePrimitive(type);
            Object.DestroyImmediate(go.GetComponent<Collider>());
            go.transform.SetParent(root.transform, false);
            go.transform.localPosition = pos;
            go.transform.localScale = scale;
            go.GetComponent<MeshRenderer>().sharedMaterial = mat;
            return go;
        }

        static GameObject SavePrefab(GameObject root, string path)
        {
            var prefab = PrefabUtility.SaveAsPrefabAsset(root, path);
            Object.DestroyImmediate(root);
            return prefab;
        }

        static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return;
            int slash = folder.LastIndexOf('/');
            EnsureFolder(folder.Substring(0, slash));
            AssetDatabase.CreateFolder(folder.Substring(0, slash), folder.Substring(slash + 1));
        }
    }
}
