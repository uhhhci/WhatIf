using UnityEngine;
using UnityEngine.Rendering;

namespace WhatIf.Content
{
    /// <summary>
    /// Owns the lighting for generated content. Lit 3D models (SPAR3D predicts shading-free colours)
    /// looked washed out under Unity's default lighting, and on the headset a scene without a light
    /// would render them almost black. Applied once at start from <see cref="WhatIfSettings"/>.
    /// </summary>
    public static class SceneLighting
    {
        public static void Apply(WhatIfSettings settings)
        {
            if (!settings.controlLighting) return;

            var sun = FindSun();
            if (sun == null)
            {
                var go = new GameObject("What If Sun");
                sun = go.AddComponent<Light>();
                sun.type = LightType.Directional;
                sun.shadows = LightShadows.None; // contact shadows handle grounding; real shadows are costly on device
            }
            sun.intensity = settings.sunIntensity;
            sun.color = Color.white;
            // standard lighting for the objects; their floor shadows are planar copies (PlanarShadows),
            // not Unity shadows
            sun.shadows = LightShadows.None;
            GeneratedObject.CastShadows = settings.castShadows;
            sun.transform.rotation = Quaternion.Euler(settings.sunAngles.x, settings.sunAngles.y, 0f);
            RenderSettings.sun = sun;

            // Flat ambient instead of the sky: predictable, and the same in the Editor and on device.
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = settings.ambientColor;
            RenderSettings.ambientIntensity = 1f;
            RenderSettings.defaultReflectionMode = DefaultReflectionMode.Custom; // no sky reflections on models
            RenderSettings.reflectionIntensity = 0.3f;
            DynamicGI.UpdateEnvironment();
        }

        /// <summary>
        /// Light the generated content like the room: a dim room makes it dimmer, warm light warmer.
        /// From the camera picture's average colour - which the camera's own exposure and white
        /// balance already even out, so the effect is kept moderate (at most about half / 1.3x).
        /// </summary>
        /// Only a fallback: once the Magic Leap's light estimation has measured the room
        /// (<see cref="RoomLightEstimate"/>), that sets the light and this does nothing.
        public static void MatchRoom(WhatIfSettings settings, Color average)
        {
            if (!settings.controlLighting || !settings.matchRoomLight) return;
            if (RoomLightEstimate.HasEstimate) return; // the measured light is better than a picture's average
            // (off by default: objects keep the standard lighting)
            float lum = 0.2126f * average.r + 0.7152f * average.g + 0.0722f * average.b;
            if (lum < 0.01f) return; // no frame
            float scale = Mathf.Lerp(0.5f, 1.3f, Mathf.Clamp01(lum / 0.55f));
            var hue = average / Mathf.Max(0.05f, Mathf.Max(average.r, Mathf.Max(average.g, average.b)));
            SetRoom(settings, Color.Lerp(Color.white, hue, 0.35f), scale, null, $"camera picture (brightness {lum:F2})");
        }

        /// <summary>Scene light like the room's: the sun's colour and strength, and the ambient light
        /// (its own tint if known, else the sun's), within 0.5-1.3x of the configured strength.</summary>
        public static void SetRoom(WhatIfSettings settings, Color tint, float scale, Color? ambientTint, string source)
        {
            if (!settings.controlLighting) return;
            scale = Mathf.Clamp(scale, 0.5f, 1.3f);
            var sun = FindSun();
            if (sun != null)
            {
                sun.intensity = settings.sunIntensity * scale;
                sun.color = tint;
            }
            RenderSettings.ambientLight = settings.ambientColor * (ambientTint ?? tint) * scale;
            Debug.Log($"[WhatIf] room light from {source}: x{scale:F2}, tint {ColorUtility.ToHtmlStringRGB(tint)}" +
                      (ambientTint.HasValue ? $", ambient {ColorUtility.ToHtmlStringRGB(ambientTint.Value)}" : ""));
        }

        static Light FindSun()
        {
            if (RenderSettings.sun != null) return RenderSettings.sun;
            foreach (var l in Object.FindObjectsOfType<Light>())
                if (l.type == LightType.Directional) return l;
            return null;
        }
    }
}
