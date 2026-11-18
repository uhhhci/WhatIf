using UnityEngine;

namespace WhatIf
{
    [CreateAssetMenu(menuName = "What If/Settings", fileName = "WhatIfSettings")]
    public class WhatIfSettings : ScriptableObject
    {
        [Header("Server")]
        [Tooltip("LAN address of the PC running the generation server, e.g. http://192.168.0.42:8000")]
        public string serverUrl = "http://192.168.0.42:8000";
        public float pollIntervalS = 0.5f;
        public float assetTimeoutS = 300f;

        [Header("Scene")]
        [Tooltip("Assumed head height above ground when no floor surface is detected (e.g. outdoors).")]
        public float defaultHeadHeightM = 1.65f;
        [Tooltip("Send a camera frame to the planner so it can see the place.")]
        public bool sendCameraFrame = true;
        [Tooltip("On the headset, tell the server where each detected surface appears in the camera image, " +
                 "so it can number them in the photo and the planner can link \"that table\" to a surface.")]
        public bool markSurfacesInImage = true;
        public int cameraFrameMaxSize = 768;

        [Header("Content")]
        [Tooltip("Swap billboards for 3D models when the server delivers one.")]
        public bool prefer3D = true;
        [Tooltip("Material used for 2D cut-outs. Use an unlit, alpha-clipped URP material (see README). " +
                 "If empty, a URP/Unlit material is created at runtime.")]
        public Material billboardMaterial;
        [Tooltip("Material for the water_level effect. If empty, a simple transparent material is created.")]
        public Material waterMaterial;
        [Tooltip("Anchor generated content with AR anchors so it stays put while you walk.")]
        public bool anchorContent = true;
        [Tooltip("Soft dark ellipse under floor and table objects so they look grounded. On Magic Leap 2 " +
                 "it is only visible with segmented dimming enabled (dark = transparent on an additive display).")]
        public bool contactShadows = true;
        [Tooltip("3D models cast a shadow onto the real floor, in the direction of the room's main light (Magic Leap " +
                 "light estimation); visible through segmented dimming. The objects themselves keep standard lighting.")]
        public bool castShadows = true;
        [Tooltip("Show each object's 2D cut-out as soon as its picture is ready, until the 3D model replaces it. " +
                 "Off: only the loading effect until the 3D model is there (the cut-out then only stands in if no " +
                 "model comes). Flat things like moss or sand patches always show their cut-out.")]
        public bool showCutoutsFirst = false;
        [Tooltip("Every text in the headset in English, whatever language is spoken (the planner's answers, " +
                 "hints and examples). Off: answers come in the language of the sentence, and the German " +
                 "keyword is shown next to \"What if\". Speaking German still works either way.")]
        public bool englishOnly = true;
        [Tooltip("Before the first wish, the headset invites with \"Say 'What if ...'\" and an example that changes " +
                 "every few seconds. Off: no invitation or examples are shown.")]
        public bool showSuggestions = true;
        [Tooltip("Animals keep turning to face the user (while idle and after coming to the user). Off: they turn " +
                 "only to where they walk.")]
        public bool animalsFaceUser = false;
        [Range(0f, 1f)] public float castShadowStrength = 0.6f;
        [Tooltip("Turn the scene's light to where the room's main light comes from (Magic Leap light estimation, " +
                 "a few seconds after each scene is built). Off: the light keeps the direction set under Lighting.")]
        public bool lightEstimation = true;
        [Tooltip("The estimated main light's luminance that counts as normal room light (strength x1). The log " +
                 "shows each estimate's luminance: set this to a typical value for a normally lit room.")]
        public float lightEstimateReference = 0.04f; // measured 0.038 in the lab office (normal light)
        [Tooltip("Particles at each spot while its object is generated: a rising column until the cut-out " +
                 "appears, then a shimmer until the 3D model replaces it.")]
        public bool loadingEffects = true;
        [Range(0f, 1f)] public float shadowStrength = 0.5f;

        [Header("Voice")]
        [Tooltip("Listen for spoken prompts that start with \"What if\". The keyword is detected offline on " +
                 "the device (Vosk); only the sentence after it is sent to the server for transcription.")]
        public bool enableVoice = true;
        [Tooltip("Magic Leap 2: dim the real world behind virtual content (segmented dimming), so models, " +
                 "shadows and painted walls look solid. Needs the Magic Leap SDK's rendering extensions " +
                 "feature enabled in OpenXR. No effect in the Editor.")]
        public bool segmentedDimming = true;
        [Tooltip("Listen to everything (transcribed locally on the PC): \"What if ...\" / \"Was wäre, wenn ...\" " +
                 "starts a scenario when nothing is shown; with a scene shown, the content decides how much changes " +
                 "(\"what if there was a mug on my table\" adds a mug, \"what if this were a beach\" starts over, " +
                 "\"make it bigger\" edits). Off: only sentences starting with the keyword are used (offline keyword spotting).")]
        public bool continuousListening = true;
        [Tooltip("Only the wearer's voice: the wearer's mouth is a few centimetres from the microphone, other people " +
                 "a metre or more, so they arrive much quieter. The headset learns the wearer's level from their " +
                 "\"What if\" sentences and drops much quieter speech before it is sent anywhere.")]
        public bool ownVoiceOnly = true;
        [Tooltip("How much quieter than the wearer's usual level speech may be and still count (dB). Smaller = " +
                 "stricter (others are dropped more reliably, a soft-spoken wearer too). The headset log shows each " +
                 "sentence's level.")]
        [Range(3f, 30f)] public float ownVoiceMarginDb = 12f;
        [Tooltip("Ignore talk that isn't meant for the app: a sentence counts only if it starts with \"What if\" / " +
                 "\"Was wäre, wenn\", the user looks at a generated object or painted surface while saying it, or it " +
                 "follows a command within Follow Up Seconds. Off: every sentence is used.")]
        public bool voiceNeedsGaze = true;
        [Tooltip("Most triangles of 3D models shown at once (Magic Leap 2: about 1-1.5 million keeps it smooth). " +
                 "Beyond it the farthest copies keep their cut-out. 0 = no limit.")]
        public int triangleBudget = 1200000;
        [Tooltip("Real walls hide virtual content behind them: straight planes estimated from the room mesh. Off: no " +
                 "occlusion at all, everything is drawn regardless of walls (walls are still estimated for placement).")]
        public bool occlusion = true;
        [Tooltip("Also let furniture and other real things hide virtual objects (coarse room mesh). Off: only walls.")]
        public bool occludeFurniture = false;
        [Tooltip("Use the ML2 depth camera to find things the planner points at in the camera picture " +
                 "(people the room mesh hasn't caught). Placement only. Headset only.")]
        public bool depthCamera = true;
        [Tooltip("Light generated content like the room (brightness and colour of the camera picture).")]
        public bool matchRoomLight = false;
        [Tooltip("Direct hand interaction: reach into an object and pinch or close the hand - move the hand to move " +
                 "it, turn the hand to turn it; with both hands: turn and scale. Needs the Hand Interaction Profile " +
                 "in the OpenXR features (every build switches it on).")]
        public bool pinchGrab = true;
        [Tooltip("Only objects up to this size (largest side) can be picked up by hand; bigger ones (trees, ruins) " +
                 "stay put and are changed by voice.")]
        [Range(0.3f, 10f)] public float grabMaxSizeM = 2f;
        [Tooltip("A let-go object falls with physics onto the room (and flies on when thrown) instead of snapping " +
                 "onto the surface below it. Wall and hanging items stay where they are let go.")]
        public bool dropWithPhysics = true;
        [Tooltip("Play generated sounds: the scene's ambience and a few object sounds (3D, from the object).")]
        public bool sounds = true;
        [Tooltip("Run the behaviours the planner writes (animals walk up to you, cars drive, plants sway).")]
        public bool behaviours = true;
        [Range(0f, 1f)] public float ambienceVolume = 0.3f;
        [Range(0f, 1f)] public float objectSoundVolume = 0.7f;
        [Tooltip("While nothing is shown, a sentence without \"What if\" can start a scene (\"a jungle here " +
                 "would be cool\"): the planner decides whether it is a wish or talk to someone. Only works with " +
                 "the local planner (with Claude such sentences are not sent).")]
        public bool naturalRequests = true;
        [Tooltip("After a command, further sentences count without looking at the scene for this long " +
                 "(\"make it bigger\" ... \"a bit more\"). Shown in the HUD.")]
        public float followUpSeconds = 8f;
        [Tooltip("Folder in StreamingAssets with the English Vosk model (keyword \"What if\").")]
        public string voskModelFolder = "vosk-model-small-en-us-0.15";
        [Tooltip("Also listen for the German keyword \"Was wäre wenn\"; the sentence is then transcribed in German.")]
        public bool germanKeyword = true;
        [Tooltip("Folder in StreamingAssets with the German Vosk model.")]
        public string voskGermanModelFolder = "vosk-model-small-de-0.15";
        [Tooltip("Seconds of silence that end a spoken prompt.")]
        public float voiceEndSilenceS = 1.0f;
        [Tooltip("Longest spoken prompt in seconds.")]
        public float voiceMaxSeconds = 12f;
        [Tooltip("Spoken prompts containing one of these clear the scene instead of imagining something.")]
        public string[] clearPhrases = { "empty again", "as it was", "back to normal", "clear everything" };

        [Header("Lighting (for lit 3D models, e.g. SPAR3D)")]
        [Tooltip("Set the scene's lighting at start. 3D models from SPAR3D have light, shading-free colours " +
                 "and look washed out under Unity's default lighting (sun 1.0 + bright sky ambient). " +
                 "Also adds a light if the scene has none, which lit models need on the headset.")]
        public bool controlLighting = true;
        [Range(0f, 2f)] public float sunIntensity = 0.7f;
        [Tooltip("Direction of the sun (X = height above the horizon, Y = compass direction).")]
        public Vector2 sunAngles = new Vector2(50f, -30f);
        [Tooltip("Uniform ambient light instead of the sky colour; keeps shadowed sides from going black.")]
        public Color ambientColor = new Color(0.35f, 0.35f, 0.38f);

        [Header("Editor")]
        [Tooltip("Show a 1 m grid floor at the assumed ground height in Play mode without a headset.")]
        public bool editorGround = true;

        [Header("Presets (bumper cycles through them)")]
        public string[] presetPrompts =
        {
            "What if this place had more plants?",
            "What if this place had fewer cars?",
            "What if this place were flooded?",
            "What if this place were inhabited by dinosaurs?",
        };
    }
}
