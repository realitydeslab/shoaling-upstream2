using UnityEngine;

namespace ShoalingUpstream.Localization
{
    /// <summary>
    /// Every number the degradation ladder turns on, with the reasoning that produced it.
    ///
    /// The governing quantity is not seconds, it is <b>metres of error against a gate radius</b>.
    /// Beat gates are 2.5-4 m of enter radius (see the seeded journeys and
    /// JourneyProgressionTests). A position is worth acting on while its likely error is inside
    /// that band and worth nothing once it is outside, because past that point it will fire the
    /// wrong beat or fire the right beat in the wrong place — and a sound arriving in the wrong
    /// place is the one failure this piece cannot absorb.
    ///
    /// So every horizon below is a seconds figure derived from an error budget of about 2 m at a
    /// walking pace of 0.7-1.0 m/s. They are defaults, not measurements: real time-to-lock and
    /// real dropout length under canopy over moving water are unknown until a field session, and
    /// they are the project's dominant risk (docs/nsdk-api-notes.md).
    /// </summary>
    public class LocalizationPolicy
    {
        // --- staleness ladder ---------------------------------------------

        /// <summary>
        /// How long a lost pose is simply held before anything is done about it.
        ///
        /// Under 1.5 s the visitor has moved about a metre, which is inside the noise the gate
        /// hysteresis already absorbs. Reacting sooner would mean reacting to every ordinary
        /// gap between VPS requests.
        /// </summary>
        public float HoldSeconds = 1.5f;

        /// <summary>
        /// How long a dropout may be carried on AR session odometry.
        ///
        /// VIO drifts at roughly a percent of distance travelled. Twenty seconds of walking is
        /// about 18 m, so a fifth of a metre of drift — an order of magnitude inside the gate.
        /// The real limit is rotational drift rather than translational, which is why this is
        /// twenty seconds and not two minutes.
        /// </summary>
        public float OdometryHorizonSeconds = 20f;

        /// <summary>
        /// How long a dropout may be carried on a decayed speed estimate, when there is no
        /// odometry at all.
        ///
        /// This is the blind case and it is bounded much harder. At 0.9 m/s decaying with
        /// <see cref="SpeedDecayTauSeconds"/> = 3 s, five seconds of propagation covers
        /// 0.9 x 3 x (1 - e^-5/3) = 2.2 m — just inside the smallest gate, and no further.
        /// </summary>
        public float SpeedHorizonSeconds = 5f;

        /// <summary>
        /// How long we keep claiming to be localized at all before declaring the fix lost.
        ///
        /// Between the horizons above and this, the position is frozen and reported as Coarse:
        /// JourneyProgression will hold whatever beat the visitor is standing in rather than
        /// tearing it down, which is already its behaviour for a non-Precise quality. Thirty
        /// seconds is long enough for someone to stop, look at something and walk on.
        /// </summary>
        public float LostSeconds = 30f;

        /// <summary>
        /// How long to wait for the first precise fix before running the walk unanchored.
        ///
        /// Time-to-lock is unmeasured. Forty-five seconds is long enough that we are not giving
        /// up on a slow lock, and short enough that a visitor is not standing in a park in
        /// silence wondering whether the piece is broken.
        /// </summary>
        public float FirstFixTimeoutSeconds = 45f;

        // --- dead reckoning ------------------------------------------------

        /// <summary>
        /// Time constant for decaying the speed estimate once the pose is lost.
        ///
        /// Decay rather than hold, because the most likely reason localization dropped is that
        /// the visitor stopped and turned to look at something, or pointed the phone at moving
        /// water. Holding the last speed through that would walk them to the headwater in
        /// forty-five seconds of blindness. Decaying to zero parks them roughly where they are,
        /// which is the low-error answer when the assumption underneath has broken.
        /// </summary>
        public float SpeedDecayTauSeconds = 3f;

        /// <summary>
        /// Hard cap on how far a single blind dropout may advance s, whatever the arithmetic
        /// says. The decay already bounds this asymptotically at speed x tau; the cap is what
        /// stops a fast walker, or a bug, from covering a whole beat spacing unobserved.
        /// </summary>
        public float MaxDeadReckonMetres = 3f;

        /// <summary>Speeds above this are pose noise, not walking, and must not enter the
        /// estimate that a dropout will then be propagated with.</summary>
        public float MaxPlausibleSpeedMps = 2.5f;

        /// <summary>Only consecutive precise frames closer together than this contribute to the
        /// speed estimate. A gap implies a dropout, and the implied speed across a dropout is a
        /// relocalization jump rather than a walk.</summary>
        public float SpeedSampleMaxGapSeconds = 0.5f;

        /// <summary>Smoothing on the speed estimate. Differentiating s frame by frame is
        /// useless; a one-second window is roughly a stride.</summary>
        public float SpeedSmoothingSeconds = 1f;

        // --- unanchored fallback -------------------------------------------

        /// <summary>
        /// Whether the piece runs at all when it cannot localize.
        ///
        /// On by default. A visitor standing in a park with nothing happening is a failed
        /// artwork, and the operator is walking beside them with a controller that can fire any
        /// beat by hand. Turn it off for an unattended installation, where a silent failure is
        /// preferable to a confidently misplaced one.
        /// </summary>
        public bool UnanchoredFallback = true;

        /// <summary>
        /// Whether unanchored mode waits for at least a Coarse fix — geolocation-grade evidence
        /// that the visitor is actually at the site.
        ///
        /// Off by default, for the same reason: on a guided walk the operator already knows
        /// where the visitor is standing, and refusing to run because the phone could not
        /// confirm it would fail the artwork for exactly the reason the fallback exists. Worth
        /// turning on if the piece is ever left running unattended.
        /// </summary>
        public bool RequireCoarseForUnanchored = false;

        /// <summary>Nominal walking pace used when unanchored with no odometry either — the
        /// piece as a timed radio play. Slow: overshooting the reach is worse than lagging it,
        /// because a beat fired early cannot be un-fired.</summary>
        public float UnanchoredPaceMps = 0.7f;

        /// <summary>Fraction of measured session displacement that counts as progress upstream
        /// when unanchored. Under one because some of any walk is wandering, turning and
        /// stepping aside, none of which is progress along the reach.</summary>
        public float UnanchoredPaceScale = 0.9f;

        // --- confidence -----------------------------------------------------

        /// <summary>
        /// Floor on <c>ARVps2Anchor.trackingConfidence</c> below which a Precise fix is not
        /// believed.
        ///
        /// Zero — i.e. off — deliberately. The SDK documents the field only as "positive number
        /// representing confidence"; its scale and distribution are unknown. A threshold against
        /// an unknown scale would either do nothing or silently reject every fix in the field,
        /// and there would be no way to tell which from inside the app. The raw value is
        /// reported to the operator on every frame so that a session's worth of it can be read
        /// off and this number set from evidence.
        /// </summary>
        public float MinimumTrackingConfidence = 0f;

        public static LocalizationPolicy Default => new();

        /// <summary>Confidence in a dead-reckoned position: full at the moment of the fix,
        /// nothing at the horizon it is being propagated to.</summary>
        public float DecayedConfidence(float ageSeconds, float horizonSeconds) =>
            horizonSeconds <= 0f ? 0f : Mathf.Clamp01(1f - ageSeconds / horizonSeconds);
    }
}
