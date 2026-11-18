using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.ARFoundation;
using WhatIf.Api;
using WhatIf.Content;
using WhatIf.Placement;
using WhatIf.Scene;
using WhatIf.UI;
using WhatIf.Voice;

namespace WhatIf
{
    /// <summary>
    /// Orchestrates one "What if ...?" round:
    ///   scan surfaces + grab a camera frame -> server plans + starts generation ->
    ///   compute placements -> show 2D cut-outs as soon as they exist -> swap in 3D models.
    ///
    /// Controls (Magic Leap 2 controller / editor keyboard):
    ///   Tap bumper / Tab              next preset prompt
    ///   Trigger / Enter               run the selected prompt
    ///   Menu / K                      type a custom prompt (system keyboard on the headset;
    ///                                 in the Editor the text is typed into the HUD, Enter / Esc)
    ///   Hold bumper 1 s / Backspace   clear everything
    /// </summary>
    public partial class WhatIfController : MonoBehaviour
    {
        public WhatIfSettings settings;
        public SceneScanner scanner;
        public CameraFrameSource cameraFrame;
        public EnvironmentEffects effects;
        public HeadsetHud hud;
        public Transform head;

        WhatIfClient _client;
        AssetLibrary _library;
        readonly List<GameObject> _spawned = new List<GameObject>();
        int _preset;
        bool _busy;
        int _round; // increments on every run/clear so stale async work can bail out
        TouchScreenKeyboard _keyboard;
        Material _billboardMaterial;

        // Typing a custom prompt where no system keyboard exists (Editor, desktop).
        const string PromptStart = "What if this place ";
        public static bool IsTyping { get; private set; }
        readonly System.Text.StringBuilder _typed = new System.Text.StringBuilder();
        int _typingStartFrame, _typingEndFrame = -1;

        InputAction _run, _next, _custom, _clear;

        void Awake()
        {
            _client = new WhatIfClient(settings.serverUrl);
            _client.TranscribeContext = SpeechHints;
            _library = new AssetLibrary(_client);
            if (head == null && Camera.main != null) head = Camera.main.transform;
            // A lost scene reference must not silently switch the camera image off.
            if (cameraFrame == null) cameraFrame = GetComponent<CameraFrameSource>();
            if (cameraFrame == null) cameraFrame = gameObject.AddComponent<WebCamFrameSource>(); // not ??: Unity fake-null
            if (GetComponent<MeshingBounds>() == null) gameObject.AddComponent<MeshingBounds>(); // ML2: room mesh needs a query box
            // depth images for things the planner points at (headset only)
            if (settings.depthCamera && !Application.isEditor)
            {
                _depthFrames = GetComponent<DepthFrames>();
                if (_depthFrames == null) _depthFrames = gameObject.AddComponent<DepthFrames>(); // not ??: Unity fake-null
            }
            // real walls hide what is behind them (headset only: no real room in the Editor)
            // (walls are always estimated on the headset - placement uses them for wall items and to keep
            // doorways free; the Occlusion setting only decides whether they hide what is behind them)
            if (!Application.isEditor && GetComponent<RoomOcclusion>() == null)
            {
                var occ = gameObject.AddComponent<RoomOcclusion>();
                occ.planeManager = scanner != null ? scanner.planeManager : null;
                occ.scanner = scanner;
                occ.occlude = settings.occlusion;
                occ.occludeFurniture = settings.occlusion && settings.occludeFurniture; // off: only walls hide virtual objects
            }
            _billboardMaterial = settings.billboardMaterial != null ? settings.billboardMaterial : MaterialFactory.AlphaClip();

            _run = new InputAction("run", InputActionType.Button);
            _run.AddBinding("<Keyboard>/enter");
            _next = new InputAction("next", InputActionType.Button, interactions: "tap(duration=0.4)");
            _next.AddBinding("<Keyboard>/tab");
            _custom = new InputAction("custom", InputActionType.Button);
            _custom.AddBinding("<Keyboard>/k");
            _clear = new InputAction("clear", InputActionType.Button, interactions: "hold(duration=1)");
            _clear.AddBinding("<Keyboard>/backspace");

            // Shortcuts are ignored while typing (and in the frame typing ended), so the keys
            // typed into a prompt don't also run presets, clear the scene or open the keyboard.
            _run.performed += _ => { if (!TypingBlocks()) Imagine(settings.presetPrompts[_preset]); };
            _next.performed += _ => { if (!TypingBlocks()) NextPreset(); };
            _custom.performed += _ => { if (!TypingBlocks()) OpenKeyboard(); };
            _clear.performed += _ => { if (!TypingBlocks()) ClearAll(); };
        }

        void OnEnable()
        {
            _run.Enable(); _next.Enable(); _custom.Enable(); _clear.Enable();
            if (Keyboard.current != null) Keyboard.current.onTextInput += OnTextInput;
        }

        void OnDisable()
        {
            _run.Disable(); _next.Disable(); _custom.Disable(); _clear.Disable();
            if (Keyboard.current != null) Keyboard.current.onTextInput -= OnTextInput;
            IsTyping = false;
        }

        bool TypingBlocks() => IsTyping || Time.frameCount <= _typingEndFrame + 1 || VoiceOnly;

        /// <summary>The headset is controlled by voice and gaze only (no controller); the Editor keeps the keyboard.</summary>
        bool VoiceOnly => settings.enableVoice && UnityEngine.XR.XRSettings.isDeviceActive;

        VoiceCommandListener _voice;

        void StartVoice()
        {
            if (!settings.enableVoice) return;
            _voice = GetComponent<VoiceCommandListener>();
            if (_voice == null) _voice = gameObject.AddComponent<VoiceCommandListener>(); // not ??: Unity fake-null
            _voice.Init(settings, _client.TranscribeFull);
            _voice.StateChanged += OnVoiceState;
            _voice.PromptHeard += OnVoicePrompt;
            _voice.UtteranceHeard += OnUtterance;
            _voice.SpeechStarted += PrefetchFrame;
        }

        bool _voiceHudShown;

        void OnVoiceState(VoiceCommandListener.State state, string message)
        {
            switch (state)
            {
                case VoiceCommandListener.State.Recording:
                    // continuous listening hears everything: a quiet hint, no sticky banner
                    if (!settings.continuousListening) hud?.Show("Listening ...", null, sticky: true);
                    break;
                case VoiceCommandListener.State.Transcribing:
                    hud?.Show("...", null, sticky: true);
                    _voiceHudShown = true;
                    break;
                case VoiceCommandListener.State.Unavailable: hud?.Show("Voice input unavailable", message); break;
                default:
                    if (message != null) hud?.Show(message);
                    else if (_voiceHudShown) hud?.Hide(); // ignored (noise, chatter): take the "..." away
                    _voiceHudShown = false;
                    break;
            }
        }

        void OnVoicePrompt(string prompt)
        {
            if (_voice.IsClearCommand(prompt)) { ClearAll(); return; }
            // with a scene shown, the content decides how much changes (see OnUtterance)
            if (HasScene()) { EnqueueEdit("What if " + prompt, CurrentGaze()); return; }
            if (_busy) ClearAll(); // a new idea replaces the one still being built
            Imagine(prompt);
        }

        void Start()
        {
            RequestPermissions();
            SceneLighting.Apply(settings);
            if (settings.castShadows) gameObject.AddComponent<PlanarShadows>().Init(scanner, head, settings);
            gameObject.AddComponent<FrameRateLog>();
            _lightEstimate = gameObject.AddComponent<RoomLightEstimate>();
            _lightEstimate.Init(settings);
            SegmentedDimming.Apply(settings, head != null ? head.GetComponent<Camera>() : Camera.main);
            StartVoice();
            StartGaze();
            ShowPreset();
            WatchServer();
            // Grid floor for testing without a headset; on device the real floor is visible.
            if (Application.isEditor && !UnityEngine.XR.XRSettings.isDeviceActive && settings.editorGround)
                EditorGround.Create(scanner, head, settings);
        }

        void Update()
        {
            if (!_wished && VoiceOnly && Time.time > _nextExampleAt) Invite();
            if (_keyboard != null)
            {
                switch (_keyboard.status)
                {
                    case TouchScreenKeyboard.Status.Visible:
                        hud?.Show(_keyboard.text + "|", "Confirm on the keyboard to show it", sticky: true);
                        break;
                    case TouchScreenKeyboard.Status.Done:
                        var text = _keyboard.text;
                        _keyboard = null;
                        if (string.IsNullOrWhiteSpace(text)) ShowPreset();
                        else if (VoiceCommandListener.ExtractScenario(text) != null || !HasScene()) Imagine(text);
                        else EnqueueEdit(text, CurrentGaze());
                        break;
                    default: // canceled or lost focus
                        _keyboard = null;
                        ShowPreset();
                        break;
                }
            }
            if (IsTyping) UpdateTyping();
            if (settings.enableVoice && settings.voiceNeedsGaze)
                // right away when speech starts, until its transcript is handled: no "stuck" second
                hud?.SetIndicator(_voice != null && _voice.Hearing ? "\u2022 \u2022 \u2022"
                                  : FollowUpOpen ? "\u2022 listening \u2022" : null);
        }

        // ------------------------------------------------------------------ input

        void NextPreset()
        {
            if (_busy) return;
            _preset = (_preset + 1) % settings.presetPrompts.Length;
            ShowPreset();
        }

        static readonly string[] OnboardingExamplesEn =
        {
            "What if this room were a jungle?", "What if this place were a medieval castle hall?",
            "What if the floor were lava?", "What if this office were on the sea floor?",
        };
        static readonly string[] OnboardingExamplesDe =
        {
            "Was wäre, wenn dieser Raum ein Dschungel wäre?", "What if this place were a medieval castle hall?",
            "Was wäre, wenn der Boden aus Lava wäre?", "What if this office were on the sea floor?",
        };
        int _exampleIndex;
        bool _wished;          // the user has wished for something: no more start invitation
        float _nextExampleAt;

        /// <summary>Before the first wish the HUD keeps inviting ("Say "What if ..."" with an example
        /// that changes every few seconds) whatever else comes and goes; afterwards it is quiet.</summary>
        void Invite()
        {
            if (_wished || !VoiceOnly || hud == null) return;
            if (!settings.showSuggestions) { hud.SetIdle(null); return; }
            bool german = settings.germanKeyword && !settings.englishOnly;
            var examples = german ? OnboardingExamplesDe : OnboardingExamplesEn;
            hud.SetIdle(german ? "Sag \"What if ...\" / \"Was wäre, wenn ...\"" : "Say \"What if ...\"",
                        "for example: \"" + examples[_exampleIndex++ % examples.Length] + "\"");
            _nextExampleAt = Time.time + 8f;
        }

        /// <summary>The first real wish: the invitation goes, transcripts and answers take over.</summary>
        void Wished()
        {
            if (_wished) return;
            _wished = true;
            hud?.SetIdle(null);
        }

        void ShowPreset()
        {
            if (VoiceOnly && !_wished) { Invite(); return; }
            if (VoiceOnly)
            {
                // an invitation, not a password: an example of a whole scenario, a different one each time
                var examples = settings.germanKeyword ? OnboardingExamplesDe : OnboardingExamplesEn;
                hud?.Show("Try: \"" + examples[_exampleIndex++ % examples.Length] + "\"",
                          "\"... alles weg\" / \"clear everything\" clears");
                return;
            }
            ShowPresetText();
        }

        void ShowPresetText() =>
            hud?.Show(settings.presetPrompts[_preset], settings.enableVoice
                ? (settings.germanKeyword && !settings.englishOnly ? "Enter: show it   Tab: next   K: type   or say \"What if ...\" / \"Was wäre, wenn ...\""
                                          : "Enter: show it   Tab: next   K: type   or say \"What if ...\"")
                : "Enter: show it   Tab: next   K: type");

        void OpenKeyboard()
        {
            if (_busy) return;
            if (TouchScreenKeyboard.isSupported)
            {
                _keyboard = TouchScreenKeyboard.Open(HasScene() ? "" : PromptStart, TouchScreenKeyboardType.Default);
                return;
            }
            // No system keyboard (Editor / desktop): type straight into the HUD. With a scene to edit,
            // start empty ("make it bigger"); otherwise with the start of a new scenario.
            _typed.Clear().Append(HasScene() ? "" : PromptStart);
            _typingStartFrame = Time.frameCount;
            IsTyping = true;
            ShowTyping();
        }

        void OnTextInput(char c)
        {
            if (!IsTyping || char.IsControl(c)) return; // Enter, Backspace, Esc are handled as keys
            // The K that opened typing mode arrives as text too; drop it.
            if (Time.frameCount <= _typingStartFrame + 1 && (c == 'k' || c == 'K')) return;
            _typed.Append(c);
            ShowTyping();
        }

        void UpdateTyping()
        {
            var kb = Keyboard.current;
            if (kb == null) return;
            if (kb.escapeKey.wasPressedThisFrame)
            {
                EndTyping();
                ShowPreset();
            }
            else if (kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame)
            {
                var text = _typed.ToString().Trim();
                EndTyping();
                if (text.Length > 3 && (VoiceCommandListener.ExtractScenario(text) != null || !HasScene())) Imagine(text);
                else if (text.Length > 3) EnqueueEdit(text, CurrentGaze());
                else ShowPreset();
            }
            else if (kb.backspaceKey.wasPressedThisFrame && _typed.Length > 0)
            {
                _typed.Length--;
                ShowTyping();
            }
        }

        void EndTyping()
        {
            IsTyping = false;
            _typingEndFrame = Time.frameCount;
        }

        void ShowTyping() => hud?.Show(_typed + "|", HasScene()
            ? "Enter: change the scene (or start with \"What if\" for a new one)   Esc: cancel"
            : "Enter: show it   Esc: cancel", sticky: true);

        static void RequestPermissions()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            // Plane extraction and meshing need SPATIAL_MAPPING; the camera frame needs CAMERA.
            // All at once: Android shows one dialog at a time, separate requests can get lost.
            var missing = new System.Collections.Generic.List<string>();
            foreach (var p in new[] { "com.magicleap.permission.SPATIAL_MAPPING", "android.permission.CAMERA",
                                      UnityEngine.Android.Permission.Microphone, "com.magicleap.permission.EYE_TRACKING",
                                      "com.magicleap.permission.DEPTH_CAMERA" })
                if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(p)) missing.Add(p);
            if (missing.Count > 0) UnityEngine.Android.Permission.RequestUserPermissions(missing.ToArray());
#endif
        }

        // ------------------------------------------------------------------ main flow

        /// <param name="natural">Said without "What if" while nothing is shown: may be talk to someone.
        /// The planner decides; nothing is shown until it says it is a request.</param>
        /// <param name="spot">Where the user looked while saying it ("a tree there"); null if not spoken.</param>
        public async void Imagine(string prompt, bool natural = false, LookedAtPoint spot = null)
        {
            if (_busy) return;
            _busy = true;
            _spot = spot;
            int round = ++_round;
            try
            {
                ClearContent();
                // "What if ...": a wish for sure - the invitation goes now. A natural sentence may be
                // talk to someone: its transcript shows, the invitation returns if it was not a wish.
                if (!natural) Wished();
                hud?.Show(natural ? $"\"{prompt}\"" : prompt, "Looking around ...", sticky: true);

                // the frame first: the surfaces are then projected with the camera that took it
                var jpg = await CaptureFrame();
                if (round != _round) return;
                var surfaces = scanner.Collect(settings);
                var request = new PlanRequest
                {
                    Prompt = prompt,
                    Natural = natural,
                    EnglishOnly = settings.englishOnly,
                    LookedAt = DescribeSpot(_spot),
                    Scene = scanner.Describe(surfaces, settings, _view),
                    ImageJpegB64 = jpg != null ? Convert.ToBase64String(jpg) : null,
                };

                hud?.Show(natural ? $"\"{prompt}\"" : prompt, "Imagining ...", sticky: true);

                // Objects are placed and generated as soon as the planner has written them (streamed),
                // instead of after the whole plan (~20 s for a full environment); the rest of the plan
                // (effects, sounds, notes) follows when it is done.
                var solver = new PlacementSolver(surfaces, scanner, head) { ImageTopOf = ImageTopOf(surfaces) };
                var tasks = new List<Task>();
                var byItem = new Dictionary<string, List<GeneratedObject>>();
                var done = new HashSet<string>();
                int unplaced = 0;
                bool shown = false;
                var where = new System.Text.StringBuilder();
                where.Append($"[WhatIf] placement: head y={head.position.y:F2}, floor y={scanner.FloorY:F2} " +
                             $"({(scanner.FloorFromSensors ? "sensors" : "assumed standing")}), surfaces:");
                for (int i = 0; i < surfaces.Count; i++)
                    where.Append($" S{i + 1}={surfaces[i].Label}@{surfaces[i].Center.y:F2}");
                Debug.Log(where);

                void PlaceItem(AssetItem item, AssetInfo asset)
                {
                    if (item == null || !done.Add(item.Id)) return;
                    var spots = item.Placement.Surface == "hand" ? HandSpot(item) : solver.Place(item.Placement, item.Id);
                    var line = new System.Text.StringBuilder($"  {item.Id} ({item.Placement.Surface}/{item.Placement.Layout} " +
                                                             $"target={item.Placement.TargetId}): {spots.Count} placed");
                    foreach (var s in spots) line.Append($" y={s.Position.y:F2} h={s.Height:F2}");
                    Debug.Log(line);
                    if (spots.Count == 0)
                    {
                        unplaced++;
                        if (asset != null) PlaceLater(item, asset, round);
                        return;
                    }
                    if (asset == null) return;
                    var instances = Spawn(item, spots);
                    byItem[item.Id] = instances;
                    tasks.Add(Realise(asset, item, instances, round));
                }

                PlanResponse response = null;
                string failed = null;
                float asked = Time.realtimeSinceStartup;
                bool streamed = await _client.PlanStream(request, ev =>
                {
                    if (round != _round) return;
                    if (ev.Event == "item")
                    {
                        if (!shown)
                        {
                            // the first object: it is a wish, and something is on its way
                            shown = true;
                            Wished();
                            Debug.Log($"[WhatIf] first object planned after {Time.realtimeSinceStartup - asked:F1} s");
                            hud?.Show(string.IsNullOrEmpty(ev.Interpretation) ? prompt : ev.Interpretation, "Imagining ...", sticky: true);
                        }
                        PlaceItem(ev.Item, ev.Asset);
                    }
                    else if (ev.Event == "done") response = ev.Response;
                    else if (ev.Event == "error") failed = ev.Detail;
                });
                if (round != _round) return;
                if (!streamed) response = await _client.Plan(request); // an older server
                if (failed != null) throw new Exception(failed);
                if (response == null) throw new Exception("The server's plan ended early");
                Debug.Log($"[WhatIf] whole plan after {Time.realtimeSinceStartup - asked:F1} s ({(streamed ? "streamed" : "not streamed")})");
                MatchRoomLight(response.RoomLight);
                if (round != _round) return;
                var plan = response.Plan;
                if (!plan.Request)
                {
                    Debug.Log("[WhatIf] voice: not a request, ignored");
                    hud?.Hide();
                    return;
                }
                Wished();
                _scenario = $"{prompt} ({plan.Interpretation})";
                hud?.Show(plan.Interpretation, plan.NoteForUser);
                OpenFollowUp();

                effects?.Apply(plan.Effects, scanner.FloorY, head.position, settings, scanner,
                               groundCoat: plan.Items.Exists(i => i.Placement.Layout == "coat"));

                // objects planned early that the final plan dropped (cut to its limit): taken away again
                var final = new HashSet<string>();
                foreach (var item in plan.Items) final.Add(item.Id);
                foreach (var id in new List<string>(byItem.Keys))
                {
                    if (final.Contains(id)) continue;
                    Debug.Log($"[WhatIf] {id}: not in the final plan, removed");
                    foreach (var o in byItem[id]) if (o != null) Destroy(o.gameObject);
                    byItem.Remove(id);
                }
                // the rest of the plan (objects that waited for a later target, coats)
                foreach (var item in plan.Items)
                    PlaceItem(item, response.Assets.TryGetValue(item.Id, out var a) ? a : null);
                if (unplaced > 0)
                    hud?.Show(plan.Interpretation, unplaced == 1 ? "One kind of object found no fitting surface - look around to scan more."
                        : $"{unplaced} kinds of objects found no fitting surface - look around to scan more.");

                tasks.Add(PlaySounds(response, byItem, round));
                await Task.WhenAll(tasks);
                if (round == _round) _lightEstimate?.Sample(); // where the room's light comes from, for the shadows
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                hud?.Show("Something went wrong", e.Message);
            }
            finally
            {
                if (round == _round) _busy = false;
            }
        }

        // ------------------------------------------------------------------ camera frame + image targets

        CameraView _view; // where the frame of the request being handled was taken from (null: none)
        DepthSnapshot _depth; // depth image taken with that frame (null: none)
        DepthFrames _depthFrames;

        struct Shot { public byte[] Jpg; public CameraView View; public DepthSnapshot Depth; }

        RoomLightEstimate _lightEstimate; // briefly after a scene is built; stopped before each picture

        Task<Shot> _inflight;           // one camera capture at a time (two at once: PrepareCapture fails)
        Task<Shot> _prefetch;           // taken when the user started speaking
        float _prefetchAt;

        /// <summary>The camera picture is taken as soon as speech begins: it is ready when the sentence
        /// has been transcribed, instead of 1.3-2 s later. It shows where the user looked while speaking.</summary>
        void PrefetchFrame()
        {
            if (!settings.sendCameraFrame || cameraFrame == null) return;
            _prefetchAt = Time.realtimeSinceStartup;
            _prefetch = TakeShot();
        }

        Task<Shot> TakeShot()
        {
            if (_inflight != null && !_inflight.IsCompleted) return _inflight;
            return _inflight = TakeShotNow();
        }

        async Task<Shot> TakeShotNow()
        {
            _lightEstimate?.Stop(); // it reads the same camera
            var jpg = await cameraFrame.CaptureJpeg(settings.cameraFrameMaxSize);
            return new Shot
            {
                Jpg = jpg,
                View = jpg != null ? cameraFrame.LastView : null,
                // (the room's light colour comes back with the server's answer: no decoding here)
                Depth = jpg != null && _depthFrames != null ? _depthFrames.Latest() : null,
            };
        }

        async Task<byte[]> CaptureFrame()
        {
            _view = null;
            if (!settings.sendCameraFrame || cameraFrame == null) return null;
            // the picture from the start of the sentence, if it is recent (a long sentence is ~10 s)
            var early = _prefetch != null && Time.realtimeSinceStartup - _prefetchAt < 30f ? _prefetch : null;
            _prefetch = null;
            Shot shot = default;
            if (early != null)
            {
                bool ready = early.IsCompleted;
                shot = await early;
                if (shot.Jpg != null) Debug.Log($"[WhatIf] camera: picture from the start of the sentence ({(ready ? "ready" : "waited")})");
            }
            if (shot.Jpg == null) shot = await TakeShot();
            _view = shot.View;
            _depth = shot.Depth;
            return shot.Jpg;
        }

        /// <summary>
        /// Real things the planner pointed at in the frame ("img:x,y") -> the top of them (once per id).
        /// Not found (room not meshed there yet): a spot ~0.8 m along the ray - roughly where the user
        /// looked - rather than the floor, plus a hint to look around.
        /// </summary>
        System.Func<string, Vector3?> ImageTopOf(List<Surface> surfaces)
        {
            var cache = new Dictionary<string, Vector3?>();
            return id =>
            {
                if (id == "gaze")
                {
                    var spot = SpotTop();
                    Debug.Log(spot.HasValue ? $"[WhatIf] gaze target at {spot.Value:F2}" : "[WhatIf] gaze target: no looked-at point");
                    return spot;
                }
                if (cache.TryGetValue(id, out var known)) return known;
                var hit = ImageTargets.Resolve(id, _view, surfaces, scanner.meshLayers, _depth);
                Vector3? top = hit?.Top;
                if (hit.HasValue)
                    Debug.Log($"[WhatIf] image target {id}: {hit.Value.Via} at {hit.Value.Point:F2}, top y={hit.Value.Top.y:F2} ({_view.Source})");
                else if (_view != null && ImageTargets.TryParse(id, out float u, out float v))
                {
                    top = _view.Unproject(u, v).GetPoint(0.8f);
                    Debug.Log($"[WhatIf] image target {id}: nothing hit, guessing 0.8 m along the ray ({_view.Source})");
                    hud?.Show("I can't see that spot in 3D yet", "Look around it for a moment so the headset can scan it.");
                }
                else Debug.Log($"[WhatIf] image target {id}: no camera view");
                cache[id] = top;
                return top;
            };
        }

        void MatchRoomLight(string hex)
        {
            if (!string.IsNullOrEmpty(hex) && ColorUtility.TryParseHtmlString(hex, out var c))
                SceneLighting.MatchRoom(settings, c);
        }

        /// <summary>A rough ceiling height: the lowest detected ceiling above the user, else 2.5 m above
        /// the floor (flyers stay below it).</summary>
        float CeilingY()
        {
            float best = scanner.FloorY + 2.5f;
            bool found = false;
            foreach (var s in scanner.Collect(settings))
                if (s.Plane != null && Vector3.Dot(s.Normal, Vector3.down) > 0.8f && s.Center.y > head.position.y)
                {
                    best = found ? Mathf.Min(best, s.Center.y) : s.Center.y;
                    found = true;
                }
            return best;
        }

        /// <summary>Names of the objects shown, as hints for speech recognition: they become more likely,
        /// nothing else less possible (Goethe in a lava scene can still be said). Only names, at most
        /// ten: a long hint text makes Whisper "hear" its words in noise.</summary>
        string SpeechHints()
        {
            var words = new List<string>();
            foreach (var o in LiveObjects())
                if (!string.IsNullOrEmpty(o.Label) && !words.Contains(o.Label) && words.Count < 10) words.Add(o.Label);
            return string.Join(", ", words);
        }

        /// <summary>An item without a fitting surface yet (walls not seen): tried again every few seconds
        /// for a minute, as the headset learns more of the room.</summary>
        async void PlaceLater(AssetItem item, AssetInfo asset, int round)
        {
            for (int i = 0; i < 20; i++)
            {
                await Task.Delay(3000);
                if (this == null || round != _round) return;
                var surfaces = scanner.Collect(settings);
                var solver = new PlacementSolver(surfaces, scanner, head) { ImageTopOf = ImageTopOf(surfaces) };
                var spots = solver.Place(item.Placement, item.Id);
                if (spots.Count == 0) continue;
                Debug.Log($"[WhatIf] {item.Id}: placed later ({spots.Count})");
                await Realise(asset, item, Spawn(item, spots), round);
                return;
            }
        }

        /// <summary>"A bird on my hand": starts in front of the user at hand height, then follows the hand.</summary>
        List<PlacedInstance> HandSpot(AssetItem item)
        {
            var fwd = head.forward; fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            fwd.Normalize();
            var pos = head.position + fwd * 0.4f + Vector3.down * 0.4f;
            return new List<PlacedInstance>
            {
                new PlacedInstance
                {
                    Position = pos,
                    Rotation = Quaternion.LookRotation(-fwd, Vector3.up),
                    Height = Mathf.Min(0.3f, item.Placement.HeightM > 0f ? item.Placement.HeightM : 0.15f),
                },
            };
        }

        void PerchOnHand(GeneratedObject o)
        {
            var perch = o.gameObject.AddComponent<WhatIf.Voice.HandPerch>();
            perch.head = head;
        }

        /// <summary>Gives an object its behaviour (or a new one): a BehaviourRunner with what it needs.</summary>
        void Behave(GeneratedObject o, ObjectBehaviour b)
        {
            if (!settings.behaviours || o == null) return;
            var runner = o.GetComponent<BehaviourRunner>();
            if (b == null || b.States.Count == 0) { if (runner != null) Destroy(runner); return; }
            if (runner == null)
            {
                runner = o.gameObject.AddComponent<BehaviourRunner>();
                runner.head = head;
                runner.faceUser = settings.animalsFaceUser;
                runner.floorY = o.transform.position.y; // where it stands (the floor, a table)
                runner.roomFloorY = scanner.FloorY;
                runner.ceilingY = CeilingY();
                runner.meshLayers = scanner.meshLayers;
                runner.IsLookedAt = obj => _gaze != null &&
                    _gaze.LookedAtDuring(Time.realtimeSinceStartup - 0.4f, Time.realtimeSinceStartup) == obj;
                runner.PositionOf = id =>
                {
                    foreach (var other in LiveObjects())
                        if (other.Group == id || other.InstanceId == id) return other.transform.position;
                    return null;
                };
                runner.behaviour = b;
            }
            else runner.Replace(b);
        }

        /// <summary>Triangles of all 3D models shown now (the headset's budget).</summary>
        int SceneTriangles()
        {
            long n = 0;
            foreach (var o in LiveObjects()) n += o.ModelTriangles;
            return (int)Mathf.Min(n, int.MaxValue);
        }

        /// <summary>A spot pushed out of the user's head: at least half the object's size plus 0.5 m
        /// away horizontally if it reaches up to head height (seen: a dolphin placed around the head).</summary>
        Vector3 AwayFromHead(Vector3 pos, float height, float footprint, string label)
        {
            if (head == null) return pos;
            float top = pos.y + height, bottom = pos.y;
            if (head.position.y < bottom - 0.2f || head.position.y > top + 0.2f) return pos; // below or above
            float keep = Mathf.Max(height, footprint) * 0.5f + 0.5f;
            var flat = new Vector3(pos.x - head.position.x, 0f, pos.z - head.position.z);
            if (flat.magnitude >= keep) return pos;
            var dir = flat.sqrMagnitude > 1e-4f ? flat.normalized
                : Vector3.ProjectOnPlane(head.forward, Vector3.up).normalized;
            var moved = new Vector3(head.position.x, pos.y, head.position.z) + dir * keep;
            Debug.Log($"[WhatIf] {label}: moved out of the user's head ({flat.magnitude:F2} -> {keep:F2} m)");
            return moved;
        }

        List<GeneratedObject> Spawn(AssetItem item, List<PlacedInstance> spots)
        {
            var list = new List<GeneratedObject>();
            foreach (var s in spots)
            {
                var go = new GameObject($"WhatIf {item.Label}");
                var at = item.Placement.Surface == "hand" ? s.Position
                    : AwayFromHead(s.Position, s.Height, item.Placement.FootprintM, item.Id);
                go.transform.SetPositionAndRotation(at, s.Rotation);
                bool moves = settings.behaviours && item.Behaviour != null && item.Behaviour.Moves;
                bool onHand = item.Placement.Surface == "hand";
                bool coat = item.Placement.Layout == "coat" || item.Placement.Layout == "drape";
                // anchors would pin movers; coats have up to 150 small pieces: not one anchor each
                if (settings.anchorContent && !moves && !onHand && !coat) go.AddComponent<ARAnchor>();
                var obj = go.AddComponent<GeneratedObject>();
                obj.Height = s.Height;
                obj.OnWall = s.OnWall;
                obj.Hanging = s.Hanging;
                // coat pieces lie on the surface or hang from an edge: no contact shadow of their own
                obj.ShadowStrength = settings.contactShadows && !coat ? settings.shadowStrength : 0f;
                obj.Coat = coat;
                obj.LieFlat = item.Placement.Layout == "coat";
                // a drape hangs flat against the edge, facing out (placed with forward = outwards)
                if (item.Placement.Layout == "drape") obj.OnWall = true;
                obj.Footprint = item.Placement.FootprintM > 0f ? item.Placement.FootprintM : s.Height * 0.8f;
                obj.Expect3D = settings.prefer3D && item.Want3d;
                // variation among many copies: fillers without a front may be shown mirrored
                // (never things that move: a mirrored bird flew backwards)
                obj.Mirror = item.Role == "filler" && item.Placement.Facing == "random" && !moves &&
                             item.Behaviour == null && UnityEngine.Random.value < 0.5f;
                obj.Filler = item.Role == "filler";
                obj.Group = item.Id;
                obj.InstanceId = NextInstanceId(item.Id);
                obj.Label = item.Label;
                obj.AssetPrompt = item.AssetPrompt;
                go.name = $"WhatIf {obj.InstanceId}";
                if (settings.loadingEffects) obj.BeginLoading();
                if (item.Behaviour != null) Behave(obj, item.Behaviour);
                else Debug.Log($"[WhatIf] {obj.InstanceId}: no behaviour");
                if (item.Placement.Surface == "hand") PerchOnHand(obj);
                SurfaceLook.CreateEmitter(obj, item.Emit, item.EmitHex); // a fire pit's sparks
                _spawned.Add(go);
                list.Add(obj);
            }
            return list;
        }

        /// <summary>Polls one asset and upgrades its instances: nothing -> billboard -> 3D.</summary>
        /// <param name="requested">An object the user just asked for: it gets its 3D model even when the
        /// scene's triangle budget is used up (seen: a cat added to a full jungle stayed a flat cut-out
        /// that kept turning to the user).</param>
        async Task Realise(AssetInfo asset, AssetItem item, List<GeneratedObject> instances, int round,
                           bool removeOnFailure = true, bool requested = false)
        {
            bool billboardShown = false, modelShown = false;
            // the 2D cut-out first, or only the loading effect until the 3D model is there (the cut-out
            // then only stands in if no model comes); flat things (moss, sand) always show their cut-out
            bool waitFor3D = !settings.showCutoutsFirst && settings.prefer3D && item.Want3d;
            float deadline = Time.realtimeSinceStartup + settings.assetTimeoutS;
            while (round == _round && Time.realtimeSinceStartup < deadline)
            {
                if (!billboardShown && asset.BillboardUrl != null && !waitFor3D)
                {
                    var tex = await _library.Texture(asset);
                    if (round != _round) return;
                    foreach (var o in instances)
                        if (o != null) { o.ShowBillboard(tex, asset.BillboardAspect ?? 1f, _billboardMaterial); o.Asset = asset; }
                    billboardShown = true;
                }
                if (!modelShown && settings.prefer3D && item.Want3d && asset.ModelUrl != null)
                {
                    // nearest first: if the triangle budget runs out, the far copies keep their cut-out
                    var order = new List<GeneratedObject>(instances);
                    order.RemoveAll(o => o == null);
                    order.Sort((a, b) => (a.transform.position - head.position).sqrMagnitude
                        .CompareTo((b.transform.position - head.position).sqrMagnitude));
                    int kept = 0;
                    foreach (var o in order)
                    {
                        if (o == null) continue;
                        var model = await _library.InstantiateModel(asset);
                        if (round != _round || model == null) { if (model != null) Destroy(model); break; }
                        int tris = GeneratedObject.CountTriangles(model);
                        if (!requested && settings.triangleBudget > 0 && SceneTriangles() + tris > settings.triangleBudget)
                        {
                            Destroy(model);
                            if (waitFor3D)
                            {
                                // over the budget: this copy shows its cut-out after all
                                var flat = await _library.Texture(asset);
                                if (round != _round) return;
                                o.ShowBillboard(flat, asset.BillboardAspect ?? 1f, _billboardMaterial);
                                o.Asset = asset;
                            }
                            o.EndLoading();
                            kept++;
                            continue;
                        }
                        o.ShowModel(model);
                        o.Asset = asset;
                    }
                    if (kept > 0)
                        Debug.Log($"[WhatIf] triangle budget: {kept} far {item.Label} copies stay cut-outs " +
                                  $"({SceneTriangles():N0} of {settings.triangleBudget:N0})");
                    modelShown = true;
                }
                if (!asset.Pending || modelShown) break;
                await Task.Delay(TimeSpan.FromSeconds(settings.pollIntervalS));
                if (round != _round) return;
                try { asset = await _client.GetAsset(asset.Id); }
                catch (Exception e)
                {
                    // A failed status request (timeout, server busy or restarting) must not end the wait:
                    // that used to leave objects as cut-outs for good. Keep polling until the deadline.
                    Debug.LogWarning($"[WhatIf] {item.Label}: status request failed ({e.Message}), retrying");
                    await Task.Delay(TimeSpan.FromSeconds(2));
                }
            }
            if (waitFor3D && !modelShown && !billboardShown && asset.BillboardUrl != null && round == _round)
            {
                // no 3D model came (failed or timed out): the cut-out stands in rather than nothing
                var tex = await _library.Texture(asset);
                if (round == _round)
                    foreach (var o in instances)
                        if (o != null) { o.ShowBillboard(tex, asset.BillboardAspect ?? 1f, _billboardMaterial); o.Asset = asset; }
                billboardShown = true;
            }
            // Done, failed or timed out: no object keeps "loading" forever, and objects that never got
            // anything to show (generation failed) don't stay behind as invisible placeholders.
            foreach (var o in instances)
            {
                if (o == null) continue;
                if (round == _round) o.FinishReplace(); // restyle without a new model: show the new cut-out
                o.EndLoading();
                if (!billboardShown && !modelShown && removeOnFailure && round == _round) { _spawned.Remove(o.gameObject); Destroy(o.gameObject); }
            }
            if (asset.Status == "failed")
                Debug.LogWarning($"[WhatIf] {item.Label}: {asset.Error}");
        }

        // ------------------------------------------------------------------ clearing

        void ClearContent()
        {
            foreach (var go in _spawned) if (go != null) Destroy(go);
            _spawned.Clear();
            effects?.Clear();
            StopAmbience();
            ResetEditing();
        }

        public void ClearAll()
        {
            _round++;
            _busy = false;
            ClearContent();
            _library.Clear();
            ShowPreset();
        }
    }

}
