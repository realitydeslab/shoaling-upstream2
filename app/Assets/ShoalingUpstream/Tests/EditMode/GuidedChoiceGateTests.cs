using NUnit.Framework;
using ShoalingUpstream.Experience;

namespace ShoalingUpstream.Tests
{
    public sealed class GuidedChoiceGateTests
    {
        [TestCase(true)]
        [TestCase(false)]
        public void ShelterCanBeFoundInEitherOrderAndRepeatKeepsSelections(bool first)
        {
            var g = new GuidedChoiceGate();
            g.Open(JourneyGate.Shelter, true);
            var a = first ? JourneyChoice.FirstFound : JourneyChoice.SecondFound;
            var b = first ? JourneyChoice.SecondFound : JourneyChoice.FirstFound;
            Assert.That(g.Choose(a), Is.True);
            Assert.That(g.Choose(a), Is.False);
            Assert.That(g.Gate, Is.EqualTo(JourneyGate.Shelter));
            Assert.That(g.Choose(JourneyChoice.Repeat), Is.True);
            Assert.That(g.Choose(b), Is.False);
            g.Open(JourneyGate.Shelter);
            Assert.That(first ? g.FirstFound : g.SecondFound, Is.True);
            Assert.That(g.Choose(b), Is.True);
            Assert.That(g.Gate, Is.EqualTo(JourneyGate.Hidden));
        }
        [TestCase(JourneyGate.Strider, JourneyChoice.Receive)]
        [TestCase(JourneyGate.Heron, JourneyChoice.Offer)]
        [TestCase(JourneyGate.Heron, JourneyChoice.Ignore)]
        [TestCase(JourneyGate.Return, JourneyChoice.Jump)]
        [TestCase(JourneyGate.Return, JourneyChoice.Locate)]
        [TestCase(JourneyGate.Home, JourneyChoice.Home)]
        [TestCase(JourneyGate.Spawn, JourneyChoice.Spawn)]
        public void ChoicesHideTheirGateAndRejectDoublePresses(JourneyGate gate, JourneyChoice choice)
        {
            var g = new GuidedChoiceGate();
            Assert.That(g.Choose(choice), Is.False);
            g.Open(gate);
            Assert.That(g.Choose(JourneyChoice.FirstFound), Is.False);
            Assert.That(g.Choose(choice), Is.True);
            Assert.That(g.Choose(choice), Is.False);
            Assert.That(g.Gate, Is.EqualTo(JourneyGate.Hidden));
        }
    }
}
