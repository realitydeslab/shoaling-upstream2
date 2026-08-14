using System;
using System.Collections.Generic;
using UnityEngine;

namespace ShoalingUpstream.Journey
{
    /// <summary>
    /// The journey manifest, mirroring service/src/journey-schema.mjs.
    /// This is the only contract shared with the editor and the service; everything else on
    /// either side is free to change.
    /// </summary>
    [Serializable]
    public class JourneyDocument
    {
        public string schemaVersion;
        public string journeyId;
        public string title;
        public int revision;
        public SiteRef site;
        public EditorFrame editorFrame;
        public List<Beat> beats = new();
        public List<AmbientSource> ambient = new();
        public Shoal shoal;
        public Safety safety;

        public const string SupportedSchemaVersion = "2.0";
    }

    [Serializable]
    public class SiteRef
    {
        public string slug;
        public string nianticOrgId;
        public string nianticSiteId;
        public string vpsAssetId;

        /// <summary>
        /// Base64 VPS anchor payload. May be empty: the NSDK Sites API can supply it at
        /// runtime from AssetInfo.VpsData.AnchorPayload, which survives asset re-promotion
        /// where a baked payload would not.
        /// </summary>
        public string anchorPayload;

        /// <summary>The creek axis. Everything the runtime gates on is distance along this.</summary>
        public List<Vec3> centreline = new();

        public UpstreamAxis upstreamAxis;
    }

    [Serializable] public class UpstreamAxis { public string kind; public string risesToward; }

    [Serializable]
    public class EditorFrame
    {
        public string splatFile;

        /// <summary>
        /// False until three physical points have been matched on site.
        ///
        /// The most dangerous mistake available in this system is authoring coordinates in
        /// splat space and shipping them as anchor space. The app refuses to run an
        /// uncalibrated journey against a real VPS anchor, while still running it in
        /// simulation, so the failure is loud rather than a quietly wrong soundscape.
        /// </summary>
        public bool calibrated;

        public float[] rotation = { 0f, 0f, 0f, 1f };
        public Vec3 translation;
        public float scale = 1f;
    }

    [Serializable]
    public class Beat
    {
        public string id;
        public string title;
        public string prompt;
        public string interaction;
        public Vec3 position;

        /// <summary>Distance along the centreline. This, not the 3D position, is what gates.</summary>
        public float s;

        public Trigger trigger;
        public BeatAudio audio;
        public int givesFish;

        public InteractionKind Kind => interaction switch
        {
            "proximity" => InteractionKind.Proximity,
            "crouch"    => InteractionKind.Crouch,
            "catch"     => InteractionKind.Catch,
            "give"      => InteractionKind.Give,
            "lift"      => InteractionKind.Lift,
            _            => InteractionKind.Proximity,
        };
    }

    public enum InteractionKind { Proximity, Crouch, Catch, Give, Lift }

    [Serializable]
    public class Trigger
    {
        public float enterRadiusM;

        /// <summary>Always greater than enter. The gap is hysteresis: with a single radius a
        /// beat fires and silences repeatedly when someone stands near its boundary, and pose
        /// jitter guarantees they will.</summary>
        public float exitRadiusM;

        public float dwellSeconds;

        /// <summary>Once committed, a beat cannot be exited for this long regardless of
        /// position. At 6-10 m spacing this, not hysteresis, is what prevents thrash: it
        /// changes the question from "am I inside the zone" to "which beat am I performing".</summary>
        public float minimumHoldSeconds;

        public bool requiresPreviousComplete;
    }

    [Serializable]
    public class BeatAudio
    {
        public AudioLayer far;
        public AudioLayer mid;
        public AudioLayer intimate;
        public AudioLayer completion;
    }

    [Serializable]
    public class AudioLayer
    {
        public string clipId;
        public float gainDb = -8f;
        public bool loop = true;
    }

    [Serializable]
    public class AmbientSource
    {
        public string id;
        public string title;
        public Vec3 position;
        public float audibleRadiusM;
        public BeatAudio audio;
    }

    [Serializable] public class Shoal { public int startingCount = 40; public int minimumCount = 6; }
    [Serializable] public class Safety { public string shortText; public string fullText; }

    /// <summary>JsonUtility cannot deserialize Vector3 from {x,y,z} reliably across versions,
    /// so the wire format gets its own type.</summary>
    [Serializable]
    public struct Vec3
    {
        public float x, y, z;
        public Vector3 ToVector3() => new(x, y, z);
        public static Vec3 From(Vector3 v) => new() { x = v.x, y = v.y, z = v.z };
        public override string ToString() => $"({x:F2}, {y:F2}, {z:F2})";
    }
}
