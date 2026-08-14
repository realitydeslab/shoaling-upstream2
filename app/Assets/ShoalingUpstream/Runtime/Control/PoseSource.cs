using System.Collections.Generic;
using ShoalingUpstream.Journey;
using UnityEngine;

namespace ShoalingUpstream.Control
{
    public enum PoseOrigin { None, Vps, Simulated }

    /// <summary>
    /// One sample of "where the visitor is", complete on both axes.
    ///
    /// It carries the anchor-local point AND the distance along the centreline even though
    /// either can be derived from the other, and that redundancy is the whole point: the two
    /// sources arrive with different halves. VPS gives a point and knows nothing about s; the
    /// editor's scrubber gives s and may not bother with a point. Each source completes its own
    /// sample against the centreline, so <see cref="JourneyProgression"/> downstream receives
    /// the same struct either way and never learns which one it is running on.
    /// </summary>
    public readonly struct PoseSample
    {
        public readonly Vector3 AnchorLocalPosition;
        public readonly float S;
        public readonly float LateralM;
        public readonly float HeadingRad;
        public readonly LocalizationQuality Quality;
        public readonly double SampledAtLocalMs;
        public readonly PoseOrigin Origin;

        public PoseSample(Vector3 anchorLocalPosition, float s, float lateralM, float headingRad,
                          LocalizationQuality quality, double sampledAtLocalMs, PoseOrigin origin)
        {
            AnchorLocalPosition = anchorLocalPosition;
            S = s;
            LateralM = lateralM;
            HeadingRad = headingRad;
            Quality = quality;
            SampledAtLocalMs = sampledAtLocalMs;
            Origin = origin;
        }
    }

    /// <summary>
    /// Where the app's idea of the visitor comes from.
    ///
    /// The seam exists because the piece has to be developed at a desk and performed in a creek,
    /// and the artist's requirement is that those are the same run: "when I simulate on the web
    /// page, in the Unity editor you also need to see the same path simulated, and you can hear
    /// the sound effect from Unity." If the simulated path went down a separate code path, the
    /// thing rehearsed at the desk would not be the thing that runs at the creek, and the only
    /// place that difference would surface is standing in the water.
    ///
    /// So this interface is narrow on purpose: no notion of AR sessions, sockets, or scrubbers.
    /// Everything downstream — trigger machine, audio, telemetry — takes an IPoseSource and
    /// cannot ask which kind it holds.
    /// </summary>
    public interface IPoseSource
    {
        PoseOrigin Origin { get; }

        /// <summary>True while this source has a sample recent enough to steer the piece.</summary>
        bool IsLive(double nowLocalMs);

        bool TryGetPose(double nowLocalMs, out PoseSample pose);
    }

    /// <summary>
    /// Completes a half-specified pose against the creek centreline.
    ///
    /// Shared by both sources so that a simulated s and a localised point land in exactly the
    /// same coordinate, and a discrepancy between the editor's beat markers and the device's
    /// triggers cannot come from two different projections.
    /// </summary>
    public sealed class CentrelineFrame
    {
        private IReadOnlyList<Vec3> _centreline;

        public CentrelineFrame(IReadOnlyList<Vec3> centreline = null) => SetCentreline(centreline);

        public IReadOnlyList<Vec3> Centreline => _centreline;

        public bool IsUsable => _centreline != null && _centreline.Count >= 2;

        public void SetCentreline(IReadOnlyList<Vec3> centreline) => _centreline = centreline;

        public float Length => IsUsable ? Journey.Centreline.Length(_centreline) : 0f;

        /// <summary>Point -> (s, lateral).</summary>
        public (float s, float lateral) Project(Vector3 point)
        {
            if (!IsUsable) return (0f, 0f);
            var projection = Journey.Centreline.Project(point, _centreline);
            return (projection.S, projection.Lateral);
        }

        /// <summary>s -> point on the path. Lateral is zero by construction: a scrubbed walk is
        /// on the line, which is also why a simulated run never exercises lateral tolerance.</summary>
        public Vector3 PointAt(float s) =>
            IsUsable ? Journey.Centreline.PointAt(s, _centreline) : Vector3.zero;
    }
}
