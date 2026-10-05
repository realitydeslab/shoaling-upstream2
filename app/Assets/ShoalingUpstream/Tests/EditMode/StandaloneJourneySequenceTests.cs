using System.Linq;
using NUnit.Framework;
using ShoalingUpstream.Experience;

namespace ShoalingUpstream.Tests
{
    public sealed class StandaloneJourneySequenceTests
    {
        private static void Finish(StandaloneJourneySequence sequence)
        {
            Assert.That(sequence.TryBegin(), Is.True);
            Assert.That(sequence.CompletePlayback(), Is.True);
        }

        [Test]
        public void GrowingNeedsBothFoundAndGreetingConfirmationsInOrder()
        {
            var sequence = new StandaloneJourneySequence();
            Finish(sequence); Finish(sequence);
            Assert.That(sequence.Current.BeatId, Is.EqualTo("beat-3"));
            Assert.That(sequence.TryBegin(), Is.False);
            Assert.That(sequence.ConfirmGreeting(StandaloneJourneySequence.Shade), Is.False);
            Assert.That(sequence.ConfirmFound(StandaloneJourneySequence.Plants), Is.False);
            Assert.That(sequence.ConfirmFound(StandaloneJourneySequence.Shade), Is.True);
            Assert.That(sequence.ConfirmFound(StandaloneJourneySequence.Shade), Is.False);
            Assert.That(sequence.ConfirmGreeting(StandaloneJourneySequence.Shade), Is.True);
            Assert.That(sequence.TryBegin(), Is.False);
            Assert.That(sequence.ConfirmGreeting(StandaloneJourneySequence.Plants), Is.False);
            Assert.That(sequence.ConfirmFound(StandaloneJourneySequence.Plants), Is.True);
            Assert.That(sequence.TryBegin(), Is.False);
            Assert.That(sequence.ConfirmGreeting(StandaloneJourneySequence.Plants), Is.True);
            Assert.That(sequence.TryBegin(), Is.True);
            Assert.That(sequence.TryBegin(), Is.False);
            Assert.That(sequence.ConfirmGreeting(StandaloneJourneySequence.Plants), Is.False);
            Assert.That(sequence.CompletePlayback(), Is.True);
            Assert.That(sequence.Current.BeatId, Is.EqualTo("beat-4"));
            Assert.That(sequence.Current.AutoPlay, Is.True);
        }

        [Test]
        public void EntireJourneyKeepsTheExistingAuthoredOrderAndCannotFinishTwice()
        {
            var sequence = new StandaloneJourneySequence();
            int completed = 0;
            while (sequence.Current != null)
            {
                if (sequence.Current.RequiresTwoGreetings)
                {
                    sequence.ConfirmFound(StandaloneJourneySequence.Shade);
                    sequence.ConfirmGreeting(StandaloneJourneySequence.Shade);
                    sequence.ConfirmFound(StandaloneJourneySequence.Plants);
                    sequence.ConfirmGreeting(StandaloneJourneySequence.Plants);
                }
                Assert.That(sequence.CompletePlayback(), Is.False);
                Finish(sequence);
                completed++;
            }
            Assert.That(completed, Is.EqualTo(17));
            Assert.That(StandaloneJourneySequence.DefaultSteps.Select(s => s.BeatId).ToArray(),
                Is.EqualTo(new[] { "beat-1", "beat-2", "beat-3", "beat-4", "beat-5", "beat-7", "beat-6",
                    "beat-8", "beat-11", "beat-17", "beat-9", "beat-10", "beat-16", "beat-12", "beat-13", "beat-14", "beat-15" }));
            Assert.That(sequence.State, Is.EqualTo(ConfirmationState.Finished));
            Assert.That(sequence.TryBegin(), Is.False);
            Assert.That(sequence.CompletePlayback(), Is.False);
            sequence.Reset();
            Assert.That(sequence.Current.BeatId, Is.EqualTo("beat-1"));
        }
    }
}

