using UnityEngine;
using WhatIf.Scene;

namespace WhatIf.Content
{
    /// <summary>
    /// Feeds the planar shadows (WhatIf/PlanarShadow on each 3D model's shadow copy): the direction of
    /// the room's main light from the Magic Leap's light estimation (until there is one: a light from
    /// above, slightly behind the user), the floor height and the strength. Only the shadows follow
    /// the room's light; the objects keep their standard lighting.
    /// </summary>
    public class PlanarShadows : MonoBehaviour
    {
        SceneScanner _scanner;
        WhatIfSettings _settings;
        Transform _head;
        Vector3 _default;

        static readonly int Dir = Shader.PropertyToID("_WhatIfShadowDir");
        static readonly int Floor = Shader.PropertyToID("_WhatIfFloorY");
        static readonly int Strength = Shader.PropertyToID("_WhatIfShadowStrength");

        public void Init(SceneScanner scanner, Transform head, WhatIfSettings settings)
        {
            _scanner = scanner;
            _head = head;
            _settings = settings;
            // until the room's light is known: from above and a little behind the user (shadows fall
            // away from the user, a little forward)
            var fwd = head != null ? Vector3.ProjectOnPlane(head.forward, Vector3.up).normalized : Vector3.forward;
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            _default = (Vector3.down + fwd * 0.45f).normalized;
        }

        void Update()
        {
            if (_settings == null) return;
            var d = RoomLightEstimate.LightDirection ?? _default;
            Shader.SetGlobalVector(Dir, new Vector4(d.x, d.y, d.z, 0f));
            Shader.SetGlobalFloat(Floor, _scanner != null ? _scanner.FloorY : 0f);
            Shader.SetGlobalFloat(Strength, _settings.castShadowStrength);
        }
    }
}
