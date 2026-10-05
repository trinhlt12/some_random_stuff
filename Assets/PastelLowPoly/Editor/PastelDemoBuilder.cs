using System.Collections.Generic;
using LowPolyTools;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace PastelLowPoly.Editor
{
    /// <summary>
    /// Creates the pastel material presets and a self-contained demo scene. Nothing outside
    /// Assets/PastelLowPoly is modified; the Styloo model is only read and copied.
    /// </summary>
    public static class PastelDemoBuilder
    {
        public const string Root = "Assets/PastelLowPoly";
        public const string MaterialsFolder = Root + "/Materials";
        public const string DemoFolder = Root + "/Demo";
        public const string DemoMeshFolder = DemoFolder + "/Meshes";
        public const string DemoMaterialFolder = DemoFolder + "/Materials";
        public const string DemoModelFolder = DemoFolder + "/LowPolyModels";
        public const string ScenePath = DemoFolder + "/PastelLowPolyDemo.unity";
        public const string ShaderName = "Pastel/Low Poly Pastel";

        const string SourceModel = "Assets/StylooClassroomAssetPack GLTF & FBX/classroom/GLTF/chairtable.glb";

        static readonly (string name, Color color)[] Presets =
        {
            ("Peach Pink", Hex("#F9BCAA")),
            ("Coral Blush", Hex("#F5A397")),
            ("Cream Yellow", Hex("#FBE3A0")),
            ("Mint Green", Hex("#A9E2C3")),
            ("Pastel Teal", Hex("#86CBCB")),
            ("Lavender", Hex("#C3B3EA")),
            ("Muted Plum", Hex("#8E6A8E")),
            ("Ivory Floor", Hex("#F6EDE0")),
        };

        [MenuItem("Tools/Pastel Low Poly/Create Material Presets")]
        public static void CreateMaterialPresets()
        {
            EnsureFolder(MaterialsFolder);
            foreach (var preset in Presets) GetOrCreatePreset(preset.name, preset.color);
            AssetDatabase.SaveAssets();
        }

        [MenuItem("Tools/Pastel Low Poly/Open Demo Scene")]
        public static void OpenDemoScene()
        {
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath) == null)
            {
                BuildDemoScene();
                return;
            }
            if (EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        }

        [MenuItem("Tools/Pastel Low Poly/Rebuild Demo Scene")]
        public static void BuildDemoScene()
        {
            if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;

            CreateMaterialPresets();
            var mats = new Dictionary<string, Material>();
            foreach (var preset in Presets)
                mats[preset.name] = AssetDatabase.LoadAssetAtPath<Material>($"{MaterialsFolder}/{preset.name}.mat");

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            SetupEnvironment();

            var meshes = CreateDemoMeshes();
            var cube = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            var sphere = Resources.GetBuiltinResource<Mesh>("New-Sphere.fbx");

            // Floor
            Box("Floor", null, new Vector3(0, -0.1f, 0), new Vector3(14, 0.2f, 12), mats["Ivory Floor"], cube);

            // Desk with a simple monitor, all built from boxes and low-sided cylinders.
            var desk = new GameObject("Desk").transform;
            desk.position = new Vector3(-2.2f, 0, 0.6f);
            desk.rotation = Quaternion.Euler(0, 12, 0);
            Box("Top", desk, new Vector3(0, 0.92f, 0), new Vector3(2.6f, 0.14f, 1.3f), mats["Peach Pink"], cube);
            foreach (var x in new[] { -1.15f, 1.15f })
                foreach (var z in new[] { -0.5f, 0.5f })
                    Box("Leg", desk, new Vector3(x, 0.425f, z), new Vector3(0.14f, 0.85f, 0.14f), mats["Coral Blush"], cube);
            Box("Monitor Base", desk, new Vector3(0.2f, 1.02f, 0.25f), new Vector3(0.5f, 0.06f, 0.32f), mats["Muted Plum"], cube);
            Place("Monitor Neck", desk, meshes["Cylinder7"], new Vector3(0.2f, 1.25f, 0.28f), Quaternion.identity, new Vector3(0.09f, 0.4f, 0.09f), mats["Lavender"]);
            Box("Monitor Shell", desk, new Vector3(0.2f, 1.75f, 0.3f), new Vector3(1.3f, 0.8f, 0.1f), mats["Pastel Teal"], cube);
            Box("Monitor Screen", desk, new Vector3(0.2f, 1.75f, 0.245f), new Vector3(1.16f, 0.66f, 0.02f), mats["Cream Yellow"], cube);
            Box("Keyboard", desk, new Vector3(0.1f, 1.01f, -0.3f), new Vector3(0.9f, 0.04f, 0.28f), mats["Mint Green"], cube);
            Place("Mug", desk, meshes["Cylinder7"], new Vector3(-0.85f, 1.1f, -0.15f), Quaternion.identity, new Vector3(0.22f, 0.22f, 0.22f), mats["Muted Plum"]);

            // Shape checks
            var tests = new GameObject("Shading Tests").transform;
            Place("Cube (face tones)", tests, cube, new Vector3(1.0f, 0.5f, -1.6f), Quaternion.Euler(0, 30, 0), Vector3.one, mats["Cream Yellow"]);
            Place("Cylinder 7 sides", tests, meshes["Cylinder7"], new Vector3(2.5f, 0.6f, -1.6f), Quaternion.identity, new Vector3(0.9f, 1.2f, 0.9f), mats["Mint Green"]);
            Place("Icosphere (many faces)", tests, meshes["Icosphere"], new Vector3(4.0f, 0.6f, -1.6f), Quaternion.Euler(10, 20, 0), Vector3.one * 1.2f, mats["Lavender"]);

            var smoothMat = DemoMaterial("Lavender (Mesh Normals)", mats["Lavender"], m => SetNormalMode(m, flat: false));
            Place("Sphere smooth - Mesh normals", tests, sphere, new Vector3(1.6f, 0.6f, 0.4f), Quaternion.identity, Vector3.one * 1.2f, smoothMat);
            Place("Sphere smooth - Flat shader", tests, sphere, new Vector3(3.2f, 0.6f, 0.4f), Quaternion.identity, Vector3.one * 1.2f, mats["Lavender"]);

            // Non-uniform scale in both normal modes.
            var squashedMesh = DemoMaterial("Pastel Teal (Mesh Normals)", mats["Pastel Teal"], m => SetNormalMode(m, flat: false));
            Place("Non-uniform scale - Mesh normals", tests, sphere, new Vector3(4.9f, 0.35f, 0.4f), Quaternion.Euler(0, 0, 25), new Vector3(1.4f, 0.5f, 0.7f), squashedMesh);
            Place("Non-uniform scale - Flat", tests, meshes["Cylinder7"], new Vector3(4.9f, 0.5f, 2.0f), Quaternion.Euler(0, 30, 20), new Vector3(1.6f, 0.5f, 0.6f), mats["Pastel Teal"]);

            // Floating object: casts a shadow on the floor and receives the desk/monitor light.
            Place("Floating Icosphere (shadow check)", tests, meshes["Icosphere"], new Vector3(1.6f, 1.7f, 2.8f), Quaternion.Euler(0, 15, 0), Vector3.one * 0.8f, mats["Peach Pink"]);

            // Option checks: vertex colours (with and without colour data) and base map.
            var vcMat = DemoMaterial("Vertex Color On", mats["Ivory Floor"], m =>
            {
                m.SetColor("_BaseColor", Color.white);
                m.SetFloat("_UseVertexColor", 1);
                m.EnableKeyword("_USE_VERTEX_COLOR");
            });
            Place("Vertex colors - mesh with colors", tests, meshes["IcosphereColored"], new Vector3(-0.6f, 0.6f, -2.6f), Quaternion.identity, Vector3.one * 1.1f, vcMat);
            Place("Vertex colors - mesh without colors", tests, cube, new Vector3(-2.0f, 0.4f, -2.6f), Quaternion.Euler(0, 20, 0), Vector3.one * 0.8f, vcMat);

            var baseMapMat = DemoMaterial("Base Map On", mats["Cream Yellow"], m =>
            {
                m.SetFloat("_UseBaseMap", 1);
                m.EnableKeyword("_USE_BASE_MAP");
                m.SetTexture("_BaseMap", DemoCheckerTexture());
            });
            Place("Base map - checker", tests, cube, new Vector3(-3.4f, 0.4f, -2.6f), Quaternion.Euler(0, -15, 0), Vector3.one * 0.8f, baseMapMat);

            AddStylooComparison();

            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.SaveAssets();
            Debug.Log($"Pastel demo scene saved to {ScenePath}");
        }

        public static void SetupEnvironment(float orthographicSize = 4.6f, Vector3? focus = null)
        {
            var cream = Hex("#FBF5EA");

            var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
            var cam = camGo.AddComponent<Camera>();
            cam.orthographic = true;
            cam.orthographicSize = orthographicSize;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = cream;
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = 80f;
            camGo.transform.rotation = Quaternion.Euler(35f, -40f, 0f);
            camGo.transform.position = (focus ?? new Vector3(0.6f, 0.4f, 0.2f)) - camGo.transform.forward * 30f;

            var lightGo = new GameObject("Directional Light (warm)");
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.color = Hex("#FFF1DC");
            light.intensity = 1.0f;
            light.shadows = LightShadows.Soft;
            light.shadowStrength = 1f;
            lightGo.transform.rotation = Quaternion.Euler(52f, -62f, 0f);

            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = Hex("#E9E1EC");
            RenderSettings.fog = false;
            RenderSettings.skybox = null;
            RenderSettings.sun = light;
        }

        static void AddStylooComparison()
        {
            var source = AssetDatabase.LoadAssetAtPath<GameObject>(SourceModel);
            if (source == null)
            {
                Debug.LogWarning($"Không tìm thấy {SourceModel}; bỏ qua phần so sánh model Styloo.");
                return;
            }

            var group = new GameObject("Styloo chairtable comparison").transform;
            group.position = new Vector3(-5.6f, 0, 4.2f);

            var original = (GameObject)PrefabUtility.InstantiatePrefab(source);
            original.name = "Original (source materials, untouched)";
            original.transform.SetParent(group, false);
            original.transform.localPosition = new Vector3(0, 0, 0);

            // Low-poly copy made with the converter, then pastel materials that keep the palette colours.
            var copy = (GameObject)PrefabUtility.InstantiatePrefab(PastelModelsDemoBuilder.LowPolyPastelPrefab(source));
            copy.name = "Low-poly copy (pastel shader)";
            copy.transform.SetParent(group, false);
            copy.transform.localPosition = new Vector3(2.6f, 0, 0);
        }

        // ------------------------------------------------------------------------------------

        static Material GetOrCreatePreset(string name, Color color)
        {
            string path = $"{MaterialsFolder}/{name}.mat";
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null) return existing; // never overwrite user tweaks

            var shader = Shader.Find(ShaderName);
            var m = new Material(shader) { name = name };
            m.SetColor("_BaseColor", color);
            SetNormalMode(m, flat: true);
            AssetDatabase.CreateAsset(m, path);
            return m;
        }

        static Material DemoMaterial(string name, Material template, System.Action<Material> configure)
        {
            EnsureFolder(DemoMaterialFolder);
            string path = $"{DemoMaterialFolder}/{name}.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m != null) return m;
            m = new Material(template) { name = name };
            configure(m);
            AssetDatabase.CreateAsset(m, path);
            return m;
        }

        public static void SetNormalMode(Material m, bool flat)
        {
            m.SetFloat("_NormalMode", flat ? 1 : 0);
            if (flat)
            {
                m.EnableKeyword("_NORMALMODE_FLAT");
                m.DisableKeyword("_NORMALMODE_MESH");
            }
            else
            {
                m.EnableKeyword("_NORMALMODE_MESH");
                m.DisableKeyword("_NORMALMODE_FLAT");
            }
        }

        static Texture2D DemoCheckerTexture()
        {
            EnsureFolder(DemoMaterialFolder);
            string path = $"{DemoMaterialFolder}/Checker.asset";
            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (tex != null) return tex;
            tex = new Texture2D(8, 8, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, name = "Checker" };
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 8; x++)
                    tex.SetPixel(x, y, ((x + y) & 1) == 0 ? Color.white : Hex("#C9B8E8"));
            tex.Apply();
            AssetDatabase.CreateAsset(tex, path);
            return tex;
        }

        static Dictionary<string, Mesh> CreateDemoMeshes()
        {
            EnsureFolder(DemoMeshFolder);
            return new Dictionary<string, Mesh>
            {
                ["Cylinder7"] = SaveMesh(DemoMeshes.Cylinder(7), "Cylinder7"),
                ["Icosphere"] = SaveMesh(DemoMeshes.Icosphere(1, false), "Icosphere"),
                ["IcosphereColored"] = SaveMesh(DemoMeshes.Icosphere(1, true), "IcosphereColored"),
            };
        }

        static Mesh SaveMesh(Mesh mesh, string name)
        {
            string path = $"{DemoMeshFolder}/{name}.asset";
            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (existing != null) return existing;
            mesh.name = name;
            AssetDatabase.CreateAsset(mesh, path);
            return mesh;
        }

        static GameObject Box(string name, Transform parent, Vector3 position, Vector3 size, Material mat, Mesh cube) =>
            Place(name, parent, cube, position, Quaternion.identity, size, mat);

        static GameObject Place(string name, Transform parent, Mesh mesh, Vector3 localPosition, Quaternion localRotation,
            Vector3 localScale, Material mat)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;
            go.transform.localRotation = localRotation;
            go.transform.localScale = localScale;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = ShadowCastingMode.On;
            r.receiveShadows = true;
            return go;
        }

        static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return;
            int slash = folder.LastIndexOf('/');
            EnsureFolder(folder.Substring(0, slash));
            AssetDatabase.CreateFolder(folder.Substring(0, slash), folder.Substring(slash + 1));
        }

        static Color Hex(string hex)
        {
            ColorUtility.TryParseHtmlString(hex, out var c);
            return c;
        }
    }
}
