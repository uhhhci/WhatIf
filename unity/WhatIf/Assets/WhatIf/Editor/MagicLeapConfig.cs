using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEngine;
using UnityEngine.XR.OpenXR;

namespace WhatIf.EditorTools
{
    /// <summary>
    /// Menu: What If > Configure Magic Leap 2. Switches on what the app uses on the headset, so nobody
    /// has to tick a dozen boxes: OpenXR as the Android XR loader and the Magic Leap OpenXR features
    /// (support, rendering extensions for segmented dimming, planes, meshing, anchors, controller and
    /// eye gaze profiles). The Android player settings are checked in Project Validation; texture
    /// compression (DXT) must be set by hand in Player Settings. Then run What If > Set Up Scene for ML2.
    /// </summary>
    public static class MagicLeapConfig
    {
        // OpenXR feature type names (Magic Leap Unity SDK 2.x and Unity OpenXR)
        internal static readonly string[] Features =
        {
            "MagicLeapFeature",                    // Magic Leap 2 Support
            "MagicLeapRenderingExtensionsFeature", // segmented / global dimming
            "MagicLeapReferenceSpacesFeature",
            "MagicLeapPlanesFeature",              // walls, floor, tables (+ classification)
            "MagicLeapMeshingFeature",             // room mesh: floor raycasts, occlusion
            "MagicLeapSpatialAnchorsFeature",      // ARAnchor on placed objects
            "MagicLeapControllerProfile",
            "EyeGazeInteraction",                  // "it" / "this"
            "HandInteractionProfile",              // pinch to grab and move objects
            "MagicLeapLightEstimationFeature",     // where the room's light comes from (shadows)
        };

        /// <summary>Magic Leap 2 Support's "Perception Snapshots": the camera pose of a picture
        /// (MLCVCamera.GetFramePose) needs them, otherwise every frame says PoseNotFound.</summary>
        internal static bool EnsurePerceptionSnapshots(UnityEngine.XR.OpenXR.Features.OpenXRFeature f)
        {
            if (f.GetType().Name != "MagicLeapFeature") return false;
            var so = new SerializedObject(f);
            var prop = so.FindProperty("perceptionSnapshots");
            if (prop == null || prop.boolValue) return false;
            prop.boolValue = true;
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(f);
            return true;
        }

        [MenuItem("What If/Configure Magic Leap 2")]
        public static void Configure()
        {
            var log = new StringBuilder();

            // 1) OpenXR loader for Android
            var general = XRGeneralSettingsPerBuildTarget.XRGeneralSettingsForBuildTarget(BuildTargetGroup.Android);
            if (general == null || general.Manager == null)
                log.AppendLine("! Open Project Settings > XR Plug-in Management once and select the Android tab, then run this again.");
            else
            {
                if (XRPackageMetadataStore.AssignLoader(general.Manager, "UnityEngine.XR.OpenXR.OpenXRLoader", BuildTargetGroup.Android))
                    log.AppendLine("OpenXR loader: on (Android)");
                general.InitManagerOnStart = true;
                EditorUtility.SetDirty(general);
                EditorUtility.SetDirty(general.Manager);
            }

            // 2) OpenXR features
            var openXr = OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Android);
            if (openXr == null) log.AppendLine("! No OpenXR settings for Android yet.");
            else
            {
                var found = new HashSet<string>();
                foreach (var f in openXr.GetFeatures())
                {
                    var name = f.GetType().Name;
                    if (System.Array.IndexOf(Features, name) < 0) continue;
                    found.Add(name);
                    if (!f.enabled) { f.enabled = true; EditorUtility.SetDirty(f); }
                    log.AppendLine($"{name}: on");
                    if (EnsurePerceptionSnapshots(f)) log.AppendLine("Perception Snapshots: on (camera pose)");
                }
                foreach (var name in Features)
                    if (!found.Contains(name)) log.AppendLine($"! {name} not found (Magic Leap SDK installed?)");
                EditorUtility.SetDirty(openXr);
            }

            // segmented dimming reads the frame's alpha (objects solid, shadows on the floor visible)
            PlayerSettings.preserveFramebufferAlpha = true;
            log.AppendLine("Preserve framebuffer alpha: on (segmented dimming)");

            AssetDatabase.SaveAssets();
            Debug.Log("[WhatIf] Configure Magic Leap 2:\n" + log);
            EditorUtility.DisplayDialog("What If - Magic Leap 2", log +
                "\nAlso: Player Settings > Android > Texture compression formats = DXT only, " +
                "then What If > Set Up Scene for ML2.", "OK");
        }
    }
}
