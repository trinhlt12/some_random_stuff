using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace RockGen.Editor
{
    public sealed class RockGeneratorWindow : EditorWindow
    {
        public static readonly (string name, RockSettings settings)[] Presets =
        {
            ("Đá cuội", new RockSettings { seed = 7, size = new Vector3(0.5f, 0.32f, 0.42f), roughness = 0.15f, asymmetry = 0.3f, bottomFlatten = 0.35f, triangleBudget = 60 }),
            ("Đá dẹt", new RockSettings { seed = 21, size = new Vector3(1.4f, 0.35f, 1.1f), roughness = 0.5f, asymmetry = 0.45f, bottomFlatten = 0.8f, triangleBudget = 80 }),
            ("Đá cao", new RockSettings { seed = 42, size = new Vector3(0.7f, 1.8f, 0.6f), roughness = 0.6f, asymmetry = 0.6f, bottomFlatten = 0.7f, triangleBudget = 100 }),
        };

        [SerializeField] RockSettings settings = RockSettings.Default;
        [SerializeField] int batchCount = 9;
        [SerializeField] string saveFolder = RockAssets.DefaultSaveFolder;

        RockPreview preview;
        string status;
        Vector2 scroll;

        [MenuItem("Tools/Rock Generator")]
        public static void Open() => GetWindow<RockGeneratorWindow>("Rock Generator").minSize = new Vector2(340, 420);

        void OnEnable()
        {
            // Preview objects are not saved; drop any left over from before a script reload.
            RockPreview.RemoveOrphans();
            preview = new RockPreview();
        }

        void OnDisable() => preview?.Clear();

        void OnGUI()
        {
            scroll = EditorGUILayout.BeginScrollView(scroll);

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PrefixLabel("Preset");
                foreach (var p in Presets)
                    if (GUILayout.Button(p.name, EditorStyles.miniButton))
                    {
                        settings = p.settings;
                        Generate(1);
                    }
            }

            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                settings.seed = EditorGUILayout.IntField("Seed", settings.seed);
                if (GUILayout.Button("Randomize Seed", GUILayout.Width(110)))
                {
                    // Editor-side randomness only; never touches UnityEngine.Random.
                    settings.seed = new System.Random().Next(1, int.MaxValue);
                    Generate(1);
                }
            }
            settings.size = EditorGUILayout.Vector3Field(new GUIContent("Kích thước (X, Y, Z)", $"Unity units, {RockSettings.MinSize}–{RockSettings.MaxSize}"), settings.size);
            settings.roughness = EditorGUILayout.Slider(new GUIContent("Độ gồ ghề", "Số mặt cắt phẳng và độ lồi lõm"), settings.roughness, 0f, 1f);
            settings.asymmetry = EditorGUILayout.Slider(new GUIContent("Bất đối xứng", "Lệch khối và độ nghiêng"), settings.asymmetry, 0f, 1f);
            settings.bottomFlatten = EditorGUILayout.Slider(new GUIContent("Làm phẳng đáy", "0 = đáy tròn, 1 = đáy phẳng rộng"), settings.bottomFlatten, 0f, 1f);
            settings.triangleBudget = EditorGUILayout.IntSlider(new GUIContent("Ngân sách tam giác", "Số tam giác tối đa. Đá là khối lồi nên có thể dùng ít hơn."),
                settings.triangleBudget, RockSettings.MinTriangles, RockSettings.MaxTriangles);
            settings = settings.Clamped();

            EditorGUILayout.Space();
            if (GUILayout.Button("Generate Preview", GUILayout.Height(24))) Generate(1);
            using (new EditorGUILayout.HorizontalScope())
            {
                batchCount = Mathf.Clamp(EditorGUILayout.IntField("Số biến thể", batchCount), 2, 25);
                if (GUILayout.Button("Generate Batch", GUILayout.Width(120))) Generate(batchCount);
            }
            if (GUILayout.Button("Clear Preview"))
            {
                preview.Clear();
                status = "Đã xóa preview.";
            }

            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                saveFolder = EditorGUILayout.TextField("Thư mục lưu", saveFolder);
                if (GUILayout.Button("…", GUILayout.Width(26)))
                {
                    string picked = EditorUtility.OpenFolderPanel("Thư mục lưu", "Assets", "");
                    string root = Application.dataPath.Substring(0, Application.dataPath.Length - "Assets".Length);
                    if (!string.IsNullOrEmpty(picked) && picked.Replace('\\', '/').StartsWith(root))
                        saveFolder = picked.Replace('\\', '/').Substring(root.Length);
                }
            }
            using (new EditorGUI.DisabledScope(preview.Rocks.Count == 0))
                if (GUILayout.Button("Save Selected as Assets", GUILayout.Height(24)))
                    SaveSelected();

            EditorGUILayout.Space();
            if (preview.Rocks.Count > 0)
            {
                string each = preview.Rocks.Count > 1 ? $" ({preview.Rocks.Count} hòn)" : "";
                EditorGUILayout.LabelField("Đỉnh / tam giác" + each, $"{preview.VertexCount} / {preview.TriangleCount}");
            }
            EditorGUILayout.HelpBox(
                "Chọn hòn đá trong Scene/Hierarchy (dưới [RockGen Preview]) rồi bấm Save. Preview không được lưu vào scene " +
                "và sẽ bị xóa khi đóng cửa sổ hoặc khi Unity compile lại script.", MessageType.None);
            if (!string.IsNullOrEmpty(status)) EditorGUILayout.HelpBox(status, MessageType.Info);

            EditorGUILayout.EndScrollView();
        }

        void Generate(int count)
        {
            var list = new List<RockSettings>();
            for (int i = 0; i < count; i++)
            {
                var s = settings;
                s.seed = settings.seed + i;
                list.Add(s);
            }
            try
            {
                preview.Show(list);
            }
            catch (System.InvalidOperationException e)
            {
                preview.Clear();
                status = e.Message;
                return;
            }
            Selection.activeGameObject = count == 1 ? preview.Rocks[0] : null;
            status = count == 1 ? $"Đã tạo Rock_{settings.seed}." : $"Đã tạo {count} biến thể (seed {settings.seed}–{settings.seed + count - 1}).";
            SceneView.RepaintAll();
        }

        void SaveSelected()
        {
            var targets = new List<GameObject>();
            foreach (var go in Selection.gameObjects)
                if (preview.Owns(go)) targets.Add(go);
            if (targets.Count == 0 && preview.Rocks.Count == 1) targets.Add(preview.Rocks[0]);
            if (targets.Count == 0)
            {
                status = "Chưa chọn hòn đá nào trong preview.";
                return;
            }

            string last = null;
            foreach (var go in targets)
            {
                var mesh = go.GetComponent<MeshFilter>().sharedMesh;
                last = RockAssets.Save(mesh, go.name, saveFolder).prefabPath;
            }
            status = $"Đã lưu {targets.Count} prefab (+ mesh .asset) vào {saveFolder}.";
            EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<GameObject>(last));
        }
    }
}
