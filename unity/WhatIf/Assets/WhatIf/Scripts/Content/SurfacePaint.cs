using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using WhatIf.Api;
using WhatIf.Scene;

namespace WhatIf.Content
{
    /// <summary>
    /// Colours real surfaces the headset detected: "blue painted walls", "a red floor".
    /// Paint is kept per key - a surface id (one particular wall) or a kind ("wall" = every wall,
    /// also ones detected later) - and drawn as a coloured polygon just in front of each matching
    /// plane, parented to the plane so it follows its tracking. The polygons are rebuilt when a plane's
    /// outline changes (planes grow while the user looks around).
    ///
    /// On Magic Leap 2 with segmented dimming the paint's alpha dims the real wall behind it, so
    /// strength 0.8 means 80 % paint colour, 20 % of the real wall's light (its shading and texture)
    /// still showing through. Without dimming the colour is only added to the real light.
    /// </summary>
    public class SurfacePaint : MonoBehaviour
    {
        public SceneScanner scanner;
        public WhatIfSettings settings;
        public float offsetM = 0.006f; // in front of the plane, against z-fighting with plane visualisers

        /// <summary>The look on a surface: an effect's building blocks (plain paint is just a colour).</summary>
        public struct Coat { public Effect Look; }

        readonly Dictionary<string, Coat> _coats = new Dictionary<string, Coat>();
        readonly Dictionary<ARPlane, Painted> _shown = new Dictionary<ARPlane, Painted>();
        readonly Dictionary<string, string> _labelOf = new Dictionary<string, string>(); // surface id -> kind
        float _nextRefresh;

        class Painted
        {
            public GameObject Go;
            public Mesh Mesh;
            public Material Material;
            public int BoundaryHash;
            public Effect Look;
        }

        public bool HasPaint => _coats.Count > 0;

        // All floor-level coats as ONE mesh in world space, flat at the floor height: per-plane pieces
        // (attached to each plane, some of which the headset reports upside down) didn't line up, and
        // the pattern broke at every seam.
        GameObject _floorGo;
        Mesh _floorMesh;
        Material _floorMat;
        Effect _floorLook;
        int _floorHash = int.MinValue;
        readonly List<List<Vector2>> _floorWorld = new List<List<Vector2>>(); // outlines (x, z) for Hits

        /// <summary>Does the ray hit a painted surface (within 10 m)?</summary>
        public bool Hits(Ray ray)
        {
            if (_floorGo != null && scanner != null)
            {
                var floor = new Plane(Vector3.up, new Vector3(0f, scanner.FloorY + offsetM, 0f));
                if (floor.Raycast(ray, out float fd) && fd <= 10f)
                {
                    var q = ray.GetPoint(fd);
                    foreach (var poly in _floorWorld) if (InPolygon(new Vector2(q.x, q.z), poly)) return true;
                }
            }
            foreach (var plane in _shown.Keys)
            {
                if (plane == null) continue;
                var t = plane.transform;
                var p = new Plane(t.up, t.position);
                if (!p.Raycast(ray, out float d) || d > 10f) continue;
                var local = t.InverseTransformPoint(ray.GetPoint(d));
                if (InPolygon(new Vector2(local.x, local.z), plane.boundary)) return true;
            }
            return false;
        }

        static bool InPolygon(Vector2 q, Unity.Collections.NativeArray<Vector2> poly)
        {
            if (!poly.IsCreated || poly.Length < 3) return false;
            bool inside = false;
            for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
            {
                var a = poly[i]; var b = poly[j];
                if ((a.y > q.y) != (b.y > q.y) && q.x < (b.x - a.x) * (q.y - a.y) / (b.y - a.y) + a.x) inside = !inside;
            }
            return inside;
        }

        /// <summary>Paint one surface (its id) or a kind of surfaces; hex "none" removes the paint.
        /// Painting a kind replaces the paint of single surfaces of that kind ("now all walls green").</summary>
        public void Set(string key, string hex, float? strength = null)
        {
            if (string.IsNullOrEmpty(key)) return;
            UpdateLabels();
            bool isKind = !_labelOf.ContainsKey(key) && key != "virtual";
            if (isKind)
            {
                var single = new List<string>();
                foreach (var k in _coats.Keys)
                    if (_labelOf.TryGetValue(k, out var kind) && kind == key) single.Add(k);
                foreach (var k in single) _coats.Remove(k);
            }
            if (hex == "none" || !ColorUtility.TryParseHtmlString(hex ?? "", out _))
                _coats.Remove(key);
            else
            {
                // a new colour keeps the rest of the look (a lava floor stays lava in another colour)
                var look = _coats.TryGetValue(key, out var old) && old.Look != null
                    ? old.Look.CopyLook("surface") : new Effect { Type = "surface", Strength = 0.8f };
                look.TintHex = hex;
                if (strength.HasValue) look.Strength = Mathf.Clamp01(strength.Value);
                _coats[key] = new Coat { Look = look };
            }
            _nextRefresh = 0f; // show it now
        }

        /// <summary>A full look (pattern, motion, glow ...) on one surface or a kind of surfaces.</summary>
        public void Set(string key, Effect look)
        {
            if (string.IsNullOrEmpty(key) || look == null) return;
            Set(key, look.TintHex ?? "#808080", look.Strength);
            if (_coats.ContainsKey(key)) _coats[key] = new Coat { Look = look.CopyLook("surface") };
            _nextRefresh = 0f;
        }

        public void Clear()
        {
            RemoveFloor();
            _coats.Clear();
            foreach (var p in _shown.Values) Remove(p);
            _shown.Clear();
        }

        public Dictionary<string, Coat> Save() => new Dictionary<string, Coat>(_coats);

        public void Restore(Dictionary<string, Coat> coats)
        {
            _coats.Clear();
            foreach (var kv in coats) _coats[kv.Key] = kv.Value;
            _nextRefresh = 0f;
        }

        /// <summary>The paint as effects, for the editor ("make the walls darker" needs the current colour).</summary>
        public List<Effect> Describe()
        {
            // surfaces with the same look become one effect
            var byLook = new Dictionary<Effect, Effect>();
            var result = new List<Effect>();
            foreach (var kv in _coats)
            {
                var look = kv.Value.Look;
                if (look == null) continue;
                if (!byLook.TryGetValue(look, out var e))
                {
                    e = look.CopyLook("surface");
                    byLook[look] = e;
                    result.Add(e);
                }
                e.Surfaces.Add(kv.Key);
            }
            return result;
        }

        void Update()
        {
            if (Time.unscaledTime < _nextRefresh) return;
            _nextRefresh = Time.unscaledTime + 0.5f;
            Refresh();
        }

        void UpdateLabels()
        {
            if (scanner == null || settings == null) return;
            foreach (var s in scanner.Collect(settings))
                _labelOf[SceneScanner.SurfaceId(s)] = s.Label;
        }

        void Refresh()
        {
            if (_coats.Count == 0)
            {
                if (_shown.Count > 0) { foreach (var p in _shown.Values) Remove(p); _shown.Clear(); }
                RemoveFloor();
                return;
            }
            if (scanner == null || settings == null) return;

            var seen = new HashSet<ARPlane>();
            var floors = new List<(Surface s, Coat coat)>();
            foreach (var s in scanner.Collect(settings))
            {
                if (s.Plane == null) continue; // the virtual ground isn't a real surface
                var id = SceneScanner.SurfaceId(s);
                _labelOf[id] = s.Label;
                if (!_coats.TryGetValue(id, out var coat) && !_coats.TryGetValue(s.Label, out coat)) continue;
                if (s.IsHorizontalUp && Mathf.Abs(s.Pose.position.y - scanner.FloorY) < 0.15f)
                {
                    floors.Add((s, coat)); // drawn together as one floor below
                    continue;
                }
                seen.Add(s.Plane);
                if (!_shown.TryGetValue(s.Plane, out var painted)) _shown[s.Plane] = painted = Create(s.Plane);
                int hash = BoundaryHash(s.Boundary);
                if (hash != painted.BoundaryHash)
                {
                    Build(painted.Mesh, s.Boundary);
                    painted.BoundaryHash = hash;
                }
                if (painted.Look != coat.Look)
                {
                    SurfaceLook.Apply(painted.Material, coat.Look);
                    painted.Look = coat.Look;
                }
            }
            var gone = new List<ARPlane>();
            foreach (var kv in _shown)
                if (kv.Key == null || !seen.Contains(kv.Key)) gone.Add(kv.Key);
            foreach (var p in gone) { Remove(_shown[p]); _shown.Remove(p); }
            BuildFloor(floors);
        }

        void BuildFloor(List<(Surface s, Coat coat)> floors)
        {
            if (floors.Count == 0) { RemoveFloor(); return; }
            float y = scanner.FloorY + offsetM;
            int hash = Mathf.RoundToInt(y * 100f);
            foreach (var (s, _) in floors) hash = hash * 31 + BoundaryHash(s.Boundary) + Mathf.RoundToInt(s.Pose.position.x * 100f);
            if (_floorGo == null)
            {
                _floorGo = new GameObject("WhatIf Floor Paint");
                _floorMesh = new Mesh { name = "WhatIf Floor Paint", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
                _floorGo.AddComponent<MeshFilter>().sharedMesh = _floorMesh;
                _floorMat = SurfaceLook.CreateMaterial(new Effect { TintHex = "#808080", Strength = 0.8f });
                if (_floorMat.HasProperty("_StencilComp"))
                    _floorMat.SetFloat("_StencilComp", (float)UnityEngine.Rendering.CompareFunction.NotEqual);
                _floorMat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent - 10;
                _floorGo.AddComponent<MeshRenderer>().sharedMaterial = _floorMat;
                _floorLook = null;
            }
            var look = floors[0].coat.Look;
            if (_floorLook != look) { SurfaceLook.Apply(_floorMat, look); _floorLook = look; }
            if (hash == _floorHash) return;
            _floorHash = hash;
            var verts = new List<Vector3>();
            var tris = new List<int>();
            _floorWorld.Clear();
            foreach (var (s, _) in floors)
            {
                // the surface's own pose and outline (already turned over for planes reported upside
                // down), flattened onto the floor height in world space
                var outline = new List<Vector2>(s.Boundary.Count);
                foreach (var b in s.Boundary)
                {
                    var w = s.Pose.position + s.Pose.rotation * new Vector3(b.x, 0f, b.y);
                    outline.Add(new Vector2(w.x, w.z));
                }
                int start = verts.Count;
                foreach (var o in outline) verts.Add(new Vector3(o.x, y, o.y));
                foreach (var t in Triangulate(outline)) tris.Add(start + t);
                _floorWorld.Add(outline);
            }
            _floorMesh.Clear();
            _floorMesh.SetVertices(verts);
            _floorMesh.SetTriangles(tris, 0);
            var normals = new Vector3[verts.Count];
            for (int i = 0; i < normals.Length; i++) normals[i] = Vector3.up;
            _floorMesh.normals = normals;
            _floorMesh.RecalculateBounds();
        }

        void RemoveFloor()
        {
            if (_floorGo != null) Destroy(_floorGo);
            if (_floorMesh != null) Destroy(_floorMesh);
            if (_floorMat != null) Destroy(_floorMat);
            _floorGo = null; _floorMesh = null; _floorMat = null; _floorLook = null;
            _floorHash = int.MinValue;
            _floorWorld.Clear();
        }

        static bool InPolygon(Vector2 q, List<Vector2> poly)
        {
            bool inside = false;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
                if ((poly[i].y > q.y) != (poly[j].y > q.y) &&
                    q.x < (poly[j].x - poly[i].x) * (q.y - poly[i].y) / (poly[j].y - poly[i].y) + poly[i].x)
                    inside = !inside;
            return inside;
        }

        Painted Create(ARPlane plane)
        {
            var go = new GameObject("WhatIf Paint");
            go.transform.SetParent(plane.transform, false); // plane space: boundary x/y on its x/z axes
            go.transform.localPosition = Vector3.up * offsetM;
            var mesh = new Mesh { name = "WhatIf Paint" };
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mat = SurfaceLook.CreateMaterial(new Effect { TintHex = "#808080", Strength = 0.8f });
            if (mat.HasProperty("_Cull")) mat.SetFloat("_Cull", 0); // visible from both sides
            // overlapping coats draw each pixel once (one continuous surface, no darker seams)
            if (mat.HasProperty("_StencilComp")) mat.SetFloat("_StencilComp", (float)UnityEngine.Rendering.CompareFunction.NotEqual);
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent - 10; // under shadows, water
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
            return new Painted { Go = go, Mesh = mesh, Material = mat, BoundaryHash = int.MinValue };
        }

        static void Remove(Painted p)
        {
            if (p == null) return;
            if (p.Go != null) Destroy(p.Go);
            if (p.Mesh != null) Destroy(p.Mesh);
            if (p.Material != null) Destroy(p.Material);
        }

        static int BoundaryHash(List<Vector2> b)
        {
            unchecked
            {
                int h = b.Count;
                foreach (var v in b) h = h * 31 + Mathf.RoundToInt(v.x * 100f) * 7 + Mathf.RoundToInt(v.y * 100f);
                return h;
            }
        }

        /// <summary>Triangulates the plane outline (ear clipping: ML2 outlines can be concave).</summary>
        static void Build(Mesh mesh, List<Vector2> boundary)
        {
            mesh.Clear();
            if (boundary == null || boundary.Count < 3) return;
            var verts = new List<Vector3>(boundary.Count);
            var uvs = new List<Vector2>(boundary.Count);
            foreach (var p in boundary) { verts.Add(new Vector3(p.x, 0f, p.y)); uvs.Add(p); }
            mesh.SetVertices(verts);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(Triangulate(boundary), 0);
            var normals = new Vector3[verts.Count];
            for (int i = 0; i < normals.Length; i++) normals[i] = Vector3.up;
            mesh.normals = normals;
            mesh.RecalculateBounds();
        }

        public static List<int> Triangulate(List<Vector2> poly)
        {
            var tris = new List<int>();
            int n = poly.Count;
            var idx = new List<int>(n);
            for (int i = 0; i < n; i++) idx.Add(i);
            float area = 0f;
            for (int i = 0; i < n; i++) { var a = poly[i]; var b = poly[(i + 1) % n]; area += a.x * b.y - b.x * a.y; }
            if (area < 0f) idx.Reverse(); // counter-clockwise from here on

            int guard = 0;
            while (idx.Count > 3 && guard++ < 10000)
            {
                bool clipped = false;
                for (int i = 0; i < idx.Count; i++)
                {
                    int i0 = idx[(i + idx.Count - 1) % idx.Count], i1 = idx[i], i2 = idx[(i + 1) % idx.Count];
                    Vector2 a = poly[i0], b = poly[i1], c = poly[i2];
                    if (Cross(b - a, c - b) <= 1e-7f) continue; // reflex or flat corner
                    bool inside = false;
                    foreach (var j in idx)
                        if (j != i0 && j != i1 && j != i2 && InTriangle(poly[j], a, b, c)) { inside = true; break; }
                    if (inside) continue;
                    AddTriangle(tris, i0, i1, i2);
                    idx.RemoveAt(i);
                    clipped = true;
                    break;
                }
                if (!clipped) break; // degenerate outline: fan the rest
            }
            for (int i = 1; i + 1 < idx.Count; i++) AddTriangle(tris, idx[0], idx[i], idx[i + 1]);
            return tris;
        }

        // Plane space is x/z with y up; counter-clockwise in (x, z) seen from +y needs this winding
        // for Unity's clockwise front faces. The material is double-sided anyway.
        static void AddTriangle(List<int> tris, int a, int b, int c) { tris.Add(a); tris.Add(c); tris.Add(b); }

        static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;

        static bool InTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c) =>
            Cross(b - a, p - a) >= 0f && Cross(c - b, p - b) >= 0f && Cross(a - c, p - c) >= 0f;
    }
}
