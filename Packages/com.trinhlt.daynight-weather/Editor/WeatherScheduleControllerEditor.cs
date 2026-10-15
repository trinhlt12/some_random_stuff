using UnityEditor;
using UnityEngine;

namespace DayNightSky.Editor
{
    [CustomEditor(typeof(WeatherScheduleController))]
    public sealed class WeatherScheduleControllerEditor : UnityEditor.Editor
    {
        static readonly float[] ClockSpeeds = { 1f, 10f, 60f, 600f };

        public override bool RequiresConstantRepaint() => Application.isPlaying;

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var weather = (WeatherScheduleController)target;
            var sky = weather.Sky;
            var schedule = weather.Schedule;

            if (weather.config == null)
            {
                EditorGUILayout.HelpBox("Assign a Weather Schedule Config (or use Tools > Day Night Sky > Add Weather Schedule).", MessageType.Info);
                if (GUILayout.Button("Use Sample Config"))
                {
                    Undo.RecordObject(weather, "Assign Weather Config");
                    weather.config = WeatherScheduleMenu.GetOrCreateSampleConfig();
                    weather.RegenerateSchedule();
                }
            }
            foreach (var e in weather.Errors) EditorGUILayout.HelpBox(e, MessageType.Error);
            if (sky == null) return;

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Clock (shared with day/night)", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Game Time", $"{FormatClock(sky.TotalHours)}   ({sky.TotalHours:0.00} h total)");
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.BeginChangeCheck();
                float speed = EditorGUILayout.FloatField(new GUIContent("Clock Speed", "Sky's clockSpeed: moves day/night and the schedule together."), sky.clockSpeed);
                foreach (float s in ClockSpeeds)
                    if (GUILayout.Button($"×{s:0}", EditorStyles.miniButton, GUILayout.Width(44f))) { speed = s; GUI.changed = true; }
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(sky, "Clock Speed");
                    sky.clockSpeed = Mathf.Max(0f, speed);
                }
            }
            double secPerHour = sky.RealSecondsPerGameHour;
            EditorGUILayout.LabelField(" ", double.IsInfinity(secPerHour)
                ? "Clock stopped (speed 0)"
                : $"1 game hour = {secPerHour:0.##} real s{(sky.autoAdvance ? "" : "  (Auto Advance is off)")}", EditorStyles.miniLabel);

            if (schedule == null) return;

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Weather", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Seed", $"{weather.SeedInUse}   (algorithm v{WeatherSchedule.AlgorithmVersion})");
            string mode = weather.Mode.ToString();
            if (weather.IsReturningToAutomatic) mode += " (returning to schedule…)";
            if (weather.Mode == WeatherMode.ManualOverride) mode += $": {schedule.GetPresetName(weather.ForcedPreset)}";
            EditorGUILayout.LabelField("Mode", mode);
            if (weather.Mode == WeatherMode.Paused)
                EditorGUILayout.HelpBox("Paused stops the shared clock: day/night, the schedule, transitions and cloud drift all hold.", MessageType.None);
            if (weather.Mode == WeatherMode.ManualOverride)
                EditorGUILayout.HelpBox("The schedule waits while day/night keeps running. Return to Automatic blends back and continues it.", MessageType.None);

            double now = weather.ScheduleHours;
            var seg = schedule.SegmentAt(now);
            float progress = seg.ProgressAt(now);
            bool inTransition = progress < 1f;
            EditorGUILayout.LabelField("Schedule Time", $"{now:0.00} h  (segment #{seg.index})");
            EditorGUILayout.LabelField("Scheduled State", inTransition
                ? $"{schedule.GetPresetName(seg.fromPreset)} → {schedule.GetPresetName(seg.toPreset)}"
                : schedule.GetPresetName(seg.toPreset));
            var rect = EditorGUILayout.GetControlRect();
            if (inTransition)
                EditorGUI.ProgressBar(rect, progress, $"Transition {progress:P0}, {FormatDuration(seg.TransitionEndHours - now, sky)} left");
            else
                EditorGUI.ProgressBar(rect, (float)((now - seg.TransitionEndHours) / System.Math.Max(seg.holdHours, 1e-6)),
                    $"Holding, {FormatDuration(seg.EndHours - now, sky)} left");
            if (weather.IsBlending)
            {
                rect = EditorGUILayout.GetControlRect();
                EditorGUI.ProgressBar(rect, weather.BlendProgress, $"Blend {weather.BlendProgress:P0}");
            }

            EditorGUILayout.LabelField("Forecast", EditorStyles.miniBoldLabel);
            for (int i = 1; i <= 2; i++)
            {
                var next = schedule.SegmentAfter(now, i);
                string when = weather.ScheduleRunning
                    ? $"{FormatClock(weather.ScheduleToClockHours(next.transitionStartHours))} (in {FormatDuration(next.transitionStartHours - now, sky)})"
                    : $"in {next.transitionStartHours - now:0.0} schedule h (waiting)";
                EditorGUILayout.LabelField($"  +{i}", $"{schedule.GetPresetName(next.toPreset)} — starts {when}, " +
                                                     $"{next.transitionHours * 60.0:0} min blend, holds {next.holdHours:0.0} h");
            }

            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (weather.Mode == WeatherMode.Paused) { if (GUILayout.Button("Resume")) weather.Resume(); }
                else if (GUILayout.Button("Pause")) weather.Pause();
                using (new EditorGUI.DisabledScope(weather.Mode == WeatherMode.Paused))
                    if (GUILayout.Button("Advance to Next Segment")) weather.AdvanceToNextSegment();
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PrefixLabel("Force");
                for (int p = 0; p < schedule.PresetCount; p++)
                    if (GUILayout.Button(schedule.GetPresetName(p), EditorStyles.miniButton)) weather.ForcePreset(p);
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(weather.Mode == WeatherMode.Automatic))
                    if (GUILayout.Button("Return to Automatic")) weather.ReturnToAutomatic();
                if (GUILayout.Button(new GUIContent("Regenerate Schedule from Seed", "Explicit reset: rebuild from the seed and current config, schedule time back to the clock.")))
                    weather.RegenerateSchedule();
            }
            SceneView.RepaintAll();
        }

        static string FormatClock(double totalHours)
        {
            int day = (int)System.Math.Floor(totalHours / 24.0);
            double h = totalHours - day * 24.0;
            int hh = (int)h, mm = (int)((h - hh) * 60.0);
            return $"Day {day}  {hh:00}:{mm:00}";
        }

        static string FormatDuration(double gameHours, DayNightSkyController sky)
        {
            gameHours = System.Math.Max(0.0, gameHours);
            int h = (int)gameHours, m = (int)((gameHours - h) * 60.0);
            double real = gameHours * sky.RealSecondsPerGameHour;
            string realText = double.IsInfinity(real) || !sky.autoAdvance ? "" : $" ≈ {real:0}s real";
            return $"{h}h{m:00}m game{realText}";
        }
    }
}
