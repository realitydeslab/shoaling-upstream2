using System;

namespace ShoalingUpstream.Control
{
    /// <summary>
    /// Backoff for a socket in a park.
    ///
    /// The failure being planned for is not a crashed service, it is a phone that has walked
    /// behind a hedge. That shapes two choices. The ceiling is low — ten seconds, matching the
    /// controller page — because the visitor walks back into range and the operator should not
    /// then wait a minute for their buttons to work. And the first retry is quick, because much
    /// of the time the socket dropped for no reason worth backing off from at all.
    ///
    /// The jitter is not decoration. Two phones on the same walk drop together when the access
    /// point does, and identical backoff would have them retry in lockstep forever.
    /// </summary>
    public sealed class ReconnectPolicy
    {
        public double InitialDelayMs = 500;
        public double MaxDelayMs = 10_000;
        public double Multiplier = 2.0;

        /// <summary>Fraction of the delay spread randomly, +/-.</summary>
        public double JitterFraction = 0.25;

        private readonly Func<double> _random;
        private int _attempt;

        /// <param name="random">Uniform [0,1). Injected so backoff is testable rather than
        /// approximately testable.</param>
        public ReconnectPolicy(Func<double> random = null)
        {
            var rng = new System.Random();
            _random = random ?? rng.NextDouble;
        }

        public int Attempt => _attempt;

        /// <summary>A connection succeeded and stayed up long enough to count.</summary>
        public void Succeeded() => _attempt = 0;

        public double NextDelayMs()
        {
            double baseDelay = InitialDelayMs * Math.Pow(Multiplier, _attempt);
            if (baseDelay > MaxDelayMs || double.IsInfinity(baseDelay)) baseDelay = MaxDelayMs;
            _attempt++;

            double jitter = baseDelay * JitterFraction * (_random() * 2.0 - 1.0);
            double delay = baseDelay + jitter;
            return delay < 0 ? 0 : delay;
        }
    }
}
