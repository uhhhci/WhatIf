using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.XR.OpenXR;

namespace WhatIf.EditorTools
{
    /// <summary>
    /// Before every Android build: switches on the OpenXR features (and Perception Snapshots) What If needs (hand interaction was
    /// left off once and grabbing silently did nothing on the headset).
    /// </summary>
    class RequiredFeaturesCheck : IPreprocessBuildWithReport
    {
        public int callbackOrder => -1000; // before OpenXR's own build steps read the features

        public void OnPreprocessBuild(BuildReport report)
        {
            if (report.summary.platform != BuildTarget.Android) return;
            // segmented dimming reads the frame's alpha: without this Android may not keep it, and then
            // nothing dims - objects look see-through, shadows on the floor are invisible
            // ... and a colour buffer with alpha: URP's HDR format has none
            if (UnityEngine.Rendering.GraphicsSettings.renderPipelineAsset is UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset urp
                && urp.supportsHDR)
            {
                urp.supportsHDR = false;
                EditorUtility.SetDirty(urp);
                Debug.LogWarning("[WhatIf] HDR was on in the render pipeline - switched off (segmented dimming needs alpha)");
            }
            if (!PlayerSettings.preserveFramebufferAlpha)
            {
                PlayerSettings.preserveFramebufferAlpha = true;
                Debug.LogWarning("[WhatIf] Preserve framebuffer alpha was off - switched on (segmented dimming)");
            }
            var openXr = OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Android);
            if (openXr == null) return;
            foreach (var f in openXr.GetFeatures())
            {
                if (MagicLeapConfig.EnsurePerceptionSnapshots(f))
                    Debug.LogWarning("[WhatIf] Perception Snapshots were off - switched on for this build (camera pose)");
                if (System.Array.IndexOf(MagicLeapConfig.Features, f.GetType().Name) < 0 || f.enabled) continue;
                f.enabled = true;
                EditorUtility.SetDirty(f);
                Debug.LogWarning($"[WhatIf] OpenXR feature {f.GetType().Name} was off - switched on for this build");
            }
            AssetDatabase.SaveAssets();
        }
    }
}
