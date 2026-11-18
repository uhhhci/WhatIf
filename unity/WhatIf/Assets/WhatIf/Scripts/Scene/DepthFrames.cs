using System;
using UnityEngine;

namespace WhatIf.Scene
{
    /// <summary>One depth image (radial distance in metres per pixel) with the pose and lens it was taken with.</summary>
    public class DepthSnapshot
    {
        public float[] Depth;
        public int Width, Height;
        public float Fx, Fy, Cx, Cy;            // pixels
        public Matrix4x4 CameraToWorld;         // Unity convention: +Z forward, +Y up

        /// <summary>Measured radial distance where a world point appears in the image (0: outside / no value).</summary>
        public float MeasuredAt(Vector3 world, out float pointDistance)
        {
            var c = CameraToWorld.inverse.MultiplyPoint3x4(world);
            pointDistance = c.magnitude;
            if (c.z <= 0.05f) return 0f;
            int u = Mathf.RoundToInt(Cx + Fx * c.x / c.z);
            int v = Mathf.RoundToInt(Cy - Fy * c.y / c.z);
            if (u < 0 || v < 0 || u >= Width || v >= Height) return 0f;
            float d = Depth[v * Width + u];
            return float.IsNaN(d) || d < 0.1f || d > 7f ? 0f : d;
        }

        /// <summary>
        /// Where a ray (e.g. from the colour camera through a point the planner chose) first meets a
        /// real surface in this depth image, and the top of the thing there: from the hit straight up
        /// until the depth jumps behind it (the top of a head, of a keyboard). False if nothing was hit.
        /// </summary>
        public bool Resolve(Ray ray, out Vector3 hit, out Vector3 top)
        {
            top = default;
            if (!FirstHit(ray, out hit)) return false;
            return TopFrom(hit, out top);
        }

        /// <summary>Where a ray first meets a real surface in this depth image.</summary>
        public bool FirstHit(Ray ray, out Vector3 hit)
        {
            hit = default;
            for (float d = 0.25f; d < 5f; d += 0.02f)
            {
                var p = ray.GetPoint(d);
                float measured = MeasuredAt(p, out float r);
                if (measured <= 0f || r < measured - 0.02f) continue;
                hit = p;
                return true;
            }
            return false;
        }

        /// <summary>
        /// The top of the thing at a point: straight up until the depth jumps behind it. False when it
        /// never jumps within 0.6 m - a wall or a tall cupboard front, nothing to stand on.
        /// </summary>
        public bool TopFrom(Vector3 p, out Vector3 top)
        {
            top = p;
            bool jumped = false;
            for (float h = 0.01f; h < 0.6f; h += 0.01f)
            {
                var q = p + Vector3.up * h;
                float m = MeasuredAt(q, out float rq);
                if (m <= 0f || m > rq + 0.15f) { jumped = true; break; } // depth jumps behind: above the thing
                top = q;
            }
            top += Vector3.up * 0.02f; // dark hair often reads too low
            return jumped;
        }
    }

    /// <summary>
    /// Magic Leap 2 depth camera (long range, up to ~5 m, 5 fps): the latest depth image, so things the
    /// planner points at in the camera picture are found in 3D even where the room mesh has nothing
    /// yet (a person who just walked in). Used for placement only. Starts once the DEPTH_CAMERA
    /// permission is granted; does nothing in the Editor.
    /// </summary>
    public class DepthFrames : MonoBehaviour
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        bool _connected;
        float _nextTry;

        void Update()
        {
            if (_connected || Time.unscaledTime < _nextTry) return;
            _nextTry = Time.unscaledTime + 2f;
            if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission("com.magicleap.permission.DEPTH_CAMERA")) return;
            try
            {
                var long_ = new UnityEngine.XR.MagicLeap.MLDepthCamera.StreamConfig
                {
                    Flags = (uint)UnityEngine.XR.MagicLeap.MLDepthCamera.CaptureFlags.DepthImage,
                    Exposure = 1600,
                    FrameRateConfig = UnityEngine.XR.MagicLeap.MLDepthCamera.FrameRate.FPS_5,
                };
                var short_ = new UnityEngine.XR.MagicLeap.MLDepthCamera.StreamConfig
                {
                    Flags = (uint)UnityEngine.XR.MagicLeap.MLDepthCamera.CaptureFlags.DepthImage,
                    Exposure = 375,
                    FrameRateConfig = UnityEngine.XR.MagicLeap.MLDepthCamera.FrameRate.FPS_5,
                };
                UnityEngine.XR.MagicLeap.MLDepthCamera.SetSettings(new UnityEngine.XR.MagicLeap.MLDepthCamera.Settings
                {
                    Streams = UnityEngine.XR.MagicLeap.MLDepthCamera.Stream.LongRange,
                    StreamConfig = new[] { long_, short_ },
                });
                var r = UnityEngine.XR.MagicLeap.MLDepthCamera.Connect();
                _connected = r.IsOk;
                Debug.Log("[WhatIf] depth camera: " + (r.IsOk ? "connected (long range, 5 fps)" : "connect failed: " + r));
                if (!r.IsOk) _nextTry = Time.unscaledTime + 30f;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[WhatIf] depth camera: " + e.Message);
                _nextTry = Time.unscaledTime + 60f;
            }
        }

        /// <summary>The latest depth image, or null (not connected, no frame within 300 ms).</summary>
        public DepthSnapshot Latest()
        {
            if (!_connected) return null;
            try
            {
                var r = UnityEngine.XR.MagicLeap.MLDepthCamera.GetLatestDepthData(300, out var data);
                if (!r.IsOk || data == null || !data.DepthImage.HasValue) return null;
                var img = data.DepthImage.Value;
                int w = (int)img.Width, h = (int)img.Height;
                var depth = new float[w * h];
                int stride = (int)Math.Max(img.Stride, (uint)(w * 4));
                for (int y = 0; y < h; y++) Buffer.BlockCopy(img.Data, y * stride, depth, y * w * 4, w * 4);
                var k = data.Intrinsics;
                // the pose is in tracking space: the XR camera's parent maps it to the world
                var cam = Camera.main;
                var space = cam != null ? cam.transform.parent : null;
                var pose = Matrix4x4.TRS(data.Position, data.Rotation, Vector3.one);
                var world = space != null ? space.localToWorldMatrix * pose : pose;
                // axis convention of the native pose: pick the variant that looks the way the head looks
                if (cam != null)
                {
                    Matrix4x4 best = world; float bestScore = float.NegativeInfinity;
                    foreach (var fix in new[] { Quaternion.identity, Quaternion.Euler(0, 180, 0), Quaternion.Euler(180, 0, 0), Quaternion.Euler(0, 0, 180) })
                    {
                        var m = world * Matrix4x4.Rotate(fix);
                        float s = Vector3.Dot(m.MultiplyVector(Vector3.forward).normalized, cam.transform.forward)
                                  + Vector3.Dot(m.MultiplyVector(Vector3.up).normalized, cam.transform.up);
                        if (s > bestScore) { bestScore = s; best = m; }
                    }
                    if (bestScore < 1.2f) { Debug.LogWarning($"[WhatIf] depth camera: pose doesn't match the head ({bestScore:F2})"); return null; }
                    world = best;
                }
                return new DepthSnapshot
                {
                    Depth = depth, Width = w, Height = h,
                    Fx = k.FocalLength.x, Fy = k.FocalLength.y, Cx = k.PrincipalPoint.x, Cy = k.PrincipalPoint.y,
                    CameraToWorld = world,
                };
            }
            catch (Exception e)
            {
                Debug.LogWarning("[WhatIf] depth camera: " + e.Message);
                return null;
            }
        }

        void OnDestroy()
        {
            if (_connected) UnityEngine.XR.MagicLeap.MLDepthCamera.Disconnect();
        }
#else
        public DepthSnapshot Latest() => null;
#endif
    }
}
