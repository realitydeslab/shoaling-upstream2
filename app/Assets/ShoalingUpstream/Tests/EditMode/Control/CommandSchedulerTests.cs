using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using ShoalingUpstream.Control;

namespace ShoalingUpstream.Tests.Control
{
    /// <summary>
    /// Scheduling, expiry and session scoping — the three properties the bus asks the device to
    /// keep, tested against a clock the test moves by hand rather than against a stopwatch.
    ///
    /// The one to keep is the first: a command that fires on arrival has converted the server's
    /// fixed lead back into variable network latency, which is the thing the lead exists to
    /// remove. Nothing about that is visible from watching one phone.
    /// </summary>
    public class CommandSchedulerTests
    {
        private const string Session = "s-test";

        /// <summary>A clock with a known offset and a tight enough round trip to be trusted.</summary>
        private static ServerClock ClockOffsetBy(double offsetMs)
        {
            var clock = new ServerClock();
            clock.Observe(1000, 1040, 1020 + offsetMs);
            Assert.IsTrue(clock.IsTrusted);
            Assert.AreEqual(offsetMs, clock.OffsetMs, 0.001);
            return clock;
        }

        private static ControlCommand Cmd(string action, double issuedAtMs, double leadMs = 400,
                                          double ttlMs = 10_000, string id = "s-test-1",
                                          string sessionId = Session, string beatId = "falls")
            => new()
            {
                Id = id,
                Action = action,
                BeatId = beatId,
                SessionId = sessionId,
                IssuedAtMs = issuedAtMs,
                FireAtMs = issuedAtMs + leadMs,
                ExpiresAtMs = issuedAtMs + ttlMs,
            };

        private static List<CommandOutcome> Pump(CommandScheduler scheduler, double nowMs)
        {
            var due = new List<CommandOutcome>();
            scheduler.Pump(nowMs, due);
            return due;
        }

        [Test]
        public void ACommandWaitsForItsMomentRatherThanFiringOnArrival()
        {
            var scheduler = new CommandScheduler(ClockOffsetBy(0));

            // Issued at server 10 000, to fire at 10 400. It reaches us at local 10 100.
            var outcome = scheduler.Enqueue(Cmd(ControlActions.FireBeat, 10_000), 10_100, Session);
            Assert.AreEqual(CommandDisposition.Queued, outcome.Disposition);

            Assert.IsEmpty(Pump(scheduler, 10_399), "300 ms early is 300 ms wrong");

            var due = Pump(scheduler, 10_400);
            Assert.AreEqual(1, due.Count);
            Assert.AreEqual(CommandDisposition.Fired, due[0].Disposition);
            Assert.AreEqual(0, scheduler.PendingCount);
        }

        [Test]
        public void ACommandThatArrivedAfterItsExpiryIsDroppedNotPlayedLate()
        {
            var scheduler = new CommandScheduler(ClockOffsetBy(0));

            // A wifi stall: the command surfaces eleven seconds after it was issued.
            var outcome = scheduler.Enqueue(Cmd(ControlActions.FireBeat, 10_000), 21_000, Session);

            Assert.AreEqual(CommandDisposition.ExpiredOnArrival, outcome.Disposition);
            Assert.AreEqual(0, scheduler.PendingCount,
                "a sound arriving at the wrong place on the creek is worse than one that never arrives");
        }

        [Test]
        public void ACommandThatGoesStaleWhileQueuedExpiresRatherThanFiring()
        {
            // The app was backgrounded, or the frame loop stalled on a scene load. Either way
            // the moment passed while the command sat in the queue.
            var scheduler = new CommandScheduler(ClockOffsetBy(0));
            scheduler.Enqueue(Cmd(ControlActions.FireBeat, 10_000), 10_100, Session);

            var due = Pump(scheduler, 25_000);
            Assert.AreEqual(1, due.Count);
            Assert.AreEqual(CommandDisposition.ExpiredWaiting, due[0].Disposition);
            Assert.AreEqual(0, scheduler.PendingCount);
        }

        [Test]
        public void ACommandFromAnotherServiceSessionIsRejected()
        {
            // After a restart the ids refer to a command log that no longer exists. Acting on
            // one would fire a beat the current operator never asked for.
            var scheduler = new CommandScheduler(ClockOffsetBy(0));

            var outcome = scheduler.Enqueue(
                Cmd(ControlActions.FireBeat, 10_000, sessionId: "a-previous-session"), 10_100, Session);

            Assert.AreEqual(CommandDisposition.StaleSession, outcome.Disposition);
            Assert.AreEqual(0, scheduler.PendingCount);
        }

        [Test]
        public void TheClockOffsetIsAppliedSoAFastDeviceDoesNotFireEarly()
        {
            // The phone's clock reads 3 s behind the laptop's: server 10 400 is local 7 400.
            var scheduler = new CommandScheduler(ClockOffsetBy(3000));

            scheduler.Enqueue(Cmd(ControlActions.FireBeat, 10_000), 7_100, Session);

            Assert.IsEmpty(Pump(scheduler, 7_399));
            Assert.AreEqual(1, Pump(scheduler, 7_401).Count,
                "the fire moment is in the DEVICE's clock, or the skew silently becomes latency");
        }

        [Test]
        public void WithoutATrustedClockTheCommandsOwnLeadIsUsedInstead()
        {
            // The first seconds after connecting, before any pong has landed. The absolute
            // timestamps are unusable, but a lead of 400 ms and a TTL of 10 s mean the same
            // thing on any clock — so the durations still schedule the command correctly.
            var scheduler = new CommandScheduler(new ServerClock());

            // Server time is nonsense relative to the device: issued at 10 000, we are at 500 000.
            var outcome = scheduler.Enqueue(Cmd(ControlActions.FireBeat, 10_000), 500_000, Session);
            Assert.AreEqual(CommandDisposition.Queued, outcome.Disposition,
                "an unknown offset must not make every command look expired");

            Assert.IsEmpty(Pump(scheduler, 500_399));
            Assert.AreEqual(CommandDisposition.Fired, Pump(scheduler, 500_400)[0].Disposition);
        }

        [Test]
        public void WithoutATrustedClockACommandStillExpires()
        {
            var scheduler = new CommandScheduler(new ServerClock());
            scheduler.Enqueue(Cmd(ControlActions.FireBeat, 10_000), 500_000, Session);

            var due = Pump(scheduler, 511_000);
            Assert.AreEqual(CommandDisposition.ExpiredWaiting, due[0].Disposition,
                "relative timing keeps the TTL; it only loses agreement between two devices");
        }

        [Test]
        public void ANewerPoseReplacesTheQueuedOlderOnesRatherThanReplayingTheWalk()
        {
            // After a stall, a queue of poses would replay the whole walk at ten times speed and
            // fire every beat along the way. What the operator wants is where the walker is now.
            var scheduler = new CommandScheduler(ClockOffsetBy(0));

            for (int i = 0; i < 5; i++)
            {
                var pose = Cmd(ControlActions.SimulatePose, 10_000 + i * 30, id: $"s-test-{i}");
                scheduler.Enqueue(pose, 10_050 + i * 30, Session);
            }

            Assert.AreEqual(1, scheduler.PendingCount);
            Assert.AreEqual(4, scheduler.SupersededCount);

            var due = Pump(scheduler, 11_000);
            Assert.AreEqual("s-test-4", due[0].Command.Id, "the newest pose is the one that survives");
        }

        [Test]
        public void BeatsAreNotSupersededBecauseEachOneIsAThingThatHappened()
        {
            var scheduler = new CommandScheduler(ClockOffsetBy(0));
            scheduler.Enqueue(Cmd(ControlActions.FireBeat, 10_000, id: "a", beatId: "tree"), 10_050, Session);
            scheduler.Enqueue(Cmd(ControlActions.FireBeat, 10_010, id: "b", beatId: "redd"), 10_060, Session);

            Assert.AreEqual(2, scheduler.PendingCount);
            var due = Pump(scheduler, 11_000);
            CollectionAssert.AreEqual(new[] { "a", "b" }, due.Select(d => d.Command.Id).ToArray(),
                "and they fire in scheduled order, not arrival order");
        }

        [Test]
        public void ClearAbandonsEverythingBecauseADroppedSocketMeansTheOperatorIsGuessing()
        {
            var scheduler = new CommandScheduler(ClockOffsetBy(0));
            scheduler.Enqueue(Cmd(ControlActions.FireBeat, 10_000), 10_050, Session);
            scheduler.Clear();

            Assert.AreEqual(0, scheduler.PendingCount);
            Assert.IsEmpty(Pump(scheduler, 11_000));
        }
    }
}
