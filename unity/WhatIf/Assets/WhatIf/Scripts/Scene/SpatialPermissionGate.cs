using UnityEngine;

namespace WhatIf.Scene
{
    /// <summary>
    /// Magic Leap 2: plane extraction and meshing only start if the spatial mapping permission is
    /// already granted when their managers are enabled. The managers are therefore left disabled in
    /// the scene and switched on here once the user has allowed it (the controller asks at start).
    /// In the Editor and on other platforms they are enabled right away.
    /// </summary>
    public class SpatialPermissionGate : MonoBehaviour
    {
        const string Permission = "com.magicleap.permission.SPATIAL_MAPPING";

        public Behaviour[] targets;
        float _nextCheck;

        void Update()
        {
            if (Time.unscaledTime < _nextCheck) return;
            _nextCheck = Time.unscaledTime + 0.5f;
            if (!Granted()) return;
            foreach (var t in targets)
                if (t != null) t.enabled = true;
            Debug.Log("[WhatIf] spatial mapping permission granted: planes and meshing on");
            enabled = false;
        }

        static bool Granted()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            return UnityEngine.Android.Permission.HasUserAuthorizedPermission(Permission);
#else
            return true;
#endif
        }
    }
}
