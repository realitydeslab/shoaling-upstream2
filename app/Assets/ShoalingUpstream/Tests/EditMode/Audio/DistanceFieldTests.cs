using NUnit.Framework;
using ShoalingUpstream.Audio;
using ShoalingUpstream.Journey;

namespace ShoalingUpstream.Tests
{
    /// <summary>
    /// The crossfade law, pinned against the editor.
    ///
    /// The numbers here are not invented for the test: they are what
    /// <c>editor/src/audition.ts</c> produces for the fixture in <c>test/helpers.ts</c>, and the
    /// same ones <c>test/audible-field.test.ts</c> asserts on the other side. If the two ever
    /// disagree, the audible-field rings an author composed against in the browser stop describing
    /// what a visitor will hear at the creek, and nothing in either codebase would say so.
    /// </summary>
    public class DistanceFieldTests
    {
        // test/helpers.ts makeBeat(): a 3.04 m exit radius and layers at -14 / -8 / -4 dB.
        private const float ExitRadiusM = 3.04f;
        private const float EnterRadiusM = 1.9f;
        private static readonly LayerGainsDb GardenLayers = new(-14f, -8f, -4f);

        private static float GardenReach => DistanceField.ReachFor(
            new Trigger { enterRadiusM = EnterRadiusM, exitRadiusM = ExitRadiusM }, 0f);

        [Test]
        public void ReachComesFromTheGateGeometryUnlessOverridden()
        {
            Assert.AreEqual(10.64f, GardenReach, 0.001f, "exit radius x 3.5");

            Assert.AreEqual(25f, DistanceField.ReachFor(null, 25f),
                "an explicit audibleRadiusM wins outright");

            Assert.AreEqual(21f, DistanceField.ReachFor((Trigger)null, 0f),
                "a source with neither falls back to a 6 m gate");
        }

        [Test]
        public void TheThreeRecordingsCoverTheWholeRangeWithNoGap()
        {
            float reach = GardenReach;
            for (float n = 0f; n <= 1.0001f; n += 0.01f)
            {
                var w = DistanceField.WeightsAt(n * reach, reach);
                Assert.Greater(w.Sum, 0.05f,
                    $"at n={n:F2} every recording had faded out, which would be a hole in the field");
            }
        }

        [Test]
        public void LayerWeightsMatchTheEditorAtEveryBreakpoint()
        {
            float reach = GardenReach;

            void At(float n, float far, float mid, float intimate)
            {
                var w = DistanceField.WeightsAt(n * reach, reach);
                Assert.AreEqual(far, w.Far, 1e-4f, $"far at n={n}");
                Assert.AreEqual(mid, w.Mid, 1e-4f, $"mid at n={n}");
                Assert.AreEqual(intimate, w.Intimate, 1e-4f, $"intimate at n={n}");
            }

            At(0.00f, 0f, 0f, 1f);
            At(0.20f, 0f, 0.142857f, 0.428571f);
            At(0.35f, 0f, 0.571429f, 0f);
            At(0.50f, 0.125f, 1f, 0f);
            At(0.65f, 0.5f, 0.571429f, 0f);
            At(0.85f, 1f, 0f, 0f);
            At(1.00f, 1f, 0f, 0f);

            // Past the reach the normalised distance saturates, so far holds rather than
            // over-shooting into a weight above one.
            At(1.40f, 1f, 0f, 0f);
        }

        [Test]
        public void ApproachingTradesFarForIntimate()
        {
            float reach = GardenReach;
            var away = DistanceField.WeightsAt(reach, reach);
            var arrived = DistanceField.WeightsAt(0f, reach);

            Assert.AreEqual(1f, away.Far, 1e-4f);
            Assert.AreEqual(0f, away.Intimate, 1e-4f);
            Assert.AreEqual(0f, arrived.Far, 1e-4f);
            Assert.AreEqual(1f, arrived.Intimate, 1e-4f);
        }

        [Test]
        public void FarRisesAndIntimateFallsMonotonicallyWithDistance()
        {
            // The two ends of the crossfade must each be one-directional. Only the mid layer has
            // a peak in the middle, and only it is allowed one.
            float reach = GardenReach;
            float previousFar = -1f, previousIntimate = float.MaxValue;

            for (float d = 0f; d <= reach; d += reach / 200f)
            {
                var w = DistanceField.WeightsAt(d, reach);
                Assert.GreaterOrEqual(w.Far, previousFar - 1e-6f, $"far fell back at {d:F2} m");
                Assert.LessOrEqual(w.Intimate, previousIntimate + 1e-6f, $"intimate rose at {d:F2} m");
                previousFar = w.Far;
                previousIntimate = w.Intimate;
            }
        }

        [Test]
        public void DistanceIsCarriedByContentAndNotByLevel()
        {
            // The central claim of the whole sound design, asserted rather than asserted-in-prose.
            // The crossfade's own level moves by ten decibels from arrival to the edge of the
            // reach — the spread the layers were authored at — which is inside the twelve the
            // inverse-square law would spend over two doublings of distance.
            float reach = GardenReach;
            float loudest = float.MinValue, quietest = float.MaxValue;

            for (float d = 0f; d <= reach; d += reach / 400f)
            {
                float g = DistanceField.ContentGainAt(d, reach, GardenLayers);
                loudest = System.Math.Max(loudest, g);
                quietest = System.Math.Min(quietest, g);
            }

            float swingDb = DistanceField.LinearToDb(loudest) - DistanceField.LinearToDb(quietest);
            Assert.LessOrEqual(swingDb, DistanceField.LevelBudgetDb,
                $"the crossfade spent {swingDb:F1} dB, which is distance carried by the fader");
            Assert.AreEqual(10f, swingDb, 0.2f, "and it should be exactly the authored layer spread");
        }

        [Test]
        public void SummingThreeRecordingsNeverExceedsTheLoudestOfThem()
        {
            // The bands overlap, so weights sum above one around n=0.5. That is fine as long as
            // the summed level still sits under the loudest single layer — otherwise the mid
            // crossover would be the loudest point in the field, which is nonsense for a source
            // the visitor is walking towards.
            float reach = GardenReach;
            float ceiling = DistanceField.DbToLinear(-4f);

            for (float d = 0f; d <= reach * DistanceField.CullFactor; d += 0.02f)
            {
                float g = DistanceField.ContentGainAt(d, reach, GardenLayers);
                Assert.LessOrEqual(g, ceiling + 1e-5f, $"summed to {DistanceField.LinearToDb(g):F2} dB at {d:F2} m");
                Assert.LessOrEqual(g, 1f, "and must never reach unity, which would be no headroom at all");
            }
        }

        [Test]
        public void TheAudibleFieldMatchesTheEditorNumerically()
        {
            var field = DistanceField.Field(GardenReach, GardenLayers);

            Assert.IsTrue(field.HasField);
            Assert.AreEqual(10.64f, field.Reach, 0.001f);
            Assert.AreEqual(17.024f, field.MaxDistance, 0.001f);
            Assert.AreEqual(0.63095734f, field.Peak, 1e-5f, "peak is the intimate layer at -4 dB");
            Assert.AreEqual(0f, field.PeakAt, 1e-6f);
            Assert.AreEqual(1.30f, field.Half, 0.001f, "the editor draws a 1.30 m ring for this beat");
        }

        [Test]
        public void TheHalfLifeIsTighterThanTheTriggerRadius()
        {
            // The finding the ring exists to show, and the reason it is worth keeping the two
            // implementations in step: with the shipped gates the sound has already halved well
            // inside the radius at which the interaction arms.
            var field = DistanceField.Field(GardenReach, GardenLayers);
            Assert.Less(field.Half, EnterRadiusM);
        }

        [Test]
        public void AmplitudeAtTheHalfLifeReallyIsHalfThePeak()
        {
            var field = DistanceField.Field(GardenReach, GardenLayers);
            float atHalf = DistanceField.AmplitudeAt(field.Half, field.Reach, GardenLayers);
            Assert.AreEqual(field.Peak / 2f, atHalf, field.Peak * 0.06f);
        }

        [Test]
        public void AmplitudeFallsMonotonicallyFromThePeakToTheHalfLife()
        {
            var field = DistanceField.Field(GardenReach, GardenLayers);
            float previous = float.MaxValue;

            for (float d = field.PeakAt; d <= field.Half; d += 0.01f)
            {
                float a = DistanceField.AmplitudeAt(d, field.Reach, GardenLayers);
                Assert.LessOrEqual(a, previous + 1e-6f, $"amplitude rose at {d:F2} m");
                previous = a;
            }
        }

        [Test]
        public void ThereIsASecondLobeWhereTheMidRecordingArrives()
        {
            // Deliberately asserted rather than tolerated. The half-life is defined as the FIRST
            // crossing precisely because the summed field rises again around n=0.5, and a future
            // reader who "fixed" the non-monotonicity would move every ring in the editor.
            float reach = GardenReach;
            float atDip = DistanceField.AmplitudeAt(0.35f * reach, reach, GardenLayers);
            float atLobe = DistanceField.AmplitudeAt(0.50f * reach, reach, GardenLayers);
            Assert.Greater(atLobe, atDip, "the mid layer fading in should raise the summed field again");
        }

        [Test]
        public void TheCullBoundaryIsAlreadyInaudible()
        {
            // Sources are stopped at 1.35x the reach. That has to be somewhere the law has already
            // arrived at silence, or the stop is a click.
            float reach = GardenReach;
            float atCull = DistanceField.AmplitudeAt(reach * DistanceField.CullFactor, reach, GardenLayers);
            Assert.Less(DistanceField.LinearToDb(atCull), -45f);

            Assert.AreEqual(0f, DistanceField.AmplitudeAt(
                DistanceField.MaxDistanceFor(reach) + 1f, reach, GardenLayers),
                "nothing is audible past the maximum distance");
        }

        [Test]
        public void RaisingEveryLayerEquallyMovesThePeakButNotTheHalfLife()
        {
            // A half-life is a ratio against a source's own peak, which is the point of using one:
            // it describes the shape of the field rather than how loud somebody set it.
            var quiet = DistanceField.Field(GardenReach, GardenLayers);
            var loud = DistanceField.Field(GardenReach, new LayerGainsDb(-8f, -2f, 2f));

            Assert.Greater(loud.Peak, quiet.Peak);
            Assert.AreEqual(quiet.Half, loud.Half, 0.001f);
        }

        [Test]
        public void AWiderGateWidensTheFieldAndSlowsTheHalving()
        {
            var narrow = DistanceField.Field(GardenReach, GardenLayers);
            var wide = DistanceField.Field(DistanceField.ReachFor(new Trigger { exitRadiusM = 10f }, 0f),
                                           GardenLayers);

            Assert.Greater(wide.Reach, narrow.Reach);
            Assert.Greater(wide.Half, narrow.Half);
        }

        [Test]
        public void ASourceWithNoRecordingsHasNoFieldAtAll()
        {
            var silent = DistanceField.Field(GardenReach, LayerGainsDb.From(new BeatAudio()));
            Assert.IsFalse(silent.HasField);
        }

        [Test]
        public void AMissingLayerIsSkippedRatherThanPlayedAtItsDefault()
        {
            // A clipless layer carries gainDb = -8 in the schema. Treating that as a level rather
            // than as an absence would put a phantom recording into every incomplete beat.
            var audio = new BeatAudio { intimate = new AudioLayer { clipId = "x--intimate", gainDb = -4f } };
            var db = LayerGainsDb.From(audio);

            Assert.IsFalse(db.HasFar);
            Assert.IsFalse(db.HasMid);
            Assert.IsTrue(db.HasIntimate);

            float reach = GardenReach;
            Assert.AreEqual(0f, DistanceField.ContentGainAt(reach, reach, db), 1e-6f,
                "at the edge of the reach only far would be sounding, and there is no far");
        }
    }
}
