using ShoalingUpstream.Journey;
using UnityEngine;

namespace ShoalingUpstream.Control
{
    /// <summary>
    /// The desk: the browser editor's walk simulation, arriving over the control bus.
    ///
    /// It reports <see cref="LocalizationQuality.Precise"/> because a simulated walker is, by
    /// construction, exactly where it says it is. That is not a lie the code tells itself — it
    /// is the point. The trigger machine gates on localization quality, so a simulated run that
    /// reported anything less would silently exercise none of the behaviour being rehearsed.
    ///
    /// The one thing simulation cannot rehearse is losing tracking, which is why the VPS source
    /// keeps its own quality field rather than this one modelling dropout.
    /// </summary>
    public sealed class SimulatedPoseSource : IPoseSource
    {
        /// <summary>
        /// How long a simulated pose keeps the piece.
        ///
        /// This doubles as the lease in <see cref="PoseSourceSwitch"/>: when the operator stops
        /// scrubbing, or the laptop goes away, the simulation must expire rather than freeze the
        /// visitor at the last scrubbed position. Two seconds is long enough to ride out a wifi
        /// stall between scrub frames and short enough that closing the browser hands the device
        /// back to VPS before anyone notices.
        /// </summary>
        public double FreshnessMs = 2000;

        private readonly CentrelineFrame _frame;
        private PoseSample _latest;
        private bool _hasSample;

        public SimulatedPoseSource(CentrelineFrame frame) => _frame = frame;

        public PoseOrigin Origin => PoseOrigin.Simulated;

        public bool IsLive(double nowLocalMs) =>
            _hasSample && nowLocalMs - _latest.SampledAtLocalMs <= FreshnessMs;

        public bool TryGetPose(double nowLocalMs, out PoseSample pose)
        {
            pose = _latest;
            return IsLive(nowLocalMs);
        }

        /// <summary>Forget the simulated walker immediately, rather than waiting out the lease.
        /// Used when the socket drops: an operator who has vanished is not still scrubbing.</summary>
        public void Clear() => _hasSample = false;

        /// <summary>
        /// Apply a pose from the editor, filling in whichever half it did not send.
        ///
        /// The editor is authoritative about s and casual about xyz — its scrubber works in
        /// distance along the path. Reconstructing the missing half here, against the same
        /// centreline the device projects onto, is what makes the two sources interchangeable
        /// rather than merely similar.
        /// </summary>
        public void Apply(SimulatedPose pose, double nowLocalMs)
        {
            Vector3 position;
            float s;
            float lateral;

            if (pose.HasS)
            {
                s = pose.S;
                position = pose.HasPosition ? pose.Position : _frame.PointAt(s);
                lateral = pose.HasPosition ? _frame.Project(pose.Position).lateral : 0f;
            }
            else
            {
                position = pose.Position;
                (s, lateral) = _frame.Project(position);
            }

            _latest = new PoseSample(position, s, lateral, pose.HeadingRad,
                                     LocalizationQuality.Precise, nowLocalMs, PoseOrigin.Simulated);
            _hasSample = true;
        }
    }
}
