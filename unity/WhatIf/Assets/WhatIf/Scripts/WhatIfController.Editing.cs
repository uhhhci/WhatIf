using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using WhatIf.Api;
using WhatIf.Content;
using WhatIf.Placement;
using WhatIf.Scene;
using WhatIf.Voice;

namespace WhatIf
{
    /// <summary>
    /// Editing the current scenario by voice: "make it bigger", "move the chairs to the other side of
    /// the room", "make the baby penguins blue", "delete all trees", "add a lamp next to the sofa", "undo".
    ///
    /// Nothing is hard-coded per command. The server (Claude) gets the sentence, the objects with
    /// their ids, kinds, descriptions and positions relative to the user, what the user looked at
    /// while speaking ("it", "this") and what changed last, and answers with generic operations
    /// (move, move_to, scale, rotate, face, restyle, delete, add, undo) that are applied here.
    /// </summary>
    public partial class WhatIfController
    {
        GazeTracker _gaze;
        string _scenario;
        readonly Queue<(string text, GeneratedObject gazed, LookedAtPoint spot, bool natural)> _edits =
            new Queue<(string, GeneratedObject, LookedAtPoint, bool)>();
        bool _editing;
        readonly List<string> _lastIds = new List<string>();
        readonly Dictionary<string, int> _instanceCounters = new Dictionary<string, int>();
        readonly Stack<Snapshot> _history = new Stack<Snapshot>();

        class ObjState
        {
            public GeneratedObject Obj;
            public bool Active;
            public Vector3 Position, Scale;
            public Quaternion Rotation;
            public string Label, Prompt;
            public AssetInfo Asset;
        }

        class Snapshot
        {
            public readonly List<ObjState> States = new List<ObjState>();
            public readonly HashSet<GameObject> Existing = new HashSet<GameObject>();
            public List<Effect> Effects; // all effects (levels, surface looks, tint) for undo
        }

        void StartGaze()
        {
            _gaze = GetComponent<GazeTracker>();
            if (_gaze == null) _gaze = gameObject.AddComponent<GazeTracker>(); // not ??: Unity fake-null
            _gaze.head = head;
            if (scanner != null) _gaze.meshLayers = scanner.meshLayers;
            if (effects != null) _gaze.SurfaceHit = effects.SurfaceHit;
            if (settings.pinchGrab) StartPinchGrab();
        }

        /// <summary>Reach into an object, pinch or grab, move and turn the hand: it follows (undo-able).</summary>
        void StartPinchGrab()
        {
            var grab = GetComponent<PinchGrab>();
            if (grab == null) grab = gameObject.AddComponent<PinchGrab>();
            grab.head = head;
            grab.MaxSizeM = settings.grabMaxSizeM;
            grab.GrabStarted = o =>
            {
                o.GetComponent<Falling>()?.Stop(); // caught while falling
                PushSnapshot();
                var anchor = o.GetComponent<ARAnchor>();
                if (anchor != null) DestroyImmediate(anchor); // anchors pin objects in place
            };
            grab.GrabEnded = o =>
            {
                _lastIds.Clear();
                _lastIds.Add(o.InstanceId); // "make it bigger" right after refers to it
                OpenFollowUp();
                if (settings.dropWithPhysics && !o.OnWall && !o.Hanging)
                {
                    // it falls (Unity physics on the room mesh), thrown with the hand's speed, and is
                    // anchored where it comes to rest
                    Falling.Drop(o, grab.ReleaseVelocity, scanner.FloorY,
                                 landed => { if (landed != null) MoveObject(landed, landed.transform.position, landed.transform.rotation); });
                    return;
                }
                // a floor-standing object lands on what is below it (floor, a table)
                var pos = o.transform.position;
                if (!o.OnWall && !o.Hanging &&
                    Physics.Raycast(pos + Vector3.up * 1.5f, Vector3.down, out var hit, 3f, scanner.meshLayers) &&
                    hit.collider.GetComponentInParent<GeneratedObject>() == null)
                    pos.y = hit.point.y;
                MoveObject(o, pos, o.transform.rotation);
            };
        }

        // ------------------------------------------------------------------ "there"

        /// <summary>Where in the room the user looked while saying a sentence ("put the lamp there").</summary>
        public class LookedAtPoint { public Vector3 Point, Normal; }

        LookedAtPoint _spot; // of the request being handled; target "gaze" resolves to it

        LookedAtPoint SpotDuring(float from, float to) =>
            _gaze != null && _gaze.PointDuring(from, to, out var p, out var n) ? new LookedAtPoint { Point = p, Normal = n } : null;

        /// <summary>The looked-at point in words for the planner: what it is, how far, which side.</summary>
        string DescribeSpot(LookedAtPoint spot)
        {
            if (spot == null || head == null) return null;
            float up = Vector3.Dot(spot.Normal, Vector3.up);
            float above = spot.Point.y - scanner.FloorY;
            string what = up > 0.7f ? (above > 0.3f ? $"a surface {above:0.0} m above the floor (table, shelf)" : "the floor")
                        : up < -0.7f ? "the ceiling" : "a wall";
            var flat = spot.Point - head.position; flat.y = 0;
            var fwd = head.forward; fwd.y = 0;
            float bearing = fwd.sqrMagnitude > 1e-4f && flat.sqrMagnitude > 1e-4f ? Vector3.SignedAngle(fwd, flat, Vector3.up) : 0f;
            string side = Mathf.Abs(bearing) < 15f ? "straight ahead" : bearing < 0 ? $"{-bearing:0} degrees to the left" : $"{bearing:0} degrees to the right";
            return $"{what}, {flat.magnitude:0.0} m away, {side}";
        }

        /// <summary>Where an object goes for target "gaze": the looked-at point; on a wall or the
        /// ceiling, the floor just in front of / below it.</summary>
        Vector3? SpotTop()
        {
            if (_spot == null) return null;
            var p = _spot.Point;
            float up = Vector3.Dot(_spot.Normal, Vector3.up);
            if (up < 0.7f)
            {
                // The gaze often meets a table's edge or front instead of its top (seen: a coffee cup "on the
                // table" landed on the floor beside it). Look straight down from just above the hit point,
                // slightly into the thing: a horizontal top there is where it goes.
                var probe = p - _spot.Normal * 0.05f + Vector3.up * 0.35f;
                if (Physics.Raycast(probe, Vector3.down, out var top, 0.6f, scanner.meshLayers) &&
                    Vector3.Dot(top.normal, Vector3.up) > 0.8f && top.point.y > scanner.FloorY + 0.15f &&
                    top.collider.GetComponentInParent<GeneratedObject>() == null)
                    return top.point;
                if (up > -0.7f) p += _spot.Normal * 0.3f; // a wall: 30 cm into the room
                p.y = scanner.FloorY;
            }
            return p;
        }

        // ------------------------------------------------------------------ which sentences count

        float _followUpUntil; // realtime until which sentences count without looking at the scene

        bool FollowUpOpen => Time.realtimeSinceStartup < _followUpUntil;

        /// <summary>After a command the user may go on without looking at the scene for a few seconds.</summary>
        void OpenFollowUp() => _followUpUntil = Time.realtimeSinceStartup + settings.followUpSeconds;

        /// <summary>
        /// Talk to other people must not change the scene. Besides "What if ..." a sentence counts only
        /// if the user looked at the generated scene (an object or a painted surface) while saying it,
        /// or it follows a command closely. Sentences that pass but aren't commands ("wow, nice") are
        /// dropped by the server.
        /// </summary>
        bool MeantForTheScene(float start, float end)
        {
            if (!settings.voiceNeedsGaze) return true;
            if (start <= _followUpUntil) return true; // started while the follow-up window was open
            return _gaze != null && _gaze.LookedAtSceneDuring(start - 0.5f, end);
        }

        bool HasPaint() => effects != null && effects.Paint(scanner, settings).HasPaint;

        /// <summary>Something to edit: objects or painted surfaces.</summary>
        bool HasScene() => HasObjects() || HasPaint();

        bool HasObjects()
        {
            foreach (var o in LiveObjects()) return true;
            return false;
        }

        IEnumerable<GeneratedObject> LiveObjects()
        {
            foreach (var go in _spawned)
                if (go != null && go.activeSelf && go.TryGetComponent<GeneratedObject>(out var o)) yield return o;
        }

        string NextInstanceId(string group)
        {
            _instanceCounters[group] = _instanceCounters.TryGetValue(group, out var n) ? n + 1 : 1;
            return $"{group}#{_instanceCounters[group]}";
        }

        void ResetEditing()
        {
            _edits.Clear();
            _lastIds.Clear();
            _instanceCounters.Clear();
            _history.Clear();
        }

        // ------------------------------------------------------------------ routing

        /// <summary>
        /// Continuous listening: "What if ..." / "Was wäre, wenn ..." starts a new scenario (clearing the
        /// current one); every other sentence edits the current scenario.
        /// </summary>
        void OnUtterance(string text, float start, float end)
        {
            _voiceHudShown = false; // this sentence is handled: its own status replaces the "..." banner
            var scenario = VoiceCommandListener.ExtractScenario(text);
            if (scenario != null)
            {
                if (_voice != null && _voice.IsClearCommand(scenario)) { ClearAll(); return; }
                if (HasScene())
                {
                    // "What if" no longer means "start over": with a scene shown, the editor decides from
                    // the content how much changes - "what if there was a coffee mug on my table" adds
                    // the mug and keeps the rest, "what if this were a beach" becomes a new scene
                    var looked = _gaze != null ? _gaze.LookedAtDuring(start - 1f, end) : null;
                    EnqueueEdit(text, looked, SpotDuring(start, end));
                    return;
                }
                if (_busy) ClearAll(); // a new idea replaces the one still being built
                Imagine(scenario, spot: SpotDuring(start, end));
                return;
            }
            if (!HasScene())
            {
                // Nothing shown yet: "a jungle here would be cool" can start a scene without "What if".
                // The planner decides whether it is a wish or talk to someone (only a local planner
                // sees such sentences; with Claude they are dropped on the server).
                if (settings.naturalRequests) { Imagine(text, natural: true, spot: SpotDuring(start, end)); return; }
                // without natural requests and with the gate: just talk to someone - ignore it quietly
                if (settings.voiceNeedsGaze) { hud?.Hide(); return; }
                hud?.Show($"\"{text}\"", settings.germanKeyword && !settings.englishOnly
                    ? "Start a scenario with \"What if ...\" or \"Was wäre, wenn ...\""
                    : "Start a scenario with \"What if ...\"");
                return;
            }
            bool meant = MeantForTheScene(start, end);
            if (!meant && !settings.naturalRequests)
            {
                Debug.Log("[WhatIf] voice: ignored (not looking at the scene, no follow-up)");
                hud?.Hide();
                return;
            }
            // Not looking at the objects ("hang a portrait on the wall" while looking at the wall):
            // the planner decides whether it is meant for the scene; nothing shows unless it acts.
            // "it" / "this": what was looked at during the sentence and the second before it;
            // "there": the point in the room looked at towards its end
            var gazed = _gaze != null ? _gaze.LookedAtDuring(start - 1f, end) : null;
            EnqueueEdit(text, gazed, SpotDuring(start, end), natural: !meant);
        }

        /// <summary>What the user is looking at now (typed edits: "make it red").</summary>
        GeneratedObject CurrentGaze() =>
            _gaze != null ? _gaze.LookedAtDuring(Time.realtimeSinceStartup - 1.5f, Time.realtimeSinceStartup) : null;

        void EnqueueEdit(string text, GeneratedObject gazed, LookedAtPoint spot = null, bool natural = false)
        {
            spot ??= SpotDuring(Time.realtimeSinceStartup - 1.5f, Time.realtimeSinceStartup);
            _edits.Enqueue((text, gazed, spot, natural));
            if (!_editing) ProcessEdits();
        }

        async void ProcessEdits()
        {
            _editing = true;
            try
            {
                while (_edits.Count > 0)
                {
                    var (text, gazed, spot, natural) = _edits.Dequeue();
                    _spot = spot;
                    await Edit(text, gazed, natural);
                }
            }
            finally { _editing = false; }
        }

        // ------------------------------------------------------------------ one edit

        async Task Edit(string utterance, GeneratedObject gazed, bool natural = false)
        {
            int round = _round;
            Wished();
            // a sentence that may be talk to someone shows quietly (no sticky banner)
            hud?.Show($"\"{utterance}\"", "...", sticky: !natural);
            // with the frame, real things can be referred to ("a crown on Tim's head", "next to the bin")
            var jpg = await CaptureFrame();
            if (round != _round) return;
            var surfaces = scanner.Collect(settings);
            var req = new EditRequest
            {
                Utterance = utterance,
                Natural = natural,
                EnglishOnly = settings.englishOnly,
                LookedAt = DescribeSpot(_spot),
                ImageJpegB64 = jpg != null ? Convert.ToBase64String(jpg) : null,
                Scene = scanner.Describe(surfaces, settings, _view),
                Scenario = _scenario,
                GazeId = gazed != null ? gazed.InstanceId : null,
                LastIds = new List<string>(_lastIds),
                Effects = effects != null ? effects.Current() : new List<Effect>(),
            };
            foreach (var o in LiveObjects()) req.Objects.Add(Describe(o, o == gazed));

            EditResponse response;
            try { response = await _client.Edit(req); MatchRoomLight(response.RoomLight); }
            catch (Exception e)
            {
                Debug.LogWarning($"[WhatIf] edit failed: {e.Message}");
                hud?.Show($"\"{utterance}\"", "Couldn't change that - is the server running?");
                return;
            }
            if (round != _round) return; // a new scenario started meanwhile

            if (response.Operations.Exists(o => o.Type == "clear"))
            {
                // "delete the whole scene": everything goes, objects, effects and paint
                ClearAll();
                if (!string.IsNullOrEmpty(response.Reply)) hud?.Show(response.Reply);
                return;
            }
            if (!string.IsNullOrWhiteSpace(response.NewScene))
            {
                // "now make it a jungle": not a change but a new scene
                if (_busy) ClearAll();
                Imagine(response.NewScene, spot: _spot);
                return;
            }
            if (response.Behaviours != null && response.Behaviours.Count > 0)
            {
                // "let the dog sit", "the car should drive faster"
                foreach (var ch in response.Behaviours)
                    foreach (var o in Resolve(ch.Targets)) Behave(o, ch.Behaviour);
                OpenFollowUp();
                if (response.Operations.Count == 0 && response.Effects == null)
                {
                    hud?.Show(string.IsNullOrEmpty(response.Reply) ? "Done." : response.Reply);
                    return;
                }
            }
            if (response.Effects != null && effects != null)
            {
                // "make the lava glow more", "water higher", "add fog": the new complete effect list
                PushSnapshot();
                effects.Apply(response.Effects, scanner.FloorY, head.position, settings, scanner);
                OpenFollowUp();
                if (response.Operations.Count == 0)
                {
                    hud?.Show(string.IsNullOrEmpty(response.Reply) ? "Done." : response.Reply);
                    return;
                }
            }
            if (response.Operations.Count == 0)
            {
                // not meant for the scene (or nothing to do): quietly drop it
                if (!natural && !string.IsNullOrEmpty(response.Reply)) hud?.Show(response.Reply); else hud?.Hide();
                return;
            }
            if (!response.Operations.TrueForAll(op => op.Type == "undo")) PushSnapshot();

            var touched = new List<string>();
            foreach (var op in response.Operations)
            {
                try { Apply(op, surfaces, touched, round); }
                catch (Exception e) { Debug.LogException(e); }
            }
            if (touched.Count > 0) { _lastIds.Clear(); _lastIds.AddRange(touched); }
            OpenFollowUp();
            hud?.Show(string.IsNullOrEmpty(response.Reply) ? "Done." : response.Reply);
        }

        SceneObject Describe(GeneratedObject o, bool lookedAt)
        {
            var to = o.transform.position - head.position; to.y = 0;
            var gaze = Vector3.ProjectOnPlane(head.forward, Vector3.up);
            float s = o.transform.lossyScale.y;
            return new SceneObject
            {
                Id = o.InstanceId,
                Group = o.Group,
                Label = o.Label,
                AssetPrompt = o.AssetPrompt,
                BearingDeg = gaze.sqrMagnitude > 1e-4f && to.sqrMagnitude > 1e-4f ? Vector3.SignedAngle(gaze, to, Vector3.up) : 0f,
                DistanceM = to.magnitude,
                HeightM = o.Height * s,
                FootprintM = o.Footprint * o.transform.lossyScale.x,
                HeightAboveFloorM = Mathf.Max(0f, o.transform.position.y - scanner.FloorY),
                LookedAt = lookedAt,
            };
        }

        List<GeneratedObject> Resolve(List<string> ids)
        {
            var list = new List<GeneratedObject>();
            foreach (var id in ids)
                foreach (var o in LiveObjects())
                    if (o.InstanceId == id && !list.Contains(o)) list.Add(o);
            return list;
        }

        /// <summary>A solver that knows the current objects as targets; those being moved aren't obstacles.</summary>
        PlacementSolver SolverFor(List<Surface> surfaces, ICollection<GeneratedObject> moving)
        {
            var solver = new PlacementSolver(surfaces, scanner, head) { ImageTopOf = ImageTopOf(surfaces) };
            foreach (var o in LiveObjects())
                solver.RegisterExisting(o.InstanceId, o.Group, o.transform.position,
                    Mathf.Max(0.1f, o.Footprint * o.transform.lossyScale.x * 0.55f), o.Height * o.transform.lossyScale.y,
                    obstacle: !o.Coat && (moving == null || !moving.Contains(o))); // moss is no obstacle
            return solver;
        }

        // ------------------------------------------------------------------ operations

        void Apply(EditOp op, List<Surface> surfaces, List<string> touched, int round)
        {
            var objs = Resolve(op.Targets);
            foreach (var o in objs) touched.Add(o.InstanceId);
            switch (op.Type)
            {
                case "move":
                {
                    var right = Flat(head.right);
                    var forward = Flat(head.forward);
                    foreach (var o in objs)
                        MoveObject(o, o.transform.position + right * op.DxRightM + Vector3.up * op.DyUpM + forward * op.DzForwardM,
                                   o.transform.rotation);
                    break;
                }
                case "move_to": MoveTo(op, objs, surfaces); break;
                case "scale":
                    foreach (var o in objs)
                    {
                        var scaled = o.transform.localScale * Mathf.Clamp(op.ScaleFactor, 0.1f, 10f);
                        o.transform.localScale = Vector3.one * Mathf.Clamp(scaled.y, 0.05f, 20f);
                    }
                    break;
                case "rotate":
                    foreach (var o in objs)
                        MoveObject(o, o.transform.position, Quaternion.Euler(0f, op.RotateDeg, 0f) * o.transform.rotation);
                    break;
                case "tip":
                    foreach (var o in objs) Tip(o, op.RotateDeg);
                    break;
                case "face": Face(op, objs, surfaces); break;
                case "restyle": Restyle(op, objs, round); break;
                case "delete":
                    foreach (var o in objs) o.gameObject.SetActive(false); // kept for undo
                    break;
                case "add": Add(op, surfaces, touched, round); break;
                case "paint":
                    if (effects == null) break;
                    var paint = effects.Paint(scanner, settings);
                    foreach (var surface in op.Targets) paint.Set(surface, op.ColorHex);
                    break;
                case "undo": Undo(round); break;
                default: Debug.LogWarning($"[WhatIf] unknown edit operation {op.Type}"); break;
            }
        }

        static Vector3 Flat(Vector3 v)
        {
            v.y = 0f;
            return v.sqrMagnitude > 1e-6f ? v.normalized : Vector3.forward;
        }

        void MoveTo(EditOp op, List<GeneratedObject> objs, List<Surface> surfaces)
        {
            if (objs.Count == 0) return;
            if (!string.IsNullOrEmpty(op.TargetId) && op.DistanceM <= 0f)
            {
                // on / beside / around another object or a surface: let the placement solver find spots
                var first = objs[0];
                var placement = new Api.Placement
                {
                    Surface = op.Relation == "on" ? "horizontal" : "floor",
                    Layout = op.Relation == "around" ? "around" : op.Relation == "beside" ? "beside"
                           : op.Relation == "under" ? "under" : "scatter",
                    Facing = op.Relation == "around" ? "toward_target" : "random",
                    TargetId = op.TargetId,
                    Count = objs.Count,
                    HeightM = first.Height * first.transform.lossyScale.y,
                    FootprintM = first.Footprint * first.transform.lossyScale.x,
                    MinSpacingM = first.Footprint * first.transform.lossyScale.x,
                    MaxDistanceM = 20f,
                };
                var spots = SolverFor(surfaces, objs).Place(placement);
                for (int i = 0; i < objs.Count && i < spots.Count; i++)
                {
                    if (op.Relation == "under")
                    {
                        // shrunk to fit below the table
                        float now = objs[i].Height * objs[i].transform.lossyScale.y;
                        if (now > 0.01f && spots[i].Height < now) objs[i].transform.localScale *= spots[i].Height / now;
                    }
                    MoveObject(objs[i], spots[i].Position, op.Relation == "around" ? spots[i].Rotation : objs[i].transform.rotation);
                }
                if (spots.Count < objs.Count)
                    Debug.LogWarning($"[WhatIf] move_to {op.TargetId}: only {spots.Count} of {objs.Count} found a spot");
                return;
            }
            // a direction + distance from the user ("the other side of the room", "next to me")
            var dir = Quaternion.Euler(0f, op.BearingDeg, 0f) * Flat(head.forward);
            var centre = head.position + dir * Mathf.Max(0.6f, op.DistanceM);
            centre.y = scanner.FloorY;
            float spacing = 0f;
            foreach (var o in objs) spacing = Mathf.Max(spacing, o.Footprint * o.transform.lossyScale.x * 1.1f);
            float ring = objs.Count > 1 ? spacing * objs.Count / (2f * Mathf.PI) + spacing * 0.5f : 0f;
            for (int i = 0; i < objs.Count; i++)
            {
                float a = i * 2f * Mathf.PI / objs.Count;
                var at = centre + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * ring;
                MoveObject(objs[i], at, objs[i].transform.rotation);
            }
        }

        void Face(EditOp op, List<GeneratedObject> objs, List<Surface> surfaces)
        {
            var solver = op.Facing == "toward_target" ? SolverFor(surfaces, objs) : null;
            foreach (var o in objs)
            {
                var pos = o.transform.position;
                Vector3? look = op.Facing switch
                {
                    "toward_user" => head.position,
                    "toward_target" => solver.CenterOf(op.TargetId, pos),
                    _ => null,
                };
                Quaternion rot;
                if (look.HasValue)
                {
                    var d = look.Value - pos; d.y = 0f;
                    if (d.sqrMagnitude < 1e-4f) continue;
                    rot = Quaternion.LookRotation(d.normalized, Vector3.up);
                }
                else rot = Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f);
                MoveObject(o, pos, rot);
            }
        }

        void Restyle(EditOp op, List<GeneratedObject> objs, int round)
        {
            if (op.Asset == null || objs.Count == 0) return;
            foreach (var o in objs)
            {
                if (!string.IsNullOrEmpty(op.Label)) o.Label = op.Label;
                if (!string.IsNullOrEmpty(op.AssetPrompt)) o.AssetPrompt = op.AssetPrompt;
                o.BeginReplace(settings.loadingEffects);
            }
            var item = new AssetItem { Id = objs[0].Group, Label = objs[0].Label, Want3d = objs[0].Expect3D };
            _ = Realise(op.Asset, item, objs, round, removeOnFailure: false);
        }

        void Add(EditOp op, List<Surface> surfaces, List<string> touched, int round)
        {
            if (op.Item == null || op.Asset == null) return;
            var solver = SolverFor(surfaces, null);
            var spots = op.Item.Placement.Surface == "hand" ? HandSpot(op.Item) : solver.Place(op.Item.Placement, op.Item.Id);
            if (op.Item.Placement.Surface != "hand" && spots.Count < op.Item.Placement.Count)
            {
                // not all fit with the planned spacing (seen: "more trees" in a corridor added one):
                // the rest a bit closer together
                var tighter = Newtonsoft.Json.JsonConvert.DeserializeObject<Api.Placement>(Newtonsoft.Json.JsonConvert.SerializeObject(op.Item.Placement));
                tighter.Count = op.Item.Placement.Count - spots.Count;
                tighter.MinSpacingM = 0f;
                tighter.FootprintM *= 0.5f;
                var more = solver.Place(tighter, op.Item.Id + "+");
                Debug.Log($"[WhatIf] {op.Item.Id}: {spots.Count} of {op.Item.Placement.Count} fit, {more.Count} more closer together");
                spots.AddRange(more);
            }
            if (spots.Count == 0)
            {
                hud?.Show(op.Item.Label, "Found no place for it - look around to scan more.");
                return;
            }
            var instances = Spawn(op.Item, spots);
            foreach (var o in instances) touched.Add(o.InstanceId);
            _ = Realise(op.Asset, op.Item, instances, round, requested: true);
        }

        /// <summary>Moves an object. AR anchors pin objects in place, so the anchor is replaced.</summary>
        /// <summary>"Lying on its back" (180), "on its side" (90): the same object laid down, turned
        /// around its own middle and set back onto what it stood on. Side-view models (front = +X)
        /// roll around their length; front-facing ones fall backwards (180) or sideways (90).</summary>
        void Tip(GeneratedObject o, float deg)
        {
            if (Mathf.Abs(deg) < 1f) return;
            bool sideView = o.AssetPrompt != null && o.AssetPrompt.ToLowerInvariant().Contains("facing right");
            var axis = sideView || Mathf.Abs(Mathf.Abs(deg) - 180f) < 1f ? o.transform.right : o.transform.forward;
            if (!WorldBounds(o, out var before)) before = new Bounds(o.transform.position + Vector3.up * o.Height * 0.5f, Vector3.one * o.Height);
            var q = Quaternion.AngleAxis(deg, axis);
            var runner = o.GetComponent<BehaviourRunner>();
            if (runner != null) runner.Lay(q); // its turning toward the user keeps the new pose
            var anchor = o.GetComponent<ARAnchor>();
            if (anchor != null) DestroyImmediate(anchor);
            var center = before.center;
            o.transform.SetPositionAndRotation(center + q * (o.transform.position - center), q * o.transform.rotation);
            if (WorldBounds(o, out var after)) o.transform.position += Vector3.up * (before.min.y - after.min.y);
            if (settings.anchorContent && runner == null) StartCoroutine(AnchorNextFrame(o.gameObject));
        }

        static bool WorldBounds(GeneratedObject o, out Bounds b)
        {
            b = default;
            bool any = false;
            foreach (var r in o.GetComponentsInChildren<Renderer>())
            {
                if (!r.enabled || !r.gameObject.activeInHierarchy || r is ParticleSystemRenderer || r.name == "contact shadow") continue;
                if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds);
            }
            return any;
        }

        void MoveObject(GeneratedObject o, Vector3 position, Quaternion rotation)
        {
            var anchor = o.GetComponent<ARAnchor>();
            if (anchor != null) DestroyImmediate(anchor);
            o.transform.SetPositionAndRotation(position, rotation);
            if (settings.anchorContent) StartCoroutine(AnchorNextFrame(o.gameObject));
        }

        static IEnumerator AnchorNextFrame(GameObject go)
        {
            yield return null;
            if (go != null && go.GetComponent<ARAnchor>() == null) go.AddComponent<ARAnchor>();
        }

        // ------------------------------------------------------------------ undo

        void PushSnapshot()
        {
            var snap = new Snapshot();
            foreach (var go in _spawned)
            {
                if (go == null || !go.TryGetComponent<GeneratedObject>(out var o)) continue;
                snap.Existing.Add(go);
                snap.States.Add(new ObjState
                {
                    Obj = o, Active = go.activeSelf, Position = go.transform.position, Rotation = go.transform.rotation,
                    Scale = go.transform.localScale, Label = o.Label, Prompt = o.AssetPrompt, Asset = o.Asset,
                });
            }
            if (effects != null) snap.Effects = effects.Current();
            _history.Push(snap);
            if (_history.Count > 30)
            {
                var keep = _history.ToArray(); // newest first
                _history.Clear();
                for (int i = Mathf.Min(keep.Length, 30) - 1; i >= 0; i--) _history.Push(keep[i]);
            }
        }

        void Undo(int round)
        {
            if (_history.Count == 0) { hud?.Show("Nothing to undo"); return; }
            var snap = _history.Pop();
            if (snap.Effects != null && effects != null) effects.Apply(snap.Effects, scanner.FloorY, head.position, settings, scanner);
            // objects added since then go away
            for (int i = _spawned.Count - 1; i >= 0; i--)
            {
                var go = _spawned[i];
                if (go != null && !snap.Existing.Contains(go)) { Destroy(go); _spawned.RemoveAt(i); }
            }
            foreach (var st in snap.States)
            {
                if (st.Obj == null) continue;
                st.Obj.gameObject.SetActive(st.Active);
                MoveObject(st.Obj, st.Position, st.Rotation);
                st.Obj.transform.localScale = st.Scale;
                st.Obj.Label = st.Label;
                st.Obj.AssetPrompt = st.Prompt;
                if (st.Asset != null && (st.Obj.Asset == null || st.Obj.Asset.Id != st.Asset.Id))
                {
                    // the previous look is cached: switching back is quick
                    st.Obj.BeginReplace(false);
                    var item = new AssetItem { Id = st.Obj.Group, Label = st.Label, Want3d = st.Obj.Expect3D };
                    _ = Realise(st.Asset, item, new List<GeneratedObject> { st.Obj }, round, removeOnFailure: false);
                }
            }
        }
    }
}
