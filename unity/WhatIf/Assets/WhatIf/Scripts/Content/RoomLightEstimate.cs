using System.Collections;
using UnityEngine;

namespace WhatIf.Content
{
    /// <summary>
    /// Where the room's main light comes from, from the Magic Leap's light estimation (it reads the RGB
    /// camera): the scene's sun is turned to come from there and takes its colour, so virtual objects
    /// cast their shadows like the real things in the room. One camera picture alone can't tell the
    /// direction reliably; the estimation can.
    /// It is the main source of the scene light: direction, colour, strength and the ambient light. The
    /// camera picture's average colour only stands in until a first estimate exists (or if it fails).
    /// The estimation uses the same camera as the pictures for the planner, so it only runs briefly -
    /// at start (retried until it works) and after each scene is built - and is stopped before every
    /// picture. Without it the sun keeps its default direction from the settings.
    /// </summary>
    public class RoomLightEstimate : MonoBehaviour
    {
        WhatIfSettings _settings;
        Coroutine _running;
        bool _created;

        /// <summary>The last estimated direction the light travels (world, pointing down), if any.</summary>
        public static Vector3? LightDirection { get; private set; }
        /// <summary>The room's light has been measured: it sets the scene light, not the picture.</summary>
        public static bool HasEstimate { get; private set; }

        float _nextTry = 3f; // the first try a few seconds after start (permissions, tracking)
        int _tries;

        void Update()
        {
            // at start: until a first estimate exists (camera permission may come later), at most 8 tries
            if (HasEstimate || _settings == null || !_settings.lightEstimation || _running != null || _tries >= 8) return;
            if (Time.realtimeSinceStartup < _nextTry) return;
            _nextTry = Time.realtimeSinceStartup + 10f;
            _tries++;
            Sample();
        }

        public void Init(WhatIfSettings settings) { _settings = settings; }

        /// <summary>A few seconds of estimation, then the sun follows it.</summary>
        public void Sample()
        {
            if (_settings == null || !_settings.lightEstimation || _running != null) return;
            _running = StartCoroutine(Run());
        }

        /// <summary>Stops a running estimation at once (a camera picture is about to be taken).</summary>
        public void Stop()
        {
            if (_running != null) { StopCoroutine(_running); _running = null; }
            Destroy();
        }

        IEnumerator Run()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            var feature = UnityEngine.XR.OpenXR.OpenXRSettings.Instance != null
                ? UnityEngine.XR.OpenXR.OpenXRSettings.Instance.GetFeature<UnityEngine.XR.OpenXR.Features.MagicLeapSupport.MagicLeapLightEstimationFeature>()
                : null;
            if (feature == null || !feature.enabled)
            {
                Debug.Log("[WhatIf] light estimation: feature not enabled, the sun keeps its default direction");
                _running = null;
                yield break;
            }
            try
            {
                feature.CreateLightEstimation(UnityEngine.XR.OpenXR.Features.MagicLeapSupport.MagicLeapLightEstimationFeature.HDRCubemapFaceResolution.Resolution_64x64);
                _created = true;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[WhatIf] light estimation failed to start: " + e.Message);
                _running = null;
                yield break;
            }
            float until = Time.realtimeSinceStartup + 4f;
            bool done = false;
            while (!done && Time.realtimeSinceStartup < until)
            {
                yield return new WaitForSecondsRealtime(0.5f);
                try
                {
                    if (!Ready(feature)) continue;
                    var data = feature.GetLightEstimationEstimateData();
                    Apply(data.DirectionalLight.Direction, data.DirectionalLight.Color, data.HarmonicsCoefficients);
                    done = true;
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning("[WhatIf] light estimation: " + e.Message);
                    break;
                }
            }
            if (!done) Debug.Log("[WhatIf] light estimation: no estimate in time, the sun keeps its direction");
            Destroy();
            _running = null;
#else
            _running = null;
            yield break;
#endif
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        static bool Ready(UnityEngine.XR.OpenXR.Features.MagicLeapSupport.MagicLeapLightEstimationFeature f)
        {
            object r = f.CheckEstimationEstimateReadiness();
            return r is bool b ? b : r != null && r.ToString().IndexOf("ready", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }
#endif

        void Destroy()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!_created) return;
            _created = false;
            try
            {
                UnityEngine.XR.OpenXR.OpenXRSettings.Instance
                    .GetFeature<UnityEngine.XR.OpenXR.Features.MagicLeapSupport.MagicLeapLightEstimationFeature>()?.DestroyLightEstimation();
            }
            catch (System.Exception e) { Debug.LogWarning("[WhatIf] light estimation: " + e.Message); }
#endif
        }

        /// <summary>The ambient light's colour: the first (constant) spherical-harmonics coefficient per
        /// colour channel. Stored either channel after channel (R0..R8, G0..G8, B0..B8) or interleaved
        /// (R0 G0 B0 R1 ...): the reading whose three values are all positive and largest wins.</summary>
        static Color? Ambient(float[] sh)
        {
            if (sh == null || sh.Length < 27) return null;
            var planar = new Color(sh[0], sh[9], sh[18]);
            var mixed = new Color(sh[0], sh[1], sh[2]);
            bool ok(Color c) => c.r > 0f && c.g > 0f && c.b > 0f;
            var pick = ok(planar) && (!ok(mixed) || planar.maxColorComponent >= mixed.maxColorComponent) ? planar
                     : ok(mixed) ? mixed : (Color?)null;
            return pick;
        }

        static float Luminance(Color c) => 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;

        void Apply(Vector3 direction, Color colour, float[] harmonics)
        {
            if (direction.sqrMagnitude < 1e-4f) return;
            direction.Normalize();
            // the light travels downwards: an estimate pointing up names where it comes from
            if (direction.y > 0f) direction = -direction;
            // a lamp near the horizon would cast metres-long shadows across the room: at least 35 deg high
            if (direction.y > -0.57f)
            {
                var flat = new Vector3(direction.x, 0f, direction.z).normalized;
                direction = (flat * 0.82f + Vector3.down * 0.57f).normalized;
            }
            // from the user's side of the objects: lit from behind, the side the user sees was in shadow
            // and downloaded (lit) models went dark a few seconds after appearing (seen: a dog). The
            // shadows then fall away from the user, which reads well anyway.
            var cam = Camera.main != null ? Camera.main.transform : null;
            if (cam != null)
            {
                var fwd = Vector3.ProjectOnPlane(cam.forward, Vector3.up).normalized;
                var flat = new Vector3(direction.x, 0f, direction.z);
                float along = Vector3.Dot(flat, fwd);
                if (along < 0f) direction = (direction - 2f * along * fwd).normalized;
            }
            LightDirection = direction;
            HasEstimate = true;
            if (!_settings.matchRoomLight)
            {
                // only the floor shadows follow the room's light; the objects keep the standard lighting
                Debug.Log($"[WhatIf] light estimation: main light from {(-direction):F2} (for the floor shadows)");
                return;
            }
            var sun = RenderSettings.sun;
            if (sun != null) sun.transform.rotation = Quaternion.LookRotation(direction, Vector3.up);
            // colour and strength from the measured light (relative to lightEstimateReference, kept within
            // 0.5-1.3x like before), the ambient light from the spherical harmonics
            float max = colour.maxColorComponent;
            var tint = max > 1e-4f ? Color.Lerp(Color.white, colour / max, 0.6f) : Color.white;
            // brighter in a bright room, but never darker than the configured light: dimming the scene
            // after it appeared looked like a shade falling over the objects
            float scale = Mathf.Max(1f, Luminance(colour) / Mathf.Max(1e-4f, _settings.lightEstimateReference));
            var amb = Ambient(harmonics);
            Color? ambTint = amb.HasValue && amb.Value.maxColorComponent > 1e-4f
                ? Color.Lerp(Color.white, amb.Value / amb.Value.maxColorComponent, 0.6f) : (Color?)null;
            SceneLighting.SetRoom(_settings, tint, scale, ambTint, "light estimation");
            Debug.Log($"[WhatIf] light estimation: main light from {(-direction):F2}, colour {colour.r:F3} {colour.g:F3} {colour.b:F3}" +
                      $" (luminance {Luminance(colour):F3}), ambient {(amb.HasValue ? $"{amb.Value.r:F3} {amb.Value.g:F3} {amb.Value.b:F3}" : "-")}");
        }
    }
}
