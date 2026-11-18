using UnityEngine;

namespace WhatIf.Scene
{
    /// <summary>
    /// Where a camera frame was taken from and how its lens maps directions to pixels (pinhole
    /// model). Used both ways: to draw the detected surfaces' numbers into the frame for the planner,
    /// and to turn a point the planner names in the frame ("the keyboard at 0.62, 0.71") back into a
    /// ray in the room. Image coordinates are 0..1 from the top left, like the planner sees them.
    /// </summary>
    public class CameraView
    {
        /// <summary>Camera to world; Unity convention: +Z looks forward, +Y is up.</summary>
        public Matrix4x4 CameraToWorld;
        // Focal lengths and principal point as fractions of the image width / height.
        public float Fx, Fy, Cx, Cy;
        public string Source; // for the log: "MLCamera" or "eye camera"

        public Vector3 Position => CameraToWorld.GetColumn(3);
        public Vector3 Forward => CameraToWorld.MultiplyVector(Vector3.forward).normalized;

        /// <summary>Image point of a world position, or null if it is behind the camera or outside the frame.</summary>
        public Vector2? Project(Vector3 world)
        {
            var c = CameraToWorld.inverse.MultiplyPoint3x4(world);
            if (c.z <= 0.05f) return null;
            float u = Cx + Fx * c.x / c.z;
            float v = Cy - Fy * c.y / c.z;
            if (u < 0 || u > 1 || v < 0 || v > 1) return null;
            return new Vector2(u, v);
        }

        /// <summary>World ray through an image point (0..1 from the top left).</summary>
        public Ray Unproject(float u, float v)
        {
            var dir = new Vector3((u - Cx) / Fx, -(v - Cy) / Fy, 1f);
            return new Ray(Position, CameraToWorld.MultiplyVector(dir).normalized);
        }

        /// <summary>A pinhole model from a Unity camera (approximation when the real camera's
        /// pose and lens aren't known), for an image of the given aspect ratio (width / height).</summary>
        public static CameraView FromUnityCamera(Camera cam, float imageAspect)
        {
            float tanV = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            float tanH = tanV * imageAspect;
            return new CameraView
            {
                CameraToWorld = Matrix4x4.TRS(cam.transform.position, cam.transform.rotation, Vector3.one),
                Fx = 0.5f / tanH, Fy = 0.5f / tanV, Cx = 0.5f, Cy = 0.5f,
                Source = "eye camera",
            };
        }

        public override string ToString() =>
            $"{Source}: fx={Fx:F2} fy={Fy:F2} c=({Cx:F2},{Cy:F2}) at {Position}";
    }
}
