using UnityEngine;

namespace WhatIf.UI
{
    /// <summary>
    /// Status text that lazily follows the view, low in the field of view so it doesn't cover the
    /// scene. Messages fade out after <see cref="holdSeconds"/> unless shown as sticky (work in
    /// progress, typing). Uses the built-in TextMesh so it needs no TextMeshPro setup.
    /// </summary>
    public class HeadsetHud : MonoBehaviour
    {
        public Transform head;
        public float followDistance = 1.4f;
        [Tooltip("How far below eye level, in metres at the given distance (0.45 m at 1.4 m is ~18 degrees).")]
        public float dropBelowEye = 0.45f;
        public float followSpeed = 3f;
        [Tooltip("How much the HUD follows looking up and down (0 = stays at eye level, 1 = fully).")]
        [Range(0f, 1f)] public float followPitch = 0.7f;
        public float characterSize = 0.006f;
        [Tooltip("Seconds a non-sticky message stays fully visible before fading out.")]
        public float holdSeconds = 6f;
        public float fadeSeconds = 1f;

        TextMesh _text;
        TextMesh _indicator;
        float _shownAt;
        bool _sticky;
        string _idle1, _idle2;   // shown whenever nothing else is (the start invitation)
        bool _showingIdle;
        bool _placed;

        void Awake()
        {
            var go = new GameObject("text");
            go.transform.SetParent(transform, false);
            _text = go.AddComponent<TextMesh>();
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            _text.font = font;
            go.GetComponent<MeshRenderer>().sharedMaterial = font.material;
            _text.anchor = TextAnchor.MiddleCenter;
            _text.alignment = TextAlignment.Center;
            _text.characterSize = characterSize;
            _text.fontSize = 48;
            _text.richText = true;
            _text.color = Color.white; // bright: dark colours are transparent on additive displays

            // small status line under the messages ("listening" while follow-ups count)
            var ind = new GameObject("indicator");
            ind.transform.SetParent(transform, false);
            ind.transform.localPosition = new Vector3(0f, -0.09f, 0f);
            _indicator = ind.AddComponent<TextMesh>();
            _indicator.font = font;
            ind.GetComponent<MeshRenderer>().sharedMaterial = font.material;
            _indicator.anchor = TextAnchor.MiddleCenter;
            _indicator.alignment = TextAlignment.Center;
            _indicator.characterSize = characterSize;
            _indicator.fontSize = 30;
            _indicator.color = new Color(0.55f, 0.9f, 1f, 1f);
        }

        /// <summary>The small status line under the messages; null hides it.</summary>
        public void SetIndicator(string text)
        {
            if (_indicator == null) return;
            var t = text ?? "";
            if (_indicator.text != t) _indicator.text = t;
        }

        /// <summary>Show a message. Sticky messages stay until replaced; others fade out.</summary>
        public void Show(string line1, string line2 = null, bool sticky = false)
        {
            _showingIdle = false;
            SetText(line1, line2, sticky);
        }

        /// <summary>A message the HUD returns to whenever nothing else is shown (after Hide or a
        /// fade-out), e.g. the invitation before the first wish; null clears it.</summary>
        public void SetIdle(string line1, string line2 = null)
        {
            _idle1 = line1; _idle2 = line2;
            if (_text == null) return;
            if (_idle1 == null) { if (_showingIdle) Hide(); return; }
            if (_showingIdle || _text.color.a <= 0.01f) ShowIdle();
        }

        void ShowIdle()
        {
            _showingIdle = true;
            SetText(_idle1, _idle2, true);
        }

        void SetText(string line1, string line2, bool sticky)
        {
            _text.text = Wrap(line1, 48) + (string.IsNullOrEmpty(line2) ? "" : "\n<size=36>" + Wrap(line2, 60) + "</size>");
            _shownAt = Time.time;
            _sticky = sticky;
            SetAlpha(1f);
        }

        public void Hide()
        {
            if (_idle1 != null) { ShowIdle(); return; }
            _showingIdle = false;
            _sticky = false;
            _shownAt = Time.time - holdSeconds - fadeSeconds;
            SetAlpha(0f);
        }

        void Update()
        {
            if (_sticky || _text == null) return;
            float t = Time.time - _shownAt - holdSeconds;
            if (t <= 0f) return;
            if (t >= fadeSeconds && _idle1 != null) { ShowIdle(); return; }
            SetAlpha(1f - Mathf.Clamp01(t / Mathf.Max(0.01f, fadeSeconds)));
        }

        void SetAlpha(float a)
        {
            var c = _text.color;
            if (Mathf.Approximately(c.a, a)) return;
            c.a = a;
            _text.color = c;
        }

        void LateUpdate()
        {
            if (head == null) return;
            var flat = head.forward; flat.y = 0;
            if (flat.sqrMagnitude < 1e-4f) flat = Vector3.forward;
            flat.Normalize();
            // follows looking up and down a good part of the way (looking up at a tree used to leave
            // the HUD below the view), and stays a little below the line of sight
            var dir = Vector3.Slerp(flat, head.forward, followPitch).normalized;
            var below = Vector3.Cross(dir, Vector3.Cross(Vector3.up, dir)).normalized; // "down" across the view
            if (below.y > 0f) below = -below;
            var target = head.position + dir * followDistance + below * dropBelowEye;
            // the first frame jumps into view (it starts at the origin, maybe behind the user)
            transform.position = _placed ? Vector3.Lerp(transform.position, target, Time.deltaTime * followSpeed) : target;
            _placed = true;
            transform.rotation = Quaternion.LookRotation(transform.position - head.position, Vector3.up);
        }

        static string Wrap(string s, int width)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= width) return s;
            var sb = new System.Text.StringBuilder();
            int col = 0;
            foreach (var word in s.Split(' '))
            {
                if (col + word.Length > width && col > 0) { sb.Append('\n'); col = 0; }
                else if (col > 0) { sb.Append(' '); col++; }
                sb.Append(word);
                col += word.Length;
            }
            return sb.ToString();
        }
    }
}
