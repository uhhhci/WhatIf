using System;
using UnityEngine;

namespace WhatIf.Content
{
    /// <summary>
    /// A let-go object falls with Unity physics instead of snapping onto what is below it: a temporary
    /// Rigidbody with gravity and a box collider around what it shows, landing on the room mesh (which
    /// has colliders), tumbling over edges, thrown with the hand's speed. Once it lies still (or after
    /// a few seconds) both are removed again and `Settled` gets the final pose. It never falls through
    /// the real floor: below it, it is set onto the floor.
    /// Other generated objects have no colliders, so it falls past them onto the real room.
    /// </summary>
    public class Falling : MonoBehaviour
    {
        public float FloorY;
        public Action<GeneratedObject> Settled;

        Rigidbody _rb;
        BoxCollider _box;
        float _age, _still;
        bool _done;

        const float MaxAgeS = 5f, StillS = 0.4f, MaxThrow = 6f;

        public static void Drop(GeneratedObject o, Vector3 velocity, float floorY, Action<GeneratedObject> settled)
        {
            if (o == null) return;
            var old = o.GetComponent<Falling>();
            if (old != null) old.Stop(); // grabbed again while falling
            var f = o.gameObject.AddComponent<Falling>();
            f.FloorY = floorY;
            f.Settled = settled;
            f.Begin(o, Vector3.ClampMagnitude(velocity, MaxThrow));
        }

        void Begin(GeneratedObject o, Vector3 velocity)
        {
            // the box in the object's own axes: measure with the root unrotated, then restore
            var rot = transform.rotation;
            transform.rotation = Quaternion.identity;
            var b = o.WorldBounds();
            transform.rotation = rot;
            var s = transform.lossyScale;
            _box = gameObject.AddComponent<BoxCollider>();
            var centre = b.center - transform.position;
            _box.center = new Vector3(centre.x / s.x, centre.y / s.y, centre.z / s.z);
            _box.size = new Vector3(Mathf.Max(0.02f, b.size.x / Mathf.Abs(s.x)), Mathf.Max(0.02f, b.size.y / Mathf.Abs(s.y)),
                                    Mathf.Max(0.02f, b.size.z / Mathf.Abs(s.z)));
            _rb = gameObject.AddComponent<Rigidbody>();
            _rb.mass = 1f;
            _rb.interpolation = RigidbodyInterpolation.Interpolate;
            _rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic; // thin room mesh: no tunnelling
            _rb.velocity = velocity;
        }

        void FixedUpdate()
        {
            if (_done || _rb == null) return;
            _age += Time.fixedDeltaTime;
            bool still = _rb.velocity.sqrMagnitude < 0.0025f && _rb.angularVelocity.sqrMagnitude < 0.01f;
            _still = still ? _still + Time.fixedDeltaTime : 0f;
            if (transform.position.y < FloorY - 0.05f)
            {
                // through a hole in the room mesh: onto the real floor
                var p = transform.position;
                transform.position = new Vector3(p.x, FloorY, p.z);
                Finish();
                return;
            }
            if (_still > StillS || _age > MaxAgeS) Finish();
        }

        void Finish()
        {
            if (_done) return;
            _done = true;
            Stop();
            Settled?.Invoke(GetComponent<GeneratedObject>());
        }

        /// <summary>Removes the physics again (also when the object is grabbed while falling).</summary>
        public void Stop()
        {
            _done = true;
            if (_rb != null) Destroy(_rb);
            if (_box != null) Destroy(_box);
            Destroy(this);
        }
    }
}
