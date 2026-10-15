using UnityEditor;
using UnityEngine;

namespace DayNightSky.Editor
{
    [CustomEditor(typeof(DayNightSkyController))]
    public sealed class DayNightSkyControllerEditor : UnityEditor.Editor
    {
        static readonly CloudPreset[] Presets = { CloudPreset.Clear, CloudPreset.Cloudy, CloudPreset.Overcast };

        int seed;

        void OnEnable() => seed = CloudNoiseGenerator.GetSeed();

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var sky = (DayNightSkyController)target;

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Weather Preview", EditorStyles.boldLabel);

            bool driven = sky.WeatherDriver != null;
            if (driven)
                EditorGUILayout.HelpBox("The weather (clouds, overcast, wind) is driven by the Weather Schedule component; " +
                                        "use its controls. Values edited here are overwritten.", MessageType.Info);
            using (new EditorGUI.DisabledScope(driven))
            {
                DrawPresetButtons(sky);
            }

            DrawNoise(sky);
        }

        void DrawPresetButtons(DayNightSkyController sky)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PrefixLabel(new GUIContent("Transition To", "Blend from the weather shown now over Transition Duration seconds."));
                foreach (var preset in Presets)
                    if (GUILayout.Button(preset.ToString(), EditorStyles.miniButton))
                    {
                        Undo.RecordObject(sky, "Cloud Transition");
                        sky.TransitionToPreset(preset);
                    }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PrefixLabel(new GUIContent("Apply Instantly", "Jump to the preset, cancelling any transition."));
                foreach (var preset in Presets)
                    if (GUILayout.Button(preset.ToString(), EditorStyles.miniButton))
                    {
                        Undo.RecordObject(sky, "Apply Cloud Preset");
                        sky.ApplyPresetImmediate(preset);
                        sky.Tick(0f);
                    }
            }

            if (sky.IsTransitioning)
            {
                var rect = EditorGUILayout.GetControlRect();
                EditorGUI.ProgressBar(rect, sky.TransitionProgress, $"Transition {sky.TransitionProgress:P0}");
                Repaint();
            }
        }

        void DrawNoise(DayNightSkyController sky)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Cloud Noise", EditorStyles.boldLabel);
            var noise = sky.skyMaterial != null && sky.skyMaterial.HasProperty(DayNightSkyMenu.CloudNoiseProperty)
                ? sky.skyMaterial.GetTexture(DayNightSkyMenu.CloudNoiseProperty)
                : null;
            if (sky.skyMaterial != null && noise == null)
            {
                EditorGUILayout.HelpBox("The sky material has no cloud noise texture, so no clouds are drawn.", MessageType.Warning);
                if (GUILayout.Button("Assign Cloud Noise To Material"))
                {
                    Undo.RecordObject(sky.skyMaterial, "Assign Cloud Noise");
                    sky.skyMaterial.SetTexture(DayNightSkyMenu.CloudNoiseProperty, CloudNoiseGenerator.GetOrCreateTexture());
                    EditorUtility.SetDirty(sky.skyMaterial);
                    ForceRebuild(sky);
                }
            }
            else if (noise != null)
            {
                using (new EditorGUI.DisabledScope(true))
                    EditorGUILayout.ObjectField("Texture", noise, typeof(Texture), false);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                seed = EditorGUILayout.IntField(new GUIContent("Seed", "Saved in the texture's import settings."), seed);
                if (GUILayout.Button("Random", EditorStyles.miniButton, GUILayout.Width(60f)))
                    seed = Random.Range(1, 100000);
            }
            // Same path, so every material keeps pointing at the (reimported) texture.
            if (GUILayout.Button("Regenerate Cloud Noise"))
            {
                CloudNoiseGenerator.Regenerate(seed);
                SceneView.RepaintAll();
            }
        }

        // The instance was copied from the template; toggle it so the controller copies it again.
        static void ForceRebuild(DayNightSkyController sky)
        {
            if (!sky.isActiveAndEnabled) return;
            sky.enabled = false;
            sky.enabled = true;
            SceneView.RepaintAll();
        }
    }
}
