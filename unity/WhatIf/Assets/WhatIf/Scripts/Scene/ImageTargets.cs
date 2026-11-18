using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace WhatIf.Scene
{
    /// <summary>
    /// Real things the planner sees in the camera image but the headset has no surface for (a
    /// keyboard, a person's head, a bin): the planner names a point in the image ("img:0.620,0.710",
    /// 0..1 from the top left) and this finds it in the room - a ray from the camera that took the
    /// image, through that point, against the depth image, the room mesh or a detected plane.
    /// The point is anchored on the thing itself first, then lifted to its top: seen at eye level, a
    /// table top is a thin line in the picture and a point just above it hits the wall behind, so a
    /// surface much nearer just below the point in the picture is taken instead (the table in front
    /// of the wall). From there the top is found (depth: up until the depth jumps; mesh: a short ray
    /// straight down), snapped to a detected table plane close by. A wall is never a top.
    /// </summary>
    public static class ImageTargets
    {
        public struct Hit
        {
            public Vector3 Point; // where the ray from the camera hit
            public Vector3 Top;   // the top of the thing at that spot  
            public string Via;    // "mesh" or "plane S3", for the log
        }

        public static bool IsImageTarget(string id) => id != null && id.StartsWith("img:");

        public static bool TryParse(string id, out float u, out float v)
        {
            u = v = 0;
            if (!IsImageTarget(id)) return false;
            var parts = id.Substring(4).Split(',');
            return parts.Length == 2
                   && float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out u)
                   && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        }

        const float JumpM = 0.25f;     // this much nearer just below the point: that is the thing meant
        const float BelowV = 0.06f;    // how far below the point to look (share of the image height)
        const float SnapM = 0.15f;     // a detected table plane this close to the found top is the top

        public static Hit? Resolve(string id, CameraView view, List<Surface> surfaces, LayerMask mesh, DepthSnapshot depth = null)
        {
            if (view == null || !TryParse(id, out float u, out float v)) return null;
            // the depth image taken with the picture first (it also sees people the mesh hasn't caught),
            // then the room mesh and detected planes
            if (depth != null)
            {
                var d = Anchor(u, v, view, r => depth.FirstHit(r, out var p) ? new Probe(p, Vector3.zero, "depth") : (Probe?)null);
                if (d.HasValue && depth.TopFrom(d.Value.Point, out var dTop))
                    return new Hit { Point = d.Value.Point, Top = Snap(dTop, surfaces), Via = d.Value.Via };
            }
            var m = Anchor(u, v, view, r => MeshOrPlane(r, surfaces, mesh));
            if (!m.HasValue) return null;
            var point = m.Value.Point;

            // the top of the thing there: straight down from a little above the hit (down-facing
            // faces such as a shelf's underside aren't hit from above)
            Vector3? top = null;
            float bestTop = float.NegativeInfinity;
            foreach (var hit in Physics.RaycastAll(point + Vector3.up * 0.3f, Vector3.down, 0.36f, mesh))
            {
                if (IsOwnContent(hit.collider) || Vector3.Dot(hit.normal, Vector3.up) < 0.5f) continue;
                if (hit.point.y > bestTop) { bestTop = hit.point.y; top = hit.point; }
            }
            if (!top.HasValue && m.Value.Normal.y > 0.5f) top = point; // a plane's or mesh's own top face
            if (!top.HasValue)
            {
                // a wall or a cupboard front: nothing to stand on there
                Debug.Log($"[WhatIf] image target {id}: {m.Value.Via} at {point:F2} is a vertical surface, not a top");
                return null;
            }
            return new Hit { Point = point, Top = Snap(top.Value, surfaces), Via = m.Value.Via };
        }

        struct Probe
        {
            public Vector3 Point, Normal; public string Via; public float Distance;
            public Probe(Vector3 p, Vector3 n, string via) { Point = p; Normal = n; Via = via; Distance = 0f; }
        }

        /// <summary>
        /// The hit for the picture point - or, when something much nearer lies just below it in the
        /// picture (a table edge in front of the wall), that nearer surface.
        /// </summary>
        static Probe? Anchor(float u, float v, CameraView view, System.Func<Ray, Probe?> cast)
        {
            var ray = view.Unproject(u, v);
            var first = cast(ray);
            float firstDist = first.HasValue ? Vector3.Distance(ray.origin, first.Value.Point) : float.PositiveInfinity;
            for (float dv = 0.01f; dv <= BelowV + 1e-4f; dv += 0.01f)
            {
                var r = view.Unproject(u, Mathf.Min(1f, v + dv));
                var below = cast(r);
                if (!below.HasValue) continue;
                if (Vector3.Distance(r.origin, below.Value.Point) < firstDist - JumpM)
                {
                    var p = below.Value;
                    p.Via += $" (nearer thing {dv * 100f:0}% below the point)";
                    return p;
                }
            }
            return first;
        }

        static Probe? MeshOrPlane(Ray ray, List<Surface> surfaces, LayerMask mesh)
        {
            const float maxDistance = 10f;

            // 1) the room mesh (finest detail: keyboards, bins, people if the mesh caught them)
            float best = float.PositiveInfinity;
            Vector3 point = default, normal = default;
            string via = null;
            foreach (var hit in Physics.RaycastAll(ray, maxDistance, mesh))
            {
                if (IsOwnContent(hit.collider)) continue;
                if (hit.distance < best) { best = hit.distance; point = hit.point; normal = hit.normal; via = "mesh"; }
            }
            // 2) detected planes (a table top the mesh hasn't covered yet), if nearer
            for (int i = 0; i < surfaces.Count; i++)
            {
                var s = surfaces[i];
                if (s.Plane == null) continue;
                var plane = new Plane(s.Normal, s.Center);
                if (!plane.Raycast(ray, out float d) || d <= 0 || d >= best || d > maxDistance) continue;
                var at = ray.GetPoint(d);
                var local = Quaternion.Inverse(s.Pose.rotation) * (at - s.Pose.position);
                if (!InPolygon(new Vector2(local.x, local.z), s.Boundary)) continue;
                best = d; point = at; normal = s.Normal; via = $"plane S{i + 1}";
            }
            if (via == null) return null;
            return new Probe(point, normal, via);
        }

        /// <summary>A detected table plane right at the found top (within SnapM, over it): its exact height.</summary>
        static Vector3 Snap(Vector3 top, List<Surface> surfaces)
        {
            float bestDy = SnapM;
            var result = top;
            foreach (var s in surfaces)
            {
                if (s.Plane == null || s.Normal.y < 0.9f) continue;
                float dy = Mathf.Abs(s.Center.y - top.y);
                if (dy >= bestDy) continue;
                var local = Quaternion.Inverse(s.Pose.rotation) * (top - s.Pose.position);
                if (!InPolygon(new Vector2(local.x, local.z), s.Boundary)) continue;
                bestDy = dy;
                result = new Vector3(top.x, s.Center.y, top.z);
            }
            return result;
        }

        static bool IsOwnContent(Collider c) => c.GetComponentInParent<Content.GeneratedObject>() != null;

        static bool InPolygon(Vector2 p, List<Vector2> poly)
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
    }
}
