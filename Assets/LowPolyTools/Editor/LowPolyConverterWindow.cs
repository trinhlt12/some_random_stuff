using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace LowPolyTools
{
    public sealed class LowPolyConverterWindow : EditorWindow
    {
        struct Preset
        {
            public string label;
            public float ratio;
            public float maxError;
        }

        static readonly Preset[] Presets =
        {
            new Preset { label = "Nhẹ", ratio = 0.75f, maxError = 0.005f },
            new Preset { label = "Vừa", ratio = 0.5f, maxError = 0.01f },
            new Preset { label = "Mạnh", ratio = 0.25f, maxError = 0.025f },
        };

        [SerializeField] GameObject source;
        [SerializeField] LowPolySettings settings = new LowPolySettings();
        [SerializeField] Vector2 orbit = new Vector2(135f, 25f);
        [SerializeField] bool wireframe = true;

        LowPolyAnalysis analysis;
        Dictionary<Mesh, MeshConversionResult> results;
        bool resultsStale;
        string lastExport;
        Vector2 scroll;
        LowPolyPreview previewOriginal;
        LowPolyPreview previewResult;

        [MenuItem("Tools/Low Poly/Low Poly Converter")]
        public static void Open()
        {
            var window = GetWindow<LowPolyConverterWindow>("Low Poly Converter");
            if (Selection.activeGameObject != null) window.SetSource(Selection.activeGameObject);
            window.minSize = new Vector2(420, 560);
        }

        void OnEnable()
        {
            if (source != null) analysis = LowPolyConverter.Analyze(source);
        }

        void OnDisable()
        {
            ClearResults();
            previewOriginal?.Dispose();
            previewResult?.Dispose();
            previewOriginal = previewResult = null;
        }

        void SetSource(GameObject go)
        {
            source = go;
            ClearResults();
            lastExport = null;
            analysis = go != null ? LowPolyConverter.Analyze(go) : null;
        }

        void ClearResults()
        {
            LowPolyConverter.DestroyResults(results);
            results = null;
            resultsStale = false;
        }

        void OnGUI()
        {
            scroll = EditorGUILayout.BeginScrollView(scroll);

            EditorGUI.BeginChangeCheck();
            var picked = (GameObject)EditorGUILayout.ObjectField("GameObject / Prefab", source, typeof(GameObject), true);
            if (EditorGUI.EndChangeCheck()) SetSource(picked);

            if (source == null)
            {
                EditorGUILayout.HelpBox("Chọn một GameObject trong scene hoặc một prefab/model trong Project.", MessageType.Info);
                EditorGUILayout.EndScrollView();
                return;
            }

            DrawAnalysis();
            DrawSettings();
            DrawActions();
            DrawStats();
            DrawPreviews();

            EditorGUILayout.EndScrollView();
        }

        void DrawAnalysis()
        {
            if (analysis == null) return;
            EditorGUILayout.Space();
            EditorGUILayout.LabelField(
                $"Mesh tĩnh hỗ trợ: {analysis.entries.Count} renderer, {analysis.uniqueMeshes.Count} mesh riêng biệt",
                EditorStyles.boldLabel);

            foreach (var issue in analysis.skipped)
                EditorGUILayout.HelpBox($"Bỏ qua '{issue.path}': {issue.message}", MessageType.Warning);
            foreach (var note in analysis.notes)
                EditorGUILayout.HelpBox(note, MessageType.None);
        }

        void DrawSettings()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Thao tác", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();

            settings.simplify = EditorGUILayout.ToggleLeft("Simplify mesh (giảm số tam giác)", settings.simplify);
            using (new EditorGUI.DisabledScope(!settings.simplify))
            using (new EditorGUI.IndentLevelScope())
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.PrefixLabel("Preset");
                    foreach (var preset in Presets)
                        if (GUILayout.Button(preset.label, EditorStyles.miniButton))
                        {
                            settings.targetRatio = preset.ratio;
                            settings.maxError = preset.maxError;
                            GUI.changed = true;
                        }
                }

                settings.targetRatio = EditorGUILayout.Slider(
                    new GUIContent("Tỉ lệ tam giác giữ lại", "Mục tiêu. Công cụ dừng sớm hơn nếu đi tiếp sẽ phá hình dạng hoặc vi phạm ràng buộc."),
                    settings.targetRatio, 0.02f, 1f);
                settings.maxError = EditorGUILayout.Slider(
                    new GUIContent("Sai số tối đa (% kích thước)", "Khoảng lệch hình học lớn nhất cho phép, tính theo % đường chéo bounding box của mesh."),
                    settings.maxError * 100f, 0.1f, 10f) / 100f;
                settings.preserveBorders = EditorGUILayout.Toggle(
                    new GUIContent("Giữ biên hở", "Khóa các đỉnh nằm trên biên hở (lỗ, mép tấm phẳng) để giữ nguyên đường viền."),
                    settings.preserveBorders);
                settings.protectSeams = EditorGUILayout.Toggle(
                    new GUIContent("Bảo vệ UV seam & ranh giới material", "Bật: khóa các đỉnh trên UV seam, seam màu và ranh giới submesh. Tắt: các đỉnh đó vẫn chỉ được trượt dọc seam, không bao giờ cắt ngang seam."),
                    settings.protectSeams);
                using (new EditorGUI.DisabledScope(settings.flatNormals))
                    settings.recalculateNormals = EditorGUILayout.Toggle(
                        new GUIContent("Tính lại smooth normals", "Sau khi giảm, tính lại normal theo các mặt mới để tránh vệt loang trên mặt phẳng lớn. Cạnh cứng (hard edge) gốc vẫn được giữ. Tắt để giữ nguyên normal gốc của các đỉnh còn lại."),
                        settings.recalculateNormals);
                EditorGUILayout.HelpBox(
                    "Tỉ lệ giữ lại là mục tiêu, không phải cam kết: công cụ dừng sớm nếu đi tiếp sẽ vượt sai số tối đa, " +
                    "phá biên, seam hoặc làm lật/chồng mặt. Thuật toán chỉ gộp cạnh (QEM), không thiết kế lại hình dáng vật thể.",
                    MessageType.Info);
            }

            settings.flatNormals = EditorGUILayout.ToggleLeft("Flat normals (shading phẳng theo từng mặt)", settings.flatNormals);
            if (settings.flatNormals)
                EditorGUILayout.HelpBox(
                    "Flat normals tách đỉnh ở các cạnh giữa hai mặt khác hướng: số tam giác không đổi nhưng số vertex thường tăng.",
                    MessageType.None);

            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                settings.outputFolder = EditorGUILayout.TextField("Thư mục xuất", settings.outputFolder);
                if (GUILayout.Button("…", GUILayout.Width(28)))
                {
                    string picked = EditorUtility.OpenFolderPanel("Thư mục xuất", "Assets", "");
                    string projectRoot = Application.dataPath.Substring(0, Application.dataPath.Length - "Assets".Length);
                    if (!string.IsNullOrEmpty(picked) && picked.Replace('\\', '/').StartsWith(projectRoot))
                        settings.outputFolder = picked.Replace('\\', '/').Substring(projectRoot.Length);
                }
            }

            if (EditorGUI.EndChangeCheck() && results != null) resultsStale = true;
        }

        void DrawActions()
        {
            bool nothingToDo = !settings.simplify && !settings.flatNormals;
            bool noMeshes = analysis == null || analysis.entries.Count == 0;
            if (nothingToDo)
                EditorGUILayout.HelpBox("Bật ít nhất một thao tác.", MessageType.Warning);

            using (new EditorGUI.DisabledScope(nothingToDo || noMeshes))
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Preview", GUILayout.Height(26)))
                {
                    RunConversion();
                    GUIUtility.ExitGUI();
                }
                if (GUILayout.Button("Xuất mesh + prefab mới", GUILayout.Height(26)))
                {
                    if (results == null || resultsStale) RunConversion();
                    if (results != null) DoExport();
                    GUIUtility.ExitGUI();
                }
            }

            if (resultsStale)
                EditorGUILayout.HelpBox("Thông số đã thay đổi; bấm Preview để cập nhật kết quả.", MessageType.Warning);
            if (!string.IsNullOrEmpty(lastExport))
                EditorGUILayout.HelpBox(lastExport, MessageType.Info);
        }

        void RunConversion()
        {
            ClearResults();
            analysis = LowPolyConverter.Analyze(source);
            try
            {
                results = LowPolyConverter.Convert(analysis, settings,
                    (label, f) => EditorUtility.DisplayCancelableProgressBar("Low Poly Converter", label, f));
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
            if (results == null) ShowNotification(new GUIContent("Đã hủy. Không có gì được ghi ra đĩa."));
        }

        void DoExport()
        {
            try
            {
                var export = LowPolyConverter.Export(analysis, results, settings);
                lastExport = $"Đã tạo {(export.isVariant ? "prefab variant" : "prefab mới")}: {export.prefabPath}\n" +
                             $"{export.meshPaths.Count} mesh asset trong cùng thư mục. Model nguồn không bị thay đổi.";
                EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<GameObject>(export.prefabPath));
            }
            catch (Exception e)
            {
                lastExport = null;
                Debug.LogException(e);
                EditorUtility.DisplayDialog("Low Poly Converter", "Xuất thất bại, các asset vừa tạo đã được xóa.\n" + e.Message, "OK");
            }
        }

        void DrawStats()
        {
            if (results == null || results.Count == 0) return;

            int vb = 0, va = 0, tb = 0, ta = 0;
            foreach (var entry in analysis.entries)
            {
                if (!results.TryGetValue(entry.mesh, out var r)) continue;
                vb += r.verticesBefore; va += r.verticesAfter;
                tb += r.trianglesBefore; ta += r.trianglesAfter;
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Thống kê (cộng theo renderer)", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Triangles", $"{tb:N0} → {ta:N0}  ({Percent(tb, ta)})");
            EditorGUILayout.LabelField("Vertices", $"{vb:N0} → {va:N0}  ({Percent(vb, va)})");

            foreach (var r in results.Values)
            {
                string line = $"{r.source.name} ×{analysis.UsersOf(r.source)}: tris {r.trianglesBefore}→{r.trianglesAfter} ({Percent(r.trianglesBefore, r.trianglesAfter)}), " +
                              $"verts {r.verticesBefore}→{r.verticesAfter}";
                if (r.simplify != null) line += $"\n  Simplify: {r.simplify.StopReason}; mục tiêu {r.simplify.targetTriangles} tris";
                if (r.flatNormals != null && r.flatNormals.degenerateTriangles > 0)
                    line += $"\n  Flat normals: {r.flatNormals.degenerateTriangles} tam giác suy biến giữ normal gốc";
                EditorGUILayout.LabelField(line, EditorStyles.wordWrappedMiniLabel);
            }
        }

        static string Percent(int before, int after)
        {
            if (before == 0) return "0%";
            float change = (after - before) / (float)before * 100f;
            return change <= 0 ? $"giảm {-change:0.#}%" : $"tăng {change:0.#}%";
        }

        void DrawPreviews()
        {
            if (analysis == null || analysis.entries.Count == 0) return;

            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("So sánh (kéo chuột để xoay)", EditorStyles.boldLabel);
                wireframe = GUILayout.Toggle(wireframe, "Wireframe", EditorStyles.miniButton, GUILayout.Width(80));
            }
            EditorGUILayout.LabelField("Material xám trung tính để đánh giá hình học. Preview không ghi gì vào asset nguồn.", EditorStyles.miniLabel);

            float width = (position.width - 30f) * 0.5f;
            var row = GUILayoutUtility.GetRect(width * 2f, Mathf.Clamp(width, 180f, 360f));
            var left = new Rect(row.x, row.y + 16, width, row.height - 16);
            var right = new Rect(row.x + width + 6, row.y + 16, width, row.height - 16);
            GUI.Label(new Rect(left.x, row.y, left.width, 16), "Gốc", EditorStyles.miniBoldLabel);
            GUI.Label(new Rect(right.x, row.y, right.width, 16), results != null ? "Kết quả" : "Kết quả (bấm Preview)", EditorStyles.miniBoldLabel);

            HandleOrbit(left);
            HandleOrbit(right);

            previewOriginal ??= new LowPolyPreview();
            previewResult ??= new LowPolyPreview();

            var original = LowPolyPreview.CollectItems(source, analysis.entries, null, out var bounds);
            previewOriginal.Draw(left, original, bounds, orbit, wireframe);
            if (results != null)
            {
                var converted = LowPolyPreview.CollectItems(source, analysis.entries, results, out _);
                previewResult.Draw(right, converted, bounds, orbit, wireframe);
            }
            else
            {
                EditorGUI.DrawRect(right, new Color(0, 0, 0, 0.15f));
            }
        }

        void HandleOrbit(Rect rect)
        {
            var e = Event.current;
            if (e.type == EventType.MouseDrag && rect.Contains(e.mousePosition))
            {
                orbit.x += e.delta.x * 0.5f;
                orbit.y = Mathf.Clamp(orbit.y + e.delta.y * 0.5f, -89f, 89f);
                e.Use();
                Repaint();
            }
        }
    }
}
