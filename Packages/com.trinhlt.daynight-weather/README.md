# Day Night Sky & Weather

Weather system for URP (Unity 6000.3+):

- **Sky** – procedural day/night gradient skybox (`Skybox/Day Night Gradient`) with sun, moon, stars and drifting clouds; can drive a Directional Light.
- **Weather schedule** – seeded, deterministic Clear / Cloudy / Overcast / Rain schedule with forecast (`WeatherScheduleConfig` asset + `WeatherScheduleController`).
- **Rain** – camera-bound rain particles (`RainController`, `Rain.prefab`) driven by the schedule.
- **Wetness** – one shared surface wetness value (`WetnessController`) written to the global shader float `_WeatherWetness`.

## Install

Pick one:

- **Git URL** – *Window › Package Manager › + › Install package from git URL…*

  ```
  https://github.com/trinhlt12/some_random_stuff.git?path=/Packages/com.trinhlt.daynight-weather
  ```

  Pin a version by appending a tag or commit, e.g. `...#v1.0.0`.

- **Local folder** – *+ › Install package from disk…* and pick this folder's `package.json`, or copy the whole folder into the other project's `Packages/` (an embedded package, editable there).

Requires URP (`com.unity.render-pipelines.universal`), which Package Manager resolves automatically.

## Quick start

All under **Tools › Day Night Sky**:

1. **Add Sky Controller To Scene** – creates the sky with the sample material and hooks up the scene's directional light.
2. **Add Weather Schedule** – adds the schedule with the sample config.
3. **Add Rain** – adds the rain prefab, following `Camera.main`.
4. **Add Wetness** – adds the controller that wets surfaces while it rains.
5. *(optional)* **Create or Update Wetness Demo** – a small primitive test area.

The sky controller puts its material into the scene skybox while enabled and restores the previous one when disabled.

## Making your own shaders get wet

Materials using `DayNightSky/Wet Lit` work out of the box. For a custom HLSL shader, include the helper and scale by a per-material exposure (0 = never wet):

```hlsl
#include "Packages/com.trinhlt.daynight-weather/Shaders/WeatherWetness.hlsl"

half wet = WeatherWetAmount(_WetExposure, 1);
albedo     = WeatherWetAlbedo(albedo, wet, _WetDarkening);
smoothness = WeatherWetSmoothness(smoothness, wet, _WetSmoothness);
```

Or just declare `float _WeatherWetness;` (outside `UnityPerMaterial`) and use it directly. Shader Graph: add a Float property with reference `_WeatherWetness`, untick *Exposed*.

## Notes

- The sample assets (sky material, cloud noise, rain prefab, sample schedule, wetness demo) live inside the package. When it is installed from Git the package is read-only: use them as they are, or duplicate them into `Assets/` to edit. The *Regenerate Cloud Noise* and *Create or Update Wetness Demo* menus write into the package, so use them only with an embedded/local copy.
- Tests: add `"testables": ["com.trinhlt.daynight-weather"]` to `Packages/manifest.json` to see them in the Test Runner.
