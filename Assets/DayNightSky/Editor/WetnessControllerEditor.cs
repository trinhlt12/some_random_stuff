using UnityEditor;
using UnityEngine;

namespace DayNightSky.Editor
{
    [CustomEditor(typeof(WetnessController))]
    public sealed class WetnessControllerEditor : UnityEditor.Editor
    {
        float preview = 0.5f;

        public override bool RequiresConstantRepaint() => Application.isPlaying;

        [MenuItem("Tools/Day Night Sky/Add Wetness")]
        public static void AddWetness()
        {
            var existing = Object.FindFirstObjectByType<WetnessController>();
            if (existing != null)
            {
                Selection.activeGameObject = existing.gameObject;
                return;
            }
            var sky = Object.FindFirstObjectByType<DayNightSkyController>();
            if (sky == null)
            {
                EditorUtility.DisplayDialog("Add Wetness", "Add a Day Night Sky first (Tools > Day Night Sky > Add Sky Controller To Scene).", "OK");
                return;
            }
            var wet = Undo.AddComponent<WetnessController>(sky.gameObject);
            wet.sky = sky;
            Selection.activeGameObject = sky.gameObject;
        }

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var wet = (WetnessController)target;
            foreach (var w in wet.Warnings) EditorGUILayout.HelpBox(w, MessageType.Warning);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("State", EditorStyles.boldLabel);
            var sky = wet.sky;
            EditorGUILayout.LabelField("Clock Source", sky != null
                ? $"{sky.name}: shared clock, {sky.TotalHours:0.00} game h{(sky.ClockPaused ? "  (PAUSED: wetness holds)" : "")}"
                : "No sky");
            EditorGUILayout.LabelField("Rates Are Per", "game hour (the clock speed is already included)");
            EditorGUILayout.LabelField("Rain Used", sky != null ? $"{sky.RainIntensity:0.00}  (blended weather)" : "-");
            var c = wet.LastConditions;
            EditorGUILayout.LabelField("Last Conditions", $"rain {c.rain:0.00}, overcast {c.overcast:0.00}, wind {c.windSpeed:0.000}, sun {c.sunHeight:0.00}");
            EditorGUILayout.LabelField("Wetting / Drying", $"{wet.CurrentWettingRate:0.00} / {wet.CurrentDryingRate:0.00} per game h  →  settles at {wet.SteadyState:0.00}");
            EditorGUILayout.LabelField("Mode", wet.Mode == WetnessMode.ManualPreview ? "Manual Preview (simulation stopped)" : "Simulating");
            EditorGUILayout.LabelField("Last Event", wet.LastEvent.ToString());
            var rect = EditorGUILayout.GetControlRect();
            EditorGUI.ProgressBar(rect, wet.DisplayedWetness, $"Wetness {wet.DisplayedWetness:0.000}" +
                                                             (wet.Mode == WetnessMode.ManualPreview ? $"  (simulated {wet.Wetness:0.000})" : ""));
            if (!Application.isPlaying)
                EditorGUILayout.HelpBox("Edit Mode does not simulate (the clock does not run). Buttons below still show values in the Scene view; " +
                                        "the global goes back to 0 when the component is disabled.", MessageType.None);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Reset Dry")) wet.ResetDry();
                if (GUILayout.Button("Set Fully Wet")) wet.SetFullyWet();
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.BeginChangeCheck();
                preview = EditorGUILayout.Slider("Manual Preview", wet.Mode == WetnessMode.ManualPreview ? wet.PreviewValue : preview, 0f, 1f);
                if (EditorGUI.EndChangeCheck() && wet.Mode == WetnessMode.ManualPreview) wet.SetManualPreview(preview);
                if (wet.Mode != WetnessMode.ManualPreview && GUILayout.Button("Preview", GUILayout.Width(70f))) wet.SetManualPreview(preview);
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(wet.Mode != WetnessMode.ManualPreview))
                    if (GUILayout.Button("Return to Simulation")) wet.ReturnToSimulation();
                using (new EditorGUI.DisabledScope(!Application.isPlaying))
                    if (GUILayout.Button(new GUIContent("Recompute From Schedule", "Rebuild from the schedule's last hours (needs a running schedule).")))
                        wet.Recompute();
            }
            SceneView.RepaintAll();
        }
    }
}
