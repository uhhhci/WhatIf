using System.Collections.Generic;
using UnityEngine;

namespace WhatIf.Content
{
    /// <summary>
    /// Moves the parts the server cut out of a 3D model (nodes named "part__name__motion", pivot at
    /// the attachment point): wag (side to side), nod (up and down), spin (wheels), flap (wings),
    /// swing (legs). Faster while the object moves (BehaviourRunner). Model space: front of the
    /// source image = +Z, image right = +X, up = +Y.
    /// </summary>
    public class PartMotion : MonoBehaviour
    {
        readonly List<(Transform t, string motion, Quaternion rest, float phase)> _parts =
            new List<(Transform, string, Quaternion, float)>();
        BehaviourRunner _runner;
        bool _sideView; // generated in side view facing right (movers): front = +X, else front = +Z

        /// <summary>Adds the animation to a freshly loaded model if it has parts; returns how many.</summary>
        public static int Attach(GameObject model, bool sideView)
        {
            var found = new List<Transform>();
            foreach (var t in model.GetComponentsInChildren<Transform>(true))
                if (t.name.StartsWith("part__")) found.Add(t);
            if (found.Count == 0) return 0;
            var pm = model.AddComponent<PartMotion>();
            pm._sideView = sideView;
            int i = 0;
            foreach (var t in found)
            {
                var bits = t.name.Split(new[] { "__" }, System.StringSplitOptions.None);
                var motion = bits.Length >= 3 ? bits[2] : "wag";
                pm._parts.Add((t, motion, t.localRotation, i++ * 1.7f));
            }
            return found.Count;
        }

        void Update()
        {
            if (_runner == null) _runner = GetComponentInParent<BehaviourRunner>();
            float speed = _runner != null && _runner.IsMoving ? 2f : 1f;
            float time = Time.time * speed;
            foreach (var (t, motion, rest, phase) in _parts)
            {
                if (t == null) continue;
                float s = Mathf.Sin(time * Rate(motion) + phase);
                // pitch (nod, swing, wheels) turns around the axis across the body, roll (wings)
                // around the axis along it: which model axis that is depends on where the front is
                var across = _sideView ? Vector3.forward : Vector3.right;
                var along = _sideView ? Vector3.right : Vector3.forward;
                bool moving = _runner != null && _runner.IsMoving;
                var q = motion switch
                {
                    "wag" => Quaternion.AngleAxis(s * 25f, Vector3.up),
                    "nod" => Quaternion.AngleAxis(s * 8f, across),
                    "flap" => Quaternion.AngleAxis(s * 35f, along),
                    "swing" => Quaternion.AngleAxis(s * (moving ? 22f : 3f), across),
                    "spin" => Quaternion.AngleAxis(-(time * (moving ? 360f : 0f)) % 360f, across),
                    _ => Quaternion.identity,
                };
                t.localRotation = rest * q;
            }
        }

        static float Rate(string motion) => motion switch
        {
            "wag" => 9f, "nod" => 2f, "flap" => 10f, "swing" => 6f, _ => 1f,
        };
    }
}
