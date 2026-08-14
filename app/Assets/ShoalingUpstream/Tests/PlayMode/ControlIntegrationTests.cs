using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using NUnit.Framework;
using ShoalingUpstream.Config;
using ShoalingUpstream.Control;
using ShoalingUpstream.Journey;
using UnityEngine;
using UnityEngine.TestTools;

namespace ShoalingUpstream.Tests.Integration
{
    /// <summary>
    /// The three connections, against a real service on a real socket.
    ///
    /// Everything in Tests/EditMode drives a fake transport, which proves the parser and the
    /// scheduler and proves nothing at all about whether the two halves of this system have ever
    /// spoken. These tests exist to answer the only question those cannot: put node on one end and
    /// the Unity play loop on the other, and see what actually happens.
    ///
    ///   1. editor  → Unity   a streamed pose moves the walker
    ///   2. control → Unity   a command fires at its moment, and is acked
    ///   3. Unity   → control a status report reaches the operator's view
    ///
    /// The service is a scratch instance on its own port with its own copy of the journey data;
    /// see <see cref="LiveService"/> for why that matters.
    /// </summary>
    [TestFixture]
    public sealed class ControlIntegrationTests
    {
        private const string Slug = "ubc-nitobe-garden-creek";

        private LiveService _service;
        private OperatorSocket _operator;
        private GameObject _host;
        private ControlLink _link;
        private RecordingEffects _effects;
        private FakeStatusSource _status;
        private JourneyDocument _journey;

        // ------------------------------------------------------------------ fixture

        [OneTimeSetUp]
        public void StartService()
        {
            _service = LiveService.Start();

            // Publish the CURRENT draft into the scratch copy. The revision published in the
            // artist's own data is the superseded seeded layout, and republishing there is their
            // decision — but a test that loads it would be testing artwork nobody is making.
            _service.Http("POST", $"/api/sites/{Slug}/publish");

            string published = _service.Http("GET", $"/api/sites/{Slug}/published");
            Assert.That(JourneyParser.TryParse(published, out _journey, out string error), Is.True,
                        $"the scratch service published something the app cannot parse: {error}");
        }

        [OneTimeTearDown]
        public void StopService() => _service?.Dispose();

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            _operator = OperatorSocket.Connect(_service.OperatorWsUrl);
            _effects = new RecordingEffects();
            _status = new FakeStatusSource();

            // Inactive first: Awake builds the transport and Start would connect before the host
            // and port below have been set.
            _host = new GameObject("ControlLink");
            _host.SetActive(false);
            _link = _host.AddComponent<ControlLink>();
            _link.Host = "127.0.0.1";
            _link.Port = _service.Port;
            _link.ConnectOnStart = true;
            _link.BuildName = "playmode-integration";
            _host.SetActive(true);

            _link.SetCentreline(_journey.site.centreline);
            _link.SetEffects(_effects);
            _link.SetStatusSource(_status);

            yield return Until(() => _link.Client.State == ControlLinkState.Online,
                               "the link to come Online");
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (_host != null) UnityEngine.Object.Destroy(_host);
            _operator?.Dispose();
            _operator = null;
            yield return null;
        }

        // ------------------------------------------------------------------ the link itself

        [UnityTest]
        public IEnumerator TheLinkGreetsTheRealService()
        {
            Assert.That(_link.Client.SessionId, Is.Not.Null.And.Not.Empty,
                        "Online means welcomed, so a session id must have arrived with it");

            // The hello reaches the bus and comes back out as presence, which is what the operator
            // page draws. Anything less and a phone that is connected still shows as absent.
            yield return Until(() =>
            {
                var presence = _operator.Latest("presence");
                if (presence is null) return false;
                foreach (var device in presence["devices"].Items)
                {
                    if (device["build"].AsString() == "playmode-integration") return true;
                }
                return false;
            }, "the device to appear in presence with the build name it said hello with");

            // And the clock estimate settles, which everything scheduled depends on.
            yield return Until(() => _link.Client.Clock.IsTrusted,
                               "the server clock offset to become trusted", 10f);
            Assert.That(_link.Client.Clock.UncertaintyMs, Is.LessThan(200));
        }

        // ------------------------------------------------------------------ 1. editor → Unity

        /// <summary>
        /// The requirement, exactly as the artist stated it: scrub in the browser, and the Unity
        /// editor follows the same path.
        ///
        /// This is the frame `editor/js/link.js` actually puts on the wire and the frame
        /// `ControlBus.streamPose` actually broadcasts to devices — copied from
        /// test/control-bus.e2e.test.mjs, not from prose.
        ///
        /// This test failed when it was written, and that failure was the finding: Unity had no
        /// case for `pose` at all. It passes now against `case "pose"` in ControlProtocol, and it
        /// was not edited to make it pass.
        /// </summary>
        [UnityTest]
        public IEnumerator EditorPoseStream_MovesTheSimulatedWalker()
        {
            const float target = 9.4f;
            Vector3 point = Centreline.PointAt(target, _journey.site.centreline);

            _operator.Send("{\"type\":\"pose\","
                           + $"\"s\":{target.ToString(System.Globalization.CultureInfo.InvariantCulture)},"
                           + $"\"position\":{{\"x\":{F(point.x)},\"y\":{F(point.y)},\"z\":{F(point.z)}}},"
                           + "\"headingRad\":1.2,"
                           + $"\"slug\":\"{Slug}\"}}");

            yield return Until(() =>
                _link.Poses.TryGetPose(_link.NowMs, out var pose)
                && Mathf.Abs(pose.S - target) < 0.25f,
                $"the simulated walker to arrive at s={target} m from a streamed pose", 6f);

            _link.Poses.TryGetPose(_link.NowMs, out var arrived);
            Assert.That(arrived.Origin, Is.EqualTo(PoseOrigin.Simulated));
            Assert.That(arrived.Quality, Is.EqualTo(LocalizationQuality.Precise));
        }

        /// <summary>
        /// Where the break is, narrowed to one line.
        ///
        /// The same pose, sent as the command the Unity client is written against, moves the
        /// walker over the very same socket. So the transport, the parser, the centreline
        /// projection and the pose source are all sound: what fails above is that the two ends
        /// were built to two different messages.
        /// </summary>
        [UnityTest]
        public IEnumerator SimulatePoseCommand_MovesTheSimulatedWalker()
        {
            const float target = 12.7f;
            _operator.Send("{\"type\":\"command\",\"action\":\"simulatePose\",\"beatId\":null,"
                           + $"\"value\":{{\"s\":{F(target)},\"headingRad\":0.4}}}}");

            yield return Until(() =>
                _link.Poses.TryGetPose(_link.NowMs, out var pose)
                && Mathf.Abs(pose.S - target) < 0.25f,
                $"the simulated walker to arrive at s={target} m from a simulatePose command", 6f);

            _link.Poses.TryGetPose(_link.NowMs, out var arrived);
            Assert.That(arrived.Origin, Is.EqualTo(PoseOrigin.Simulated));

            // The pose completed its missing half against the journey's own centreline: the
            // command carried no point at all.
            Vector3 expected = Centreline.PointAt(target, _journey.site.centreline);
            Assert.That(Vector3.Distance(arrived.AnchorLocalPosition, expected), Is.LessThan(0.1f),
                        "the point should have been reconstructed from s against the centreline");
        }

        /// <summary>
        /// The whole point of the channel: scrub, and the right beat arms and completes.
        ///
        /// A pose landing at the right distance is necessary and nowhere near sufficient — the
        /// question the artist is actually asking is whether the walk rehearsed at the desk is
        /// the walk performed at the creek. So this drives a real <see cref="JourneyProgression"/>
        /// from the followed pose, over the real socket, along the real published centreline, and
        /// watches what the trigger machine does.
        ///
        /// `tree` sits at s = 3.97 m with a 1.9 m enter radius, 1.2 s of dwell and a proximity
        /// interaction, so walking up to it and standing there should arm, commit and complete it
        /// — and nothing else, because every other beat is metres away.
        /// </summary>
        [UnityTest]
        [Timeout(60000)]
        public IEnumerator AScrubbedWalkArmsAndCompletesTheRightBeat()
        {
            var beat = _journey.beats.Find(b => b.id == "tree");
            Assert.That(beat, Is.Not.Null, "the published journey should still open with `tree`");

            var progression = new JourneyProgression(_journey);
            var seen = new List<string>();

            float s = 0f;
            float lastPoseS = float.NaN;
            double lastSentAt = 0;
            double deadline = Time.realtimeSinceStartupAsDouble + 25.0;

            while (Time.realtimeSinceStartupAsDouble < deadline)
            {
                // A walking pace up to the beat, then standing in it. The editor throttles its
                // stream to about twenty a second and so does this.
                s = Mathf.Min(beat.s, s + 1.2f * Time.deltaTime);
                if (Time.realtimeSinceStartupAsDouble - lastSentAt > 0.05)
                {
                    lastSentAt = Time.realtimeSinceStartupAsDouble;
                    Vector3 point = Centreline.PointAt(s, _journey.site.centreline);
                    _operator.Send("{\"type\":\"pose\","
                                   + $"\"s\":{F(s)},"
                                   + $"\"position\":{{\"x\":{F(point.x)},\"y\":{F(point.y)},\"z\":{F(point.z)}}},"
                                   + $"\"headingRad\":0,\"slug\":\"{Slug}\"}}");
                }

                // Everything downstream takes the switch, not a source — it cannot tell that this
                // walk is being driven from a browser.
                if (_link.Poses.TryGetPose(_link.NowMs, out var pose))
                {
                    lastPoseS = pose.S;
                    progression.Tick(pose.AnchorLocalPosition, pose.Quality, Time.deltaTime);
                }
                foreach (var e in progression.DrainEvents()) seen.Add(e.ToString());

                if (progression.StateOf("tree") == BeatState.Complete) break;
                yield return null;
            }

            UnityEngine.Debug.Log($"[integration] scrubbed walk produced: {string.Join(" | ", seen)}");

            Assert.That(progression.StateOf("tree"), Is.EqualTo(BeatState.Complete),
                        $"the beat never completed; events were {string.Join(" | ", seen)}");
            Assert.That(seen, Has.Some.StartsWith("Armed tree"));
            Assert.That(seen, Has.Some.StartsWith("Committed tree"));
            Assert.That(seen, Has.Some.StartsWith("Completed tree"));

            foreach (var other in _journey.beats)
            {
                if (other.id == "tree") continue;
                Assert.That(progression.StateOf(other.id), Is.EqualTo(BeatState.Idle),
                            $"'{other.id}' is metres away and must not have been touched");
            }

            // The progression's own idea of where it is came from the browser, through the bus and
            // through the pose source, and matches it to the centimetre.
            Assert.That(progression.S, Is.EqualTo(lastPoseS).Within(0.05f),
                        "the trigger machine is running on the position the operator scrubbed");

            // It completed before reaching the marker, and that is correct rather than sloppy:
            // dwell accumulates from the moment the beat arms, so a visitor walking in at pace
            // satisfies it partway through the enter radius rather than at the centre.
            Assert.That(Mathf.Abs(progression.S - beat.s),
                        Is.LessThan(beat.trigger.enterRadiusM),
                        "it can only have committed from inside the enter radius");
        }

        /// <summary>
        /// The same channel, driven by bytes the real editor page put on the wire.
        ///
        /// `test/unity-integration.e2e.test.mjs` records what a device receives while Playwright
        /// scrubs the editor, and this replays one of those frames verbatim. It closes the last
        /// gap in the chain: everything else here sends frames this fixture composed, and a
        /// fixture can only be as right as whoever wrote it.
        ///
        /// The recording is made against the test fixture's own 10 m journey, so its `position`
        /// belongs to that geometry rather than to the garden's. `s` is what the editor is
        /// authoritative about and `s` is what is checked.
        /// </summary>
        [UnityTest]
        public IEnumerator TheRealEditorsOwnFrameMovesTheWalker()
        {
            // One frame per line, so a frame can be replayed byte for byte without this test
            // parsing and re-serialising it — which would only prove that two of my own functions
            // agree.
            string path = Path.Combine(LiveService.RepoRoot, "logs", "editor-pose-frames.jsonl");
            if (!File.Exists(path))
            {
                Assert.Ignore($"no {path} — record it with "
                              + "SHOALING_BROWSER=1 node --test test/unity-integration.e2e.test.mjs");
            }

            string line = null;
            foreach (string candidate in File.ReadAllLines(path))
            {
                if (!string.IsNullOrWhiteSpace(candidate)) line = candidate;
            }
            Assert.That(line, Is.Not.Null, "the recording is empty");

            Assert.That(JsonValue.TryParse(line, out var frame), Is.True);
            Assert.That(frame["type"].AsString(), Is.EqualTo("pose"));
            float expected = frame["s"].AsFloat();

            _operator.Send(line);

            yield return Until(() =>
                _link.Poses.TryGetPose(_link.NowMs, out var pose)
                && Mathf.Abs(pose.S - expected) < 0.05f,
                $"the walker to follow the editor's own recorded frame to s={expected}", 6f);
        }

        // ------------------------------------------------------------------ 2. controller → Unity

        [UnityTest]
        public IEnumerator AFiredBeatArrivesAtItsMomentAndIsAcked()
        {
            yield return Until(() => _link.Client.Clock.IsTrusted,
                               "a trusted clock, so the fire time can be checked in server time", 10f);

            _operator.Forget();
            _operator.Send("{\"type\":\"command\",\"action\":\"fireBeat\",\"beatId\":\"heron\",\"value\":null}");

            // The operator's own copy of the schedule, straight off the bus.
            yield return Until(() => _operator.Latest("commandIssued") != null,
                               "the bus to schedule the command");
            var issued = _operator.Latest("commandIssued")["command"];
            double fireAtMs = issued["fireAtMs"].AsDouble();
            double issuedAtMs = issued["issuedAtMs"].AsDouble();
            Assert.That(fireAtMs - issuedAtMs, Is.GreaterThan(0),
                        "the bus schedules ahead rather than triggering");

            double firedAtServerMs = double.NaN;
            yield return Until(() =>
            {
                if (_effects.Calls.Count == 0) return false;
                if (double.IsNaN(firedAtServerMs))
                {
                    firedAtServerMs = _link.Client.Clock.ToServerMs(_link.NowMs);
                }
                return true;
            }, "the beat to fire", 6f);

            Assert.That(_effects.Calls, Is.EqualTo(new List<string> { "fireBeat:heron" }));

            // The whole point of the lead: acting at fireAtMs converts variable network latency
            // into fixed latency. Acting on arrival would land this a full lead early.
            Assert.That(firedAtServerMs, Is.GreaterThanOrEqualTo(fireAtMs - 60),
                        "the beat fired before its scheduled moment — it acted on arrival");
            Assert.That(firedAtServerMs, Is.LessThan(fireAtMs + 250),
                        "the beat fired well after its scheduled moment");

            yield return Until(() =>
            {
                var ack = _operator.Latest("commandAck");
                return ack != null && ack["commandId"].AsString() == issued["id"].AsString()
                       && ack["applied"].AsBool();
            }, "the ack to reach the operator, applied");
        }

        [UnityTest]
        public IEnumerator EveryOperatorActionReachesTheEffectsInOrder()
        {
            _operator.Forget();
            var actions = new[] { "replayCurrent", "advance", "silence", "resume" };
            foreach (string action in actions)
            {
                _operator.Send($"{{\"type\":\"command\",\"action\":\"{action}\",\"beatId\":null}}");
                // Spaced like button presses. A burst in the same millisecond is a different
                // question, and it has its own test below.
                double until = Time.realtimeSinceStartupAsDouble + 0.15;
                while (Time.realtimeSinceStartupAsDouble < until) yield return null;
            }

            yield return Until(() => _effects.Calls.Count == 4, "all four actions to be applied", 8f);
            Assert.That(_effects.Calls, Is.EqualTo(new List<string>(actions)),
                        "commands must arrive in the order they were pressed");

            yield return Until(() => _operator.AllOf("commandAck").Count == 4,
                               "four acks to come back");
            foreach (var ack in _operator.AllOf("commandAck"))
            {
                Assert.That(ack["applied"].AsBool(), Is.True);
            }
        }

        /// <summary>
        /// Commands the bus stamps with the same fireAtMs are not guaranteed to fire in the order
        /// they were issued.
        ///
        /// `CommandScheduler.Enqueue` keeps the queue in fire order with `List.Sort`, and that
        /// sort is introsort — unstable. Two commands issued inside the same millisecond get the
        /// same `fireAtMs`, tie, and can come out either way round. This run found it by sending
        /// four in a burst and getting `advance` before `replayCurrent`.
        ///
        /// Narrow: an operator's fingers cannot tie, and every action but `fireBeat` is
        /// idempotent enough that the order rarely reads. It is on record because the doc promises
        /// the opposite — "each one is a thing that happened, so they queue and fire in order" —
        /// and because `POST /api/control/command` can be driven faster than fingers.
        ///
        /// This test asserts the tie rather than the reordering, which would be flaky. The fix, if
        /// it is wanted, is a stable insert or a sequence tiebreaker; it is not this agent's to
        /// make.
        /// </summary>
        [UnityTest]
        public IEnumerator CommandsIssuedInTheSameMillisecondTieInTheQueue()
        {
            _operator.Forget();
            var actions = new[] { "replayCurrent", "advance", "silence", "resume" };
            foreach (string action in actions)
            {
                _operator.Send($"{{\"type\":\"command\",\"action\":\"{action}\",\"beatId\":null}}");
            }

            yield return Until(() => _effects.Calls.Count == 4, "all four actions to be applied", 8f);
            Assert.That(_effects.Calls, Is.EquivalentTo(actions),
                        "all four must arrive, whatever the order");

            var issued = _operator.AllOf("commandIssued");
            var fireTimes = new List<double>();
            foreach (var frame in issued) fireTimes.Add(frame["command"]["fireAtMs"].AsDouble());

            bool tied = false;
            for (int i = 1; i < fireTimes.Count; i++)
            {
                if (fireTimes[i] == fireTimes[i - 1]) tied = true;
            }

            UnityEngine.Debug.Log($"[integration] burst fired as {string.Join(", ", _effects.Calls)} "
                                  + $"for fireAtMs {string.Join(", ", fireTimes)}");

            if (!tied)
            {
                Assert.Ignore("no two commands shared a fireAtMs on this run — the machine was "
                              + "slow enough to separate them, and there is nothing to show");
            }
            Assert.That(tied, Is.True,
                        "the bus stamps a burst with equal fireAtMs, and the device's queue sorts "
                        + "on that value with an unstable sort");
        }

        /// <summary>
        /// A command that surfaces after its own TTL is dropped rather than fired late.
        ///
        /// Produced honestly: the client is not pumped for longer than the bus's ten-second TTL,
        /// which is what a backgrounded phone or a wifi stall does. The frame is sitting in the
        /// transport's queue the whole time — nothing here fakes a clock.
        /// </summary>
        [UnityTest]
        [Timeout(60000)]
        public IEnumerator ACommandThatSurfacesTooLateIsDroppedNotFired()
        {
            // Its own client, because this one must be able to stop pumping — ControlLink pumps
            // every frame by design.
            using var transport = new WebSocketControlTransport();
            var frame = new CentrelineFrame(_journey.site.centreline);
            var simulated = new SimulatedPoseSource(frame);
            var poses = new PoseSourceSwitch(new VpsPoseSource(frame), simulated);
            var effects = new RecordingEffects();
            var clock = new LocalClock();
            var client = new ControlClient(transport, poses, simulated, effects);

            client.Connect(ControlClient.DeviceUrl("127.0.0.1", _service.Port), clock.NowMs);
            // Pumped until the clock is trusted, not merely until the socket is up: expiry is a
            // question about server timestamps, and a client with no offset yet answers it on
            // relative timing instead. That case is its own test below.
            yield return Until(() => { client.Pump(clock.NowMs); return client.Clock.IsTrusted; },
                               "the second client to reach a trusted clock", 15f);

            _operator.Forget();
            _operator.Send("{\"type\":\"command\",\"action\":\"fireBeat\",\"beatId\":\"redd\"}");

            // Not pumping. The frame crosses the socket and waits in the queue.
            double stallUntil = Time.realtimeSinceStartupAsDouble + 11.0;
            while (Time.realtimeSinceStartupAsDouble < stallUntil) yield return null;

            Assert.That(effects.Calls, Is.Empty, "nothing can have fired — nothing was pumped");

            client.Pump(clock.NowMs);
            Assert.That(effects.Calls, Is.Empty,
                        "the command was past its expiry when it surfaced and must not fire");

            yield return Until(() =>
            {
                client.Pump(clock.NowMs);
                var ack = _operator.Latest("commandAck");
                return ack != null && !ack["applied"].AsBool();
            }, "an ack saying it did not play, so the operator is not left listening for it", 8f);

            client.Disconnect();
        }

        /// <summary>
        /// The same eleven-second stall, on a connection young enough that the stall ruins the
        /// only clock sample it had — and the command fires anyway, eleven seconds late.
        ///
        /// The mechanism, which took a live run to see: the heartbeat goes out as the link comes
        /// Online and its pong is still unread when the stall begins, so the round trip finally
        /// measured against it is the length of the stall. One sample, eleven seconds wide,
        /// uncertainty far past the trust threshold — and an untrusted estimate falls back to
        /// relative timing, which measures the TTL from ARRIVAL. Nothing can be too late for a
        /// deadline that starts when it lands.
        ///
        /// Once a second good sample exists the minimum-round-trip filter throws the ruined one
        /// away and the test above holds, which is what that filter is for. The window is narrow —
        /// a stall in the first seconds of a connection — and the fallback itself is deliberate
        /// and documented. What is not documented is that expiry goes with it.
        ///
        /// A characterisation test, not an endorsement. Recorded here because the alternative is
        /// finding it in a creek.
        /// </summary>
        [UnityTest]
        [Timeout(60000)]
        public IEnumerator AStallThatWrecksTheOnlyClockSampleLetsAStaleCommandFire()
        {
            using var transport = new WebSocketControlTransport();
            var frame = new CentrelineFrame(_journey.site.centreline);
            var simulated = new SimulatedPoseSource(frame);
            var poses = new PoseSourceSwitch(new VpsPoseSource(frame), simulated);
            var effects = new RecordingEffects();
            var clock = new LocalClock();
            var client = new ControlClient(transport, poses, simulated, effects);

            client.Connect(ControlClient.DeviceUrl("127.0.0.1", _service.Port), clock.NowMs);
            // Stopping at Online leaves exactly one heartbeat in flight with its pong unread —
            // the state a client is in for its first few hundred milliseconds.
            yield return Until(() => { client.Pump(clock.NowMs); return client.State == ControlLinkState.Online; },
                               "the client to come Online");
            Assume.That(client.Clock.HasEstimate, Is.False,
                        "this test needs the pong to still be unread when the stall starts");

            _operator.Forget();
            _operator.Send("{\"type\":\"command\",\"action\":\"fireBeat\",\"beatId\":\"strider\"}");

            double stallUntil = Time.realtimeSinceStartupAsDouble + 11.0;
            while (Time.realtimeSinceStartupAsDouble < stallUntil) yield return null;

            yield return Until(() => { client.Pump(clock.NowMs); return effects.Calls.Count > 0; },
                               "the stale command to be applied on relative timing", 8f);

            Assert.That(effects.Calls, Is.EqualTo(new List<string> { "fireBeat:strider" }),
                        "a command 11 s past its TTL was applied, because with no clock estimate "
                        + "the TTL is measured from arrival rather than from issue");

            client.Disconnect();
        }

        // ------------------------------------------------------------------ 3. Unity → controller

        [UnityTest]
        public IEnumerator UnityStatusReachesTheOperatorsView()
        {
            _status.Value = new ControlStatus
            {
                Site = Slug,
                Revision = _journey.revision,
                Localization = "precise",
                TrackingConfidence = 0.82,
                CurrentBeat = "heron",
                HighWaterMark = 3,
                Completed = new[] { "tree", "redd" },
                ShoalCount = 27,
            };

            // Give the report an s to carry, through the channel that works today.
            _operator.Send("{\"type\":\"command\",\"action\":\"simulatePose\",\"value\":{\"s\":7.5}}");

            JsonValue state = null;
            yield return Until(() =>
            {
                state = _operator.Latest("state");
                return state != null
                       && state["state"]["currentBeat"].AsString() == "heron"
                       && Mathf.Abs(state["state"]["s"].AsFloat() - 7.5f) < 0.25f;
            }, "the operator's authoritative state to show what Unity is doing", 8f);

            var merged = state["state"];
            Assert.That(merged["site"].AsString(), Is.EqualTo(Slug));
            Assert.That(merged["revision"].AsDouble(), Is.EqualTo((double)_journey.revision));
            Assert.That(merged["localization"].AsString(), Is.EqualTo("precise"));
            Assert.That(merged["trackingConfidence"].AsDouble(), Is.EqualTo(0.82).Within(0.001));
            Assert.That(merged["shoalCount"].AsDouble(), Is.EqualTo(27));
            Assert.That(merged["highWaterMark"].AsDouble(), Is.EqualTo(3));
            Assert.That(merged["completed"].Count, Is.EqualTo(2));

            // The bytes Unity puts on the wire, kept for test/unity-integration.e2e.test.mjs to
            // render in the real controller page. A recording rather than a hand-written fixture,
            // so the page is checked against what the phone genuinely sends — including the
            // fields the client filled in from the live pose rather than from the journey.
            var composed = _status.Value;
            if (_link.Poses.TryGetPose(_link.NowMs, out var live))
            {
                composed.S = live.S;
                composed.LateralM = live.LateralM;
            }
            string recording = ControlProtocol.Status(composed);
            string path = Path.Combine(LiveService.RepoRoot, "logs", "unity-status-frame.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, recording);
            UnityEngine.Debug.Log($"[integration] Unity's status frame recorded to {path}: {recording}");
        }

        /// <summary>
        /// Nothing outside the bus's allow-list is ever emitted.
        ///
        /// The failure this guards against is silent in both directions: the server merges the
        /// keys it knows and drops the rest without complaint, so a report with a stray key still
        /// looks like a report and a nested one arrives as nothing at all.
        /// </summary>
        [UnityTest]
        public IEnumerator TheStatusReportCarriesOnlyAllowListedKeys()
        {
            var allowed = new HashSet<string>
            {
                "type", "site", "revision", "localization", "trackingConfidence",
                "s", "lateralM", "currentBeat", "highWaterMark", "completed", "shoalCount",
            };

            _status.Value = new ControlStatus
            {
                Site = Slug, Localization = "coarse", TrackingConfidence = 0.4,
                CurrentBeat = null, HighWaterMark = -1, Completed = Array.Empty<string>(),
            };

            Assert.That(JsonValue.TryParse(ControlProtocol.Status(_status.Value), out var frame), Is.True);
            foreach (string key in frame.Keys)
            {
                Assert.That(allowed.Contains(key), Is.True, $"'{key}' is not on the bus's allow-list");
            }
            yield break;
        }

        // ------------------------------------------------------------------ helpers

        private static string F(float value) =>
            value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>Wait for a condition, or fail saying what never happened. NUnit's own
        /// WaitUntil has no deadline, and a hung integration test tells you nothing.</summary>
        private static IEnumerator Until(Func<bool> condition, string what, float seconds = 5f)
        {
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed.TotalSeconds < seconds)
            {
                bool met;
                try { met = condition(); }
                catch (Exception) { met = false; }
                if (met) yield break;
                yield return null;
            }
            Assert.Fail($"timed out after {seconds:0.#} s waiting for {what}");
        }

        private sealed class FakeStatusSource : IControlStatusSource
        {
            public ControlStatus Value = new()
            {
                HighWaterMark = -1,
                Completed = Array.Empty<string>(),
            };

            public ControlStatus Snapshot() => Value;
        }
    }
}
