using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace WhatIf.Content
{
    /// <summary>
    /// Magic Leap 2 segmented dimming: the display darkens the real world behind every pixel by the
    /// pixel's alpha, so virtual content looks solid instead of see-through, dark colours become
    /// visible, contact shadows darken the real floor and paint covers a real wall.
    ///
    /// What it needs, all set here on the headset:
    /// - the Magic Leap rendering feature's blend mode set to alpha blend (reached by reflection, so
    ///   the project compiles without the Magic Leap SDK; enable "Magic Leap 2 Rendering Extensions" /
    ///   "Segmented Dimmer" in the OpenXR feature list),
    /// - a transparent black camera background (alpha 0 = no dimming where nothing is drawn),
    /// - a colour buffer that keeps alpha: no HDR (URP's HDR format has no alpha channel) and no
    ///   post-processing.
    /// Materials then control it: opaque = fully dimmed, transparent = their alpha.
    /// Nothing changes in the Editor.
    /// </summary>
    public static class SegmentedDimming
    {
        public static void Apply(WhatIfSettings settings, Camera cam)
        {
            if (settings == null) return;
            // On the headset, also before the XR display reports itself active: that check skipped the
            // whole setup at start (HDR stayed on - its colour buffer has no alpha - so nothing dimmed:
            // objects see-through, floor shadows invisible; this log line never appeared)
#if !UNITY_ANDROID || UNITY_EDITOR
            return;
#endif

            // The see-through background is needed with and without dimming (else: Unity's skybox).
            if (cam != null)
            {
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
                cam.allowHDR = false;
                var data = cam.GetComponent<UniversalAdditionalCameraData>();
                if (data != null) data.renderPostProcessing = false;
            }
            if (GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset urp) urp.supportsHDR = false;

            Debug.Log("[WhatIf] segmented dimming: " +
                      (settings.segmentedDimming ? SetBlendMode("AlphaBlend") : "off, " + SetBlendMode("Additive")));
        }

        /// <summary>Sets MagicLeapRenderingExtensionsFeature.BlendMode (Magic Leap Unity SDK 2.x):
        /// AlphaBlend = segmented dimming, Additive = plain see-through display.</summary>
        static string SetBlendMode(string mode)
        {
            try
            {
                Type featureType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type[] types;
                    try { types = asm.GetTypes(); }
                    catch (ReflectionTypeLoadException e) { types = e.Types; }
                    foreach (var t in types)
                        if (t != null && t.Name == "MagicLeapRenderingExtensionsFeature") { featureType = t; break; }
                    if (featureType != null) break;
                }
                if (featureType == null) return "Magic Leap SDK not found (install com.magicleap.unitysdk)";

                var openXr = UnityEngine.XR.OpenXR.OpenXRSettings.Instance;
                var feature = openXr != null ? openXr.GetFeature(featureType) : null;
                if (feature == null) return "rendering feature missing in the OpenXR settings";
                if (!feature.enabled) return "enable \"Magic Leap 2 Rendering Extensions\" in the OpenXR features";

                // SDK 2.x: a public field (synchronised to the runtime every frame); a property in others
                const BindingFlags pub = BindingFlags.Public | BindingFlags.Instance;
                var field = featureType.GetField("BlendMode", pub);
                if (field != null && field.FieldType.IsEnum)
                {
                    field.SetValue(feature, Enum.Parse(field.FieldType, mode));
                    return "blend mode " + mode;
                }
                var prop = featureType.GetProperty("BlendMode", pub);
                if (prop == null || !prop.PropertyType.IsEnum || !prop.CanWrite) return "no BlendMode in this SDK version";
                prop.SetValue(feature, Enum.Parse(prop.PropertyType, mode));
                return "blend mode " + mode;
            }
            catch (Exception e)
            {
                return "failed: " + e.Message;
            }
        }
    }
}
