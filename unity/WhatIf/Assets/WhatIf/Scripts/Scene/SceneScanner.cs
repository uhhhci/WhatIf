using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using WhatIf.Api;

namespace WhatIf.Scene
{
    /// <summary>A detected surface with a resolved semantic label.</summary>
    public class Surface
    {
        public ARPlane Plane;          // null for the virtual fallback ground and estimated walls
        public string Id;              // set for estimated walls (no ARPlane)
        public string Label;           // floor | wall | ceiling | table | seat | unknown
        public Vector3 Center;
        public Vector3 Normal;
        public Vector2 Size;
        public Pose Pose;              // plane space: x/z on the surface, y = normal
        public List<Vector2> Boundary; // polygon in plane space (x, z)
        public float Area => Size.x * Size.y;

        public bool IsHorizontalUp => Vector3.Dot(Normal, Vector3.up) > 0.8f;
        public bool IsVertical => Mathf.Abs(Vector3.Dot(Normal, Vector3.up)) < 0.3f;
    }

    /// <summary>
    /// Collects the planes found by Magic Leap plane extraction (through AR Foundation),
    /// labels them and estimates the floor height. Works without any planes too:
    /// outdoors, ML2 depth sensing is often unreliable, so we fall back to a virtual
    /// ground disc at head height minus the configured head height.
    /// </summary>
    public class SceneScanner : MonoBehaviour
    {
        public ARPlaneManager planeManager;
        public Transform head;                   // usually the XR camera
        [Tooltip("Optional: meshing colliders are raycast to find the ground when no floor plane exists.")]
        public LayerMask meshLayers = ~0;

        public float FloorY { get; private set; }
        public bool FloorFromSensors { get; private set; }
        /// <summary>True once <see cref="Collect"/> has estimated <see cref="FloorY"/>.</summary>
        public bool HasFloorEstimate { get; private set; }

        public List<Surface> Collect(WhatIfSettings settings)
        {
            var result = new List<Surface>();
            FloorY = EstimateFloor(settings);
            HasFloorEstimate = true;

            if (planeManager != null)
            {
                foreach (var plane in planeManager.trackables)
                {
                    if (plane.trackingState != TrackingState.Tracking) continue;
                    // nothing can be seen below the floor: such planes are reflections or errors
                    // (seen: a "floor" 1.2 m below the real one, on which objects got lost)
                    if (FloorFromSensors && plane.center.y < FloorY - 0.3f &&
                        Mathf.Abs(Vector3.Dot(plane.normal, Vector3.up)) > 0.8f) continue;
                    var boundary = new List<Vector2>();
                    foreach (var p in plane.boundary) boundary.Add(p);
                    var s = new Surface
                    {
                        Plane = plane,
                        Center = plane.center,
                        Normal = plane.normal,
                        Size = plane.size,
                        Pose = new Pose(plane.transform.position, plane.transform.rotation),
                        Boundary = boundary,
                    };
                    s.Label = Classify(plane, s);
                    // a floor, table or seat that faces down (seen on ML2) would hang objects from
                    // it like from a ceiling: turn it over (normal up, plane space flipped with it)
                    if ((s.Label == "floor" || s.Label == "table" || s.Label == "seat") &&
                        Vector3.Dot(s.Normal, Vector3.up) < -0.8f)
                    {
                        s.Normal = -s.Normal;
                        s.Pose = new Pose(s.Pose.position, s.Pose.rotation * Quaternion.Euler(180f, 0f, 0f));
                        for (int i = 0; i < s.Boundary.Count; i++) s.Boundary[i] = new Vector2(s.Boundary[i].x, -s.Boundary[i].y);
                    }
                    result.Add(s);
                }
            }
            // walls estimated from the room mesh (RoomOcclusion): things can hang on walls the plane
            // detection missed
            int n = 0;
            foreach (var (a, b) in RoomOcclusion.Walls)
            {
                var along = new Vector3(b.x - a.x, 0f, b.y - a.y);
                float len = along.magnitude;
                if (len < 0.5f) continue;
                along /= len;
                var mid = new Vector3((a.x + b.x) * 0.5f, FloorY + 1.4f, (a.y + b.y) * 0.5f);
                var normal = new Vector3(-along.z, 0f, along.x);
                if (Vector3.Dot(normal, head.position - mid) < 0f) normal = -normal; // facing into the room
                // plane space like an ARPlane: y = normal, x along the wall, z up the wall
                var rot = Quaternion.LookRotation(Vector3.up, normal);
                if (Vector3.Dot(rot * Vector3.right, along) < 0f) along = -along;
                result.Add(new Surface
                {
                    Id = "estimated-wall-" + n++,
                    Label = "wall",
                    Center = mid,
                    Normal = normal,
                    Size = new Vector2(len, 2.5f),
                    Pose = new Pose(mid, rot),
                    Boundary = new List<Vector2>
                    {
                        new Vector2(-len / 2f, -1.3f), new Vector2(len / 2f, -1.3f),
                        new Vector2(len / 2f, 1.1f), new Vector2(-len / 2f, 1.1f),
                    },
                });
            }
            return result;
        }

        /// <summary>Virtual ground disc used when no suitable surface was detected.</summary>
        public Surface VirtualGround(float radius)
        {
            var c = new Vector3(head.position.x, FloorY, head.position.z);
            var poly = new List<Vector2>();
            for (int i = 0; i < 24; i++)
            {
                float a = i / 24f * Mathf.PI * 2f;
                poly.Add(new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * radius);
            }
            return new Surface
            {
                Label = "floor", Center = c, Normal = Vector3.up, Size = Vector2.one * radius * 2,
                Pose = new Pose(c, Quaternion.identity), Boundary = poly,
            };
        }

        /// <summary>Id the server and the planner use to refer to a surface.</summary>
        public static string SurfaceId(Surface s) => s.Plane != null ? s.Plane.trackableId.ToString() : s.Id ?? "virtual";

        public SceneContext Describe(List<Surface> surfaces, WhatIfSettings settings, CameraView view = null)
        {
            var ctx = new SceneContext
            {
                HeadHeightM = head.position.y - FloorY,
            };
            float extent = 0;
            var gaze = Vector3.ProjectOnPlane(head.forward, Vector3.up);
            // Image positions only make sense when the camera frame shows the room (on the headset):
            // projected with the camera that took the frame (pose + lens), so the numbers drawn into
            // the image sit on the right surfaces.
            if (!settings.markSurfacesInImage) view = null;
            foreach (var s in surfaces)
            {
                var flat = new Vector3(s.Center.x - head.position.x, 0, s.Center.z - head.position.z);
                extent = Mathf.Max(extent, flat.magnitude + s.Size.magnitude / 2);
                var info = new PlaneInfo
                {
                    Id = SurfaceId(s),
                    Label = s.Label,
                    WidthM = s.Size.x,
                    HeightM = s.Size.y,
                    DistanceM = flat.magnitude,
                    HeightRelHeadM = s.Center.y - head.position.y,
                    HeightAboveFloorM = s.Center.y - FloorY,
                };
                if (gaze.sqrMagnitude > 1e-4f && flat.sqrMagnitude > 1e-4f)
                    info.BearingDeg = Vector3.SignedAngle(gaze, flat, Vector3.up);
                var uv = view?.Project(s.Center);
                if (uv.HasValue)
                {
                    info.ViewX = uv.Value.x;
                    info.ViewY = uv.Value.y;
                }
                ctx.Planes.Add(info);
            }
            if (surfaces.Count > 0) ctx.MeshExtentM = extent;
            return ctx;
        }

        string Classify(ARPlane plane, Surface s)
        {
            // Magic Leap's plane extraction provides semantic labels through AR Foundation.
            switch (plane.classification)
            {
                case PlaneClassification.Floor: return "floor";
                case PlaneClassification.Wall: return "wall";
                case PlaneClassification.Ceiling: return "ceiling";
                case PlaneClassification.Table: return "table";
                case PlaneClassification.Seat: return "seat";
            }
            // Geometric fallback.
            if (s.IsVertical) return "wall";
            if (Vector3.Dot(s.Normal, Vector3.down) > 0.8f) return "ceiling";
            if (s.IsHorizontalUp)
            {
                float aboveFloor = s.Center.y - FloorY;
                if (aboveFloor < 0.25f) return "floor";
                if (aboveFloor < 1.3f) return "table";
            }
            return "unknown";
        }

        float EstimateFloor(WhatIfSettings settings)
        {
            // a) the room mesh straight down: the first surface well below the head (a seated user's
            //    ray hits the seat or desk first - seen: 0.57 m with the floor at 0 - so only hits
            //    more than 0.8 m below the head count)
            float meshFloor = float.NegativeInfinity;
            foreach (var hit in Physics.RaycastAll(head.position, Vector3.down, 3f, meshLayers))
            {
                if (hit.point.y > head.position.y - 0.8f) continue;
                if (hit.collider.GetComponentInParent<Content.GeneratedObject>() != null) continue; // own content
                meshFloor = Mathf.Max(meshFloor, hit.point.y);
            }
            bool haveMesh = !float.IsNegativeInfinity(meshFloor);

            // b) planes. With the mesh known, it is the reference: a plane only refines it when it agrees
            //    within a few cm (seen: a "floor" plane 0.3 m too low won over the mesh at 0 - wall items
            //    and things standing on the floor ended up below it; earlier one at -1.20 m). Without the
            //    mesh: the largest plane classified as floor, else the largest horizontal one - not the
            //    lowest, which is the one most likely wrong.
            float classified = float.PositiveInfinity, large = float.PositiveInfinity, close = float.PositiveInfinity;
            float classifiedArea = 0f, largeArea = 0f, closeGap = 0.06f;
            if (planeManager != null)
            {
                foreach (var p in planeManager.trackables)
                {
                    if (p.trackingState != TrackingState.Tracking) continue;
                    bool up = Vector3.Dot(p.normal, Vector3.up) > 0.8f;
                    bool below = p.center.y < head.position.y - 0.8f;
                    if (!up || !below) continue;
                    float area = p.size.x * p.size.y;
                    if (haveMesh)
                    {
                        float gap = Mathf.Abs(p.center.y - meshFloor);
                        if (gap < closeGap) { closeGap = gap; close = p.center.y; }
                        continue;
                    }
                    if (p.classification == PlaneClassification.Floor && area > classifiedArea) { classifiedArea = area; classified = p.center.y; }
                    else if (area > 1f && area > largeArea) { largeArea = area; large = p.center.y; }
                }
            }
            FloorFromSensors = true;
            if (haveMesh) return float.IsInfinity(close) ? meshFloor : close;
            if (!float.IsInfinity(classified)) return classified;
            if (!float.IsInfinity(large)) return large;

            // c) assume a standing user
            FloorFromSensors = false;
            return head.position.y - settings.defaultHeadHeightM;
        }
    }
}
