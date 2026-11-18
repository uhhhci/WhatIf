using UnityEngine;
using UnityEngine.InputSystem;

namespace WhatIf.UI
{
    /// <summary>
    /// Walk around in the Editor without a headset, keyboard only:
    ///   W/S forward/back, A/D sideways, Q/E down/up, arrow keys to look around, hold Shift to go faster.
    /// Pauses while a prompt is being typed and turns itself off as soon as an XR device drives the camera.
    /// </summary>
    public class EditorFlyCamera : MonoBehaviour
    {
        public float speed = 1.5f;        // m/s, roughly walking pace
        public float turnSpeed = 90f;     // degrees per second
        public float fastMultiplier = 3f;

        float _yaw, _pitch;

        void OnEnable()
        {
            var e = transform.eulerAngles;
            _yaw = e.y;
            _pitch = e.x > 180f ? e.x - 360f : e.x;
        }

        void Update()
        {
            if (UnityEngine.XR.XRSettings.isDeviceActive) { enabled = false; return; }
            var kb = Keyboard.current;
            if (kb == null || WhatIfController.IsTyping) return;

            float boost = kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed ? fastMultiplier : 1f;

            // Look: arrow keys
            float yaw = Axis(kb.rightArrowKey, kb.leftArrowKey);
            float pitch = Axis(kb.downArrowKey, kb.upArrowKey);
            _yaw += yaw * turnSpeed * boost * Time.deltaTime;
            _pitch = Mathf.Clamp(_pitch + pitch * turnSpeed * boost * Time.deltaTime, -85f, 85f);
            transform.rotation = Quaternion.Euler(_pitch, _yaw, 0f);

            // Move: on the horizontal plane like a person walking, Q/E for height
            var forward = Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized;
            var right = Vector3.ProjectOnPlane(transform.right, Vector3.up).normalized;
            var move = forward * Axis(kb.wKey, kb.sKey) + right * Axis(kb.dKey, kb.aKey) + Vector3.up * Axis(kb.eKey, kb.qKey);
            transform.position += move * (speed * boost * Time.deltaTime);
        }

        static float Axis(UnityEngine.InputSystem.Controls.KeyControl positive, UnityEngine.InputSystem.Controls.KeyControl negative) =>
            (positive.isPressed ? 1f : 0f) - (negative.isPressed ? 1f : 0f);
    }
}
