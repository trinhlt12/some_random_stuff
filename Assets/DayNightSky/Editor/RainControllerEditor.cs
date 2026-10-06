using UnityEditor;
using UnityEngine;

namespace DayNightSky.Editor
{
    [CustomEditor(typeof(RainController))]
    public sealed class RainControllerEditor : UnityEditor.Editor
    {
        public override bool RequiresConstantRepaint() => Application.isPlaying;

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var rain = (RainController)target;

            if (rain.material == null)
                EditorGUILayout.HelpBox("No material: assign RainStreak (DayNightSky/Rain Streak).", MessageType.Warning);
            if (rain.targetCamera == null && !Application.isPlaying)
                EditorGUILayout.HelpBox("No camera assigned: Camera.main is used at runtime.", MessageType.Info);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Particle Budget", EditorStyles.boldLabel);
            var q = rain.ActiveSettings;
            var b = RainController.ComputeBudget(q, rain.heightAboveCamera, rain.depthBelowCamera, rain.fallSpeed);
            EditorGUILayout.LabelField("Quality", $"{rain.quality}: max {b.maxParticles} drops, area {q.areaWidth:0}×{q.areaDepth:0} m");
            EditorGUILayout.LabelField("Drop Lifetime", $"{b.lifetime:0.00} s  = ({rain.heightAboveCamera:0.#} + {rain.depthBelowCamera:0.#} m) / {rain.fallSpeed:0.#} m/s");
            EditorGUILayout.LabelField("Emission at Rain 1", b.clamped
                ? $"{b.effectiveEmission:0}/s (clamped from {b.requestedEmission:0}/s)"
                : $"{b.effectiveEmission:0}/s");
            EditorGUILayout.LabelField("Live Drops at Rain 1", $"≈ {b.steadyStateParticles:0}  (emission × lifetime)");
            if (b.clamped)
                EditorGUILayout.HelpBox($"Requested emission × lifetime would exceed {RainController.BudgetHeadroom:P0} of Max Particles, " +
                                        "so the emission is clamped. Raise Max Particles, or lower the height/raise the fall speed to shorten the lifetime.",
                                        MessageType.Warning);
            EditorGUILayout.HelpBox("Live drops ≈ emission per second × lifetime. Lifetime is the fall time from the emitter to below the camera. " +
                                    "Density on screen follows emission ÷ area, so a smaller area needs less emission for the same look.",
                                    MessageType.None);
            if (q.maxParticles > RainController.HardParticleLimit)
                EditorGUILayout.HelpBox($"Max Particles is capped at {RainController.HardParticleLimit}.", MessageType.Warning);

            if (!Application.isPlaying) return;

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Live", EditorStyles.boldLabel);
            string state = rain.IsIdle ? "Idle (not simulating or drawing)" : rain.IsPausedByClock ? "Paused with the clock" : "Raining";
            EditorGUILayout.LabelField("State", state);
            EditorGUILayout.LabelField("Intensity", $"{rain.CurrentIntensity:0.00}{(rain.previewIntensity ? "  (preview)" : "")}");
            EditorGUILayout.LabelField("Live Drops", $"{rain.AliveParticles} / {rain.Budget.maxParticles}");
            var v = rain.DropVelocity;
            float tilt = Mathf.Atan2(new Vector2(v.x, v.z).magnitude, -v.y) * Mathf.Rad2Deg;
            EditorGUILayout.LabelField("Drop Velocity", $"{v.x:0.0}, {v.y:0.0}, {v.z:0.0} m/s  (tilt {tilt:0}°)");
            EditorGUILayout.LabelField("Teleports", rain.TeleportCount.ToString());

            var weather = rain.sky != null ? rain.sky.GetComponent<WeatherScheduleController>() : null;
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(weather == null || weather.Schedule == null || weather.Schedule.FindPreset("rain") < 0))
                    if (GUILayout.Button("Force Rain")) weather.ForcePreset("rain");
                using (new EditorGUI.DisabledScope(weather == null || weather.Mode == WeatherMode.Automatic))
                    if (GUILayout.Button("Return to Automatic")) weather.ReturnToAutomatic();
            }
            var motion = rain.targetCamera != null ? rain.targetCamera.GetComponent<RainTestCameraMotion>() : null;
            using (new EditorGUI.DisabledScope(motion == null))
                if (GUILayout.Button(new GUIContent("Teleport Camera", "Needs RainTestCameraMotion on the camera."))) motion.TeleportNow();
        }
    }
}
