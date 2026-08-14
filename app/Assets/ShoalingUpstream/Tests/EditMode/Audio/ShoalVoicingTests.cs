using System.Linq;
using NUnit.Framework;
using UnityEngine;
using ShoalingUpstream.Audio;
using ShoalingUpstream.Journey;

namespace ShoalingUpstream.Tests
{
    /// <summary>
    /// The shoal, which the visitor is.
    ///
    /// Nothing here checks a number on a screen, because there is no number on a screen: the whole
    /// point is that "there are fewer of us now" arrives through voices, width and individuation.
    /// What can be checked is that those three move in the right directions, at the right speeds,
    /// and that the level does not do the work for them.
    /// </summary>
    public class ShoalVoicingTests
    {
        private const float Dt = 1f / 30f;

        private static Shoal Garden => new() { startingCount = 40, minimumCount = 6 };

        private static ShoalVoicingState Settle(ShoalVoicing voicing, float seconds)
        {
            int steps = Mathf.CeilToInt(seconds / Dt);
            ShoalVoicingState state = voicing.State;
            for (int i = 0; i < steps; i++) state = voicing.Tick(Dt);
            return state;
        }

        [Test]
        public void TheBedLadderIsGeometricAcrossTheJourneysOwnRange()
        {
            // Geometric because auditory numerosity is Weber-like: six to twelve is the same
            // perceived change as twelve to twenty-four, so equal rungs have to be equal ratios.
            // Derived from the journey rather than hard-coded, so a site with a different shoal
            // gets a ladder that spans it.
            var beds = new ShoalVoicing(Garden).Beds;
            CollectionAssert.AreEqual(new[] { 6, 10, 15, 25, 40 }, beds.ToArray());

            var wide = new ShoalVoicing(new Shoal { startingCount = 200, minimumCount = 10 }).Beds;
            Assert.AreEqual(10, wide[0]);
            Assert.AreEqual(200, wide[wide.Count - 1]);
            for (int i = 1; i < wide.Count; i++)
                Assert.Greater(wide[i], wide[i - 1], "no rung may repeat: a bed crossfaded with itself is silence");
        }

        [Test]
        public void AFullShoalIsWideAndAThinnedOneIsNarrow()
        {
            var voicing = new ShoalVoicing(Garden);

            var full = voicing.State;
            Assert.AreEqual(ShoalVoicing.MaxVoices, full.Voices);
            Assert.AreEqual(ShoalVoicing.MaxWidthDeg, full.UnisonWidthDeg, 0.01f);
            Assert.AreEqual(1f, full.Density, 1e-4f);
            Assert.AreEqual(40, full.BedHigh);

            voicing.Reset(6);
            var floor = voicing.State;
            Assert.AreEqual(ShoalVoicing.MinVoices, floor.Voices);
            Assert.AreEqual(ShoalVoicing.MinWidthDeg, floor.UnisonWidthDeg, 0.01f);
            Assert.AreEqual(0f, floor.Density, 1e-4f);
            Assert.AreEqual(6, floor.BedLow);
        }

        [Test]
        public void FewerFishIsNeverMoreVoices()
        {
            var voicing = new ShoalVoicing(Garden);
            int previous = int.MaxValue;
            float previousWidth = float.MaxValue;

            for (int count = 40; count >= 6; count--)
            {
                voicing.Reset(count);
                var state = voicing.State;
                Assert.LessOrEqual(state.Voices, previous, $"voice count rose at {count} fish");
                Assert.LessOrEqual(state.UnisonWidthDeg, previousWidth + 1e-3f, $"width rose at {count} fish");
                previous = state.Voices;
                previousWidth = state.UnisonWidthDeg;
            }
        }

        [Test]
        public void IndividuationIsTheInverseOfDensity()
        {
            // The counter-intuitive cue, and the one that does the work: a smaller group is more
            // legible as individuals, so the decorrelation delay lengthens until single fish poke
            // out of the texture instead of the cloud simply getting quieter.
            var voicing = new ShoalVoicing(Garden);
            Assert.AreEqual(0f, voicing.State.Individuation, 1e-4f, "a full shoal is one fused cloud");

            voicing.Reset(6);
            Assert.AreEqual(1f, voicing.State.Individuation, 1e-4f);

            // The desk approximates it with a per-voice start offset, which has to stay under the
            // echo threshold or the copies stop fusing and become a slapback.
            Assert.Less(ShoalVoicing.DeskVoiceDelayMs(1f), 50f);
        }

        [Test]
        public void TheWholeShoalRangeIsThreeDecibels()
        {
            // If thinning were carried by level the visitor would hear a fader move and read it as
            // the piece getting quieter rather than as their shoal being taken.
            var voicing = new ShoalVoicing(Garden);
            float loudest = voicing.State.GainDb;
            voicing.Reset(6);
            float quietest = voicing.State.GainDb;

            Assert.AreEqual(3f, loudest - quietest, 0.01f);
        }

        [Test]
        public void TotalPowerIsConstantWhateverTheHeadcount()
        {
            // Same reason, one level down: the per-voice gains are normalised, so losing a voice
            // changes where the shoal is and not how loud it is.
            var voicing = new ShoalVoicing(Garden);
            var gains = new float[ShoalVoicing.MaxVoices];

            for (int count = 40; count >= 6; count -= 2)
            {
                voicing.Reset(count);
                ShoalVoicing.VoiceGains(voicing.State, gains);

                float power = gains.Sum(g => g * g);
                float expected = DistanceField.DbToLinear(voicing.State.GainDb);
                Assert.AreEqual(expected * expected, power, 1e-4f, $"at {count} fish");

                int sounding = gains.Count(g => g > 0f);
                Assert.AreEqual(voicing.State.Voices, sounding, $"at {count} fish");
            }
        }

        [Test]
        public void VoicesAreKeptSymmetricAndInnermostFirst()
        {
            var voicing = new ShoalVoicing(Garden);
            voicing.Reset(6);
            var gains = new float[ShoalVoicing.MaxVoices];
            ShoalVoicing.VoiceGains(voicing.State, gains);

            // Two voices left, and they must be the pair either side of straight ahead. A shoal
            // that thinned to its two outermost directions would read as a hole rather than a knot.
            Assert.Greater(gains[3], 0f);
            Assert.Greater(gains[4], 0f);
            Assert.AreEqual(gains[3], gains[4], 1e-5f, "and balanced, or the group leans");
            for (int i = 0; i < gains.Length; i++)
                if (i != 3 && i != 4) Assert.AreEqual(0f, gains[i], 1e-6f);
        }

        [Test]
        public void GivingFishDropsTheDensityFastAndSettlesTheGroupSlowly()
        {
            // Timing is the whole cue. Instant-and-permanent reads as a bug; slow-and-gradual
            // reads as unrelated to the heron. A hard drop followed by a long settle reads as loss.
            var voicing = new ShoalVoicing(Garden);
            float startDensity = voicing.State.Density;
            int startVoices = voicing.State.Voices;

            voicing.SetCount(28);
            float targetDensity = Mathf.Log(28f / 6f) / Mathf.Log(40f / 6f);

            var justAfter = Settle(voicing, 0.4f);
            float moved = (startDensity - justAfter.Density) / (startDensity - targetDensity);
            Assert.Greater(moved, 0.9f, "the density drop must be unmistakably caused by the donation");
            Assert.AreEqual(startVoices, justAfter.Voices,
                "but the group has not had time to reorganise yet");

            var settled = Settle(voicing, 15f);
            Assert.AreEqual(targetDensity, settled.Density, 0.01f);
            Assert.AreEqual(7, settled.Voices, "28 of 40 fish is seven voices");
            Assert.Less(settled.Voices, startVoices);
        }

        [Test]
        public void SpawningWidensRatherThanJolting()
        {
            // The reverse gesture is deliberately not the mirror of the loss: growth is a widening
            // over a couple of seconds, which is what teaches the mapping by contrast.
            var voicing = new ShoalVoicing(Garden);
            voicing.Reset(10);
            voicing.SetCount(40);

            var earlyState = Settle(voicing, 0.4f);
            Assert.Less(earlyState.Density, 0.65f, "growth must not snap the way loss does");

            var grown = Settle(voicing, 6f);
            Assert.AreEqual(1f, grown.Density, 0.02f);
            Assert.AreEqual(ShoalVoicing.MaxVoices, grown.Voices);
        }

        [Test]
        public void TheShoalCannotBeEmptiedOrOverfilled()
        {
            var voicing = new ShoalVoicing(Garden);

            voicing.SetCount(0);
            Assert.AreEqual(6, voicing.Count, "a greedy authoring mistake must not empty the creek");

            voicing.SetCount(500);
            Assert.AreEqual(40, voicing.Count);
        }

        [Test]
        public void BedsBracketTheCurrentDensityAndCrossfadeBetweenNeighbours()
        {
            var voicing = new ShoalVoicing(Garden);
            voicing.Reset(15);
            var state = voicing.State;

            Assert.LessOrEqual(state.BedLow, 15);
            Assert.GreaterOrEqual(state.BedHigh, 15);
            Assert.LessOrEqual(state.BedBlend, 1f);
            Assert.GreaterOrEqual(state.BedBlend, 0f);
            Assert.AreEqual("shoal--15", ShoalVoicing.BedClipId(15));
        }

        [Test]
        public void AJourneyThatCannotThinStillProducesAValidVoicing()
        {
            // Degenerate configuration: no room between the floor and the start. It must not
            // divide by zero, and it must sound like a full shoal rather than an empty one.
            var voicing = new ShoalVoicing(new Shoal { startingCount = 8, minimumCount = 8 });
            Assert.AreEqual(1f, voicing.State.Density, 1e-4f);
            Assert.AreEqual(ShoalVoicing.MaxVoices, voicing.State.Voices);
            Assert.AreEqual(8, voicing.State.BedLow);
        }
    }
}
