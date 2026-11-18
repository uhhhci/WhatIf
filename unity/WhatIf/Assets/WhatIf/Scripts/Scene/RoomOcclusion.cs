using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.ARFoundation;

namespace WhatIf.Scene
{
    /// <summary>
    /// Real walls hide virtual content behind them (crucial for depth perception) - with clean,
    /// straight, floor-to-ceiling planes instead of the wobbly room mesh:
    ///  1. where the room mesh has vertical surfaces that run from near the floor to above
    ///     <see cref="wallMinTopM"/> (walls, not desks, chairs or door lintels), and along detected
    ///     wall planes, a floor grid marks "wall" cells;
    ///  2. straight lines are fitted through those cells (RANSAC, then least squares);
    ///  3. each line becomes wall segments, split where the wall has a gap - gaps of a door's width
    ///     are kept as <see cref="Doorways"/> (placement keeps them free);
    ///  4. a segment is only a wall if it is at the room's edge: where the room mesh shows floor behind
    ///     it (seen from inside the room), it is furniture or a cupboard row standing in the room;
    ///  5. every segment gets an invisible depth-only quad (WhatIf/Occlusion) with sharp edges.
    /// Of the detected planes only those the headset classifies as walls (and that are big enough) count:
    /// cupboard fronts, whiteboards and screens are vertical planes too.
    /// Furniture, clutter and people don't occlude: virtual objects pass through them, which looks
    /// better than ragged holes. <see cref="occludeFurniture"/> brings the whole mesh back.
    /// Only on the headset (in the Editor there is no real room).
    /// </summary>
    public class RoomOcclusion : MonoBehaviour
    {
        public ARMeshManager meshManager;
        public ARPlaneManager planeManager;
        public SceneScanner scanner;
        [Tooltip("Hide virtual content behind the estimated walls. Off: walls are still estimated (for placing " +
                 "things on them and keeping doorways free), but everything is drawn regardless of walls.")]
        public bool occlude = true;
        [Tooltip("Also let furniture and other real things hide virtual objects (the coarse room mesh).")]
        public bool occludeFurniture = false;
        [Tooltip("A wall's vertical surface reaches at least this high above the floor ...")]
        public float wallMinTopM = 2.0f;
        [Tooltip("Height of the occluding planes above the floor (no ceiling known: this).")]
        public float wallHeightM = 3.2f;

        /// <summary>Doorways found in the walls (centre on the floor): placement keeps them free.</summary>
        public static readonly List<Vector3> Doorways = new List<Vector3>();
        /// <summary>The estimated wall segments (floor plan, x/z): things can hang on them too.</summary>
        public static readonly List<(Vector2 a, Vector2 b)> Walls = new List<(Vector2, Vector2)>();

        const float CellM = 0.25f;
        const float InlierM = 0.16f;
        const float GapM = 0.6f;           // a wall gap at least this wide may split it ...
        const float BridgeM = 1.2f;        // ... but gaps up to this wide without a lintel are unscanned wall
        const float DoorMinM = 0.6f, DoorMaxM = 1.6f; // a gap with a lintel above it is a doorway
        const float SnapDeg = 10f;         // walls this close to the room's main direction (or 90 deg) snap to it
        const int MinCells = 5;            // ~1.25 m of wall

        // Per floor cell: at which heights vertical surfaces were seen (bit i = BinM-high band i above
        // _baseY). A wall is vertical all the way up; a chair below a hanging lamp has a gap between
        // (seen: walls estimated in the middle of a room, under the dining lamp).
        readonly Dictionary<Vector2Int, ulong> _bins = new Dictionary<Vector2Int, ulong>();
        const float BinM = 0.25f;
        float? _baseY;
        readonly HashSet<MeshFilter> _dirty = new HashSet<MeshFilter>();
        Material _material;
        GameObject _wallsGo;
        Mesh _wallsMesh;
        int _lastWallCells = -1;
        float _nextUpdate;

        void Start()
        {
            var shader = Shader.Find("WhatIf/Occlusion");
            if (shader == null) { Debug.LogWarning("[WhatIf] occlusion: shader missing"); enabled = false; return; }
            _material = new Material(shader) { name = "WhatIf Occlusion" };
            if (meshManager == null) meshManager = FindObjectOfType<ARMeshManager>();
            if (meshManager != null)
            {
                meshManager.meshesChanged += OnMeshesChanged;
                if (meshManager.meshes != null) foreach (var m in meshManager.meshes) Added(m);
            }
            _wallsGo = new GameObject("WhatIf wall occluders");
            _wallsMesh = new Mesh { name = "wall occluders" };
            _wallsGo.AddComponent<MeshFilter>().sharedMesh = _wallsMesh;
            var r = _wallsGo.AddComponent<MeshRenderer>();
            r.sharedMaterial = _material;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.enabled = occlude;
            Debug.Log("[WhatIf] occlusion: " + (!occlude ? "off (walls only estimated for placement)"
                                                : occludeFurniture ? "whole room mesh" : "straight walls"));
        }

        void OnDestroy()
        {
            if (meshManager != null) meshManager.meshesChanged -= OnMeshesChanged;
            Doorways.Clear();
            Walls.Clear();
        }

        void OnMeshesChanged(ARMeshesChangedEventArgs args)
        {
            foreach (var m in args.added) Added(m);
            foreach (var m in args.updated) Added(m);
        }

        void Added(MeshFilter chunk)
        {
            if (chunk == null) return;
            if (occludeFurniture)
            {
                var r = chunk.GetComponent<MeshRenderer>();
                if (r == null) r = chunk.gameObject.AddComponent<MeshRenderer>();
                r.sharedMaterial = _material;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
                return;
            }
            _dirty.Add(chunk);
        }

        float FloorY => scanner != null ? scanner.FloorY : (Camera.main != null ? Camera.main.transform.position.y - 1.6f : 0f);

        static Vector2Int Cell(float x, float z) => new Vector2Int(Mathf.FloorToInt(x / CellM), Mathf.FloorToInt(z / CellM));
        static Vector2 Centre(Vector2Int c) => new Vector2((c.x + 0.5f) * CellM, (c.y + 0.5f) * CellM);

        void Note(float x, float z, float bottom, float top)
        {
            if (_baseY == null) _baseY = FloorY - 0.5f; // fixed, so the bands don't move with the floor estimate
            int lo = Mathf.Clamp(Mathf.FloorToInt((bottom - _baseY.Value) / BinM), 0, 63);
            int hi = Mathf.Clamp(Mathf.FloorToInt((top - _baseY.Value) / BinM), 0, 63);
            ulong bits = 0;
            for (int i = lo; i <= hi; i++) bits |= 1UL << i;
            var c = Cell(x, z);
            _bins[c] = _bins.TryGetValue(c, out var old) ? old | bits : bits;
        }

        /// <summary>How many of the bands between two heights above the floor have vertical surfaces.</summary>
        int Filled(ulong bits, float floor, float from, float to, out int total)
        {
            int lo = Mathf.Clamp(Mathf.FloorToInt((floor + from - _baseY.Value) / BinM), 0, 63);
            int hi = Mathf.Clamp(Mathf.FloorToInt((floor + to - _baseY.Value) / BinM) - 1, 0, 63);
            int n = 0;
            for (int i = lo; i <= hi; i++) if ((bits & (1UL << i)) != 0) n++;
            total = hi - lo + 1;
            return n;
        }

        /// <summary>Vertical surfaces without gaps from 0.9 m to above head height: a lamp over chairs
        /// has a gap there, while a wall behind a sofa or cupboard (hidden low down) still counts.</summary>
        bool IsWall(ulong bits, float floor) =>
            Filled(bits, floor, 0.9f, wallMinTopM + 0.2f, out int total) >= total - 1 && total > 1;

        void Update()
        {
            if (occludeFurniture || _material == null || Time.unscaledTime < _nextUpdate) return;
            _nextUpdate = Time.unscaledTime + 2f;
            foreach (var c in _dirty) if (c != null) Measure(c);
            _dirty.Clear();
            MeasurePlanes();
            Rebuild();
        }

        /// <summary>Vertical triangles of a room mesh chunk: how low and how high they reach per cell.</summary>
        void Measure(MeshFilter chunk)
        {
            var mesh = chunk.sharedMesh;
            if (mesh == null) return;
            var v = mesh.vertices; var t = mesh.triangles; var m = chunk.transform.localToWorldMatrix;
            for (int i = 0; i + 2 < t.Length; i += 3)
            {
                Vector3 a = m.MultiplyPoint3x4(v[t[i]]), b = m.MultiplyPoint3x4(v[t[i + 1]]), c = m.MultiplyPoint3x4(v[t[i + 2]]);
                var n = Vector3.Cross(b - a, c - a);
                if (n.sqrMagnitude < 1e-10f || Mathf.Abs(n.normalized.y) > 0.3f) continue;
                var centre = (a + b + c) / 3f;
                Note(centre.x, centre.z, Mathf.Min(a.y, Mathf.Min(b.y, c.y)), Mathf.Max(a.y, Mathf.Max(b.y, c.y)));
            }
        }

        /// <summary>Planes the headset classifies as walls (at least 1 m high, 0.6 m wide) are walls for
        /// sure: their whole width counts, floor to top. Other vertical planes (cupboard fronts, screens,
        /// whiteboards) don't.</summary>
        void MeasurePlanes()
        {
            if (planeManager == null) return;
            float floor = FloorY;
            foreach (var plane in planeManager.trackables)
            {
                if (Mathf.Abs(Vector3.Dot(plane.normal, Vector3.up)) > 0.3f || plane.boundary.Length < 3) continue;
                if (plane.classification != UnityEngine.XR.ARSubsystems.PlaneClassification.Wall) continue;
                if (plane.size.y < 1f || plane.size.x < 0.6f) continue;
                var b = plane.boundary;
                for (int i = 0; i < b.Length; i++)
                {
                    // along the outline (plane space x/z): its footprint on the floor is the wall's line
                    Vector3 p = plane.transform.TransformPoint(new Vector3(b[i].x, 0f, b[i].y));
                    Vector3 q = plane.transform.TransformPoint(new Vector3(b[(i + 1) % b.Length].x, 0f, b[(i + 1) % b.Length].y));
                    int steps = Mathf.Max(1, Mathf.CeilToInt(new Vector2(q.x - p.x, q.z - p.z).magnitude / (CellM * 0.5f)));
                    for (int s = 0; s <= steps; s++)
                    {
                        var w = Vector3.Lerp(p, q, s / (float)steps);
                        Note(w.x, w.z, floor, floor + wallHeightM);
                    }
                }
            }
        }

        void Rebuild()
        {
            float floor = FloorY;
            var cells = new List<Vector2>();
            if (_baseY == null) return;
            foreach (var kv in _bins)
                if (IsWall(kv.Value, floor)) cells.Add(Centre(kv.Key));
            // only when clearly more wall was seen: walls stay steady instead of jumping every update
            if (_lastWallCells >= 0 && cells.Count < _lastWallCells * 1.05f + 2) return;
            _lastWallCells = cells.Count;
            float? mainAngle = null; // the room's main wall direction (walls are mostly at right angles)

            var segments = new List<(Vector2 a, Vector2 b)>();
            var doors = new List<Vector3>();
            var rng = new System.Random(1); // the same walls for the same cells: no flicker between updates
            var left = new List<Vector2>(cells);
            for (int wall = 0; wall < 12 && left.Count >= MinCells; wall++)
            {
                // RANSAC: the line through two cells with the most cells near it
                int bestCount = 0; Vector2 bestP = default, bestD = default;
                for (int it = 0; it < 250; it++)
                {
                    var p = left[rng.Next(left.Count)]; var q = left[rng.Next(left.Count)];
                    var d = q - p;
                    if (d.sqrMagnitude < 0.25f) continue; // at least 0.5 m apart: a steady direction
                    d.Normalize();
                    int count = 0;
                    foreach (var c in left) if (Mathf.Abs(Cross(d, c - p)) < InlierM) count++;
                    if (count > bestCount) { bestCount = count; bestP = p; bestD = d; }
                }
                if (bestCount < MinCells) break;
                var inliers = left.FindAll(c => Mathf.Abs(Cross(bestD, c - bestP)) < InlierM);
                left.RemoveAll(c => Mathf.Abs(Cross(bestD, c - bestP)) < InlierM);
                // least squares through the inliers: the wall's real line
                var mean = Vector2.zero; foreach (var c in inliers) mean += c; mean /= inliers.Count;
                float sxx = 0, sxy = 0, syy = 0;
                foreach (var c in inliers) { var e = c - mean; sxx += e.x * e.x; sxy += e.x * e.y; syy += e.y * e.y; }
                float angle = 0.5f * Mathf.Atan2(2f * sxy, sxx - syy);
                if (mainAngle == null) mainAngle = angle; // the first line has the most wall
                else
                {
                    // straight, square rooms: snap to the main direction or its right angle
                    float rel = Mathf.Repeat(Mathf.DeltaAngle(mainAngle.Value * Mathf.Rad2Deg, angle * Mathf.Rad2Deg), 90f);
                    if (rel < SnapDeg) angle -= rel * Mathf.Deg2Rad;
                    else if (rel > 90f - SnapDeg) angle += (90f - rel) * Mathf.Deg2Rad;
                }
                var dir = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
                // along the line: split where the wall has a gap (doorways, openings)
                var ts = new List<float>();
                foreach (var c in inliers) ts.Add(Vector2.Dot(c - mean, dir));
                ts.Sort();
                float start = ts[0];
                for (int i = 1; i <= ts.Count; i++)
                {
                    bool door = false;
                    if (i < ts.Count)
                    {
                        float gap = ts[i] - ts[i - 1];
                        if (gap <= GapM) continue;
                        door = gap >= DoorMinM && gap <= DoorMaxM && Lintel(mean, dir, ts[i - 1], ts[i], floor);
                        if (!door && gap <= BridgeM) continue; // an unscanned bit of wall: keep it whole
                    }
                    float stop = ts[i - 1];
                    if (stop - start >= 0.5f)
                        segments.Add((mean + dir * (start - CellM * 0.5f), mean + dir * (stop + CellM * 0.5f)));
                    if (door)
                    {
                        var mid = mean + dir * ((ts[i] + ts[i - 1]) * 0.5f);
                        doors.Add(new Vector3(mid.x, floor, mid.y));
                    }
                    if (i < ts.Count) start = ts[i];
                }
            }

            // only walls at the room's edge: floor behind a "wall" means furniture standing in the room
            int before = segments.Count;
            segments.RemoveAll(sg => !AtRoomEdge(sg.a, sg.b, floor));
            if (segments.Count < before)
                Debug.Log($"[WhatIf] occlusion: {before - segments.Count} wall candidates have floor behind them - furniture, not walls");

            Doorways.Clear();
            Doorways.AddRange(doors);
            Walls.Clear();
            Walls.AddRange(segments);
            var verts = new List<Vector3>();
            var tris = new List<int>();
            float y0 = floor - 0.1f, y1 = floor + wallHeightM + 0.3f;
            foreach (var (sa, sb) in segments)
            {
                // 15 cm longer at both ends: where two walls meet, the room showed through the corner
                var along = (sb - sa).normalized * 0.15f;
                var a = sa - along; var b = sb + along;
                int k = verts.Count;
                verts.Add(new Vector3(a.x, y0, a.y)); verts.Add(new Vector3(b.x, y0, b.y));
                verts.Add(new Vector3(b.x, y1, b.y)); verts.Add(new Vector3(a.x, y1, a.y));
                tris.AddRange(new[] { k, k + 1, k + 2, k, k + 2, k + 3 });
            }
            _wallsMesh.Clear();
            _wallsMesh.SetVertices(verts);
            _wallsMesh.SetTriangles(tris, 0);
            _wallsMesh.RecalculateBounds();
            Debug.Log($"[WhatIf] occlusion: {segments.Count} wall planes, {doors.Count} doorways from {cells.Count} wall cells");
        }

        /// <summary>
        /// A wall is the room's edge: behind it (seen from the user, who is inside the room) the room mesh
        /// shows no floor. Behind a desk, a cupboard row or a partition standing in the room it does.
        /// Points 0.8 and 1.4 m behind the segment are tested from above against the room mesh only
        /// (detected planes can reach a little past walls).
        /// </summary>
        bool AtRoomEdge(Vector2 a, Vector2 b, float floor)
        {
            var cam = Camera.main;
            if (cam == null) return true;
            var dir = (b - a).normalized;
            var normal = new Vector2(-dir.y, dir.x);
            var head = new Vector2(cam.transform.position.x, cam.transform.position.z);
            var mid = (a + b) * 0.5f;
            if (Vector2.Dot(head - mid, normal) > 0f) normal = -normal; // pointing away from the user
            var layers = scanner != null ? scanner.meshLayers : (LayerMask)~0;
            int tested = 0, floorBehind = 0;
            for (int i = 0; i < 5; i++)
            {
                var along = Vector2.Lerp(a, b, (i + 0.5f) / 5f);
                foreach (float off in new[] { 0.8f, 1.4f })
                {
                    var w = along + normal * off;
                    tested++;
                    foreach (var hit in Physics.RaycastAll(new Vector3(w.x, floor + 2.2f, w.y), Vector3.down, 2.6f, layers))
                    {
                        if (hit.collider.GetComponentInParent<ARPlane>() != null) continue;
                        if (hit.collider.GetComponentInParent<Content.GeneratedObject>() != null) continue;
                        if (Mathf.Abs(hit.point.y - floor) < 0.12f && hit.normal.y > 0.8f) { floorBehind++; break; }
                    }
                }
            }
            return floorBehind * 10 < tested * 3; // floor behind at fewer than 30 % of the points
        }

        /// <summary>A door has a lintel: above the gap, a vertical surface that starts high up.</summary>
        bool Lintel(Vector2 origin, Vector2 dir, float from, float to, float floor)
        {
            var normal = new Vector2(-dir.y, dir.x);
            for (float t = from + CellM; t < to; t += CellM * 0.5f)
                for (float off = -InlierM; off <= InlierM; off += CellM * 0.5f)
                {
                    var w = origin + dir * t + normal * off;
                    // above the gap: surfaces around door-top height, but (almost) nothing low down
                    if (_bins.TryGetValue(Cell(w.x, w.y), out var b) &&
                        Filled(b, floor, 1.9f, 2.4f, out _) > 0 && Filled(b, floor, 0.25f, 1.5f, out _) <= 1)
                        return true;
                }
            return false;
        }

        static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;
    }
}
