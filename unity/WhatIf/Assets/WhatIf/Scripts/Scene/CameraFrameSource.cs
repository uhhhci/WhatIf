using System;
using System.Threading.Tasks;
using UnityEngine;

namespace WhatIf.Scene
{
    /// <summary>Provides a still image of what the user sees, for the planner.</summary>
    public abstract class CameraFrameSource : MonoBehaviour
    {
        /// <summary>Returns a JPEG of the current view, or null if unavailable.</summary>
        public abstract Task<byte[]> CaptureJpeg(int maxSize);

        /// <summary>Where the last frame was taken from and its lens, so image points can be found
        /// in the room; null when the frame isn't aligned with the room (the Editor's webcam).</summary>
        public CameraView LastView { get; protected set; }

        /// <summary>Average colour of the last frame (the room's light), black before the first.</summary>
        public static Color LastAverage { get; protected set; } = Color.black;

        protected static byte[] EncodeScaled(Texture source, int maxSize)
        {
            float scale = Mathf.Min(1f, (float)maxSize / Mathf.Max(source.width, source.height));
            int w = Mathf.Max(1, Mathf.RoundToInt(source.width * scale));
            int h = Mathf.Max(1, Mathf.RoundToInt(source.height * scale));
            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(source, rt);
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply();
            // average colour of the view: how bright and how warm the room's light is
            var px = tex.GetPixels32();
            long r = 0, g = 0, b = 0;
            for (int i = 0; i < px.Length; i += 7) { r += px[i].r; g += px[i].g; b += px[i].b; }
            int n = Mathf.Max(1, (px.Length + 6) / 7);
            LastAverage = new Color(r / (255f * n), g / (255f * n), b / (255f * n));
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            var jpg = tex.EncodeToJPG(80);
            Destroy(tex);
            return jpg;
        }
    }
}
