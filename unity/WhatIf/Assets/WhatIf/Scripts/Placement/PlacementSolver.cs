using System;
using System.Collections.Generic;
using UnityEngine;
using WhatIf.Api;
using WhatIf.Scene;

namespace WhatIf.Placement
{
    public struct PlacedInstance
    {
        public Vector3 Position;
        public Quaternion Rotation;   // up = object's up; for wall items forward = away from wall
        public float Height;
        public bool OnWall;
        public bool Hanging;          // from the ceiling
    }

    /// <summary>
    /// Turns the planner's abstract placement rules ("4 trees along the edge of the floor,
    /// 3 m apart") into concrete poses on the detected surfaces.
    /// </summary>
    public class PlacementSolver
    {
        readonly List<Surface> _surfaces;
        readonly SceneScanner _scanner;
        readonly Transform _head;
        readonly List<(Vector3 pos, float radius)> _occupied = new List<(Vector3, float)>();
        // Where earlier items of this plan went, so later items can be placed around / beside them.
        readonly Dictionary<string, List<(Vector3 pos, float radius)>> _byItem = new Dictionary<string, List<(Vector3, float)>>();
        readonly Dictionary<string, List<float>> _heightsByItem = new Dictionary<string, List<float>>();
        readonly System.Random _rng;

        public PlacementSolver(List<Surface> surfaces, SceneScanner scanner, Transform head, int seed = 0)
        {
            _surfaces = surfaces;
            _scanner = scanner;
            _head = head;
            _rng = seed == 0 ? new System.Random() : new System.Random(seed);
        }

        /// <summary>
        /// Finds a real thing the planner named in the camera image ("img:x,y") in the room: returns
        /// the top of it, or null. Set by the controller for the frame the request was made with.
        /// </summary>
        public System.Func<string, Vector3?> ImageTopOf;

        /// <summary>Registers an image target as a thing standing on the floor up to its top, so
        /// "on" (on top of it), "beside" and "around" work like for any other object.</summary>
        void EnsureImageTarget(string id)
        {
            if (!(Scene.ImageTargets.IsImageTarget(id) || id == "gaze") || _byItem.ContainsKey(id) || ImageTopOf == null) return;
            var top = ImageTopOf(id);
            if (!top.HasValue) return;
            float floor = Mathf.Min(_scanner.FloorY, top.Value.y);
            RegisterExisting(id, null, new Vector3(top.Value.x, floor, top.Value.z), 0.25f,
                             Mathf.Max(0.02f, top.Value.y - floor), obstacle: true);
        }

        public List<PlacedInstance> Place(Api.Placement p, string itemId = null)
        {
            EnsureImageTarget(p.TargetId);
            var result = new List<PlacedInstance>();
            // Each instance claims a circle of its footprint: objects can't overlap even when
            // the planner's spacing is small (a sofa is much wider than it is tall).
            float radius = Mathf.Max(0.1f, p.MinSpacingM / 2f, Footprint(p) * 0.55f);

            if (p.Layout == "under")
                PlaceUnder(p, result, radius);
            else if (p.Layout == "around" || p.Layout == "beside")
                PlaceAround(p, result, radius, beside: p.Layout == "beside");
            else if (p.TargetId != null && FindSurface(p.TargetId) == null && _byItem.ContainsKey(p.TargetId))
                PlaceOnItem(p, result, radius); // plates on the generated dining table
            if (result.Count == 0)
            {
                var candidates = Matching(p);
                bool coat = p.Layout == "coat" || p.Layout == "drape"; // they work out their own number
                if (candidates.Count > 0 && !string.IsNullOrEmpty(p.Density) && !coat)
                    p.Count = DensityCount(p, candidates);
                if (candidates.Count > 0)
                {
                    switch (p.Layout)
                    {
                        case "row": PlaceRow(p, candidates, result, radius); break;
                        case "edge": PlaceEdge(p, candidates, result, radius); break;
                        case "single": PlaceSingle(p, candidates, result, radius); break;
                        case "coat": PlaceCoat(p, candidates, result); break;
                        case "drape": PlaceDrape(p, candidates, result); break;
                        default: PlaceScatter(p, candidates, result, radius); break;
                    }
                }
            }
            if (!string.IsNullOrEmpty(itemId))
            {
                var mine = new List<(Vector3, float)>();
                var heights = new List<float>();
                foreach (var r in result) { mine.Add((r.Position, radius)); heights.Add(r.Height); }
                _byItem[itemId] = mine;
                _heightsByItem[itemId] = heights;
            }
            return result;
        }

        /// <summary>
        /// An object already in the scene (when editing): it can be a target by instance id or group
        /// ("around the table", "next to chair#2") and, unless it is being moved, an obstacle.
        /// </summary>
        public void RegisterExisting(string instanceId, string group, Vector3 basePos, float radius, float height, bool obstacle)
        {
            foreach (var key in new[] { instanceId, group })
            {
                if (string.IsNullOrEmpty(key)) continue;
                if (!_byItem.TryGetValue(key, out var list)) _byItem[key] = list = new List<(Vector3, float)>();
                if (!_heightsByItem.TryGetValue(key, out var hs)) _heightsByItem[key] = hs = new List<float>();
                list.Add((basePos, radius));
                hs.Add(height);
            }
            if (obstacle) _occupied.Add((basePos, radius));
        }

        // ------------------------------------------------------------------ targets

        Surface FindSurface(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            foreach (var s in _surfaces) if (s.Plane != null && SceneScanner.SurfaceId(s) == id) return s;
            return null;
        }

        /// <summary>Centre of the target nearest to `from` (a detected surface or an earlier item).</summary>
        /// <summary>Centre of a target (surface, item group or instance) nearest to `from`; null if unknown.</summary>
        public Vector3? CenterOf(string targetId, Vector3 from) => TargetCenter(new Api.Placement { TargetId = targetId }, from);

        Vector3? TargetCenter(Api.Placement p, Vector3 from)
        {
            EnsureImageTarget(p.TargetId);
            var s = FindSurface(p.TargetId);
            if (s != null) return s.Center;
            if (p.TargetId == null || !_byItem.TryGetValue(p.TargetId, out var items) || items.Count == 0) return null;
            var best = items[0].pos;
            foreach (var (pos, _) in items)
                if ((pos - from).sqrMagnitude < (best - from).sqrMagnitude) best = pos;
            return best;
        }

        /// <summary>
        /// Chairs around a table, a lamp beside a sofa: candidate spots on a ring just outside the
        /// target (its rectangle for a detected surface, a circle for an earlier item), on the floor,
        /// each facing the target. "around" spreads them evenly; "beside" takes the free spots
        /// nearest to the user.
        /// </summary>
        void PlaceAround(Api.Placement p, List<PlacedInstance> outList, float radius, bool beside)
        {
            var rings = new List<Func<int, List<(Vector3 pos, Vector3 centre)>>>();
            float gap = radius + 0.05f;
            var surface = FindSurface(p.TargetId);
            if (surface != null && !surface.IsVertical)
                rings.Add(n => RectRing(surface, gap, n));
            else if (p.TargetId != null && _byItem.TryGetValue(p.TargetId, out var items))
                foreach (var (pos, r) in items)
                    rings.Add(n => CircleRing(pos, r + gap, n));
            if (rings.Count == 0) return; // unknown target: caller falls back to the normal layouts

            int perTarget = Mathf.Max(1, Mathf.CeilToInt(p.Count / (float)rings.Count));
            foreach (var ring in rings)
            {
                var spots = ring(beside ? 12 : perTarget);
                if (beside) spots.Sort((a, b) => Distance(a.pos).CompareTo(Distance(b.pos)));
                int added = 0;
                foreach (var (pos, centre) in spots)
                {
                    if (added >= perTarget || outList.Count >= p.Count) break;
                    var at = new Vector3(pos.x, _scanner.FloorY, pos.z);
                    float dist = Distance(at);
                    if (dist < 0.4f || dist > p.MaxDistanceM || !Free(at, radius)) continue;
                    var inward = centre - at; inward.y = 0;
                    var rot = p.Facing == "toward_target" || !beside
                        ? Quaternion.LookRotation(inward.sqrMagnitude > 1e-4f ? inward.normalized : Vector3.forward, Vector3.up)
                        : GroundRotation(p.Facing, at, centre);
                    float h = p.HeightM * (1f + ((float)_rng.NextDouble() * 2f - 1f) * p.HeightJitter);
                    outList.Add(new PlacedInstance { Position = at, Rotation = rot, Height = h });
                    _occupied.Add((at, radius));
                    added++;
                }
            }
        }

        /// <summary>
        /// A cat under the table, a box under the desk: spots on the floor inside the target's
        /// footprint (a detected table or seat, an earlier generated table, a real thing pointed at in
        /// the picture), each object shrunk to fit the free height there. The free height is measured
        /// with a ray up from the floor against the room mesh (drawers, a crossbar), capped by the top.
        /// </summary>
        void PlaceUnder(Api.Placement p, List<PlacedInstance> outList, float radius)
        {
            var spots = new List<(Func<Vector3?> pick, float top, Vector3 centre)>();
            var surface = FindSurface(p.TargetId);
            if (surface != null && !surface.IsVertical)
            {
                var s = surface;
                spots.Add((() =>
                {
                    var local = RandomPointInPolygon(s.Boundary);
                    if (!local.HasValue) return null;
                    // keep a little inside the edge, so it really is under it
                    var l = local.Value * 0.8f;
                    var w = s.Pose.position + s.Pose.rotation * new Vector3(l.x, 0, l.y);
                    return new Vector3(w.x, _scanner.FloorY, w.z);
                }, s.Center.y, s.Center));
            }
            else if (p.TargetId != null && _byItem.TryGetValue(p.TargetId, out var items) && _heightsByItem.TryGetValue(p.TargetId, out var hs))
            {
                for (int i = 0; i < items.Count; i++)
                {
                    var (pos, r) = items[i];
                    float top = pos.y + hs[i];
                    spots.Add((() =>
                    {
                        float a = (float)_rng.NextDouble() * Mathf.PI * 2f, d = r * 0.6f * Mathf.Sqrt((float)_rng.NextDouble());
                        return new Vector3(pos.x + Mathf.Cos(a) * d, _scanner.FloorY, pos.z + Mathf.Sin(a) * d);
                    }, top, pos));
                }
            }
            if (spots.Count == 0) { Debug.Log($"[WhatIf] under: unknown target {p.TargetId}"); return; }

            int perTarget = Mathf.Max(1, Mathf.CeilToInt(p.Count / (float)spots.Count));
            float small = Mathf.Min(radius, 0.25f);
            foreach (var (pick, top, centre) in spots)
            {
                float clearance = top - _scanner.FloorY;
                if (clearance < 0.12f) continue; // a low bench: nothing fits beneath
                int added = 0;
                for (int attempt = 0; attempt < perTarget * 30 && added < perTarget && outList.Count < p.Count; attempt++)
                {
                    var at = pick();
                    if (!at.HasValue || Distance(at.Value) < 0.4f || !FreeOnTop(at.Value, small, outList)) continue;
                    // free height here: the table top, or lower where the mesh shows drawers or a crossbar
                    float free = clearance;
                    if (Physics.Raycast(at.Value + Vector3.up * 0.03f, Vector3.up, out var hit, clearance, _scanner.meshLayers))
                        free = Mathf.Min(free, hit.distance + 0.03f);
                    if (free < 0.12f) continue;
                    float want = p.HeightM * (1f + ((float)_rng.NextDouble() * 2f - 1f) * p.HeightJitter);
                    float h = Mathf.Min(want, free * 0.85f); // shrunk to fit, a little room above
                    outList.Add(new PlacedInstance { Position = at.Value, Rotation = GroundRotation(p.Facing, at.Value, centre), Height = h });
                    added++;
                }
            }
            Debug.Log($"[WhatIf] under {p.TargetId}: {outList.Count} placed");
        }

        /// <summary>
        /// Objects on top of an earlier generated object (plates on a generated table): spread over
        /// the inner part of its footprint, standing at its planned height.
        /// </summary>
        void PlaceOnItem(Api.Placement p, List<PlacedInstance> outList, float radius)
        {
            var targets = _byItem[p.TargetId];
            var heights = _heightsByItem[p.TargetId];
            if (targets.Count == 0) return;
            int perTarget = Mathf.Max(1, Mathf.CeilToInt(p.Count / (float)targets.Count));
            float small = Mathf.Min(radius, 0.3f); // small things on a table may stand close together
            for (int t = 0; t < targets.Count && outList.Count < p.Count; t++)
            {
                var (centre, r) = targets[t];
                float top = centre.y + heights[t];
                int added = 0;
                for (int attempt = 0; attempt < perTarget * 30 && added < perTarget && outList.Count < p.Count; attempt++)
                {
                    // ring of places for settings around the table's edge, a centre piece in the middle
                    float a = (float)_rng.NextDouble() * Mathf.PI * 2f;
                    float d = perTarget == 1 ? 0f : r * Mathf.Lerp(0.35f, 0.6f, (float)_rng.NextDouble());
                    var at = new Vector3(centre.x + Mathf.Cos(a) * d, top, centre.z + Mathf.Sin(a) * d);
                    if (!FreeOnTop(at, small, outList)) continue;
                    float h = p.HeightM * (1f + ((float)_rng.NextDouble() * 2f - 1f) * p.HeightJitter);
                    outList.Add(new PlacedInstance { Position = at, Rotation = GroundRotation(p.Facing, at, centre), Height = h });
                    added++;
                }
            }
        }

        static bool FreeOnTop(Vector3 at, float r, List<PlacedInstance> placed)
        {
            foreach (var o in placed)
                if ((o.Position - at).sqrMagnitude < 4f * r * r) return false;
            return true;
        }

        /// <summary>n spots on the rectangle of a horizontal surface, grown by `gap`; with 4 spots one per side.</summary>
        static List<(Vector3, Vector3)> RectRing(Surface s, float gap, int n)
        {
            float hx = s.Size.x / 2f + gap, hz = s.Size.y / 2f + gap;
            var ax = s.Pose.rotation * Vector3.right;
            var az = s.Pose.rotation * Vector3.forward;
            float len = 4f * (hx + hz);
            var list = new List<(Vector3, Vector3)>();
            for (int i = 0; i < n; i++)
            {
                // start in the middle of the first side, so 4 spots land in the middle of each side
                float t = (hx + (i + 0.5f) * len / n - (len / n) * 0.5f) % len;
                Vector2 q;
                if (t < 2 * hx) q = new Vector2(-hx + t, -hz);
                else if ((t -= 2 * hx) < 2 * hz) q = new Vector2(hx, -hz + t);
                else if ((t -= 2 * hz) < 2 * hx) q = new Vector2(hx - t, hz);
                else { t -= 2 * hx; q = new Vector2(-hx, hz - t); }
                list.Add((s.Center + ax * q.x + az * q.y, s.Center));
            }
            return list;
        }

        /// <summary>n spots evenly on a circle (around a generated object), starting towards the user.</summary>
        List<(Vector3, Vector3)> CircleRing(Vector3 centre, float r, int n)
        {
            var toUser = _head.position - centre; toUser.y = 0;
            float start = toUser.sqrMagnitude > 1e-4f ? Mathf.Atan2(toUser.z, toUser.x) : 0f;
            var list = new List<(Vector3, Vector3)>();
            for (int i = 0; i < n; i++)
            {
                float a = start + (i + 0.5f) * 2f * Mathf.PI / n;
                list.Add((centre + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * r, centre));
            }
            return list;
        }

        // ------------------------------------------------------------------ surfaces

        List<Surface> Matching(Api.Placement p)
        {
            var target = FindSurface(p.TargetId);
            if (target != null && p.Layout != "around" && p.Layout != "beside" && p.Layout != "under" && Fits(p.Surface, target))
                return new List<Surface> { target }; // "on this table"
            // A target of the wrong kind (seen: a giraffe "on the floor" with the ceiling as target,
            // which hung it 2.5 m below the floor) is ignored: the surface type decides.
            var list = new List<Surface>();
            foreach (var s in _surfaces)
            {
                bool ok = p.Surface switch
                {
                    "floor" => s.Label == "floor",
                    "wall" => s.Label == "wall",
                    "ceiling" => s.Label == "ceiling",
                    "table" => s.Label == "table" || s.Label == "seat",
                    "horizontal" => s.IsHorizontalUp,
                    // "any" never means the ceiling: hanging is asked for explicitly (seen: cups upside down there)
                    _ => Vector3.Dot(s.Normal, Vector3.down) <= 0.8f,
                };
                // "on a table" never means a seat pushed under a table (seen: a cup "on the table" on it)
                if (ok && p.Surface == "table" && Covered(s)) ok = false;
                if (ok && Distance(s.Center) - s.Size.magnitude / 2 < p.MaxDistanceM) list.Add(s);
            }
            // Ground-standing things still work outdoors / before scanning: virtual ground.
            bool groundish = p.Surface == "floor" || p.Surface == "horizontal" || p.Surface == "any" ||
                             (p.Surface == "table" && list.Count == 0);
            if (groundish && !list.Exists(s => s.Label == "floor" && s.Area > 4f))
                list.Add(_scanner.VirtualGround(Mathf.Min(p.MaxDistanceM, 15f)));
            return list;
        }

        /// <summary>A table or seat surface with another table top right above it (a chair under a table).</summary>
        bool Covered(Surface s)
        {
            if (s.Plane == null) return false;
            foreach (var o in _surfaces)
            {
                if (o == s || o.Plane == null || !o.IsHorizontalUp || o.Label == "floor") continue;
                if (o.Center.y < s.Center.y + 0.15f || o.Center.y > s.Center.y + 1.2f) continue;
                var local = Quaternion.Inverse(o.Pose.rotation) * (s.Center - o.Pose.position);
                if (PointInPolygon(new Vector2(local.x, local.z), o.Boundary)) return true;
            }
            return false;
        }

        static bool PointInPolygon(Vector2 p, List<Vector2> poly)
        {
            if (poly == null || poly.Count < 3) return false;
            bool inside = false;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            {
                var a = poly[i]; var b = poly[j];
                if ((a.y > p.y) != (b.y > p.y) && p.x < (b.x - a.x) * (p.y - a.y) / (b.y - a.y) + a.x) inside = !inside;
            }
            return inside;
        }

        /// <summary>Height of the lowest detected ceiling above a point (null: none detected).</summary>
        float? CeilingAbove(Vector3 world)
        {
            float? best = null;
            foreach (var s in _surfaces)
            {
                if (s.Plane == null || Vector3.Dot(s.Normal, Vector3.down) <= 0.8f) continue;
                if (s.Center.y <= world.y + 0.5f) continue;
                if (best == null || s.Center.y < best.Value) best = s.Center.y;
            }
            return best;
        }

        /// <summary>Can an item asking for this surface type stand on / hang from this surface?</summary>
        static bool Fits(string surface, Surface s)
        {
            bool up = s.IsHorizontalUp, down = Vector3.Dot(s.Normal, Vector3.down) > 0.8f;
            return surface switch
            {
                "floor" or "table" or "horizontal" => up,
                "wall" => s.IsVertical,
                "ceiling" => down,
                _ => true,
            };
        }

        float Distance(Vector3 world)
        {
            var d = world - _head.position;
            d.y = 0;
            return d.magnitude;
        }

        // ------------------------------------------------------------------ layouts

        void PlaceScatter(Api.Placement p, List<Surface> surfaces, List<PlacedInstance> outList, float radius)
        {
            int total = p.Count * 60, attempts = total;
            bool big = Big(p);
            while (outList.Count < p.Count && attempts-- > 0)
            {
                var s = PickWeighted(surfaces);
                var local = RandomPointInPolygon(s.Boundary);
                if (local == null) continue;
                // Big things (trees, pillars, wardrobes) go to the outer area of the room, so its
                // middle stays free for the user and small things; late attempts accept any spot.
                // strictly: a copy that finds no spot there is left out (a crown at eye level in the
                // middle of the room put the user's head inside trees)
                if (big && s.IsHorizontalUp && s.Plane != null && EdgeDistance(s, local.Value) > OuterBandM) continue;
                TryAdd(p, s, local.Value, radius, outList, preferView: true);
            }
        }

        const float OuterBandM = 1.3f;

        /// <summary>Reaching eye level (taller than 1.3 m) or wider than 1 m: kept to the outer area and
        /// away from the user.</summary>
        static bool Big(Api.Placement p) => p.HeightM > 1.3f || Footprint(p) > 1.0f;

        /// <summary>How far a spot is from the room's edge: the nearest wall or obstacle in the room
        /// mesh (eight directions), or the edge of the detected surface, whichever is nearer.</summary>
        float EdgeDistance(Surface s, Vector2 local)
        {
            float best = s.Plane != null ? PolygonEdgeDistance(local, s.Boundary) : float.PositiveInfinity;
            var world = s.Pose.position + s.Pose.rotation * new Vector3(local.x, 0, local.y) + Vector3.up * 0.4f;
            for (int i = 0; i < 8; i++)
            {
                var dir = Quaternion.Euler(0f, i * 45f, 0f) * Vector3.forward;
                if (Physics.Raycast(world, dir, out var hit, OuterBandM + 0.5f, _scanner.meshLayers))
                    best = Mathf.Min(best, hit.distance);
            }
            return best;
        }

        static float PolygonEdgeDistance(Vector2 pt, List<Vector2> poly)
        {
            if (poly == null || poly.Count < 2) return float.PositiveInfinity;
            float best = float.PositiveInfinity;
            for (int i = 0; i < poly.Count; i++)
            {
                Vector2 a = poly[i], b = poly[(i + 1) % poly.Count], ab = b - a;
                float t = ab.sqrMagnitude > 1e-6f ? Mathf.Clamp01(Vector2.Dot(pt - a, ab) / ab.sqrMagnitude) : 0f;
                best = Mathf.Min(best, (pt - (a + ab * t)).magnitude);
            }
            return best;
        }

        void PlaceSingle(Api.Placement p, List<Surface> surfaces, List<PlacedInstance> outList, float radius)
        {
            // Best-scoring of many samples: in view, at a comfortable distance.
            PlacedInstance? best = null;
            float bestScore = float.NegativeInfinity;
            float ideal = Mathf.Clamp(Mathf.Max(p.HeightM * 1.5f, MinUserDistance(p) + 1f), 1.5f, p.MaxDistanceM);
            for (int i = 0; i < 200; i++)
            {
                var s = PickWeighted(surfaces);
                var local = RandomPointInPolygon(s.Boundary);
                if (local == null) continue;
                var inst = Make(p, s, local.Value);
                if (!Free(inst.Position, radius)) continue;
                float score = ViewScore(inst.Position) * 2f - Mathf.Abs(Distance(inst.Position) - ideal) / ideal;
                if (score > bestScore) { bestScore = score; best = inst; }
            }
            if (best == null) return;
            outList.Add(best.Value);
            _occupied.Add((best.Value.Position, radius));
            if (p.Count > 1) PlaceScatter(p, surfaces, outList, radius);
        }

        void PlaceRow(Api.Placement p, List<Surface> surfaces, List<PlacedInstance> outList, float radius)
        {
            var s = Largest(surfaces);
            // the long axis of the surface, through its centre (in plane space)
            Vector2 axis = s.Size.x >= s.Size.y ? Vector2.right : Vector2.up;
            float len = Mathf.Max(s.Size.x, s.Size.y) * 0.9f;
            if (s.Plane == null) // virtual ground: row across the user's view, 3 m ahead
                axis = Flat(Quaternion.Inverse(s.Pose.rotation) * _head.right);
            Vector2 origin = s.Plane == null
                ? Flat(Quaternion.Inverse(s.Pose.rotation) * (Vector3.ProjectOnPlane(_head.forward, Vector3.up).normalized * 3f))
                : Vector2.zero;
            if (s.Plane == null) len = Mathf.Min(p.MaxDistanceM, p.Count * Mathf.Max(p.MinSpacingM, 1f));
            float step = Mathf.Max(p.MinSpacingM, Footprint(p) * 1.1f, 0.1f);
            int n = Mathf.Min(p.Count, Mathf.Max(1, Mathf.FloorToInt(len / step) + 1));
            for (int i = 0; i < n; i++)
            {
                float t = n == 1 ? 0 : (i / (float)(n - 1) - 0.5f) * len;
                TryAdd(p, s, origin + axis.normalized * t, radius * 0.9f, outList, preferView: false);
            }
            if (outList.Count < p.Count) PlaceScatter(p, surfaces, outList, radius);
        }

        void PlaceEdge(Api.Placement p, List<Surface> surfaces, List<PlacedInstance> outList, float radius)
        {
            // Walk along the boundary polygons; real boundaries hug walls, curbs and railings.
            var real = surfaces.FindAll(s => s.Plane != null && s.Boundary.Count >= 3);
            int attempts = p.Count * 60;
            while (real.Count > 0 && outList.Count < p.Count && attempts-- > 0)
            {
                var s = PickWeighted(real);
                var b = s.Boundary;
                int i = _rng.Next(b.Count);
                var a = b[i];
                var c = b[(i + 1) % b.Count];
                var pt = Vector2.Lerp(a, c, (float)_rng.NextDouble());
                // move slightly inwards so the object stands on the surface
                var centroid = Centroid(b);
                pt = Vector2.MoveTowards(pt, centroid, Mathf.Min(radius, 0.4f));
                TryAdd(p, s, pt, radius, outList, preferView: true);
            }
            if (outList.Count < p.Count) PlaceScatter(p, surfaces, outList, radius);
        }

        // ------------------------------------------------------------------ coats

        const int MaxCoatPieces = 250, MaxDrapePieces = 60; // coat pieces are flat quads: cheap

        /// <summary>
        /// A coat: many small pieces (moss clumps, leaves, shells, snow) lying close together on the
        /// floor or on table and seat tops, slightly pressed in and tilted. Pieces have their own
        /// volume, so a plane a few centimetres off just looks like loosely lying moss. They are no
        /// obstacles: other things may stand among them. Table pieces keep off the outline's edge,
        /// which is only roughly where the real edge is.
        /// </summary>
        void PlaceCoat(Api.Placement p, List<Surface> surfaces, List<PlacedInstance> outList)
        {
            if (p.Surface == "table") surfaces = surfaces.FindAll(s => s.Plane != null); // no virtual tables
            if (surfaces.Count == 0) return;
            float fp = Mathf.Max(0.04f, Footprint(p));
            // very_dense covers it all (sand in a desert): the patches overlap
            float coverage = p.Density switch { "sparse" => 0.15f, "normal" => 0.3f, "very_dense" => 1.6f, _ => 0.6f };
            float area = 0f;
            foreach (var s in surfaces) area += s.Plane != null ? s.Area : Mathf.PI * 16f;
            int wanted = Mathf.RoundToInt(area * coverage / (fp * fp));
            if (wanted > MaxCoatPieces)
            {
                // more than the cap: bigger patches instead of fewer (seen: 150 small sand flakes on a
                // 35 m2 floor that should be all sand)
                fp = Mathf.Min(1.6f, Mathf.Sqrt(area * coverage / MaxCoatPieces));
                p.FootprintM = fp; // each patch is shown this wide
            }
            int n = Mathf.Clamp(Mathf.RoundToInt(area * coverage / (fp * fp)), 1, MaxCoatPieces);
            var mine = new List<Vector3>();
            float gap = fp * (coverage > 1f ? 0.45f : 0.6f);
            for (int attempts = n * 30; outList.Count < n && attempts > 0; attempts--)
            {
                var s = PickWeighted(surfaces);
                var local = RandomPointInPolygon(s.Boundary);
                if (local == null) continue;
                if (s.Plane != null && s.Label != "floor" && PolygonEdgeDistance(local.Value, s.Boundary) < fp * 0.4f) continue;
                var world = s.Pose.position + s.Pose.rotation * new Vector3(local.Value.x, 0, local.Value.y);
                float dist = Distance(world);
                if (dist < 0.45f || dist > p.MaxDistanceM) continue; // the user's own spot stays free
                // only where the room mesh confirms a surface at that height (seen: moss floating at seat
                // and desk height off wrong or phantom planes); the piece then lies on the mesh
                if (Content.EnvironmentEffects.InWater(world)) continue; // the beach ends at the water
                var onMesh = SurfaceAt(world);
                if (!onMesh.HasValue) continue;
                world = onMesh.Value;
                bool close = false;
                foreach (var m in mine) if ((m - world).sqrMagnitude < gap * gap) { close = true; break; }
                if (close) continue;
                float h = p.HeightM * (1f + ((float)_rng.NextDouble() * 2f - 1f) * p.HeightJitter);
                // flat patches: any turn around the up axis, lying on the surface
                var rot = Quaternion.Euler(0f, (float)_rng.NextDouble() * 360f, 0f);
                outList.Add(new PlacedInstance { Position = world, Rotation = rot, Height = h });
                mine.Add(world);
            }
            Debug.Log($"[WhatIf] coat {p.Density}: {area:0.0} m2, piece {fp:0.00} m -> {outList.Count} of {n}");
        }

        /// <summary>
        /// A drape: pieces hanging down over the free edges of real tables and seats (ivy, seaweed, a
        /// cloth, icicles), their top at the edge, their front outwards. The plane's outline is only
        /// roughly right, so each spot looks in the room mesh for where the top really ends; edges
        /// against a wall, a backrest or other furniture get nothing.
        /// </summary>
        void PlaceDrape(Api.Placement p, List<Surface> surfaces, List<PlacedInstance> outList)
        {
            var tops = surfaces.FindAll(s => s.Plane != null && s.IsHorizontalUp && s.Boundary.Count >= 3 &&
                                             (s.Label == "table" || s.Label == "seat"));
            float fp = Mathf.Max(0.05f, Footprint(p));
            float coverage = p.Density switch { "sparse" => 0.35f, "dense" => 0.8f, "very_dense" => 1f, _ => 0.55f };
            float step = Mathf.Max(0.08f, fp) / coverage;
            foreach (var s in tops)
            {
                float top = s.Center.y;
                float room = top - _scanner.FloorY - 0.03f;
                if (room < 0.15f) continue;
                var b = s.Boundary;
                var centre = Centroid(b);
                float carry = (float)_rng.NextDouble() * step;
                for (int i = 0; i < b.Count && outList.Count < MaxDrapePieces; i++)
                {
                    Vector2 a = b[i], e = b[(i + 1) % b.Count];
                    float len = (e - a).magnitude;
                    if (len < 1e-3f) continue;
                    var dir = (e - a) / len;
                    var nrm = new Vector2(dir.y, -dir.x);
                    float t = carry;
                    for (; t < len && outList.Count < MaxDrapePieces; t += step)
                    {
                        var pt = Vector2.Lerp(a, e, t / len);
                        var n2 = Vector2.Dot(nrm, pt - centre) < 0f ? -nrm : nrm;
                        var outward = s.Pose.rotation * new Vector3(n2.x, 0f, n2.y);
                        outward.y = 0f;
                        if (outward.sqrMagnitude < 1e-4f) continue;
                        outward.Normalize();
                        var edge = s.Pose.position + s.Pose.rotation * new Vector3(pt.x, 0f, pt.y);
                        if (Distance(edge) > p.MaxDistanceM) continue;
                        var real = RealEdge(edge, outward, top);
                        if (!real.HasValue) continue;
                        float h = Mathf.Min(room, p.HeightM * (1f + ((float)_rng.NextDouble() * 2f - 1f) * p.HeightJitter));
                        var at = real.Value + outward * 0.02f;
                        at.y = top - h; // pivot at the base: the piece's top meets the edge
                        var rot = Quaternion.Euler(0f, Tilt(10f), 0f) * Quaternion.LookRotation(outward, Vector3.up);
                        outList.Add(new PlacedInstance { Position = at, Rotation = rot, Height = h });
                    }
                    carry = t - len;
                }
            }
            Debug.Log($"[WhatIf] drape: {tops.Count} tables / seats -> {outList.Count} pieces");
        }

        /// <summary>Where the top really ends near an outline point: probing the room mesh outwards
        /// from a little inside the outline. Null where nothing may hang: something stands right
        /// outside the edge (a wall, a backrest) or the top seems to go on.</summary>
        Vector3? RealEdge(Vector3 edge, Vector3 outward, float top)
        {
            // something right outside the edge, just below the top: nothing can hang there
            if (Physics.Raycast(edge + outward * 0.03f + Vector3.down * 0.12f, outward, 0.15f, _scanner.meshLayers))
                return null;
            for (float k = -0.06f; k <= 0.15f; k += 0.015f)
            {
                var from = edge + outward * k + Vector3.up * 0.15f;
                bool onTop = Physics.Raycast(from, Vector3.down, out var hit, 0.3f, _scanner.meshLayers) &&
                             hit.point.y > top - 0.04f;
                if (!onTop) return edge + outward * k;
            }
            return null;
        }

        /// <summary>The room mesh right under a point of a plane, if it is within 6 cm of the plane's
        /// height (an upward-facing surface); null where the mesh shows nothing there.</summary>
        Vector3? SurfaceAt(Vector3 onPlane)
        {
            if (!Physics.Raycast(onPlane + Vector3.up * 0.3f, Vector3.down, out var hit, 0.45f, _scanner.meshLayers))
                return null;
            if (Mathf.Abs(hit.point.y - onPlane.y) > 0.06f || hit.normal.y < 0.8f) return null;
            return hit.point;
        }

        float Tilt(float maxDeg) => ((float)_rng.NextDouble() * 2f - 1f) * maxDeg;

        // ------------------------------------------------------------------ helpers

        bool TryAdd(Api.Placement p, Surface s, Vector2 local, float radius, List<PlacedInstance> outList, bool preferView)
        {
            var inst = Make(p, s, local);
            float dist = Distance(inst.Position);
            if (dist < MinUserDistance(p) || dist > p.MaxDistanceM) return false;
            if (inst.OnWall && BehindFurniture(inst)) return false;
            if (!Free(inst.Position, radius)) return false;
            // Soft preference for the user's field of view: reject some samples behind them.
            if (preferView && (float)_rng.NextDouble() > 0.25f + 0.75f * ViewScore(inst.Position)) return false;
            outList.Add(inst);
            _occupied.Add((inst.Position, radius));
            return true;
        }

        PlacedInstance Make(Api.Placement p, Surface s, Vector2 local)
        {
            var world = s.Pose.position + s.Pose.rotation * new Vector3(local.x, 0, local.y);
            // on the floor: at the checked floor height, not the plane's own (a floor plane detected
            // 0.3 m too low put a dog below the floor)
            if (s.Label == "floor") world.y = _scanner.FloorY;
            float jitter = 1f + ((float)_rng.NextDouble() * 2f - 1f) * p.HeightJitter;
            float h = p.HeightM * jitter;
            // Fit the room: a 5.5 m giraffe under a 3 m ceiling is shrunk to stand under it, and
            // nothing hanging from the ceiling reaches below the floor.
            float? ceiling = CeilingAbove(world);
            bool hanging = Vector3.Dot(s.Normal, Vector3.down) > 0.8f;
            // Hanging things stay above the floor; standing things may reach through the ceiling (a tall
            // tree keeps its size instead of being squeezed into a crown at eye level).
            if (hanging) h = Mathf.Min(h, Mathf.Max(0.1f, world.y - _scanner.FloorY - 0.1f));
            if (s.IsVertical)
            {
                // Hang on the wall: forward points away from the wall, keep it between floor and ~3 m.
                float floor = _scanner.FloorY;
                world.y = Mathf.Clamp(world.y, floor + 0.05f, floor + Mathf.Max(0.1f, 3f - h));
                var fwd = s.Normal;
                return new PlacedInstance
                {
                    Position = world, Rotation = Quaternion.LookRotation(fwd, Vector3.up), Height = h, OnWall = true,
                };
            }
            if (Vector3.Dot(s.Normal, Vector3.down) > 0.8f)
            {
                var yaw = Quaternion.Euler(0, (float)_rng.NextDouble() * 360f, 0);
                world.y -= h; // pivot is at the object's base, so it hangs down from the ceiling
                return new PlacedInstance { Position = world, Rotation = yaw, Height = h, Hanging = true };
            }
            return new PlacedInstance { Position = world, Rotation = GroundRotation(p.Facing, world, TargetCenter(p, world)), Height = h };
        }

        /// <summary>
        /// Yaw for objects standing on a horizontal surface. Generated 3D models face their local +Z
        /// (the side seen in the source image), so "facing" means pointing +Z somewhere sensible.
        /// </summary>
        Quaternion GroundRotation(string facing, Vector3 pos, Vector3? target = null)
        {
            if (facing == "toward_target")
            {
                var to = (target ?? _head.position) - pos; to.y = 0;
                if (to.sqrMagnitude > 1e-4f) return Quaternion.LookRotation(to.normalized, Vector3.up);
                facing = "toward_user";
            }
            if (facing == "back_to_wall")
            {
                var away = AwayFromNearestWall(pos, 2.5f);
                if (away.HasValue) return Quaternion.LookRotation(away.Value, Vector3.up);
                facing = "toward_user"; // no wall nearby: at least face the viewer
            }
            if (facing == "toward_user")
            {
                var to = _head.position - pos;
                to.y = 0;
                if (to.sqrMagnitude > 1e-4f)
                {
                    float jitter = ((float)_rng.NextDouble() * 2f - 1f) * 25f; // not all robotically aligned
                    return Quaternion.Euler(0, jitter, 0) * Quaternion.LookRotation(to.normalized, Vector3.up);
                }
            }
            return Quaternion.Euler(0, (float)_rng.NextDouble() * 360f, 0);
        }

        /// <summary>Horizontal direction pointing away from the closest wall, if one is within maxDist.</summary>
        Vector3? AwayFromNearestWall(Vector3 pos, float maxDist)
        {
            Vector3? best = null;
            float bestDist = maxDist;
            foreach (var s in _surfaces)
            {
                if (s.Label != "wall") continue;
                var n = s.Normal; n.y = 0;
                if (n.sqrMagnitude < 1e-4f) continue;
                n.Normalize();
                // distance to the wall's plane, and only if the point is within the wall's extent
                float d = Vector3.Dot(pos - s.Center, n);
                var along = Vector3.ProjectOnPlane(pos - s.Center, n); along.y = 0;
                if (along.magnitude > s.Size.magnitude / 2 + 0.5f) continue;
                if (Mathf.Abs(d) < bestDist)
                {
                    bestDist = Mathf.Abs(d);
                    best = d >= 0 ? n : -n; // point into the room, i.e. towards the object's side
                }
            }
            return best;
        }

        /// <summary>Largest horizontal size of one instance; estimated from the height for older plans.</summary>
        static float Footprint(Api.Placement p) => p.FootprintM > 0f ? p.FootprintM : p.HeightM * 0.8f;

        /// <summary>Keep the object's edge at least 0.6 m from the user (big animals don't stand on your toes).</summary>
        /// <summary>
        /// Fillers given as a density ("ferns, dense"): as many as cover that share of the free area of
        /// the matching surfaces with their footprints, so a big room gets more than a small one.
        /// </summary>
        int DensityCount(Api.Placement p, List<Surface> surfaces)
        {
            float coverage = p.Density switch
            {
                "sparse" => 0.04f, "normal" => 0.10f, "dense" => 0.25f, "very_dense" => 0.45f, _ => 0f,
            };
            if (coverage <= 0f) return p.Count;
            float area = 0f;
            foreach (var s in surfaces)
                area += s.Plane != null ? s.Area : Mathf.PI * 16f; // virtual ground: about a 4 m circle
            area = Mathf.Max(0f, area - Mathf.PI * 0.8f * 0.8f);  // the user's own spot stays free
            float one = Mathf.Max(0.01f, Footprint(p) * Footprint(p));
            int n = Mathf.Clamp(Mathf.RoundToInt(area * coverage / one), 1, 60);
            Debug.Log($"[WhatIf] density {p.Density}: {area:0.0} m2 free, footprint {Footprint(p):0.00} m -> {n}");
            return n;
        }

        /// <summary>Clearance around the user: big things keep their crown or canopy away too (seen: a
        /// user sitting inside a tree whose canopy was wider than its footprint).</summary>
        static float MinUserDistance(Api.Placement p) => Big(p)
            ? Mathf.Max(Footprint(p) * 0.5f, p.HeightM * 0.35f) + 1.0f
            : Footprint(p) * 0.5f + 0.6f;

        /// <summary>A wall spot with something real in front of it (a cupboard, chair backs): without
        /// furniture occlusion the object would show in front of the furniture (seen: climbing holds
        /// "on" chairs).</summary>
        bool BehindFurniture(PlacedInstance inst)
        {
            var normal = inst.Rotation * Vector3.forward; // out of the wall
            var mid = inst.Position + Vector3.up * inst.Height * 0.5f;
            foreach (var from in new[] { inst.Position + Vector3.up * 0.05f, mid })
                if (Physics.Raycast(from + normal * 0.08f, normal, 0.7f, _scanner.meshLayers)) return true;
            return false;
        }

        bool Free(Vector3 pos, float radius)
        {
            // doorways stay passable (seen: a big tree right in the door frame)
            foreach (var d in RoomOcclusion.Doorways)
            {
                var to = pos - d; to.y = 0f;
                if (to.magnitude < radius + 0.9f) return false;
            }
            foreach (var (o, r) in _occupied)
                if ((o - pos).sqrMagnitude < (r + radius) * (r + radius)) return false;
            return true;
        }

        float ViewScore(Vector3 pos)
        {
            var to = pos - _head.position; to.y = 0;
            var fwd = _head.forward; fwd.y = 0;
            if (to.sqrMagnitude < 1e-4f || fwd.sqrMagnitude < 1e-4f) return 1f;
            return Mathf.Clamp01((Vector3.Dot(to.normalized, fwd.normalized) + 0.2f) / 1.2f);
        }

        Surface PickWeighted(List<Surface> list)
        {
            float total = 0;
            foreach (var s in list) total += Mathf.Max(0.05f, s.Area);
            float r = (float)_rng.NextDouble() * total;
            foreach (var s in list)
            {
                r -= Mathf.Max(0.05f, s.Area);
                if (r <= 0) return s;
            }
            return list[list.Count - 1];
        }

        static Surface Largest(List<Surface> list)
        {
            Surface best = list[0];
            foreach (var s in list) if (s.Area > best.Area) best = s;
            return best;
        }

        Vector2? RandomPointInPolygon(List<Vector2> poly)
        {
            if (poly == null || poly.Count < 3) return null;
            Vector2 min = poly[0], max = poly[0];
            foreach (var v in poly) { min = Vector2.Min(min, v); max = Vector2.Max(max, v); }
            for (int i = 0; i < 30; i++)
            {
                var pt = new Vector2(Mathf.Lerp(min.x, max.x, (float)_rng.NextDouble()),
                                     Mathf.Lerp(min.y, max.y, (float)_rng.NextDouble()));
                if (Inside(poly, pt)) return pt;
            }
            return null;
        }

        static bool Inside(List<Vector2> poly, Vector2 p)
        {
            bool inside = false;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            {
                if ((poly[i].y > p.y) != (poly[j].y > p.y) &&
                    p.x < (poly[j].x - poly[i].x) * (p.y - poly[i].y) / (poly[j].y - poly[i].y) + poly[i].x)
                    inside = !inside;
            }
            return inside;
        }

        static Vector2 Centroid(List<Vector2> poly)
        {
            var c = Vector2.zero;
            foreach (var v in poly) c += v;
            return c / poly.Count;
        }

        static Vector2 Flat(Vector3 v) => new Vector2(v.x, v.z);
    }
}
