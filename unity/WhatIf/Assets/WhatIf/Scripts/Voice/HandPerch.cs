using UnityEngine;
using UnityEngine.InputSystem;

namespace WhatIf.Voice
{
    /// <summary>
    /// Keeps an object on the user's hand ("a bird on my hand"): follows the palm of whichever hand
    /// is tracked (the one nearer to it when both are), slightly above it, smoothed. While no hand is
    /// tracked it stays where it was. Uses OpenXR's Hand Interaction profile (grip pose = palm).
    /// </summary>
    public class HandPerch : MonoBehaviour
    {
        public Transform head;
        public float aboveHandM = 0.04f;
        public float followSpeed = 12f;

        InputAction _leftPos, _rightPos, _leftTracked, _rightTracked;

        void Awake()
        {
            _leftPos = new InputAction("palm L", binding: "<HandInteraction>{LeftHand}/devicePosition");
            _rightPos = new InputAction("palm R", binding: "<HandInteraction>{RightHand}/devicePosition");
            _leftTracked = new InputAction("tracked L", binding: "<HandInteraction>{LeftHand}/isTracked");
            _rightTracked = new InputAction("tracked R", binding: "<HandInteraction>{RightHand}/isTracked");
        }

        void OnEnable() { _leftPos.Enable(); _rightPos.Enable(); _leftTracked.Enable(); _rightTracked.Enable(); }
        void OnDisable() { _leftPos.Disable(); _rightPos.Disable(); _leftTracked.Disable(); _rightTracked.Disable(); }
        void OnDestroy() { _leftPos.Dispose(); _rightPos.Dispose(); _leftTracked.Dispose(); _rightTracked.Dispose(); }

        Vector3 World(InputAction pos)
        {
            var p = pos.ReadValue<Vector3>();
            var space = head != null ? head.parent : null; // device poses are in tracking space
            return space != null ? space.TransformPoint(p) : p;
        }

        static bool Tracked(InputAction a) => a.ReadValue<float>() > 0.5f;

        void LateUpdate()
        {
            bool l = Tracked(_leftTracked), r = Tracked(_rightTracked);
            if (!l && !r) return;
            Vector3 target;
            if (l && r)
            {
                var a = World(_leftPos); var b = World(_rightPos);
                target = (a - transform.position).sqrMagnitude < (b - transform.position).sqrMagnitude ? a : b;
            }
            else target = World(l ? _leftPos : _rightPos);
            if (target.sqrMagnitude < 1e-6f) return; // no pose yet
            target += Vector3.up * aboveHandM;
            transform.position = Vector3.Lerp(transform.position, target, Mathf.Clamp01(Time.deltaTime * followSpeed));
        }
    }
}
