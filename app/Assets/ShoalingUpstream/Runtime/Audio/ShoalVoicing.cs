using System;
using System.Collections.Generic;
using UnityEngine;
using ShoalingUpstream.Journey;

namespace ShoalingUpstream.Audio
{
    /// <summary>Everything a backend needs to render the shoal at one instant.</summary>
    public readonly struct ShoalVoicingState
    {
        /// <summary>How many decorrelated copies of the bed are sounding. This is the headcount cue.</summary>
        public readonly int Voices;

        /// <summary>Total angular spread of those voices around the visitor, in degrees.</summary>
        public readonly float UnisonWidthDeg;

        /// <summary>Per-voice decorrelation delay. Longer delays individuate.</summary>
        public readonly float VoiceDelayMs;

        public readonly int BedLow, BedHigh;

        /// <summary>0 at <see cref="BedLow"/>, 1 at <see cref="BedHigh"/>.</summary>
        public readonly float BedBlend;

        public readonly float GainDb;

        /// <summary>0 at the floor, 1 at the starting count, logarithmic in between.</summary>
        public readonly float Density;

        public ShoalVoicingState(int voices, float unisonWidthDeg, float voiceDelayMs,
                                 int bedLow, int bedHigh, float bedBlend, float gainDb, float density)
        {
            Voices = voices; UnisonWidthDeg = unisonWidthDeg; VoiceDelayMs = voiceDelayMs;
            BedLow = bedLow; BedHigh = bedHigh; BedBlend = bedBlend;
            GainDb = gainDb; Density = density;
        }

        public override string ToString() =>
            $"{Voices} voices, {UnisonWidthDeg:F0}deg, beds {BedLow}/{BedHigh} @ {BedBlend:F2}, {GainDb:F1} dB";
    }

    /// <summary>
    /// The visitor is a shoal, and the shoal has to be heard rather than read.
    ///
    /// **Why voices and not level.** A pre-rendered bed at a given grain density already encodes
    /// how busy the cloud is, but density alone reads as "busier", not as "more of them" — and
    /// level alone reads as "it got quieter", which is the failure the sound design most needs to
    /// avoid, because the shoal only shrinks when the visitor gives fish away and that has to land
    /// as loss. What makes a crowd read as a crowd is *decorrelation*: several copies of the same
    /// texture arriving from slightly different directions with slightly different micro-delays.
    /// So the parameter that carries headcount is the number of decorrelated voices, and it is a
    /// small integer because a small integer is exactly what the ear can count. Past about eight
    /// the ear stops counting and hears "many", so the ladder saturates there rather than tracking
    /// forty fish with forty voices.
    ///
    /// **Why logarithmic.** Auditory numerosity is Weber-like: going from six fish to twelve is
    /// the same perceived change as twelve to twenty-four. Mapping count linearly onto voices
    /// would spend the whole resolution at the top of the range, where it cannot be heard, and
    /// none at the bottom, where every fish matters. So the ladder is geometric and everything
    /// downstream is driven by the log-normalised density.
    ///
    /// **Why three cues move together.** Density drops, extent narrows, individuation rises. The
    /// third is counter-intuitive and is the one that works: a smaller group is *more* legible as
    /// individuals, so lengthening the decorrelation delay until single fish poke out of the
    /// texture is what makes people say "there are fewer of them" rather than "it got quieter".
    ///
    /// The beds themselves must be the same grain cloud rendered at different densities from the
    /// same seed. If they are separate recordings the transition sounds like a crossfade between
    /// two takes rather than like loss.
    /// </summary>
    public class ShoalVoicing
    {
        public const int MinVoices = 2;
        public const int MaxVoices = 8;

        public const float NarrowWidthDeg = 25f;
        public const float WideWidthDeg = 140f;

        /// <summary>Decorrelation delay at the floor. Kept under the ~50 ms echo threshold: past
        /// it the copies stop fusing into one cloud and start sounding like a slapback.</summary>
        public const float MaxVoiceDelayMs = 45f;

        /// <summary>
        /// The entire level range across the whole shoal, from forty fish to six.
        ///
        /// Three decibels is deliberately almost nothing. Thinning is carried by voices, width and
        /// individuation; if it were carried by level the visitor would hear a fader move and read
        /// it as the piece getting quieter rather than as their shoal being taken.
        /// </summary>
        public const float QuietGainDb = -9f;
        public const float FullGainDb = -6f;

        // Fast on the loss, slow on the settle. The 300 ms drop is what makes the thinning
        // unmistakably *caused by* the heron rather than a coincidence; instant-and-permanent
        // reads as a bug, and slow-and-gradual reads as unrelated to what just happened. The
        // group then takes several seconds to reorganise around its new size.
        private const float DropTauSeconds = 0.13f;   // ~95% inside 400 ms

        /// <summary>Growth is not the mirror of loss. Spawning is a widening, not a jolt, and
        /// findings put it at about two seconds.</summary>
        private const float GrowTauSeconds = 0.7f;

        private const float SettleTauSeconds = 2.5f;  // ~95% inside 8 s

        private readonly int _startingCount;
        private readonly int _minimumCount;
        private readonly int[] _beds;
        private readonly float _logSpan;

        private int _count;
        private float _targetDensity;
        private float _density;
        private float _settled;

        public ShoalVoicing(Shoal shoal, int bedCount = 5)
        {
            _startingCount = Mathf.Max(1, shoal?.startingCount ?? 40);
            _minimumCount = Mathf.Clamp(shoal?.minimumCount ?? 6, 1, _startingCount);
            _beds = BuildBeds(_minimumCount, _startingCount, Mathf.Max(2, bedCount));

            // A journey whose floor equals its start can never thin, so the ladder collapses to a
            // single rung and density is pinned at 1 rather than dividing by zero.
            _logSpan = _startingCount > _minimumCount
                ? Mathf.Log((float)_startingCount / _minimumCount)
                : 0f;

            Reset(_startingCount);
        }

        /// <summary>The density ladder, smallest first. One pre-rendered bed per rung.</summary>
        public IReadOnlyList<int> Beds => _beds;

        public int Count => _count;
        public ShoalVoicingState State { get; private set; }

        /// <summary>Clip id convention for a bed. The renders do not exist yet; a resolver that
        /// cannot find them leaves the shoal silent rather than substituting the wrong size.</summary>
        public static string BedClipId(int bedCount) => $"shoal--{bedCount}";

        /// <summary>Jump straight to a count with no transition. Used at the start of a walk,
        /// where there is nothing to have caused a change.</summary>
        public void Reset(int count)
        {
            _count = Mathf.Clamp(count, _minimumCount, _startingCount);
            _targetDensity = DensityFor(_count);
            _density = _targetDensity;
            _settled = _targetDensity;
            State = Compose(_density, _settled);
        }

        public void SetCount(int count)
        {
            _count = Mathf.Clamp(count, _minimumCount, _startingCount);
            _targetDensity = DensityFor(_count);
        }

        public ShoalVoicingState Tick(float deltaTime)
        {
            if (deltaTime > 0f)
            {
                float tau = _targetDensity < _density ? DropTauSeconds : GrowTauSeconds;
                _density = Approach(_density, _targetDensity, tau, deltaTime);
                _settled = Approach(_settled, _targetDensity, SettleTauSeconds, deltaTime);
            }
            State = Compose(_density, _settled);
            return State;
        }

        private ShoalVoicingState Compose(float density, float settled)
        {
            // Bed selection and level ride the fast parameter — that is the drop you hear at the
            // moment of donation. Voices, width and individuation ride the slow one, so the group
            // is still visibly reorganising for several seconds afterwards.
            float rung = density * (_beds.Length - 1);
            int low = Mathf.Clamp(Mathf.FloorToInt(rung), 0, _beds.Length - 1);
            int high = Mathf.Min(low + 1, _beds.Length - 1);

            int voices = Mathf.RoundToInt(Mathf.Lerp(MinVoices, MaxVoices, settled));

            return new ShoalVoicingState(
                voices,
                Mathf.Lerp(NarrowWidthDeg, WideWidthDeg, settled),
                Mathf.Lerp(MaxVoiceDelayMs, 0f, settled),
                _beds[low], _beds[high],
                low == high ? 0f : rung - low,
                Mathf.Lerp(QuietGainDb, FullGainDb, density),
                density);
        }

        private float DensityFor(int count) =>
            _logSpan <= 0f ? 1f : Mathf.Clamp01(Mathf.Log((float)count / _minimumCount) / _logSpan);

        private static float Approach(float current, float target, float tau, float dt) =>
            current + (target - current) * (1f - Mathf.Exp(-dt / tau));

        /// <summary>Geometrically spaced rungs from the floor to the starting count, so each step
        /// is the same perceived change in numerosity rather than the same arithmetic one.</summary>
        private static int[] BuildBeds(int minimum, int starting, int rungs)
        {
            if (starting <= minimum) return new[] { minimum };

            var beds = new int[rungs];
            float ratio = Mathf.Pow((float)starting / minimum, 1f / (rungs - 1));
            for (int i = 0; i < rungs; i++) beds[i] = Mathf.RoundToInt(minimum * Mathf.Pow(ratio, i));

            beds[0] = minimum;
            beds[rungs - 1] = starting;

            // Rounding can collide two rungs on a narrow range; a duplicate bed would mean a
            // crossfade between a file and itself, which is a silent no-op rather than a change.
            for (int i = 1; i < rungs; i++)
                if (beds[i] <= beds[i - 1]) beds[i] = beds[i - 1] + 1;
            beds[rungs - 1] = Mathf.Max(beds[rungs - 1], starting);

            return beds;
        }
    }
}
