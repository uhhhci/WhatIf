using UnityEditor;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using WhatIf.Content;
using WhatIf.Scene;
using WhatIf.UI;

namespace WhatIf.EditorTools
{
    /// <summary>Menu: What If > Add To Open Scene. Wires all components into the current scene.</summary>
    public static class WhatIfSetup
    {
        const string SettingsPath = "Assets/WhatIf/WhatIfSettings.asset";

        [MenuItem("What If/Add To Open Scene")]
        public static void AddToScene()
        {
            var settings = AssetDatabase.LoadAssetAtPath<WhatIfSettings>(SettingsPath);
            if (settings == null)
            {
                settings = ScriptableObject.CreateInstance<WhatIfSettings>();
                AssetDatabase.CreateAsset(settings, SettingsPath);
                AssetDatabase.SaveAssets();
            }

            var cam = Camera.main;
            if (cam == null)
            {
                EditorUtility.DisplayDialog("What If", "No Main Camera found. Add an XR Origin (Magic Leap rig) first.", "OK");
                return;
            }
            var planes = Object.FindObjectOfType<ARPlaneManager>();
            if (planes == null)
            {
                var origin = Object.FindObjectOfType<Unity.XR.CoreUtils.XROrigin>();
                if (origin != null) planes = Undo.AddComponent<ARPlaneManager>(origin.gameObject);
            }

            // Walk around in Play mode without a headset (disables itself on device).
            if (cam.GetComponent<EditorFlyCamera>() == null) Undo.AddComponent<EditorFlyCamera>(cam.gameObject);

            var root = new GameObject("What If");
            Undo.RegisterCreatedObjectUndo(root, "Add What If");
            var scanner = root.AddComponent<SceneScanner>();
            scanner.planeManager = planes;
            scanner.head = cam.transform;
            var frames = root.AddComponent<WebCamFrameSource>();
            var effects = root.AddComponent<EnvironmentEffects>();

            var hudGo = new GameObject("HUD");
            hudGo.transform.SetParent(root.transform, false);
            var hud = hudGo.AddComponent<HeadsetHud>();
            hud.head = cam.transform;

            var controller = root.AddComponent<WhatIfController>();
            controller.settings = settings;
            controller.scanner = scanner;
            controller.cameraFrame = frames;
            controller.effects = effects;
            controller.hud = hud;
            controller.head = cam.transform;

            Selection.activeObject = settings;
            Debug.Log("[WhatIf] Added. Set the server URL in Assets/WhatIf/WhatIfSettings.asset." +
                      (planes == null ? " WARNING: no ARPlaneManager/XROrigin found - add the Magic Leap rig." : ""));
        }
    }
}
