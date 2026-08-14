using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using ShoalingUpstream.Control;
using ShoalingUpstream.Journey;
using UnityEngine;

namespace ShoalingUpstream.Tests.Control
{
    /// <summary>
    /// The seam the whole folder exists for: two ways of knowing where the visitor is, and
    /// nothing downstream able to tell them apart.
    ///
    /// The artist's requirement is that the walk simulated in the browser is the same walk the
    /// device performs — same beats, same sound, same order. If simulation ran down a separate
    /// path, the only place the difference would surface is standing in the creek.
    /// </summary>
    public class PoseSourceTests
    {
        private const float Dt = 1f / 30f;

        private static List<Vec3> StraightReach() => new()
        {
            new Vec3 { x = 0, y = 0, z = 0 },
            new Vec3 { x = 0, y = 0, z = 60 },
        };

        private static JourneyDocument MakeJourney(params (string id, float s)[] beats)
        {
            var doc = new JourneyDocument
            {
                schemaVersion = JourneyDocument.SupportedSchemaVersion,
                journeyId = "test",
                site = new SiteRef { centreline = StraightReach() },
                shoal = new Shoal { startingCount = 40, minimumCount = 6 },
            };
            foreach (var (id, s) in beats)
            {
                doc.beats.Add(new Beat
                {
                    id = id,
                    interaction = "proximity",
                    s = s,
                    position = new Vec3 { x = 0, y = 0, z = s },
                    trigger = new Trigger
                    {
                        enterRadiusM = 2.5f, exitRadiusM = 4f,
                        dwellSeconds = 1f, minimumHoldSeconds = 5f,
                        requiresPreviousComplete = true,
                    },
                });
            }
            return doc;
        }

        [Test]
        public void ASimulatedPoseCarryingOnlySIsCompletedAgainstTheCentreline()
        {
            // The editor's scrubber works in distance along the path and may send nothing else.
            var frame = new CentrelineFrame(StraightReach());
            var simulated = new SimulatedPoseSource(frame);

            simulated.Apply(new SimulatedPose(true, 24f, false, Vector3.zero, 1.2f), 1000);

            Assert.IsTrue(simulated.TryGetPose(1000, out var pose));
            Assert.AreEqual(24f, pose.S, 0.001f);
            Assert.AreEqual(24f, pose.AnchorLocalPosition.z, 0.001f,
                "downstream takes a 3D point, so the missing half is reconstructed here");
            Assert.AreEqual(0f, pose.AnchorLocalPosition.x, 0.001f);
            Assert.AreEqual(0f, pose.LateralM, 0.001f, "a scrubbed walk is on the line");
            Assert.AreEqual(1.2f, pose.HeadingRad, 0.001f);
            Assert.AreEqual(LocalizationQuality.Precise, pose.Quality,
                "the trigger machine gates on quality — anything less rehearses nothing");
        }

        [Test]
        public void AVpsPoseCarryingOnlyAPositionIsCompletedTheOtherWay()
        {
            var frame = new CentrelineFrame(StraightReach());
            var vps = new VpsPoseSource(frame);

            // Three metres off to the side, level with s = 24.
            vps.Submit(new Vector3(3f, 0f, 24f), 0.4f, LocalizationQuality.Precise, 1000);

            Assert.IsTrue(vps.TryGetPose(1000, out var pose));
            Assert.AreEqual(24f, pose.S, 0.001f);
            Assert.AreEqual(3f, pose.LateralM, 0.001f);
            Assert.AreEqual(PoseOrigin.Vps, pose.Origin);
        }

        [Test]
        public void NeitherSourceKeepsAPoseForeverWhenItStopsBeingFed()
        {
            var frame = new CentrelineFrame(StraightReach());
            var simulated = new SimulatedPoseSource(frame) { FreshnessMs = 2000 };
            var vps = new VpsPoseSource(frame) { FreshnessMs = 1500 };

            simulated.Apply(new SimulatedPose(true, 10f, false, Vector3.zero, 0f), 1000);
            vps.Submit(Vector3.zero, 0f, LocalizationQuality.Precise, 1000);

            Assert.IsTrue(simulated.IsLive(2900));
            Assert.IsFalse(simulated.IsLive(3100), "an operator who stopped scrubbing is not still scrubbing");
            Assert.IsTrue(vps.IsLive(2400));
            Assert.IsFalse(vps.IsLive(2600), "a stale VPS pose means the app stopped submitting, "
                + "and firing beats from it fires them at a phone in a pocket");
        }

        [Test]
        public void SimulationTakesOverWhileItIsLiveAndHandsBackWhenItStops()
        {
            var frame = new CentrelineFrame(StraightReach());
            var vps = new VpsPoseSource(frame);
            var simulated = new SimulatedPoseSource(frame) { FreshnessMs = 2000 };
            var poses = new PoseSourceSwitch(vps, simulated);

            vps.Submit(new Vector3(0, 0, 5f), 0f, LocalizationQuality.Precise, 1000);
            Assert.IsTrue(poses.TryGetPose(1000, out var onCreek));
            Assert.AreEqual(PoseOrigin.Vps, onCreek.Origin);

            // Somebody at the laptop starts dragging the scrubber.
            simulated.Apply(new SimulatedPose(true, 30f, false, Vector3.zero, 0f), 1100);
            Assert.IsTrue(poses.TryGetPose(1100, out var scrubbing));
            Assert.AreEqual(PoseOrigin.Simulated, scrubbing.Origin);
            Assert.AreEqual(30f, scrubbing.S, 0.001f);

            // They stop. Two seconds later the device is back on its own localization, with no
            // mode switch for anyone to get wrong under pressure.
            vps.Submit(new Vector3(0, 0, 6f), 0f, LocalizationQuality.Precise, 3200);
            Assert.IsTrue(poses.TryGetPose(3200, out var backOnCreek));
            Assert.AreEqual(PoseOrigin.Vps, backOnCreek.Origin);
            Assert.AreEqual(6f, backOnCreek.S, 0.001f);
        }

        [Test]
        public void AModeLockPinsTheSourceRegardlessOfWhatIsLive()
        {
            var frame = new CentrelineFrame(StraightReach());
            var vps = new VpsPoseSource(frame);
            var simulated = new SimulatedPoseSource(frame);
            var poses = new PoseSourceSwitch(vps, simulated);

            vps.Submit(new Vector3(0, 0, 5f), 0f, LocalizationQuality.Precise, 1000);
            simulated.Apply(new SimulatedPose(true, 30f, false, Vector3.zero, 0f), 1000);

            poses.Mode = PoseSourceMode.VpsOnly;
            Assert.IsTrue(poses.TryGetPose(1000, out var real));
            Assert.AreEqual(5f, real.S, 0.001f);

            poses.Mode = PoseSourceMode.SimulatedOnly;
            Assert.IsTrue(poses.TryGetPose(1000, out var fake));
            Assert.AreEqual(30f, fake.S, 0.001f);

            // In the Unity Editor there is no localization at all, and that has to read as
            // "no pose" rather than as a pose at the origin.
            var empty = new PoseSourceSwitch(new VpsPoseSource(frame), new SimulatedPoseSource(frame))
            {
                Mode = PoseSourceMode.VpsOnly,
            };
            Assert.IsFalse(empty.TryGetPose(1000, out _));
            Assert.AreEqual(PoseOrigin.None, empty.Origin);
        }

        [Test]
        public void TheTriggerMachineCannotTellWhichSourceWalkedIt()
        {
            // The same walk twice: once as VPS positions, once as scrubbed distances. If these
            // ever diverge, the piece rehearsed at a desk is not the piece performed at a creek.
            var viaVps = WalkWith(PoseOrigin.Vps);
            var viaSimulation = WalkWith(PoseOrigin.Simulated);

            CollectionAssert.AreEqual(new[] { "tree", "redd", "headwater" }, viaVps);
            CollectionAssert.AreEqual(viaVps, viaSimulation,
                "the same walk must complete the same beats in the same order whichever source drove it");
        }

        /// <summary>Walk 0-60 m at 0.7 m/s through the switch, returning the beats completed.</summary>
        private static List<string> WalkWith(PoseOrigin driver)
        {
            var doc = MakeJourney(("tree", 10f), ("redd", 25f), ("headwater", 45f));
            var frame = new CentrelineFrame(doc.site.centreline);
            var vps = new VpsPoseSource(frame);
            var simulated = new SimulatedPoseSource(frame);
            var poses = new PoseSourceSwitch(vps, simulated);
            var progression = new JourneyProgression(doc);

            var completed = new List<string>();
            double now = 1000;
            for (float s = 0f; s <= 60f; s += 0.7f * Dt)
            {
                if (driver == PoseOrigin.Vps)
                {
                    vps.Submit(new Vector3(0, 0, s), 0f, LocalizationQuality.Precise, now);
                }
                else
                {
                    simulated.Apply(new SimulatedPose(true, s, false, Vector3.zero, 0f), now);
                }

                if (poses.TryGetPose(now, out var pose))
                {
                    progression.Tick(pose.AnchorLocalPosition, pose.Quality, Dt);
                    completed.AddRange(progression.DrainEvents()
                        .Where(e => e.Kind == JourneyProgression.Event.Type.Completed)
                        .Select(e => e.Beat.id));
                }
                now += Dt * 1000.0;
            }
            return completed;
        }
    }
}
