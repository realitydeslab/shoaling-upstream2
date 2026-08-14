using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using ShoalingUpstream.Gestures;

namespace ShoalingUpstream.Tests
{
    /// <summary>
    /// Gesture detection against synthetic motion.
    ///
    /// The point of testing these here is that the expensive failure is a *false positive* —
    /// spawning eggs because someone bent to tie a shoelace — and false positives are exactly
    /// what you cannot reliably provoke on demand at a creek.
    /// </summary>
    public class GestureDetectorTests
    {
        private const float Dt = 1f / 50f;

        private static List<GestureDetector.Gesture> Run(
            GestureDetector d, IEnumerable<(float height, float speed, float seconds)> phases)
        {
            var fired = new List<GestureDetector.Gesture>();
            foreach (var (height, speed, seconds) in phases)
            {
                int steps = Mathf.CeilToInt(seconds / Dt);
                for (int i = 0; i < steps; i++)
                {
                    var g = d.Tick(height, speed, Dt);
                    if (g != GestureDetector.Gesture.None) fired.Add(g);
                }
            }
            return fired;
        }

        [Test]
        public void SettlesOnABaselineThenDetectsACrouch()
        {
            var d = new GestureDetector();
            var fired = Run(d, new[]
            {
                (1.50f, 0f, 3f),   // standing
                (1.05f, 0f, 1.5f), // down at the gravel
                (1.50f, 0f, 2f),   // back up
            });

            CollectionAssert.Contains(fired, GestureDetector.Gesture.Crouch);
            Assert.AreEqual(1, fired.Count, "a single crouch should fire exactly once");
        }

        [Test]
        public void DetectsA40cmLiftAndHoldsWhileUp()
        {
            // Not a jump: lifting yourself onto something and staying there.
            var d = new GestureDetector();
            var fired = new List<GestureDetector.Gesture>();

            for (int i = 0; i < 150; i++) fired.Add(d.Tick(1.50f, 0f, Dt)); // stand
            Assert.IsFalse(d.IsLifted);

            for (int i = 0; i < 100; i++) fired.Add(d.Tick(1.90f, 0f, Dt)); // up 40 cm, held 2 s
            Assert.IsTrue(d.IsLifted, "IsLifted must stay true for as long as they are up, so the "
                                    + "audio can hold 'out of the water' rather than fire a one-shot");

            fired.RemoveAll(g => g == GestureDetector.Gesture.None);
            CollectionAssert.Contains(fired, GestureDetector.Gesture.Lift);

            for (int i = 0; i < 50; i++) d.Tick(1.50f, 0f, Dt); // back down
            Assert.IsFalse(d.IsLifted);
        }

        [Test]
        public void ATinyBobIsNotALift()
        {
            var d = new GestureDetector();
            var fired = Run(d, new[]
            {
                (1.50f, 0f, 3f),
                (1.66f, 0f, 2f),   // 16 cm — rising onto your toes, not lifting yourself over
                (1.50f, 0f, 1f),
            });
            CollectionAssert.DoesNotContain(fired, GestureDetector.Gesture.Lift);
        }

        [Test]
        public void AQuickDipIsNotACrouch()
        {
            var d = new GestureDetector();
            var fired = Run(d, new[]
            {
                (1.50f, 0f, 3f),
                (1.05f, 0f, 0.25f), // down and straight back up — reaching for a dropped glove
                (1.50f, 0f, 2f),
            });
            CollectionAssert.DoesNotContain(fired, GestureDetector.Gesture.Crouch);
        }

        [Test]
        public void BaselineFollowsASlopingBankWithoutFiring()
        {
            // Walking up a 2.3 m rise over the whole reach must never look like a lift.
            var d = new GestureDetector();
            var fired = new List<GestureDetector.Gesture>();
            float height = 1.50f;
            for (int i = 0; i < 3000; i++) // 60 s
            {
                height += 2.3f / 3000f;    // the whole reach's rise, gradually
                var g = d.Tick(height, 0.7f, Dt);
                if (g != GestureDetector.Gesture.None) fired.Add(g);
            }
            CollectionAssert.DoesNotContain(fired, GestureDetector.Gesture.Lift);
            CollectionAssert.DoesNotContain(fired, GestureDetector.Gesture.Crouch);
            Assert.Greater(d.Baseline, 3.0f, "the baseline should have tracked the climb");
        }

        [Test]
        public void SlowBarometricDriftDoesNotFireAnything()
        {
            // Weather moves the barometer by more than the reach's entire elevation change,
            // which is precisely why absolute height is useless here.
            var d = new GestureDetector();
            var fired = new List<GestureDetector.Gesture>();
            float height = 1.50f;
            for (int i = 0; i < 15000; i++) // 5 minutes
            {
                height += 3.0f / 15000f;    // 3 m of apparent drift
                var g = d.Tick(height, 0f, Dt);
                if (g != GestureDetector.Gesture.None) fired.Add(g);
            }
            CollectionAssert.IsEmpty(fired);
        }

        [Test]
        public void LungeIsACommitThenAStop()
        {
            var d = new GestureDetector();
            var fired = Run(d, new[]
            {
                (1.50f, 0.0f, 2f),  // still
                (1.50f, 1.1f, 0.4f), // commit forward
                (1.50f, 0.05f, 0.6f), // and stop
            });
            CollectionAssert.Contains(fired, GestureDetector.Gesture.Lunge);
        }

        [Test]
        public void SteadyWalkingIsNotALunge()
        {
            var d = new GestureDetector();
            var fired = Run(d, new[]
            {
                (1.50f, 0.0f, 2f),
                (1.50f, 1.0f, 6f),  // just walking briskly, never stopping
            });
            CollectionAssert.DoesNotContain(fired, GestureDetector.Gesture.Lunge);
        }

        [Test]
        public void CooldownStopsOneGestureFiringTwice()
        {
            var d = new GestureDetector();
            var fired = Run(d, new[]
            {
                (1.50f, 0f, 3f),
                (1.05f, 0f, 4f),   // stay down a long time
                (1.50f, 0f, 1f),
            });
            Assert.AreEqual(1, fired.Count,
                "holding a crouch must not machine-gun the gesture");
        }
    }
}
