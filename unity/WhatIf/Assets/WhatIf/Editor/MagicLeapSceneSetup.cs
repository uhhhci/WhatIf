using System.Collections.Generic;
using System.Text;
using Unity.XR.CoreUtils;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using WhatIf.Scene;
using TrackedPoseDriver = UnityEngine.InputSystem.XR.TrackedPoseDriver;

namespace WhatIf.EditorTools
{
    /// <summary>
    /// Menu: What If > Set Up Scene for ML2. Adds what the headset needs to the open scene and wires
    /// it to the What If components:
    /// - AR Session,
    /// - XR Origin with Camera Offset; the existing Main Camera moves under it and gets a Tracked
    ///   Pose Driver (head pose),
    /// - AR Plane Manager (walls, floor, tables) and AR Anchor Manager on the origin,
    /// - AR Mesh Manager with collider-only chunks (floor raycasts where no floor plane exists),
    /// - a SpatialPermissionGate that enables planes and meshing once the permission is granted,
    /// - the scene in the build list.
    /// Safe to run again: existing pieces are reused.
    /// </summary>
    public static class MagicLeapSceneSetup
    {
        const string MeshPrefabPath = "Assets/WhatIf/Prefabs/RoomMeshChunk.prefab";

        [MenuItem("What If/Set Up Scene for ML2")]
        public static void Setup()
        {
            var log = new StringBuilder();
            var scene = EditorSceneManager.GetActiveScene();
            if (string.IsNullOrEmpty(scene.path))
            {
                EditorUtility.DisplayDialog("What If", "Save the scene first.", "OK");
                return;
            }
            var controller = Object.FindObjectOfType<WhatIfController>();
            if (controller == null)
            {
                EditorUtility.DisplayDialog("What If", "No What If object in this scene. Run What If > Add To Open Scene first.", "OK");
                return;
            }
            var cam = controller.head != null ? controller.head.GetComponent<Camera>() : Camera.main;
            if (cam == null) cam = Camera.main;
            if (cam == null)
            {
                EditorUtility.DisplayDialog("What If", "No camera found (tag one MainCamera).", "OK");
                return;
            }

            // 1) AR Session
            if (Object.FindObjectOfType<ARSession>() == null)
            {
                var session = new GameObject("AR Session");
                Undo.RegisterCreatedObjectUndo(session, "AR Session");
                session.AddComponent<ARSession>();
                session.AddComponent<ARInputManager>();
                log.AppendLine("AR Session: added");
            }
            else log.AppendLine("AR Session: exists");

            // 2) XR Origin + camera
            var origin = Object.FindObjectOfType<XROrigin>();
            if (origin == null)
            {
                var go = new GameObject("XR Origin");
                Undo.RegisterCreatedObjectUndo(go, "XR Origin");
                origin = go.AddComponent<XROrigin>();
                log.AppendLine("XR Origin: added");
            }
            else log.AppendLine("XR Origin: exists");

            var offset = origin.CameraFloorOffsetObject;
            if (offset == null)
            {
                offset = new GameObject("Camera Offset");
                Undo.RegisterCreatedObjectUndo(offset, "Camera Offset");
                offset.transform.SetParent(origin.transform, false);
            }
            if (cam.transform.parent != offset.transform)
            {
                Undo.SetTransformParent(cam.transform, offset.transform, "Camera under XR Origin");
                log.AppendLine("Main Camera: moved under XR Origin/Camera Offset");
            }
            Undo.RecordObject(cam.transform, "Camera pose");
            cam.transform.localPosition = Vector3.zero;
            cam.transform.localRotation = Quaternion.identity;
            Undo.RecordObject(cam, "Camera clip");
            cam.nearClipPlane = 0.1f;

            Undo.RecordObject(origin, "XR Origin");
            origin.Camera = cam;
            origin.CameraFloorOffsetObject = offset;
            origin.RequestedTrackingOriginMode = XROrigin.TrackingOriginMode.NotSpecified;
            origin.CameraYOffset = 0f;
            EditorUtility.SetDirty(origin);

            if (cam.GetComponent<TrackedPoseDriver>() == null)
            {
                var tpd = Undo.AddComponent<TrackedPoseDriver>(cam.gameObject);
                tpd.positionInput = new InputActionProperty(new InputAction("Position",
                    binding: "<XRHMD>/centerEyePosition", expectedControlType: "Vector3"));
                tpd.rotationInput = new InputActionProperty(new InputAction("Rotation",
                    binding: "<XRHMD>/centerEyeRotation", expectedControlType: "Quaternion"));
                tpd.trackingStateInput = new InputActionProperty(new InputAction("Tracking State",
                    binding: "<XRHMD>/trackingState", expectedControlType: "Integer"));
                EditorUtility.SetDirty(tpd);
                log.AppendLine("Tracked Pose Driver: added (head pose)");
            }

            // 3) Planes + anchors on the origin
            var planes = origin.GetComponent<ARPlaneManager>();
            if (planes == null) planes = Undo.AddComponent<ARPlaneManager>(origin.gameObject);
            Undo.RecordObject(planes, "Planes");
            planes.requestedDetectionMode = PlaneDetectionMode.Horizontal | PlaneDetectionMode.Vertical;
            planes.enabled = false; // SpatialPermissionGate enables it
            if (origin.GetComponent<ARAnchorManager>() == null) Undo.AddComponent<ARAnchorManager>(origin.gameObject);
            log.AppendLine("AR Plane Manager + AR Anchor Manager: on XR Origin");

            // 4) Meshing (child of the origin, as AR Foundation requires)
            var meshes = origin.GetComponentInChildren<ARMeshManager>(true);
            if (meshes == null)
            {
                var go = new GameObject("Room Mesh");
                Undo.RegisterCreatedObjectUndo(go, "Room Mesh");
                go.transform.SetParent(origin.transform, false);
                meshes = go.AddComponent<ARMeshManager>();
            }
            Undo.RecordObject(meshes, "Meshing");
            meshes.meshPrefab = MeshChunkPrefab().GetComponent<MeshFilter>();
            meshes.density = 0.5f;
            meshes.enabled = false; // SpatialPermissionGate enables it
            log.AppendLine("AR Mesh Manager: added (collider-only chunks, for floor raycasts)");

            // 5) Wire into What If
            var scanner = controller.scanner;
            if (scanner != null)
            {
                Undo.RecordObject(scanner, "Scanner");
                scanner.planeManager = planes;
                scanner.head = cam.transform;
                EditorUtility.SetDirty(scanner);
            }
            Undo.RecordObject(controller, "Controller");
            controller.head = cam.transform;
            EditorUtility.SetDirty(controller);

            var gate = controller.GetComponent<SpatialPermissionGate>();
            if (gate == null) gate = Undo.AddComponent<SpatialPermissionGate>(controller.gameObject);
            Undo.RecordObject(gate, "Gate");
            gate.targets = new Behaviour[] { planes, meshes };
            gate.enabled = true;
            EditorUtility.SetDirty(gate);
            log.AppendLine("What If: wired (planes, head), permission gate added");

            // 6) Scene in the build
            var list = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            if (!list.Exists(s => s.path == scene.path))
            {
                list.Insert(0, new EditorBuildSettingsScene(scene.path, true));
                EditorBuildSettings.scenes = list.ToArray();
                log.AppendLine("Build list: " + scene.path + " added");
            }

            // 7) Shaders the scripts look up by name (Shader.Find): a player build only contains
            // shaders that something references, so without this the controller can't make materials.
            IncludeShaders(log, "Universal Render Pipeline/Unlit", "Universal Render Pipeline/Particles/Unlit");

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log("[WhatIf] Set Up Scene for ML2:\n" + log);
            EditorUtility.DisplayDialog("What If - ML2 scene", log + "\nScene saved.", "OK");
        }

        /// <summary>Adds shaders to Project Settings > Graphics > Always Included Shaders.</summary>
        static void IncludeShaders(StringBuilder log, params string[] names)
        {
            var graphics = AssetDatabase.LoadAssetAtPath<Object>("ProjectSettings/GraphicsSettings.asset");
            if (graphics == null) { log.AppendLine("! GraphicsSettings not found"); return; }
            var so = new SerializedObject(graphics);
            var list = so.FindProperty("m_AlwaysIncludedShaders");
            foreach (var name in names)
            {
                var shader = Shader.Find(name);
                if (shader == null) { log.AppendLine("! shader not found: " + name); continue; }
                bool present = false;
                for (int i = 0; i < list.arraySize; i++)
                    if (list.GetArrayElementAtIndex(i).objectReferenceValue == shader) present = true;
                if (present) continue;
                list.InsertArrayElementAtIndex(list.arraySize);
                list.GetArrayElementAtIndex(list.arraySize - 1).objectReferenceValue = shader;
                log.AppendLine("Always included shader: " + name);
            }
            so.ApplyModifiedPropertiesWithoutUndo();
            AssetDatabase.SaveAssets();
        }

        /// <summary>Mesh chunk with a collider only: invisible, but raycasts hit the room.</summary>
        static GameObject MeshChunkPrefab()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(MeshPrefabPath);
            if (prefab != null) return prefab;
            if (!AssetDatabase.IsValidFolder("Assets/WhatIf/Prefabs"))
                AssetDatabase.CreateFolder("Assets/WhatIf", "Prefabs");
            var go = new GameObject("RoomMeshChunk");
            go.AddComponent<MeshFilter>();
            go.AddComponent<MeshCollider>();
            prefab = PrefabUtility.SaveAsPrefabAsset(go, MeshPrefabPath);
            Object.DestroyImmediate(go);
            return prefab;
        }
    }
}
