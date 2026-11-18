using System;
using System.Threading.Tasks;
using UnityEngine;

namespace WhatIf.Scene
{
    /// <summary>
    /// Captures through Unity's WebCamTexture (Android Camera2 on the headset, the webcam in the
    /// Editor). Magic Leap 2 exposes its RGB camera through the standard Android camera API, which
    /// replaces the deprecated MLCamera.
    ///
    /// The camera needs a few seconds to deliver its first frame, so it is started on the first
    /// request (waiting up to <see cref="firstFrameTimeoutS"/>) and kept running for
    /// <see cref="keepRunningS"/> after the last capture, so follow-up prompts get a frame at once.
    /// Every step is logged with "[WhatIf] camera:" so failures show up in the headset log.
    /// The planner also works without an image (surfaces + prompt only).
    ///
    /// In its own file on purpose: a player build only finds a MonoBehaviour whose file is named
    /// like the class (otherwise the scene fails to load on the headset).
    /// </summary>
    public class WebCamFrameSource : CameraFrameSource
    {
        public int requestedWidth = 1280;
        public int requestedHeight = 720;
        public float firstFrameTimeoutS = 6f;
        public float keepRunningS = 90f;

        WebCamTexture _cam;
        float _lastUse;
        bool _loggedDevices;

#if UNITY_ANDROID && !UNITY_EDITOR
        MagicLeapCamera _ml;
        bool _mlFailed;
#endif

        /// <summary>Width and height from a JPEG's frame header (no decoding).</summary>
        static bool JpegSize(byte[] jpg, out int width, out int height)
        {
            width = height = 0;
            int i = 2;
            while (i + 9 < jpg.Length)
            {
                if (jpg[i] != 0xFF) { i++; continue; }
                byte marker = jpg[i + 1];
                if (marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
                {
                    height = (jpg[i + 5] << 8) | jpg[i + 6];
                    width = (jpg[i + 7] << 8) | jpg[i + 8];
                    return width > 0 && height > 0;
                }
                int len = (jpg[i + 2] << 8) | jpg[i + 3];
                i += 2 + len;
            }
            return false;
        }

        public override async Task<byte[]> CaptureJpeg(int maxSize)
        {
            LastView = null;
#if UNITY_ANDROID && !UNITY_EDITOR
            // Magic Leap 2: MLCamera first (WebCamTexture finds no camera there)
            if (UnityEngine.XR.XRSettings.isDeviceActive && !_mlFailed)
            {
                try
                {
                    if (_ml == null) _ml = new MagicLeapCamera(Mathf.Min(requestedWidth, maxSize)); // about the size sent
                    float t0 = Time.realtimeSinceStartup;
                    var raw = await _ml.CaptureJpeg(firstFrameTimeoutS);
                    if (raw != null && JpegSize(raw, out int jw, out int jh) && Mathf.Max(jw, jh) <= maxSize * 1.4f)
                    {
                        // already about the right size: sent as it is - no decoding, GPU read-back and
                        // re-encoding on the main thread
                        Debug.Log($"[WhatIf] camera: frame {jw}x{jh} -> {raw.Length / 1024} kB as is " +
                                  $"after {Time.realtimeSinceStartup - t0:0.0} s");
                        LastView = _ml.LastView ?? EyeView(jw / (float)jh);
                        return raw;
                    }
                    if (raw != null)
                    {
                        var tex = new Texture2D(2, 2);
                        tex.LoadImage(raw);
                        var jpg = EncodeScaled(tex, maxSize);
                        Debug.Log($"[WhatIf] camera: frame {tex.width}x{tex.height} -> {jpg.Length / 1024} kB " +
                                  $"after {Time.realtimeSinceStartup - t0:0.0} s");
                        LastView = _ml.LastView ?? EyeView(tex.width / (float)tex.height);
                        Destroy(tex);
                        return jpg;
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[WhatIf] camera: MLCamera failed ({e.Message}), trying WebCamTexture from now on");
                    _mlFailed = true;
                }
            }
#endif
            try
            {
                if (_cam == null)
                {
                    var devices = WebCamTexture.devices;
                    if (!_loggedDevices || devices.Length == 0)
                    {
                        var names = new string[devices.Length];
                        for (int i = 0; i < devices.Length; i++)
                            names[i] = devices[i].name + (devices[i].isFrontFacing ? " (front)" : "");
                        Debug.Log($"[WhatIf] camera: {devices.Length} device(s): {string.Join(", ", names)}" +
                                  (devices.Length == 0 ? " - camera permission granted?" : ""));
                        _loggedDevices = devices.Length > 0;
                    }
                    if (devices.Length == 0) return null;
                    int pick = 0; // prefer a world-facing camera
                    for (int i = 0; i < devices.Length; i++) if (!devices[i].isFrontFacing) { pick = i; break; }
                    _cam = new WebCamTexture(devices[pick].name, requestedWidth, requestedHeight);
                }
                _lastUse = Time.unscaledTime;
                if (!_cam.isPlaying) _cam.Play();

                // wait for a real frame
                float start = Time.realtimeSinceStartup;
                while (_cam.width <= 16 && Time.realtimeSinceStartup - start < firstFrameTimeoutS)
                    await Task.Delay(50);
                if (_cam.width <= 16)
                {
                    Debug.LogWarning($"[WhatIf] camera: no frame after {firstFrameTimeoutS:0} s (playing={_cam.isPlaying})");
                    return null;
                }
                var jpg = EncodeScaled(_cam, maxSize);
                // on the headset the camera looks where the user looks (approximately); not in the Editor
                if (UnityEngine.XR.XRSettings.isDeviceActive) LastView = EyeView(_cam.width / (float)_cam.height);
                Debug.Log($"[WhatIf] camera: frame {_cam.width}x{_cam.height} -> {jpg.Length / 1024} kB " +
                          $"after {Time.realtimeSinceStartup - start:0.0} s");
                return jpg;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[WhatIf] camera: capture failed: {e.Message}");
                return null;
            }
        }

        static CameraView EyeView(float aspect) =>
            Camera.main != null ? CameraView.FromUnityCamera(Camera.main, aspect) : null;

        void Update()
        {
            // stop the camera when it hasn't been needed for a while (power, privacy indicator)
            if (_cam != null && _cam.isPlaying && Time.unscaledTime - _lastUse > keepRunningS) _cam.Stop();
        }

        void OnDisable()
        {
            if (_cam != null && _cam.isPlaying) _cam.Stop();
#if UNITY_ANDROID && !UNITY_EDITOR
            _ml?.Disconnect();
            _ml = null;
#endif
        }
    }
}
