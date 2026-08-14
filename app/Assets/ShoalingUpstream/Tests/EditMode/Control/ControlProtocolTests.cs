using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using ShoalingUpstream.Control;

namespace ShoalingUpstream.Tests.Control
{
    /// <summary>
    /// The wire format, against the shapes test/control-bus.e2e.test.mjs pins on the other side.
    ///
    /// Everything here is a round trip or a malformed input, because those are the two ways this
    /// layer fails in the field: a key the server's allow-list quietly drops, or a truncated
    /// frame taken during a wifi stall.
    /// </summary>
    public class ControlProtocolTests
    {
        /// <summary>Exactly the keys service/src/control-bus.mjs merges. Anything else we send
        /// is discarded silently, which in a park looks like the phone not reporting at all.</summary>
        private static readonly HashSet<string> StatusAllowList = new()
        {
            "type", "site", "revision", "localization", "trackingConfidence",
            "s", "lateralM", "currentBeat", "highWaterMark", "completed", "shoalCount",
        };

        [Test]
        public void AWelcomeCarriesTheSessionThatScopesCommandIds()
        {
            var msg = ControlProtocol.Parse(Frames.Welcome("s-abc", 7));
            Assert.AreEqual(ControlMessageKind.Welcome, msg.Kind);
            Assert.AreEqual("s-abc", msg.SessionId);
            Assert.AreEqual(7, msg.ClientId);
            Assert.AreEqual("device", msg.Role);
        }

        [Test]
        public void ACommandCarriesItsScheduleNotAnInstruction()
        {
            var msg = ControlProtocol.Parse(
                Frames.Command(ControlActions.FireBeat, 1_000_000, leadMs: 400, beatId: "falls"));

            Assert.AreEqual(ControlMessageKind.Command, msg.Kind);
            Assert.AreEqual("fireBeat", msg.Command.Action);
            Assert.AreEqual("falls", msg.Command.BeatId);
            Assert.AreEqual(1_000_400, msg.Command.FireAtMs, 0.001);
            Assert.AreEqual(400, msg.Command.LeadMs, 0.001);
            Assert.AreEqual(10_000, msg.Command.TtlMs, 0.001);
        }

        [Test]
        public void AStatusIsFlatAndOnlyUsesKeysTheBusWillMerge()
        {
            string json = ControlProtocol.Status(new ControlStatus
            {
                Localization = "precise",
                TrackingConfidence = 0.92,
                S = 9.4,
                LateralM = 0.6,
                CurrentBeat = "strider",
                HighWaterMark = 2,
                Completed = new[] { "tree", "redd" },
                ShoalCount = 33,
            });

            Assert.IsTrue(JsonValue.TryParse(json, out var parsed));
            Assert.AreEqual("status", parsed["type"].AsString());
            Assert.AreEqual(9.4, parsed["s"].AsDouble(), 0.0001);
            Assert.AreEqual("strider", parsed["currentBeat"].AsString());
            Assert.AreEqual(2, parsed["completed"].Count);

            foreach (string key in parsed.Keys)
            {
                Assert.IsTrue(StatusAllowList.Contains(key),
                    $"'{key}' is outside the server's allow-list and would be dropped silently");
            }

            Assert.AreEqual(JsonKind.Number, parsed["s"].Kind,
                "the report must be flat — a nested pose object arrives as no report at all");
        }

        [Test]
        public void AStatusFromADeviceInAnotherLocaleIsStillJson()
        {
            // A phone set to French formats 9.4 as "9,4" under the current culture, and
            // JSON.parse rejects the frame outright — a failure that only appears on someone
            // else's phone.
            var original = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("fr-FR");
                string json = ControlProtocol.Status(new ControlStatus
                {
                    Localization = "precise", TrackingConfidence = 0.92, S = 9.4,
                    Completed = System.Array.Empty<string>(),
                });
                StringAssert.Contains("9.4", json);
                Assert.IsTrue(JsonValue.TryParse(json, out _));
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = original;
            }
        }

        [Test]
        public void AnAckEchoesTheSessionTheCommandCameFrom()
        {
            string json = ControlProtocol.Ack("s-abc-7", true, "played", "s-abc");
            Assert.IsTrue(JsonValue.TryParse(json, out var parsed));
            Assert.AreEqual("ack", parsed["type"].AsString());
            Assert.AreEqual("s-abc-7", parsed["commandId"].AsString());
            Assert.IsTrue(parsed["applied"].AsBool());
            Assert.AreEqual("s-abc", parsed["sessionId"].AsString(),
                "the bus drops an ack whose session is not its own");
        }

        [Test]
        public void APoseIsReadWhicheverHalfTheEditorSent()
        {
            var sOnly = ControlProtocol.Parse(
                Frames.Command(ControlActions.SimulatePose, 0, valueJson: "{\"s\":12.5,\"headingRad\":1.5}"));
            Assert.IsTrue(ControlProtocol.TryReadPose(sOnly.Command, out var a));
            Assert.IsTrue(a.HasS);
            Assert.IsFalse(a.HasPosition);
            Assert.AreEqual(12.5f, a.S, 0.0001f);
            Assert.AreEqual(1.5f, a.HeadingRad, 0.0001f);

            var full = ControlProtocol.Parse(Frames.Command(ControlActions.SimulatePose, 0,
                valueJson: "{\"s\":12.5,\"x\":1,\"y\":0,\"z\":12,\"headingRad\":0}"));
            Assert.IsTrue(ControlProtocol.TryReadPose(full.Command, out var b));
            Assert.IsTrue(b.HasS);
            Assert.IsTrue(b.HasPosition);
            Assert.AreEqual(12f, b.Position.z, 0.0001f);

            var xyzOnly = ControlProtocol.Parse(Frames.Command(ControlActions.SimulatePose, 0,
                valueJson: "{\"x\":0,\"y\":0,\"z\":20}"));
            Assert.IsTrue(ControlProtocol.TryReadPose(xyzOnly.Command, out var c));
            Assert.IsFalse(c.HasS);
            Assert.IsTrue(c.HasPosition);
        }

        [Test]
        public void AnEmptyOrWrongShapedPoseIsRefusedRatherThanReadAsZero()
        {
            // Reading a missing s as 0 would teleport the visitor to the downstream end of the
            // creek and fire the first beat.
            var empty = ControlProtocol.Parse(Frames.Command(ControlActions.SimulatePose, 0, valueJson: "{}"));
            Assert.IsFalse(ControlProtocol.TryReadPose(empty.Command, out _));

            var nullValue = ControlProtocol.Parse(Frames.Command(ControlActions.SimulatePose, 0));
            Assert.IsFalse(ControlProtocol.TryReadPose(nullValue.Command, out _));

            var notAPose = ControlProtocol.Parse(Frames.Command(ControlActions.FireBeat, 0,
                valueJson: "{\"s\":5}", beatId: "falls"));
            Assert.IsFalse(ControlProtocol.TryReadPose(notAPose.Command, out _),
                "only simulatePose is a pose, whatever else happens to carry an s");
        }

        [Test]
        public void RubbishOnTheSocketParsesToNothingRatherThanThrowing()
        {
            Assert.IsNull(ControlProtocol.Parse("not json at all"));
            Assert.IsNull(ControlProtocol.Parse("{\"type\":\"command\","));   // truncated mid-frame
            Assert.IsNull(ControlProtocol.Parse(""));
            Assert.IsNull(ControlProtocol.Parse(null));
            Assert.IsNull(ControlProtocol.Parse("[1,2,3]"), "the protocol is objects");
        }

        [Test]
        public void AMessageTypeThisBuildDoesNotKnowIsNotAnError()
        {
            // The bus is expected to grow. An older phone in the field has to keep walking.
            var msg = ControlProtocol.Parse("{\"type\":\"somethingNewer\",\"x\":1}");
            Assert.IsNotNull(msg);
            Assert.AreEqual(ControlMessageKind.Unknown, msg.Kind);
            Assert.AreEqual("somethingNewer", msg.RawType);
        }

        [Test]
        public void JsonRoundTripsTheAwkwardParts()
        {
            string written = new JsonWriter()
                .Field("type", "hello")
                .Field("device", "iPhone \"14\"\n\tPro \\ 128")
                .Field("gain", -8.25)
                .Field("loop", true)
                .Field("completed", new List<string> { "a", "b" })
                .NullField("currentBeat")
                .Done();

            Assert.IsTrue(JsonValue.TryParse(written, out var parsed));
            Assert.AreEqual("iPhone \"14\"\n\tPro \\ 128", parsed["device"].AsString());
            Assert.AreEqual(-8.25, parsed["gain"].AsDouble(), 0.0001);
            Assert.IsTrue(parsed["loop"].AsBool());
            Assert.AreEqual(JsonKind.Null, parsed["currentBeat"].Kind);
            CollectionAssert.AreEqual(new[] { "a", "b" },
                parsed["completed"].Items.Select(i => i.AsString()).ToArray());

            Assert.IsTrue(JsonValue.TryParse("{\"a\":{\"b\":[1,2,{\"c\":-1.5e3}]},\"u\":\"\\u00e9\"}",
                out var nested));
            Assert.AreEqual(-1500, nested["a"]["b"][2]["c"].AsDouble(), 0.0001);
            Assert.AreEqual("é", nested["u"].AsString());
        }

        [Test]
        public void AMissingKeyReadsAsNullSoLookupsCanBeChained()
        {
            Assert.IsTrue(JsonValue.TryParse("{\"a\":1}", out var v));
            Assert.AreEqual(JsonKind.Null, v["nope"]["deeper"].Kind);
            Assert.AreEqual(7, v["nope"].AsDouble(7), 0.0001);
            Assert.IsNull(v["nope"].AsString());
        }

        // ------------------------------------------------------------------ the streamed pose

        [Test]
        public void AStreamedPoseIsItsOwnMessageAndCarriesBothHalves()
        {
            var msg = ControlProtocol.Parse(Frames.StreamedPose(
                s: 9.4, position: (1.5, 0.2, 2.5), headingRad: 1.2, sentAtMs: 1_700_000));

            Assert.AreEqual(ControlMessageKind.Pose, msg.Kind);
            Assert.IsTrue(msg.HasPose);
            Assert.IsTrue(msg.Pose.HasS);
            Assert.AreEqual(9.4f, msg.Pose.S, 0.0001f);
            Assert.IsTrue(msg.Pose.HasPosition);
            Assert.AreEqual(2.5f, msg.Pose.Position.z, 0.0001f,
                "the point rides nested under `position`, not flat beside s");
            Assert.AreEqual(1.2f, msg.Pose.HeadingRad, 0.0001f);
            Assert.AreEqual("test-creek", msg.Slug);
            Assert.AreEqual(1_700_000, msg.ServerNowMs, 0.001);
            Assert.IsNull(msg.Command, "a pose is not a command and carries no schedule");
        }

        [Test]
        public void EitherHalfOfAStreamedPoseIsEnough()
        {
            // The editor's scrubber works in distance along the path and may send nothing else.
            var sOnly = ControlProtocol.Parse(Frames.StreamedPose(s: 9.4, position: null));
            Assert.IsTrue(sOnly.HasPose);
            Assert.IsTrue(sOnly.Pose.HasS);
            Assert.IsFalse(sOnly.Pose.HasPosition);

            var pointOnly = ControlProtocol.Parse(Frames.StreamedPose(s: null, position: (1, 0, 2)));
            Assert.IsTrue(pointOnly.HasPose);
            Assert.IsFalse(pointOnly.Pose.HasS);
            Assert.IsTrue(pointOnly.Pose.HasPosition);
        }

        [Test]
        public void AStreamedPoseWithNeitherHalfIsRefusedRatherThanReadAsZero()
        {
            // The bus nulls every field rather than omitting keys, so this frame really arrives.
            // Reading a missing s as 0 would teleport the visitor to the downstream end of the
            // creek and fire the first beat.
            var msg = ControlProtocol.Parse(Frames.StreamedPose(s: null, position: null));

            Assert.AreEqual(ControlMessageKind.Pose, msg.Kind, "it is still a pose message");
            Assert.IsFalse(msg.HasPose, "but it carries no pose, so the previous one stands");
        }

        [Test]
        public void APartialPointIsNotAPoint()
        {
            var msg = ControlProtocol.Parse(
                "{\"type\":\"pose\",\"s\":null,\"position\":{\"x\":1,\"z\":2},\"headingRad\":0}");

            Assert.AreEqual(ControlMessageKind.Pose, msg.Kind);
            Assert.IsFalse(msg.HasPose, "two thirds of a point is not a position");
        }

        [Test]
        public void AMissingHeadingReadsAsZeroRatherThanRefusingTheWholePose()
        {
            // Heading only turns the head. Losing it must not lose the walker.
            var msg = ControlProtocol.Parse(Frames.StreamedPose(s: 4.2, headingRad: null));

            Assert.IsTrue(msg.HasPose);
            Assert.AreEqual(4.2f, msg.Pose.S, 0.0001f);
            Assert.AreEqual(0f, msg.Pose.HeadingRad, 0.0001f);
        }

        [Test]
        public void TheSimulatePoseCommandStillParsesAlongsideTheStreamedPose()
        {
            // A phone in the field may still be sent one, and removing the older form would be a
            // second break. Its point is FLAT beside its s — the layouts differ, deliberately.
            var msg = ControlProtocol.Parse(Frames.Command(
                ControlActions.SimulatePose, 1_000_000,
                valueJson: "{\"s\":27.5,\"x\":0,\"y\":0,\"z\":27.5,\"headingRad\":1.2}"));

            Assert.AreEqual(ControlMessageKind.Command, msg.Kind);
            Assert.IsTrue(ControlProtocol.TryReadPose(msg.Command, out var pose));
            Assert.AreEqual(27.5f, pose.S, 0.0001f);
            Assert.IsTrue(pose.HasPosition);
            Assert.AreEqual(27.5f, pose.Position.z, 0.0001f);
        }

        [Test]
        public void TheTwoPoseLayoutsAreNotInterchangeable()
        {
            // Pinning the difference rather than papering over it. A reader permissive enough to
            // take either shape from either message would go on working the day one end changed,
            // which is exactly how the original mismatch survived 201 passing tests.
            var streamedShape = ControlProtocol.Parse(Frames.StreamedPose(s: null, position: (1, 2, 3)));
            Assert.IsTrue(streamedShape.HasPose, "nested `position` is the pose message's shape");

            var flatAsPose = ControlProtocol.Parse(
                "{\"type\":\"pose\",\"s\":null,\"x\":1,\"y\":2,\"z\":3}");
            Assert.IsFalse(flatAsPose.HasPose, "a flat point is not this message's shape");

            var command = ControlProtocol.Parse(Frames.Command(
                ControlActions.SimulatePose, 1_000, valueJson: "{\"position\":{\"x\":1,\"y\":2,\"z\":3}}"));
            Assert.IsFalse(ControlProtocol.TryReadPose(command.Command, out _),
                "and a nested point is not the command's");
        }

        [Test]
        public void HelloAndHeartbeatAreWhatTheServerSwitchesOn()
        {
            Assert.IsTrue(JsonValue.TryParse(ControlProtocol.Hello("iPhone", "iOS 18", "dev"), out var hello));
            Assert.AreEqual("hello", hello["type"].AsString());
            Assert.AreEqual("iOS 18", hello["os"].AsString());

            Assert.IsTrue(JsonValue.TryParse(ControlProtocol.Heartbeat(), out var beat));
            Assert.AreEqual("heartbeat", beat["type"].AsString());
        }
    }
}
