using System;
using System.Collections;
using System.Collections.Generic;
using ShoalingUpstream.Audio;
using ShoalingUpstream.Config;
using ShoalingUpstream.Control;
using ShoalingUpstream.Journey;
using ShoalingUpstream.Localization;
using UnityEngine;
using UnityEngine.Networking;
#if NSDK_PRESENT
using ShoalingUpstream.Localization.Nsdk;
#endif

namespace ShoalingUpstream.Simulation
{
    /// <summary>
    /// The desk. Everything the piece is, wired together and driven by the browser's scrubber.
    ///
    /// This exists because the artwork is made at a laptop and performed in a creek, and until
    /// now the only way to see the Unity half was to read a test report. It is glue and a heads-up
    /// display and nothing else: every decision worth arguing about is in Runtime/, which this
    /// only assembles.
    ///
    /// It lives outside Runtime/ deliberately. Nothing here should ship on a phone — the phone's
    /// entry point localizes against VPS and has no reason to draw a debug overlay or spawn a
    /// sphere at every beat.
    /// </summary>
    [AddComponentMenu("Shoaling Upstream/Simulation Driver")]
    public sealed class SimulationDriver : MonoBehaviour
    {
        /// <summary>Spawn: plants copies of a Timeline-driven object and plays them. ReactFlock:
        /// reaches into whatever an earlier Spawn beat left standing and animates it in place —
        /// there is nothing here for a Timeline to be bound to, since the target list is only
        /// known at fire time. SwimToEye: reaches into a flock left wherever an earlier beat
        /// planted it — real-world coordinates the operator has since walked away from — and
        /// carries it to wherever the eye is aimed now. Appear: plants copies of a plain
        /// prefab and plays whatever Legacy Animation clip it already defaults to — for
        /// something that just shows up and does its own thing, with no Timeline and no fall.
        /// EatAndGrow: destroys one flock outright and grows another in place, no swap, no
        /// shake — for "that disappeared, and these got bigger because of it". PlayClip: reaches
        /// into an already-standing flock (typically one Appear planted) and plays a clip or
        /// Animator state on it in place — no spawn, no destroy, no re-registration — for a
        /// second gesture on something already on screen, like the heron lowering its head.
        /// Despawn: destroys a whole flock outright and nothing else — for a beat that just
        /// clears something off screen, no growth or hand-off involved. Rotate: turns a whole
        /// flock some number of degrees around world Y, instantly, and nothing else — unlike
        /// PlayClip's own RotateYDegrees this does not touch any clip, so an already-looping
        /// animation (a fish's own swim loop) keeps looping through the turn instead of being
        /// switched to a one-shot clip and left frozen once it finishes. Jump: every member of a
        /// flock leaps up and lands back where it started, independently staggered in both when
        /// it starts and how high it goes. Cull: keeps a random handful of a flock's members and
        /// fades every other one to nothing over time — the survivors stay registered under
        /// this beat's own id, the same handoff every other flock-consuming beat uses. Stunt: the
        /// first member of a flock only — tilts its Rotation X to a target value, speeds up
        /// whatever animation is already playing on it for a while, then reverses both. SwimFlock:
        /// every member of a flock eases forward along its own current facing by a fixed
        /// distance, in place — like Rotate, no swap, no re-registration — independently
        /// staggered in when it starts (reuses RotateStaggerMaxSeconds).</summary>
        public enum SceneTriggerKind
        {
            Spawn, ReactFlock, SwimToEye, Appear, EatAndGrow, PlayClip, Despawn, Rotate, Jump, Cull, Stunt, LieDownFade, SwimFlock,
        }

        /// <summary>One step of a PlayClip trigger's sequence.</summary>
        [Serializable]
        public struct ClipStep
        {
            [Tooltip("Clip or Animator state name to play.")]
            public string ClipName;

            [Tooltip("Pause, in seconds, after the previous step's clip finishes and before this "
                     + "one starts. Ignored on the first step.")]
            public float DelaySeconds;

            [Tooltip("Turns the member this many degrees around world Y, instantly, right before "
                     + "this step's clip starts — for a clip whose own baked pose always resets "
                     + "facing to the same direction (e.g. a repeated clip that would otherwise "
                     + "snap back to its first playthrough's facing). Also switches to this clip "
                     + "with a hard cut instead of a blend: blending the reset facing back in "
                     + "would visibly spin the model round again while this turn is already "
                     + "applied. Only applied to a member that actually has this step's clip. 0 "
                     + "leaves it untouched.")]
            public float RotateYDegrees;
        }

        [Serializable]
        public struct SceneTrigger
        {
            public string BeatId;
            public SceneTriggerKind Kind;

            [Tooltip("Spawn only: the inactive template each copy is cloned from — a child of the "
                     + "anchor SnapEggAnchorToFloorWhenSettled eases straight down once a floor is "
                     + "found (see SpawnFallDurationSeconds), the same way Appear's own Template is "
                     + "cloned and played.")]
            public Transform FallTemplate;

            [Tooltip("Off: planted level, at eye height, in front of wherever the operator is "
                     + "facing — for a scene meant to be walked up to. On: planted along the "
                     + "device's real aim, pitch included, and turned to face the same way — for "
                     + "a scene meant to be looked AT, like something dropped when the device is "
                     + "pointed at the ground.")]
            public bool FollowCameraPitch;

            [Tooltip("How many copies fire each time this beat does. 1 (or 0) just plays "
                     + "FallTemplate above; more clone it, so one template can stand for a whole "
                     + "flock rather than each copy needing its own authored fall.")]
            public int Count;

            [Tooltip("Seconds between one copy starting and the next, so a flock does not fall "
                     + "in lock-step.")]
            public float StaggerSeconds;

            [Tooltip("Random horizontal spread, in metres, applied to each copy around the same "
                     + "planted point — a flock scatters instead of stacking exactly.")]
            public float ScatterRadiusM;

            [Tooltip("Spawn only: plants the anchor directly under the eye's own X/Z instead of "
                     + "SceneTriggerDistanceM out in front of it — this is what actually controls "
                     + "where it lands, not a cosmetic starting nudge.")]
            public bool SpawnUnderEye;

            [Tooltip("Spawn only: how long SnapEggAnchorToFloorWhenSettled's own straight-down "
                     + "ease takes once a floor is found under this copy — the whole of the visible "
                     + "\"fall\", from wherever it spawned (screen/eye height) down to the floor. "
                     + "Not a hand-authored Timeline clip, so there is no external asset for this "
                     + "to go missing.")]
            public float SpawnFallDurationSeconds;

            // --- ReactFlock only ------------------------------------------------

            [Tooltip("ReactFlock only: the Spawn beat whose still-standing copies this reaches "
                     + "into. That flock is consumed — a later Spawn press starts a fresh one.")]
            public string TargetBeatId;

            [Tooltip("ReactFlock only: how long the shake-and-grow plays before each one either "
                     + "becomes FishPrefab or, with none assigned yet, just settles.")]
            public float HatchDurationSeconds;

            [Tooltip("ReactFlock only: how much bigger each one ends up — 0.5 means 150% of "
                     + "whatever size it was when the beat fired.")]
            public float GrowMultiplier;

            [Tooltip("ReactFlock only: peak trembling distance, in metres, while it grows.")]
            public float ShakeAmplitudeM;

            [Tooltip("ReactFlock only: what each one becomes when the duration ends. Left "
                     + "unassigned, it grows and settles in place instead — a stand-in until the "
                     + "real model exists.")]
            public GameObject FishPrefab;

            [Tooltip("ReactFlock only: the Legacy clip FishPrefab loops once swapped in. Left "
                     + "empty, falls back to \"Swim\" — every fish before Rainbow Trout shares "
                     + "that name; Rainbow Trout's own clip is \"Trout_Swim\".")]
            public string FishSwimClipName;

            [Tooltip("ReactFlock only: the swapped-in model keeps the facing of the one it "
                     + "replaces (a fish becoming a bigger fish in the same spot), instead of turning "
                     + "to face wherever the device happens to be aimed now — which is right for "
                     + "something hatching out of an egg, that has no facing of its own to keep.")]
            public bool InheritFacing;

            [Tooltip("ReactFlock only: once FishPrefab exists, slowly eases it away from the "
                     + "whole flock's own centroid (its position right after hatching, before "
                     + "any spread) until its distance from that centroid is this many times what "
                     + "it started at — 5 spreads the flock out to 5x its post-hatch spacing. "
                     + "Additive via the same swim-offset FishDrift's own wobble rides on top of, "
                     + "so the drift keeps going the whole time this plays out. 0 or 1 leaves "
                     + "every member exactly where it hatched.")]
            public float SpreadMultiplier;

            [Tooltip("ReactFlock + SpreadMultiplier only: how long the slow spread-apart takes.")]
            public float SpreadDurationSeconds;

            [Tooltip("ReactFlock + SpreadMultiplier only: eases the member up by this many metres "
                     + "over the same SpreadDurationSeconds the spread-apart itself takes, so it "
                     + "rises while it spreads rather than only drifting outward. Ignored (and the "
                     + "rise computed instead from the operator's own eye height) when "
                     + "SpreadRiseToEyeHeight is set. 0 leaves height untouched.")]
            public float SpreadRiseM;

            [Tooltip("ReactFlock + SpreadMultiplier only: rises to approximately the operator's own "
                     + "eye height instead of SpreadRiseM's fixed distance — so by the time the "
                     + "spread finishes, the operator is looking out at the shoal roughly level "
                     + "with them, rather than down on top of it. Falls back to SpreadRiseM with no "
                     + "eye to read (the desktop scene).")]
            public bool SpreadRiseToEyeHeight;

            [Tooltip("SwimToEye: how long the journey to the eye takes, start to arrival. "
                     + "EatAndGrow: how long the closest member of TargetBeatId's flock takes to "
                     + "swim toward ConsumeTargetBeatId's own member before it is eaten.")]
            public float SwimDurationSeconds;

            [Tooltip("SwimToEye only: how far in front of the eye it arrives — its own distance, "
                     + "not SceneTriggerDistanceM, so a Spawn beat's placement can stay close "
                     + "while a flock arriving here lands somewhere easier to work in front of.")]
            public float SwimDestinationDistanceM;

            [Tooltip("SwimToEye only: random spread, in metres, applied around the shared "
                     + "destination in the eye's own right/up — so the flock arrives spread out "
                     + "instead of piled on one point, while staying within frame.")]
            public float SwimScatterRadiusM;

            [Tooltip("Appear only: the inactive template each copy is cloned from. Placed and "
                     + "scattered the same way Spawn's FallTemplate is, but played as a plain "
                     + "Legacy Animation clip rather than eased through a scripted fall.")]
            public GameObject Template;

            [Tooltip("Appear only: a fixed, deliberate offset from the planted point, in the "
                     + "eye's own right/up — x positive is toward screen-right, y positive is "
                     + "toward screen-top. Unlike ScatterRadiusM this is not random: it is how "
                     + "two Appear triggers on the same beat land in different corners of frame "
                     + "instead of on top of each other. Ignored when UseWorldPosition is set.")]
            public Vector2 AppearOffsetM;

            [Tooltip("Appear only: plants this copy at a FIXED WORLD position instead of "
                     + "camera-relative — breaks from every other trigger's wizard-of-oz "
                     + "placement (there is no VPS fix to plant against, so everything else is "
                     + "placed wherever the operator currently has the device pointed), so use "
                     + "only when a beat genuinely should not follow the device. AppearOffsetM, "
                     + "ScatterRadiusM, FollowCameraPitch and SceneTriggerDistanceM are all "
                     + "ignored when this is set.")]
            public bool UseWorldPosition;

            [Tooltip("Appear only: the fixed world position used when UseWorldPosition is set.")]
            public Vector3 WorldPosition;

            [Tooltip("Appear only: instead of UseWorldPosition's fixed point or the default "
                     + "eye-relative placement, plants this copy relative to the first living "
                     + "member of this OTHER beat's own flock (e.g. the fry, so the heron can "
                     + "follow wherever they actually ended up) — see AnchorOffsetM for how the "
                     + "offset itself is expressed. Takes priority over the eye-relative default; "
                     + "ignored when UseWorldPosition is also set. Left empty, placement falls "
                     + "back to that default.")]
            public string AnchorBeatId;

            [Tooltip("Appear + AnchorBeatId only: offset from the anchor's own position — x "
                     + "toward its head, y world-vertical, z toward its right side. \"Head\" is the "
                     + "direction the anchor flock's own members are facing (read off the fish "
                     + "themselves, see FlockHeadDirection — they turn to face wherever the operator "
                     + "was aiming when they hatched, so it is not a fixed world direction) and "
                     + "\"right\" is that turned a quarter round from above.")]
            public Vector3 AnchorOffsetM;

            [Tooltip("Appear + AnchorBeatId only: the head-axis component of AnchorOffsetM is "
                     + "measured from whichever anchor-flock member sits furthest along the head "
                     + "direction, not the flock's own centroid — for a flock that has spread out "
                     + "(see SpreadMultiplier), the centroid trails behind whoever is actually "
                     + "leading. Off by default (centroid, matching every other axis).")]
            public bool AnchorToFrontmost;

            [Tooltip("Appear + AnchorBeatId only: the \"head\"/\"right\" axes AnchorOffsetM is "
                     + "measured along come from the operator's own current aim (leveled), not the "
                     + "anchor flock's average facing — for a placement judged by \"directly ahead "
                     + "of them, from where I'm standing now\" rather than \"off to the side of "
                     + "them, however they happen to be facing\". Falls back to the flock's own "
                     + "facing with no eye to read (the desktop scene).")]
            public bool AnchorForwardFromEye;

            [Tooltip("Appear + AnchorBeatId only: overrides the computed anchor's Y with a real "
                     + "floor height, raycast straight down against detected AR planes (falls back "
                     + "to a fixed distance below the anchor if none is found, or on the desktop "
                     + "scene with no ARRaycastManager) — for a beat meant to stand something on "
                     + "the ground rather than float it at the flock's own (mid-water) height.")]
            public bool AnchorOnGround;

            [Tooltip("Appear only: which clip or Animator state to loop — works for either a "
                     + "Legacy Animation or a Mecanim Animator, since a clone may carry either (or "
                     + "several, one per child, as with the nest's three babies). Left empty on a "
                     + "Legacy clip, it just calls Play() with no name, i.e. whatever the importer "
                     + "picked as the default — fine for a model with exactly one clip, but worth "
                     + "naming explicitly for one with several. An Animator needs a name "
                     + "regardless.")]
            public string AppearClipName;

            [Tooltip("Appear and PlayClip: playback speed applied to every Legacy Animation or "
                     + "Animator found (there can be more than one target, e.g. the nest's three "
                     + "babies, or PlayClip's own ClipSequence). Left at 0, playback runs at each "
                     + "clip's own normal speed.")]
            public float AppearClipSpeed;

            [Tooltip("PlayClip only: the sequence to play, in order, on every member of "
                     + "TargetBeatId's flock — e.g. the heron lowering its head, pausing, then "
                     + "turning. Each step's DelaySeconds is a pause after the PREVIOUS step's own "
                     + "clip finishes (ignored on the first step) before that step starts.")]
            public ClipStep[] ClipSequence;

            [Tooltip("PlayClip only: name of a child transform on the target, placed at the point that "
                     + "should stay put — the middle of a model's feet — while ClipSequence plays "
                     + "(see FeetLock). A clip's own baked turn pivots on the model's origin, which "
                     + "is rarely where the feet are, so left alone the whole model slides round an "
                     + "arc as it turns. Left empty, nothing is held. A target that doesn't carry it "
                     + "(the nest sharing the heron's beat) is left alone.")]
            public string LockFeetTransformName;

            [Tooltip("PlayClip only: an optional beat whose flock holds a single fish to feed to "
                     + "TargetBeatId's Animation-bearing member (the heron, not the nest) — it "
                     + "swims to FishFollowBoneName, turns to face up on arrival, tracks that "
                     + "bone every frame, and disappears at the bone's own lowest point once "
                     + "FishDisappearAfterSeconds have passed. Left empty, no fish behaviour "
                     + "runs.")]
            public string FishSourceBeatId;

            [Tooltip("PlayClip + FishSourceBeatId only: name of the bone/child transform on the "
                     + "target member the fish swims to and then tracks (e.g. the heron's "
                     + "beak-tip bone).")]
            public string FishFollowBoneName;

            [Tooltip("PlayClip + FishSourceBeatId only: an offset from FishFollowBoneName, in "
                     + "that bone's own local space, for when the bone itself sits a bit short of "
                     + "where the fish should actually end up (e.g. the beak-tip bone reading "
                     + "closer to the eye than the mouth).")]
            public Vector3 FishFollowLocalOffset;

            [Tooltip("PlayClip + FishSourceBeatId only: how long the fish takes to swim to "
                     + "FishFollowBoneName before it starts tracking it every frame.")]
            public float FishSwimDurationSeconds;

            [Tooltip("PlayClip + FishSourceBeatId only: seconds after this beat fires before the "
                     + "clip that should end with the fish being eaten begins (e.g. the second "
                     + "\"Lower Head\") — timed to that ClipSequence step's own start, not to the "
                     + "fish's actual disappearance. From that point the fish keeps tracking "
                     + "FishFollowBoneName and disappears at its own lowest point afterward, "
                     + "read at runtime rather than guessed as a fraction of the clip's length.")]
            public float FishDisappearAfterSeconds;

            [Tooltip("EatAndGrow only: the flock destroyed outright. TargetBeatId is the one "
                     + "grown in place instead — GrowMultiplier and HatchDurationSeconds do "
                     + "double duty here as the grow amount and how long it takes.")]
            public string ConsumeTargetBeatId;

            [Tooltip("EatAndGrow only: seconds from this beat firing until ConsumeTargetBeatId's "
                     + "flock is actually destroyed — the closest TargetBeatId member spends this "
                     + "time swimming toward it first (see SwimDurationSeconds).")]
            public float EatDelaySeconds;

            [Tooltip("EatAndGrow only: seconds after ConsumeTargetBeatId's flock is destroyed "
                     + "before TargetBeatId's flock starts growing.")]
            public float GrowDelaySeconds;

            [Tooltip("Rotate only: degrees to turn every member of TargetBeatId's flock around "
                     + "world Y.")]
            public float RotateYDegrees;

            [Tooltip("Rotate only: how long one member's own turn takes, animated rather than "
                     + "snapped.")]
            public float RotateDurationSeconds;

            [Tooltip("Rotate only: each member waits a random amount up to this many seconds, "
                     + "independently, before starting its own turn — so a flock turns raggedly "
                     + "rather than all at once.")]
            public float RotateStaggerMaxSeconds;

            [Tooltip("Jump only: each member's own peak height varies between half of this and "
                     + "this, in metres.")]
            public float JumpHeightM;

            [Tooltip("Jump only: how long one member's own up-and-back-down arc takes.")]
            public float JumpDurationSeconds;

            [Tooltip("Jump only: each member waits a random amount up to this many seconds, "
                     + "independently, before starting its own jump — so a flock leaps raggedly "
                     + "rather than all at once.")]
            public float JumpStaggerMaxSeconds;

            [Tooltip("Jump only: how far along world X a member ends up once it lands — a small "
                     + "net hop rather than landing exactly back where it started.")]
            public float JumpForwardM;

            [Tooltip("Jump only: peak nose-up tilt reached partway through the rise (level at "
                     + "takeoff, tilted here at the quarter-point, level again at the top of the "
                     + "arc), mirrored nose-down on the way back — not derived from the "
                     + "trajectory's own slope, a separate scripted flutter on top of it.")]
            public float JumpPitchDegrees;

            [Tooltip("Cull only: how many of TargetBeatId's flock survive, picked at random. "
                     + "Every other member fades to nothing and is destroyed.")]
            public int KeepCount;

            [Tooltip("Cull only: how long each culled member takes to fade away.")]
            public float CullDurationSeconds;

            [Tooltip("Stunt only: target Rotation X (world/local Euler, degrees) the first "
                     + "flock member tilts to before speeding up, then tilts back from "
                     + "afterward. Uses RotateDurationSeconds for each of those two tilts.")]
            public float StuntRotationXTarget;

            [Tooltip("Stunt only: playback speed multiplier applied to whatever animation is "
                     + "already playing on the member (Legacy Animation or Animator) once the "
                     + "first tilt finishes.")]
            public float StuntSpeedMultiplier;

            [Tooltip("Stunt only: how long the sped-up playback lasts before speed and rotation "
                     + "both reverse.")]
            public float StuntSpeedDurationSeconds;

            [Tooltip("Stunt only: egg prefab dropped from the member's tail once it settles back "
                     + "down. Left null to skip the egg-drop finale entirely.")]
            public GameObject EggPrefab;

            [Tooltip("Stunt only: how many EggPrefab copies to drop.")]
            public int EggCount;

            [Tooltip("Stunt only: total time the EggCount eggs are staggered across — spread "
                     + "roughly evenly, not all released at once.")]
            public float EggDropWindowSeconds;

            [Tooltip("Stunt only: random horizontal jitter radius applied to each egg's spawn "
                     + "point around the tail, so they don't all drop from the same point.")]
            public float EggJitterRadiusM;

            [Tooltip("Stunt only: how far below its own spawn point each egg falls, and how long "
                     + "that fall takes.")]
            public float EggFallDistanceM;
            public float EggFallDurationSeconds;

            [Tooltip("LieDownFade only: how far forward each flock member swims (its own current "
                     + "facing) before tilting onto its side, and how long that swim takes.")]
            public float SwimForwardDistanceM;
            public float SwimForwardDurationSeconds;

            [Tooltip("LieDownFade only: how long each member holds its tilted pose (reusing "
                     + "StuntRotationXTarget/RotateDurationSeconds for the tilt itself, and "
                     + "CullDurationSeconds for the fade afterward) before fading away.")]
            public float LieDownHoldSeconds;
        }

        [Header("Service")]
        [Tooltip("The laptop running service/src/server.mjs. It prints this address on startup.")]
        public string ServiceHost = "127.0.0.1";
        public int ServicePort = 8710;
        public string Slug = "ucb-strawberry-creek-south";

        [Tooltip("Never read at run time. Copies of the rainbow trout's own materials with the "
                 + "alpha-blend keyword already switched on — held here only so the player build "
                 + "keeps that shader variant. glTFast's shader declares it with shader_feature, so a "
                 + "variant no material in the build uses is stripped, and switching a fade on at run "
                 + "time (see FadeAndDestroy) then silently does nothing: the fish just vanish at the "
                 + "end instead of fading. Filled in by SimulationSceneBuilder.")]
        public Material[] FadeVariantMaterials;

        [Header("View")]
        [Tooltip("Raise the camera above the centreline. Zero because the centreline is authored "
                 + "at the phone's own height — it is the path the chest walks, not the creek bed.")]
        public float EyeOffsetM = 0f;

        [Tooltip("The inset map in the corner. The first-person view alone makes it hard to tell "
                 + "a walk from a stall.")]
        public bool ShowOverview = true;

        [Tooltip("The centreline, the beat spheres and the walker pin — useful for judging a "
                 + "walking journey, clutter (small dots scattered across the background at "
                 + "wherever each beat's own position happens to be) for a scene like this one "
                 + "that is really about what SceneTriggers plant, not about the walk.")]
        public bool ShowDebugMarkers = false;

        [Header("Scene Triggers")]
        [Tooltip("A beat that, when fired, also plays a Timeline — the visual half of a beat "
                 + "that the audio system already knows how to fire. Play On Awake should be off "
                 + "on each director; this is what starts it.")]
        public List<SceneTrigger> SceneTriggers = new();

        [Tooltip("How far in front of the eye a triggered scene is planted. There is no VPS fix "
                 + "to plant it against on the wizard-of-oz path, so it is placed relative to "
                 + "wherever the operator has the device pointed the instant they fire the beat, "
                 + "not at the beat's own journey coordinates.")]
        public float SceneTriggerDistanceM = 1.5f;

        /// <summary>
        /// Set before the scene loads to point it somewhere else. For the PlayMode fixture, which
        /// runs a scratch service on a random port; nothing else should touch it.
        /// </summary>
        public static string HostOverride;
        public static int PortOverride;

        // --- the piece ------------------------------------------------------
        private ControlLink _link;
        private JourneyAudioRunner _audio;
        private JourneyProgression _progression;
        private JourneyDocument _journey;
        private VpsLocalizer _gate;
#if NSDK_PRESENT
        private VpsLocalizationRunner _vps;
#endif

        // --- the view -------------------------------------------------------
        private Camera _eye;
        private float? _lastKnownGoodEyeY;
        private UnityEngine.XR.ARFoundation.ARRaycastManager _raycastManager;
        private List<UnityEngine.XR.XRInputSubsystem> _inputSubsystemsScratch;
        private List<UnityEngine.XR.ARSubsystems.XRPlaneSubsystem> _planeSubsystemsScratch;
        private UnityEngine.XR.ARFoundation.ARPlaneManager _planeManager;
        private string _lastEggDropDiag = "(no egg dropped yet)";

        /// <summary>The real floor height, in world Y, the first time 01-a's own egg-drop
        /// (SnapEggAnchorToFloorWhenSettled) actually finds one — reused by any later beat that
        /// needs to stand something on the ground (PlayAppear's AnchorOnGround) instead of each
        /// one raycasting for itself. Per feedback ("整套模型有时不显示，有时突然显示两个"): a
        /// live raycast retry could run for the ground/nest at 04-a for up to its own several-
        /// second window before anything appeared, which read as the button not working and
        /// invited a second press — spawning a second copy once both eventually resolved. The
        /// room's floor does not move between 01-a and 04-a, so reusing the height already
        /// confirmed there resolves instantly instead.</summary>
        private float? _knownFloorY;
        private string _lastStableTrackingDiag = "(no spawn beat fired yet)";
        private string _lastDraftFetchDiag = "(not attempted yet)";
        private Camera _overview;
        private readonly Dictionary<string, Renderer> _markers = new();
        private readonly Dictionary<string, float> _flashUntil = new();
        private LineRenderer _path;
        private Transform _walkerPin;

        // --- what the HUD says ----------------------------------------------
        private string _journeyNote = "loading…";
        private string _gateNote = "";
        private string _audioNote = "";
        private readonly List<string> _log = new();
        private int _shoalCount;

        public JourneyDocument Journey => _journey;
        public JourneyProgression Progression => _progression;
        public ControlLink Link => _link;
        public bool Ready => _progression != null;

        // ------------------------------------------------------------------ startup

        private void Awake()
        {
            if (!string.IsNullOrEmpty(HostOverride)) ServiceHost = HostOverride;
            if (PortOverride > 0) ServicePort = PortOverride;

            _link = GetComponent<ControlLink>();
            _audio = GetComponent<JourneyAudioRunner>();
#if NSDK_PRESENT
            _vps = GetComponent<VpsLocalizationRunner>();
#endif
            _link.Host = ServiceHost;
            _link.Port = ServicePort;

            _eye = Camera.main;
            // Null on the desktop scene, which has no XR Origin — FallEgg falls back to its
            // fixed distance/duration in that case.
            _raycastManager = FindFirstObjectByType<UnityEngine.XR.ARFoundation.ARRaycastManager>();
            _planeManager = FindFirstObjectByType<UnityEngine.XR.ARFoundation.ARPlaneManager>();
            BuildOverviewCamera();
        }

        private IEnumerator Start()
        {
            StartCoroutine(WatchForStuckJourneyLoad());
            yield return RunCatchingExceptions(LoadJourney());
            if (_journey != null) Begin();
        }

        /// <summary>LoadJourney's own UnityWebRequest carries a 5 s timeout and ResolveAsync's own
        /// service fetch races a 2.5 s one, so under every failure mode this file's own code
        /// accounts for, _journeyNote should read something other than the initial "loading…"
        /// well within 10 s. If it still hasn't by then, whatever is stuck is stuck somewhere
        /// neither of those timeouts actually covers — most likely the request itself never
        /// calling back at all, which no exception would ever surface. Says so on the HUD instead
        /// of leaving "loading…" indistinguishable from "still within its normal timeout".</summary>
        private IEnumerator WatchForStuckJourneyLoad()
        {
            yield return new WaitForSeconds(10f);
            if (_journeyNote == "loading…")
            {
                _journeyNote = "!! still loading after 10s — LoadJourney's own request never "
                                + "came back at all (not an exception, not its own 5s timeout "
                                + "firing either)";
            }
        }

        /// <summary>Drives inner's own MoveNext() by hand instead of a plain `yield return inner`,
        /// so an exception thrown at ANY point inside it — including after it has already resumed
        /// from a yield, where a plain `yield return` can't wrap it in try/catch at all, since C#
        /// forbids a yield inside a try that has a catch clause — lands here instead of just
        /// silently killing the coroutine. Without this, "journey stuck on loading…" forever and
        /// an exception thrown and swallowed somewhere inside LoadJourney look identical from the
        /// HUD, with nothing in the Unity console to tell them apart when there is no Xcode
        /// console in reach.</summary>
        private IEnumerator RunCatchingExceptions(IEnumerator inner)
        {
            while (true)
            {
                object current;
                try
                {
                    if (!inner.MoveNext()) yield break;
                    current = inner.Current;
                }
                catch (Exception e)
                {
                    _journeyNote = $"!! LoadJourney threw {e.GetType().Name}: {e.Message}";
                    Debug.LogException(e);
                    yield break;
                }
                yield return current;
            }
        }

        /// <summary>
        /// Where the journey comes from, and why it is not the one the phone would load.
        ///
        /// The app reads PUBLISHED revisions, and for this site the published revision is the old
        /// seeded layout — a 3-point, 34 m straight ending barrier/headwater, against a draft that
        /// is 16 points, 18.8 m, ending falls/spawn. Worse, the draft's revision is 0 and the
        /// published one is 1, and <see cref="JourneyResolver"/> settles ties by taking the higher
        /// revision. So a draft can never win that contest: offered alongside anything published
        /// it is refused as superseded, by design.
        ///
        /// A simulation scene that showed the artist a reach they are not editing would be worse
        /// than useless, so this asks the service for the DRAFT and offers it to the resolver
        /// alone. The precedence rule is deliberately not consulted — a draft is not a revision
        /// and has nothing to be ranked against. Every other check still applies: it must parse,
        /// it must be for this site, and it must pass the calibration gate.
        ///
        /// If the service is not running there is no draft to be had, and it falls back to what a
        /// phone would do. The HUD says which of the two happened, in red when it is the fallback,
        /// because the difference is four beats and sixteen metres.
        /// </summary>
        private IEnumerator LoadJourney()
        {
            string url = $"http://{ServiceHost}:{ServicePort}/api/sites/{Slug}/draft";
            using (var request = UnityWebRequest.Get(url))
            {
                request.timeout = 5;
                yield return request.SendWebRequest();

                if (request.result == UnityWebRequest.Result.Success)
                {
                    var resolution = JourneyResolver.Resolve(
                        new[] { new JourneyCandidate(JourneySourceKind.Service, request.downloadHandler.text) },
                        JourneyEnvironment.Simulation, Slug);

                    if (resolution.HasJourney)
                    {
                        _journey = resolution.Document;
                        _journeyNote = $"draft r{_journey.revision} from the service — "
                                       + $"{_journey.site.centreline.Count} points, "
                                       + $"{Centreline.Length(_journey.site.centreline):0.0} m";
                        JourneyProvider.Log(resolution);
                        yield break;
                    }

                    _journeyNote = "the service's draft was refused: "
                                   + string.Join("; ", resolution.Notes);
                    Debug.LogError($"[simulation] {_journeyNote}");
                    yield break;
                }

                // Falls through to the ResolveAsync fallback below without a word about why —
                // confirmed on a device: that made an ATS block on this very request look
                // identical to "no laptop, falling back to what a phone would do" on the HUD,
                // once the failure carrying the actual reason was fixed elsewhere. Put on its
                // own HUD line, not into _journeyNote itself, since the fallback below still
                // gets a real chance and may yet succeed — and Debug.Log alone is invisible
                // without an Xcode console in reach.
                _lastDraftFetchDiag = $"{url}   {request.result} — {request.error}";
                Debug.LogWarning($"[simulation] draft fetch did not succeed: {_lastDraftFetchDiag}");
            }

            // No laptop. Fall back to the phone's own precedence so the scene still opens
            // something, and say loudly that it is not what is being edited.
            var provider = JourneyProvider.ForLaunch(new JourneyProviderSettings
            {
                Slug = Slug,
                Environment = JourneyEnvironment.Simulation,
            });
            var task = provider.ResolveAsync();
            while (!task.IsCompleted) yield return null;

            JourneyProvider.Log(task.Result);
            if (!task.Result.HasJourney)
            {
                _journeyNote = "no journey from anywhere — is the service running on "
                               + $"{ServiceHost}:{ServicePort}?";
                Debug.LogError($"[simulation] {_journeyNote}");
                yield break;
            }

            _journey = task.Result.Document;
            _journeyNote = $"!! PUBLISHED r{_journey.revision} from {task.Result.Source} — "
                           + "the service was unreachable, so this is NOT your draft";
        }

        private void Begin()
        {
            // The calibration gate, on the surface that is allowed to run an uncalibrated
            // journey. Every site is uncalibrated today, so this is the only surface that runs at
            // all — and it is worth seeing rather than assuming.
            _gate = new VpsLocalizer(_journey, null, RuntimeSurface.Simulation);
            _gateNote = _gate.Mode == LocalizationMode.Refused
                ? $"REFUSED: {_gate.RefusalReason}"
                : "passed (simulation surface)";
            if (_gate.Mode == LocalizationMode.Refused)
            {
                Debug.LogError($"[simulation] calibration gate {_gateNote}");
                return;
            }

#if NSDK_PRESENT
            // The device path, wired and running, so what it does at a desk is visible rather
            // than imagined: with no AR session it samples nothing and never reaches a fix, which
            // is exactly why the pose channel below exists.
            if (_vps != null) _vps.Run(_journey);
#endif

            _progression = new JourneyProgression(_journey);
            _shoalCount = _journey.shoal?.startingCount ?? 40;

            _link.SetCentreline(_journey.site.centreline);
            _link.SetEffects(new Effects(this));
            _link.SetStatusSource(new Status(this));

            // forceSimulation is left as SimulationSceneBuilder set it — true for the desk
            // (never PHASE at a laptop), false for the AR build (a real PHASE render on the
            // device it was actually built for). Overwriting it here unconditionally used to
            // force every build onto the desk renderer, AR included.
            _audio.listener = _eye != null ? _eye.transform : transform;
            _audio.Begin(_journey, new StreamingAssetsClipResolver());
            _audioNote = _audio.Engine != null
                ? $"{_audio.Engine.BackendDescription}"
                  + (_audio.Engine.MissingClips.Count > 0
                     ? $" — {_audio.Engine.MissingClips.Count} clip(s) missing"
                     : "")
                : "not started";

            if (ShowDebugMarkers) BuildMarkers();
            Note($"journey ready: {_journeyNote}");
        }

        // ------------------------------------------------------------------ the frame

        private void Update()
        {
            // Confirmed on a real device (via a diagnostic HUD line): ARSession.state reads
            // SessionTracking, but SubsystemManager reports the XRInputSubsystem registered and
            // NOT running — nothing feeds any pose-consumption API (TrackedPoseDriver,
            // ARPoseDriver, or even the raw legacy InputTracking calls) because the subsystem
            // that would report it was never started. Starting it here is a one-line, cheap-to-
            // repeat nudge rather than something to chase in Player/XR settings blind.
            _inputSubsystemsScratch ??= new List<UnityEngine.XR.XRInputSubsystem>();
            SubsystemManager.GetSubsystems(_inputSubsystemsScratch);
            foreach (var inputSubsystem in _inputSubsystemsScratch)
            {
                if (!inputSubsystem.running) inputSubsystem.Start();
            }
            // Same fix, same reasoning, for plane detection: the egg-drop's floor raycast kept
            // missing every plane, which fits the identical registered-but-not-running gap.
            _planeSubsystemsScratch ??= new List<UnityEngine.XR.ARSubsystems.XRPlaneSubsystem>();
            SubsystemManager.GetSubsystems(_planeSubsystemsScratch);
            foreach (var planeSubsystem in _planeSubsystemsScratch)
            {
                if (!planeSubsystem.running) planeSubsystem.Start();
            }
            // Confirmed on a device: even after waiting for SessionTracking to hold steady with a
            // sane Y, the very next read of the same transform (a few lines of code, same or next
            // frame) could come back tens of metres off — a one-off coordinate correction ARKit
            // itself makes mid-session, not a timing issue this side can wait longer to avoid.
            // Recording whichever Y last looked plausible, every frame, gives anchor placement a
            // real fallback for the instant it asks and gets a bad one, rather than baking that
            // instant in forever.
            if (_eye != null && Mathf.Abs(_eye.transform.position.y) < 5f)
                _lastKnownGoodEyeY = _eye.transform.position.y;

            if (_progression == null) return;

            // Whichever source is live. At a desk that is always the browser's scrubber; on the
            // creek it would be VPS, and nothing below can tell the difference — which is the
            // whole point of the seam.
            bool moving = _link.Poses.TryGetPose(_link.NowMs, out var pose);
            if (moving)
            {
                _progression.Tick(pose.AnchorLocalPosition, pose.Quality, Time.deltaTime);
                PlaceWalker(pose);
            }
#if NSDK_PRESENT
            else if (_vps != null && _vps.Localizer != null)
            {
                var fix = _vps.Localizer.Fix;
                if (fix.Quality != LocalizationQuality.Unavailable)
                {
                    _progression.Tick(fix.JourneyPosition, fix.Quality, Time.deltaTime);
                }
            }
#endif

            foreach (var journeyEvent in _progression.DrainEvents())
            {
                _audio.Handle(journeyEvent);
                Note(journeyEvent.ToString());
                if (journeyEvent.Kind == JourneyProgression.Event.Type.Completed
                    && journeyEvent.Beat != null)
                {
                    _flashUntil[journeyEvent.Beat.id] = Time.time + 0.6f;
                }
            }

            _audio.SetShoalCount(_shoalCount);
            if (ShowDebugMarkers) PaintMarkers();
        }

        private void PlaceWalker(PoseSample pose)
        {
            Vector3 at = pose.AnchorLocalPosition + Vector3.up * EyeOffsetM;
            if (_eye != null)
            {
                _eye.transform.position = at;
                // Heading comes from the path rather than from any look direction: the body
                // follows the route and it is the body that decides which beats are near.
                _eye.transform.rotation = Quaternion.Euler(0f, pose.HeadingRad * Mathf.Rad2Deg, 0f);
            }
            if (_walkerPin != null) _walkerPin.position = at;
        }

        // ------------------------------------------------------------------ the view

        private void BuildOverviewCamera()
        {
            if (!ShowOverview) return;

            var go = new GameObject("Overview Camera");
            go.transform.SetParent(transform, false);
            _overview = go.AddComponent<Camera>();
            _overview.orthographic = true;
            _overview.depth = 1;                       // drawn over the eye view
            _overview.rect = new Rect(0.68f, 0.02f, 0.30f, 0.30f);
            _overview.clearFlags = CameraClearFlags.SolidColor;
            _overview.backgroundColor = new Color(0.05f, 0.07f, 0.09f);
            _overview.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
        }

        /// <summary>A sphere at every beat and a line down the reach. Crude on purpose: the
        /// question this scene answers is "did anything happen", and that has to be answerable
        /// from across a room.</summary>
        private void BuildMarkers()
        {
            var centreline = _journey.site.centreline;

            var pathGo = new GameObject("Centreline");
            pathGo.transform.SetParent(transform, false);
            _path = pathGo.AddComponent<LineRenderer>();
            _path.material = new Material(Shader.Find("Sprites/Default"));
            _path.widthMultiplier = 0.06f;
            _path.positionCount = centreline.Count;
            _path.startColor = _path.endColor = new Color(0.35f, 0.6f, 0.75f);
            for (int i = 0; i < centreline.Count; i++) _path.SetPosition(i, centreline[i].ToVector3());

            foreach (var beat in _journey.beats)
            {
                var marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                marker.name = $"Beat — {beat.id}";
                marker.transform.SetParent(transform, false);
                marker.transform.position = beat.position.ToVector3();
                marker.transform.localScale = Vector3.one * 0.5f;
                Destroy(marker.GetComponent<Collider>());
                _markers[beat.id] = marker.GetComponent<Renderer>();
            }

            var pin = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            pin.name = "Walker";
            pin.transform.SetParent(transform, false);
            pin.transform.localScale = new Vector3(0.3f, 0.5f, 0.3f);
            Destroy(pin.GetComponent<Collider>());
            pin.GetComponent<Renderer>().material.color = Color.white;
            _walkerPin = pin.transform;

            FrameOverview(centreline);
        }

        /// <summary>Every Spawn beat's still-standing copies, keyed by that beat's own id — what
        /// a ReactFlock beat reaches into. Populated as PlaySceneFlock spawns each one; never
        /// swept on a timer, only consumed when a ReactFlock beat processes the list.</summary>
        private readonly Dictionary<string, List<Transform>> _flocks = new();

        /// <summary>Each fish's own SwimToEye offset, additive on top of FishDrift's wobble
        /// rather than something FishDrift is paused for — the two run concurrently, so a swim
        /// reads as "still floating, also drifting left" instead of the wobble stopping while it
        /// travels. Never reset, only ever moved further along: a second swim starts from
        /// wherever the first left off.</summary>
        private readonly Dictionary<Transform, Vector3> _swimOffsets = new();

        /// <summary>Bumped every time the operator's "Skip forward" fires — every timed coroutine
        /// below captures the value in effect when it starts, and jumps straight to its own end
        /// state as soon as this no longer matches, rather than either finishing naturally or
        /// being torn down mid-flight. A counter rather than a bool so several presses in a row,
        /// or several beats' worth of coroutines overlapping, all still resolve correctly without
        /// needing to track who has "already seen" a single flag.</summary>
        private int _skipGeneration;

        private void RequestSkipCurrentToEnd() => _skipGeneration++;

        /// <summary>Guards RebuildJourneyUpTo against a second press landing mid-rebuild — with
        /// two overlapping, _flocks and _progression would both be getting torn down and refired
        /// by two coroutines at once. Refused rather than queued: an operator who presses again
        /// before the first finishes almost certainly wants a fresh rebuild from the button's
        /// CURRENT meaning, not two stacked ones.</summary>
        private bool _rebuildInProgress;

        /// <summary>A WaitForSeconds stand-in for pure sequencing delays (not the interpolated
        /// coroutines below, which watch _skipGeneration directly since they have their own
        /// per-frame state to snap forward) — returns as soon as either seconds have passed or a
        /// skip has been requested since sinceGeneration was captured.</summary>
        private IEnumerator WaitOrSkip(float seconds, int sinceGeneration)
        {
            float t = 0f;
            while (t < seconds)
            {
                if (_skipGeneration != sinceGeneration) yield break;
                t += Time.deltaTime;
                yield return null;
            }
        }

        /// <summary>Confirmed on a device: ARSession.state can already read SessionTracking while
        /// the reported camera position is still converging from the garbage value ARKit starts
        /// with — a Spawn/Appear beat fired in that window bakes its anchor into wherever that
        /// garbage position was (once, tens of metres from anywhere real) and it never moves
        /// again, since anchoring is deliberately one-shot rather than continuous. Waiting for
        /// tracking to have been genuinely continuous for a beat, not just started, is what
        /// separates "reporting a state" from "reporting something trustworthy". A no-op on the
        /// desktop scene, which has no ARSession at all (state stays None forever there).</summary>
        private IEnumerator WaitForStableTracking(int gen)
        {
            if (UnityEngine.XR.ARFoundation.ARSession.state
                == UnityEngine.XR.ARFoundation.ARSessionState.None)
                yield break;

            const float requiredStableSeconds = 1.5f;
            const float maxWaitSeconds = 20f;
            // Confirmed on a device: even after SessionTracking held steady for the full 1.5 s
            // above, the reported Y (gravity-aligned) coordinate specifically kept coming back
            // tens of metres off — every fresh spawn landed with Y around -16 to -20,
            // independently, across many separate sessions in different rooms, while X/Z looked
            // plausible each time. Horizontal (visual) tracking and the gravity-based vertical
            // calibration apparently don't converge on the same schedule, so SessionTracking
            // alone was never actually proof the position was trustworthy. No iPad is realistically
            // 5+ metres above or below wherever its own AR session started, so that is used as a
            // direct sanity bound on Y specifically, rather than guessing a still-longer delay.
            const float maxSaneAbsoluteY = 5f;
            float stableFor = 0f;
            float waited = 0f;
            while (stableFor < requiredStableSeconds && waited < maxWaitSeconds)
            {
                if (_skipGeneration != gen) yield break;
                bool trackingOk = UnityEngine.XR.ARFoundation.ARSession.state
                                  == UnityEngine.XR.ARFoundation.ARSessionState.SessionTracking;
                bool positionSane = _eye == null || Mathf.Abs(_eye.transform.position.y) < maxSaneAbsoluteY;
                stableFor = trackingOk && positionSane ? stableFor + Time.deltaTime : 0f;
                waited += Time.deltaTime;
                yield return null;
            }
            _lastStableTrackingDiag = $"waited {waited:F1}s   stableFor {stableFor:F1}s"
                                      + $"   settledY {(_eye != null ? _eye.transform.position.y : 0f):F2}"
                                      + (stableFor >= requiredStableSeconds ? "   OK" : "   TIMED OUT");
        }

        /// <summary>Guards the exact instant an anchor's Y gets used, not just some earlier check —
        /// confirmed on a device that the reported Y can still jump to something absurd between a
        /// passed stability check and the very next read of the same transform. Substitutes
        /// whichever Y last looked plausible (tracked every frame in Update) rather than baking a
        /// bad instant in forever; falls back to 0 if nothing plausible has been seen yet.</summary>
        private float SaneAnchorY(float y) => Mathf.Abs(y) < 5f ? y : (_lastKnownGoodEyeY ?? 0f);

        private void StartDrift(Transform fish) => StartCoroutine(FishDrift(fish));

        /// <summary>The horizontal direction a model's head points. Every model here is built so its
        /// root's own local left (-X) is its head at identity rotation — see HatchOne — so the root's
        /// rotation carries whichever way it was actually turned (toward wherever the operator was
        /// aiming when it hatched, then any later turn), rather than a fixed world direction.</summary>
        private static Vector3 HeadDirection(Transform model)
        {
            Vector3 head = model.TransformDirection(Vector3.left);
            head.y = 0f;
            return head.sqrMagnitude < 0.0001f ? Vector3.left : head.normalized;
        }

        /// <summary>The way a flock as a whole is heading — its members' own head directions
        /// averaged, so a beat placing something "in front of the fish" or "to their right" reads
        /// it off the fish themselves instead of assuming a fixed world direction.</summary>
        private static Vector3 FlockHeadDirection(List<Transform> flock)
        {
            Vector3 sum = Vector3.zero;
            foreach (var member in flock)
            {
                if (member != null) sum += HeadDirection(member);
            }
            sum.y = 0f;
            return sum.sqrMagnitude < 0.0001f ? Vector3.left : sum.normalized;
        }

        /// <summary>The rotation, about world Y only, that turns a model built to face Vector3.left
        /// at identity round to face `head` — built from a signed yaw angle rather than
        /// Quaternion.FromToRotation, which for exactly opposite vectors picks an arbitrary axis and
        /// can hand back a rotation that turns the model upside down instead.</summary>
        private static Quaternion YawFromLeftTo(Vector3 head)
        {
            return Quaternion.Euler(0f, Vector3.SignedAngle(Vector3.left, head, Vector3.up), 0f);
        }

        /// <summary>Play whatever a beat is wired to. Spawn plants its own flock on top of
        /// whatever earlier presses left standing — nothing is torn down first, so a flurry of
        /// button presses reads as a flurry of eggs, not a replacement of one. ReactFlock instead
        /// reaches into a named Spawn beat's flock and animates it in place.</summary>
        private void PlayScene(string beatId)
        {
            foreach (var trigger in SceneTriggers)
            {
                if (trigger.BeatId != beatId) continue;
                if (trigger.Kind == SceneTriggerKind.ReactFlock) StartCoroutine(PlayHatchReaction(trigger));
                else if (trigger.Kind == SceneTriggerKind.SwimToEye) StartCoroutine(PlaySwimToEye(trigger));
                else if (trigger.Kind == SceneTriggerKind.Appear) StartCoroutine(PlayAppear(trigger));
                else if (trigger.Kind == SceneTriggerKind.EatAndGrow) StartCoroutine(PlayEatAndGrow(trigger));
                else if (trigger.Kind == SceneTriggerKind.PlayClip) StartCoroutine(PlayClipOnFlock(trigger));
                else if (trigger.Kind == SceneTriggerKind.Despawn) PlayDespawn(trigger);
                else if (trigger.Kind == SceneTriggerKind.Rotate) StartCoroutine(PlayRotateFlock(trigger));
                else if (trigger.Kind == SceneTriggerKind.SwimFlock) StartCoroutine(PlaySwimFlock(trigger));
                else if (trigger.Kind == SceneTriggerKind.Jump) StartCoroutine(PlayJump(trigger));
                else if (trigger.Kind == SceneTriggerKind.Cull) StartCoroutine(PlayCull(trigger));
                else if (trigger.Kind == SceneTriggerKind.Stunt) StartCoroutine(PlayStunt(trigger));
                else if (trigger.Kind == SceneTriggerKind.LieDownFade) StartCoroutine(PlayLieDownFade(trigger));
                else if (trigger.FallTemplate != null) StartCoroutine(PlaySceneFlock(trigger));
            }
        }

        /// <summary>The order "Skip forward"/"Replay current"/"Resume" all walk — 01-a through
        /// 07-c. Explicit rather than trusting journey.beats' own array order: the journey
        /// document lists "beat-7" (03-b) before "beat-6" (04-a), so iterating it directly would
        /// let a skip (or a rebuild) jump straight to 03-b ahead of 04-a instead of respecting
        /// the numbered sequence.</summary>
        private static readonly string[] AdvanceOrder =
        {
            "beat-1", "beat-2", "beat-3", "beat-4", "beat-5", "beat-7", "beat-6", "beat-8",
            "beat-11", "beat-17", "beat-9", "beat-10", "beat-16", "beat-12", "beat-13", "beat-14", "beat-15",
        };

        /// <summary>Tears down every currently-spawned flock and replays the whole journey from
        /// beat-1 up through (and, with includeTarget, INCLUDING) targetBeatId in AdvanceOrder —
        /// a full rebuild rather than a per-beat undo, since a later beat routinely
        /// destroys/consumes an earlier one's own flock (Hatch swapping an egg for a fish, for
        /// one), so there is no "previous state" sitting around to simply restore. Every beat up
        /// to the target is fast-forwarded to its own end state instantly (see
        /// RequestSkipCurrentToEnd); the target itself, when included, plays out at its normal
        /// pace so the operator actually sees it — this is what "Replay current" fires. Without
        /// the target, this lands the scene exactly where it stood right after the PREVIOUS beat
        /// finished and before the target ever fired — what "Resume" fires.</summary>
        private IEnumerator RebuildJourneyUpTo(string targetBeatId, bool includeTarget)
        {
            if (_rebuildInProgress) yield break;
            _rebuildInProgress = true;

            foreach (var flock in _flocks.Values)
                foreach (var member in flock)
                    if (member != null) Destroy(member.root.gameObject);
            _flocks.Clear();
            _swimOffsets.Clear();
            _progression?.ResetAll();
            RequestSkipCurrentToEnd();

            int targetIndex = Array.IndexOf(AdvanceOrder, targetBeatId);
            if (targetIndex < 0) { _rebuildInProgress = false; yield break; }

            int lastIndex = includeTarget ? targetIndex : targetIndex - 1;
            for (int i = 0; i <= lastIndex; i++)
            {
                string beatId = AdvanceOrder[i];
                bool isLast = i == lastIndex;
                _progression.ForceBeat(beatId);
                PlayScene(beatId);
                if (!isLast || !includeTarget)
                {
                    // Not the one the operator actually wants to watch — snap it to its end state
                    // instead of waiting out its real duration, the same way Skip forward does,
                    // so rebuilding through a dozen earlier beats reads as fast, not as replaying
                    // the whole piece from scratch.
                    RequestSkipCurrentToEnd();
                }
                for (int f = 0; f < 10; f++) yield return null;
            }

            _rebuildInProgress = false;
        }

        private IEnumerator PlaySceneFlock(SceneTrigger trigger)
        {
            int gen = _skipGeneration;
            yield return WaitForStableTracking(gen);
            // The anchor, not FallTemplate's own transform: SnapEggAnchorToFloorWhenSettled moves
            // the anchor once a floor is found, so placing the inner object directly here and
            // then letting that coroutine move the anchor out from under it would leave the two
            // fighting over where the egg actually is.
            Transform templateAnchor = trigger.FallTemplate.parent != null
                ? trigger.FallTemplate.parent
                : trigger.FallTemplate;

            bool hasEye = _eye != null;
            Vector3 anchorPos = templateAnchor.position;
            Quaternion facing = templateAnchor.rotation;
            Vector3 forward = Vector3.forward;
            if (hasEye)
            {
                forward = _eye.transform.forward;
                if (!trigger.FollowCameraPitch)
                {
                    // Level, not identity: this still needs to yaw to match wherever the camera
                    // is actually facing, or the anchor (and so the whole clutch) settles along a
                    // fixed world direction that has nothing to do with where the device was
                    // aimed when it fired — confirmed on a device: the anchor itself sat right in
                    // front only after turning the iPad to face the same way it had been aimed.
                    forward.y = 0f;
                    forward = forward.sqrMagnitude < 0.0001f ? Vector3.forward : forward.normalized;
                }
                // The real aim, pitch included, when FollowCameraPitch is set; otherwise the
                // same leveled-but-yaw-matched direction used for anchorPos below.
                facing = Quaternion.LookRotation(forward);
                // 0, not SceneTriggerDistanceM, when SpawnUnderEye: the anchor's own X/Z is what
                // the floor-snap pulls the settled egg back to (see SnapEggAnchorToFloorWhenSettled),
                // regardless of which way the scripted fall pushes it in between, so this is
                // the actual lever for "land directly under the device" rather than a starting nudge.
                float forwardDistance = trigger.SpawnUnderEye ? 0f : SceneTriggerDistanceM;
                anchorPos = _eye.transform.position + forward * forwardDistance;
                anchorPos.y = SaneAnchorY(anchorPos.y);
            }

            float? sharedFallbackFloorY = null;
            if (hasEye && _raycastManager != null)
            {
                // One probe straight down from the eye itself, taken once per beat fire, as a
                // last resort for whichever eggs never find their OWN floor within the retry
                // window below — the spot right under the operator's feet is almost certainly
                // scanned already, since they were just standing there aiming the trigger.
                var floorHits = new List<UnityEngine.XR.ARFoundation.ARRaycastHit>();
                var floorRay = new Ray(_eye.transform.position, Vector3.down);
                if (_raycastManager.Raycast(floorRay, floorHits, UnityEngine.XR.ARSubsystems.TrackableType.PlaneWithinPolygon
                                                                | UnityEngine.XR.ARSubsystems.TrackableType.PlaneEstimated))
                {
                    sharedFallbackFloorY = floorHits[0].pose.position.y;
                }
            }

            int count = Mathf.Max(1, trigger.Count);
            for (int i = 0; i < count; i++)
            {
                // Always a clone, template included: with earlier flocks left standing, reusing
                // the template for the first egg of every press would teleport it out of the
                // previous flock instead of leaving it behind like all the rest.
                var clone = Instantiate(templateAnchor.gameObject, templateAnchor.parent);
                // The template is inactive — hidden until it is played, since it never is
                // directly — and a clone of an inactive object starts inactive too.
                clone.SetActive(true);
                Transform spawnAnchor = clone.transform;
                // The template's own single child — see BuildFishEggTrigger — the same
                // "child, not the anchor itself" pattern HatchOne's own egg-to-fish handoff uses.
                Transform fallMember = spawnAnchor.childCount > 0 ? spawnAnchor.GetChild(0) : spawnAnchor;

                if (hasEye)
                {
                    // In the eye's own right/up, not world X/Z, so a scatter reads as spread
                    // across the screen regardless of which way the device is actually pointed.
                    Vector2 jitter = UnityEngine.Random.insideUnitCircle * trigger.ScatterRadiusM;
                    Vector3 spread = _eye.transform.right * jitter.x + _eye.transform.up * jitter.y;
                    spawnAnchor.position = anchorPos + spread;
                    spawnAnchor.rotation = facing;
                }

                if (_raycastManager != null)
                    StartCoroutine(SnapEggAnchorToFloorWhenSettled(
                        spawnAnchor, fallMember, trigger.SpawnFallDurationSeconds, gen, sharedFallbackFloorY));

                // Tracked by the object the fall actually drives (one level in from the anchor),
                // not the anchor itself — a ReactFlock beat animates this directly, and the
                // anchor itself is never touched again once it settles.
                if (!_flocks.TryGetValue(trigger.BeatId, out var flock))
                {
                    flock = new List<Transform>();
                    _flocks[trigger.BeatId] = flock;
                }
                flock.Add(fallMember);

                if (i < count - 1 && trigger.StaggerSeconds > 0f)
                    yield return WaitOrSkip(trigger.StaggerSeconds, gen);
            }
        }

        /// <summary>Destroys every member of TargetBeatId's flock outright and forgets it —
        /// nothing grows, nothing hands off. Instant, so no coroutine.</summary>
        private void PlayDespawn(SceneTrigger trigger)
        {
            if (!_flocks.TryGetValue(trigger.TargetBeatId, out var flock)) return;
            foreach (var member in flock) if (member != null) Destroy(member.gameObject);
            flock.Clear();
        }

        /// <summary>Picks KeepCount survivors from TargetBeatId's flock at random, shrinks every
        /// other member away over CullDurationSeconds, and re-registers only the survivors under
        /// this beat's own id — the same consume-and-hand-off every other flock-reaching beat
        /// uses, so a later beat can still reach into whichever fish made it through.</summary>
        private IEnumerator PlayCull(SceneTrigger trigger)
        {
            if (!_flocks.TryGetValue(trigger.TargetBeatId, out var flock) || flock.Count == 0)
                yield break;

            var members = new List<Transform>(flock);
            for (int i = members.Count - 1; i > 0; i--)
            {
                int j = UnityEngine.Random.Range(0, i + 1);
                (members[i], members[j]) = (members[j], members[i]);
            }

            int keep = Mathf.Clamp(trigger.KeepCount, 0, members.Count);

            if (!_flocks.TryGetValue(trigger.BeatId, out var survivors))
            {
                survivors = new List<Transform>();
                _flocks[trigger.BeatId] = survivors;
            }
            for (int i = 0; i < keep; i++)
            {
                if (members[i] != null) survivors.Add(members[i]);
            }

            for (int i = keep; i < members.Count; i++)
            {
                if (members[i] != null) StartCoroutine(FadeAndDestroy(members[i], trigger.CullDurationSeconds));
            }

            flock.Clear();
        }

        /// <summary>Fades every renderer under target to fully transparent, then destroys it —
        /// alpha, not scale, since shrinking to nothing reads as "shrank" rather than "faded
        /// away". Uses Renderer.materials (plural), not sharedMaterials: that instantiates a
        /// per-renderer copy first, so fading this one fish's material does not fade every other
        /// fish still sharing the same imported material asset.</summary>
        private IEnumerator FadeAndDestroy(Transform target, float durationSeconds)
        {
            int gen = _skipGeneration;
            var materials = new List<Material>();
            foreach (var renderer in target.GetComponentsInChildren<Renderer>())
            {
                foreach (var material in renderer.materials)
                {
                    PrepareMaterialForFade(material);
                    materials.Add(material);
                }
            }

            float duration = Mathf.Max(0.01f, durationSeconds);
            float t = 0f;
            while (t < duration)
            {
                if (target == null) yield break;
                t += Time.deltaTime;
                if (_skipGeneration != gen) t = duration;
                float alpha = 1f - Mathf.Clamp01(t / duration);
                foreach (var material in materials) SetMaterialAlpha(material, alpha);
                yield return null;
            }
            if (target != null) Destroy(target.gameObject);
        }

        /// <summary>Switches a material into glTFast's own alpha-blended mode — every model here
        /// imports through "glTF/PbrMetallicRoughness" (see Runtime/Shader/Built-In in the
        /// com.unity.cloud.gltfast package), which is explicitly derived from Unity's Standard
        /// shader and carries the same _Mode/_SrcBlend/_DstBlend/_ZWrite properties Standard's own
        /// Fade/Transparent modes use. Still HasProperty-guarded in case a future model imports
        /// through a different shader (glTFUnlit, or a non-glTFast source) that does not have
        /// them.</summary>
        public static void PrepareMaterialForFade(Material material)
        {
            if (material.HasProperty("_Mode")) material.SetFloat("_Mode", 3f);
            if (material.HasProperty("_Surface")) material.SetFloat("_Surface", 1f);
            if (material.HasProperty("_SrcBlend"))
                material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            if (material.HasProperty("_DstBlend"))
                material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            if (material.HasProperty("_ZWrite")) material.SetInt("_ZWrite", 0);
            material.DisableKeyword("_ALPHATEST_ON");
            material.EnableKeyword("_ALPHABLEND_ON");
            material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        }

        private static void SetMaterialAlpha(Material material, float alpha)
        {
            // glTFast's own Built-in-RP shader ("glTF/PbrMetallicRoughness" — see
            // Runtime/Shader/Built-In/glTFPbrMetallicRoughness.shader in the package, which every
            // model in this project imports through) names its tint "baseColorFactor", not
            // Unity's usual "_Color"/"_BaseColor" — neither of which this shader actually has, so
            // alpha silently never changed until this was found and checked first.
            if (material.HasProperty("baseColorFactor"))
            {
                var color = material.GetColor("baseColorFactor");
                color.a = alpha;
                material.SetColor("baseColorFactor", color);
            }
            else if (material.HasProperty("_BaseColor"))
            {
                var color = material.GetColor("_BaseColor");
                color.a = alpha;
                material.SetColor("_BaseColor", color);
            }
            else if (material.HasProperty("_Color"))
            {
                var color = material.GetColor("_Color");
                color.a = alpha;
                material.SetColor("_Color", color);
            }
        }

        /// <summary>"Fish A" — the lower of TargetBeatId's (usually two, post-Cull) surviving
        /// flock members, picked by Y position rather than flock order, which is otherwise
        /// arbitrary (Cull's KeepCount survivors are chosen at random). "Fish B" is whichever one
        /// is left; this beat never touches it. Fish A tilts its Rotation X to StuntRotationXTarget
        /// over RotateDurationSeconds, pivoting about its own skeleton root rather than the body's
        /// own origin, the same tail/head-pivot technique Jump uses; speeds up whatever it is
        /// already playing (Legacy Animation or Animator, whichever is present) by
        /// StuntSpeedMultiplier for StuntSpeedDurationSeconds; then reverses both: speed drops
        /// back to normal instantly and Rotation X eases back to whatever it started at, again
        /// over RotateDurationSeconds, pivoting the same way; then, once settled, releases
        /// EggCount eggs from its tail.</summary>
        /// <summary>"07-b Rebirth - Spawn": both survivors from "beat-13" swim down to just
        /// above the real floor first (per feedback, "都先游到地面附近"), then Fish A (the lower
        /// of the two once settled) plays the spawning stunt — tilts near-vertical, tail-wags
        /// (speeds up its own already-playing swim clip) — with the egg release now started
        /// partway through that tail-wag rather than after Fish A has already settled back down
        /// (per feedback, "摆尾结束前egg掉落"), then reverses the tilt. Fish B swims up to hover
        /// over wherever the eggs landed once they have had time to (see HoverOverEggs).</summary>
        private IEnumerator PlayStunt(SceneTrigger trigger)
        {
            if (!_flocks.TryGetValue(trigger.TargetBeatId, out var flock) || flock.Count == 0)
                yield break;
            Transform member = null;
            float lowestY = float.MaxValue;
            foreach (var candidate in flock)
            {
                if (candidate == null) continue;
                if (member == null || candidate.position.y < lowestY)
                {
                    member = candidate;
                    lowestY = candidate.position.y;
                }
            }
            if (member == null) yield break;

            int gen = _skipGeneration;

            // Hands off every survivor, not just Fish A — Fish B was never touched by this beat's
            // own tilt/speedup, but still needs to reach beat-15 (which acts on both) and now
            // also gets its own swim-to-ground and swim-above-the-egg moves below.
            if (!_flocks.TryGetValue(trigger.BeatId, out var settled))
            {
                settled = new List<Transform>();
                _flocks[trigger.BeatId] = settled;
            }
            foreach (var survivor in flock) if (survivor != null) settled.Add(survivor);
            Transform other = null;
            foreach (var survivor in flock)
            {
                if (survivor != null && survivor != member) { other = survivor; break; }
            }
            flock.Clear();

            const float swimToGroundSeconds = 2f;
            var groundWait = StartCoroutine(SwimDownToGround(member, swimToGroundSeconds, gen));
            if (other != null) StartCoroutine(SwimDownToGround(other, swimToGroundSeconds, gen));
            yield return groundWait;
            if (member == null) yield break;

            float originalXDegrees = member.eulerAngles.x;
            Quaternion startRotation = member.rotation;
            _swimOffsets.TryGetValue(member, out var originalOffset);

            yield return TiltAroundBone(member, trigger.StuntRotationXTarget, trigger.RotateDurationSeconds, gen);
            if (member == null) yield break;

            SetPlaybackSpeed(member, trigger.StuntSpeedMultiplier);

            if (trigger.EggPrefab != null && trigger.EggCount > 0)
            {
                Transform tailBone = null;
                foreach (var t0 in member.GetComponentsInChildren<Transform>(true))
                {
                    if (t0.name == "TailFinLower_M_010") { tailBone = t0; break; }
                }
                Vector3 tailPos = tailBone != null ? tailBone.position : member.position;
                StartCoroutine(DropEggsFromTail(tailPos, trigger, gen));
                if (other != null) StartCoroutine(HoverOverEggs(other, tailPos, trigger, gen));
            }

            yield return WaitOrSkip(trigger.StuntSpeedDurationSeconds, gen);
            if (member != null) SetPlaybackSpeed(member, 1f);
            if (member == null) yield break;

            yield return TiltAroundBone(member, originalXDegrees, trigger.RotateDurationSeconds, gen);
            if (member == null) yield break;

            // Snap back to the exact original rotation and offset rather than trusting the
            // interpolated result — TiltAroundBone's Euler round-trip can land a hair off after
            // passing near the gimbal-lock pole partway through.
            _swimOffsets[member] = originalOffset;
            member.rotation = startRotation;
        }

        /// <summary>Eases member down to just above the real floor beneath it (raycast against
        /// detected AR planes, same technique the egg drop uses), or a fixed fallback distance below
        /// its current height if no plane is found there — never leaves it hanging at whatever
        /// mid-water height it happened to be swimming at — while carrying it a little way forward
        /// along its own head as it descends.</summary>
        private IEnumerator SwimDownToGround(Transform member, float durationSeconds, int gen)
        {
            if (member == null) yield break;
            const float clearanceM = 0.5f;
            const float lastResortFallM = 1.2f;
            // Per feedback ("两条鱼下降的同时向前游一点"): it does not drop straight down but
            // carries on a little way along its own head as it goes.
            const float forwardWhileDescendingM = 1f;
            float targetY = member.position.y - lastResortFallM;
            if (_raycastManager != null)
            {
                var hits = new List<UnityEngine.XR.ARFoundation.ARRaycastHit>();
                var ray = new Ray(member.position, Vector3.down);
                if (_raycastManager.Raycast(ray, hits, UnityEngine.XR.ARSubsystems.TrackableType.PlaneWithinPolygon
                                                      | UnityEngine.XR.ARSubsystems.TrackableType.PlaneEstimated))
                {
                    targetY = hits[0].pose.position.y + clearanceM;
                }
            }
            Vector3 targetPos = member.position + HeadDirection(member) * forwardWhileDescendingM;
            targetPos.y = targetY;
            yield return SwimOneToward(member, targetPos, durationSeconds);
        }

        /// <summary>Fish B's own move for 07-b: waits out the egg drop's whole staggered window
        /// plus one egg's own fall time — how long it takes the LAST egg released to actually
        /// land — plus a further 2 s per feedback ("egg落地2s后 Rainbow tout B游到egg上方漂浮"),
        /// then swims up to hover directly over where they landed.</summary>
        private IEnumerator HoverOverEggs(Transform member, Vector3 tailPos, SceneTrigger trigger, int gen)
        {
            float wait = Mathf.Max(0f, trigger.EggDropWindowSeconds) + Mathf.Max(0f, trigger.EggFallDurationSeconds) + 2f;
            yield return WaitOrSkip(wait, gen);
            if (member == null) yield break;
            const float hoverHeightM = 1f;
            yield return SwimOneToward(member, tailPos + Vector3.up * hoverHeightM, 2f);
        }

        /// <summary>Tilts member's Rotation X to targetXDegrees over duration, pivoting about its
        /// own skeleton root bone (Root_M_00) rather than the body's own origin —
        /// SkinnedMeshRenderer.bounds is known to be unreliable on this asset (confirmed once
        /// already, on Rainbow Trout's spawn placement), so a real bone position is used instead,
        /// the same technique JumpOne uses for its own tail pivot. Falls back to the body's own
        /// origin (no pivot correction) if the bone isn't found. Shared by Stunt's two tilts and
        /// the lie-down finale so all three pivot identically.</summary>
        private IEnumerator TiltAroundBone(Transform member, float targetXDegrees, float duration, int gen)
        {
            duration = Mathf.Max(0.01f, duration);
            Vector3 targetEuler = member.eulerAngles;
            targetEuler.x = targetXDegrees;

            Quaternion startRotation = member.rotation;
            Quaternion targetRotation = Quaternion.Euler(targetEuler);
            _swimOffsets.TryGetValue(member, out var baseOffset);
            Vector3 startPos = member.position;

            Transform pivotBone = null;
            foreach (var t0 in member.GetComponentsInChildren<Transform>(true))
            {
                if (t0.name == "Root_M_00") { pivotBone = t0; break; }
            }
            Vector3 pivotPoint = pivotBone != null ? pivotBone.position : startPos;
            Vector3 toOrigin = startPos - pivotPoint;
            Debug.Log($"[StuntDiag v5] member={member.name} pivotBoneFound={pivotBone != null} startPos={startPos} pivotPoint={pivotPoint} toOrigin={toOrigin}");

            float t = 0f;
            while (t < duration)
            {
                if (member == null) yield break;
                t += Time.deltaTime;
                if (_skipGeneration != gen) t = duration;
                // Slerp between the two rotations directly, not a per-axis Euler lerp: eulerAngles
                // reads back in [0, 360) — e.g. -89 comes back as 271 — so lerping that range
                // component-wise on the way back swept 271° the long way round instead of the
                // intended 89° turn back. Slerp always takes the shorter arc.
                Quaternion current = Quaternion.Slerp(startRotation, targetRotation, Mathf.Clamp01(t / duration));
                Vector3 offsetDelta = (pivotPoint + (current * Quaternion.Inverse(startRotation)) * toOrigin) - startPos;
                _swimOffsets[member] = baseOffset + offsetDelta;
                member.rotation = current;
                yield return null;
            }
            if (member == null) yield break;
            Vector3 targetOffsetDelta = (pivotPoint + (targetRotation * Quaternion.Inverse(startRotation)) * toOrigin) - startPos;
            _swimOffsets[member] = baseOffset + targetOffsetDelta;
            member.rotation = targetRotation;
        }

        /// <summary>07-c: every survivor of TargetBeatId's flock (both Fish A and Fish B) swims
        /// forward, tilts onto its side the same way Fish A did for 07-b's stunt, holds there for
        /// LieDownHoldSeconds, then fades away over CullDurationSeconds and is destroyed.</summary>
        private IEnumerator PlayLieDownFade(SceneTrigger trigger)
        {
            if (!_flocks.TryGetValue(trigger.TargetBeatId, out var flock) || flock.Count == 0)
                yield break;
            int gen = _skipGeneration;
            foreach (var member in new List<Transform>(flock))
            {
                if (member != null) StartCoroutine(LieDownAndFade(member, trigger, gen));
            }
            flock.Clear();
        }

        private IEnumerator LieDownAndFade(Transform member, SceneTrigger trigger, int gen)
        {
            yield return SwimForward(member, trigger.SwimForwardDistanceM, trigger.SwimForwardDurationSeconds, gen);
            if (member == null) yield break;
            yield return TiltAroundBone(member, trigger.StuntRotationXTarget, trigger.RotateDurationSeconds, gen);
            if (member == null) yield break;
            yield return WaitOrSkip(trigger.LieDownHoldSeconds, gen);
            if (member == null) yield break;
            yield return FadeAndDestroy(member, trigger.CullDurationSeconds);
        }

        /// <summary>Moves member forward (its own current facing) by distance over duration,
        /// additively through _swimOffsets like every other deliberate move here, so it composes
        /// with FishDrift's idle wobble instead of fighting it.</summary>
        private IEnumerator SwimForward(Transform member, float distance, float duration, int gen)
        {
            duration = Mathf.Max(0.01f, duration);
            _swimOffsets.TryGetValue(member, out var baseOffset);
            // Along the model's own head direction, not member.forward: every model is built with
            // its head along local left, so its local +Z is the direction of its side and "forward"
            // swam it sideways.
            Vector3 forwardDelta = HeadDirection(member) * distance;
            float t = 0f;
            while (t < duration)
            {
                if (member == null) yield break;
                t += Time.deltaTime;
                if (_skipGeneration != gen) t = duration;
                _swimOffsets[member] = baseOffset + forwardDelta * Mathf.Clamp01(t / duration);
                yield return null;
            }
            if (member != null) _swimOffsets[member] = baseOffset + forwardDelta;
        }

        /// <summary>07-b's finale: EggCount eggs released from wherever the member's tail settled,
        /// staggered across EggDropWindowSeconds so they don't all pop into existence at once, each
        /// jittered a little horizontally so they don't all fall from the exact same point.</summary>
        private IEnumerator DropEggsFromTail(Vector3 tailPos, SceneTrigger trigger, int gen)
        {
            int count = Mathf.Max(1, trigger.EggCount);
            float window = Mathf.Max(0f, trigger.EggDropWindowSeconds);
            float stagger = count > 1 ? window / (count - 1) : 0f;
            for (int i = 0; i < count; i++)
            {
                Vector2 jitter = UnityEngine.Random.insideUnitCircle * Mathf.Max(0f, trigger.EggJitterRadiusM);
                Vector3 spawnPos = tailPos + new Vector3(jitter.x, 0f, jitter.y);
                var egg = Instantiate(trigger.EggPrefab, spawnPos, Quaternion.identity);
                StartCoroutine(FallEgg(egg.transform, trigger.EggFallDistanceM, trigger.EggFallDurationSeconds, gen));
                if (i < count - 1 && stagger > 0f)
                    yield return WaitOrSkip(stagger, gen);
            }
        }

        /// <summary>Eases anchor from wherever it currently is down to targetPos, with a
        /// quadratic ease-in (accelerating, gravity-like rather than a uniform slide) — used by
        /// SnapEggAnchorToFloorWhenSettled so the egg visibly, vertically falls from spawn height
        /// straight down to the floor once one is found, per feedback ("egg直接从屏幕高度竖直掉
        /// 到地上").</summary>
        private IEnumerator EaseAnchorDown(Transform anchor, Vector3 targetPos, float durationSeconds, int gen)
        {
            if (anchor == null) yield break;
            durationSeconds = Mathf.Max(0.01f, durationSeconds);
            Vector3 start = anchor.position;
            float t = 0f;
            while (t < durationSeconds)
            {
                if (anchor == null) yield break;
                t += Time.deltaTime;
                if (_skipGeneration != gen) t = durationSeconds;
                float p = Mathf.Clamp01(t / durationSeconds);
                anchor.position = Vector3.Lerp(start, targetPos, p * p);
                yield return null;
            }
            if (anchor != null) anchor.position = targetPos;
        }

        /// <summary>The whole of egg's visible "fall": starts looking for a real floor the
        /// instant it spawns (no scripted horizontal drift first — that used to run concurrently
        /// with this and read as sliding sideways before it fell, per feedback), then eases
        /// straight down onto whichever one it finds. Never leaves an egg hanging: if no plane is
        /// ever detected anywhere under it (a room LiDAR has not scanned yet), a last-resort
        /// fallback well below spawn height stands in, so nothing is left floating in mid air
        /// forever, per feedback ("有些停在半空没有掉落").</summary>
        private IEnumerator SnapEggAnchorToFloorWhenSettled(Transform anchor, Transform fallMember, float fallDurationSeconds, int gen, float? fallbackFloorY)
        {
            if (anchor == null || fallMember == null) yield break;
            Vector3 anchorStartPos = anchor.position;

            // Confirmed on a device: a whole flock's worth of these lands scattered across up to
            // a few metres (ScatterRadiusM), and only whichever ones happen to fall over ground
            // already scanned by that moment land correctly — the rest never get a second try. As
            // the operator keeps looking around, more of that area gets scanned over the next
            // several seconds, so retrying here rather than checking once gives every egg, not
            // just the lucky ones, a real chance to land.
            const float retryWindowSeconds = 15f;
            const float retryIntervalSeconds = 0.5f;
            var hits = new List<UnityEngine.XR.ARFoundation.ARRaycastHit>();
            bool hitFloor = false;
            float waited = 0f;
            while (!hitFloor && waited < retryWindowSeconds)
            {
                if (_skipGeneration != gen || anchor == null || fallMember == null) yield break;
                var ray = new Ray(anchorStartPos + Vector3.up * 0.1f, Vector3.down);
                // PlaneEstimated too, not just PlaneWithinPolygon: a plane can exist at this spot
                // without its tracked BOUNDARY having grown to cover it yet (freshly detected
                // planes start small), which reads as "nothing here" even when the floor
                // genuinely has been seen. PlaneEstimated tests the plane's full mathematical
                // extent instead.
                hitFloor = _raycastManager.Raycast(ray, hits, UnityEngine.XR.ARSubsystems.TrackableType.PlaneWithinPolygon
                                                              | UnityEngine.XR.ARSubsystems.TrackableType.PlaneEstimated);
                if (hitFloor)
                {
                    _knownFloorY = hits[0].pose.position.y;
                    Vector3 targetWorldPos = new Vector3(anchorStartPos.x, hits[0].pose.position.y, anchorStartPos.z);
                    Vector3 anchorTarget = anchor.position + (targetWorldPos - fallMember.position);
                    yield return StartCoroutine(EaseAnchorDown(anchor, anchorTarget, fallDurationSeconds, gen));
                }
                else
                {
                    yield return WaitOrSkip(retryIntervalSeconds, gen);
                    waited += retryIntervalSeconds;
                }
            }

            bool usedFallback = false;
            if (!hitFloor && anchor != null && fallMember != null)
            {
                // Nothing was ever scanned directly under this specific egg — rather than leave
                // it hanging at head height, settle it at the floor height measured once under
                // the eye itself when this beat fired (see PlaySceneFlock), a reasonable stand-in
                // for "the floor" in most rooms. And if even THAT probe found nothing (a room
                // LiDAR has scanned nothing at all yet), fall a fixed, plausible distance below
                // spawn height rather than leave the egg hanging in mid air forever, per feedback
                // ("有些停在半空没有掉落") — a wrong floor height is a far smaller problem than an
                // egg that never lands.
                const float lastResortFallM = 1.2f;
                float targetY = fallbackFloorY ?? (anchorStartPos.y - lastResortFallM);
                if (fallbackFloorY.HasValue) _knownFloorY = fallbackFloorY.Value;
                Vector3 targetWorldPos = new Vector3(anchorStartPos.x, targetY, anchorStartPos.z);
                Vector3 anchorTarget = anchor.position + (targetWorldPos - fallMember.position);
                yield return StartCoroutine(EaseAnchorDown(anchor, anchorTarget, fallDurationSeconds, gen));
                usedFallback = true;
            }

            _lastEggDropDiag = $"beat-1 snap   anchorStart {anchorStartPos:F2}"
                                + $"   settledAt {(fallMember != null ? fallMember.position : Vector3.zero):F2}"
                                + $"   hit {hitFloor} ({hits.Count})   fallback {usedFallback}   waited {waited:F1}s";
        }

        /// <summary>Falls to the real floor when a raycast against detected AR planes finds one
        /// (LiDAR-assisted plane detection on a supporting device) — straight down from the
        /// egg's own spawn point, since that is directly below wherever the fish actually is.
        /// Falls back to the fixed distance/duration below when there's no ARRaycastManager (the
        /// desktop scene) or nothing detected yet (floor not scanned at that spot). A single,
        /// immediate check, not a retry: each egg starts falling the instant it spawns, per
        /// feedback ("egg出现后就掉落到地上，不要等所有egg都生成以后才一起掉落") — a retry here
        /// once delayed the start of the fall itself while it waited on a floor, and since every
        /// egg in the batch tends to fail its very first check at the same moment (the floor
        /// nearby not scanned yet) and then all succeed within the same 0.5 s retry tick once it
        /// is, the whole batch read as holding position and then dropping together instead of
        /// each one falling as it appeared.</summary>
        private IEnumerator FallEgg(Transform egg, float distance, float duration, int gen)
        {
            if (egg == null) yield break;
            Vector3 start = egg.position;
            Vector3 end = start + Vector3.down * Mathf.Max(0f, distance);
            bool hitPlane = false;
            int hitCount = 0;
            if (_raycastManager != null)
            {
                var hits = new List<UnityEngine.XR.ARFoundation.ARRaycastHit>();
                var ray = new Ray(start, Vector3.down);
                hitPlane = _raycastManager.Raycast(ray, hits, UnityEngine.XR.ARSubsystems.TrackableType.PlaneWithinPolygon
                                                              | UnityEngine.XR.ARSubsystems.TrackableType.PlaneEstimated);
                hitCount = hits.Count;
                if (hitPlane) end = hits[0].pose.position;
            }
            _lastEggDropDiag = $"raycastManager {_raycastManager != null}   start {start:F2}"
                                + $"   hit {hitPlane} ({hitCount})   end {end:F2}";
            duration = Mathf.Max(0.01f, duration);
            float t = 0f;
            while (t < duration)
            {
                if (egg == null) yield break;
                t += Time.deltaTime;
                if (_skipGeneration != gen) t = duration;
                egg.position = Vector3.Lerp(start, end, Mathf.Clamp01(t / duration));
                yield return null;
            }
            if (egg != null) egg.position = end;
        }

        private static void SetPlaybackSpeed(Transform member, float speed)
        {
            foreach (var animation in member.GetComponentsInChildren<Animation>())
            {
                foreach (AnimationState state in animation) state.speed = speed;
            }
            foreach (var animator in member.GetComponentsInChildren<Animator>())
            {
                animator.speed = speed;
            }
        }

        private IEnumerator PlayRotateFlock(SceneTrigger trigger)
        {
            if (!_flocks.TryGetValue(trigger.TargetBeatId, out var flock)) yield break;
            foreach (var member in flock)
            {
                if (member != null) StartCoroutine(RotateOne(member, trigger));
            }
        }

        /// <summary>One member's own turn, animated over RotateDurationSeconds rather than
        /// snapped, after an independently randomised delay (seeded off the member's own instance
        /// ID, the same pattern JumpOne uses) so a flock turns raggedly instead of in lockstep.
        /// </summary>
        private IEnumerator RotateOne(Transform member, SceneTrigger trigger)
        {
            int gen = _skipGeneration;
            float seed = member.GetInstanceID() * 0.019f;
            float staggerMax = Mathf.Max(0f, trigger.RotateStaggerMaxSeconds);
            float delay = staggerMax > 0f ? Mathf.PerlinNoise(seed, 0f) * staggerMax : 0f;
            yield return WaitOrSkip(delay, gen);
            if (member == null) yield break;

            Quaternion start = member.rotation;
            Quaternion end = start * Quaternion.Euler(0f, trigger.RotateYDegrees, 0f);
            float duration = Mathf.Max(0.01f, trigger.RotateDurationSeconds);
            float t = 0f;
            while (t < duration)
            {
                if (member == null) yield break;
                t += Time.deltaTime;
                if (_skipGeneration != gen) t = duration;
                member.rotation = Quaternion.Slerp(start, end, Mathf.Clamp01(t / duration));
                yield return null;
            }
            if (member != null) member.rotation = end;
        }

        /// <summary>"06-b: Return Home - Swim": every rainbow trout from "beat-9" eases forward
        /// along its own current facing by SwimForwardDistanceM, in place — no swap, no
        /// re-registration, the same as Rotate (06-a) and Jump (06-c) both already do to this
        /// same flock.</summary>
        private IEnumerator PlaySwimFlock(SceneTrigger trigger)
        {
            if (!_flocks.TryGetValue(trigger.TargetBeatId, out var flock)) yield break;
            foreach (var member in flock)
            {
                if (member != null) StartCoroutine(SwimForwardStaggered(member, trigger));
            }
        }

        /// <summary>One member's own forward swim, after an independently randomised delay
        /// (seeded off the member's own instance id, the same pattern RotateOne uses) so a flock
        /// sets off raggedly instead of in lockstep, per feedback ("陆续向头的方向移动").</summary>
        private IEnumerator SwimForwardStaggered(Transform member, SceneTrigger trigger)
        {
            int gen = _skipGeneration;
            float seed = member.GetInstanceID() * 0.023f;
            float staggerMax = Mathf.Max(0f, trigger.RotateStaggerMaxSeconds);
            float delay = staggerMax > 0f ? Mathf.PerlinNoise(seed, 0f) * staggerMax : 0f;
            yield return WaitOrSkip(delay, gen);
            if (member == null) yield break;
            yield return SwimForward(member, trigger.SwimForwardDistanceM, trigger.SwimForwardDurationSeconds, gen);
        }

        private IEnumerator PlayJump(SceneTrigger trigger)
        {
            if (!_flocks.TryGetValue(trigger.TargetBeatId, out var flock)) yield break;
            foreach (var member in flock)
            {
                if (member != null) StartCoroutine(JumpOne(member, trigger));
            }
        }

        /// <summary>One member's own up-and-back-down arc, additive on top of FishDrift's wobble
        /// the same way every other swim offset here is — a fixed sine arc rather than physics,
        /// since it always has to land exactly back where it started. Start time and peak height
        /// are each independently randomised, seeded off the member's own instance ID so a given
        /// member jumps the same way every time this beat fires rather than re-rolling.</summary>
        private IEnumerator JumpOne(Transform member, SceneTrigger trigger)
        {
            int gen = _skipGeneration;
            float seed = member.GetInstanceID() * 0.021f;
            float staggerMax = Mathf.Max(0f, trigger.JumpStaggerMaxSeconds);
            float delay = staggerMax > 0f ? Mathf.PerlinNoise(seed, 0f) * staggerMax : 0f;
            yield return WaitOrSkip(delay, gen);
            if (member == null) yield break;

            float height = Mathf.Max(0.01f, trigger.JumpHeightM) * Mathf.Lerp(0.5f, 1f, Mathf.PerlinNoise(seed, 10f));
            float duration = Mathf.Max(0.01f, trigger.JumpDurationSeconds);

            Transform tail = null;
            foreach (var t0 in member.GetComponentsInChildren<Transform>(true))
            {
                if (t0.name == "TailFinLower_M_010")
                {
                    tail = t0;
                    break;
                }
            }
            Quaternion startRotation = member.rotation;
            // Fixed once, in the fish's own local space, rather than re-found every frame — the
            // tail bone is itself animating (Trout_Swim), and this only needs "roughly where the
            // tail is," not its live position. Falls back to half the trout's own ~13.8 m length,
            // behind rather than ahead, if no such bone is found.
            Vector3 tailLocalOffset = tail != null
                ? member.InverseTransformPoint(tail.position)
                : Vector3.back * 6.9f;
            // The tail's own world-space offset from the body's origin before any tilt — the
            // reference the pivot compensation below measures against.
            Vector3 tailWorldOffset = startRotation * tailLocalOffset;

            // The fish's own head direction, read once: the jump travels along it, and the nose-up /
            // nose-down pitch turns about the horizontal axis square across it — the fish's own side
            // to side axis. The pitch used to turn about world Z, which was square across the head
            // only while every fish faced world -X/+X; once fish face wherever the operator was aimed
            // it was a roll instead — the fish flipped onto its side and back, per feedback ("不要左
            // 右翻动，而应前后翻动"). Positive angle about cross(head, up) lifts the nose.
            Vector3 heading = HeadDirection(member);
            Vector3 pitchAxis = Vector3.Cross(heading, Vector3.up);

            _swimOffsets.TryGetValue(member, out var baseOffset);
            float t = 0f;
            while (t < duration)
            {
                if (member == null) yield break;
                t += Time.deltaTime;
                if (_skipGeneration != gen) t = duration;
                float p = Mathf.Clamp01(t / duration);

                // A true parabola, not a sine arc — 0 at both ends, height at the midpoint — so
                // takeoff and landing read as departing/rejoining the water rather than an
                // arbitrary curve.
                float y = 4f * height * p * (1f - p);
                // Level at takeoff, tilted nose-up at the quarter-point, level again at the peak;
                // mirrored nose-down across the second half — a flutter layered on the trajectory
                // rather than derived from its slope, since following the slope directly (steep
                // at both ends, level only at the exact peak) read as unnatural.
                float pitchDegrees = p <= 0.5f
                    ? Mathf.Sin(p / 0.5f * Mathf.PI) * trigger.JumpPitchDegrees
                    : -Mathf.Sin((p - 0.5f) / 0.5f * Mathf.PI) * trigger.JumpPitchDegrees;

                // About the fish's own side-to-side axis, and around the tail rather than the
                // body's own origin: the amount the tilt alone would have moved the tail is
                // subtracted back out, so the tail stays put and the rest of the body swings
                // around it instead.
                Quaternion tilt = Quaternion.AngleAxis(pitchDegrees, pitchAxis);
                Vector3 pivotCompensation = tailWorldOffset - tilt * tailWorldOffset;

                _swimOffsets[member] = baseOffset
                    + new Vector3(0f, y, 0f)
                    + heading * (p * trigger.JumpForwardM)
                    + pivotCompensation;
                member.rotation = tilt * startRotation;
                yield return null;
            }
            if (member != null)
            {
                _swimOffsets[member] = baseOffset + heading * trigger.JumpForwardM;
                member.rotation = startRotation;
            }
        }

        /// <summary>Whichever member of TargetBeatId's flock sits to ConsumeTargetBeatId's own
        /// member's right and is closest to it swims that way over SwimDurationSeconds;
        /// EatDelaySeconds after
        /// firing, that flock is destroyed outright; GrowDelaySeconds after that,
        /// TargetBeatId's whole flock (the approaching member included — it was never removed,
        /// only nudged) grows in place — no swap, no shake. Re-registered under this beat's own
        /// id, the same handoff every other flock-consuming beat uses.</summary>
        private IEnumerator PlayEatAndGrow(SceneTrigger trigger)
        {
            int gen = _skipGeneration;
            if (!_flocks.TryGetValue(trigger.ConsumeTargetBeatId, out var eaten) || eaten.Count == 0)
                yield break;
            Transform eatenTarget = eaten[0];

            if (!_flocks.TryGetValue(trigger.TargetBeatId, out var flock) || flock.Count == 0)
                yield break;

            if (eatenTarget != null)
            {
                // Only a member behind the strider — on the tail side of it, along the way the
                // flock itself is heading — is a candidate at all; closest overall is not enough
                // on its own. Read off the flock's own facing, not world X, which stopped meaning
                // "behind it" once fish began facing wherever the operator was aimed.
                Vector3 flockHead = FlockHeadDirection(flock);
                Transform closest = null;
                float closestDist = float.MaxValue;
                foreach (var member in flock)
                {
                    if (member == null || Vector3.Dot(member.position - eatenTarget.position, flockHead) >= 0f) continue;
                    float dist = Vector3.Distance(member.position, eatenTarget.position);
                    if (dist < closestDist)
                    {
                        closestDist = dist;
                        closest = member;
                    }
                }
                if (closest != null)
                    StartCoroutine(SwimOneToward(closest, eatenTarget.position, trigger.SwimDurationSeconds));
            }

            yield return WaitOrSkip(trigger.EatDelaySeconds, gen);

            foreach (var member in eaten) if (member != null) Destroy(member.gameObject);
            eaten.Clear();

            yield return WaitOrSkip(trigger.GrowDelaySeconds, gen);

            var targets = new List<Transform>(flock);
            flock.Clear();

            if (!_flocks.TryGetValue(trigger.BeatId, out var grown))
            {
                grown = new List<Transform>();
                _flocks[trigger.BeatId] = grown;
            }

            foreach (var target in targets)
            {
                if (target == null) continue;
                grown.Add(target);
                StartCoroutine(GrowInPlace(target, trigger.GrowMultiplier, trigger.HatchDurationSeconds));
            }
        }

        /// <summary>Eases the fish's swim offset toward wherever targetPos currently is —
        /// additive, and left running alongside FishDrift the same way SwimOneToEye is, so the
        /// wobble keeps going the whole time this plays out. Unlike SwimOneToEye's fixed delta,
        /// this aims at an absolute point.</summary>
        private IEnumerator SwimOneToward(Transform fish, Vector3 targetPos, float durationSeconds)
        {
            _swimOffsets.TryGetValue(fish, out var startOffset);
            float duration = Mathf.Max(0.01f, durationSeconds);
            float t = 0f;
            int gen = _skipGeneration;
            while (t < duration)
            {
                if (fish == null) yield break;
                t += Time.deltaTime;
                if (_skipGeneration != gen) t = duration;
                _swimOffsets.TryGetValue(fish, out var currentOffset);
                Vector3 basePlusWobble = fish.position - currentOffset;
                Vector3 desiredOffset = targetPos - basePlusWobble;
                _swimOffsets[fish] = Vector3.Lerp(startOffset, desiredOffset, Mathf.Clamp01(t / duration));
                yield return null;
            }
        }

        private IEnumerator GrowInPlace(Transform target, float growMultiplier, float durationSeconds)
        {
            Vector3 start = target.localScale;
            Vector3 end = start * (1f + growMultiplier);
            float duration = Mathf.Max(0.01f, durationSeconds);
            float t = 0f;
            int gen = _skipGeneration;
            while (t < duration)
            {
                if (target == null) yield break;
                t += Time.deltaTime;
                if (_skipGeneration != gen) t = duration;
                target.localScale = Vector3.Lerp(start, end, Mathf.Clamp01(t / duration));
                yield return null;
            }
            if (target != null) target.localScale = end;
        }

        /// <summary>Plants copies of Template in front of the eye and lets each one run whatever
        /// Legacy Animation clip it already defaults to — no Timeline, no fall, just "here it is,
        /// doing its own thing". Registered under this beat's own id, same as Spawn, so a later
        /// beat can reach into the flock.</summary>
        private IEnumerator PlayAppear(SceneTrigger trigger)
        {
            if (trigger.Template == null) yield break;
            int gen = _skipGeneration;
            bool useAnchorFlock = !string.IsNullOrEmpty(trigger.AnchorBeatId);
            if (!trigger.UseWorldPosition && !useAnchorFlock) yield return WaitForStableTracking(gen);

            bool hasEye = _eye != null && !trigger.UseWorldPosition && !useAnchorFlock;
            Vector3 anchorPos = trigger.Template.transform.position;
            Quaternion facing = trigger.Template.transform.rotation;
            if (trigger.UseWorldPosition)
            {
                anchorPos = trigger.WorldPosition;
            }
            else if (useAnchorFlock)
            {
                // The anchor beat's own hatch/grow animation (see HatchOne) can still be mid-
                // flight for several seconds after ITS trigger fires — its flock is registered
                // but still empty until each member finishes swapping in. Checking once, right
                // here, raced that window: firing this beat shortly after the anchor beat (the
                // normal operating pace) often found nothing yet and fell all the way back to
                // the template's own build-time position (world origin) — confirmed as the cause
                // of feedback that the Strider never appeared. Retrying for a few seconds gives
                // the anchor beat a real chance to finish first.
                const float anchorWaitSeconds = 8f;
                const float anchorRetryIntervalSeconds = 0.25f;
                float anchorWaited = 0f;
                List<Transform> anchorFlock = null;
                while (anchorWaited < anchorWaitSeconds)
                {
                    if (_skipGeneration != gen) break;
                    if (_flocks.TryGetValue(trigger.AnchorBeatId, out anchorFlock) && anchorFlock.Count > 0)
                        break;
                    yield return WaitOrSkip(anchorRetryIntervalSeconds, gen);
                    anchorWaited += anchorRetryIntervalSeconds;
                }
                if (anchorFlock != null && anchorFlock.Count > 0)
                {
                    // The flock's own centroid, not just its first member — confirmed on a device:
                    // anchoring to a single fish reads as "in front of that one fish", visibly off
                    // to whichever side it happened to end up on once the flock spreads apart
                    // (see SpreadMultiplier), rather than in front of the group as a whole.
                    Vector3 centroid = Vector3.zero;
                    int validCount = 0;
                    foreach (var member in anchorFlock)
                    {
                        if (member == null) continue;
                        centroid += member.position;
                        validCount++;
                    }
                    if (validCount > 0)
                    {
                        centroid /= validCount;
                        // "In front of" and "to the right of" the fish, read off the fish themselves:
                        // every model is built to face Vector3.left at identity rotation and then
                        // turned to face wherever the operator was aiming when it hatched (see
                        // HatchOne), so a fixed world direction lands "in front of them" on the
                        // wrong side as soon as the operator was not facing the way the session
                        // started. Right is the head direction turned a quarter round from above.
                        // head, right and up form an orthonormal basis, so centroid decomposes
                        // cleanly onto them below rather than needing centroid added back in wholesale.
                        //
                        // AnchorForwardFromEye swaps that average for the operator's own current
                        // aim instead — for a placement judged by "directly ahead of the fish, from
                        // where I am standing right now" (the strider) rather than "off to the side
                        // of them, however they happen to be facing" (the heron, which stayed
                        // correctly placed on the fish's own average facing — only the strider's
                        // small, dead-ahead offset made a several-degree mismatch between the two
                        // visible as "off to the side" rather than "basically ahead").
                        Vector3 anchorHead = FlockHeadDirection(anchorFlock);
                        if (trigger.AnchorForwardFromEye && _eye != null)
                        {
                            Vector3 f = _eye.transform.forward;
                            f.y = 0f;
                            if (f.sqrMagnitude > 0.0001f) anchorHead = f.normalized;
                        }
                        Vector3 anchorRight = Vector3.Cross(Vector3.up, anchorHead);
                        // The template faces the same way the fish do, as it always has.
                        facing = YawFromLeftTo(anchorHead);

                        // AnchorToFrontmost only ever adjusts this one scalar — how far along
                        // anchorHead the group's own leading edge reaches — never the axis itself:
                        // an earlier version re-aimed anchorHead at whichever member came out
                        // frontmost, which let one outlier fish that had also drifted sideways
                        // (SpreadMultiplier's own scatter, FishDrift's wobble) rotate the WHOLE
                        // reference frame, including the heron's own "to the right" offset — the
                        // actual cause of it landing behind the fry instead of level with them.
                        float headAxis = Vector3.Dot(centroid, anchorHead);
                        if (trigger.AnchorToFrontmost)
                        {
                            float best = float.NegativeInfinity;
                            foreach (var member in anchorFlock)
                            {
                                if (member == null) continue;
                                float d = Vector3.Dot(member.position, anchorHead);
                                if (d > best) best = d;
                            }
                            if (best > float.NegativeInfinity) headAxis = best;
                        }
                        float rightAxis = Vector3.Dot(centroid, anchorRight);

                        anchorPos = anchorHead * (headAxis + trigger.AnchorOffsetM.x)
                                  + anchorRight * (rightAxis + trigger.AnchorOffsetM.z)
                                  + Vector3.up * (centroid.y + trigger.AnchorOffsetM.y);

                        if (trigger.AnchorOnGround)
                        {
                            // Reuses the floor height 01-a's own egg-drop already confirmed
                            // (_knownFloorY), instead of raycasting again right here, per feedback
                            // ("不要此时读取地面高度，而是存储01-a 所在高度，以此作为地面高度") —
                            // the room's own floor has not moved between beats. A live retry loop
                            // here used to run for several seconds with nothing yet visible before
                            // it resolved, which read as the button not working and invited a
                            // second press once the operator gave up waiting and tried again —
                            // spawning a second copy once both eventually finished ("有时不显示，
                            // 有时突然显示两个"). This resolves instantly instead, whether or not
                            // 01-a has fired yet.
                            float groundY;
                            if (_knownFloorY.HasValue)
                            {
                                groundY = _knownFloorY.Value;
                            }
                            else
                            {
                                // 01-a has not confirmed a floor yet (fired out of the usual
                                // order, or never found one) — a single immediate probe under the
                                // operator's own feet, the same fallback PlaySceneFlock's own
                                // sharedFallbackFloorY uses, rather than a retry loop.
                                groundY = anchorPos.y;
                                if (_eye != null && _raycastManager != null)
                                {
                                    var eyeHits = new List<UnityEngine.XR.ARFoundation.ARRaycastHit>();
                                    var eyeRay = new Ray(_eye.transform.position, Vector3.down);
                                    if (_raycastManager.Raycast(eyeRay, eyeHits, UnityEngine.XR.ARSubsystems.TrackableType.PlaneWithinPolygon
                                                                                 | UnityEngine.XR.ARSubsystems.TrackableType.PlaneEstimated))
                                    {
                                        groundY = eyeHits[0].pose.position.y;
                                    }
                                    else
                                    {
                                        // Nothing scanned anywhere useful yet: the lowest fish's
                                        // own height — they hatched on the floor and only float a
                                        // little above it, so they are the best stand-in left.
                                        float lowestFish = float.MaxValue;
                                        foreach (var member in anchorFlock)
                                        {
                                            if (member != null) lowestFish = Mathf.Min(lowestFish, member.position.y);
                                        }
                                        if (lowestFish != float.MaxValue) groundY = lowestFish;
                                    }
                                }
                            }
                            anchorPos.y = groundY;
                        }
                    }
                }
            }
            else if (hasEye)
            {
                Vector3 forward = _eye.transform.forward;
                if (trigger.FollowCameraPitch)
                {
                    facing = Quaternion.LookRotation(forward);
                }
                else
                {
                    forward.y = 0f;
                    forward = forward.sqrMagnitude < 0.0001f ? Vector3.forward : forward.normalized;
                }
                anchorPos = _eye.transform.position + forward * SceneTriggerDistanceM
                    + _eye.transform.right * trigger.AppearOffsetM.x
                    + _eye.transform.up * trigger.AppearOffsetM.y;
                anchorPos.y = SaneAnchorY(anchorPos.y);
            }

            int count = Mathf.Max(1, trigger.Count);
            for (int i = 0; i < count; i++)
            {
                var clone = Instantiate(trigger.Template);
                // The template is inactive — hidden until it is played — and a clone of an
                // inactive object starts inactive too.
                clone.SetActive(true);

                if (trigger.UseWorldPosition || useAnchorFlock)
                {
                    clone.transform.position = anchorPos;
                    clone.transform.rotation = facing;
                }
                else if (hasEye)
                {
                    Vector2 jitter = UnityEngine.Random.insideUnitCircle * trigger.ScatterRadiusM;
                    Vector3 spread = _eye.transform.right * jitter.x + _eye.transform.up * jitter.y;
                    clone.transform.position = anchorPos + spread;
                    clone.transform.rotation = facing;
                }

                // Plural, not singular: a clone can carry more than one animated child (the
                // nest's three babies, each with its own Animator), and every one of them needs
                // the same clip and speed applied independently.
                foreach (var animation in clone.GetComponentsInChildren<Animation>())
                {
                    animation.wrapMode = WrapMode.Loop;
                    string clipName = trigger.AppearClipName;
                    if (string.IsNullOrEmpty(clipName)) animation.Play();
                    else animation.Play(clipName);

                    if (trigger.AppearClipSpeed > 0f)
                    {
                        var state = animation[string.IsNullOrEmpty(clipName) ? animation.clip.name : clipName];
                        if (state != null) state.speed = trigger.AppearClipSpeed;
                    }
                }

                // Several Animators on one clone (the nest's three babies) start at evenly spread
                // points in the clip's own cycle rather than all in lockstep, so a loop reads as
                // three separate animals rather than one clip times three.
                var animators = clone.GetComponentsInChildren<Animator>();
                for (int a = 0; a < animators.Length; a++)
                {
                    var animator = animators[a];
                    if (!string.IsNullOrEmpty(trigger.AppearClipName))
                    {
                        float phase = animators.Length > 1 ? (float)a / animators.Length : 0f;
                        animator.Play(trigger.AppearClipName, 0, phase);
                    }
                    if (trigger.AppearClipSpeed > 0f) animator.speed = trigger.AppearClipSpeed;
                }

                if (!_flocks.TryGetValue(trigger.BeatId, out var flock))
                {
                    flock = new List<Transform>();
                    _flocks[trigger.BeatId] = flock;
                }
                flock.Add(clone.transform);

                if (i < count - 1 && trigger.StaggerSeconds > 0f)
                    yield return WaitOrSkip(trigger.StaggerSeconds, gen);
            }
        }

        /// <summary>Reaches into TargetBeatId's still-standing flock and plays ClipSequence on
        /// each member in place, one step after another — no spawn, no destroy, and the flock is
        /// left registered exactly as it was, since this is a gesture on something already on
        /// screen rather than a hand-off to a new beat. Unlike Appear's own looped playback, each
        /// step plays once: a gesture like the heron lowering its head is a beat, not an idle
        /// loop.</summary>
        private IEnumerator PlayClipOnFlock(SceneTrigger trigger)
        {
            if (trigger.ClipSequence == null || trigger.ClipSequence.Length == 0) yield break;
            if (!_flocks.TryGetValue(trigger.TargetBeatId, out var flock)) yield break;

            foreach (var member in flock)
            {
                if (member != null) StartCoroutine(PlayClipSequence(member, trigger));
            }

            if (!string.IsNullOrEmpty(trigger.FishSourceBeatId)) StartCoroutine(FeedFishToTarget(trigger, flock));
        }

        /// <summary>Pulls one fish out of FishSourceBeatId's flock — it is removed there, not
        /// consumed-and-reregistered the way ReactFlock/EatAndGrow hand a whole flock onward,
        /// since only a single member is being fed rather than the group progressing to a new
        /// beat. Swims it to FishFollowBoneName on whichever TargetBeatId member actually carries
        /// a Legacy Animation (the heron, not the nest), then tracks that bone every frame — as an
        /// additive offset on top of FishDrift's own wobble, the same pattern SwimOneToEye uses,
        /// rather than taking the fish's Transform over outright. On arrival it is turned to face
        /// up, as if held vertically in the beak. It keeps tracking through FishDisappearAfterSeconds
        /// (timed to the second ClipSequence clip starting), then switches to watching the beak's
        /// own height for a local minimum — a runtime read of the actual lowest point reached
        /// rather than a guessed instant inside the clip — and disappears exactly there.</summary>
        private IEnumerator FeedFishToTarget(SceneTrigger trigger, List<Transform> targetFlock)
        {
            int gen = _skipGeneration;
            Transform target = null;
            foreach (var candidate in targetFlock)
            {
                if (candidate != null && candidate.GetComponentInChildren<Animation>() != null)
                {
                    target = candidate;
                    break;
                }
            }
            if (target == null) yield break;

            if (!_flocks.TryGetValue(trigger.FishSourceBeatId, out var fishFlock) || fishFlock.Count == 0)
                yield break;
            Transform fish = fishFlock[0];
            fishFlock.RemoveAt(0);
            if (fish == null) yield break;

            Transform beak = null;
            foreach (var t in target.GetComponentsInChildren<Transform>(true))
            {
                if (t.name == trigger.FishFollowBoneName)
                {
                    beak = t;
                    break;
                }
            }
            if (beak == null) yield break;

            _swimOffsets.TryGetValue(fish, out var startOffset);
            float swimDuration = Mathf.Max(0.01f, trigger.FishSwimDurationSeconds);
            float t2 = 0f;
            while (t2 < swimDuration)
            {
                if (fish == null || beak == null) yield break;
                t2 += Time.deltaTime;
                if (_skipGeneration != gen) t2 = swimDuration;
                _swimOffsets.TryGetValue(fish, out var currentOffset);
                Vector3 basePlusWobble = fish.position - currentOffset;
                Vector3 desiredOffset = beak.TransformPoint(trigger.FishFollowLocalOffset) - basePlusWobble;
                _swimOffsets[fish] = Vector3.Lerp(startOffset, desiredOffset, Mathf.Clamp01(t2 / swimDuration));
                yield return null;
            }

            // Arrived: "head up", as if the heron is holding it vertically before swallowing. Nose
            // up by a quarter turn about the fish's own side-to-side axis (see JumpOne) — a fixed
            // world Z only lifted the nose while the fish happened to face world -X, and otherwise
            // just rolled it onto its side.
            if (fish != null) fish.Rotate(Vector3.Cross(HeadDirection(fish), Vector3.up), 90f, Space.World);

            float untilSecondClip = Mathf.Max(0f, trigger.FishDisappearAfterSeconds - swimDuration);
            float t3 = 0f;
            while (t3 < untilSecondClip)
            {
                if (fish == null || beak == null) { if (fish != null) Destroy(fish.gameObject); yield break; }
                if (_skipGeneration != gen) break;
                _swimOffsets.TryGetValue(fish, out var currentOffset);
                Vector3 basePlusWobble = fish.position - currentOffset;
                _swimOffsets[fish] = beak.TransformPoint(trigger.FishFollowLocalOffset) - basePlusWobble;
                t3 += Time.deltaTime;
                yield return null;
            }

            // Switching into the second clip (a hard cut now, see PlayClipSequence) can jolt the
            // beak's height for a frame or two — settled through here rather than mistaken for the
            // clip's own real dip.
            // Skipped outright along with the wait above: both exist purely to pace this against
            // the heron's own animation, which a skip has already jumped past.
            float settle = _skipGeneration != gen ? 0f : 0.4f;
            while (settle > 0f)
            {
                if (fish == null || beak == null) { if (fish != null) Destroy(fish.gameObject); yield break; }
                if (_skipGeneration != gen) break;
                _swimOffsets.TryGetValue(fish, out var currentOffset);
                Vector3 basePlusWobble = fish.position - currentOffset;
                _swimOffsets[fish] = beak.TransformPoint(trigger.FishFollowLocalOffset) - basePlusWobble;
                settle -= Time.deltaTime;
                yield return null;
            }

            // From here, track the beak's own height every frame and disappear the instant it
            // stops falling and starts rising again — its actual lowest point, not a guessed
            // fraction of the clip's length.
            float lastHeight = beak != null ? beak.TransformPoint(trigger.FishFollowLocalOffset).y : 0f;
            while (fish != null && beak != null)
            {
                if (_skipGeneration != gen) break;
                _swimOffsets.TryGetValue(fish, out var currentOffset);
                Vector3 basePlusWobble = fish.position - currentOffset;
                _swimOffsets[fish] = beak.TransformPoint(trigger.FishFollowLocalOffset) - basePlusWobble;

                float height = beak.TransformPoint(trigger.FishFollowLocalOffset).y;
                if (height > lastHeight) break;
                lastHeight = height;
                yield return null;
            }

            if (fish != null) Destroy(fish.gameObject);

            // The babies switch off their idle loop to "Clicked" once the fish is gone — spread
            // across evenly staggered starting points in the clip's own cycle, the same way
            // PlayAppear itself staggers several Animators on one clone, so all three reading as
            // one shared reaction rather than a single clip times three.
            foreach (var other in targetFlock)
            {
                if (other == null || other == target) continue;
                var animators = other.GetComponentsInChildren<Animator>();
                for (int a = 0; a < animators.Length; a++)
                {
                    float phase = animators.Length > 1 ? (float)a / animators.Length : 0f;
                    animators[a].Play("Clicked", 0, phase);
                }
            }
        }

        private IEnumerator PlayClipSequence(Transform member, SceneTrigger trigger)
        {
            int gen = _skipGeneration;

            // Keeps the model's feet planted for the whole sequence — see FeetLock. Only on a member
            // that actually carries the named point (the heron, not the nest sharing its beat).
            FeetLock feetLock = null;
            if (!string.IsNullOrEmpty(trigger.LockFeetTransformName))
            {
                foreach (var t0 in member.GetComponentsInChildren<Transform>(true))
                {
                    if (t0.name != trigger.LockFeetTransformName) continue;
                    feetLock = FeetLock.Attach(member, t0);
                    break;
                }
            }

            float lastDuration = 0f;
            for (int i = 0; i < trigger.ClipSequence.Length; i++)
            {
                if (member == null) yield break;

                var step = trigger.ClipSequence[i];
                if (i > 0 && step.DelaySeconds > 0f) yield return WaitOrSkip(step.DelaySeconds, gen);
                if (member == null) yield break;

                // Instant, and only on a member that actually has this clip — otherwise a mixed
                // flock (the heron plus the nest, both under "beat-6") would spin the nest too. A
                // plain turn about the root: the FeetLock above re-plants the feet the same frame,
                // so it reads as spinning on the spot rather than swinging round some other point.
                bool matchesHere = step.RotateYDegrees != 0f && HasNamedClip(member, step.ClipName);
                if (matchesHere) member.Rotate(0f, step.RotateYDegrees, 0f, Space.World);

                // A hard cut, not a blend, into a clip that resets the facing the turn above has
                // already re-applied: a blend would interpolate the model's own baked facing back
                // through the half-turn while the root is already turned, visibly spinning it
                // round again.
                lastDuration = PlayNamedClip(member, step.ClipName, trigger.AppearClipSpeed, matchesHere ? 0f : 0.3f);

                bool isLast = i == trigger.ClipSequence.Length - 1;
                if (!isLast) yield return WaitOrSkip(lastDuration, gen);
            }

            if (feetLock != null)
            {
                yield return WaitOrSkip(lastDuration + 0.5f, gen);
                if (feetLock != null) Destroy(feetLock);
            }
        }

        /// <summary>Whether member carries clipName as a Legacy Animation clip or an Animator
        /// state, without playing anything — used to gate a side effect (like ClipStep's own
        /// rotation) to only the member(s) this step's clip actually matches.</summary>
        private static bool HasNamedClip(Transform member, string clipName)
        {
            foreach (var animation in member.GetComponentsInChildren<Animation>())
            {
                if (animation.GetClip(clipName) != null) return true;
            }
            foreach (var animator in member.GetComponentsInChildren<Animator>())
            {
                if (animator.HasState(0, Animator.StringToHash(clipName))) return true;
            }
            return false;
        }

        /// <summary>Plays clipName on whatever Legacy Animation or Animator components member
        /// carries — guarded by whether each actually has it, since a mixed flock (the heron plus
        /// the nest's babies, both under "beat-6") only has some members carrying any one clip,
        /// and Animator.Play in particular logs an error for a state it can't find rather than
        /// no-oping quietly the way Legacy's Play does. Returns how long the slowest Legacy match
        /// takes at the given speed, so a caller can wait for it before chaining a follow-up clip
        /// — 0 if nothing on member carries clipName.</summary>
        private static float PlayNamedClip(Transform member, string clipName, float speed, float crossFadeSeconds = 0.3f)
        {
            float duration = 0f;
            foreach (var animation in member.GetComponentsInChildren<Animation>())
            {
                if (animation.GetClip(clipName) == null) continue;
                animation.wrapMode = WrapMode.Once;
                // CrossFade, not Play: two clips authored independently rarely share a starting
                // pose, so a hard cut reads as a sudden extra snap on top of whatever the clip
                // itself animates. Blending hides that seam — except where a caller asks for a
                // hard cut on purpose (crossFadeSeconds 0), see PlayClipSequence.
                if (crossFadeSeconds > 0f) animation.CrossFade(clipName, crossFadeSeconds);
                else animation.Play(clipName);
                var state = animation[clipName];
                if (state == null) continue;
                if (speed > 0f) state.speed = speed;
                duration = Mathf.Max(duration, state.length / (speed > 0f ? speed : 1f));
            }

            foreach (var animator in member.GetComponentsInChildren<Animator>())
            {
                int hash = Animator.StringToHash(clipName);
                if (!animator.HasState(0, hash)) continue;
                animator.Play(hash);
                if (speed > 0f) animator.speed = speed;
            }

            return duration;
        }

        /// <summary>Reaches into a Spawn beat's still-standing flock and animates each member in
        /// place: a shake-and-grow, then either a swap for FishPrefab or, with none assigned yet,
        /// a settle. The flock is consumed — this batch will not be reached into again.</summary>
        private IEnumerator PlayHatchReaction(SceneTrigger trigger)
        {
            if (!_flocks.TryGetValue(trigger.TargetBeatId, out var flock) || flock.Count == 0)
                yield break;

            var targets = new List<Transform>(flock);
            flock.Clear();

            // The flock's own centroid, measured before anything hatches — SpreadMultiplier eases
            // each new fish away from THIS point, not away from each other pairwise, so the whole
            // flock expands uniformly outward rather than members near the middle barely moving.
            Vector3 centroid = Vector3.zero;
            int validCount = 0;
            foreach (var target in targets)
            {
                if (target == null) continue;
                centroid += target.position;
                validCount++;
            }
            if (validCount > 0) centroid /= validCount;

            foreach (var target in targets)
            {
                if (target != null) StartCoroutine(HatchOne(target, trigger, centroid));
            }
        }

        private IEnumerator HatchOne(Transform egg, SceneTrigger trigger, Vector3 flockCentroid)
        {
            Vector3 basePosition = egg.position;
            Vector3 startScale = egg.localScale;
            Vector3 endScale = startScale * (1f + trigger.GrowMultiplier);
            // Staggered within the window rather than every egg finishing together, so a flock
            // bursts one after another instead of all at once.
            float duration = UnityEngine.Random.Range(
                Mathf.Max(0.01f, trigger.HatchDurationSeconds * 0.3f),
                Mathf.Max(0.01f, trigger.HatchDurationSeconds));
            // Offsets the noise per egg so a whole flock does not tremble in lockstep.
            float seed = egg.GetInstanceID() * 0.017f;
            float t = 0f;
            int gen = _skipGeneration;

            while (t < duration)
            {
                if (egg == null) yield break;
                t += Time.deltaTime;
                if (_skipGeneration != gen) t = duration;
                float p = Mathf.Clamp01(t / duration);

                // 0.5x per feedback that the pre-hatch shake read too fast — halves how quickly
                // Time.time advances through the Perlin noise, not its amplitude.
                Vector3 jitter = new Vector3(
                    Mathf.PerlinNoise(Time.time * 8.5f + seed, 0f) - 0.5f,
                    Mathf.PerlinNoise(Time.time * 6.5f + seed, 10f) - 0.5f,
                    Mathf.PerlinNoise(Time.time * 9.5f + seed, 20f) - 0.5f) * trigger.ShakeAmplitudeM;

                egg.position = basePosition + jitter;
                egg.localScale = Vector3.Lerp(startScale, endScale, p);
                yield return null;
            }

            if (egg == null) yield break;
            egg.position = basePosition;
            egg.localScale = endScale;

            if (trigger.FishPrefab != null)
            {
                // egg's own pivot, not its Mesh child's: every Build*Prefab centres its mesh by
                // shifting the MESH's local offset until the geometry's centre lands on the
                // root's own origin (see e.g. BuildFishPrefab's bounds-centring), so it is the
                // root — egg here — whose position reliably tracks the visible geometry's centre
                // at runtime; the Mesh child's own position is just whatever that correction
                // left it at, not something to spawn the next stage at. Confirmed as the actual
                // cause of Rainbow Trout not landing on the fry's own spot: this used to read
                // the Mesh child's position instead, which is exactly the position this comment
                // already said not to use. Identity rotation, not egg.rotation, since egg's is
                // whichever way the camera happened to be aimed when it was planted, not a fixed
                // orientation — using it would face the next stage an arbitrary direction instead
                // of the correction baked into the prefab.
                // Head toward wherever the operator is currently facing, not a fixed world
                // direction, per feedback ("每个egg生成的alevin (相对于user) 头朝前，尾朝后").
                // Read fresh per fish rather than shared for the whole flock: HatchOne's own
                // stagger spreads hatching across several seconds, and each fish should face
                // however the device happened to be aimed at ITS OWN moment. A desktop scene with
                // no eye keeps the fixed Vector3.left every model is built to face at identity.
                // A fish swapped for a bigger fish in the same spot (InheritFacing) keeps the
                // facing of the one it replaces instead — turning it to wherever the device points
                // NOW would spin it on the spot if the operator had walked round since.
                Vector3 headDirection = Vector3.left;
                if (trigger.InheritFacing)
                {
                    headDirection = HeadDirection(egg);
                }
                else if (_eye != null)
                {
                    Vector3 f = _eye.transform.forward;
                    f.y = 0f;
                    if (f.sqrMagnitude > 0.0001f) headDirection = f.normalized;
                }
                var fish = Instantiate(trigger.FishPrefab, egg.position, YawFromLeftTo(headDirection));
                // Legacy import (see ALEVIN FISH.glb.meta): an Animation component that plays a
                // clip by name directly. Mecanim was tried first and needs an AnimatorController
                // asset glTFast does not generate, so nothing was ever wired to the Animator.
                var fishAnimation = fish.GetComponentInChildren<Animation>();
                if (fishAnimation != null)
                {
                    // Looping explicitly, not trusting whatever the imported clip defaulted to
                    // — if it defaulted to Once, the fish would already be sitting on its last
                    // frame, static, by the time a later beat moves it.
                    fishAnimation.wrapMode = WrapMode.Loop;
                    string clipName = string.IsNullOrEmpty(trigger.FishSwimClipName) ? "Swim" : trigger.FishSwimClipName;
                    var swimState = fishAnimation[clipName];
                    if (swimState != null) swimState.wrapMode = WrapMode.Loop;
                    fishAnimation.Play(clipName);
                }
                StartDrift(fish.transform);
                if (trigger.SpreadMultiplier > 1f)
                {
                    // The iPad's own current X/Z, not the egg cluster's centroid, as the point
                    // the flock fans out from — per feedback ("最终散开位置以iPad所在位置为基点
                    // 向前方散开") — kept at the flock's own Y so reading it doesn't yank fish up
                    // to eye height (see SpreadFishFromCentroid's own note). Falls back to the
                    // centroid with no eye to read (the desktop scene).
                    Vector3 spreadOrigin = flockCentroid;
                    if (_eye != null)
                    {
                        spreadOrigin = _eye.transform.position;
                        spreadOrigin.y = flockCentroid.y;
                    }
                    // Rises to roughly the operator's own eye height instead of a fixed distance,
                    // per feedback ("缓慢升高到大概iPad所在的高度，让用户从iPad看出去有种在Alevin
                    // 鱼群中的感觉，而不是现在的俯视鱼群") — the fish hatch down at floor height,
                    // so a fixed 0.5 m still left them well below eye level, read from above.
                    float riseM = trigger.SpreadRiseM;
                    if (trigger.SpreadRiseToEyeHeight && _eye != null)
                        riseM = Mathf.Max(0f, _eye.transform.position.y - egg.position.y);
                    StartCoroutine(SpreadFishFromCentroid(
                        fish.transform, egg.position, spreadOrigin, headDirection, trigger.SpreadMultiplier, riseM, trigger.SpreadDurationSeconds));
                }

                // Registered under this Hatch beat's own id — a later ReactFlock/SwimToEye beat
                // targets it by that id, the same way this beat targeted Spawn's.
                if (!_flocks.TryGetValue(trigger.BeatId, out var hatched))
                {
                    hatched = new List<Transform>();
                    _flocks[trigger.BeatId] = hatched;
                }
                hatched.Add(fish.transform);

                Destroy(egg.gameObject);
            }
            // else: no fish model yet — left grown and settled, a visible stand-in until
            // FishPrefab is wired in.
        }

        /// <summary>Eases fish's swim offset from 0 out to a point fanned out from origin —
        /// additive, same as every other scripted move here, so it composes with FishDrift's own
        /// wobble instead of fighting it. The component of spawnPosition's own offset from origin
        /// along headDirection (this fish's own actual facing, set once in HatchOne — see its own
        /// note) is clamped to stay positive before scaling by multiplier, so the flock fans out
        /// in front of origin — head-first, per feedback ("头朝前尾朝后") — rather than exploding
        /// radially, backward included, around wherever each egg happened to scatter. The
        /// sideways component is scaled the same way multiplier always worked, so the flock still
        /// spreads apart from each other instead of collapsing onto a single line. riseM adds a
        /// straight vertical climb over the same duration, per feedback ("散开的过程中同时缓慢升
        /// 高0.5m") — a fish rising while it spreads rather than only fanning outward level.
        /// </summary>
        private IEnumerator SpreadFishFromCentroid(
            Transform fish, Vector3 spawnPosition, Vector3 origin, Vector3 headDirection, float multiplier, float riseM, float durationSeconds)
        {
            Vector3 fromOrigin = spawnPosition - origin;
            float headDistance = Vector3.Dot(fromOrigin, headDirection);
            Vector3 lateral = fromOrigin - headDirection * headDistance;
            // The 0.1 floor guards against an egg that scattered slightly behind origin spreading
            // backward past it — it is meant to be a small nudge, not a deliberate glide. Clamping
            // headDistance itself, before scaling by multiplier, instead floored the SCALED result
            // at 0.1 * multiplier: eggs spawn directly under the eye (SpawnUnderEye), so headDistance
            // starts at or near zero for nearly all of them, and at multiplier 5 the floor alone
            // was already a uniform 0.5 m glide forward for the whole clutch — read, per feedback,
            // as "还是向前游0.5m" once SpreadRiseM's own 0.5 m climb was added alongside it and
            // expected to read as the dominant motion instead.
            const float minForwardM = 0.1f;
            float newHeadDistance = Mathf.Max(headDistance * multiplier, minForwardM);
            Vector3 endOffset = (headDirection * newHeadDistance + lateral * multiplier) - fromOrigin
                               + Vector3.up * riseM;
            float duration = Mathf.Max(0.01f, durationSeconds);
            float t = 0f;
            int gen = _skipGeneration;
            while (t < duration)
            {
                if (fish == null) yield break;
                t += Time.deltaTime;
                if (_skipGeneration != gen) t = duration;
                _swimOffsets[fish] = Vector3.Lerp(Vector3.zero, endOffset, Mathf.Clamp01(t / duration));
                yield return null;
            }
            if (fish != null) _swimOffsets[fish] = endOffset;
        }

        /// <summary>A gentle, endless hover once a fish exists: forward/back, left/right and
        /// up/down within about 0.2 body-lengths of wherever it surfaced — its own spawn point
        /// stays the origin the whole time — so it reads as suspended in water rather than
        /// pinned in place. Stops on its own once the fish is destroyed.</summary>
        private IEnumerator FishDrift(Transform fish)
        {
            var renderers = fish.GetComponentsInChildren<Renderer>();
            float bodyLength = 0.3f;
            if (renderers.Length > 0)
            {
                var bounds = renderers[0].bounds;
                for (int i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);
                bodyLength = Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z);
            }
            // All three axes, 0 to 0.4 body-lengths, capped at 0.2 m. The cap is in absolute metres
            // so a big model does not sway further than a small one needs to for it to read as
            // suspended in water. It used to be 0.0375 m — 3.75 cm either side of where the fish
            // sat, at a pace slow enough that nobody could see it move at all, which is what "现在
            // 并没有浮动" was reporting.
            float amplitude = Mathf.Min(bodyLength * 0.4f, 0.2f);
            float seed = fish.GetInstanceID() * 0.031f;
            Vector3 basePosition = fish.position;

            while (fish != null)
            {
                Vector3 wobble = new Vector3(
                    (Mathf.PerlinNoise(Time.time * 0.22f + seed, 0f) - 0.5f) * (2f * amplitude),
                    (Mathf.PerlinNoise(Time.time * 0.18f + seed, 10f) - 0.5f) * (2f * amplitude),
                    (Mathf.PerlinNoise(Time.time * 0.2f + seed, 20f) - 0.5f) * (2f * amplitude));
                _swimOffsets.TryGetValue(fish, out var swimOffset);
                fish.position = basePosition + wobble + swimOffset;
                yield return null;
            }
            _swimOffsets.Remove(fish);
        }

        /// <summary>Placeholder for the "device has physically moved" case: rather than actually
        /// carrying the flock to wherever the eye now is, each member just nudges a fixed
        /// distance toward its own head. Revisit once every scene's animation is done and the
        /// real device-moved behaviour is worth building. Consumed and re-registered under this
        /// beat's own id, the same handoff Spawn → ReactFlock already uses, so a later beat could
        /// carry this flock onward again.</summary>
        private IEnumerator PlaySwimToEye(SceneTrigger trigger)
        {
            if (!_flocks.TryGetValue(trigger.TargetBeatId, out var flock) || flock.Count == 0)
                yield break;

            var targets = new List<Transform>(flock);
            flock.Clear();

            if (!_flocks.TryGetValue(trigger.BeatId, out var arrived))
            {
                arrived = new List<Transform>();
                _flocks[trigger.BeatId] = arrived;
            }

            // Not a shared fixed direction: each fish's OWN current rotation now bakes in
            // whichever way it was actually facing when it hatched (see HatchOne's own note),
            // since that can differ fish to fish and beat to beat as the operator moves between
            // presses. TransformDirection(Vector3.left) reads that back out per fish rather than
            // assuming they all still face whatever a single shared constant once meant —
            // confirmed on a device this matters: assuming a shared facing read as swimming
            // backward (tail-first) the moment a fish's actual facing did not match it.
            //
            // A fixed nudge rather than a fraction of the fish's own measured size: the Swim
            // animation is playing, and a skinned mesh's bounds while animating reflect whatever
            // pose it happens to be in when sampled — a fin flung wide reads as a much bigger
            // fish than it is, and the nudge grew along with it. Bumped by a further 1m per
            // feedback ("swim的距离再增加1m").
            const float nudgeM = 2f;
            int gen = _skipGeneration;

            foreach (var target in targets)
            {
                if (target == null) continue;
                arrived.Add(target);
                Vector3 towardHead = target.TransformDirection(Vector3.left);
                StartCoroutine(SwimOneToEyeStaggered(target, towardHead * nudgeM, trigger.SwimDurationSeconds, gen));
            }
        }

        /// <summary>Delays SwimOneToEye by a random head start and randomises its own duration a
        /// little, both seeded off the fish's own instance id (same fish, same stagger, every
        /// time this beat fires) — per feedback ("所有alevin不要同时开始和结束向前移动，彼此随机
        /// 错开一些时间"), so a whole flock reads as individually setting off rather than
        /// marching forward and stopping in lockstep.</summary>
        private IEnumerator SwimOneToEyeStaggered(Transform fish, Vector3 delta, float durationSeconds, int gen)
        {
            float seed = fish.GetInstanceID() * 0.031f;
            float delaySeconds = Mathf.PerlinNoise(seed, 0f) * Mathf.Max(0f, durationSeconds) * 0.5f;
            yield return WaitOrSkip(delaySeconds, gen);
            if (fish == null) yield break;
            float ownDuration = Mathf.Max(0.01f, durationSeconds) * Mathf.Lerp(0.7f, 1.3f, Mathf.PerlinNoise(seed, 10f));
            yield return StartCoroutine(SwimOneToEye(fish, delta, ownDuration));
        }

        /// <summary>Eases the fish's swim offset by `delta` on top of whatever it already is —
        /// additive, and left running alongside FishDrift rather than taking the Transform over,
        /// so the wobble keeps going the whole time this plays out.</summary>
        private IEnumerator SwimOneToEye(Transform fish, Vector3 delta, float durationSeconds)
        {
            _swimOffsets.TryGetValue(fish, out var start);
            Vector3 end = start + delta;
            float duration = Mathf.Max(0.01f, durationSeconds);
            float t = 0f;
            int gen = _skipGeneration;
            while (t < duration)
            {
                if (fish == null) yield break;
                t += Time.deltaTime;
                if (_skipGeneration != gen) t = duration;
                _swimOffsets[fish] = Vector3.Lerp(start, end, Mathf.Clamp01(t / duration));
                yield return null;
            }
            if (fish != null) _swimOffsets[fish] = end;
        }

        private void FrameOverview(IReadOnlyList<Vec3> centreline)
        {
            if (_overview == null) return;

            var min = centreline[0].ToVector3();
            var max = min;
            foreach (var point in centreline)
            {
                min = Vector3.Min(min, point.ToVector3());
                max = Vector3.Max(max, point.ToVector3());
            }
            var centre = (min + max) * 0.5f;
            _overview.transform.position = new Vector3(centre.x, max.y + 30f, centre.z);
            _overview.orthographicSize = Mathf.Max(max.x - min.x, max.z - min.z) * 0.65f + 2f;
        }

        private void PaintMarkers()
        {
            foreach (var beat in _journey.beats)
            {
                if (!_markers.TryGetValue(beat.id, out var renderer) || renderer == null) continue;

                Color colour = _progression.StateOf(beat.id) switch
                {
                    BeatState.Approaching => new Color(1f, 0.75f, 0.2f),   // armed
                    BeatState.Committed => new Color(1f, 0.35f, 0.15f),    // firing
                    BeatState.AwaitingAction => new Color(0.9f, 0.3f, 0.9f), // needs a gesture
                    BeatState.Complete => new Color(0.3f, 0.85f, 0.45f),
                    _ => new Color(0.35f, 0.38f, 0.42f),
                };

                if (_flashUntil.TryGetValue(beat.id, out float until) && Time.time < until)
                {
                    colour = Color.Lerp(Color.white, colour, (until - Time.time) / 0.6f);
                }
                renderer.material.color = colour;
            }
        }

        // ------------------------------------------------------------------ the readout

        private void Note(string line)
        {
            _log.Add(line);
            if (_log.Count > 8) _log.RemoveAt(0);
        }

        /// <summary>
        /// IMGUI rather than a Canvas: no prefab, no font asset, no import step, and it survives
        /// a scene rebuilt from a script with nothing to reconnect.
        /// </summary>
        private void OnGUI()
        {
            var style = new GUIStyle(GUI.skin.label)
            {
                fontSize = 15,
                richText = true,
                alignment = TextAnchor.UpperLeft,
            };
            style.normal.textColor = Color.white;

            GUI.color = new Color(0f, 0f, 0f, 0.65f);
            GUI.DrawTexture(new Rect(8, 8, 780, 284), Texture2D.whiteTexture);
            GUI.color = Color.white;

            string connection = _link == null ? "—" : _link.Client.State.ToString();
            string connectionColour = _link != null && _link.Client.State == ControlLinkState.Online
                ? "#7fdca0" : "#ff8f6b";

            var text = new System.Text.StringBuilder();
            text.AppendLine($"<b>Shoaling Upstream — simulation</b>   service {ServiceHost}:{ServicePort}");
            text.AppendLine($"link      <color={connectionColour}>{connection}</color>"
                            + (string.IsNullOrEmpty(_link?.Client.LastNote) ? "" : $"  ({_link.Client.LastNote})"));
            text.AppendLine($"journey   {(_journeyNote.StartsWith("!!") ? $"<color=#ff8f6b>{_journeyNote}</color>" : _journeyNote)}");
            text.AppendLine($"draft     {_lastDraftFetchDiag}");
            // _progression is set right before _link.SetEffects() in Begin() — if this is false,
            // Begin() never got that far (gate refused it, or the journey never loaded at all),
            // which is exactly what "nothing wired to play it" means.
            text.AppendLine($"gate      {_gateNote}   progression-set {_progression != null}");
            text.AppendLine($"settle    {_lastStableTrackingDiag}");
            text.AppendLine($"eggDrop   {_lastEggDropDiag}");
            text.AppendLine($"ar        state {UnityEngine.XR.ARFoundation.ARSession.state}"
                            + $"   notTracking {UnityEngine.XR.ARFoundation.ARSession.notTrackingReason}"
                            + $"   cam.pos {(_eye != null ? _eye.transform.position.ToString("F2") : "—")}");
            if (_flocks.TryGetValue("beat-1", out var beat1Flock) && beat1Flock.Count > 0)
            {
                // The most recently spawned egg, not flock[0] — flock[0] can be left over from a
                // much earlier press (a different camera pose entirely), which is nowhere near
                // whatever is actually on screen right now.
                Transform egg = null;
                for (int i = beat1Flock.Count - 1; i >= 0; i--)
                {
                    if (beat1Flock[i] != null) { egg = beat1Flock[i]; break; }
                }
                if (egg != null)
                {
                    text.AppendLine($"eggLast   pos {egg.position:F2}   localPos {egg.localPosition:F2}"
                                    + $"   parent {(egg.parent != null ? egg.parent.name : "—")}"
                                    + $"   dist-from-cam {(_eye != null ? Vector3.Distance(_eye.transform.position, egg.position) : -1f):F2}");
                    // Where the script's own Camera+Transform data says this egg should draw —
                    // compared against where it actually appears on screen, this is the most
                    // direct possible test of whether rendering is using the same transform the
                    // rest of this HUD reads, or something else entirely (an XR "native display"
                    // matrix, for instance, can differ from the GameObject Transform Unity
                    // exposes to script).
                    Vector3 screenPt = _eye != null ? _eye.WorldToScreenPoint(egg.position) : Vector3.zero;
                    text.AppendLine($"eggScreen screenPt {screenPt:F0}   screen {Screen.width}x{Screen.height}"
                                    + $"   inFront {screenPt.z > 0f}");
                }
            }

            if (_progression != null)
            {
                bool live = _link.Poses.TryGetPose(_link.NowMs, out var pose);
                text.AppendLine($"s         <b>{_progression.S:0.00} m</b>"
                                + $"   lateral {_progression.Lateral:0.00} m"
                                + $"   {(live ? _link.Poses.Origin.ToString().ToLowerInvariant() : "<color=#ff8f6b>no pose — scrub the browser</color>")}");
                text.AppendLine($"beat      {_progression.Current?.id ?? "—"}"
                                + $"   done {_progression.Completed.Count}/{_journey.beats.Count}"
                                + $"   shoal {_shoalCount}");
            }
            else
            {
                text.AppendLine("s         —");
            }
            text.AppendLine($"<color=#9fb4c4>{string.Join("  ·  ", _log)}</color>");

            GUI.Label(new Rect(16, 12, 548, 216), text.ToString(), style);
        }

        // ------------------------------------------------------------------ the bus

        /// <summary>What the operator's buttons are allowed to do. Every method answers whether it
        /// actually did anything, because that answer is the ack.</summary>
        private sealed class Effects : IControlEffects
        {
            private readonly SimulationDriver _driver;
            public Effects(SimulationDriver driver) => _driver = driver;

            public bool FireBeat(string beatId)
            {
                if (_driver._progression == null || string.IsNullOrEmpty(beatId)) return false;
                bool fired = _driver._progression.ForceBeat(beatId);
                if (fired)
                {
                    _driver.Note($"operator fired {beatId}");
                    _driver.PlayScene(beatId);
                }
                return fired;
            }

            /// <summary>Replays whatever beat is currently "current" from a clean rebuild of
            /// everything up to and including it — not a bare re-fire, which used to just plant a
            /// second copy of that beat's flock on top of the first (or, for a beat that consumes
            /// another's flock, find that source already emptied by the first firing and silently
            /// do nothing) — see RebuildJourneyUpTo.</summary>
            public bool ReplayCurrent()
            {
                var current = _driver._progression?.Current;
                if (current == null || _driver._rebuildInProgress) return false;
                _driver.Note($"operator replayed {current.id}");
                _driver.StartCoroutine(_driver.RebuildJourneyUpTo(current.id, includeTarget: true));
                return true;
            }

            /// <summary>Rewinds to right after the PREVIOUS beat finished and before the current
            /// one ever fired, via the same clean rebuild ReplayCurrent uses — see
            /// RebuildJourneyUpTo.</summary>
            public bool Resume()
            {
                var current = _driver._progression?.Current;
                if (current == null || _driver._rebuildInProgress) return false;
                _driver.Note($"operator resumed before {current.id}");
                _driver.StartCoroutine(_driver.RebuildJourneyUpTo(current.id, includeTarget: false));
                return true;
            }

            public bool Advance()
            {
                if (_driver._journey == null) return false;
                foreach (var beatId in AdvanceOrder)
                {
                    if (_driver._progression.StateOf(beatId) == BeatState.Complete) continue;

                    // Whatever is still mid-flight from an earlier beat jumps to its own end
                    // state first, so the skip reads as "finish that, then this" rather than
                    // leaving it running underneath the next beat's own animation. Bumping
                    // _skipGeneration only flags the change — the coroutines that actually act on
                    // it (registering a hatched fish into its own flock, for one) do not run that
                    // code until they next resume, at least a couple of frames out. PlayScene
                    // below is delayed to give them that time; ForceBeat is not — it has to mark
                    // this beat Complete synchronously, in this same call, or an operator mashing
                    // Skip forward faster than that delay would see this same beat as still
                    // incomplete on their very next press and re-queue it instead of advancing,
                    // stalling progress entirely rather than merely misreading an empty flock.
                    _driver.RequestSkipCurrentToEnd();
                    bool fired = _driver._progression.ForceBeat(beatId);
                    if (fired)
                    {
                        _driver.Note($"operator fired {beatId}");
                        _driver.StartCoroutine(PlaySceneDelayed(beatId));
                    }
                    return fired;
                }
                return false;
            }

            private IEnumerator PlaySceneDelayed(string beatId)
            {
                // A single frame is not enough on its own: a skipped HatchOne, for one, needs one
                // frame to notice _skipGeneration changed and a second to fall out of its own
                // while loop and reach its post-loop registration code — and that can chain
                // (01-b's hatch feeding 02-a's swim feeding 02-b's hatch again) if the operator is
                // mashing Skip forward through several beats at once. Several frames costs nothing
                // perceptible and comfortably covers that chain.
                for (int i = 0; i < 10; i++) yield return null;
                _driver.PlayScene(beatId);
            }

            // Refused rather than faked. The audio engine has no mute here, and an operator
            // seeing a button confirmed when nothing happened is worse than seeing it refused.
            public bool Silence() => false;
        }

        private sealed class Status : IControlStatusSource
        {
            private readonly SimulationDriver _driver;
            public Status(SimulationDriver driver) => _driver = driver;

            public ControlStatus Snapshot()
            {
                var progression = _driver._progression;
                return new ControlStatus
                {
                    Site = _driver._journey?.site?.slug,
                    Revision = _driver._journey?.revision,
                    CurrentBeat = progression?.Current?.id,
                    HighWaterMark = progression?.HighWaterMark ?? -1,
                    Completed = progression?.Completed ?? Array.Empty<string>(),
                    ShoalCount = _driver._shoalCount,
                    TrackingConfidence = progression?.Quality == LocalizationQuality.Precise ? 1f : 0f,
                    // S, LateralM and Localization are left null: the client fills them from
                    // whichever pose source is live, which is the one steering the piece.
                };
            }
        }
    }
}
