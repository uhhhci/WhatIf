using UnityEngine;
using WhatIf.Content;
using WhatIf.Scene;

namespace WhatIf.UI
{
    /// <summary>
    /// Editor-only grid floor (1 m cells) at the height the app assumes the ground is, so placed
    /// objects visibly stand on something. Never used on the headset: there the real floor is visible.
    /// Created by WhatIfController when <see cref="WhatIfSettings.editorGround"/> is on.
    /// </summary>
    public class EditorGround : MonoBehaviour
    {
        public SceneScanner scanner;
        public Transform head;
        public WhatIfSettings settings;

        const float SizeM = 200f; // Unity's plane primitive is 10 x 10 m

        public static EditorGround Create(SceneScanner scanner, Transform head, WhatIfSettings settings)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Plane);
            go.name = "WhatIf Editor Ground";
            // No collider: SceneScanner raycasts down to find the floor and must not hit this.
            Destroy(go.GetComponent<Collider>());
            go.transform.localScale = new Vector3(SizeM / 10f, 1f, SizeM / 10f);

            var mat = MaterialFactory.Opaque();
            MaterialFactory.SetTexture(mat, GridTexture(), Vector2.one * SizeM); // 1 texture tile = 1 m
            go.GetComponent<MeshRenderer>().sharedMaterial = mat;

            var ground = go.AddComponent<EditorGround>();
            ground.scanner = scanner;
            ground.head = head;
            ground.settings = settings;
            ground.LateUpdate();
            return ground;
        }

        void LateUpdate()
        {
            if (head == null) return;
            float y = scanner != null && scanner.HasFloorEstimate
                ? scanner.FloorY
                : head.position.y - settings.defaultHeadHeightM;
            // Follow the user in whole metres so the grid lines stay put in the world.
            transform.position = new Vector3(Mathf.Round(head.position.x), y, Mathf.Round(head.position.z));
        }

        static Texture2D GridTexture()
        {
            const int n = 64;
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, mipChain: true)
            {
                name = "WhatIfGrid",
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Trilinear,
                anisoLevel = 8,
            };
            var fill = new Color(0.32f, 0.33f, 0.35f);
            var line = new Color(0.55f, 0.57f, 0.60f);
            var px = new Color[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                    px[y * n + x] = x < 2 || y < 2 ? line : fill; // lines on two edges -> continuous grid
            tex.SetPixels(px);
            tex.Apply(updateMipmaps: true, makeNoLongerReadable: true);
            return tex;
        }
    }
}
