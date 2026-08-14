using NUnit.Framework;
using ShoalingUpstream.Control;

namespace ShoalingUpstream.Tests.Control
{
    /// <summary>
    /// Clock skew between the laptop and the phone.
    ///
    /// This is the quiet way the whole scheduling design fails. Commands carry absolute server
    /// timestamps and the lead is 400 ms; two clocks a second apart turn every command into
    /// either "already expired" or "fire immediately", and neither announces itself. The park's
    /// wifi is also the worst possible measurement channel, so the tests here are mostly about
    /// what the estimator does with bad samples rather than good ones.
    /// </summary>
    public class ServerClockTests
    {
        /// <summary>One round trip, with the server clock a known amount ahead and the two legs
        /// equal. Feeds the estimator the way a heartbeat/pong pair does.</summary>
        private static void Trip(ServerClock clock, double localSendMs, double rttMs, double trueOffsetMs)
        {
            double receivedAt = localSendMs + rttMs;
            double serverNow = localSendMs + rttMs / 2.0 + trueOffsetMs;
            clock.Observe(localSendMs, receivedAt, serverNow);
        }

        [Test]
        public void ASymmetricRoundTripRecoversTheOffsetExactly()
        {
            var clock = new ServerClock();
            Trip(clock, 1000, rttMs: 40, trueOffsetMs: 250);

            Assert.IsTrue(clock.HasEstimate);
            Assert.AreEqual(250, clock.OffsetMs, 0.001);
            Assert.AreEqual(20, clock.UncertaintyMs, 0.001, "half the round trip is the residual");
            Assert.IsTrue(clock.IsTrusted);

            // What the scheduler actually asks it.
            Assert.AreEqual(750, clock.ToLocalMs(1000), 0.001);
            Assert.AreEqual(1250, clock.ToServerMs(1000), 0.001);
        }

        [Test]
        public void TheLowestLatencySampleWinsRatherThanTheAverage()
        {
            // Park wifi is right-tailed: most trips are quick, a few are wrecked by a retransmit.
            // A long trip is also the one most likely to be asymmetric, so averaging imports
            // exactly the error the minimum filter is there to exclude.
            var clock = new ServerClock();
            Trip(clock, 1000, rttMs: 30, trueOffsetMs: 120);

            // Four bad samples, each with its delay entirely on the return leg — so the naive
            // rtt/2 assumption is badly wrong for all of them.
            for (int i = 1; i <= 4; i++)
            {
                double sendAt = 1000 + i * 1000;
                double rtt = 600;
                double serverNow = sendAt + 30 + 120;   // server answered promptly; the reply crawled
                clock.Observe(sendAt, sendAt + rtt, serverNow);
            }

            Assert.AreEqual(120, clock.OffsetMs, 1.0,
                "a single clean sample must outweigh four congested ones");
            Assert.AreEqual(15, clock.UncertaintyMs, 0.001);
        }

        [Test]
        public void ALargeChangeIsTreatedAsAStepAndAdoptedWhole()
        {
            // iOS picks up network time mid-session, often minutes after launch — which on this
            // walk is roughly when the phone reaches the park's wifi. Smoothing across that
            // leaves the device wrong in both directions for the length of the window.
            var clock = new ServerClock();
            for (int i = 0; i < 5; i++) Trip(clock, 1000 + i * 500, rttMs: 40, trueOffsetMs: 100);
            Assert.AreEqual(100, clock.OffsetMs, 0.001);

            Trip(clock, 5000, rttMs: 40, trueOffsetMs: -4000);

            Assert.AreEqual(1, clock.StepCount);
            Assert.AreEqual(-4000, clock.OffsetMs, 0.001,
                "a step must be adopted, not averaged with a relationship that no longer holds");
        }

        [Test]
        public void SmallDriftIsRefinedRatherThanTreatedAsAStep()
        {
            var clock = new ServerClock();
            Trip(clock, 1000, rttMs: 100, trueOffsetMs: 100);
            Trip(clock, 2000, rttMs: 20, trueOffsetMs: 140);

            Assert.AreEqual(0, clock.StepCount);
            Assert.AreEqual(140, clock.OffsetMs, 0.001, "the tighter round trip should win");
            Assert.AreEqual(10, clock.UncertaintyMs, 0.001);
        }

        [Test]
        public void AnOffsetTooCoarseToPlaceTheFireMomentIsNotTrusted()
        {
            var clock = new ServerClock { MaxTrustedUncertaintyMs = 200 };

            Assert.IsFalse(clock.IsTrusted, "with no samples there is nothing to trust");

            Trip(clock, 1000, rttMs: 900, trueOffsetMs: 50);
            Assert.IsTrue(clock.HasEstimate);
            Assert.IsFalse(clock.IsTrusted,
                "450 ms of residual is larger than the bus's own lead — the timestamp is no "
                + "longer saying anything the lead does not");

            Trip(clock, 3000, rttMs: 60, trueOffsetMs: 50);
            Assert.IsTrue(clock.IsTrusted);
        }

        [Test]
        public void ASampleWithANegativeRoundTripIsDiscarded()
        {
            // A wall-clock step landing inside the round trip. LocalClock exists to prevent this,
            // but a sample that implies time ran backwards is junk under any clock.
            var clock = new ServerClock();
            clock.Observe(sentAtLocalMs: 2000, receivedAtLocalMs: 1500, serverNowMs: 2000);
            Assert.IsFalse(clock.HasEstimate);
        }

        [Test]
        public void ResetForgetsEverythingBecauseAReconnectMayBeADifferentMachine()
        {
            var clock = new ServerClock();
            Trip(clock, 1000, rttMs: 20, trueOffsetMs: 300);
            Assert.IsTrue(clock.IsTrusted);

            clock.Reset();
            Assert.IsFalse(clock.HasEstimate);
            Assert.IsFalse(clock.IsTrusted);
            Assert.AreEqual(0, clock.OffsetMs, 0.001);
        }

        [Test]
        public void TheLocalClockIsUnixMillisecondsButDoesNotStep()
        {
            // Comparable to Date.now() so the protocol's timestamps mean something, monotonic so
            // an OS time step does not expire everything already queued.
            var clock = new LocalClock();
            double first = clock.NowMs;
            double second = clock.NowMs;

            Assert.GreaterOrEqual(second, first);
            Assert.Greater(first, 1_700_000_000_000d, "should be a Unix millisecond timestamp");
        }
    }
}
