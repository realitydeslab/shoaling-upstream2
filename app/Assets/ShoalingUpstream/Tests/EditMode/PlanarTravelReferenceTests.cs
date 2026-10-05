using NUnit.Framework;
using ShoalingUpstream.Experience;
using UnityEngine;

namespace ShoalingUpstream.Tests
{
    public sealed class PlanarTravelReferenceTests
    {
        [Test]
        public void PitchDoesNotChangeTheSharedHorizontalHeading()
        {
            var travel = new PlanarTravelReference();
            travel.Observe(new Vector3(1f, 0f, 1f).normalized);
            travel.Observe(new Vector3(1f, 10f, 1f).normalized);
            Vector3 expected = new Vector3(1f, 0f, 1f).normalized;
            Assert.That(Vector3.Distance(travel.Forward, expected), Is.LessThan(.0001f));
            travel.Observe(Vector3.down);
            Assert.That(Vector3.Distance(travel.Forward, expected), Is.LessThan(.0001f));
        }
        [Test]
        public void LookingSidewaysWhileWalkingDoesNotTurnTheShoalSideways()
        {
            var travel = new PlanarTravelReference();
            travel.ObserveTravel(Vector3.right, Vector3.forward, true);
            Assert.That(travel.Forward, Is.EqualTo(Vector3.forward));
            travel.ObserveTravel(Vector3.right, Vector3.zero, false);
            Assert.That(travel.Forward, Is.EqualTo(Vector3.right));
        }
        [Test]
        public void RainbowTurnIsNotUndoneBeforeTheParticipantTurns()
        {
            var travel = new PlanarTravelReference();
            travel.Observe(Vector3.forward);
            travel.BeginReturn();
            travel.Observe(Vector3.right);
            Assert.That(travel.Forward, Is.EqualTo(Vector3.forward));
            travel.CompleteReturn();
            travel.Observe(Vector3.forward);
            Assert.That(travel.Forward, Is.EqualTo(Vector3.back));
            Assert.That(travel.AwaitingParticipantTurn, Is.True);
            travel.Observe(Vector3.back);
            Assert.That(travel.AwaitingParticipantTurn, Is.False);
            travel.Observe(Vector3.left);
            Assert.That(travel.Forward, Is.EqualTo(Vector3.left));
        }
    }
}
