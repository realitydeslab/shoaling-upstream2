using UnityEngine;

namespace ShoalingUpstream.Audio
{
    /// <summary>What the envelope reported this tick. Only the edges are events.</summary>
    public enum MediumTransition { None, LeftWater, ReenteredWater }

    /// <summary>
    /// Being out of the water, for as long as the visitor stays up.
    ///
    /// The barrier interaction is a 40 cm lift — hauling yourself onto something and staying
    /// there — not a jump. That is a sustained plateau rather than a ballistic arc, which means
    /// the audio must be able to *hold* "out of the water" indefinitely. A one-shot would be
    /// wrong twice over: it would end while the visitor was still up, and it would describe the
    /// moment rather than the state.
    ///
    /// What changes is the medium, not an object. Leaving the water takes the whole field dry and
    /// bright at once — reverb send to nothing, low-pass wide open — which needs no learning from
    /// anyone who has ever surfaced in a swimming pool. The alternative, a confirmation sound for
    /// "you lifted", would be an earcon: measurably around seven times the learning cost of a
    /// causal sound, and a synthetic beep in a creek breaks the fiction that the visitor is a fish.
    ///
    /// The splash belongs to re-entry and is raised at the instant the visitor comes down, not
    /// when the filter finishes moving: the splash is the cause and the filter is the consequence,
    /// and firing it at the end of the sweep would put it audibly late.
    /// </summary>
    public class MediumEnvelope
    {
        /// <summary>Underwater is dull. This is where the field sits with the visitor in the creek.</summary>
        public const float WaterCutoffHz = 1400f;

        /// <summary>Out of the water the field is unfiltered.</summary>
        public const float AirCutoffHz = 18000f;

        /// <summary>PHASE has one global reverb, so the send is the only per-moment control there
        /// is over the space. Driving it to zero out of the water is the whole cue.</summary>
        public const float WaterReverbSend = 0.55f;

        // Leaving is quicker than returning. Surfacing is abrupt; sinking back into the water has
        // a settling to it. Both are short enough to fuse with the body movement that caused them
        // and long enough not to click.
        private const float RiseTauSeconds = 0.05f;
        private const float FallTauSeconds = 0.08f;

        private bool _outOfWater;
        private bool _reported;
        private float _blend;

        /// <summary>0 fully submerged, 1 fully out. Holds at 1 for as long as the visitor is up.</summary>
        public float AirBlend => _blend;

        public bool IsOutOfWater => _outOfWater;

        public float LowPassHz => LogLerp(WaterCutoffHz, AirCutoffHz, _blend);

        public float ReverbSend => Mathf.Lerp(WaterReverbSend, 0f, _blend);

        public void Reset()
        {
            _outOfWater = false;
            _reported = true;
            _blend = 0f;
        }

        /// <summary>Idempotent: called every frame from the gesture detector's held state, so
        /// staying up must not re-raise the transition.</summary>
        public void SetOutOfWater(bool outOfWater)
        {
            if (outOfWater == _outOfWater) return;
            _outOfWater = outOfWater;
            _reported = false;
        }

        public MediumTransition Tick(float deltaTime)
        {
            float target = _outOfWater ? 1f : 0f;
            if (deltaTime > 0f)
            {
                float tau = _outOfWater ? RiseTauSeconds : FallTauSeconds;
                _blend += (target - _blend) * (1f - Mathf.Exp(-deltaTime / tau));
            }

            if (_reported) return MediumTransition.None;
            _reported = true;
            return _outOfWater ? MediumTransition.LeftWater : MediumTransition.ReenteredWater;
        }

        /// <summary>
        /// Cutoff interpolates logarithmically, never linearly.
        ///
        /// A linear sweep spends almost all its travel in the top octave, where nothing is
        /// audible, and produces "extreme and unnatural sounding results"; a logarithmic one is
        /// perceptually even. Halfway between 1.4 kHz and 18 kHz is 5 kHz, not 9.7 kHz.
        /// </summary>
        public static float LogLerp(float from, float to, float t) =>
            Mathf.Exp(Mathf.Lerp(Mathf.Log(from), Mathf.Log(to), Mathf.Clamp01(t)));
    }
}
