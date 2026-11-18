using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using WhatIf.Content;

namespace WhatIf.Voice
{
    /// <summary>
    /// Which generated object the user is looking at, so "make it bigger" knows what "it" is.
    ///
    /// On the Magic Leap 2 the eye-gaze pose comes from OpenXR's Eye Gaze Interaction profile (enable
    /// it under XR Plug-in Management > OpenXR > Interaction Profiles; the app asks for the eye
    /// tracking permission). Without eye tracking (Editor, profile off) the head direction is used.
    /// Every frame the object closest to the gaze ray is recorded, so a sentence can be matched to
    /// what was looked at while (and just before) it was spoken.
    /// </summary>
    public class GazeTracker : MonoBehaviour
    {
        public Transform head;
        [Tooltip("Objects within this angle of the gaze ray count as looked at (eye tracking is ~1-2 deg accurate; " +
                 "head direction much less, so the fallback uses a wider cone).")]
        public float eyeConeDeg = 6f;
        public float headConeDeg = 12f;

        public bool UsingEyes { get; private set; }

        /// <summary>True if a gaze ray hits a painted (or otherwise changed) real surface; set by the controller.</summary>
        public System.Func<Ray, bool> SurfaceHit;

        readonly List<(float time, GeneratedObject obj, bool surface)> _history = new List<(float, GeneratedObject, bool)>();
        InputDevice _eyes;
        Vector3Control _eyePos;
        QuaternionControl _eyeRot;

        void Update()
        {
            if (head == null) return;
            var ray = GazeRay(out bool eyes);
            UsingEyes = eyes;
            var hit = Pick(ray, eyes ? eyeConeDeg : headConeDeg);
            bool surface = hit == null && SurfaceHit != null && SurfaceHit(ray);
            float now = Time.realtimeSinceStartup;
            _history.Add((now, hit, surface));
            // where the gaze meets the real room (room mesh): "put it there"
            if (Physics.Raycast(ray, out var roomHit, 12f, meshLayers) &&
                roomHit.collider.GetComponentInParent<GeneratedObject>() == null)
                _points.Add((now, roomHit.point, roomHit.normal));
            // keep ~15 s
            int drop = 0;
            while (drop < _history.Count && _history[drop].time < now - 15f) drop++;
            if (drop > 0) _history.RemoveRange(0, drop);
            drop = 0;
            while (drop < _points.Count && _points[drop].time < now - 15f) drop++;
            if (drop > 0) _points.RemoveRange(0, drop);
        }

        /// <summary>Layers of the room mesh (set by the controller).</summary>
        public LayerMask meshLayers = ~0;

        readonly List<(float time, Vector3 point, Vector3 normal)> _points = new List<(float, Vector3, Vector3)>();

        /// <summary>
        /// The point in the room the user looked at towards the end of a sentence ("put the lamp there"):
        /// the sample nearest to the middle of the gaze points of the last 1.2 s; false if there were
        /// too few (eyes closed, nothing meshed there).
        /// </summary>
        public bool PointDuring(float from, float to, out Vector3 point, out Vector3 normal)
        {
            point = normal = default;
            float start = Mathf.Max(from, to - 1.2f);
            var pts = new List<(Vector3 p, Vector3 n)>();
            foreach (var (t, p, n) in _points)
                if (t >= start && t <= to) pts.Add((p, n));
            if (pts.Count < 3) return false;
            var mean = Vector3.zero;
            foreach (var (p, _) in pts) mean += p;
            mean /= pts.Count;
            float best = float.MaxValue;
            foreach (var (p, n) in pts)
            {
                float d = (p - mean).sqrMagnitude;
                if (d < best) { best = d; point = p; normal = n; }
            }
            return true;
        }

        /// <summary>
        /// Whether the user looked at the generated scene between two times: at an object or a painted
        /// surface for at least a quarter of the time. Used to tell commands from talking to people.
        /// </summary>
        public bool LookedAtSceneDuring(float from, float to)
        {
            int all = 0, scene = 0;
            foreach (var (t, obj, surface) in _history)
            {
                if (t < from || t > to) continue;
                all++;
                if (surface || (obj != null && obj.gameObject.activeInHierarchy)) scene++;
            }
            return all > 0 && scene >= Mathf.Max(1, all / 4);
        }

        /// <summary>The object looked at longest between two times (realtimeSinceStartup); null if none.</summary>
        public GeneratedObject LookedAtDuring(float from, float to)
        {
            var dwell = new Dictionary<GeneratedObject, int>();
            foreach (var (t, obj, _) in _history)
                if (t >= from && t <= to && obj != null && obj.gameObject.activeInHierarchy)
                    dwell[obj] = dwell.TryGetValue(obj, out var n) ? n + 1 : 1;
            GeneratedObject best = null;
            int bestN = 0;
            foreach (var kv in dwell)
                if (kv.Value > bestN) { best = kv.Key; bestN = kv.Value; }
            return best;
        }

        Ray GazeRay(out bool eyes)
        {
            eyes = false;
            if (_eyes == null || !_eyes.added)
            {
                _eyes = null;
                foreach (var d in InputSystem.devices)
                {
                    // OpenXR's eye gaze device ("EyeGaze"); controls pose/position and pose/rotation
                    if (d.layout.IndexOf("EyeGaze", System.StringComparison.OrdinalIgnoreCase) < 0) continue;
                    _eyes = d;
                    _eyePos = d.TryGetChildControl<Vector3Control>("pose/position");
                    _eyeRot = d.TryGetChildControl<QuaternionControl>("pose/rotation");
                    break;
                }
            }
            if (_eyes != null && _eyePos != null && _eyeRot != null)
            {
                var rot = _eyeRot.ReadValue();
                if (rot != default && Mathf.Abs(rot.w) + Mathf.Abs(rot.x) + Mathf.Abs(rot.y) + Mathf.Abs(rot.z) > 0.1f)
                {
                    // device poses are in tracking space: the camera's parent (camera offset) maps them to world
                    var space = head.parent;
                    var pos = space != null ? space.TransformPoint(_eyePos.ReadValue()) : _eyePos.ReadValue();
                    var dir = space != null ? space.rotation * (rot * Vector3.forward) : rot * Vector3.forward;
                    eyes = true;
                    return new Ray(pos, dir);
                }
            }
            return new Ray(head.position, head.forward);
        }

        /// <summary>The object whose bounds the ray hits (nearest), else the one closest in angle within the cone.</summary>
        static GeneratedObject Pick(Ray ray, float coneDeg)
        {
            GeneratedObject best = null;
            float bestHit = float.MaxValue, bestAngle = coneDeg;
            foreach (var obj in FindObjectsOfType<GeneratedObject>())
            {
                if (!obj.isActiveAndEnabled) continue;
                var b = obj.WorldBounds();
                if (b.IntersectRay(ray, out float d) && d < bestHit) { bestHit = d; best = obj; continue; }
                if (bestHit < float.MaxValue) continue; // a direct hit beats near misses
                float angle = Vector3.Angle(ray.direction, b.center - ray.origin);
                if (angle < bestAngle) { bestAngle = angle; best = obj; }
            }
            return best;
        }
    }
}
