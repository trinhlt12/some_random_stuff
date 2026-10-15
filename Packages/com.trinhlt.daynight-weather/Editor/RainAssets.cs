using UnityEditor;
using UnityEngine;

namespace DayNightSky.Editor
{
    /// <summary>Creates the sample rain material and prefab (once) and adds rain to a scene.</summary>
    public static class RainAssets
    {
        public const string Folder = PackagePaths.Root + "/Rain";
        public const string MaterialPath = Folder + "/RainStreak.mat";
        public const string PrefabPath = Folder + "/Rain.prefab";
        public const string ShaderName = "DayNightSky/Rain Streak";

        public static Material GetOrCreateMaterial()
        {
            var m = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            if (m != null) return m;
            System.IO.Directory.CreateDirectory(Folder);
            m = new Material(Shader.Find(ShaderName)) { name = "RainStreak" };
            AssetDatabase.CreateAsset(m, MaterialPath);
            AssetDatabase.SaveAssets();
            return m;
        }

        /// <summary>A "Rain" object with one configured Particle System and a RainController.</summary>
        public static GameObject GetOrCreatePrefab()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            if (prefab != null) return prefab;
            var go = new GameObject("Rain");
            try
            {
                go.AddComponent<ParticleSystem>();
                var rain = go.AddComponent<RainController>();
                rain.material = GetOrCreateMaterial();
                rain.ConfigureParticleSystem();
                // Idle until the weather asks for rain.
                go.GetComponent<ParticleSystemRenderer>().enabled = false;
                go.GetComponent<ParticleSystem>().Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                return PrefabUtility.SaveAsPrefabAsset(go, PrefabPath);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [MenuItem("Tools/Day Night Sky/Add Rain")]
        public static void AddRain()
        {
            var existing = Object.FindFirstObjectByType<RainController>();
            if (existing != null)
            {
                Selection.activeGameObject = existing.gameObject;
                return;
            }
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(GetOrCreatePrefab());
            Undo.RegisterCreatedObjectUndo(instance, "Add Rain");
            var rain = instance.GetComponent<RainController>();
            rain.sky = Object.FindFirstObjectByType<DayNightSkyController>();
            rain.targetCamera = Camera.main;
            Selection.activeGameObject = instance;
        }
    }
}
