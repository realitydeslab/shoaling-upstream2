namespace ShoalingUpstream.Control
{
    public enum PoseSourceMode
    {
        /// <summary>Simulation takes over while it is live, and hands back when it stops.</summary>
        Auto,
        /// <summary>Ignore the bus entirely. What a device runs on when the piece is performed.</summary>
        VpsOnly,
        /// <summary>Ignore VPS entirely. Useful in the Editor, where there is no localization.</summary>
        SimulatedOnly,
    }

    /// <summary>
    /// The interchange itself: one <see cref="IPoseSource"/> that is either of two.
    ///
    /// Auto is not a convenience, it is the working mode. On a desk there is no VPS pose at all,
    /// so simulation wins by default; on the creek nobody is scrubbing, so VPS wins by default;
    /// and in the one case that actually needs a rule — a phone in the creek while someone at
    /// the laptop drags the scrubber — the operator wins for as long as they are dragging and
    /// the device returns to its own localization a couple of seconds after they stop. That is
    /// the behaviour an operator expects from a control surface, and it needs no mode switch to
    /// get right or wrong under pressure.
    ///
    /// Handover is deliberately abrupt rather than blended. A cross-fade between two positions
    /// on a line produces a walker moving at a speed neither source reported, and the trigger
    /// machine's dwell and hysteresis are calibrated against real walking speed.
    /// </summary>
    public sealed class PoseSourceSwitch : IPoseSource
    {
        private readonly IPoseSource _vps;
        private readonly IPoseSource _simulated;

        public PoseSourceMode Mode = PoseSourceMode.Auto;

        public PoseSourceSwitch(IPoseSource vps, IPoseSource simulated)
        {
            _vps = vps;
            _simulated = simulated;
        }

        /// <summary>Which source last supplied a pose. Reported to the operator so a beat that
        /// fires during a simulated walk is not mistaken for one that fired in the creek.</summary>
        public PoseOrigin Origin => _active;

        private PoseOrigin _active = PoseOrigin.None;

        public bool IsLive(double nowLocalMs) => Choose(nowLocalMs) != null;

        public bool TryGetPose(double nowLocalMs, out PoseSample pose)
        {
            var source = Choose(nowLocalMs);
            if (source is null)
            {
                _active = PoseOrigin.None;
                pose = default;
                return false;
            }
            _active = source.Origin;
            return source.TryGetPose(nowLocalMs, out pose);
        }

        private IPoseSource Choose(double nowLocalMs) => Mode switch
        {
            PoseSourceMode.VpsOnly => _vps.IsLive(nowLocalMs) ? _vps : null,
            PoseSourceMode.SimulatedOnly => _simulated.IsLive(nowLocalMs) ? _simulated : null,
            _ => _simulated.IsLive(nowLocalMs) ? _simulated
               : _vps.IsLive(nowLocalMs) ? _vps
               : null,
        };
    }
}
