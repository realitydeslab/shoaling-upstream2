using NUnit.Framework;
using UnityEngine;
using ShoalingUpstream.Journey;
using ShoalingUpstream.Localization;

namespace ShoalingUpstream.Localization.Tests
{
    /// <summary>
    /// The splat-space-to-anchor-space transform, in both directions.
    ///
    /// Worth its own file because getting the direction wrong is silent: every number stays
    /// finite, every beat still fires, and the whole soundscape is in the wrong place. The only
    /// way to catch it is to assert the direction explicitly against a transform that is not
    /// its own inverse.
    /// </summary>
    public class JourneyFrameTests
    {
        private static EditorFrame Frame(Quaternion rotation, Vector3 translation, float scale) => new()
        {
            calibrated = true,
            rotation = new[] { rotation.x, rotation.y, rotation.z, rotation.w },
            translation = Vec3.From(translation),
            scale = scale,
        };

        [Test]
        public void ToJourneyIsTheInverseOfTheAuthoredDirection()
        {
            var frame = JourneyFrame.From(Frame(Quaternion.Euler(0f, 37f, 0f), new Vector3(4f, -1f, 9f), 1.25f));
            var journeyPoint = new Vector3(2.5f, 0.4f, 18f);

            var anchorPoint = frame.ToAnchor(journeyPoint);
            Assert.AreNotEqual(journeyPoint, anchorPoint, "the fixture must not be an identity transform");

            var back = frame.ToJourney(anchorPoint);
            Assert.AreEqual(journeyPoint.x, back.x, 1e-3f);
            Assert.AreEqual(journeyPoint.y, back.y, 1e-3f);
            Assert.AreEqual(journeyPoint.z, back.z, 1e-3f);
        }

        [Test]
        public void ScaleIsAppliedInTheRightPlace()
        {
            // Ten metres in journey coordinates is twenty metres in the anchor's frame at scale 2.
            var frame = JourneyFrame.From(Frame(Quaternion.identity, Vector3.zero, 2f));
            Assert.AreEqual(new Vector3(0f, 0f, 20f), frame.ToAnchor(new Vector3(0f, 0f, 10f)));
            Assert.AreEqual(new Vector3(0f, 0f, 10f), frame.ToJourney(new Vector3(0f, 0f, 20f)));
        }

        [Test]
        public void AMalformedFrameFallsBackToIdentityRatherThanToNaN()
        {
            // A zero scale would divide every position the piece ever computes, and NaN
            // propagates silently through a projection into an s that no assertion catches.
            var broken = JourneyFrame.From(new EditorFrame
            {
                rotation = new[] { 0f, 0f, 0f, 0f },
                translation = new Vec3(),
                scale = 0f,
            });

            Assert.IsFalse(broken.WellFormed, "a broken frame must announce itself");
            var p = broken.ToJourney(new Vector3(1f, 2f, 3f));
            Assert.AreEqual(new Vector3(1f, 2f, 3f), p);
        }

        [Test]
        public void ADriftedQuaternionIsNormalisedRatherThanRejected()
        {
            var frame = JourneyFrame.From(new EditorFrame
            {
                rotation = new[] { 0f, 0f, 0f, 0.5f },   // identity, badly scaled
                translation = new Vec3(),
                scale = 1f,
            });

            Assert.IsTrue(frame.WellFormed);
            var back = frame.ToJourney(new Vector3(0f, 0f, 12f));
            Assert.AreEqual(12f, back.z, 1e-3f);
        }

        [Test]
        public void SessionToJourneyRecoversTheJourneyPositionFromASessionPose()
        {
            // The transform cached at a precise fix, which is what carries a dropout.
            var sessionRotation = Quaternion.Euler(0f, 115f, 0f);
            var sessionOffset = new Vector3(-40f, 3f, 88f);

            Pose SessionOf(Vector3 journeyPoint) =>
                new(sessionRotation * journeyPoint + sessionOffset, sessionRotation);

            var captured = SessionToJourney.Capture(SessionOf(new Vector3(0f, 0f, 10f)),
                                                    new Pose(new Vector3(0f, 0f, 10f), Quaternion.identity));

            var recovered = captured.Apply(SessionOf(new Vector3(1f, 0f, 22f)).position);
            Assert.AreEqual(1f, recovered.x, 1e-2f);
            Assert.AreEqual(22f, recovered.z, 1e-2f);
        }
    }
}
