using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace PastelLowPoly.Editor
{
    public sealed class PastelConverterWindow : EditorWindow
    {
        [SerializeField] GameObject source;
        [SerializeField] PastelConvertSettings settings = new PastelConvertSettings();

        List<SourceMaterialInfo> infos;
        readonly List<string> notes = new List<string>();
        string lastResult;
        Vector2 scroll;

        [MenuItem("Tools/Pastel Low Poly/Pastel Material Converter")]
        public static void Open()
        {
            var w = GetWindow<PastelConverterWindow>("Pastel Converter");
            if (Selection.activeGameObject != null) w.SetSource(Selection.activeGameObject);
            w.minSize = new Vector2(400, 420);
        }

        void OnEnable()
        {
            if (source != null) SetSource(source);
        }

        void SetSource(GameObject go)
        {
            source = go;
            lastResult = null;
            notes.Clear();
            infos = go != null ? PastelMaterialConverter.Analyze(go, notes) : null;
        }

        void OnGUI()
        {
            scroll = EditorGUILayout.BeginScrollView(scroll);

            EditorGUI.BeginChangeCheck();
            var picked = (GameObject)EditorGUILayout.ObjectField("GameObject / Prefab", source, typeof(GameObject), true);
            if (EditorGUI.EndChangeCheck()) SetSource(picked);

            EditorGUILayout.HelpBox(
                "Tạo material pastel cho từng material gốc, giữ nguồn màu của từng bộ phận (màu material, texture/palette, vertex color). " +
                "Prefab/model trong Project: tạo prefab variant mới. Object trong scene: thay material trực tiếp (Undo được). Material gốc không bị sửa.",
                MessageType.Info);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Màu", EditorStyles.boldLabel);
            settings.pastelize = EditorGUILayout.Toggle(new GUIContent("Pastel hóa màu gốc", "Giữ tông màu (hue) của từng phần, nâng độ sáng và làm dịu độ đậm màu."), settings.pastelize);
            using (new EditorGUI.DisabledScope(!settings.pastelize))
            using (new EditorGUI.IndentLevelScope())
            {
                settings.pastelAmount = EditorGUILayout.Slider("Mức pastel", settings.pastelAmount, 0f, 1f);
                settings.lightMin = EditorGUILayout.Slider(new GUIContent("Độ sáng phần tối nhất", "Phần đen/tối của model sẽ sáng tới mức này (OKLab L)."), settings.lightMin, 0f, 1f);
                settings.lightMax = EditorGUILayout.Slider(new GUIContent("Độ sáng phần sáng nhất"), settings.lightMax, 0f, 1f);
                settings.saturation = EditorGUILayout.Slider(new GUIContent("Độ đậm màu", "Nhân với độ đậm màu gốc."), settings.saturation, 0f, 2f);
                settings.chromaMax = EditorGUILayout.Slider(new GUIContent("Giới hạn độ đậm", "Trần độ đậm màu để không bị chói."), settings.chromaMax, 0f, 0.3f);
            }
            settings.textureDetailBlur = EditorGUILayout.Slider(
                new GUIContent("Làm mờ chi tiết texture", "Đọc mip nhỏ hơn để xóa vân gỗ, vải... của texture tả thực. Để 0 với texture palette (Styloo) vì sẽ trộn các ô màu."),
                settings.textureDetailBlur, 0f, 8f);
            settings.flatNormals = EditorGUILayout.Toggle("Flat shading", settings.flatNormals);
            settings.outputFolder = EditorGUILayout.TextField("Thư mục xuất", settings.outputFolder);

            if (infos != null)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField($"Material tìm thấy: {infos.Count}", EditorStyles.boldLabel);
                foreach (var info in infos) DrawInfo(info);
                foreach (var n in notes) EditorGUILayout.HelpBox(n, MessageType.None);
            }

            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(source == null || infos == null || infos.Count == 0))
            {
                if (GUILayout.Button("Chuyển sang pastel", GUILayout.Height(26)))
                {
                    try
                    {
                        var r = PastelMaterialConverter.Convert(source, settings);
                        lastResult = r.appliedToScene
                            ? $"Đã thay {r.materialPaths.Count} material trên object trong scene (Ctrl+Z để hoàn tác)."
                            : $"Đã tạo prefab variant: {r.prefabPath}\n{r.materialPaths.Count} material pastel cùng thư mục.";
                        if (r.prefabPath != null)
                            EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<GameObject>(r.prefabPath));
                    }
                    catch (Exception e)
                    {
                        Debug.LogException(e);
                        lastResult = null;
                        EditorUtility.DisplayDialog("Pastel Converter", "Chuyển thất bại, các asset vừa tạo đã được xóa.\n" + e.Message, "OK");
                    }
                    GUIUtility.ExitGUI();
                }
            }
            if (!string.IsNullOrEmpty(lastResult)) EditorGUILayout.HelpBox(lastResult, MessageType.Info);

            EditorGUILayout.EndScrollView();
        }

        static void DrawInfo(SourceMaterialInfo info)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.ObjectField(info.source, typeof(Material), false);
                    var rect = GUILayoutUtility.GetRect(36, 18, GUILayout.Width(36));
                    EditorGUI.DrawRect(rect, info.color);
                }
                var flags = new List<string>();
                if (info.texture != null) flags.Add($"texture: {info.texture.name}");
                if (info.vertexColors) flags.Add("vertex color");
                if (info.cutout) flags.Add($"cutout {info.cutoff:0.##}");
                if (info.doubleSided) flags.Add("2 mặt");
                if (flags.Count > 0) EditorGUILayout.LabelField(string.Join(" · ", flags), EditorStyles.miniLabel);
                foreach (var n in info.notes) EditorGUILayout.LabelField(n, EditorStyles.wordWrappedMiniLabel);
            }
        }
    }
}
