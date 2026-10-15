using UnityEditor;
using UnityEngine;

namespace DayNightSky.Editor
{
    public static class WeatherScheduleMenu
    {
        public static readonly string SampleConfigPath = PackagePaths.Root + "/Weather/SampleWeatherSchedule.asset";

        /// <summary>The ready-to-run sample config (Clear ↔ Cloudy ↔ Overcast), created if missing.</summary>
        public static WeatherScheduleConfig GetOrCreateSampleConfig()
        {
            var config = AssetDatabase.LoadAssetAtPath<WeatherScheduleConfig>(SampleConfigPath);
            if (config != null) return config;
            config = WeatherScheduleConfig.CreateDefault();
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(SampleConfigPath));
            AssetDatabase.CreateAsset(config, SampleConfigPath);
            AssetDatabase.SaveAssets();
            return config;
        }

        [MenuItem("Tools/Day Night Sky/Add Weather Schedule")]
        public static void AddWeatherSchedule()
        {
            var sky = Selection.activeGameObject != null ? Selection.activeGameObject.GetComponent<DayNightSkyController>() : null;
            if (sky == null) sky = Object.FindFirstObjectByType<DayNightSkyController>();
            if (sky == null)
            {
                DayNightSkyMenu.AddController();
                sky = Object.FindFirstObjectByType<DayNightSkyController>();
            }

            var weather = sky.GetComponent<WeatherScheduleController>();
            if (weather == null)
            {
                weather = Undo.AddComponent<WeatherScheduleController>(sky.gameObject);
                weather.config = GetOrCreateSampleConfig();
                weather.RegenerateSchedule();
            }
            Selection.activeGameObject = sky.gameObject;
            EditorGUIUtility.PingObject(weather);
        }
    }
}
