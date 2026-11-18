using System;
using System.Collections.Generic;
using UnityEngine;
using WhatIf.Api;

namespace WhatIf.Content
{
    /// <summary>
    /// Runs a behaviour the planner wrote for an object: a few states made of building blocks
    /// (where to go, how to move, movement in place) and events that switch between them (looked at,
    /// user near, arrived, timer). Knows nothing about dogs or cars - only the blocks.
    ///
    /// Moving objects were generated in side view facing right, so their front is the model's +X
    /// (yaw offset <see cref="forwardYawDeg"/>). Movement stays on the floor (or at flying height),
    /// within a few metres of where it started, and turns away from walls (room mesh raycasts).
    /// </summary>
    public class BehaviourRunner : MonoBehaviour
    {
        public ObjectBehaviour behaviour;
        public Transform head;
        public float floorY;
        /// <summary>The room's floor and a rough ceiling: flyers stay between them (not far up in the sky).</summary>
        public float roomFloorY, ceilingY = float.PositiveInfinity;
        public LayerMask meshLayers = ~0;
        // Moving things are generated facing right; the cut-out's right becomes the model's -X in Unity
        // (the glTF loader mirrors X), so the front is -X: a yaw offset of +90 (with -90 they moved
        // tail first - seen with fish and birds).
        public float forwardYawDeg = 90f;
        /// <summary>Turn to keep facing the user (idle "look at user", after coming to the user). Off:
        /// animals don't follow the user with their body (it looked like every dog tracked you).</summary>
        public bool faceUser = false;
        /// <summary>True while the user looks at this object (from the gaze tracker).</summary>
        public Func<GeneratedObject, bool> IsLookedAt;
        /// <summary>Position of another item (to_target), null if unknown.</summary>
        public Func<string, Vector3?> PositionOf;

        /// <summary>Walking / driving / flying right now (moving parts animate faster).</summary>
        public bool IsMoving => _state != null && _state.Move != "none" && !_arrived;

        GeneratedObject _obj;
        BehaviourState _state;
        float _stateTime, _phase;
        Vector3 _home, _goal;
        bool _hasGoal, _arrived;
        float _yaw;
        Vector3 _baseScale;
        Transform _visual; // the child that idle motions animate (so the root keeps its pose)
        float _headHeight = 1.7f; // the user's eye height above the floor when this appeared
        Quaternion _pose = Quaternion.identity; // laid down ("on its back"), kept while it turns

        /// <summary>Laid down by an edit: turning toward the user keeps this pose.</summary>
        public void Lay(Quaternion worldTurn)
        {
            _pose = Quaternion.Inverse(Quaternion.Euler(0f, _yaw, 0f)) * worldTurn * Quaternion.Euler(0f, _yaw, 0f) * _pose;
        }

        void Start()
        {
            _obj = GetComponent<GeneratedObject>();
            _home = transform.position;
            _yaw = transform.eulerAngles.y;
            _baseScale = transform.localScale;
            _phase = UnityEngine.Random.value * 10f;
            _headHeight = head != null ? Mathf.Clamp(head.position.y - roomFloorY, 1.0f, 2.0f) : 1.7f;
            Enter(behaviour != null && behaviour.States.Count > 0 ? behaviour.States[0] : null);
        }

        /// <summary>A new behaviour (edits: "let the dog sit").</summary>
        public void Replace(ObjectBehaviour b)
        {
            behaviour = b;
            Enter(b != null && b.States.Count > 0 ? b.States[0] : null);
        }

        const float MinStateS = 2f;

        void Enter(BehaviourState s)
        {
            if (s != null) Debug.Log($"[WhatIf] behaviour {name}: {s.Name} (move {s.Move}, idle {string.Join(",", s.Idle)})");
            _state = s;
            _stateTime = 0f;
            _hasGoal = false;
            _arrived = false;
        }

        void Update()
        {
            if (_state == null || head == null) return;
            float dt = Time.deltaTime;
            _stateTime += dt;
            Move(dt);
            Idle();
            foreach (var t in _state.Next)
            {
                // a state lasts at least 2 s before an event ends it: "user looks" -> come -> (nothing to
                // walk) arrived -> idle -> user still looks ... flipped every frame (seen: a cat turning
                // to the user non-stop, and the log flooded)
                if (t.Event != "timer" && _stateTime < MinStateS) continue;
                bool fire = t.Event switch
                {
                    "user_looks" => IsLookedAt != null && IsLookedAt(_obj),
                    "user_near" => Flat(head.position - transform.position).magnitude < Mathf.Max(0.3f, t.Value),
                    "arrived" => _arrived,
                    "timer" => _stateTime > Mathf.Max(0.5f, t.Value),
                    _ => false,
                };
                if (!fire) continue;
                var next = behaviour.States.Find(st => st.Name == t.Goto);
                if (next != null && next != _state) { Enter(next); break; }
            }
        }

        // ------------------------------------------------------------------ where to go

        void Move(float dt)
        {
            if (_state.Move == "none") { _arrived = true; return; }
            var pos = transform.position;
            var user = new Vector3(head.position.x, pos.y, head.position.z);
            Vector3 goal;
            float stopAt = 0.1f;
            switch (_state.Move)
            {
                case "to_user": goal = user; stopAt = 0.8f; break;
                case "follow_user": goal = user; stopAt = 1.2f; break;
                case "flee_user":
                    goal = pos + Flat(pos - user).normalized * 2f; stopAt = 0f;
                    if (Flat(pos - user).magnitude > 3.5f) { _arrived = true; return; }
                    break;
                case "circle":
                {
                    float a = (_stateTime * _state.Speed / 1.2f) + _phase;
                    goal = _home + new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a)) * 1.2f; stopAt = 0f;
                    break;
                }
                case "to_target":
                    goal = PositionOf?.Invoke(_state.TargetId) ?? _home; stopAt = 0.7f; break;
                default: // wander: random free spots near home
                    if (!_hasGoal || Flat(_goal - pos).magnitude < 0.2f)
                    {
                        if (_hasGoal) { _arrived = true; }
                        _goal = PickSpot(pos);
                        _hasGoal = true;
                    }
                    goal = _goal; stopAt = 0.15f;
                    break;
            }
            // never into the user: at least half the body plus 0.5 m from the head (seen: a dolphin
            // swimming "to its target" ended with the user's head inside it)
            float keep = KeepFromUser();
            if (_state.Move == "to_user" || _state.Move == "follow_user") stopAt = Mathf.Max(stopAt, keep);
            var to = Flat(goal - pos);
            if (to.magnitude <= stopAt)
            {
                _arrived = true;
                // turning to the user is for coming to them; elsewhere it kept turning with every step
                // the user took (seen: a dolphin)
                if (faceUser && (_state.Move == "to_user" || _state.Move == "follow_user")) Face(Flat(user - pos), dt, 2f);
                return;
            }
            if (_state.Move != "wander") _arrived = false;
            var dir = to.normalized;
            // turn away from walls and real obstacles ahead (room mesh)
            if (Physics.Raycast(pos + Vector3.up * 0.3f, dir, 0.6f, meshLayers))
            {
                _goal = PickSpot(pos); _hasGoal = true;
                return;
            }
            // Turn towards the goal and move the way the body faces, slowing down while the goal is
            // still to the side and standing (turning on the spot) while it is behind: moving
            // straight at a new goal while still turning looked like swimming or driving backwards.
            Face(dir, dt, _state.Gait == "roll" ? 2.5f : 5f);
            float heading = (_yaw - forwardYawDeg) * Mathf.Deg2Rad;
            var facing = new Vector3(Mathf.Sin(heading), 0f, Mathf.Cos(heading));
            float along = Mathf.Clamp01(Vector3.Dot(facing, dir));
            float step = Mathf.Min(to.magnitude - stopAt, Speed() * dt) * along * along;
            var next = pos + facing * step;
            next.y = BaseHeight();
            if (Flat(next - user).magnitude < keep && Flat(next - user).magnitude < Flat(pos - user).magnitude)
            {
                _arrived = true; // would come too close: stays where it is
                return;
            }
            transform.position = next;
        }

        /// <summary>How close the object may come to the user's head (horizontally).</summary>
        float KeepFromUser()
        {
            float size = _obj != null ? Mathf.Max(_obj.Height * transform.lossyScale.y, _obj.Footprint * transform.lossyScale.x) : 0.5f;
            return size * 0.5f + 0.5f;
        }

        Vector3 PickSpot(Vector3 from)
        {
            for (int i = 0; i < 12; i++)
            {
                var a = UnityEngine.Random.value * Mathf.PI * 2f;
                var cand = _home + new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a)) * UnityEngine.Random.Range(0.5f, 3f);
                var d = Flat(cand - from);
                if (!Physics.Raycast(from + Vector3.up * 0.3f, d.normalized, d.magnitude + 0.3f, meshLayers)) return cand;
            }
            return _home;
        }

        /// <summary>The planner's speed, but believable for the object's size and kind of movement
        /// (fish darted through the room).</summary>
        float Speed()
        {
            float size = _obj != null ? Mathf.Max(0.05f, _obj.Height * transform.lossyScale.y) : 0.5f;
            float cap = _state.Gait switch
            {
                "swim" => Mathf.Clamp(size * 0.8f, 0.08f, 0.3f),
                "fly" => Mathf.Clamp(size * 2f, 0.3f, 0.9f),
                "roll" => 1.5f,
                "slither" => Mathf.Clamp(size * 0.6f, 0.08f, 0.35f),
                _ => Mathf.Clamp(size * 1.2f, 0.15f, 1.2f),
            };
            return Mathf.Min(_state.Speed, cap);
        }

        /// <summary>Flyers fly above the user's head (measured when they appear: standing or sitting),
        /// at least 2 m above the floor, and below the rough ceiling.</summary>
        float BaseHeight() => _state.Gait == "fly"
            ? Mathf.Min(roomFloorY + Mathf.Max(2.0f, _headHeight + 0.4f) + 0.15f * Mathf.Sin(Time.time * 0.7f + _phase),
                        ceilingY - 0.15f - (_obj != null ? _obj.Height * transform.lossyScale.y : 0.2f))
            : _state.Gait == "swim" ? SwimHeight() : floorY;

        static readonly System.Text.RegularExpressions.Regex SurfaceSwimmer = new System.Text.RegularExpressions.Regex(
            @"(dolphins?|whales?|orcas?|ducks?|ducklings?|swans?|geese|goose|seals?|otters?|turtles?|beavers?|" +
            @"pelicans?|flamingos?|hippos?|delfin(e)?|wal(e)?|enten?|schw(a|ä)ne?|robben?)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        /// <summary>While there is water: dolphins, whales, ducks and the like at the surface (about 40 % of
        /// the body under it - a dolphin swam at head height where it was placed); fish and everything else
        /// fully under water, at their height between the floor and the surface. Without water: the
        /// height where it was placed.</summary>
        float SwimHeight()
        {
            var water = EnvironmentEffects.WaterY;
            if (!water.HasValue) return transform.position.y;
            float size = _obj != null ? _obj.Height * transform.lossyScale.y : 0.3f;
            string what = _obj != null ? $"{_obj.Label} {_obj.AssetPrompt}" : name;
            if (SurfaceSwimmer.IsMatch(what)) return Mathf.Max(floorY, water.Value - size * 0.4f);
            float lo = floorY + 0.03f, hi = water.Value - size - 0.03f;
            if (hi < lo) return lo; // shallow water: on the ground under it
            return Mathf.Clamp(transform.position.y, lo, hi);
        }

        void Face(Vector3 dir, float dt, float turnSpeed)
        {
            if (dir.sqrMagnitude < 1e-4f) return;
            float want = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg + forwardYawDeg;
            float before = _yaw;
            _yaw = Mathf.LerpAngle(_yaw, want, Mathf.Clamp01(dt * turnSpeed));
            float bank = (_state.Gait == "roll" || _state.Gait == "fly") ? Mathf.Clamp(Mathf.DeltaAngle(before, _yaw) / Mathf.Max(dt, 1e-3f) * -0.15f, -20f, 20f) : 0f;
            transform.rotation = Quaternion.Euler(0f, _yaw, 0f) * Quaternion.Euler(bank, 0f, 0f) * _pose;
        }

        // ------------------------------------------------------------------ movement in place

        void Idle()
        {
            float t = Time.time + _phase;
            bool moving = _state.Move != "none" && !_arrived;
            var offset = Vector3.zero;
            var rot = Quaternion.identity;
            var scale = Vector3.one;
            // gaits
            if (moving)
            {
                float f = _state.Speed * 6f;
                switch (_state.Gait)
                {
                    case "hop": offset.y += Mathf.Abs(Mathf.Sin(t * f * 0.8f)) * 0.12f; break;
                    case "trot": offset.y += Mathf.Abs(Mathf.Sin(t * f * 1.6f)) * 0.03f; break;
                    case "waddle": rot *= Quaternion.Euler(0, 0, Mathf.Sin(t * f) * 8f); break;
                    case "slither": rot *= Quaternion.Euler(0, Mathf.Sin(t * f) * 15f, 0); break;
                    case "swim": offset.y += Mathf.Sin(t * 1.3f) * 0.05f; break;
                }
            }
            foreach (var m in _state.Idle)
            {
                switch (m)
                {
                    case "breathe":
                    {
                        // at most ~1.5 cm up and down, whatever the size (2.5 % was 10 cm on a giraffe)
                        float size = _obj != null ? Mathf.Max(0.05f, _obj.Height * transform.lossyScale.y) : 0.5f;
                        float amount = Mathf.Min(0.025f, 0.015f / size);
                        scale.y *= 1f + Mathf.Sin(t * 1.6f) * amount;
                        break;
                    }
                    case "sway": rot *= Quaternion.Euler(Mathf.Sin(t * 0.9f) * 1.5f, 0, Mathf.Sin(t * 0.7f) * 1.5f); break;
                    case "bob": offset.y += Mathf.Sin(t * 1.2f) * 0.04f; break;
                    case "spin": rot *= Quaternion.Euler(0, (t * 60f) % 360f, 0); break;
                    case "tilt": rot *= Quaternion.Euler(0, 0, Mathf.Sin(t * 0.5f) * 4f); break;
                    case "look_at_user":
                        if (!moving && faceUser) Face(Flat(head.position - transform.position), Time.deltaTime, 1.5f);
                        break;
                }
            }
            var visual = Visual();
            if (visual == null) return;
            visual.localPosition = offset;
            visual.localRotation = rot;
            visual.localScale = scale;
        }

        /// <summary>Idle motions move a pivot between the root and what is shown, so the root's pose
        /// (placement, anchors, edits) stays untouched.</summary>
        Transform Visual()
        {
            if (_visual != null) return _visual;
            var pivot = new GameObject("behaviour pivot").transform;
            pivot.SetParent(transform, false);
            var children = new List<Transform>();
            foreach (Transform c in transform) if (c != pivot && c.name != "contact shadow") children.Add(c);
            foreach (var c in children) c.SetParent(pivot, true);
            _visual = pivot;
            return _visual;
        }

        /// <summary>Content added later (the 3D model replacing the cut-out) goes under the pivot too.</summary>
        void LateUpdate()
        {
            if (_visual == null) return;
            foreach (Transform c in transform)
                if (c != _visual && c.name != "contact shadow" && c.name != "behaviour pivot")
                {
                    c.SetParent(_visual, true);
                    break; // one per frame is enough
                }
        }

        static Vector3 Flat(Vector3 v) { v.y = 0f; return v; }
    }
}
