using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using ShoalingUpstream.Journey;

namespace ShoalingUpstream.Tests
{
    /// <summary>
    /// The trigger machine, exercised by walking a synthetic visitor along the reach.
    ///
    /// These are the behaviours a field test is worst at producing on demand — standing near a
    /// boundary, backtracking, loitering, losing tracking mid-beat — so they belong here rather
    /// than in a notebook at the creek.
    /// </summary>
    public class JourneyProgressionTests
    {
        private const float Dt = 1f / 30f;

        private static JourneyDocument MakeJourney(params (string id, float s, string kind)[] beats)
        {
            var doc = new JourneyDocument
            {
                schemaVersion = JourneyDocument.SupportedSchemaVersion,
                journeyId = "test",
                site = new SiteRef
                {
                    centreline = new List<Vec3>
                    {
                        new() { x = 0, y = 0, z = 0 },
                        new() { x = 0, y = 0, z = 60 },
                    },
                },
                shoal = new Shoal { startingCount = 40, minimumCount = 6 },
            };

            foreach (var (id, s, kind) in beats)
            {
                doc.beats.Add(new Beat
                {
                    id = id,
                    title = id,
                    interaction = kind,
                    s = s,
                    position = new Vec3 { x = 0, y = 0, z = s },
                    trigger = new Trigger
                    {
                        enterRadiusM = 2.5f,
                        exitRadiusM = 4.0f,
                        dwellSeconds = 1.0f,
                        minimumHoldSeconds = 5f,
                        requiresPreviousComplete = true,
                    },
                    givesFish = kind == "give" ? 12 : 0,
                });
            }
            return doc;
        }

        private static Vector3 At(float s) => new(0, 0, s);

        /// <summary>Hold a position for a while, returning everything the machine raised.</summary>
        private static List<JourneyProgression.Event> Hold(
            JourneyProgression p, float s, float seconds,
            LocalizationQuality q = LocalizationQuality.Precise)
        {
            var events = new List<JourneyProgression.Event>();
            int steps = Mathf.CeilToInt(seconds / Dt);
            for (int i = 0; i < steps; i++)
            {
                p.Tick(At(s), q, Dt);
                events.AddRange(p.DrainEvents());
            }
            return events;
        }

        [Test]
        public void NothingFiresBeforeLocalizationIsPrecise()
        {
            var p = new JourneyProgression(MakeJourney(("tree", 10f, "proximity")));

            var coarse = Hold(p, 10f, 5f, LocalizationQuality.Coarse);
            Assert.IsEmpty(coarse, "a coarse fix must not fire anything");
            Assert.IsNull(p.Current);

            var unavailable = Hold(p, 10f, 5f, LocalizationQuality.Unavailable);
            Assert.IsEmpty(unavailable);

            var precise = Hold(p, 10f, 3f);
            Assert.IsNotEmpty(precise, "a precise fix at the beat should fire it");
            Assert.AreEqual(BeatState.Complete, p.StateOf("tree"));
        }

        [Test]
        public void DwellIsRequiredBeforeCommitting()
        {
            var p = new JourneyProgression(MakeJourney(("tree", 10f, "proximity")));

            // Half the dwell, then leave.
            Hold(p, 10f, 0.5f);
            Assert.AreEqual(BeatState.Approaching, p.StateOf("tree"),
                "the beat should be armed but not committed");

            Hold(p, 30f, 1f);
            Assert.AreNotEqual(BeatState.Complete, p.StateOf("tree"),
                "walking away before the dwell elapses must not complete the beat");
        }

        [Test]
        public void StandingOnTheBoundaryDoesNotThrash()
        {
            // The failure this whole design exists to prevent: a visitor loitering near the
            // edge of a zone while pose noise pushes them in and out.
            var p = new JourneyProgression(MakeJourney(("tree", 10f, "proximity")));
            var events = new List<JourneyProgression.Event>();

            // Oscillate across the 2.5 m enter boundary for ten seconds.
            for (int i = 0; i < 300; i++)
            {
                float s = 10f + (i % 2 == 0 ? 2.3f : 2.7f);
                p.Tick(At(s), LocalizationQuality.Precise, Dt);
                events.AddRange(p.DrainEvents());
            }

            int completions = events.Count(e => e.Kind == JourneyProgression.Event.Type.Completed);
            Assert.AreEqual(1, completions,
                $"the beat must complete exactly once, not once per boundary crossing (got {completions})");
        }

        [Test]
        public void MinimumHoldKeepsABeatOwningTheMachine()
        {
            var p = new JourneyProgression(MakeJourney(
                ("tree", 10f, "proximity"),
                ("redd", 18f, "proximity")));

            Hold(p, 10f, 2f);
            Assert.AreEqual(BeatState.Complete, p.StateOf("tree"));

            // Sprint to the next beat immediately. Minimum hold is 5 s, so the first beat
            // should still own the machine and the second must not fire yet.
            Hold(p, 18f, 1.5f);
            Assert.AreNotEqual(BeatState.Complete, p.StateOf("redd"),
                "the next beat must wait out the previous beat's minimum hold");

            Hold(p, 18f, 6f);
            Assert.AreEqual(BeatState.Complete, p.StateOf("redd"),
                "once the hold expires the next beat should fire normally");
        }

        [Test]
        public void APassedBeatNeverFiresAgainWhenBacktracking()
        {
            var p = new JourneyProgression(MakeJourney(
                ("tree", 10f, "proximity"),
                ("redd", 20f, "proximity")));

            Hold(p, 10f, 2f);
            Hold(p, 20f, 8f);
            Assert.AreEqual(BeatState.Complete, p.StateOf("redd"));

            // Walk back downstream over the first beat.
            var events = Hold(p, 10f, 10f);
            Assert.IsFalse(events.Any(e => e.Kind == JourneyProgression.Event.Type.Completed),
                "walking back over a finished beat must not replay it");
        }

        [Test]
        public void OrderIsSoftGatedSoWanderingAheadDoesNotDeadlock()
        {
            var p = new JourneyProgression(MakeJourney(
                ("tree", 10f, "proximity"),
                ("redd", 20f, "proximity"),
                ("heron", 30f, "proximity")));

            // Skip straight to the third beat.
            var events = Hold(p, 30f, 3f);
            Assert.IsTrue(events.Any(e => e.Kind == JourneyProgression.Event.Type.Blocked),
                "a beat two ahead of the high-water mark should report itself blocked");
            Assert.AreNotEqual(BeatState.Complete, p.StateOf("heron"));

            // Coming back to the proper first beat still works — no deadlock.
            Hold(p, 10f, 3f);
            Assert.AreEqual(BeatState.Complete, p.StateOf("tree"));
        }

        [Test]
        public void GestureBeatsWaitForTheGesture()
        {
            var p = new JourneyProgression(MakeJourney(("redd", 10f, "crouch")));

            Hold(p, 10f, 3f);
            Assert.AreEqual(BeatState.AwaitingAction, p.StateOf("redd"),
                "a crouch beat must not complete on arrival alone");
            Assert.IsTrue(p.AwaitingAction);

            Assert.IsFalse(p.SatisfyAction(InteractionKind.Lift),
                "the wrong gesture must not satisfy the beat");
            Assert.IsTrue(p.SatisfyAction(InteractionKind.Crouch));
            Assert.AreEqual(BeatState.Complete, p.StateOf("redd"));
        }

        [Test]
        public void AStrayGestureAwayFromAnyBeatIsSilent()
        {
            var p = new JourneyProgression(MakeJourney(("redd", 10f, "crouch")));
            Hold(p, 45f, 2f);
            Assert.IsFalse(p.SatisfyAction(InteractionKind.Crouch),
                "crouching in the middle of nowhere must do nothing");
        }

        [Test]
        public void GivingFishThinsTheShoalAndStopsAtTheFloor()
        {
            var doc = MakeJourney(("heron", 10f, "give"));
            doc.beats[0].givesFish = 12;
            var p = new JourneyProgression(doc);
            Assert.AreEqual(40, p.ShoalCount);

            Hold(p, 10f, 3f);
            p.SatisfyAction(InteractionKind.Give);
            Assert.AreEqual(28, p.ShoalCount, "the shoal should be 12 smaller");

            // A greedy authoring mistake must not empty the creek.
            var doc2 = MakeJourney(("heron", 10f, "give"));
            doc2.beats[0].givesFish = 500;
            var p2 = new JourneyProgression(doc2);
            Hold(p2, 10f, 3f);
            p2.SatisfyAction(InteractionKind.Give);
            Assert.AreEqual(6, p2.ShoalCount, "shoal count must not fall below the minimum");
        }

        [Test]
        public void LosingTrackingMidBeatHoldsRatherThanTearingDown()
        {
            var p = new JourneyProgression(MakeJourney(("redd", 10f, "crouch")));
            Hold(p, 10f, 3f);
            Assert.AreEqual(BeatState.AwaitingAction, p.StateOf("redd"));

            // Tracking drops out for two seconds.
            var lost = Hold(p, 10f, 2f, LocalizationQuality.Unavailable);
            Assert.IsEmpty(lost, "a tracking dropout should not raise events");
            Assert.AreEqual(BeatState.AwaitingAction, p.StateOf("redd"),
                "a brief loss must not abandon the beat the visitor is standing in");

            Assert.IsTrue(p.SatisfyAction(InteractionKind.Crouch),
                "the gesture should still land once tracking returns");
        }

        [Test]
        public void OperatorCanForceABeatFromAnywhere()
        {
            // The controller exists because automatic triggers fail in the field. Forcing must
            // work regardless of where the visitor actually is.
            var p = new JourneyProgression(MakeJourney(
                ("tree", 10f, "proximity"),
                ("redd", 20f, "crouch")));

            Hold(p, 50f, 1f);
            Assert.IsTrue(p.ForceBeat("redd"));
            Assert.AreEqual(BeatState.Complete, p.StateOf("redd"));
            Assert.Contains("redd", p.Completed.ToList());

            Assert.IsFalse(p.ForceBeat("no-such-beat"));
        }

        [Test]
        public void AWholeWalkUpstreamCompletesEveryBeatInOrder()
        {
            var doc = MakeJourney(
                ("tree", 5f, "proximity"),
                ("redd", 15f, "crouch"),
                ("strider", 25f, "catch"),
                ("heron", 35f, "give"),
                ("barrier", 45f, "lift"),
                ("headwater", 55f, "proximity"));

            var p = new JourneyProgression(doc);
            var order = new List<string>();

            // Walk at roughly 0.7 m/s, performing whatever gesture is asked for.
            for (float s = 0f; s <= 60f; s += 0.7f * Dt)
            {
                p.Tick(At(s), LocalizationQuality.Precise, Dt);
                foreach (var e in p.DrainEvents())
                {
                    if (e.Kind == JourneyProgression.Event.Type.Completed) order.Add(e.Beat.id);
                }
                if (p.AwaitingAction) p.SatisfyAction(p.Current.Kind);
            }

            CollectionAssert.AreEqual(
                new[] { "tree", "redd", "strider", "heron", "barrier", "headwater" }, order,
                "a straight walk upstream should complete all six beats in order");
            Assert.AreEqual(28, p.ShoalCount, "the heron should have taken 12 fish");
        }

        [Test]
        public void CentrelineProjectionIgnoresLateralOffset()
        {
            var centreline = new List<Vec3>
            {
                new() { x = 0, y = 0, z = 0 },
                new() { x = 0, y = 0, z = 60 },
            };

            // Three metres off to the side of the creek, level with s = 20.
            var projection = Centreline.Project(new Vector3(3f, 0f, 20f), centreline);
            Assert.AreEqual(20f, projection.S, 0.01f, "s should ignore cross-stream offset");
            Assert.AreEqual(3f, projection.Lateral, 0.01f);

            Assert.AreEqual(60f, Centreline.Length(centreline), 0.01f);
            Assert.AreEqual(new Vector3(0, 0, 42f), Centreline.PointAt(42f, centreline));
        }
    }
}
