using System;
using System.Collections.Generic;
using UnityEngine;
using ShoalingUpstream.Journey;

namespace ShoalingUpstream.Localization
{
    /// <summary>
    /// Turns VPS samples into a distance along the reach, and keeps producing one when VPS stops.
    ///
    /// The lifecycle is the easy half. The hard half is what this does when tracking degrades,
    /// and the governing fact is that the visitor keeps walking whatever the phone believes.
    /// Three rules follow from that, and everything below is an expression of one of them:
    ///
    /// <list type="number">
    /// <item><b>Never stop producing a position.</b> The piece is a soundscape in a real creek
    /// with non-isolating headphones. Silence is not a neutral state that the visitor waits out;
    /// it reads as the work being over or broken. So a dropout is carried, first by session
    /// odometry and then by a decayed speed estimate, and a total failure to localize still runs
    /// the walk.</item>
    ///
    /// <item><b>Never claim more than is true.</b> A carried position is labelled as carried, a
    /// frozen one as frozen, an unanchored walk as unanchored, and the SDK's own state and
    /// confidence go out on every frame unsmoothed. The operator is a safety net and cannot
    /// decide whether to intervene from a story that has been tidied up.</item>
    ///
    /// <item><b>Measured s may go anywhere; assumed s only goes upstream.</b> People genuinely
    /// walk back to look at something, and JourneyProgression already handles that correctly, so
    /// clamping a measured position monotonically would freeze the audio at a place the visitor
    /// is not. But a position with no evidence under it must never drift downstream and silently
    /// unwind progress, so dead reckoning and the unanchored walk advance only.</item>
    /// </list>
    ///
    /// No NSDK types appear here. Samples arrive through <see cref="IVpsSource"/> or are handed
    /// in directly, which is what lets the whole ladder be walked in EditMode.
    /// </summary>
    public class VpsLocalizer
    {
        private readonly JourneyDocument _journey;
        private readonly LocalizationPolicy _policy;
        private readonly RuntimeSurface _surface;
        private readonly JourneyFrame _frame;
        private readonly IReadOnlyList<Vec3> _centreline;
        private readonly float _reachLength;

        private float _elapsed;
        private float _startedAt;
        private float _lastMeasuredAt;
        private bool _hasMeasured;

        private float _s;
        private float _lateral;
        private Vector3 _journeyPosition;
        private float _speed;
        private float _deadReckoned;
        private float _snapMetres;

        private SessionToJourney _sessionToJourney;
        private Vector3 _lastSessionPosition;
        private bool _hasLastSessionPosition;

        private VpsTrackingState _rawState = VpsTrackingState.Unavailable;
        private float _rawConfidence;
        private bool _sawCoarseOrBetter;

        public LocalizationMode Mode { get; private set; }
        public AnchorRoute Route { get; private set; }
        public LocalizationFix Fix { get; private set; } = LocalizationFix.None;

        /// <summary>Non-null exactly when <see cref="Mode"/> is <see cref="LocalizationMode.Refused"/>.</summary>
        public string RefusalReason { get; }

        /// <summary>Set when the anchor came from an asset the journey was not authored against.
        /// Localization will work; the beats may be offset. Surfaced, never suppressed.</summary>
        public bool AnchorFrameSuspect { get; private set; }

        public VpsLocalizer(JourneyDocument journey, LocalizationPolicy policy = null,
                            RuntimeSurface surface = RuntimeSurface.Device)
        {
            _journey = journey ?? throw new ArgumentNullException(nameof(journey));
            _policy = policy ?? LocalizationPolicy.Default;
            _surface = surface;
            _frame = JourneyFrame.From(journey.editorFrame);
            _centreline = journey.site?.centreline ?? new List<Vec3>();
            _reachLength = Centreline.Length(_centreline);

            RefusalReason = Gate(journey, surface);
            Mode = RefusalReason == null ? LocalizationMode.Idle : LocalizationMode.Refused;
        }

        /// <summary>
        /// Whether this journey may run on this surface at all. Returns null to permit, or the
        /// reason to refuse.
        ///
        /// The calibration rule is the one that matters and it is not a degradation path: an
        /// uncalibrated editorFrame means the beat coordinates are provisional, and provisional
        /// coordinates against a real anchor put a visitor at the wrong place on a wet bank. The
        /// same journey runs happily in simulation, which is where uncalibrated work belongs.
        /// The rule is also in the schema and in the service validator; it is repeated here
        /// because this is the last place before a person is standing in a creek.
        /// </summary>
        public static string Gate(JourneyDocument journey, RuntimeSurface surface)
        {
            if (journey?.site?.centreline == null || journey.site.centreline.Count < 2)
            {
                return "the journey has no centreline, so there is no reach to measure along";
            }

            if (surface == RuntimeSurface.Device && journey.editorFrame?.calibrated != true)
            {
                return "editorFrame.calibrated is false — the beat coordinates are provisional "
                     + "and must not be run against a real anchor. This journey will run in simulation.";
            }

            if (surface == RuntimeSurface.Device && !JourneyFrame.From(journey.editorFrame).WellFormed)
            {
                return "editorFrame carries a malformed rotation or scale";
            }

            return null;
        }

        /// <summary>
        /// Start, having resolved an anchor. A failed resolution is not an error to swallow: it
        /// means VPS will never produce anything, so the unanchored fallback is entered
        /// immediately rather than after a timeout that we already know the answer to.
        /// </summary>
        public void Begin(AnchorResolution anchor)
        {
            if (Mode == LocalizationMode.Refused) return;

            Route = anchor.Route;
            AnchorFrameSuspect = anchor.Success && !anchor.AssetMatchesJourney;
            _startedAt = _elapsed;

            if (anchor.Success)
            {
                Mode = LocalizationMode.Searching;
                return;
            }

            Mode = LocalizationMode.Failed;
            if (_policy.UnanchoredFallback && !_policy.RequireCoarseForUnanchored) EnterUnanchored();
        }

        public void Tick(in VpsSample sample, float deltaTime)
        {
            if (Mode == LocalizationMode.Refused) return;

            _elapsed += Mathf.Max(0f, deltaTime);
            _rawState = sample.State;
            _rawConfidence = sample.TrackingConfidence;
            _snapMetres = 0f;
            if (sample.State != VpsTrackingState.Unavailable) _sawCoarseOrBetter = true;

            if (Mode == LocalizationMode.Idle) return;

            bool usable = sample.State == VpsTrackingState.Precise
                       && sample.HasAnchorPose
                       && sample.TrackingConfidence >= _policy.MinimumTrackingConfidence;

            if (usable) AcceptMeasured(sample);
            else Degrade(sample, deltaTime);

            TrackSessionPosition(sample);
        }

        /// <summary>
        /// Take a precise fix as the truth, including when it contradicts an estimate.
        ///
        /// Relocalization is genuinely discontinuous — a fix can arrive metres from where we
        /// thought we were — and the temptation is to reject the outliers. That is the wrong
        /// trade here: rejecting a jump means never recovering from a drifted estimate, and the
        /// estimate is the thing with no evidence under it. So the correction is always taken
        /// and its magnitude is reported instead, so audio can crossfade rather than click and
        /// the operator can see that the estimate had drifted.
        /// </summary>
        private void AcceptMeasured(in VpsSample sample)
        {
            var journeyPose = _frame.ToJourney(sample.AnchorPose);
            var projection = Centreline.Project(journeyPose.position, _centreline);
            float s = Mathf.Clamp(projection.S, 0f, _reachLength);

            if (_hasMeasured)
            {
                float gap = _elapsed - _lastMeasuredAt;

                // Only continuous tracking teaches us anything about walking speed. The apparent
                // speed across a dropout is a relocalization jump wearing a velocity costume,
                // and feeding it into the estimate would then propagate it through the next one.
                if (gap > 1e-4f && gap <= _policy.SpeedSampleMaxGapSeconds)
                {
                    float instantaneous = (s - _s) / gap;
                    if (Mathf.Abs(instantaneous) <= _policy.MaxPlausibleSpeedMps)
                    {
                        float alpha = _policy.SpeedSmoothingSeconds <= 0f
                            ? 1f
                            : Mathf.Clamp01(gap / _policy.SpeedSmoothingSeconds);
                        _speed = Mathf.Lerp(_speed, instantaneous, alpha);
                    }
                }
            }

            if (Fix.Source == FixSource.DeadReckoned || Fix.Source == FixSource.Unanchored)
            {
                _snapMetres = Mathf.Abs(s - _s);
            }

            _s = s;
            _lateral = projection.Lateral;
            _journeyPosition = journeyPose.position;
            _lastMeasuredAt = _elapsed - sample.PoseAgeSeconds;
            _hasMeasured = true;
            _deadReckoned = 0f;
            Mode = LocalizationMode.Tracking;

            if (sample.HasSessionPose)
            {
                _sessionToJourney = SessionToJourney.Capture(sample.SessionPose, journeyPose);
            }

            Publish(FixSource.Anchor, DeadReckonBasis.None, LocalizationQuality.Precise,
                    Mathf.Max(0f, sample.PoseAgeSeconds), 1f);
        }

        private void Degrade(in VpsSample sample, float deltaTime)
        {
            if (Mode == LocalizationMode.Unanchored) { AdvanceUnanchored(sample, deltaTime); return; }

            if (!_hasMeasured)
            {
                // Still looking for the first fix. Coarse is reported as Coarse and nothing
                // fires, which is right: Coarse is geolocation-grade, and geolocation error
                // against a 2.5 m gate would fire beats essentially at random. Its real job is
                // to tell us the visitor has arrived at the site.
                float searching = _elapsed - _startedAt;
                bool mayFallBack = _policy.UnanchoredFallback
                                && (!_policy.RequireCoarseForUnanchored || _sawCoarseOrBetter);

                if (searching >= _policy.FirstFixTimeoutSeconds && mayFallBack)
                {
                    EnterUnanchored();
                    AdvanceUnanchored(sample, deltaTime);
                    return;
                }

                Mode = LocalizationMode.Searching;
                Publish(FixSource.None, DeadReckonBasis.None, Map(sample.State), searching, 0f);
                return;
            }

            float age = _elapsed - _lastMeasuredAt;

            if (age <= _policy.HoldSeconds)
            {
                // Below the gate's own noise floor. Hold the last fix exactly as it was rather
                // than reacting to every ordinary gap between VPS requests.
                Publish(FixSource.Anchor, DeadReckonBasis.None, LocalizationQuality.Precise, age, 1f);
                return;
            }

            if (sample.HasSessionPose && _sessionToJourney.Valid && age <= _policy.OdometryHorizonSeconds)
            {
                // The good case, and the reason the session pose is carried on the sample at all.
                // ARKit is still tracking the phone's own motion; only the fix against the site
                // map is gone. Pushing the session pose through the transform cached at the last
                // fix turns a twenty-second dropout into a couple of decimetres of VIO drift.
                //
                // This is measured motion in a drifting frame, not a guess, so it is allowed to
                // move s downstream.
                var journeyPosition = _sessionToJourney.Apply(sample.SessionPose.position);
                var projection = Centreline.Project(journeyPosition, _centreline);
                _s = Mathf.Clamp(projection.S, 0f, _reachLength);
                _lateral = projection.Lateral;
                _journeyPosition = journeyPosition;

                Publish(FixSource.DeadReckoned, DeadReckonBasis.Odometry, LocalizationQuality.Precise,
                        age, _policy.DecayedConfidence(age, _policy.OdometryHorizonSeconds));
                return;
            }

            if (age <= _policy.SpeedHorizonSeconds)
            {
                // Reached when there is no session pose, or when there is one but no fix ever
                // arrived while it was available, so there is no transform to push it through.
                // Blind. Carry the visitor forward at the speed they were last walking, decaying
                // toward standing still, upstream only and hard-capped. This buys a few seconds
                // over a gap in a stride, which is most of what it is for.
                _speed *= Mathf.Exp(-Mathf.Max(0f, deltaTime) / Mathf.Max(1e-3f, _policy.SpeedDecayTauSeconds));
                float step = Mathf.Max(0f, _speed) * Mathf.Max(0f, deltaTime);
                step = Mathf.Min(step, Mathf.Max(0f, _policy.MaxDeadReckonMetres - _deadReckoned));
                _deadReckoned += step;
                _s = Mathf.Clamp(_s + step, 0f, _reachLength);
                _journeyPosition = Centreline.PointAt(_s, _centreline);

                Publish(FixSource.DeadReckoned, DeadReckonBasis.SpeedDecay, LocalizationQuality.Precise,
                        age, _policy.DecayedConfidence(age, _policy.SpeedHorizonSeconds));
                return;
            }

            if (age <= _policy.LostSeconds)
            {
                // Past every horizon that can be justified in metres. Stop claiming: freeze s and
                // report Coarse, which makes JourneyProgression hold whatever beat the visitor is
                // standing in rather than tear it down. A stale position that keeps being sold as
                // Precise is worse than no position, because it will confidently fire the wrong
                // beat, and a beat in the wrong place is the failure this piece cannot absorb.
                Publish(FixSource.DeadReckoned, DeadReckonBasis.Frozen, LocalizationQuality.Coarse, age, 0f);
                return;
            }

            if (_policy.UnanchoredFallback)
            {
                EnterUnanchored();
                AdvanceUnanchored(sample, deltaTime);
                return;
            }

            Publish(FixSource.DeadReckoned, DeadReckonBasis.Frozen, LocalizationQuality.Unavailable, age, 0f);
        }

        private void EnterUnanchored()
        {
            Mode = LocalizationMode.Unanchored;
            _deadReckoned = 0f;
            if (!_hasMeasured) { _s = 0f; _lateral = 0f; }
        }

        /// <summary>
        /// Run the walk with no world frame.
        ///
        /// Preferably on session odometry: ARKit will happily track the phone's own motion with
        /// no VPS at all, so displacement is real even when position is not. The visitor stops,
        /// the piece stops; they walk, it advances. Direction along the creek is unknowable this
        /// way, so all displacement is counted as progress — an assumption that is sound on a
        /// linear reach walked upstream and errs only by however much the visitor wanders.
        ///
        /// Failing that, a clock at a nominal pace, which is the piece as a timed radio play.
        /// Both are reported as Unanchored, both fire beats, and both say so loudly, because the
        /// operator is walking alongside with a controller that can fire any beat by hand.
        /// </summary>
        private void AdvanceUnanchored(in VpsSample sample, float deltaTime)
        {
            float step;
            DeadReckonBasis basis;

            if (sample.HasSessionPose && _hasLastSessionPosition)
            {
                var delta = sample.SessionPose.position - _lastSessionPosition;
                delta.y = 0f;   // stooping to look at the water is not progress upstream
                step = delta.magnitude * _policy.UnanchoredPaceScale;
                basis = DeadReckonBasis.Odometry;
            }
            else
            {
                step = _policy.UnanchoredPaceMps * Mathf.Max(0f, deltaTime);
                basis = DeadReckonBasis.SpeedDecay;
            }

            _s = Mathf.Clamp(_s + Mathf.Max(0f, step), 0f, _reachLength);
            _journeyPosition = Centreline.PointAt(_s, _centreline);
            _lateral = 0f;

            Publish(FixSource.Unanchored, basis, LocalizationQuality.Precise,
                    _hasMeasured ? _elapsed - _lastMeasuredAt : _elapsed - _startedAt, 0f);
        }

        private void TrackSessionPosition(in VpsSample sample)
        {
            if (!sample.HasSessionPose) { _hasLastSessionPosition = false; return; }
            _lastSessionPosition = sample.SessionPose.position;
            _hasLastSessionPosition = true;
        }

        private static LocalizationQuality Map(VpsTrackingState state) => state switch
        {
            VpsTrackingState.Precise => LocalizationQuality.Precise,
            VpsTrackingState.Coarse => LocalizationQuality.Coarse,
            _ => LocalizationQuality.Unavailable,
        };

        private void Publish(FixSource source, DeadReckonBasis basis, LocalizationQuality quality,
                             float age, float confidence)
        {
            Fix = new LocalizationFix(source, basis, quality, _s, _lateral, _journeyPosition,
                                      _speed, Mathf.Max(0f, age), confidence);
        }

        /// <summary>The operator's view. Built on demand rather than every frame — the controller
        /// page is fed at a few hertz, not at frame rate.</summary>
        public LocalizationReport Report() => new(
            Mode, _rawState, _rawConfidence, Fix, Route, _snapMetres, Describe());

        private string Describe()
        {
            string suspect = AnchorFrameSuspect ? " Anchor asset does not match the authored one." : "";

            switch (Mode)
            {
                case LocalizationMode.Refused:
                    return $"Refused: {RefusalReason}";

                case LocalizationMode.Idle:
                    return "Not started.";

                case LocalizationMode.Failed:
                    return "No anchor payload from either route — VPS cannot start.";

                case LocalizationMode.Searching:
                    return _rawState == VpsTrackingState.Coarse
                        ? $"At the site but not localized ({Fix.AgeSeconds:F0} s). Nothing will fire yet.{suspect}"
                        : $"Searching for a fix ({Fix.AgeSeconds:F0} s).{suspect}";

                case LocalizationMode.Unanchored:
                    return Fix.Basis == DeadReckonBasis.Odometry
                        ? $"UNANCHORED — no VPS. Following the walk on phone motion, {Fix.S:F1} m in.{suspect}"
                        : $"UNANCHORED — no VPS and no motion tracking. Running on a clock at "
                          + $"{_policy.UnanchoredPaceMps:F1} m/s, {Fix.S:F1} m in.{suspect}";

                case LocalizationMode.Tracking:
                    string snap = _snapMetres > 0.25f ? $" Corrected {_snapMetres:F1} m." : "";
                    return Fix.Source switch
                    {
                        FixSource.Anchor when Fix.AgeSeconds <= 0.05f =>
                            $"Precise, {Fix.S:F1} m upstream, {Fix.Lateral:F1} m off the line.{snap}{suspect}",
                        FixSource.Anchor =>
                            $"Precise, {Fix.S:F1} m upstream (pose {Fix.AgeSeconds:F1} s old).{snap}{suspect}",
                        FixSource.DeadReckoned when Fix.Basis == DeadReckonBasis.Odometry =>
                            $"VPS lost {Fix.AgeSeconds:F0} s — carried on phone motion at {Fix.S:F1} m.{suspect}",
                        FixSource.DeadReckoned when Fix.Basis == DeadReckonBasis.SpeedDecay =>
                            $"VPS lost {Fix.AgeSeconds:F1} s — estimated at {Fix.S:F1} m from "
                            + $"{Fix.SpeedMps:F1} m/s.{suspect}",
                        _ =>
                            $"VPS lost {Fix.AgeSeconds:F0} s — position frozen at {Fix.S:F1} m, "
                            + $"nothing new will fire.{suspect}",
                    };

                default:
                    return Mode.ToString();
            }
        }
    }
}
