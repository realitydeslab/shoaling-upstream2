using UnityEngine;

namespace ShoalingUpstream.Gestures
{
    /// <summary>
    /// Reads the three body gestures from phone height and motion.
    ///
    /// All three are detected from *relative* vertical displacement over a short window, never
    /// from absolute altitude. The reach only rises about 2.3 m end to end, which is inside
    /// ordinary barometric drift across a twenty-minute session — so absolute height tells us
    /// nothing, while a half-second delta tells us everything.
    ///
    /// Everything here is pure maths over a sample stream, so it runs in EditMode tests against
    /// synthetic motion. Asking someone to repeatedly crouch beside a creek to test a threshold
    /// is a poor use of a person.
    /// </summary>
    public class GestureDetector
    {
        public enum Gesture { None, Crouch, Lift, Lunge }

        // --- crouch -------------------------------------------------------
        // A crouch at the gravel bed: drop roughly 0.4-0.5 m and stay down long enough to look.
        public float CrouchDropM = 0.35f;
        public float CrouchHoldSeconds = 0.6f;

        // --- lift ---------------------------------------------------------
        // Not a jump. Lifting yourself up onto something, about 0.4 m, and staying there.
        //
        // That makes it a *plateau*, not a ballistic arc, which is genuinely easier to detect
        // than a jump: there is no free-fall signature to catch inside 100 ms, just a sustained
        // rise. It is also far more dignified in a public park and much safer on a wet bank,
        // and because it can be held, the audio can hold "out of the water" for as long as the
        // visitor stays up rather than firing a 400 ms one-shot.
        public float LiftRiseM = 0.28f;
        public float LiftHoldSeconds = 0.5f;

        // --- lunge --------------------------------------------------------
        // Taking an insect off the surface: a short forward committal and a stop, which is what
        // a trout actually does. Replaces tapping the screen, which is a button press wearing a
        // fish costume.
        public float LungeSpeedMps = 0.75f;
        public float LungeStopMps = 0.22f;
        public float LungeWindowSeconds = 1.2f;

        /// <summary>Reference height, tracked slowly so it follows terrain but not gestures.</summary>
        public float Baseline { get; private set; }
        public bool HasBaseline { get; private set; }

        /// <summary>True while the visitor is holding themselves up. The audio reads this
        /// continuously, so "out of the water" lasts as long as they do.</summary>
        public bool IsLifted { get; private set; }
        public bool IsCrouched { get; private set; }

        private float _belowFor;
        private float _aboveFor;
        private float _lungeTimer;
        private bool _lungeArmed;
        private float _cooldown;
        private bool _crouchLatched;
        private bool _liftLatched;
        private float _gestureReference;

        /// <summary>
        /// The baseline always follows, never freezes.
        ///
        /// Freezing it whenever the visitor is displaced seems right and is badly wrong: walking
        /// up a sloping bank displaces you permanently, so the baseline sticks, the offset grows
        /// without limit, and the climb reads as one lift after another. Instead it tracks
        /// continuously on a timescale slow enough that a sub-second gesture barely moves it
        /// (a 0.45 m crouch loses ~0.06 m of contrast) but fast enough that a real slope never
        /// accumulates past a threshold — a 2.3 m climb over a minute settles at ~0.15 m of lag,
        /// comfortably under the 0.28 m lift trigger.
        /// </summary>
        private const float BaselineTauSeconds = 4f;
        private const float CooldownSeconds = 1.5f;

        public void Reset()
        {
            HasBaseline = false;
            IsLifted = IsCrouched = false;
            _belowFor = _aboveFor = _lungeTimer = _cooldown = 0f;
            _lungeArmed = _crouchLatched = _liftLatched = false;
            _gestureReference = 0f;
        }

        /// <param name="height">Phone height, metres, in any consistent frame.</param>
        /// <param name="horizontalSpeed">Horizontal speed, m/s.</param>
        public Gesture Tick(float height, float horizontalSpeed, float deltaTime)
        {
            if (deltaTime <= 0f) return Gesture.None;

            if (!HasBaseline)
            {
                Baseline = height;
                HasBaseline = true;
                return Gesture.None;
            }

            float offset = height - Baseline;

            float alpha = 1f - Mathf.Exp(-deltaTime / BaselineTauSeconds);
            Baseline += (height - Baseline) * alpha;

            if (_cooldown > 0f) _cooldown -= deltaTime;

            // One gesture per excursion. The visitor has to come back towards standing before
            // the same gesture can fire again, so holding a crouch reports once rather than
            // repeating every time a cooldown lapses.
            float rearmBand = Mathf.Min(CrouchDropM, LiftRiseM) * 0.5f;
            if (offset > -rearmBand) _crouchLatched = false;
            if (offset < rearmBand) _liftLatched = false;

            // --- crouch / lift --------------------------------------------
            _belowFor = offset <= -CrouchDropM ? _belowFor + deltaTime : 0f;
            _aboveFor = offset >= LiftRiseM ? _aboveFor + deltaTime : 0f;

            // The *sustained* states are held against a reference frozen at the moment the
            // gesture fired, not against the live baseline. The baseline has to keep drifting
            // to cope with a sloping bank, and if the held state read from it, staying up on a
            // rock would quietly "become" the new ground after a few seconds and the world
            // would drop back underwater while the visitor was still standing on it.
            if (IsLifted && height < _gestureReference + LiftRiseM * 0.5f) IsLifted = false;
            if (IsCrouched && height > _gestureReference - CrouchDropM * 0.5f) IsCrouched = false;

            // --- lunge ----------------------------------------------------
            if (horizontalSpeed >= LungeSpeedMps)
            {
                _lungeArmed = true;
                _lungeTimer = 0f;
            }
            else if (_lungeArmed)
            {
                _lungeTimer += deltaTime;
                if (horizontalSpeed <= LungeStopMps)
                {
                    _lungeArmed = false;
                    if (_cooldown <= 0f)
                    {
                        _cooldown = CooldownSeconds;
                        return Gesture.Lunge;
                    }
                }
                else if (_lungeTimer > LungeWindowSeconds)
                {
                    // Committed forward but never stopped — that is walking, not a lunge.
                    _lungeArmed = false;
                }
            }

            if (_cooldown > 0f) return Gesture.None;

            // Tuned to miss rather than to over-fire. A missed gesture costs a retry; a spurious
            // one spawns eggs while somebody ties a shoelace.
            if (_belowFor >= CrouchHoldSeconds && !_crouchLatched)
            {
                _belowFor = 0f;
                _crouchLatched = true;
                _cooldown = CooldownSeconds;
                _gestureReference = Baseline;
                IsCrouched = true;
                return Gesture.Crouch;
            }
            if (_aboveFor >= LiftHoldSeconds && !_liftLatched)
            {
                _aboveFor = 0f;
                _liftLatched = true;
                _cooldown = CooldownSeconds;
                _gestureReference = Baseline;
                IsLifted = true;
                return Gesture.Lift;
            }

            return Gesture.None;
        }
    }
}
