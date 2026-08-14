using System.Collections.Generic;
using NUnit.Framework;
using ShoalingUpstream.Control;

namespace ShoalingUpstream.Tests.Control
{
    /// <summary>
    /// Backoff, with the randomness injected so this is a test rather than an approximation.
    /// </summary>
    public class ReconnectPolicyTests
    {
        /// <summary>Jitter fixed at the midpoint, which is zero displacement.</summary>
        private static ReconnectPolicy Deterministic() => new(() => 0.5);

        [Test]
        public void BackoffGrowsAndThenStopsGrowing()
        {
            var policy = Deterministic();
            var delays = new List<double>();
            for (int i = 0; i < 8; i++) delays.Add(policy.NextDelayMs());

            Assert.AreEqual(500, delays[0], 0.001, "the first retry is quick — most drops are nothing");
            Assert.AreEqual(1000, delays[1], 0.001);
            Assert.AreEqual(2000, delays[2], 0.001);

            // The ceiling is low on purpose: the visitor walks back into range, and the operator
            // should not then wait a minute for their buttons to work.
            Assert.AreEqual(10_000, delays[^1], 0.001);
            for (int i = 1; i < delays.Count; i++) Assert.GreaterOrEqual(delays[i], delays[i - 1]);
        }

        [Test]
        public void JitterStaysInsideItsBandAndIsNeverNegative()
        {
            // Two phones on the same walk drop together when the access point does. Identical
            // backoff would have them retry in lockstep forever.
            foreach (double roll in new[] { 0.0, 0.25, 0.75, 0.999 })
            {
                var policy = new ReconnectPolicy(() => roll) { JitterFraction = 0.25 };
                double delay = policy.NextDelayMs();
                Assert.GreaterOrEqual(delay, 375 - 0.001);
                Assert.LessOrEqual(delay, 625 + 0.001);
            }

            var wild = new ReconnectPolicy(() => 0.0) { JitterFraction = 2.0 };
            Assert.GreaterOrEqual(wild.NextDelayMs(), 0, "a delay must never be negative");
        }

        [Test]
        public void ASuccessfulConnectionResetsTheBackoff()
        {
            var policy = Deterministic();
            for (int i = 0; i < 5; i++) policy.NextDelayMs();
            Assert.AreEqual(5, policy.Attempt);

            policy.Succeeded();
            Assert.AreEqual(0, policy.Attempt);
            Assert.AreEqual(500, policy.NextDelayMs(), 0.001,
                "walking back into range should not inherit the backoff from walking out of it");
        }
    }
}
