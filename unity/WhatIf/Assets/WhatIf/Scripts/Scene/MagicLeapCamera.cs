#if UNITY_ANDROID && !UNITY_EDITOR
using System;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.XR.MagicLeap;

#pragma warning disable 618 // MLCamera is marked deprecated in favour of the Android camera APIs, but it is what works with Unity

namespace WhatIf.Scene
{
    /// <summary>
    /// Still images from the Magic Leap 2 RGB camera through MLCamera (real world only, no virtual
    /// content). Unity's WebCamTexture finds no usable camera on the headset, so this is the
    /// headset path of <see cref="WebCamFrameSource"/>. Connects on first use and stays connected.
    /// </summary>
    public class MagicLeapCamera
    {
        readonly int _wantWidth;
        MLCamera _camera;
        bool _prepared;
        TaskCompletionSource<(byte[] jpg, MLCamera.ResultExtras extras)> _pending;

        public MagicLeapCamera(int wantWidth) { _wantWidth = wantWidth; }

        /// <summary>Where the last image was taken from and its lens (null if unknown).</summary>
        public CameraView LastView { get; private set; }

        /// <summary>A JPEG straight from the camera, or null (reason in the log).</summary>
        public async Task<byte[]> CaptureJpeg(float timeoutS)
        {
            LastView = null;
            if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Camera))
            {
                Debug.LogWarning("[WhatIf] camera: camera permission not granted");
                return null;
            }
            if (_camera == null)
            {
                var ctx = MLCamera.ConnectContext.Create();
                ctx.CamId = MLCamera.Identifier.Main;
                ctx.Flags = MLCamera.ConnectFlag.CamOnly;
                ctx.EnableVideoStabilization = false;
                var connect = MLCamera.CreateAndConnectAsync(ctx);
                if (await Task.WhenAny(connect, Task.Delay(TimeSpan.FromSeconds(timeoutS))) != connect || connect.Result == null)
                {
                    Debug.LogWarning("[WhatIf] camera: MLCamera connect failed (is another app using the camera?)");
                    return null;
                }
                _camera = connect.Result;
                _camera.OnRawImageAvailable += OnImage;
                Debug.Log("[WhatIf] camera: MLCamera connected");
            }
            if (!_prepared && !Prepare()) return null;

            _pending = new TaskCompletionSource<(byte[], MLCamera.ResultExtras)>(TaskCreationOptions.RunContinuationsAsynchronously);
            // the async versions: the blocking ones held the main thread while the camera adjusted
            // exposure and took the picture (a visible hiccup after every sentence)
            await _camera.PreCaptureAEAWBAsync();
            var r = await _camera.CaptureImageAsync(1);
            if (!r.IsOk)
            {
                Debug.LogWarning("[WhatIf] camera: CaptureImage failed: " + r);
                _prepared = false; // prepare again next time
                return null;
            }
            var done = await Task.WhenAny(_pending.Task, Task.Delay(TimeSpan.FromSeconds(timeoutS)));
            if (done != _pending.Task)
            {
                Debug.LogWarning($"[WhatIf] camera: no image after {timeoutS:0} s");
                _prepared = false;
                return null;
            }
            var (jpg, extras) = _pending.Task.Result;
            if (jpg != null) LastView = BuildView(extras);
            return jpg;
        }

        /// <summary>
        /// The camera's pose at the moment of the image (MLCVCamera, from the frame's timestamp) and
        /// its lens (intrinsics), so image points can be turned into rays in the room.
        /// </summary>
        static CameraView BuildView(MLCamera.ResultExtras extras)
        {
            try
            {
                if (!extras.Intrinsics.HasValue) { Debug.LogWarning("[WhatIf] camera: no intrinsics"); return null; }
                var k = extras.Intrinsics.Value;
                var r = MLCVCamera.GetFramePose(extras.VCamTimestamp, out Matrix4x4 pose);
                if (!r.IsOk) { Debug.LogWarning("[WhatIf] camera: no frame pose: " + r); return null; }

                // The pose is in tracking space: the XR camera's parent (Camera Offset) maps it to world.
                var cam = Camera.main;
                var space = cam != null ? cam.transform.parent : null;
                var world = space != null ? space.localToWorldMatrix * pose : pose;

                // The axis convention of the native pose isn't documented for Unity: pick the variant
                // that looks the way the head looks (the RGB camera sits in the headset's front).
                Matrix4x4 best = world;
                if (cam != null)
                {
                    float bestScore = float.NegativeInfinity;
                    foreach (var fix in new[] { Quaternion.identity, Quaternion.Euler(0, 180, 0), Quaternion.Euler(180, 0, 0), Quaternion.Euler(0, 0, 180) })
                    {
                        var m = world * Matrix4x4.Rotate(fix);
                        float score = Vector3.Dot(m.MultiplyVector(Vector3.forward).normalized, cam.transform.forward)
                                      + Vector3.Dot(m.MultiplyVector(Vector3.up).normalized, cam.transform.up);
                        if (score > bestScore) { bestScore = score; best = m; }
                    }
                    if (bestScore < 1.5f) // more than ~40 degrees off: rather use the eye camera
                    {
                        Debug.LogWarning($"[WhatIf] camera: frame pose doesn't match the head (score {bestScore:F2}), using the eye camera");
                        return null;
                    }
                }
                var view = new CameraView
                {
                    CameraToWorld = best,
                    Fx = k.FocalLength.x / k.Width, Fy = k.FocalLength.y / k.Height,
                    Cx = k.PrincipalPoint.x / k.Width, Cy = k.PrincipalPoint.y / k.Height,
                    Source = "MLCamera",
                };
                Debug.Log("[WhatIf] camera view: " + view);
                return view;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[WhatIf] camera: frame pose failed: " + e.Message);
                return null;
            }
        }

        bool Prepare()
        {
            var caps = MLCamera.GetImageStreamCapabilitiesForCamera(_camera, MLCamera.CaptureType.Image);
            if (caps == null || caps.Length == 0)
            {
                Debug.LogWarning("[WhatIf] camera: no image stream capabilities");
                return false;
            }
            var best = caps[0]; // the size closest to the wanted width
            foreach (var c in caps)
                if (Mathf.Abs(c.Width - _wantWidth) < Mathf.Abs(best.Width - _wantWidth)) best = c;
            var config = new MLCamera.CaptureConfig
            {
                CaptureFrameRate = MLCamera.CaptureFrameRate.None,
                StreamConfigs = new[] { MLCamera.CaptureStreamConfig.Create(best, MLCamera.OutputFormat.JPEG) },
            };
            var r = _camera.PrepareCapture(config, out _);
            if (!r.IsOk)
            {
                Debug.LogWarning("[WhatIf] camera: PrepareCapture failed: " + r);
                return false;
            }
            Debug.Log($"[WhatIf] camera: capturing {best.Width}x{best.Height} JPEG");
            _prepared = true;
            return true;
        }

        void OnImage(MLCamera.CameraOutput output, MLCamera.ResultExtras extras, MLCamera.Metadata metadata)
        {
            if (_pending == null || output.Planes == null || output.Planes.Length == 0) return;
            var data = output.Planes[0].Data;
            if (output.Format != MLCamera.OutputFormat.JPEG || data == null)
            {
                _pending.TrySetResult((null, extras));
                return;
            }
            var copy = new byte[data.Length]; // the SDK may reuse its buffer
            Buffer.BlockCopy(data, 0, copy, 0, data.Length);
            _pending.TrySetResult((copy, extras));
        }

        public void Disconnect()
        {
            if (_camera == null) return;
            _camera.OnRawImageAvailable -= OnImage;
            _camera.Disconnect();
            _camera = null;
            _prepared = false;
        }
    }
}
#endif
