#if NSDK_PRESENT
using UnityEngine;
using Unity.XR.CoreUtils;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using NianticSpatial.NSDK.AR.VPS2;
using NianticSpatial.NSDK.AR.XRSubsystems;

namespace ShoalingUpstream.Localization.Nsdk
{
    /// <summary>
    /// The one place NSDK meets the piece.
    ///
    /// Everything it does is translation: an <c>ARVps2Manager</c> and an <c>ARVps2Anchor</c> in,
    /// a <see cref="VpsSample"/> out. No decisions are made here — no thresholds, no smoothing,
    /// no holding — because a decision made here could not be tested without a device, an AR
    /// session, a network and a creek.
    /// </summary>
    public class Vps2VpsSource : IVpsSource
    {
        private readonly ARVps2Manager _manager;
        private readonly XROrigin _origin;
        private ARVps2Anchor _anchor;

        public Vps2VpsSource(ARVps2Manager manager, XROrigin origin)
        {
            _manager = manager;
            _origin = origin;
        }

        public ARVps2Anchor Anchor => _anchor;

        public bool TryTrackAnchor(string base64Payload)
        {
            if (_manager == null || string.IsNullOrWhiteSpace(base64Payload)) return false;
            Stop();
            return _manager.TryTrackAnchor(base64Payload, out _anchor);
        }

        public VpsSample Sample()
        {
            var camera = _origin != null ? _origin.Camera : Camera.main;
            if (camera == null) return VpsSample.Nothing;

            // The session pose is deliberately taken from the trackables parent rather than from
            // world space. World space moves whenever the XR Origin is repositioned, and the
            // localizer caches a session-to-journey transform across dropouts — a frame that can
            // shift underneath that cache would corrupt every dead-reckoned position after it.
            var trackables = _origin != null ? _origin.TrackablesParent : null;
            var sessionPose = trackables != null
                ? new Pose(trackables.InverseTransformPoint(camera.transform.position),
                           Quaternion.Inverse(trackables.rotation) * camera.transform.rotation)
                : new Pose(camera.transform.position, camera.transform.rotation);
            // Only claim a session pose while ARKit is actually tracking. This flag is what
            // licenses odometry dead reckoning through a VPS dropout, so a pose from a session
            // that is still initialising or has lost tracking must not be offered as one.
            bool hasSession = ARSession.state == ARSessionState.SessionTracking;

            var state = VpsTrackingState.Unavailable;
            if (_manager != null && _manager.TryGetLatestLocalization(out XRVps2Localization localization))
            {
                state = localization.TrackingState switch
                {
                    Vps2TrackingState.Precise => VpsTrackingState.Precise,
                    Vps2TrackingState.Coarse => VpsTrackingState.Coarse,
                    _ => VpsTrackingState.Unavailable,
                };
            }

            // The session's localization state and the anchor's tracking state are separate
            // facts and the sample carries the conjunction of them. The session can report
            // Precise while this particular anchor is Limited or gone — the payload belongs to a
            // private map this build may not have permission for, or the anchor has been removed
            // — and a pose read in that state is not in the frame the beats were authored in.
            bool anchorUsable = _anchor != null
                             && _anchor.trackingState == TrackingState.Tracking;

            if (!anchorUsable)
            {
                return new VpsSample(
                    state == VpsTrackingState.Precise ? VpsTrackingState.Coarse : state,
                    Pose.identity, false, sessionPose, hasSession,
                    _anchor != null ? _anchor.trackingConfidence : 0f);
            }

            // Authored content is parented to the anchor, so the anchor's local frame is the
            // frame the journey's coordinates live in once editorFrame has been applied.
            var anchorTransform = _anchor.transform;
            var anchorPose = new Pose(
                anchorTransform.InverseTransformPoint(camera.transform.position),
                Quaternion.Inverse(anchorTransform.rotation) * camera.transform.rotation);

            return new VpsSample(state, anchorPose, true, sessionPose, hasSession,
                                 _anchor.trackingConfidence);
        }

        public void Stop()
        {
            if (_manager != null && _anchor != null) _manager.RemoveAnchor(_anchor);
            _anchor = null;
        }
    }
}
#endif
