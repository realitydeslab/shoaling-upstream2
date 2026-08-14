using System;
using System.Diagnostics;

namespace ShoalingUpstream.Control
{
    /// <summary>
    /// A local millisecond clock that is comparable to the server's <c>Date.now()</c> but does
    /// not jump.
    ///
    /// Both properties are needed and neither alone is enough. The protocol's timestamps are
    /// Unix milliseconds, so the base has to be wall clock. But iOS steps the wall clock when it
    /// picks up network time — often minutes after launch, which is exactly when a phone
    /// reaches a park's wifi — and a step of even a second would make every command already in
    /// the scheduler look expired. So the epoch is sampled once and advanced by a monotonic
    /// stopwatch afterwards. Any real drift between this and the server is then absorbed by
    /// <see cref="ServerClock"/>, which is measuring that difference continuously anyway.
    /// </summary>
    public sealed class LocalClock
    {
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private readonly double _epochMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        public double NowMs => _epochMs + _stopwatch.Elapsed.TotalMilliseconds;
    }

    /// <summary>
    /// Estimates the offset between this device's clock and the service's.
    ///
    /// Every command carries absolute server timestamps, and the device has to decide "is this
    /// moment in my future or my past". Two clocks on a park's wifi, one of them a phone that
    /// has been asleep, are routinely tens to hundreds of milliseconds apart — comfortably
    /// larger than the 400 ms lead the bus schedules with. Guessing wrong in one direction plays
    /// every sound late; guessing wrong in the other drops every command as expired.
    ///
    /// The estimator is the NTP one, cut down. A heartbeat is sent at local t0; the pong comes
    /// back at local t1 carrying the server's own now:
    ///
    ///     rtt    = t1 - t0
    ///     offset = serverNowMs + rtt/2 - t1        (server clock minus local clock)
    ///
    /// The rtt/2 assumes the two legs are symmetric, which is where all the error lives. The
    /// standard fix applies here for the standard reason: keep several samples and use the one
    /// with the LOWEST round trip, never the mean. Wifi latency is heavily right-tailed — a
    /// handful of samples delayed by a retransmit will drag an average badly, and a long round
    /// trip is precisely the one most likely to be asymmetric. The minimum-rtt sample is the
    /// closest thing to an uncontended measurement, and its error is bounded by rtt/2.
    /// </summary>
    public sealed class ServerClock
    {
        /// <summary>
        /// Above this uncertainty the offset is not trusted, and schedules fall back to relative
        /// timing. Set against the bus's own 400 ms lead: if we cannot place the fire moment to
        /// better than half of that, the absolute timestamp is no longer telling us anything the
        /// lead does not.
        /// </summary>
        public double MaxTrustedUncertaintyMs = 200;

        /// <summary>
        /// A change larger than this is a clock step, not drift, and is adopted whole rather
        /// than averaged in. Phones re-sync their clocks mid-session; smoothing across such a
        /// step would leave the device wrong in both directions for as long as the window.
        /// </summary>
        public double StepThresholdMs = 500;

        private const int WindowSize = 8;

        private readonly double[] _offsets = new double[WindowSize];
        private readonly double[] _rtts = new double[WindowSize];
        private int _count;
        private int _next;

        public bool HasEstimate { get; private set; }

        /// <summary>Server clock minus local clock, in milliseconds.</summary>
        public double OffsetMs { get; private set; }

        /// <summary>Half the best round trip: the residual we cannot resolve by measurement.</summary>
        public double UncertaintyMs { get; private set; } = double.PositiveInfinity;

        /// <summary>How many samples ago the estimator last stepped rather than refined. Read by
        /// the client so a step can be logged — a large one usually means something worth
        /// knowing about the laptop, not the phone.</summary>
        public int StepCount { get; private set; }

        public bool IsTrusted => HasEstimate && UncertaintyMs <= MaxTrustedUncertaintyMs;

        /// <summary>Feed one completed heartbeat/pong round trip.</summary>
        public void Observe(double sentAtLocalMs, double receivedAtLocalMs, double serverNowMs)
        {
            double rtt = receivedAtLocalMs - sentAtLocalMs;
            if (rtt < 0) return;   // a clock step landed inside the round trip; the sample is junk

            double offset = serverNowMs + rtt / 2.0 - receivedAtLocalMs;

            // A step invalidates the window rather than joining it: the old samples describe a
            // relationship between the clocks that no longer exists.
            if (HasEstimate && Math.Abs(offset - OffsetMs) > StepThresholdMs)
            {
                _count = 0;
                _next = 0;
                StepCount++;
            }

            _offsets[_next] = offset;
            _rtts[_next] = rtt;
            _next = (_next + 1) % WindowSize;
            if (_count < WindowSize) _count++;

            int best = 0;
            for (int i = 1; i < _count; i++)
            {
                if (_rtts[i] < _rtts[best]) best = i;
            }

            OffsetMs = _offsets[best];
            UncertaintyMs = _rtts[best] / 2.0;
            HasEstimate = true;
        }

        /// <summary>Drop the estimate. Called on reconnect: a new service session may be a
        /// different machine, and stale samples would be worse than none.</summary>
        public void Reset()
        {
            _count = 0;
            _next = 0;
            HasEstimate = false;
            UncertaintyMs = double.PositiveInfinity;
            OffsetMs = 0;
        }

        public double ToLocalMs(double serverMs) => serverMs - OffsetMs;

        public double ToServerMs(double localMs) => localMs + OffsetMs;
    }
}
