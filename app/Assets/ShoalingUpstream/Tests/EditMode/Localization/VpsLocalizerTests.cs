using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using ShoalingUpstream.Journey;
using ShoalingUpstream.Localization;

namespace ShoalingUpstream.Localization.Tests
{
    /// <summary>
    /// The degradation ladder, walked.
    ///
    /// Every case here is one the field cannot produce on demand: tracking that drops at a known
    /// moment for a known length, a phone that localizes only after forty seconds, a site that
    /// never localizes at all. They are also the cases that decide whether a visitor hears the
    /// piece or stands in a park in silence, so they are the ones that have to be pinned.
    ///
    /// The synthetic walk is deliberately the same shape as JourneyProgressionTests — a visitor
    /// moving upstream at a plausible pace — with the AR stack failing underneath it.
    /// </summary>
    public class VpsLocalizerTests
    {
        private const float Dt = 1f / 30f;
        private const float Reach = 60f;

        /// <summary>A session frame that is emphatically not the journey frame, so that any test
        /// that passes by accidentally treating the two as the same will fail.</summary>
        private static readonly Quaternion SessionRotation = Quaternion.Euler(0f, 115f, 0f);
        private static readonly Vector3 SessionOffset = new(-40f, 3f, 88f);

        // --- fixtures ------------------------------------------------------

        private static JourneyDocument MakeJourney(bool calibrated = true, EditorFrame frame = null,
                                                   params (string id, float s, string kind)[] beats)
        {
            var doc = new JourneyDocument
            {
                schemaVersion = JourneyDocument.SupportedSchemaVersion,
                journeyId = "test",
                site = new SiteRef
                {
                    anchorPayload = "cGF5bG9hZA==",
                    centreline = new List<Vec3>
                    {
                        new() { x = 0, y = 0, z = 0 },
                        new() { x = 0, y = 0, z = Reach },
                    },
                },
                editorFrame = frame ?? new EditorFrame { calibrated = calibrated, scale = 1f },
                shoal = new Shoal { startingCount = 40, minimumCount = 6 },
            };

            foreach (var (id, s, kind) in beats)
            {
                doc.beats.Add(new Beat
                {
                    id = id, title = id, interaction = kind, s = s,
                    position = new Vec3 { x = 0, y = 0, z = s },
                    trigger = new Trigger
                    {
                        enterRadiusM = 2.5f, exitRadiusM = 4.0f,
                        dwellSeconds = 1.0f, minimumHoldSeconds = 5f,
                        requiresPreviousComplete = true,
                    },
                    givesFish = kind == "give" ? 12 : 0,
                });
            }
            return doc;
        }

        private static AnchorResolution GoodAnchor =>
            new(true, AnchorRoute.StoredPayload, "cGF5bG9hZA==", "asset", true, "stored payload");

        private static Vector3 JourneyPoint(float s, float lateral = 0f) => new(lateral, 0f, s);

        private static Pose SessionPoseAt(float s, float lateral = 0f) => new(
            SessionRotation * JourneyPoint(s, lateral) + SessionOffset, SessionRotation);

        /// <summary>A fully localized frame at distance s along the reach.</summary>
        private static VpsSample Precise(float s, float lateral = 0f, float confidence = 1f) => new(
            VpsTrackingState.Precise,
            new Pose(JourneyPoint(s, lateral), Quaternion.identity), true,
            SessionPoseAt(s, lateral), true, confidence);

        /// <summary>ARKit still tracking, VPS gone. The common dropout.</summary>
        private static VpsSample SessionOnly(float s, VpsTrackingState state = VpsTrackingState.Unavailable) =>
            new(state, Pose.identity, false, SessionPoseAt(s), true, 0f);

        /// <summary>Nothing at all — no VPS, no ARKit. The blind case.</summary>
        private static VpsSample Blind(VpsTrackingState state = VpsTrackingState.Unavailable) =>
            new(state, Pose.identity, false, Pose.identity, false, 0f);

        private static void Run(VpsLocalizer localizer, float seconds, System.Func<float, VpsSample> sample)
        {
            int steps = Mathf.CeilToInt(seconds / Dt);
            for (int i = 0; i < steps; i++) localizer.Tick(sample((i + 1) * Dt), Dt);
        }

        private static void Hold(VpsLocalizer localizer, float seconds, VpsSample sample) =>
            Run(localizer, seconds, _ => sample);

        private static VpsLocalizer Started(JourneyDocument doc, LocalizationPolicy policy = null,
                                            RuntimeSurface surface = RuntimeSurface.Device)
        {
            var localizer = new VpsLocalizer(doc, policy, surface);
            localizer.Begin(GoodAnchor);
            return localizer;
        }

        // --- the calibration gate -------------------------------------------

        [Test]
        public void AnUncalibratedJourneyIsRefusedOnDeviceAndAllowedInSimulation()
        {
            var doc = MakeJourney(calibrated: false);

            var device = new VpsLocalizer(doc, null, RuntimeSurface.Device);
            Assert.AreEqual(LocalizationMode.Refused, device.Mode);
            StringAssert.Contains("calibrated", device.RefusalReason);

            // Refusal is terminal: ticking a refused localizer must not quietly start working.
            device.Begin(GoodAnchor);
            Hold(device, 5f, Precise(10f));
            Assert.AreEqual(LocalizationMode.Refused, device.Mode);
            Assert.AreEqual(0f, device.Fix.S);

            var simulation = new VpsLocalizer(doc, null, RuntimeSurface.Simulation);
            Assert.AreEqual(LocalizationMode.Idle, simulation.Mode);
            Assert.IsNull(simulation.RefusalReason);
        }

        [Test]
        public void AJourneyWithNoCentrelineIsRefusedOnEitherSurface()
        {
            var doc = MakeJourney();
            doc.site.centreline = new List<Vec3> { new() { x = 0, y = 0, z = 0 } };

            Assert.IsNotNull(VpsLocalizer.Gate(doc, RuntimeSurface.Device));
            Assert.IsNotNull(VpsLocalizer.Gate(doc, RuntimeSurface.Simulation));
        }

        // --- pose to journey space to s -------------------------------------

        [Test]
        public void APreciseFixBecomesADistanceAlongTheReach()
        {
            var localizer = Started(MakeJourney());
            localizer.Tick(Precise(18.5f, lateral: 2.1f), Dt);

            Assert.AreEqual(LocalizationMode.Tracking, localizer.Mode);
            Assert.AreEqual(FixSource.Anchor, localizer.Fix.Source);
            Assert.AreEqual(LocalizationQuality.Precise, localizer.Fix.Quality);
            Assert.AreEqual(18.5f, localizer.Fix.S, 0.01f);
            Assert.AreEqual(2.1f, localizer.Fix.Lateral, 0.01f,
                "cross-stream offset belongs in the report, not in s");
        }

        [Test]
        public void TheEditorFrameIsAppliedBeforeProjection()
        {
            // A pose in the anchor's frame is not a pose in the journey's. With a non-identity
            // editorFrame, reading the anchor pose straight onto the centreline gives an s that
            // is wrong by metres — and wrong quietly.
            var rotation = Quaternion.Euler(0f, 90f, 0f);
            var frame = new EditorFrame
            {
                calibrated = true,
                rotation = new[] { rotation.x, rotation.y, rotation.z, rotation.w },
                translation = Vec3.From(new Vector3(5f, 0f, -2f)),
                scale = 2f,
            };

            var localizer = Started(MakeJourney(frame: frame));
            var journeyFrame = JourneyFrame.From(frame);

            // Stand at s = 15 in journey coordinates; hand over where that is in anchor space.
            var anchorPosition = journeyFrame.ToAnchor(JourneyPoint(15f));
            localizer.Tick(new VpsSample(VpsTrackingState.Precise,
                new Pose(anchorPosition, Quaternion.identity), true,
                SessionPoseAt(15f), true, 1f), Dt);

            Assert.AreEqual(15f, localizer.Fix.S, 0.05f);
            Assert.AreNotEqual(anchorPosition.z, localizer.Fix.S,
                "the fixture must not be an identity transform");
        }

        [Test]
        public void SIsClampedToTheReach()
        {
            var localizer = Started(MakeJourney());
            localizer.Tick(Precise(-25f), Dt);
            Assert.AreEqual(0f, localizer.Fix.S, 0.01f);

            localizer.Tick(Precise(500f), Dt);
            Assert.AreEqual(Reach, localizer.Fix.S, 0.01f);
        }

        // --- nothing fires before the first fix -----------------------------

        [Test]
        public void CoarseIsReportedAsCoarseAndNeverPromoted()
        {
            // Coarse is geolocation-grade. Against a 2.5 m gate that would fire beats at random,
            // so it never drives the trigger machine. Its job is to say the visitor has arrived.
            var localizer = Started(MakeJourney());
            Hold(localizer, 10f, Blind(VpsTrackingState.Coarse));

            Assert.AreEqual(LocalizationMode.Searching, localizer.Mode);
            Assert.AreEqual(LocalizationQuality.Coarse, localizer.Fix.Quality);
            Assert.AreEqual(FixSource.None, localizer.Fix.Source);
            Assert.AreEqual(0f, localizer.Fix.S);
            StringAssert.Contains("Nothing will fire", localizer.Report().Detail);
        }

        // --- the staleness ladder -------------------------------------------

        [Test]
        public void AVeryBriefDropoutIsSimplyHeld()
        {
            var localizer = Started(MakeJourney());
            Hold(localizer, 2f, Precise(12f));

            Hold(localizer, 1f, Blind());
            Assert.AreEqual(FixSource.Anchor, localizer.Fix.Source,
                "under the hold window the last fix stands unchanged");
            Assert.AreEqual(12f, localizer.Fix.S, 0.01f);
            Assert.AreEqual(LocalizationQuality.Precise, localizer.Fix.Quality);
        }

        [Test]
        public void OdometryCarriesADropoutAtTheAccuracyOfTheWalkItself()
        {
            var localizer = Started(MakeJourney());

            // Walk to 12 m under VPS, so the session-to-journey transform is captured.
            float s = 0f;
            Run(localizer, 12f / 0.9f, _ => Precise(s += 0.9f * Dt));
            Assert.AreEqual(LocalizationMode.Tracking, localizer.Mode);

            // VPS drops for ten seconds. ARKit keeps tracking; the visitor keeps walking.
            Run(localizer, 10f, _ => SessionOnly(s += 0.9f * Dt));

            Assert.AreEqual(FixSource.DeadReckoned, localizer.Fix.Source);
            Assert.AreEqual(DeadReckonBasis.Odometry, localizer.Fix.Basis);
            Assert.AreEqual(LocalizationQuality.Precise, localizer.Fix.Quality,
                "a dropout carried on odometry must keep firing beats");
            Assert.AreEqual(s, localizer.Fix.S, 0.05f,
                "odometry through the cached transform should track the real walk almost exactly");
            Assert.Less(localizer.Fix.Confidence, 1f, "confidence must decay with staleness");
        }

        [Test]
        public void PastTheOdometryHorizonThePositionFreezesAndStopsClaimingToBePrecise()
        {
            var localizer = Started(MakeJourney(), new LocalizationPolicy { OdometryHorizonSeconds = 5f });

            float s = 0f;
            Run(localizer, 8f, _ => Precise(s += 0.9f * Dt));
            float atDropout = localizer.Fix.S;

            Run(localizer, 12f, _ => SessionOnly(s += 0.9f * Dt));

            Assert.AreEqual(DeadReckonBasis.Frozen, localizer.Fix.Basis);
            Assert.AreEqual(LocalizationQuality.Coarse, localizer.Fix.Quality,
                "a pose too stale to select a beat must stop being sold as Precise");
            Assert.Greater(localizer.Fix.S, atDropout, "s froze where the odometry left it");
            Assert.Less(localizer.Fix.S, s - 1f, "and did not keep following the walk");
            StringAssert.Contains("frozen", localizer.Report().Detail);
        }

        [Test]
        public void BlindDeadReckoningCarriesAFewSecondsAndIsBoundedHard()
        {
            var policy = new LocalizationPolicy { MaxDeadReckonMetres = 3f };
            var localizer = Started(MakeJourney(), policy);

            // Establish a walking speed under VPS.
            float s = 0f;
            Run(localizer, 6f, _ => Precise(s += 0.9f * Dt));
            float atDropout = localizer.Fix.S;
            Assert.Greater(localizer.Fix.SpeedMps, 0.4f, "the walk should have taught a speed");

            // Everything fails at once — no VPS and no ARKit.
            Hold(localizer, 4f, Blind());

            Assert.AreEqual(DeadReckonBasis.SpeedDecay, localizer.Fix.Basis);
            Assert.AreEqual(LocalizationQuality.Precise, localizer.Fix.Quality);
            float advanced = localizer.Fix.S - atDropout;
            Assert.Greater(advanced, 0.5f, "a visitor mid-stride must be carried through the gap");
            Assert.LessOrEqual(advanced, policy.MaxDeadReckonMetres + 0.01f,
                "and never further than one beat spacing, whatever the arithmetic says");
        }

        [Test]
        public void ABlindEstimateDecaysTowardStandingStillRatherThanRunningAway()
        {
            var localizer = Started(MakeJourney(), new LocalizationPolicy
            {
                MaxDeadReckonMetres = 100f,        // remove the cap so the decay is what is tested
                SpeedHorizonSeconds = 5f,
            });

            float s = 0f;
            Run(localizer, 6f, _ => Precise(s += 1.2f * Dt));

            Hold(localizer, 1.6f, Blind());     // through the hold window, where nothing moves
            float first = localizer.Fix.S;

            Hold(localizer, 1.5f, Blind());
            float earlyRate = localizer.Fix.S - first;
            float second = localizer.Fix.S;

            Hold(localizer, 1.5f, Blind());
            float lateRate = localizer.Fix.S - second;

            Assert.Greater(earlyRate, 0f, "the estimate should have moved at all");

            Assert.Less(lateRate, earlyRate,
                "holding the last speed through a long dropout would walk the visitor to the headwater");
        }

        [Test]
        public void ABlindEstimateNeverRunsDownstream()
        {
            var localizer = Started(MakeJourney(), new LocalizationPolicy { MaxDeadReckonMetres = 100f });

            // Walk downstream under VPS so the speed estimate is negative.
            float s = 30f;
            localizer.Tick(Precise(s), Dt);
            Run(localizer, 6f, _ => Precise(s -= 0.9f * Dt));
            Assert.Less(localizer.Fix.SpeedMps, 0f, "the estimate should have learned a downstream walk");

            float atDropout = localizer.Fix.S;
            Hold(localizer, 4f, Blind());

            Assert.GreaterOrEqual(localizer.Fix.S, atDropout,
                "a position with no evidence under it must not silently unwind progress");
        }

        [Test]
        public void AMeasuredPositionMayGoBackwards()
        {
            // People walk back to look at something. JourneyProgression already handles that
            // correctly; freezing s at a high-water mark would instead park the audio at a place
            // the visitor is not, and the creek is meant to get quieter as they leave it.
            var localizer = Started(MakeJourney());
            Hold(localizer, 2f, Precise(30f));
            Assert.AreEqual(30f, localizer.Fix.S, 0.01f);

            Hold(localizer, 2f, Precise(18f));
            Assert.AreEqual(18f, localizer.Fix.S, 0.01f);
        }

        [Test]
        public void ACorrectionAfterDeadReckoningIsTakenWholeAndItsSizeReported()
        {
            // Relocalization is genuinely discontinuous. Rejecting the outliers would mean never
            // recovering from a drifted estimate, so the jump is taken and announced instead.
            var localizer = Started(MakeJourney(), new LocalizationPolicy { MaxDeadReckonMetres = 100f });

            float s = 0f;
            Run(localizer, 6f, _ => Precise(s += 1.2f * Dt));
            Hold(localizer, 3f, Blind());
            float estimated = localizer.Fix.S;

            localizer.Tick(Precise(estimated - 2.5f), Dt);
            var report = localizer.Report();

            Assert.AreEqual(estimated - 2.5f, localizer.Fix.S, 0.01f, "the fix wins over the estimate");
            Assert.AreEqual(2.5f, report.SnapMetres, 0.05f);
            StringAssert.Contains("Corrected", report.Detail);
        }

        [Test]
        public void ASnapIsReportedOnlyOnTheFrameItHappens()
        {
            var localizer = Started(MakeJourney(), new LocalizationPolicy { MaxDeadReckonMetres = 100f });
            float s = 0f;
            Run(localizer, 6f, _ => Precise(s += 1.2f * Dt));
            Hold(localizer, 3f, Blind());

            localizer.Tick(Precise(localizer.Fix.S - 2f), Dt);
            Assert.Greater(localizer.Report().SnapMetres, 1f);

            localizer.Tick(Precise(localizer.Fix.S), Dt);
            Assert.AreEqual(0f, localizer.Report().SnapMetres,
                "a snap is an event, not a state, and must not stick to the report");
        }

        // --- never localizing at all ----------------------------------------

        [Test]
        public void FailingToLocalizeAtAllStillRunsTheWalk()
        {
            // A visitor standing in a park with nothing happening is a failed artwork.
            var policy = new LocalizationPolicy { FirstFixTimeoutSeconds = 10f };
            var localizer = Started(MakeJourney(), policy);

            Hold(localizer, 8f, SessionOnly(0f));
            Assert.AreEqual(LocalizationMode.Searching, localizer.Mode, "not yet — give the lock a chance");

            // Walk on, with the phone still unable to place itself.
            float s = 0f;
            Run(localizer, 20f, _ => SessionOnly(s += 0.9f * Dt));

            Assert.AreEqual(LocalizationMode.Unanchored, localizer.Mode);
            Assert.AreEqual(FixSource.Unanchored, localizer.Fix.Source);
            Assert.AreEqual(LocalizationQuality.Precise, localizer.Fix.Quality,
                "unanchored mode has to fire beats or there is no artwork");
            Assert.Greater(localizer.Fix.S, 5f, "the walk should be following the visitor's motion");
            StringAssert.Contains("UNANCHORED", localizer.Report().Detail);
        }

        [Test]
        public void AnUnanchoredWalkFollowsMotionNotAClockWhenItCan()
        {
            var localizer = Started(MakeJourney(), new LocalizationPolicy { FirstFixTimeoutSeconds = 1f });
            Hold(localizer, 2f, SessionOnly(0f));
            Assert.AreEqual(LocalizationMode.Unanchored, localizer.Mode);

            // Stand still for ten seconds.
            Hold(localizer, 10f, SessionOnly(0f));
            Assert.AreEqual(0f, localizer.Fix.S, 0.01f,
                "a visitor who has stopped must not be walked upstream by a clock");

            float s = 0f;
            Run(localizer, 10f, _ => SessionOnly(s += 0.9f * Dt));
            Assert.Greater(localizer.Fix.S, 5f, "and must advance once they move again");
        }

        [Test]
        public void WithNoMotionTrackingEitherTheWalkRunsOnAClock()
        {
            var localizer = Started(MakeJourney(), new LocalizationPolicy { FirstFixTimeoutSeconds = 1f });
            Hold(localizer, 11f, Blind());

            Assert.AreEqual(LocalizationMode.Unanchored, localizer.Mode);
            Assert.Greater(localizer.Fix.S, 3f, "a timed radio play beats silence");
            StringAssert.Contains("clock", localizer.Report().Detail);
        }

        [Test]
        public void AnUnanchoredWalkIsAbandonedTheMomentARealFixArrives()
        {
            var localizer = Started(MakeJourney(), new LocalizationPolicy { FirstFixTimeoutSeconds = 1f });
            float s = 0f;
            Run(localizer, 12f, _ => SessionOnly(s += 0.9f * Dt));
            Assert.AreEqual(LocalizationMode.Unanchored, localizer.Mode);

            localizer.Tick(Precise(4f), Dt);

            Assert.AreEqual(LocalizationMode.Tracking, localizer.Mode);
            Assert.AreEqual(4f, localizer.Fix.S, 0.01f);
            Assert.Greater(localizer.Report().SnapMetres, 1f,
                "the correction is large and audio has to be told so it can crossfade");
        }

        [Test]
        public void TheUnanchoredFallbackCanBeTurnedOffForAnUnattendedInstallation()
        {
            var localizer = Started(MakeJourney(), new LocalizationPolicy
            {
                FirstFixTimeoutSeconds = 1f,
                UnanchoredFallback = false,
            });

            Hold(localizer, 10f, SessionOnly(0f));
            Assert.AreEqual(LocalizationMode.Searching, localizer.Mode);
            Assert.AreEqual(LocalizationQuality.Unavailable, localizer.Fix.Quality);
        }

        [Test]
        public void ALostFixEventuallyBecomesAnUnanchoredWalkFromWhereItWasLost()
        {
            var localizer = Started(MakeJourney(), new LocalizationPolicy
            {
                OdometryHorizonSeconds = 2f,
                LostSeconds = 6f,
            });

            Hold(localizer, 3f, Precise(22f));
            Hold(localizer, 10f, Blind());

            Assert.AreEqual(LocalizationMode.Unanchored, localizer.Mode);
            Assert.GreaterOrEqual(localizer.Fix.S, 22f,
                "the walk resumes from the last place we actually knew, not from the start");
        }

        [Test]
        public void WithTheFallbackOffALostFixEndsAsUnavailableRatherThanAsAGuess()
        {
            var localizer = Started(MakeJourney(), new LocalizationPolicy
            {
                OdometryHorizonSeconds = 2f,
                LostSeconds = 4f,
                UnanchoredFallback = false,
            });

            Hold(localizer, 3f, Precise(22f));
            Hold(localizer, 8f, Blind());

            Assert.AreEqual(LocalizationQuality.Unavailable, localizer.Fix.Quality);
            Assert.AreEqual(22f, localizer.Fix.S, 0.5f, "the last known position, not a fabricated one");
        }

        [Test]
        public void AFailedAnchorResolutionGoesStraightToTheUnanchoredWalk()
        {
            // VPS will never produce anything without a payload, so there is nothing to be
            // gained by spending the first-fix timeout finding that out.
            var localizer = new VpsLocalizer(MakeJourney(), null, RuntimeSurface.Device);
            localizer.Begin(AnchorResolution.Failure("journey carries no anchor payload"));

            Assert.AreEqual(LocalizationMode.Unanchored, localizer.Mode);

            float s = 0f;
            Run(localizer, 5f, _ => SessionOnly(s += 0.9f * Dt));
            Assert.Greater(localizer.Fix.S, 3f);
        }

        // --- honesty ---------------------------------------------------------

        [Test]
        public void TheReportCarriesTheSdkStateUnsmoothedEvenWhileDeadReckoning()
        {
            // The controller is a safety net, and a safety net told a tidied-up story cannot be
            // used to decide whether to intervene.
            var localizer = Started(MakeJourney());
            float s = 0f;
            Run(localizer, 6f, _ => Precise(s += 0.9f * Dt, confidence: 0.87f));
            Assert.AreEqual(0.87f, localizer.Report().RawConfidence, 1e-4f);

            Run(localizer, 4f, _ => SessionOnly(s += 0.9f * Dt, VpsTrackingState.Coarse));
            var report = localizer.Report();

            Assert.AreEqual(VpsTrackingState.Coarse, report.RawState,
                "the SDK said Coarse and the operator must be told Coarse");
            Assert.AreEqual(LocalizationQuality.Precise, report.Fix.Quality,
                "while what we act on is separately visible");
            Assert.AreEqual(FixSource.DeadReckoned, report.Fix.Source);
            Assert.AreEqual("precise", report.StatusWord);
        }

        [Test]
        public void ALowConfidenceFixIsBelievedUntilTheFloorIsDeliberatelyRaised()
        {
            // trackingConfidence has no documented scale, so thresholding on it by default would
            // either do nothing or silently reject every fix in the field.
            var trusting = Started(MakeJourney());
            trusting.Tick(Precise(10f, confidence: 0.01f), Dt);
            Assert.AreEqual(FixSource.Anchor, trusting.Fix.Source);

            var strict = Started(MakeJourney(), new LocalizationPolicy { MinimumTrackingConfidence = 0.5f });
            strict.Tick(Precise(10f, confidence: 0.01f), Dt);
            Assert.AreEqual(FixSource.None, strict.Fix.Source);
            Assert.AreEqual(0f, strict.Fix.S);
        }

        [Test]
        public void AnAnchorFromTheWrongAssetIsUsedAndSaidSoOnEveryFrame()
        {
            var localizer = new VpsLocalizer(MakeJourney(), null, RuntimeSurface.Device);
            localizer.Begin(new AnchorResolution(true, AnchorRoute.RuntimeLookup, "cGF5", "other-asset",
                                                 false, "runtime lookup found a different asset"));

            Assert.IsTrue(localizer.AnchorFrameSuspect);
            localizer.Tick(Precise(10f), Dt);
            StringAssert.Contains("does not match", localizer.Report().Detail);
        }

        [Test]
        public void ThePoseAgeTheSdkReportsCountsTowardStaleness()
        {
            // A pose that arrives late is not a fresh pose. Ignoring its own age would extend
            // every horizon by however long the SDK held on to it.
            var policy = new LocalizationPolicy { HoldSeconds = 1f, OdometryHorizonSeconds = 0f };
            var localizer = Started(MakeJourney(), policy);

            localizer.Tick(new VpsSample(VpsTrackingState.Precise,
                new Pose(JourneyPoint(10f), Quaternion.identity), true,
                Pose.identity, false, 1f, poseAgeSeconds: 1.5f), Dt);

            Assert.AreEqual(1.5f, localizer.Fix.AgeSeconds, 0.01f);
            localizer.Tick(Blind(), Dt);
            Assert.AreNotEqual(FixSource.Anchor, localizer.Fix.Source,
                "a pose already 1.5 s old is past a 1 s hold window on arrival");
        }

        // --- the whole walk ---------------------------------------------------

        [Test]
        public void AWalkWithADropoutInTheMiddleStillCompletesEveryBeatInOrder()
        {
            var doc = MakeJourney(true, null,
                ("tree", 5f, "proximity"),
                ("redd", 15f, "crouch"),
                ("strider", 25f, "catch"),
                ("heron", 35f, "give"),
                ("barrier", 45f, "lift"),
                ("headwater", 55f, "proximity"));

            var localizer = Started(doc);
            var progression = new JourneyProgression(doc);
            var order = new List<string>();

            // Walk the whole reach at 0.7 m/s. VPS fails completely between 20 and 30 metres —
            // eleven seconds of it, straddling the third beat.
            for (float s = 0f; s <= Reach; s += 0.7f * Dt)
            {
                bool dropout = s > 20f && s < 30f;
                localizer.Tick(dropout ? SessionOnly(s) : Precise(s), Dt);

                var fix = localizer.Fix;
                progression.Tick(fix.JourneyPosition, fix.Quality, Dt);
                foreach (var e in progression.DrainEvents())
                {
                    if (e.Kind == JourneyProgression.Event.Type.Completed) order.Add(e.Beat.id);
                }
                if (progression.AwaitingAction) progression.SatisfyAction(progression.Current.Kind);
            }

            CollectionAssert.AreEqual(
                new[] { "tree", "redd", "strider", "heron", "barrier", "headwater" }, order,
                "a ten-metre VPS dropout mid-walk must not cost the visitor a beat");
            Assert.AreEqual(28, progression.ShoalCount);
        }

        [Test]
        public void AWalkWithNoLocalizationAtAllStillCompletesEveryBeat()
        {
            var doc = MakeJourney(true, null,
                ("tree", 5f, "proximity"),
                ("redd", 15f, "proximity"),
                ("strider", 25f, "proximity"),
                ("heron", 35f, "proximity"),
                ("barrier", 45f, "proximity"),
                ("headwater", 55f, "proximity"));

            var localizer = Started(doc, new LocalizationPolicy { FirstFixTimeoutSeconds = 5f });
            var progression = new JourneyProgression(doc);
            var order = new List<string>();

            // Stand still for the timeout, then walk the reach. Nothing ever localizes.
            for (int i = 0; i < Mathf.CeilToInt(6f / Dt); i++)
            {
                localizer.Tick(SessionOnly(0f), Dt);
                progression.Tick(localizer.Fix.JourneyPosition, localizer.Fix.Quality, Dt);
                progression.DrainEvents();
            }

            // Walk past the end of the reach: with no world frame the piece can only count
            // displacement, and it discounts some of that as wandering, so it lags the walk.
            for (float s = 0f; s <= Reach + 12f; s += 0.7f * Dt)
            {
                localizer.Tick(SessionOnly(s), Dt);
                var fix = localizer.Fix;
                progression.Tick(fix.JourneyPosition, fix.Quality, Dt);
                foreach (var e in progression.DrainEvents())
                {
                    if (e.Kind == JourneyProgression.Event.Type.Completed) order.Add(e.Beat.id);
                }
            }

            Assert.AreEqual(LocalizationMode.Unanchored, localizer.Mode);
            CollectionAssert.AreEqual(
                new[] { "tree", "redd", "strider", "heron", "barrier", "headwater" }, order,
                "the piece has to be walkable even when VPS never works at all");
        }

        [Test]
        public void ARefusedJourneyCompletesNothing()
        {
            var doc = MakeJourney(false, null, ("tree", 5f, "proximity"));
            var localizer = new VpsLocalizer(doc, null, RuntimeSurface.Device);
            var progression = new JourneyProgression(doc);

            for (float s = 0f; s <= 20f; s += 0.7f * Dt)
            {
                localizer.Tick(Precise(s), Dt);
                progression.Tick(localizer.Fix.JourneyPosition, localizer.Fix.Quality, Dt);
            }

            Assert.IsEmpty(progression.DrainEvents().Where(
                e => e.Kind == JourneyProgression.Event.Type.Completed).ToList(),
                "provisional coordinates must not put a visitor anywhere on a real bank");
        }
    }
}
