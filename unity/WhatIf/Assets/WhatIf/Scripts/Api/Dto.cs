// Mirrors server/whatif/schemas.py. Serialized with Newtonsoft.Json + snake_case naming.
using System.Collections.Generic;
using Newtonsoft.Json;

namespace WhatIf.Api
{
    public class PlaneInfo
    {
        public string Id;
        public string Label;          // floor | wall | ceiling | table | seat | unknown
        public float WidthM;
        public float HeightM;
        public float DistanceM;
        public float HeightRelHeadM;
        public float? BearingDeg;        // direction from the gaze, + = right
        public float? HeightAboveFloorM;
        public float? ViewX, ViewY;      // where the centre appears in the camera image (0..1, top left)
    }

    public class SceneContext
    {
        public List<PlaneInfo> Planes = new List<PlaneInfo>();
        public float? HeadHeightM;
        public float? MeshExtentM;
    }

    public class PlanRequest
    {
        public string Prompt;
        public bool EnglishOnly;      // every text the server writes for the HUD in English
        public SceneContext Scene;
        public string ImageJpegB64;
        public bool Generate = true;
        public bool Natural;         // said without "What if" while nothing is shown: the planner decides
        public string LookedAt;      // where the user looked while speaking, in words (target "gaze")
    }

    public class Placement
    {
        public string Surface;        // floor | wall | ceiling | table | horizontal | any
        public string Layout;         // scatter | row | edge | single
        public string Facing;         // random | toward_user | back_to_wall | toward_target
        public string TargetId;       // a detected plane's id or another item's id (around / beside / on)
        public int Count;
        public float HeightM;
        public float FootprintM;      // largest horizontal size of one instance (0 = unknown)
        public float HeightJitter;
        public float MinSpacingM;
        public float MaxDistanceM;
        public string Density;        // "" | sparse | normal | dense | very_dense: count from the free area
    }

    public class Transition
    {
        public string Event;         // user_looks | user_near | arrived | timer
        public float Value;
        public string Goto;
    }

    public class BehaviourState
    {
        public string Name;
        public string Move = "none"; // none | wander | to_user | follow_user | flee_user | circle | to_target
        public string TargetId;
        public string Gait = "glide"; // glide | roll | hop | trot | waddle | slither | fly | swim
        public float Speed = 0.5f;
        public List<string> Idle = new List<string>();
        public List<Transition> Next = new List<Transition>();
    }

    public class ObjectBehaviour
    {
        public List<BehaviourState> States = new List<BehaviourState>();

        public bool Moves => States.Exists(s => s.Move != "none");
    }

    public class BehaviourChange
    {
        public List<string> Targets = new List<string>();
        public ObjectBehaviour Behaviour;
    }

    public class AssetItem
    {
        public string Id;
        public string Label;
        public string AssetPrompt;
        public Placement Placement;
        [JsonProperty("want_3d")] public bool Want3d;
        public string Role = "hero";  // hero | filler (many small things: lighter model, may be mirrored)
        public string Emit = "none";  // particles the object gives off: none | rise | fall | float (a fire pit)
        public string EmitHex;
        public ObjectBehaviour Behaviour;   // living things and machines: a small state machine (BehaviourRunner)
        public List<string> Parts = new List<string>(); // moving parts ("tail:wag"), split on the server
    }

    public class Effect
    {
        public string Type;           // surface | level | tint (older: surface_paint, water_level)
        public float? WaterHeightM;   // level: height above the floor
        public string TintHex;        // main colour
        public float Strength;        // opacity
        public List<string> Surfaces = new List<string>(); // surface: surface ids or kinds ("wall")
        // building blocks of the look (WhatIf/Surface shader)
        public List<string> Colors = new List<string>();
        public string Pattern = "none";
        public float PatternScaleM = 0.5f;
        public string Motion = "still";
        public float Speed = 0.2f;
        public float Glow;
        public string GlowHex;
        public float Reflect;
        public string Edge = "none";
        public string EdgeHex;
        public string Particles = "none";
        public string ParticleHex;
        public float ParticleAmount = 0.3f;

        public Effect CopyLook(string type)
        {
            var e = (Effect)MemberwiseClone();
            e.Type = type;
            e.Surfaces = new List<string>();
            e.Colors = new List<string>(Colors);
            return e;
        }
    }

    public class ScenePlan
    {
        public bool Request = true;  // false: the sentence wasn't a wish for a change (talk to someone)
        public string Scope;         // single | environment
        public string Interpretation;
        public string Realism;
        public List<AssetItem> Items = new List<AssetItem>();
        public List<Effect> Effects = new List<Effect>();
        public string NoteForUser;
    }

    public class AssetInfo
    {
        public string Id;
        public string Prompt;
        public string Status;         // queued | running | billboard_ready | done | failed
        public string BillboardUrl;
        public float? BillboardAspect;
        public string ModelUrl;
        public bool Pending;
        public string Error;
    }

    /// <summary>One line of /plan/stream: "item" (an object as soon as it is planned), "done" (the whole
    /// answer, as /plan gives it) or "error".</summary>
    public class PlanStreamEvent
    {
        public string Event;
        public AssetItem Item;
        public AssetInfo Asset;
        public string Interpretation;
        public PlanResponse Response;
        public string Detail;
    }

    public class PlanResponse
    {
        public string RoomLight;          // average colour of the camera picture (from the server)
        public ScenePlan Plan;
        public Dictionary<string, AssetInfo> Assets = new Dictionary<string, AssetInfo>();
        public Dictionary<string, SoundInfo> Sounds = new Dictionary<string, SoundInfo>(); // item id or "ambience"
    }

    public class SoundInfo
    {
        public string Id;
        public string Prompt;
        public bool Loop = true;
        public string Status;       // queued | done | failed
        public string Url;
    }

    // ------------------------------------------------------------------ editing by voice

    public class SceneObject
    {
        public string Id;            // instance id, e.g. "chair#2"
        public string Group;         // item id shared by all instances of one kind
        public string Label;
        public string AssetPrompt;
        public float BearingDeg;
        public float DistanceM;
        public float HeightM;
        public float FootprintM;
        public float HeightAboveFloorM;
        public bool LookedAt;
    }

    public class EditRequest
    {
        public bool EnglishOnly;      // every text the server writes for the HUD in English
        public bool Natural;          // said while not looking at the scene: the planner decides
        public string Utterance;
        public string ImageJpegB64;      // camera frame, so real things in view can be referred to
        public string LookedAt;          // where the user looked while speaking ("there"), in words
        public SceneContext Scene;
        public List<SceneObject> Objects = new List<SceneObject>();
        public string GazeId;
        public List<string> LastIds = new List<string>();
        public string Scenario;
        public List<Effect> Effects = new List<Effect>();
        public bool Generate = true;
    }

    public class EditOp
    {
        public string Type;          // move | move_to | scale | rotate | face | restyle | delete | add | undo
        public List<string> Targets = new List<string>();
        public float DxRightM, DyUpM, DzForwardM;
        public float BearingDeg, DistanceM;
        public string TargetId;
        public string Relation;      // none | on | beside | around
        public float ScaleFactor = 1f;
        public float RotateDeg;
        public string Facing;        // none | toward_user | toward_target | random
        public string AssetPrompt;
        public string Label;
        public AssetItem Item;
        public string ColorHex;      // paint: "#3050c0", "none" = remove
        public AssetInfo Asset;      // restyle / add: generation already started by the server
    }

    public class EditResponse
    {
        public string RoomLight;          // average colour of the camera picture (from the server)
        public string Reply;
        public List<EditOp> Operations = new List<EditOp>();
        public string NewScene;      // non-empty: start this as a new scene ("now make it a jungle")
        public List<Effect> Effects; // non-null: replaces the effects shown ("make the lava glow more")
        public List<BehaviourChange> Behaviours = new List<BehaviourChange>(); // "let the dog sit"
    }

    /// <summary>GET /health: is the server up, and has it finished loading its models?</summary>
    public class HealthInfo
    {
        public bool Ok;
        public bool Ready = true;
        public List<string> Loading = new List<string>();
        public string Models;        // loading | ready | failed
    }

    public class TranscribeResult
    {
        public string Text;
        public string Language;
        public bool Speech = true;   // false: silence, noise or a Whisper phantom phrase
    }
}
