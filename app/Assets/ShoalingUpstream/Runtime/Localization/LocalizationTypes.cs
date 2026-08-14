using UnityEngine;
using ShoalingUpstream.Journey;

namespace ShoalingUpstream.Localization
{
    /// <summary>
    /// Mirrors NSDK's <c>Vps2TrackingState</c> without depending on it.
    ///
    /// The mirror exists so that everything above it — the degradation ladder, the journey-space
    /// maths, the operator report — compiles and runs with no AR session, no device and no
    /// network. Those are exactly the paths that cannot be rehearsed at a creek.
    /// </summary>
    public enum VpsTrackingState { Unavailable, Coarse, Precise }

    /// <summary>Where the reported position actually came from. Never inferred by the reader.</summary>
    public enum FixSource
    {
        /// <summary>Nothing yet.</summary>
        None,

        /// <summary>A live VPS pose against the tracked anchor. The only measured-against-the-world source.</summary>
        Anchor,

        /// <summary>Propagated since the last anchor pose. See <see cref="DeadReckonBasis"/> for how.</summary>
        DeadReckoned,

        /// <summary>No anchor was ever obtained; the walk is being followed without a world frame.</summary>
        Unanchored,
    }

    /// <summary>
    /// How a dead-reckoned position was propagated. The two are an order of magnitude apart in
    /// error, so the operator has to be able to tell them apart.
    /// </summary>
    public enum DeadReckonBasis
    {
        None,

        /// <summary>AR session odometry (VIO) carried through the cached session-to-journey
        /// transform. Drifts at roughly a percent of distance walked — good for tens of seconds.</summary>
        Odometry,

        /// <summary>A decayed estimate of the visitor's along-stream speed. Blind; good for a few
        /// seconds and bounded hard.</summary>
        SpeedDecay,

        /// <summary>Past every horizon. The position is frozen and is no longer being claimed.</summary>
        Frozen,
    }

    public enum LocalizationMode
    {
        /// <summary>Uncalibrated journey on a device build. Terminal, deliberate, and loud.</summary>
        Refused,

        /// <summary><see cref="VpsLocalizer.Begin"/> has not been called.</summary>
        Idle,

        /// <summary>Anchor in hand, waiting for the first precise fix.</summary>
        Searching,

        /// <summary>Has had a precise fix. May currently be dead-reckoning.</summary>
        Tracking,

        /// <summary>Running the walk with no world frame, because silence is worse.</summary>
        Unanchored,

        /// <summary>Neither anchor route produced a payload.</summary>
        Failed,
    }

    /// <summary>
    /// Device or simulation, stated rather than sniffed.
    ///
    /// The calibration gate turns on this value, so it must be an explicit decision somebody
    /// made and not a platform define that happened to be set. A simulation build that quietly
    /// believed it was a device build — or worse, the reverse — is precisely the failure the
    /// gate exists to prevent.
    /// </summary>
    public enum RuntimeSurface { Device, Simulation }

    /// <summary>
    /// One frame of everything the AR stack can tell us. Both poses are optional and are
    /// separately optional: VPS can drop while ARKit keeps tracking, which is the common case
    /// under canopy and the case the degradation ladder is built around.
    /// </summary>
    public readonly struct VpsSample
    {
        public readonly VpsTrackingState State;

        /// <summary>Camera pose in the tracked anchor's local frame. Valid only when
        /// <see cref="HasAnchorPose"/>.</summary>
        public readonly Pose AnchorPose;
        public readonly bool HasAnchorPose;

        /// <summary>Camera pose in the AR session's own frame. Available whenever ARKit is
        /// tracking, VPS or no VPS — this is what makes odometry dead reckoning possible.</summary>
        public readonly Pose SessionPose;
        public readonly bool HasSessionPose;

        /// <summary><c>ARVps2Anchor.trackingConfidence</c>, verbatim. Its scale is undocumented,
        /// so nothing here thresholds on it by default.</summary>
        public readonly float TrackingConfidence;

        /// <summary>How old the anchor pose already was when the SDK handed it over, if known.
        /// Added to our own staleness clock so a pose that arrives late is not mistaken for fresh.</summary>
        public readonly float PoseAgeSeconds;

        public VpsSample(
            VpsTrackingState state,
            Pose anchorPose, bool hasAnchorPose,
            Pose sessionPose, bool hasSessionPose,
            float trackingConfidence = 1f,
            float poseAgeSeconds = 0f)
        {
            State = state;
            AnchorPose = anchorPose;
            HasAnchorPose = hasAnchorPose;
            SessionPose = sessionPose;
            HasSessionPose = hasSessionPose;
            TrackingConfidence = trackingConfidence;
            PoseAgeSeconds = poseAgeSeconds;
        }

        /// <summary>Nothing at all this frame — no VPS, no ARKit.</summary>
        public static VpsSample Nothing =>
            new(VpsTrackingState.Unavailable, Pose.identity, false, Pose.identity, false, 0f);
    }

    /// <summary>
    /// The position the rest of the piece runs on.
    ///
    /// <see cref="Quality"/> is what <see cref="JourneyProgression"/> should be told, and it is
    /// a judgement: a bounded amount of dead reckoning is reported as <c>Precise</c> because a
    /// beat firing two metres late beats a beat that never fires. <see cref="Source"/> and
    /// <see cref="Confidence"/> carry the truth underneath, for audio and for the operator.
    /// </summary>
    public readonly struct LocalizationFix
    {
        public readonly FixSource Source;
        public readonly DeadReckonBasis Basis;
        public readonly LocalizationQuality Quality;

        /// <summary>Distance along <c>site.centreline</c>, in journey coordinates.</summary>
        public readonly float S;

        /// <summary>Cross-stream distance from the centreline. Diagnostic only — nothing gates on
        /// it, because on a linear reach it is almost entirely pose noise.</summary>
        public readonly float Lateral;

        public readonly Vector3 JourneyPosition;

        /// <summary>Along-stream speed estimate, m/s. Signed: negative is downstream.</summary>
        public readonly float SpeedMps;

        /// <summary>Seconds since the last measured anchor pose.</summary>
        public readonly float AgeSeconds;

        /// <summary>0..1, ours, derived from staleness and basis. Not the SDK's number.</summary>
        public readonly float Confidence;

        public LocalizationFix(
            FixSource source, DeadReckonBasis basis, LocalizationQuality quality,
            float s, float lateral, Vector3 journeyPosition,
            float speedMps, float ageSeconds, float confidence)
        {
            Source = source; Basis = basis; Quality = quality;
            S = s; Lateral = lateral; JourneyPosition = journeyPosition;
            SpeedMps = speedMps; AgeSeconds = ageSeconds; Confidence = confidence;
        }

        public static LocalizationFix None => new(
            FixSource.None, DeadReckonBasis.None, LocalizationQuality.Unavailable,
            0f, 0f, Vector3.zero, 0f, 0f, 0f);
    }

    /// <summary>
    /// What the operator sees on the controller page.
    ///
    /// This deliberately carries the SDK's raw state and raw confidence alongside our derived
    /// ones. The controller is a safety net, and a safety net that has been told a smoothed
    /// story cannot be used to decide whether to intervene. If we are dead-reckoning, it says
    /// so; if we are guessing, it says that too.
    /// </summary>
    public readonly struct LocalizationReport
    {
        public readonly LocalizationMode Mode;

        /// <summary>Exactly what NSDK reported this frame. Never smoothed, never held over.</summary>
        public readonly VpsTrackingState RawState;

        /// <summary>Exactly what <c>trackingConfidence</c> reported. Unscaled and uninterpreted.</summary>
        public readonly float RawConfidence;

        public readonly LocalizationFix Fix;
        public readonly AnchorRoute Route;

        /// <summary>Metres the position jumped when the last estimate was corrected by a real
        /// fix. Audio needs this to crossfade instead of clicking; the operator needs it because
        /// a large snap means the estimate had drifted.</summary>
        public readonly float SnapMetres;

        /// <summary>One plain sentence, for a human reading it while walking beside a visitor.</summary>
        public readonly string Detail;

        public LocalizationReport(
            LocalizationMode mode, VpsTrackingState rawState, float rawConfidence,
            LocalizationFix fix, AnchorRoute route, float snapMetres, string detail)
        {
            Mode = mode; RawState = rawState; RawConfidence = rawConfidence;
            Fix = fix; Route = route; SnapMetres = snapMetres; Detail = detail;
        }

        /// <summary>The <c>localization</c> field of the WebSocket status message
        /// (see docs/configuration.md). Derived from the quality we act on, so the operator page
        /// and the trigger machine can never disagree about what fired.</summary>
        public string StatusWord => Fix.Quality switch
        {
            LocalizationQuality.Precise => "precise",
            LocalizationQuality.Coarse => "coarse",
            _ => "unavailable",
        };

        public override string ToString() => Detail;
    }

    /// <summary>
    /// The seam. Everything NSDK-shaped lives behind this one interface, so the localizer,
    /// the maths and every degradation path are exercised in EditMode against synthetic poses.
    /// </summary>
    public interface IVpsSource
    {
        /// <summary>Begin tracking the anchor described by a base64 payload. False if the SDK
        /// refused it outright — a malformed payload, or a private map this build cannot see.</summary>
        bool TryTrackAnchor(string base64Payload);

        /// <summary>This frame's view of the world. Called once per Update.</summary>
        VpsSample Sample();

        void Stop();
    }
}
