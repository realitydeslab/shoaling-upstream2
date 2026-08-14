using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using ShoalingUpstream.Audio;
using ShoalingUpstream.Journey;

namespace ShoalingUpstream.Tests
{
    /// <summary>
    /// The engine, driven by a synthetic walk.
    ///
    /// PHASE does not exist in the editor and Unity's audio thread is not worth waiting on, so
    /// everything here runs against the headless backend — which is exactly the seam the design
    /// exists to provide. What is asserted is the sound design: that approaching a beat swaps one
    /// recording for another rather than turning a fader up, that a confirmation fires once, and
    /// that being out of the water lasts as long as the visitor stays up.
    /// </summary>
    public class SpatialAudioEngineTests
    {
        private const float Dt = 1f / 30f;

        /// <summary>Resolves anything. The engine's job is deciding what to play, not finding it.</summary>
        private class AnyClipResolver : IAudioClipResolver
        {
            private readonly HashSet<string> _refuse;
            public AnyClipResolver(params string[] refuse) => _refuse = new HashSet<string>(refuse);

            public bool TryResolve(string clipId, out ResolvedClip clip)
            {
                clip = _refuse.Contains(clipId)
                    ? ResolvedClip.None
                    : new ResolvedClip(clipId, filePath: $"/dev/null/{clipId}.mp3");
                return clip.IsValid;
            }
        }

        private static JourneyDocument MakeJourney(params (string id, Vector3 at, string completion)[] beats)
        {
            var doc = new JourneyDocument
            {
                schemaVersion = JourneyDocument.SupportedSchemaVersion,
                journeyId = "audio-test",
                site = new SiteRef
                {
                    centreline = new List<Vec3> { new() { x = 0, y = 0, z = 0 }, new() { x = 0, y = 0, z = 60 } },
                },
                shoal = new Shoal { startingCount = 40, minimumCount = 6 },
            };

            foreach (var (id, at, completion) in beats)
            {
                doc.beats.Add(new Beat
                {
                    id = id,
                    interaction = "proximity",
                    position = Vec3.From(at),
                    s = at.z,
                    // The shipped garden gate, so reach and half-life match the editor's rings.
                    trigger = new Trigger { enterRadiusM = 1.9f, exitRadiusM = 3.04f },
                    audio = new BeatAudio
                    {
                        far = new AudioLayer { clipId = $"{id}--far", gainDb = -14f },
                        mid = new AudioLayer { clipId = $"{id}--mid", gainDb = -8f },
                        intimate = new AudioLayer { clipId = $"{id}--intimate", gainDb = -4f },
                        completion = completion == null
                            ? null
                            : new AudioLayer { clipId = completion, gainDb = -3f, loop = false },
                    },
                });
            }
            return doc;
        }

        private static (SpatialAudioEngine engine, HeadlessAudioBackend backend) Build(
            JourneyDocument journey, IAudioClipResolver resolver = null, AudioEngineSettings settings = null)
        {
            var backend = new HeadlessAudioBackend();
            var engine = new SpatialAudioEngine(journey, backend, resolver ?? new AnyClipResolver(), settings);
            return (engine, backend);
        }

        private static void Hold(SpatialAudioEngine engine, Vector3 at, float seconds)
        {
            int steps = Mathf.CeilToInt(seconds / Dt);
            for (int i = 0; i < steps; i++) engine.Tick(at, Quaternion.identity, Dt);
        }

        [Test]
        public void EveryBeatBecomesASpatialisedSourceWithItsThreeRecordings()
        {
            var (engine, backend) = Build(MakeJourney(("tree", new Vector3(0, 0, 10), null)));

            Assert.AreEqual(1, backend.Sources.Count);
            var recorded = backend.Find("tree");
            CollectionAssert.AreEqual(
                new[] { "tree--far", "tree--mid", "tree--intimate" }, recorded.ClipIds);
            Assert.AreEqual(new Vector3(0, 0, 10), recorded.Position);
            Assert.IsEmpty(engine.MissingClips);
        }

        [Test]
        public void ApproachingSwapsTheRecordingRatherThanRaisingTheFader()
        {
            var journey = MakeJourney(("tree", Vector3.zero, null));
            var (engine, _) = Build(journey);
            float reach = DistanceField.ReachFor(journey.beats[0]);

            Hold(engine, new Vector3(0, 0, reach), 3f);
            engine.TryGetSource("tree", out var away);
            Assert.Greater(away.Gains.Far, away.Gains.Intimate, "at the edge you hear the far take");

            Hold(engine, Vector3.zero, 3f);
            engine.TryGetSource("tree", out var arrived);
            Assert.Greater(arrived.Gains.Intimate, arrived.Gains.Far, "on top of it you hear the intimate one");

            // And the thing that must NOT happen: the swap must not be an artefact of one
            // recording simply being turned up. Strip the distance rolloff and the crossfade's own
            // level moves by less than the twelve decibels inverse-square would spend over two
            // doublings — so the identity of what is playing changed, and its loudness barely did.
            float reachDb = DistanceField.LinearToDb(
                DistanceField.ContentGainAt(reach, reach, LayerGainsDb.From(journey.beats[0].audio)));
            float arrivedDb = DistanceField.LinearToDb(
                DistanceField.ContentGainAt(0f, reach, LayerGainsDb.From(journey.beats[0].audio)));

            Assert.Less(arrivedDb - reachDb, DistanceField.LevelBudgetDb,
                $"the crossfade spent {arrivedDb - reachDb:F1} dB, which is distance carried by gain");
        }

        [Test]
        public void GainsStayInsideTheBudgetForEveryStepOfAWalk()
        {
            var journey = MakeJourney(("tree", new Vector3(0, 0, 10), null), ("redd", new Vector3(0, 0, 20), null));
            var (engine, _) = Build(journey);
            float ceiling = DistanceField.DbToLinear(-4f);

            for (float z = 0f; z <= 30f; z += 0.7f * Dt)
            {
                engine.Tick(new Vector3(0, 0, z), Quaternion.identity, Dt);
                foreach (var source in engine.Sources)
                {
                    Assert.LessOrEqual(source.Gains.Sum, ceiling + 1e-4f,
                        $"{source.Id} summed above its loudest layer at z={z:F1}");
                    Assert.GreaterOrEqual(source.Gains.Far, 0f);
                }
            }
        }

        [Test]
        public void ASourceOutOfEarshotIsStoppedAndOneInEarshotIsStarted()
        {
            var journey = MakeJourney(("tree", Vector3.zero, null));
            var (engine, backend) = Build(journey);
            float reach = DistanceField.ReachFor(journey.beats[0]);

            Hold(engine, Vector3.zero, 2f);
            Assert.IsTrue(backend.Find("tree").Active);

            Hold(engine, new Vector3(0, 0, reach * 2f), 4f);
            Assert.IsFalse(backend.Find("tree").Active, "past the cull the players should be stopped");
            Assert.Less(backend.Find("tree").Gains.Sum, 1e-3f, "and silent before they are");
        }

        [Test]
        public void GainsAreSmoothedRatherThanSnappedToTheCurrentDistance()
        {
            // Pose jitter is continuous and unavoidable. Pushing raw per-frame distances would put
            // it straight onto the gains, and the ear is very good at hearing a level that is
            // trembling.
            var journey = MakeJourney(("tree", Vector3.zero, null));
            var (engine, _) = Build(journey);

            engine.Tick(Vector3.zero, Quaternion.identity, Dt);
            engine.TryGetSource("tree", out var afterOneFrame);

            float instant = DistanceField.GainsAt(0f, DistanceField.ReachFor(journey.beats[0]),
                LayerGainsDb.From(journey.beats[0].audio)).Intimate;

            Assert.Less(afterOneFrame.Gains.Intimate, instant,
                "one frame must not arrive at the target level");
            Assert.Greater(afterOneFrame.Gains.Intimate, 0f, "but it must be on its way");

            Hold(engine, Vector3.zero, 2f);
            engine.TryGetSource("tree", out var settled);
            Assert.AreEqual(instant, settled.Gains.Intimate, 1e-3f, "and it must get there");
        }

        [Test]
        public void CompletingABeatFiresOneConfirmationHeadRelative()
        {
            var journey = MakeJourney(("redd", new Vector3(0, 0, 10), "lay-egg"));
            var (engine, backend) = Build(journey);

            Assert.IsTrue(engine.FireCompletion(journey.beats[0]));
            Assert.AreEqual(1, backend.OneShots.Count);
            Assert.AreEqual("lay-egg", backend.OneShots[0].ClipId);
            Assert.AreEqual(DistanceField.DbToLinear(-3f), backend.OneShots[0].Gain, 1e-5f);
        }

        [Test]
        public void ABeatWithNoConfirmationClipIsSilentRatherThanBroken()
        {
            var journey = MakeJourney(("tree", new Vector3(0, 0, 10), null));
            var (engine, backend) = Build(journey);

            Assert.IsFalse(engine.FireCompletion(journey.beats[0]));
            Assert.IsFalse(engine.FireCompletion(null));
            Assert.IsEmpty(backend.OneShots);
        }

        [Test]
        public void AMissingClipIsReportedAndNotSubstituted()
        {
            // Three layer names are one suffix apart. A resolver that guessed would play the
            // intimate recording from twenty metres and nobody would know why it felt wrong.
            var journey = MakeJourney(("tree", Vector3.zero, null));
            var (engine, backend) = Build(journey, new AnyClipResolver("tree--mid"));

            CollectionAssert.AreEqual(new[] { "tree--mid" }, engine.MissingClips.ToArray());
            Assert.IsNull(backend.Find("tree").ClipIds[(int)LayerSlot.Mid]);
            Assert.AreEqual("tree--far", backend.Find("tree").ClipIds[(int)LayerSlot.Far]);
        }

        // --- the barrier -------------------------------------------------------

        [Test]
        public void BeingOutOfTheWaterIsHeldForAsLongAsTheVisitorStaysUp()
        {
            // The interaction is a 40 cm lift, sustained and holdable, not a jump. A one-shot
            // would end while the visitor was still standing on the barrier.
            var (engine, backend) = Build(MakeJourney(("falls", Vector3.zero, "jump")));

            Hold(engine, Vector3.zero, 1f);
            Assert.AreEqual(0f, engine.AirBlend, 0.01f);
            Assert.AreEqual(MediumEnvelope.WaterCutoffHz, backend.LowPassHz, 1f);

            engine.SetOutOfWater(true);
            Hold(engine, Vector3.zero, 0.5f);
            Assert.AreEqual(1f, engine.AirBlend, 0.01f, "surfacing should be quick");

            // Thirty seconds up a rock. Nothing may decay, retrigger or time out.
            Hold(engine, Vector3.zero, 30f);
            Assert.AreEqual(1f, engine.AirBlend, 0.001f);
            Assert.AreEqual(MediumEnvelope.AirCutoffHz, backend.LowPassHz, 1f, "the field stays bright");
            Assert.AreEqual(0f, backend.ReverbSend, 0.001f, "and stays dry");
            Assert.IsEmpty(backend.OneShots, "holding must not fire anything");
        }

        [Test]
        public void ComingBackDownSplashesExactlyOnceAndReturnsTheField()
        {
            var settings = new AudioEngineSettings { ReentrySplashClipId = "jump" };
            var (engine, backend) = Build(MakeJourney(("falls", Vector3.zero, null)), settings: settings);

            engine.SetOutOfWater(true);
            Hold(engine, Vector3.zero, 5f);
            Assert.IsEmpty(backend.OneShots);

            engine.SetOutOfWater(false);
            Hold(engine, Vector3.zero, 5f);

            Assert.AreEqual(1, backend.OneShots.Count(o => o.ClipId == "jump"),
                "one landing, one splash");
            Assert.AreEqual(0f, engine.AirBlend, 0.01f);
            Assert.AreEqual(MediumEnvelope.WaterCutoffHz, backend.LowPassHz, 1f);
        }

        [Test]
        public void HoldingTheLiftStateIsIdempotent()
        {
            // Driven every frame from the gesture detector's held flag, so re-asserting the same
            // state must not re-raise the transition.
            var settings = new AudioEngineSettings { ReentrySplashClipId = "jump" };
            var (engine, backend) = Build(MakeJourney(("falls", Vector3.zero, null)), settings: settings);

            for (int i = 0; i < 300; i++)
            {
                engine.SetOutOfWater(true);
                engine.Tick(Vector3.zero, Quaternion.identity, Dt);
            }
            for (int i = 0; i < 300; i++)
            {
                engine.SetOutOfWater(false);
                engine.Tick(Vector3.zero, Quaternion.identity, Dt);
            }

            Assert.AreEqual(1, backend.OneShots.Count, "ten seconds of the same flag is one landing");
        }

        [Test]
        public void TheMediumSweepIsPerceptuallyEvenRatherThanLinear()
        {
            // Halfway between 1.4 kHz and 18 kHz is 5 kHz. A linear sweep would spend almost all
            // its travel in the top octave, where nothing is audible.
            float midpoint = MediumEnvelope.LogLerp(
                MediumEnvelope.WaterCutoffHz, MediumEnvelope.AirCutoffHz, 0.5f);

            Assert.AreEqual(Mathf.Sqrt(MediumEnvelope.WaterCutoffHz * MediumEnvelope.AirCutoffHz),
                midpoint, 1f);
            Assert.Less(midpoint, (MediumEnvelope.WaterCutoffHz + MediumEnvelope.AirCutoffHz) * 0.5f);
        }

        // --- the shoal ---------------------------------------------------------

        [Test]
        public void GivingFishReachesTheBackendAsVoicesAndNotAsANumber()
        {
            var (engine, backend) = Build(MakeJourney(("heron", Vector3.zero, null)));

            Hold(engine, Vector3.zero, 1f);
            int before = backend.Shoal.Voices;

            engine.SetShoalCount(28);
            Hold(engine, Vector3.zero, 15f);

            Assert.Less(backend.Shoal.Voices, before);
            Assert.Less(backend.Shoal.UnisonWidthDeg, ShoalVoicing.MaxWidthDeg);
            Assert.Greater(backend.Shoal.Individuation, 0f, "and the survivors individuate");
            Assert.AreEqual(28, engine.ShoalCount);
        }

        [Test]
        public void TheShoalBedPairIsOnlyReboundWhenItActuallyChanges()
        {
            // Rebinding is what rebuilds the native sound events, and rebuilding restarts the bed
            // from its first sample. Doing that per frame would make the shoal stutter constantly.
            var (engine, backend) = Build(MakeJourney(("heron", Vector3.zero, null)));

            Hold(engine, Vector3.zero, 2f);
            int changes = backend.ShoalBedChanges;
            Hold(engine, Vector3.zero, 5f);
            Assert.AreEqual(changes, backend.ShoalBedChanges, "a steady shoal must not rebind");

            engine.SetShoalCount(10);
            Hold(engine, Vector3.zero, 5f);
            Assert.Greater(backend.ShoalBedChanges, changes);
            // Ten fish sits between the 10 and 15 rungs, so the pair brackets it rather than
            // snapping to the nearest bed — shrinkage is continuous, and the commonest structural
            // error in this kind of system is implementing something continuous as buckets.
            Assert.AreEqual("shoal--10", backend.ShoalBedLowClipId);
            Assert.AreEqual("shoal--15", backend.ShoalBedHighClipId);
        }

        [Test]
        public void TheListenerFollowsThePhone()
        {
            var (engine, backend) = Build(MakeJourney(("tree", Vector3.zero, null)));
            // Written out rather than built with Quaternion.Euler: the orientation only has to be
            // something other than identity, and the components keep this a pure-maths test.
            var pose = new Quaternion(0f, 0.342f, 0f, 0.940f);

            engine.Tick(new Vector3(1f, 1.4f, 7f), pose, Dt);
            Assert.AreEqual(new Vector3(1f, 1.4f, 7f), backend.ListenerPosition);
            Assert.AreEqual(pose, backend.ListenerRotation);
        }

        [Test]
        public void AmbientBedsGetTheirOwnReachFromTheirAudibleRadius()
        {
            var journey = MakeJourney(("tree", Vector3.zero, null));
            journey.ambient.Add(new AmbientSource
            {
                id = "creek-bed",
                position = new Vec3 { x = 0, y = 0, z = 0 },
                audibleRadiusM = 40f,
                audio = new BeatAudio
                {
                    far = new AudioLayer { clipId = "creek--far", gainDb = -20f },
                    mid = new AudioLayer { clipId = "creek--mid", gainDb = -17f },
                    intimate = new AudioLayer { clipId = "creek--intimate", gainDb = -15f },
                },
            });

            var (engine, _) = Build(journey);
            Assert.IsTrue(engine.TryGetSource("creek-bed", out var bed));
            Assert.AreEqual(40f, bed.Reach, 0.01f);

            // Twenty-five metres out, the beat has been culled and the creek is still there.
            Hold(engine, new Vector3(0, 0, 25f), 3f);
            engine.TryGetSource("tree", out var beat);
            Assert.Less(beat.Gains.Sum, 1e-3f);
            Assert.Greater(bed.Gains.Sum, 1e-3f);
        }
    }
}
