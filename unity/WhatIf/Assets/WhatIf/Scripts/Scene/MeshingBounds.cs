using UnityEngine;
#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features.MagicLeapSupport;
#endif

namespace WhatIf.Scene
{
    /// <summary>
    /// Magic Leap 2 meshing only delivers mesh inside a query box, which starts out with size zero:
    /// without this the room mesh stays empty (no floor raycasts, no image targets). Keeps an
    /// 8 m box centred on the head and logs how many mesh chunks exist now and then.
    /// Added by the controller at start; does nothing in the Editor.
    /// </summary>
    public class MeshingBounds : MonoBehaviour
    {
        public float sizeM = 8f;

#if UNITY_ANDROID && !UNITY_EDITOR
        float _next, _nextLog = 10f;
        bool _configured;

        void Update()
        {
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 1f;
            var feature = OpenXRSettings.Instance != null ? OpenXRSettings.Instance.GetFeature<MagicLeapMeshingFeature>() : null;
            var cam = Camera.main;
            if (feature == null || !feature.enabled || cam == null) return;
            try
            {
                if (!_configured)
                {
                    feature.MeshRenderMode = MagicLeapMeshingFeature.MeshingMode.Triangles;
                    feature.UpdateMeshQuerySettings(MagicLeapMeshingFeature.MeshingQuerySettings.DefaultSettings());
                    _configured = true;
                    Debug.Log($"[WhatIf] meshing: query box {sizeM:0} m around the head");
                }
                // the box is in tracking space: the XR camera's parent (Camera Offset)
                var space = cam.transform.parent;
                var pos = space != null ? space.InverseTransformPoint(cam.transform.position) : cam.transform.position;
                feature.SetMeshQueryBounds(pos, Vector3.one * sizeM, Quaternion.identity);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[WhatIf] meshing: " + e.Message);
                enabled = false;
            }
            if (Time.unscaledTime > _nextLog)
            {
                _nextLog = Time.unscaledTime + 30f;
                var meshes = FindObjectOfType<ARMeshManager>();
                Debug.Log($"[WhatIf] meshing: {(meshes != null && meshes.meshes != null ? meshes.meshes.Count : 0)} mesh chunks");
            }
        }
#endif
    }
}
