using ShoalingUpstream.Journey;
using UnityEngine;

namespace ShoalingUpstream.Control
{
    /// <summary>
    /// The real thing: a pose from VPS localization, reduced to a point on the creek.
    ///
    /// This class deliberately does not know what an AR session is. Whoever owns the tracking —
    /// today an ARFoundation camera against a Niantic anchor, tomorrow whatever replaces it —
    /// calls <see cref="Submit"/> with a camera position already expressed in the anchor's local
    /// frame. Keeping the localization plumbing on the other side of that one method is what
    /// lets the whole downstream chain be tested in EditMode with no device present.
    /// </summary>
    public sealed class VpsPoseSource : IPoseSource
    {
        /// <summary>
        /// How long a pose stays usable after the last submission.
        ///
        /// Longer than a dropped frame, shorter than a walk. ARFoundation keeps reporting a
        /// pose from inertial tracking through a relocalization gap, so a stale sample here
        /// means the app itself has stopped submitting — a paused session, a backgrounded app —
        /// and continuing to trigger beats from it would fire them at a phone in a pocket.
        /// </summary>
        public double FreshnessMs = 1500;

        private readonly CentrelineFrame _frame;
        private PoseSample _latest;
        private bool _hasSample;

        public VpsPoseSource(CentrelineFrame frame) => _frame = frame;

        public PoseOrigin Origin => PoseOrigin.Vps;

        public bool IsLive(double nowLocalMs) =>
            _hasSample && nowLocalMs - _latest.SampledAtLocalMs <= FreshnessMs;

        public bool TryGetPose(double nowLocalMs, out PoseSample pose)
        {
            pose = _latest;
            return IsLive(nowLocalMs);
        }

        /// <param name="anchorLocalPosition">Camera position in the VPS anchor's local frame.</param>
        /// <param name="headingRad">Camera yaw about the anchor's up axis.</param>
        public void Submit(Vector3 anchorLocalPosition, float headingRad,
                           LocalizationQuality quality, double nowLocalMs)
        {
            var (s, lateral) = _frame.Project(anchorLocalPosition);
            _latest = new PoseSample(anchorLocalPosition, s, lateral, headingRad,
                                     quality, nowLocalMs, PoseOrigin.Vps);
            _hasSample = true;
        }

        /// <summary>Tracking lost entirely. Reported rather than simply stopping, so the
        /// operator's window says "unavailable" instead of ageing a last-known position.</summary>
        public void SubmitLost(double nowLocalMs)
        {
            _latest = new PoseSample(_latest.AnchorLocalPosition, _latest.S, _latest.LateralM,
                                     _latest.HeadingRad, LocalizationQuality.Unavailable,
                                     nowLocalMs, PoseOrigin.Vps);
            _hasSample = true;
        }
    }
}
