using UnityEditor;
using UnityEngine;

namespace DayNightSky.Editor
{
    public static class DayNightSkyMenu
    {
        public const string MaterialPath = "Assets/DayNightSky/Materials/DayNightSky.mat";
        public const string ShaderName = "Skybox/Day Night Gradient";
        public const string CloudNoiseProperty = "_CloudNoise";

        /// <summary>Returns the sample material (with the shared cloud noise), creating either if missing.</summary>
        public static Material GetOrCreateMaterial()
        {
            var m = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            if (m == null)
            {
                m = new Material(Shader.Find(ShaderName)) { name = "DayNightSky" };
                AssetDatabase.CreateAsset(m, MaterialPath);
            }
            if (m.HasProperty(CloudNoiseProperty) && m.GetTexture(CloudNoiseProperty) == null)
            {
                m.SetTexture(CloudNoiseProperty, CloudNoiseGenerator.GetOrCreateTexture());
                EditorUtility.SetDirty(m);
                AssetDatabase.SaveAssets();
            }
            return m;
        }

        [MenuItem("Tools/Day Night Sky/Add Sky Controller To Scene")]
        public static void AddController()
        {
            var existing = Object.FindFirstObjectByType<DayNightSkyController>();
            if (existing != null)
            {
                Selection.activeGameObject = existing.gameObject;
                EditorGUIUtility.PingObject(existing);
                return;
            }

            var go = new GameObject("Day Night Sky");
            Undo.RegisterCreatedObjectUndo(go, "Add Day Night Sky");
            var controller = Undo.AddComponent<DayNightSkyController>(go);
            controller.skyMaterial = GetOrCreateMaterial();

            // Suggest the scene's sun, or the first directional light, for the optional sync.
            var sun = RenderSettings.sun;
            if (sun == null)
                foreach (var light in Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
                    if (light.type == LightType.Directional) { sun = light; break; }
            controller.sunLight = sun;

            Selection.activeGameObject = go;
        }
    }
}
