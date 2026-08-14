using System.Collections.Generic;
using NUnit.Framework;
using ShoalingUpstream.Control;
using ShoalingUpstream.Journey;
using UnityEngine;

namespace ShoalingUpstream.Tests.Control
{
    /// <summary>
    /// The client end to end, with a fake socket and a clock the test moves by hand.
    ///
    /// The behaviours worth pinning are all about a bad afternoon rather than a good one: a
    /// service that restarts, a socket that drops behind a hedge, a command that arrives after
    /// its moment, a frame of rubbish. None of them may stall the frame loop or fire something
    /// at the wrong place on the creek.
    /// </summary>
    public class ControlClientTests
    {
        private const string Url = "ws://127.0.0.1:8710/ws?role=device";

        private sealed class StubStatus : IControlStatusSource
        {
            public string CurrentBeat;
            public double HighWaterMark = -1;
            public double? ShoalCount = 40;

            public ControlStatus Snapshot() => new()
            {
                CurrentBeat = CurrentBeat,
                HighWaterMark = HighWaterMark,
                ShoalCount = ShoalCount,
                TrackingConfidence = 0.9,
                Completed = System.Array.Empty<string>(),
            };
        }

        private sealed class Rig
        {
            public readonly FakeControlTransport Transport = new();
            public readonly CentrelineFrame Frame;
            public readonly VpsPoseSource Vps;
            public readonly SimulatedPoseSource Simulated;
            public readonly PoseSourceSwitch Poses;
            public readonly RecordingEffects Effects = new();
            public readonly StubStatus Status = new();
            public readonly ControlClient Client;

            public Rig()
            {
                Frame = new CentrelineFrame(new List<Vec3>
                {
                    new() { x = 0, y = 0, z = 0 },
                    new() { x = 0, y = 0, z = 60 },
                });
                Vps = new VpsPoseSource(Frame);
                Simulated = new SimulatedPoseSource(Frame);
                Poses = new PoseSourceSwitch(Vps, Simulated);
                Client = new ControlClient(Transport, Poses, Simulated, Effects, Status,
                                           new ReconnectPolicy(() => 0.5));
            }

            /// <summary>Connect, be welcomed, and settle the clock to a known zero offset — the
            /// state the device spends the walk in.</summary>
            public Rig Online(double at = 1000)
            {
                Client.Connect(Url, at);
                Transport.Deliver(Frames.Welcome());
                Client.Pump(at);                       // welcome, hello, first heartbeat
                Transport.Deliver(Frames.Pong(at + 20));
                Client.Pump(at + 40);                  // rtt 40, offset 0
                Assert.IsTrue(Client.Clock.IsTrusted);
                return this;
            }
        }

        [Test]
        public void TheDeviceUrlAsksForTheDeviceRole()
        {
            // An operator connection receives commandIssued instead of command, and would never
            // fire anything at all.
            StringAssert.Contains("role=device", ControlClient.DeviceUrl("192.168.1.20"));
            StringAssert.Contains(":8710/ws", ControlClient.DeviceUrl("192.168.1.20"));
        }

        [Test]
        public void HelloFollowsTheWelcomeAndNotTheOpenSocket()
        {
            var rig = new Rig();
            rig.Client.Connect(Url, 1000);
            rig.Client.Pump(1000);

            Assert.IsEmpty(rig.Transport.SentOfType("hello"),
                "an open socket is not yet a welcomed one — the session id scopes every ack");
            Assert.AreEqual(ControlLinkState.Connecting, rig.Client.State);

            rig.Transport.Deliver(Frames.Welcome("s-abc"));
            rig.Client.Pump(1010);

            Assert.AreEqual(1, rig.Transport.SentOfType("hello").Count);
            Assert.AreEqual(ControlLinkState.Online, rig.Client.State);
            Assert.AreEqual("s-abc", rig.Client.SessionId);
        }

        [Test]
        public void TheHeartbeatBurstEstablishesTheClockWithinACoupleOfSeconds()
        {
            // Each pong is one offset sample, and the estimate is useless until there are a few.
            // Until then every command runs on relative timing and two devices do not agree.
            var rig = new Rig();
            rig.Client.Connect(Url, 1000);
            rig.Transport.Deliver(Frames.Welcome());
            rig.Client.Pump(1000);

            double now = 1000;
            for (int i = 0; i < 4; i++)
            {
                rig.Transport.Deliver(Frames.Pong(now + 15));
                now += 30;
                rig.Client.Pump(now);          // answer arrives
                now += 400;
                rig.Client.Pump(now);          // next heartbeat in the burst goes out
            }

            Assert.IsTrue(rig.Client.Clock.IsTrusted);
            Assert.Less(now - 1000, 2000, "the clock should be settled within two seconds of connecting");
            Assert.GreaterOrEqual(rig.Transport.SentOfType("heartbeat").Count, 4);
        }

        [Test]
        public void AFireBeatReachesTheAudioAtItsMomentAndIsAcked()
        {
            var rig = new Rig().Online();

            rig.Transport.Deliver(Frames.Command(ControlActions.FireBeat, 1_100, beatId: "falls"));
            rig.Client.Pump(1_200);
            CollectionAssert.IsEmpty(rig.Effects.Calls, "300 ms early is 300 ms wrong");

            rig.Client.Pump(1_500);
            CollectionAssert.Contains(rig.Effects.Calls, "fireBeat:falls");

            var ack = rig.Transport.LastSentOfType("ack");
            Assert.IsNotNull(ack);
            Assert.AreEqual("s-test-1", ack["commandId"].AsString());
            Assert.IsTrue(ack["applied"].AsBool());
            Assert.AreEqual(Frames.Session, ack["sessionId"].AsString(),
                "the bus drops an ack whose session is not its own");
        }

        [Test]
        public void ACommandFromAPreviousServiceSessionIsNeitherPlayedNorAcked()
        {
            var rig = new Rig().Online();

            rig.Transport.Deliver(Frames.Command(ControlActions.FireBeat, 1_100, beatId: "falls",
                sessionId: "a-previous-session", id: "stale-7"));
            rig.Client.Pump(1_600);

            CollectionAssert.IsEmpty(rig.Effects.Calls);
            CollectionAssert.IsEmpty(rig.Transport.SentOfType("ack"),
                "acking would report a success for a command log that no longer exists");
        }

        [Test]
        public void ALateCommandIsAckedAsNotAppliedRatherThanPlayed()
        {
            var rig = new Rig().Online();

            // Issued eleven seconds ago; the wifi only just gave it up.
            rig.Transport.Deliver(Frames.Command(ControlActions.FireBeat, 1_100, beatId: "falls"));
            rig.Client.Pump(12_500);

            CollectionAssert.IsEmpty(rig.Effects.Calls);
            var ack = rig.Transport.LastSentOfType("ack");
            Assert.IsNotNull(ack, "the operator pressed a button and must be told it did not play");
            Assert.IsFalse(ack["applied"].AsBool());
        }

        [Test]
        public void AnUnhandledActionIsRefusedRatherThanConfirmed()
        {
            // An operator seeing a button confirmed when nothing happened is worse than seeing
            // it refused.
            var rig = new Rig().Online();
            rig.Transport.Deliver(Frames.Command("teleportEverybody", 1_100));
            rig.Client.Pump(1_600);

            var ack = rig.Transport.LastSentOfType("ack");
            Assert.IsFalse(ack["applied"].AsBool());
            StringAssert.Contains("teleportEverybody", ack["note"].AsString());
        }

        [Test]
        public void ASimulatedPoseDrivesThePoseSourceInsteadOfBeingPlayed()
        {
            var rig = new Rig().Online();
            rig.Vps.Submit(new Vector3(0, 0, 3f), 0f, LocalizationQuality.Precise, 1_100);

            rig.Transport.Deliver(Frames.Pose(s: 27.5, issuedAtMs: 1_100));
            rig.Client.Pump(1_600);

            Assert.IsTrue(rig.Poses.TryGetPose(1_600, out var pose));
            Assert.AreEqual(PoseOrigin.Simulated, pose.Origin,
                "the browser's walker takes over while it is being driven");
            Assert.AreEqual(27.5f, pose.S, 0.001f);
            Assert.AreEqual(27.5f, pose.AnchorLocalPosition.z, 0.001f,
                "the editor sends s alone; the 3D point downstream needs is reconstructed here");

            CollectionAssert.IsEmpty(rig.Effects.Calls, "a pose is not an effect");
            CollectionAssert.IsEmpty(rig.Transport.SentOfType("ack"),
                "acking every scrub frame would spend the socket on bookkeeping nobody reads");
        }

        [Test]
        public void AStreamedPoseMovesTheWalkerTheMomentItLands()
        {
            var rig = new Rig().Online();

            rig.Transport.Deliver(Frames.StreamedPose(s: 27.5, headingRad: 1.2));
            rig.Client.Pump(1_041);   // one millisecond after the frame, not one lead time

            Assert.IsTrue(rig.Poses.TryGetPose(1_041, out var pose),
                "a pose is state, not an instruction — nothing schedules it");
            Assert.AreEqual(PoseOrigin.Simulated, pose.Origin);
            Assert.AreEqual(27.5f, pose.S, 0.001f);
            Assert.AreEqual(27.5f, pose.AnchorLocalPosition.z, 0.001f,
                "the editor sends s alone; the point downstream needs is reconstructed here");
            Assert.AreEqual(LocalizationQuality.Precise, pose.Quality);

            CollectionAssert.IsEmpty(rig.Effects.Calls, "a pose is not an effect");
            CollectionAssert.IsEmpty(rig.Transport.SentOfType("ack"),
                "acking twenty scrub frames a second would spend the socket on bookkeeping "
                + "nobody reads");
            Assert.AreEqual(0, rig.Client.Scheduler.PendingCount,
                "and nothing of it is left waiting in the command queue");
        }

        [Test]
        public void TheLatestStreamedPoseWins()
        {
            // No history and no replay: after a stall the operator wants where the walker is now,
            // not ten seconds of queued positions played back at ten times speed.
            var rig = new Rig().Online();

            rig.Transport.Deliver(Frames.StreamedPose(s: 5));
            rig.Transport.Deliver(Frames.StreamedPose(s: 12));
            rig.Transport.Deliver(Frames.StreamedPose(s: 30));
            rig.Client.Pump(1_050);

            Assert.IsTrue(rig.Poses.TryGetPose(1_050, out var pose));
            Assert.AreEqual(30f, pose.S, 0.001f);
            Assert.AreEqual(0, rig.Client.Scheduler.PendingCount);
        }

        [Test]
        public void AStreamedPoseCarryingNothingLeavesThePreviousOneStanding()
        {
            var rig = new Rig().Online();

            rig.Transport.Deliver(Frames.StreamedPose(s: 27.5));
            rig.Client.Pump(1_050);

            rig.Transport.Deliver(Frames.StreamedPose(s: null, position: null));
            rig.Client.Pump(1_060);

            Assert.IsTrue(rig.Poses.TryGetPose(1_060, out var pose));
            Assert.AreEqual(27.5f, pose.S, 0.001f,
                "an unreadable frame must not teleport the visitor to the end of the creek");
            StringAssert.Contains("neither s nor a position", rig.Client.LastNote);
        }

        [Test]
        public void ADroppedSocketStopsAStreamedWalkAtOnce()
        {
            // An operator who has vanished is not still scrubbing, and the two-second lease is
            // too long to leave the visitor pinned to wherever the last frame put them.
            var rig = new Rig().Online();
            rig.Transport.Deliver(Frames.StreamedPose(s: 27.5));
            rig.Client.Pump(1_050);
            Assert.IsTrue(rig.Poses.IsLive(1_050));

            rig.Transport.Drop();
            rig.Client.Pump(1_060);

            Assert.IsFalse(rig.Poses.IsLive(1_060));
        }

        [Test]
        public void BothPoseCarriersDriveTheSameWalker()
        {
            // The older simulatePose command is still live: a phone in the field may be sent one,
            // and dropping it would be a second break. Whichever arrives last wins, and nothing
            // downstream can tell which carried it.
            var rig = new Rig().Online();

            rig.Transport.Deliver(Frames.Pose(s: 10, issuedAtMs: 1_000));
            rig.Client.Pump(1_500);
            Assert.IsTrue(rig.Poses.TryGetPose(1_500, out var viaCommand));
            Assert.AreEqual(10f, viaCommand.S, 0.001f);
            Assert.AreEqual(PoseOrigin.Simulated, viaCommand.Origin);

            rig.Transport.Deliver(Frames.StreamedPose(s: 42));
            rig.Client.Pump(1_510);
            Assert.IsTrue(rig.Poses.TryGetPose(1_510, out var viaStream));
            Assert.AreEqual(42f, viaStream.S, 0.001f);
            Assert.AreEqual(PoseOrigin.Simulated, viaStream.Origin);
        }

        [Test]
        public void TheStatusReportedIsTheOneThePieceIsActuallyRunningOn()
        {
            // Whichever source is steering is what the operator sees, or a simulated walk shows
            // numbers that do not match the beats being fired against it.
            var rig = new Rig().Online();
            rig.Status.CurrentBeat = "strider";

            rig.Transport.Deliver(Frames.Pose(s: 18.25, issuedAtMs: 1_100));
            rig.Client.Pump(1_600);
            rig.Client.Pump(1_900);

            var status = rig.Transport.LastSentOfType("status");
            Assert.IsNotNull(status);
            Assert.AreEqual(18.25, status["s"].AsDouble(), 0.001);
            Assert.AreEqual("precise", status["localization"].AsString());
            Assert.AreEqual("strider", status["currentBeat"].AsString());
        }

        [Test]
        public void AStationaryVisitorDoesNotFillTheSocketWithIdenticalReports()
        {
            var rig = new Rig().Online();
            rig.Vps.Submit(new Vector3(0, 0, 10f), 0f, LocalizationQuality.Precise, 1_100);

            rig.Client.Pump(1_100);
            int afterFirst = rig.Transport.SentOfType("status").Count;

            for (double t = 1_200; t <= 2_500; t += 100)
            {
                rig.Vps.Submit(new Vector3(0, 0, 10f), 0f, LocalizationQuality.Precise, t);
                rig.Client.Pump(t);
            }

            int sent = rig.Transport.SentOfType("status").Count - afterFirst;
            Assert.LessOrEqual(sent, 2, "an unchanged status is only re-sent to keep the operator's "
                + "window from ageing into looking dead");
            Assert.GreaterOrEqual(sent, 1);
        }

        [Test]
        public void ADroppedSocketReconnectsWithBackoffRatherThanSpinning()
        {
            var rig = new Rig().Online();
            Assert.AreEqual(1, rig.Transport.ConnectCount);

            rig.Transport.Drop("connection reset by peer");
            rig.Client.Pump(2_000);
            Assert.AreEqual(ControlLinkState.Offline, rig.Client.State);
            Assert.AreEqual(1, rig.Transport.ConnectCount, "not immediately");

            rig.Client.Pump(2_400);
            Assert.AreEqual(1, rig.Transport.ConnectCount, "still inside the first backoff");

            rig.Client.Pump(2_600);          // 500 ms with jitter fixed at the midpoint
            Assert.AreEqual(2, rig.Transport.ConnectCount);

            // And the second failure waits longer than the first.
            rig.Transport.Drop();
            rig.Client.Pump(2_700);
            rig.Client.Pump(3_300);
            Assert.AreEqual(2, rig.Transport.ConnectCount, "the backoff should have grown");
            rig.Client.Pump(3_800);
            Assert.AreEqual(3, rig.Transport.ConnectCount);
        }

        [Test]
        public void ADroppedSocketHandsThePieceStraightBackToVps()
        {
            var rig = new Rig().Online();
            rig.Vps.Submit(new Vector3(0, 0, 4f), 0f, LocalizationQuality.Precise, 1_100);
            rig.Transport.Deliver(Frames.Pose(s: 30, issuedAtMs: 1_100));
            rig.Client.Pump(1_600);
            Assert.IsTrue(rig.Poses.TryGetPose(1_600, out var scrubbed));
            Assert.AreEqual(PoseOrigin.Simulated, scrubbed.Origin);

            rig.Transport.Drop();
            rig.Client.Pump(1_700);
            rig.Vps.Submit(new Vector3(0, 0, 4.2f), 0f, LocalizationQuality.Precise, 1_700);

            Assert.IsTrue(rig.Poses.TryGetPose(1_700, out var real));
            Assert.AreEqual(PoseOrigin.Vps, real.Origin,
                "an operator who has vanished is not still scrubbing — waiting out the lease "
                + "would pin the visitor to wherever the last frame put them");
        }

        [Test]
        public void AReconnectForgetsTheOldSessionAndItsQueue()
        {
            var rig = new Rig().Online();
            rig.Transport.Deliver(Frames.Command(ControlActions.FireBeat, 1_100, leadMs: 5_000,
                beatId: "falls"));
            rig.Client.Pump(1_200);
            Assert.AreEqual(1, rig.Client.Scheduler.PendingCount);

            rig.Transport.Drop();
            rig.Client.Pump(2_000);
            Assert.AreEqual(0, rig.Client.Scheduler.PendingCount,
                "a socket drop means the operator no longer knows what the device is about to do");
            Assert.IsNull(rig.Client.SessionId);

            rig.Client.Pump(2_600);
            rig.Transport.Deliver(Frames.Welcome("s-restarted"));
            rig.Client.Pump(2_700);

            Assert.AreEqual("s-restarted", rig.Client.SessionId);
            Assert.IsFalse(rig.Client.Clock.IsTrusted,
                "a reconnect may be a different machine; offsets measured against the old one "
                + "describe a relationship that no longer holds");
            CollectionAssert.IsEmpty(rig.Effects.Calls);
        }

        [Test]
        public void RubbishOnTheSocketDoesNotStallTheClient()
        {
            // The assertion that matters in a park is the one after the rubbish.
            var rig = new Rig().Online();
            rig.Transport.Deliver("not json at all");
            rig.Transport.Deliver("{\"type\":\"command\",");
            rig.Transport.Deliver("{\"type\":\"somethingNewer\"}");
            rig.Transport.Deliver(Frames.Command(ControlActions.Silence, 1_100));

            rig.Client.Pump(1_600);

            CollectionAssert.Contains(rig.Effects.Calls, "silence");
            Assert.AreEqual(ControlLinkState.Online, rig.Client.State);
        }

        [Test]
        public void WithNothingWiredToPlayItACommandIsRefusedNotSwallowed()
        {
            var rig = new Rig();
            rig.Client.SetEffects(null);
            rig.Online();

            rig.Transport.Deliver(Frames.Command(ControlActions.FireBeat, 1_100, beatId: "falls"));
            rig.Client.Pump(1_600);

            var ack = rig.Transport.LastSentOfType("ack");
            Assert.IsNotNull(ack);
            Assert.IsFalse(ack["applied"].AsBool());
        }

        [Test]
        public void ASocketThatOpensAndNeverGreetsUsIsGivenUpOn()
        {
            // The wrong service on the right port, or a far end that died between the handshake
            // and the welcome. Waiting forever in Connecting is worse than starting again.
            var rig = new Rig();
            rig.Client.Connect(Url, 1000);
            rig.Client.Pump(1000);
            Assert.AreEqual(ControlLinkState.Connecting, rig.Client.State);

            rig.Client.Pump(5_500);
            Assert.AreEqual(ControlLinkState.Connecting, rig.Client.State, "not yet");

            rig.Client.Pump(6_500);
            Assert.AreEqual(ControlLinkState.Offline, rig.Client.State);
            StringAssert.Contains("never greeted", rig.Client.LastNote);

            rig.Client.Pump(7_100);
            Assert.AreEqual(2, rig.Transport.ConnectCount, "and it tries again");
        }

        [Test]
        public void ASocketThatIsOpenButRoutesNowhereIsNoticedAndReplaced()
        {
            // A park's wifi fails by staying associated and carrying nothing. The socket reports
            // itself open the whole time, so only the missing pongs give it away — and a device
            // that looks connected while acting on nothing is the failure the operator cannot
            // diagnose from their end.
            var rig = new Rig().Online();
            Assert.AreEqual(1, rig.Transport.ConnectCount);

            // Three heartbeats go out and none is answered.
            double now = 1_100;
            for (int i = 0; i < 4; i++)
            {
                now += 9_000;
                rig.Client.Pump(now);
            }

            Assert.AreEqual(ControlLinkState.Offline, rig.Client.State);
            StringAssert.Contains("routes nowhere", rig.Client.LastNote);
            Assert.GreaterOrEqual(rig.Transport.SentOfType("heartbeat").Count, 3,
                "it should have kept asking before giving up, not given up on one lost pong");
        }

        [Test]
        public void AServiceThatWasNeverThereIsJustSilence()
        {
            // The piece runs automatically. A laptop that is switched off must cost nothing but
            // the operator's safety net.
            var rig = new Rig();
            rig.Transport.NothingListening = true;
            rig.Client.Connect(Url, 1000);

            for (double t = 1000; t < 30_000; t += 100) rig.Client.Pump(t);

            Assert.AreEqual(ControlLinkState.Offline, rig.Client.State);
            CollectionAssert.IsEmpty(rig.Effects.Calls);
            Assert.Less(rig.Transport.ConnectCount, 15,
                "backoff, not a retry every frame for the length of the walk");
        }
    }
}
