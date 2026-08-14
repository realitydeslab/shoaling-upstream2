using System;
using UnityEngine;
using ShoalingUpstream.Journey;

namespace ShoalingUpstream.Audio
{
    /// <summary>Which of a source's three recordings a gain belongs to.</summary>
    public enum LayerSlot { Far = 0, Mid = 1, Intimate = 2 }

    /// <summary>The three crossfade weights at one distance. They are not normalised: see
    /// <see cref="DistanceField.WeightsAt(float,float)"/>.</summary>
    public readonly struct LayerWeights
    {
        public readonly float Far, Mid, Intimate;
        public LayerWeights(float far, float mid, float intimate) { Far = far; Mid = mid; Intimate = intimate; }
        public float this[LayerSlot slot] => slot switch
        {
            LayerSlot.Far => Far,
            LayerSlot.Mid => Mid,
            _ => Intimate,
        };
        public float Sum => Far + Mid + Intimate;
        public override string ToString() => $"far {Far:F3} mid {Mid:F3} intimate {Intimate:F3}";
    }

    /// <summary>Linear gains, post-crossfade and post-rolloff — what a backend is actually told.</summary>
    public readonly struct LayerGains
    {
        public readonly float Far, Mid, Intimate;
        public LayerGains(float far, float mid, float intimate) { Far = far; Mid = mid; Intimate = intimate; }
        public float this[LayerSlot slot] => slot switch
        {
            LayerSlot.Far => Far,
            LayerSlot.Mid => Mid,
            _ => Intimate,
        };
        public float Sum => Far + Mid + Intimate;
        public static readonly LayerGains Silent = new(0f, 0f, 0f);
        public override string ToString() => $"far {Far:F4} mid {Mid:F4} intimate {Intimate:F4}";
    }

    /// <summary>The authored level of each recording, in dB.</summary>
    public readonly struct LayerGainsDb
    {
        public readonly float Far, Mid, Intimate;
        public readonly bool HasFar, HasMid, HasIntimate;

        public LayerGainsDb(float far, float mid, float intimate,
                            bool hasFar = true, bool hasMid = true, bool hasIntimate = true)
        {
            Far = far; Mid = mid; Intimate = intimate;
            HasFar = hasFar; HasMid = hasMid; HasIntimate = hasIntimate;
        }

        public bool Has(LayerSlot slot) => slot switch
        {
            LayerSlot.Far => HasFar,
            LayerSlot.Mid => HasMid,
            _ => HasIntimate,
        };

        public float this[LayerSlot slot] => slot switch
        {
            LayerSlot.Far => Far,
            LayerSlot.Mid => Mid,
            _ => Intimate,
        };

        public bool Any => HasFar || HasMid || HasIntimate;

        /// <summary>A layer with no clipId contributes nothing, exactly as in the editor, where
        /// a missing spec is skipped rather than played at its default level.</summary>
        public static LayerGainsDb From(BeatAudio audio)
        {
            if (audio == null) return new LayerGainsDb(0, 0, 0, false, false, false);
            return new LayerGainsDb(
                Level(audio.far), Level(audio.mid), Level(audio.intimate),
                Present(audio.far), Present(audio.mid), Present(audio.intimate));
        }

        private static bool Present(AudioLayer layer) => !string.IsNullOrEmpty(layer?.clipId);
        private static float Level(AudioLayer layer) => layer == null ? DistanceField.DefaultGainDb : layer.gainDb;
    }

    /// <summary>
    /// The audible field of a source and its half-life radius — the C# side of
    /// <c>audibleField()</c> in <c>editor/src/audition.ts</c>.
    ///
    /// The half-life is the distance at which amplitude has fallen to half its peak (−6 dB): the
    /// point where a source stops being the thing you are listening to and becomes background.
    /// The editor draws it as a ring, so the number is a claim about the piece, and the app and
    /// the editor disagreeing about it would mean the rings an author composed against were a lie.
    /// </summary>
    public readonly struct AudibleField
    {
        public readonly bool HasField;
        public readonly float Reach, MaxDistance, Peak, PeakAt, Half;

        public AudibleField(bool hasField, float reach, float maxDistance, float peak, float peakAt, float half)
        {
            HasField = hasField; Reach = reach; MaxDistance = maxDistance;
            Peak = peak; PeakAt = peakAt; Half = half;
        }
    }

    /// <summary>
    /// Distance carried by content, not by gain.
    ///
    /// At 5–20 m a source travels two doublings of distance, which under the inverse-square law is
    /// about 12 dB. Against a 47–67 dB(A) creek bank, through headphones that are deliberately not
    /// noise-cancelling, 12 dB reads as "slightly louder" and never as arrival. So approaching a
    /// beat crossfades between three *different recordings* — far, mid, intimate — and the fader
    /// barely moves.
    ///
    /// Every number here is a port of the law in <c>editor/src/audition.ts</c>, so that what is
    /// heard at a desk through Resonance is what is heard on the bank through PHASE. That is why
    /// the crossfade lives in plain C# and not in the native bridge: it has to be one law, shared
    /// by both backends and testable without a device.
    ///
    /// Deliberately Unity-free apart from Mathf, so the whole thing runs in EditMode.
    /// </summary>
    public static class DistanceField
    {
        /// <summary>Reach when a source has neither an explicit radius nor a trigger.</summary>
        public const float DefaultExitRadiusM = 6f;

        /// <summary>A beat's sound carries several times further than the radius at which its
        /// interaction arms. Two beats whose trigger rings do not touch are still audible over
        /// each other the whole way between them.</summary>
        public const float ReachPerExitRadius = 3.5f;

        public const float DefaultGainDb = -8f;

        /// <summary>Matches <c>source.setMinDistance(1)</c>: inside a metre nothing attenuates.</summary>
        public const float MinDistanceM = 1f;

        public const float MaxDistanceFloorM = 12f;
        public const float MaxDistanceFactor = 1.6f;

        /// <summary>
        /// Past this multiple of the reach a source is stopped outright rather than left running
        /// at a vanishing gain. On the shipped garden settings the rolloff has already reached
        /// about −53 dB there, which is far below anything audible over a creek, so the cut is
        /// silent — and stopping the players is what keeps voice count proportional to what is
        /// actually in earshot.
        /// </summary>
        public const float CullFactor = 1.35f;

        /// <summary>The whole inverse-square level budget across 5–20 m. Stated as a constant
        /// because it is the design's central claim, and a crossfade that quietly exceeded it
        /// would have gone back to carrying distance with the fader.</summary>
        public const float LevelBudgetDb = 12f;

        // The crossfade breakpoints, in normalised distance (distance / reach). Intimate holds
        // alone to 0.15 and is gone by 0.35; mid peaks at 0.5; far takes over from 0.45 and owns
        // everything past 0.85. The bands overlap everywhere, so there is no distance at which
        // the source falls silent between recordings.
        private const double IntimateSpan = 0.35;
        private const double MidCentre = 0.5;
        private const double MidSpan = 0.35;
        private const double FarOnset = 0.45;
        private const double FarSpan = 0.4;

        /// <summary>Sampling step for the field scan. Matches the editor's 5 cm so the half-life
        /// lands on the same value rather than one step away from it.</summary>
        private const double FieldStep = 0.05;

        public static float DbToLinear(float db) => Mathf.Pow(10f, db / 20f);
        public static float LinearToDb(float linear) => linear <= 0f ? -200f : 20f * Mathf.Log10(linear);

        public static float ReachFor(Beat beat) =>
            ReachFor(beat?.trigger, 0f);

        public static float ReachFor(AmbientSource ambient) =>
            ReachFor(null, ambient?.audibleRadiusM ?? 0f);

        /// <summary>An explicit <c>audibleRadiusM</c> wins; otherwise the reach is derived from
        /// the gate geometry, which is what the editor does.</summary>
        public static float ReachFor(Trigger trigger, float audibleRadiusM)
        {
            if (audibleRadiusM > 0f) return audibleRadiusM;
            float exit = trigger != null && trigger.exitRadiusM > 0f ? trigger.exitRadiusM : DefaultExitRadiusM;
            return exit * ReachPerExitRadius;
        }

        public static float MaxDistanceFor(float reach) =>
            Mathf.Max(MaxDistanceFloorM, reach * MaxDistanceFactor);

        public static LayerWeights WeightsAt(float distance, float reach)
        {
            var (far, mid, intimate) = Weights(distance, reach);
            return new LayerWeights((float)far, (float)mid, (float)intimate);
        }

        /// <summary>
        /// Resonance Audio's own 'logarithmic' attenuation, which is not a logarithm.
        ///
        /// It is 1/(d+1) offset by the minimum distance and renormalised so it reaches exactly
        /// zero at the maximum. Assuming a real logarithm from the name put the editor's
        /// half-life out by 20%, which is why the shape is pinned rather than described.
        ///
        /// We apply it ourselves rather than letting an engine apply its own, and set the
        /// backend's rolloff to unity. PHASE only exposes a <c>rolloffFactor</c> scalar, so
        /// leaving distance to the engine would mean the desk and the bank disagreeing about the
        /// one law the whole sound design rests on.
        /// </summary>
        public static float RolloffAt(float distance, float maxDistance) =>
            (float)Rolloff(distance, maxDistance);

        /// <summary>Per-layer linear gain at a distance: the crossfade weight times the authored
        /// level, times the distance rolloff. This is exactly what the editor writes into each
        /// layer's GainNode before Resonance spatialises it.</summary>
        public static LayerGains GainsAt(float distance, float reach, in LayerGainsDb db)
        {
            var (far, mid, intimate) = Weights(distance, reach);
            double rolloff = Rolloff(distance, MaxDistanceFor(reach));
            return new LayerGains(
                (float)(Contribution(db, LayerSlot.Far, far) * rolloff),
                (float)(Contribution(db, LayerSlot.Mid, mid) * rolloff),
                (float)(Contribution(db, LayerSlot.Intimate, intimate) * rolloff));
        }

        /// <summary>The crossfade alone, before any distance attenuation. This is the number the
        /// 12 dB budget is a statement about: it is what the *content* is doing.</summary>
        public static float ContentGainAt(float distance, float reach, in LayerGainsDb db)
        {
            var (far, mid, intimate) = Weights(distance, reach);
            return (float)(Contribution(db, LayerSlot.Far, far)
                         + Contribution(db, LayerSlot.Mid, mid)
                         + Contribution(db, LayerSlot.Intimate, intimate));
        }

        public static float AmplitudeAt(float distance, float reach, in LayerGainsDb db) =>
            (float)Amplitude(distance, reach, db);

        /// <summary>
        /// Scan the law for its peak and its half-life, rather than deriving them.
        ///
        /// Sampled, not solved, for the same reason the editor samples: the summed crossfade has a
        /// second lobe where the mid recording fades in, so there is no closed form for "the first
        /// distance at which this has halved", and a rule of thumb here would silently drift away
        /// from whatever the law actually does.
        /// </summary>
        public static AudibleField Field(float reach, in LayerGainsDb db)
        {
            double maxD = MaxDistanceFor(reach);

            double peak = 0d, peakAt = 0d;
            for (double d = 0d; d <= maxD; d += FieldStep)
            {
                double a = Amplitude(d, reach, db);
                if (a > peak) { peak = a; peakAt = d; }
            }
            if (peak <= 0d) return new AudibleField(false, reach, (float)maxD, 0f, 0f, 0f);

            double half = maxD;
            for (double d = peakAt; d <= maxD; d += FieldStep)
            {
                if (Amplitude(d, reach, db) <= peak * 0.5d) { half = d; break; }
            }

            return new AudibleField(true, reach, (float)maxD,
                (float)peak, (float)peakAt, (float)Math.Round(half, 2));
        }

        // Doubles throughout the field scan, and in the weights it calls, because the editor
        // computes in JavaScript numbers and the tests assert the two agree to a hundredth of a
        // metre. Float accumulation over ~340 steps drifts past that on its own.
        private static double Amplitude(double distance, double reach, in LayerGainsDb db)
        {
            var (far, mid, intimate) = Weights(distance, reach);
            double a = Contribution(db, LayerSlot.Far, far)
                     + Contribution(db, LayerSlot.Mid, mid)
                     + Contribution(db, LayerSlot.Intimate, intimate);
            return a * Rolloff(distance, MaxDistanceFor((float)reach));
        }

        private static double Contribution(in LayerGainsDb db, LayerSlot slot, double weight) =>
            db.Has(slot) ? Math.Pow(10d, db[slot] / 20d) * weight : 0d;

        private static (double far, double mid, double intimate) Weights(double distance, double reach)
        {
            double n = Math.Min(1d, distance / Math.Max(reach, 1e-3d));
            return (
                Clamp01((n - FarOnset) / FarSpan),
                Clamp01(1d - Math.Abs(n - MidCentre) / MidSpan),
                Clamp01(1d - n / IntimateSpan));
        }

        private static double Rolloff(double distance, double maxDistance)
        {
            if (distance > maxDistance) return 0d;
            if (distance <= MinDistanceM) return 1d;
            double range = maxDistance - MinDistanceM;
            double att = 1d / (distance - MinDistanceM + 1d);
            double attMax = 1d / (range + 1d);
            return Math.Max(0d, (att - attMax) / (1d - attMax));
        }

        private static double Clamp01(double v) => v < 0d ? 0d : v > 1d ? 1d : v;
    }
}
