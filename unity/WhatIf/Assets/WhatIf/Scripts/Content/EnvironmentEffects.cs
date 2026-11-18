using System.Collections.Generic;
using UnityEngine;
using WhatIf.Api;

namespace WhatIf.Content
{
    /// <summary>
    /// Scene-wide effects that are not individual objects, built from blocks by the planner:
    /// `level` (a layer at a height: water, fog, a sea of lava), `surface` (a look over detected
    /// real surfaces: paint, a lava or ice floor, a starry ceiling) and `tint` (mood). The looks come
    /// from the WhatIf/Surface shader (SurfaceLook); nothing here knows water or lava.
    /// </summary>
    public class EnvironmentEffects : MonoBehaviour
    {
        readonly List<GameObject> _active = new List<GameObject>();
        readonly List<Effect> _shown = new List<Effect>(); // levels / tint, for the editor
        readonly List<Effect> _particleEffects = new List<Effect>();
        SurfacePaint _paint;

        /// <summary>Paint on real surfaces (created on first use).</summary>
        public SurfacePaint Paint(Scene.SceneScanner scanner, WhatIfSettings settings)
        {
            if (_paint == null) _paint = GetComponent<SurfacePaint>();
            if (_paint == null) _paint = gameObject.AddComponent<SurfacePaint>(); // not ??: Unity fake-null
            _paint.scanner = scanner;
            _paint.settings = settings;
            return _paint;
        }

        /// <param name="groundCoat">The scene covers the floor (sand, snow): shallow water lies beside it.</param>
        public void Apply(IEnumerable<Effect> effects, float floorY, Vector3 center, WhatIfSettings settings,
                          Scene.SceneScanner scanner = null, bool groundCoat = false)
        {
            Clear();
            Shore = null;
            WaterY = null;
            foreach (var e in effects)
            {
                if ((e.Type == "level" || e.Type == "water_level") && e.WaterHeightM.HasValue)
                {
                    // at least 6 cm up: lower, it z-fought with sand patches, floor paint and the shadow
                    // catcher lying on the floor (seen at a beach)
                    float y = floorY + Mathf.Max(0.06f, e.WaterHeightM.Value);
                    WaterY = y;
                    // shallow water with a floor coat (sand at a beach): beside it, not over it - from a
                    // shoreline in front of the user outwards; no coat pieces go into the water
                    bool shore = groundCoat && e.WaterHeightM.Value < 0.4f;
                    _active.Add(Level(y, center, e, shore));
                    var p = SurfaceLook.CreateParticles(e, new Vector3(center.x, y, center.z), new Vector3(8f, 2f, 8f), transform);
                    if (p != null) _active.Add(p);
                }
                else if (e.Type == "tint")
                    _active.Add(Tint(e.TintHex, e.Strength));
                else if ((e.Type == "surface" || e.Type == "surface_paint") && scanner != null)
                {
                    var paint = Paint(scanner, settings);
                    foreach (var s in e.Surfaces) paint.Set(s, e);
                    // particles over the floor area around the user (embers from a lava floor, snow)
                    bool ceiling = e.Surfaces.Contains("ceiling");
                    var at = new Vector3(center.x, ceiling ? floorY + 0.2f : floorY, center.z);
                    var ps = SurfaceLook.CreateParticles(e, at, new Vector3(8f, ceiling ? 2.3f : 2f, 8f), transform);
                    if (ps != null) { _active.Add(ps); _particleEffects.Add(e); }
                    continue;
                }
                else continue;
                _shown.Add(e);
            }
        }

        /// <summary>Does the ray hit a changed real surface (paint)? For the voice gate.</summary>
        public bool SurfaceHit(Ray ray) => _paint != null && _paint.HasPaint && _paint.Hits(ray);

        /// <summary>What is shown now (water, tint, paint), so edits can refer to it.</summary>
        public List<Effect> Current()
        {
            var list = new List<Effect>(_shown);
            if (_paint != null) list.AddRange(_paint.Describe());
            return list;
        }

        public void Clear()
        {
            foreach (var go in _active) if (go != null) Destroy(go);
            _active.Clear();
            _shown.Clear();
            WaterY = null;
            Shore = null;
            _particleEffects.Clear();
            if (_paint != null) _paint.Clear();
        }

        /// <summary>A layer at a height (water, fog, lava) - large; real walls hide what is beyond them.</summary>
        /// <summary>Where shallow water begins (a point on the shoreline and the direction into the
        /// water), while such water is shown; coat pieces stay out of it.</summary>
        public static (Vector3 point, Vector3 into)? Shore;

        /// <summary>Height of the water surface while water is shown (swimmers stay at it).</summary>
        public static float? WaterY;

        public static bool InWater(Vector3 p) =>
            Shore.HasValue && Vector3.Dot(p - Shore.Value.point, Shore.Value.into) > 0f;

        GameObject Level(float y, Vector3 center, Effect e, bool shore)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Plane); // 10 x 10 m
            go.name = "WhatIf Level";
            Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(transform, false);
            if (shore)
            {
                var cam = Camera.main != null ? Camera.main.transform : transform;
                var into = Vector3.ProjectOnPlane(cam.forward, Vector3.up);
                into = into.sqrMagnitude > 1e-4f ? into.normalized : Vector3.forward;
                var line = new Vector3(cam.position.x, y, cam.position.z) + into * 1.6f;
                Shore = (line, into);
                go.transform.SetPositionAndRotation(line + into * 15f, Quaternion.LookRotation(into, Vector3.up));
                go.transform.localScale = new Vector3(6f, 1f, 3f); // 60 m wide, 30 m out from the shore
            }
            else
            {
                go.transform.position = new Vector3(center.x, y, center.z);
                go.transform.localScale = new Vector3(6f, 1f, 6f); // 60 x 60 m
            }
            var mat = SurfaceLook.CreateMaterial(e);
            // drawn after the other see-through things: sand patches under the water stay under it
            // (sorted by distance, some were drawn on top of the water)
            mat.renderQueue = 3100;
            go.GetComponent<MeshRenderer>().sharedMaterial = mat;
            return go;
        }

        GameObject Tint(string hex, float strength)
        {
            // A large inverted sphere around the user, tinted and semi-transparent.
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = "WhatIf Tint";
            Destroy(go.GetComponent<Collider>());
            var cam = Camera.main != null ? Camera.main.transform : transform;
            go.transform.SetParent(cam, false);
            go.transform.localScale = Vector3.one * -4f; // negative scale flips faces inwards
            if (!ColorUtility.TryParseHtmlString(hex ?? "#304060", out var c)) c = new Color(0.2f, 0.25f, 0.4f);
            c.a = Mathf.Lerp(0.1f, 0.5f, strength);
            var mat = MaterialFactory.Transparent();
            MaterialFactory.SetColor(mat, c);
            go.GetComponent<MeshRenderer>().sharedMaterial = mat;
            return go;
        }
    }

    public static class MaterialFactory
    {
        public static Material Transparent()
        {
            var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Transparent");
            var mat = new Material(shader);
            if (shader.name.StartsWith("Universal"))
            {
                mat.SetFloat("_Surface", 1); // transparent
                mat.SetFloat("_Blend", 0);
                mat.SetOverrideTag("RenderType", "Transparent");
                mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                // Alpha accumulates like coverage ("over"): with ML2 segmented dimming the frame's
                // alpha is how much of the real world is dimmed behind the pixel.
                if (mat.HasProperty("_SrcBlendAlpha")) mat.SetInt("_SrcBlendAlpha", (int)UnityEngine.Rendering.BlendMode.One);
                if (mat.HasProperty("_DstBlendAlpha")) mat.SetInt("_DstBlendAlpha", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                mat.SetInt("_ZWrite", 0);
                mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            }
            return mat;
        }

        public static Material AlphaClip()
        {
            var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Transparent Cutout");
            var mat = new Material(shader);
            if (shader.name.StartsWith("Universal"))
            {
                mat.SetFloat("_AlphaClip", 1);
                mat.SetFloat("_Cutoff", 0.5f);
                mat.SetFloat("_Cull", 0); // double sided
                mat.EnableKeyword("_ALPHATEST_ON");
                mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.AlphaTest;
            }
            return mat;
        }

        public static Material Opaque()
        {
            var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Texture");
            return new Material(shader);
        }

        /// <summary>Sets the main texture on URP (_BaseMap) and built-in (_MainTex) shaders.</summary>
        public static void SetTexture(Material mat, Texture tex, Vector2 tiling)
        {
            mat.mainTexture = tex;
            mat.mainTextureScale = tiling;
            if (mat.HasProperty("_BaseMap"))
            {
                mat.SetTexture("_BaseMap", tex);
                mat.SetTextureScale("_BaseMap", tiling);
            }
        }

        public static void SetColor(Material mat, Color c)
        {
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", c);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", c);
        }
    }
}
