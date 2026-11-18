using UnityEngine;

namespace WhatIf.Content
{
    /// <summary>Average frame rate and the slowest frame of the last 10 s in the log, with what is on:
    /// shows when the scene gets too heavy for the headset (shadows, many objects).</summary>
    public class FrameRateLog : MonoBehaviour
    {
        float _start, _worst;
        int _frames;

        void Update()
        {
            float dt = Time.unscaledDeltaTime;
            _frames++;
            if (dt > _worst) _worst = dt;
            float now = Time.realtimeSinceStartup;
            if (_start == 0f) { _start = now; return; }
            if (now - _start < 10f) return;
            float fps = _frames / (now - _start);
            var sun = RenderSettings.sun;
            Debug.Log($"[WhatIf] frame rate: {fps:F0} fps, slowest frame {_worst * 1000f:F0} ms, " +
                      $"{FindObjectsOfType<GeneratedObject>().Length} objects, shadows {(sun != null && sun.shadows != LightShadows.None ? "on" : "off")}");
            _start = now; _frames = 0; _worst = 0f;
        }
    }
}
