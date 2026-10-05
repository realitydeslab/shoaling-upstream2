using NUnit.Framework;
using ShoalingUpstream.Experience;
using UnityEngine;

namespace ShoalingUpstream.Tests
{
    public sealed class NaturalFishMotionTests
    {
        [Test]
        public void NaturalVerticalBuoyancyDoesNotHoldTheNextNarrationOrButton()
        {
            Assert.That(NaturalFishMotion.Arrived(new Vector3(.01f, .035f, .01f), Vector3.zero), Is.True);
            Assert.That(NaturalFishMotion.Arrived(new Vector3(.1f, .035f, 0f), Vector3.zero), Is.False);
            Assert.That(NaturalFishMotion.Arrived(new Vector3(.01f, .2f, 0f), Vector3.zero), Is.False);
        }
        [Test]
        public void AbruptWalkingAndDistantTargetsCannotProduceSpeedSpikes()
        {
            float speed = .3f;
            foreach (float target in new[] { 9f, 20f, .15f, 40f, 0f })
            {
                float next = NaturalFishMotion.CruiseSpeed(speed, target, 1f / 60f);
                Assert.That(Mathf.Abs(next - speed), Is.LessThanOrEqualTo(.00601f));
                Assert.That(next, Is.InRange(0f, 1.44f)); speed = next;
            }
            for (int i = 0; i < 600; i++) speed = NaturalFishMotion.CruiseSpeed(speed, 40f, 1f / 60f);
            Assert.That(speed, Is.EqualTo(1.44f).Within(.0001f));
        }
        [Test]
        public void ReverseTargetTurnsBeforeMovingAndNeverSwimsTailFirst()
        {
            Vector3 position = Vector3.zero, heading = Vector3.forward, target = Vector3.back;
            var first = NaturalFishMotion.Step(position, ref heading, target, .1f, 20f, .1f);
            Assert.That(first.magnitude, Is.EqualTo(0f).Within(.0001f));
            bool moved = false;
            for (int i = 0; i < 220; i++)
            {
                Vector3 shift = NaturalFishMotion.Step(position, ref heading, target, .1f, 20f, .1f);
                Assert.That(heading.y, Is.EqualTo(0f).Within(.00001f));
                Assert.That(Vector3.Dot(shift, heading), Is.GreaterThanOrEqualTo(-.000001f));
                Assert.That(shift.magnitude, Is.LessThanOrEqualTo(.01001f));
                position += shift; moved |= shift.magnitude > 0f;
            }
            Assert.That(moved, Is.True);
            Assert.That(Vector3.Distance(position, target), Is.LessThan(.03f));
        }
        [Test]
        public void TurningHasABoundedContinuousAngularSpeed()
        {
            Vector3 head = Vector3.forward;
            NaturalFishMotion.Step(Vector3.zero, ref head, Vector3.right, .1f, 20f, .1f);
            Assert.That(Vector3.Angle(Vector3.forward, head), Is.EqualTo(2f).Within(.01f));
        }
        [Test]
        public void TinyFrameStepsDoNotLoseTheirTurnToFloatingPointRounding()
        {
            Vector3 head = Vector3.forward;
            for (int i = 0; i < 2000; i++)
                head = NaturalFishMotion.TurnHeading(head, Vector3.right, .01f);
            Assert.That(NaturalFishMotion.YawDelta(Vector3.forward, head), Is.EqualTo(20f).Within(.01f));
            Assert.That(NaturalFishMotion.YawDelta(Vector3.forward,
                NaturalFishMotion.TurnHeading(Vector3.forward, Vector3.right, .01f)), Is.EqualTo(.01f).Within(.0001f));
        }
        [TestCase(1)] [TestCase(24)] [TestCase(908)]
        public void EggsAreCircularIrregularWithZeroToOneRadiusSurfaceGaps(int seed)
        {
            var points = OrganicEggLayout.Create(40, .02f, seed);
            foreach (var point in points)
            {
                float nearest = float.MaxValue;
                foreach (var other in points) if (other != point) nearest = Mathf.Min(nearest, Vector2.Distance(point, other));
                Assert.That(nearest, Is.InRange(.01999f, .03001f));
                Assert.That(point.magnitude, Is.LessThanOrEqualTo(Mathf.Sqrt(40) * .02f * .68f));
            }
        }
    }
}
