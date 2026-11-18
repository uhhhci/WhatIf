using System;
using UnityEngine;
using UnityEngine.InputSystem;
using WhatIf.Content;

namespace WhatIf.Voice
{
    /// <summary>
    /// Direct hand interaction, no gaze: reach into an object and pinch (thumb and index finger) or
    /// close the hand - the object is held and follows the hand, turning with it on all axes like
    /// something held (wall items only slide along the wall).
    /// Let go and it stays (a floor object lands on what is below it). With both hands on the same
    /// object: turn it (the line between the hands) and scale it (their distance).
    /// No colliders or rigidbodies needed: the hand is tested against each object's visible bounds
    /// (a few centimetres of slack), the smallest object containing the hand wins.
    /// Uses OpenXR's Hand Interaction profile (enable "Hand Interaction Profile" in the OpenXR
    /// features; What If > Configure Magic Leap 2 does it, and every build checks it).
    /// </summary>
    public class PinchGrab : MonoBehaviour
    {
        public Transform head;
        /// <summary>Before a grab changes anything (for undo).</summary>
        public Action<GeneratedObject> GrabStarted;
        /// <summary>After letting go: final pose (re-anchor, snap to the floor).</summary>
        public Action<GeneratedObject> GrabEnded;

        /// <summary>Only things up to this size (largest side, m) can be picked up: a parrot, a lamp, a
        /// chair - not a palm tree, whose crown would also catch every reach for what stands under it.</summary>
        public float MaxSizeM = 2f;
        /// <summary>The hand's speed when it let go (m/s, world): a thrown object flies on with it.</summary>
        public Vector3 ReleaseVelocity { get; private set; }
        Vector3 _lastPos, _vel;

        const float Reach = 0.06f; // how far outside an object's bounds a hand still grabs it

        sealed class Hand
        {
            public InputAction Pinch, Grasp, PinchPos, GripPos, GripRot;
            public bool On, UsingGrip;
            public Hand(string side)
            {
                Pinch = new InputAction("pinch " + side, binding: $"<HandInteraction>{{{side}Hand}}/pinchValue");
                Grasp = new InputAction("grasp " + side, binding: $"<HandInteraction>{{{side}Hand}}/graspValue");
                PinchPos = new InputAction("pinch pos " + side, binding: $"<HandInteraction>{{{side}Hand}}/pinchPosition");
                GripPos = new InputAction("grip pos " + side, binding: $"<HandInteraction>{{{side}Hand}}/devicePosition");
                GripRot = new InputAction("grip rot " + side, binding: $"<HandInteraction>{{{side}Hand}}/deviceRotation");
            }
            public void Enable(bool on)
            {
                foreach (var a in new[] { Pinch, Grasp, PinchPos, GripPos, GripRot })
                    if (on) a.Enable(); else a.Disable();
            }
            /// <summary>Pinching or a closed hand (with hysteresis); remembers which, for the position.</summary>
            public bool Holding()
            {
                float hi = On ? 0.6f : 0.85f;
                bool pinch = Pinch.ReadValue<float>() > hi, grasp = Grasp.ReadValue<float>() > hi;
                if (!On) UsingGrip = grasp && !pinch;
                return pinch || grasp;
            }
        }

        readonly Hand _right = new Hand("Right"), _left = new Hand("Left");
        GeneratedObject _held;
        Hand _holder;               // the hand holding it alone
        Vector3 _offset;            // object position in the hand's frame
        Quaternion _rotOffset;      // object rotation in the hand's frame
        bool _two;
        float _startYaw, _startDistance;
        Quaternion _rotStart;
        Vector3 _scaleStart;

        void OnEnable() { _right.Enable(true); _left.Enable(true); }
        void OnDisable() { _right.Enable(false); _left.Enable(false); Release(); }

        Transform Space => head != null ? head.parent : null; // device poses are in tracking space

        Vector3 Pos(Hand h)
        {
            var p = (h.UsingGrip ? h.GripPos : h.PinchPos).ReadValue<Vector3>();
            return Space != null ? Space.TransformPoint(p) : p;
        }

        Quaternion Rot(Hand h)
        {
            var r = h.GripRot.ReadValue<Quaternion>();
            if (r.x == 0f && r.y == 0f && r.z == 0f && r.w == 0f) r = Quaternion.identity; // not tracked yet
            return Space != null ? Space.rotation * r : r;
        }

        /// <summary>The object the hand is in (smallest first), or null.</summary>
        GeneratedObject At(Vector3 p)
        {
            GeneratedObject best = null;
            float bestSize = float.MaxValue;
            foreach (var o in FindObjectsOfType<GeneratedObject>())
            {
                if (o.Coat) continue; // moss and sand patches are not things to pick up
                var b = o.WorldBounds();
                if (Mathf.Max(b.size.x, b.size.y, b.size.z) > MaxSizeM) continue; // too big to pick up
                b.Expand(Reach * 2f);
                if (!b.Contains(p)) continue;
                float size = b.size.sqrMagnitude;
                if (size < bestSize) { best = o; bestSize = size; }
            }
            return best;
        }

        void Update()
        {
            if (head == null) return;
            bool r = _right.Holding(), l = _left.Holding();
            bool rStarted = r && !_right.On, lStarted = l && !_left.On;
            _right.On = r; _left.On = l;

            if (_held == null)
            {
                var hand = rStarted ? _right : lStarted ? _left : null;
                if (hand == null) return;
                var target = At(Pos(hand));
                if (target == null) return;
                _held = target;
                GrabStarted?.Invoke(_held);
                BeginOneHand(hand);
                Debug.Log($"[WhatIf] grab: {_held.InstanceId} ({(hand == _right ? "right" : "left")} hand)");
                return;
            }

            // the other hand joins only if it reaches into the same object
            if ((rStarted && _holder != _right && At(Pos(_right)) != _held) ||
                (lStarted && _holder != _left && At(Pos(_left)) != _held))
            {
                if (rStarted && _holder != _right) _right.On = false;
                if (lStarted && _holder != _left) _left.On = false;
                r = _right.On; l = _left.On;
            }
            if (!r && !l) { Release(); return; }

            if (r && l)
            {
                var d = Pos(_right) - Pos(_left); d.y = 0;
                if (!_two)
                {
                    _two = true;
                    _startYaw = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
                    _startDistance = Mathf.Max(0.05f, d.magnitude);
                    _rotStart = _held.transform.rotation;
                    _scaleStart = _held.transform.localScale;
                }
                float yaw = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
                _held.transform.rotation = Quaternion.Euler(0f, Mathf.DeltaAngle(_startYaw, yaw), 0f) * _rotStart;
                _held.transform.localScale = _scaleStart * Mathf.Clamp(d.magnitude / _startDistance, 0.2f, 5f);
                return;
            }
            var h = r ? _right : _left;
            if (_two || h != _holder) { _two = false; BeginOneHand(h); }

            // held like a real thing: position and rotation follow the hand on all axes
            var handRot = Rot(h);
            var handPos = Pos(h);
            if (Time.deltaTime > 1e-4f) _vel = Vector3.Lerp(_vel, (handPos - _lastPos) / Time.deltaTime, 0.5f);
            _lastPos = handPos;
            var pos = handPos + handRot * _offset;
            if (_held.OnWall) { _held.transform.position = pos; return; } // stays flat on the wall
            _held.transform.SetPositionAndRotation(pos, handRot * _rotOffset);
        }

        void BeginOneHand(Hand h)
        {
            _holder = h;
            _lastPos = Pos(h);
            _vel = Vector3.zero;
            var inv = Quaternion.Inverse(Rot(h));
            _offset = inv * (_held.transform.position - Pos(h));
            _rotOffset = inv * _held.transform.rotation;
        }

        void Release()
        {
            if (_held == null) return;
            var o = _held;
            ReleaseVelocity = _two ? Vector3.zero : _vel; // let go with both hands: it just drops
            _held = null;
            _holder = null;
            _two = false;
            _vel = Vector3.zero;
            GrabEnded?.Invoke(o);
            Debug.Log($"[WhatIf] released: {o.InstanceId}");
        }
    }
}
