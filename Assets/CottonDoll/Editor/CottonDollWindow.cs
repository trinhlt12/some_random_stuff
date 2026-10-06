using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CottonDoll.Editor
{
    /// <summary>Tools > Cotton Doll > Generator: edit the doll, preview it in the scene and save it as a prefab.</summary>
    public sealed class CottonDollWindow : EditorWindow
    {
        const string PrefsKey = "CottonDoll.Settings";
        const string FolderKey = "CottonDoll.SaveFolder";

        CottonDollSettings settings;
        SerializedObject serialized;
        List<DollPart> lastParts;
        double lastGenerateMs;
        bool autoUpdate;
        bool pendingUpdate;
        double pendingAt;
        Vector2 scroll;
        string saveFolder = "Assets/CottonDoll/Generated";
        string saveName = "CottonDoll";
        Vector3 previewPosition;
        bool hasPreviewPosition;
        readonly Dictionary<string, bool> foldouts = new Dictionary<string, bool>();

        static readonly (string title, string[] fields)[] Groups =
        {
            ("Body", new[] { "height", "headSize", "headFlatness", "earSize", "torsoWidth", "torsoDepth", "armLength", "armThickness", "legLength", "legThickness", "handSize", "footSize" }),
            ("Hair", new[] { "hairVolume", "hairLength", "bangLength" }),
            ("Outfit", new[] { "shirtLooseness", "shirtLength", "sleeveLength", "shortsLength", "shoeSize", "clothesGapPercent" }),
            ("Face", new[] { "faceFeatures" }),
            ("Colours", new[] { "skinColor", "hairColor", "shirtColor", "shortsColor", "shoeColor", "faceColor" }),
            ("Parts", new[] { "showBody", "showHair", "showShirt", "showShorts", "showShoes" }),
        };

        [MenuItem("Tools/Cotton Doll/Generator")]
        public static void Open() => GetWindow<CottonDollWindow>("Cotton Doll");

        void OnEnable()
        {
            settings = CreateInstance<CottonDollSettings>();
            settings.hideFlags = HideFlags.DontSave;
            string json = EditorPrefs.GetString(PrefsKey, "");
            if (!string.IsNullOrEmpty(json)) JsonUtility.FromJsonOverwrite(json, settings);
            settings.Sanitize();
            serialized = new SerializedObject(settings);
            saveFolder = EditorPrefs.GetString(FolderKey, saveFolder);
            Undo.undoRedoPerformed += OnUndoRedo;
        }

        void OnDisable()
        {
            Undo.undoRedoPerformed -= OnUndoRedo;
            if (settings != null)
            {
                EditorPrefs.SetString(PrefsKey, JsonUtility.ToJson(settings));
                DestroyImmediate(settings);
            }
            EditorPrefs.SetString(FolderKey, saveFolder);
        }

        void OnUndoRedo()
        {
            serialized?.Update();
            Repaint();
        }

        void Update()
        {
            if (pendingUpdate && EditorApplication.timeSinceStartup >= pendingAt)
            {
                pendingUpdate = false;
                if (CottonDollAssets.FindPreview(SceneManager.GetActiveScene()) != null) Generate();
            }
        }

        void OnGUI()
        {
            serialized.Update();
            scroll = EditorGUILayout.BeginScrollView(scroll);

            EditorGUILayout.HelpBox("Static cotton doll (no rig yet). Pivot between the feet, facing +Z, Y up. " +
                                    "Sizes are multipliers of the reference proportions; ranges are limited to shapes that hold up.",
                                    MessageType.None);

            EditorGUI.BeginChangeCheck();
            foreach (var (title, fields) in Groups)
            {
                foldouts.TryGetValue(title, out bool open);
                if (!foldouts.ContainsKey(title)) open = title != "Colours" && title != "Parts";
                open = EditorGUILayout.Foldout(open, title, true, EditorStyles.foldoutHeader);
                foldouts[title] = open;
                if (!open) continue;
                EditorGUI.indentLevel++;
                foreach (var f in fields) EditorGUILayout.PropertyField(serialized.FindProperty(f));
                EditorGUI.indentLevel--;
            }
            bool changed = EditorGUI.EndChangeCheck();
            serialized.ApplyModifiedProperties();
            if (changed && autoUpdate)
            {
                pendingUpdate = true;
                pendingAt = EditorApplication.timeSinceStartup + 0.35;
            }

            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Generate / Update Preview", GUILayout.Height(26))) Generate();
                if (GUILayout.Button("Reset to Default", GUILayout.Height(26)))
                {
                    Undo.RecordObject(settings, "Reset Cotton Doll");
                    settings.ResetToDefaults();
                    serialized.Update();
                    if (autoUpdate) Generate();
                }
            }
            autoUpdate = EditorGUILayout.ToggleLeft("Update preview automatically after changes (each update takes a few seconds)", autoUpdate);
            using (new EditorGUILayout.HorizontalScope())
            {
                previewPosition = EditorGUILayout.Vector3Field("Preview Position", previewPosition);
                if (GUILayout.Button("In Front of Camera", GUILayout.Width(130))) { previewPosition = InFrontOfCamera(); hasPreviewPosition = true; MovePreview(); }
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Clear Preview")) CottonDollAssets.ClearPreview(SceneManager.GetActiveScene());
                if (GUILayout.Button("Open Preview Scene")) CottonDollAssets.OpenPreviewScene(settings);
            }

            DrawStats();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Save", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                saveFolder = EditorGUILayout.TextField("Folder", saveFolder);
                if (GUILayout.Button("…", GUILayout.Width(28)))
                {
                    string abs = EditorUtility.SaveFolderPanel("Save Cotton Doll In", saveFolder, "");
                    if (!string.IsNullOrEmpty(abs))
                    {
                        string dataPath = Application.dataPath.Replace('\\', '/');
                        abs = abs.Replace('\\', '/');
                        if (abs == dataPath || abs.StartsWith(dataPath + "/")) saveFolder = "Assets" + abs.Substring(dataPath.Length);
                        else EditorUtility.DisplayDialog("Cotton Doll", "Choose a folder inside this project's Assets folder.", "OK");
                    }
                }
            }
            saveName = EditorGUILayout.TextField("Name", saveName);
            if (!CottonDollAssets.IsValidFolder(saveFolder)) EditorGUILayout.HelpBox("The folder must be inside Assets.", MessageType.Warning);
            if (!CottonDollAssets.IsValidName(saveName)) EditorGUILayout.HelpBox("Invalid name.", MessageType.Warning);
            using (new EditorGUI.DisabledScope(!CottonDollAssets.IsValidFolder(saveFolder) || !CottonDollAssets.IsValidName(saveName)))
                if (GUILayout.Button("Save as Assets (meshes, materials, prefab)", GUILayout.Height(26))) Save();

            EditorGUILayout.EndScrollView();
        }

        void DrawStats()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Statistics", EditorStyles.boldLabel);
            if (lastParts == null)
            {
                EditorGUILayout.LabelField("Generate to see the numbers.");
                return;
            }
            foreach (var p in lastParts)
                EditorGUILayout.LabelField(p.name, $"{p.data.VertexCount} vertices, {p.data.TriangleCount} triangles  ({p.slot})");
            int tris = CottonDollGenerator.TriangleCount(lastParts);
            int mats = lastParts.Select(p => p.slot).Distinct().Count();
            EditorGUILayout.LabelField("Total", $"{CottonDollGenerator.VertexCount(lastParts)} vertices, {tris} triangles, {mats} materials");
            EditorGUILayout.LabelField("Generated In", $"{lastGenerateMs:0} ms");
            if (tris > 6000) EditorGUILayout.HelpBox("Above the 6000-triangle target.", MessageType.Warning);
        }

        Vector3 InFrontOfCamera()
        {
            var cam = Camera.main;
            if (cam == null) return Vector3.zero;
            Vector3 fwd = cam.transform.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            fwd.Normalize();
            Vector3 p = cam.transform.position + fwd * Mathf.Max(2f, settings.height * 3f);
            p.y = 0f;
            return p;
        }

        Quaternion FacingCamera(Vector3 position)
        {
            var cam = Camera.main;
            if (cam == null) return Quaternion.identity;
            Vector3 to = cam.transform.position - position;
            to.y = 0f;
            return to.sqrMagnitude < 1e-4f ? Quaternion.identity : Quaternion.LookRotation(to.normalized, Vector3.up);
        }

        void MovePreview()
        {
            var root = CottonDollAssets.FindPreview(SceneManager.GetActiveScene());
            if (root == null) return;
            Undo.RecordObject(root.transform, "Move Cotton Doll Preview");
            root.transform.SetPositionAndRotation(previewPosition, FacingCamera(previewPosition));
        }

        void Generate()
        {
            if (Application.isPlaying)
            {
                ShowNotification(new GUIContent("Exit Play Mode to generate"));
                return;
            }
            if (!hasPreviewPosition)
            {
                previewPosition = InFrontOfCamera();
                hasPreviewPosition = true;
            }
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                EditorUtility.DisplayProgressBar("Cotton Doll", "Building meshes (a few seconds)…", 0.5f);
                lastParts = CottonDollGenerator.Generate(settings);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
            lastGenerateMs = watch.Elapsed.TotalMilliseconds;
            serialized.Update();
            CottonDollAssets.CreateOrUpdatePreview(settings, lastParts, SceneManager.GetActiveScene(), previewPosition, FacingCamera(previewPosition));
            SceneView.RepaintAll();
            Repaint();
        }

        void Save()
        {
            var parts = lastParts ?? CottonDollGenerator.Generate(settings);
            if (lastParts == null) lastParts = parts;
            var existing = CottonDollAssets.ExistingPaths(saveFolder, saveName, parts);
            bool overwrite = false;
            if (existing.Count > 0)
            {
                int choice = EditorUtility.DisplayDialogComplex("Cotton Doll",
                    $"{existing.Count} asset(s) named \"{saveName}\" already exist in {saveFolder}:\n" +
                    string.Join("\n", existing.Take(6)) + (existing.Count > 6 ? "\n…" : "") +
                    "\n\nOverwrite them (prefab and assets keep their GUIDs), or save with a new name?",
                    "Overwrite", "Cancel", "Save as New Name");
                if (choice == 1) return;
                if (choice == 2)
                {
                    int n = 2;
                    while (CottonDollAssets.ExistingPaths(saveFolder, $"{saveName}_{n}", parts).Count > 0) n++;
                    saveName = $"{saveName}_{n}";
                }
                else overwrite = true;
            }
            var prefab = CottonDollAssets.SaveAsAssets(settings, parts, saveFolder, saveName, overwrite);
            if (prefab != null)
            {
                EditorGUIUtility.PingObject(prefab);
                ShowNotification(new GUIContent($"Saved {saveName}.prefab"));
            }
        }
    }
}
