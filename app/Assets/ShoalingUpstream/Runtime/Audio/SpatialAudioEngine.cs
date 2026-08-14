using System;
using System.Collections.Generic;
using UnityEngine;
using ShoalingUpstream.Journey;

namespace ShoalingUpstream.Audio
{
    /// <summary>
    /// One spatialised source: a beat or an ambient bed, with its three recordings.
    /// Exposed read-only so tests and the operator status line can see what is actually sounding.
    /// </summary>
    public class AudioSourceState
    {
        public string Id { get; internal set; }
        public Vector3 Position { get; internal set; }
        public float Reach { get; internal set; }
        public float Distance { get; internal set; }
        public bool Active { get; internal set; }
        public LayerGains Gains { get; internal set; }
        public AudibleField Field { get; internal set; }

        internal int Handle;
        internal LayerGainsDb Db;

        public override string ToString() => $"{Id} @ {Distance:F1} m {Gains}";
    }

    /// <summary>
    /// The engine.
    ///
    /// It owns the sound design and none of the rendering: a listener that follows the phone,
    /// one spatialised source per beat and per ambient bed, the three-layer distance crossfade,
    /// the shoal, the medium, and the discrete confirmations. Every one of those is plain C#
    /// arithmetic over a <see cref="IAudioBackend"/> that is only ever told gains.
    ///
    /// It is not a MonoBehaviour and touches nothing but Vector3 and Quaternion, so a whole walk
    /// can be driven through it in an EditMode test. That is the point of the seam: PHASE does not
    /// exist in the editor, and a crossfade that could only be checked by standing at a creek
    /// would never be checked.
    /// </summary>
    public class SpatialAudioEngine
    {
        /// <summary>Matches the editor's <c>setTargetAtTime(..., 0.08)</c>. Continuous field
        /// parameters have a 50–100 ms budget, so smoothing here costs nothing that can be heard,
        /// and the alternative — pushing raw per-frame distances — puts pose jitter straight onto
        /// the gains.</summary>
        private const float RiseTauSeconds = 0.08f;

        /// <summary>Slower on the way out, so a source leaving earshot fades rather than drops.</summary>
        private const float FallTauSeconds = 0.12f;

        /// <summary>Below this a source is inaudible under a creek, so its players can be stopped.</summary>
        private const float SilenceThreshold = 1e-4f;

        private readonly JourneyDocument _journey;
        private readonly IAudioBackend _backend;
        private readonly IAudioClipResolver _resolver;
        private readonly AudioEngineSettings _settings;

        private readonly List<AudioSourceState> _sources = new();
        private readonly Dictionary<string, AudioSourceState> _byId = new();
        private readonly List<string> _missing = new();

        private readonly ShoalVoicing _shoal;
        private readonly MediumEnvelope _medium = new();

        private int _shoalBedLow = -1, _shoalBedHigh = -1;

        public SpatialAudioEngine(JourneyDocument journey, IAudioBackend backend,
                                  IAudioClipResolver resolver, AudioEngineSettings settings = null)
        {
            _journey = journey ?? throw new ArgumentNullException(nameof(journey));
            _backend = backend ?? throw new ArgumentNullException(nameof(backend));
            _resolver = resolver;
            _settings = settings ?? new AudioEngineSettings();

            _shoal = new ShoalVoicing(journey.shoal);
            _medium.Reset();

            _backend.Initialise(_settings);

            foreach (var beat in journey.beats)
                AddSource(beat.id, beat.position.ToVector3(), DistanceField.ReachFor(beat), beat.audio);

            foreach (var ambient in journey.ambient)
                AddSource(ambient.id, ambient.position.ToVector3(), DistanceField.ReachFor(ambient), ambient.audio);

            PushShoalBeds(_shoal.State);
            _backend.SetShoal(_shoal.State);
            _backend.SetMedium(_medium.LowPassHz, _medium.ReverbSend);
        }

        public IReadOnlyList<AudioSourceState> Sources => _sources;

        /// <summary>Clip ids the journey names and the resolver could not find. Reported rather
        /// than substituted, and surfaced so a walk never starts with a silent beat nobody knew about.</summary>
        public IReadOnlyList<string> MissingClips => _missing;

        public ShoalVoicingState Shoal => _shoal.State;
        public int ShoalCount => _shoal.Count;
        public float AirBlend => _medium.AirBlend;
        public bool IsOutOfWater => _medium.IsOutOfWater;
        public string BackendDescription => _backend.Description;

        public bool TryGetSource(string id, out AudioSourceState state) => _byId.TryGetValue(id, out state);

        /// <summary>
        /// Advance the field.
        /// </summary>
        /// <param name="listenerPosition">Camera position in the same frame the beats are in.</param>
        public void Tick(Vector3 listenerPosition, Quaternion listenerRotation, float deltaTime)
        {
            _backend.SetListener(listenerPosition, listenerRotation);

            for (int i = 0; i < _sources.Count; i++)
            {
                var source = _sources[i];
                source.Distance = Vector3.Distance(listenerPosition, source.Position);

                bool inEarshot = source.Distance <= source.Reach * DistanceField.CullFactor;
                var target = inEarshot
                    ? DistanceField.GainsAt(source.Distance, source.Reach, source.Db)
                    : LayerGains.Silent;

                float tau = inEarshot ? RiseTauSeconds : FallTauSeconds;
                source.Gains = Approach(source.Gains, target, tau, deltaTime);

                // Start before pushing gains and stop only once they have actually reached
                // silence: starting late would let the first frames of a loop arrive at full
                // level, and stopping early would cut a source that is still sounding.
                if (inEarshot && !source.Active)
                {
                    source.Active = true;
                    _backend.SetSourceActive(source.Handle, true);
                }

                _backend.SetSourceGains(source.Handle, source.Gains);

                if (!inEarshot && source.Active && source.Gains.Sum < SilenceThreshold)
                {
                    source.Active = false;
                    _backend.SetSourceActive(source.Handle, false);
                }
            }

            var shoal = _shoal.Tick(deltaTime);
            PushShoalBeds(shoal);
            _backend.SetShoal(shoal);

            var transition = _medium.Tick(deltaTime);
            _backend.SetMedium(_medium.LowPassHz, _medium.ReverbSend);
            if (transition == MediumTransition.ReenteredWater) PlaySplash();
        }

        /// <summary>The fiction-level event, not an action. What "gave fish to the heron" does to
        /// the sound is this class's business, not the trigger machine's.</summary>
        public void SetShoalCount(int count) => _shoal.SetCount(count);

        /// <summary>Driven continuously from the gesture detector's held state, so the field stays
        /// dry and bright for exactly as long as the visitor stays up on the barrier.</summary>
        public void SetOutOfWater(bool outOfWater) => _medium.SetOutOfWater(outOfWater);

        /// <summary>Returns false when the beat has no completion clip, or the clip is missing —
        /// which is silence, not an exception, because a walk in progress must not stop.</summary>
        public bool FireCompletion(Beat beat)
        {
            var spec = beat?.audio?.completion;
            if (string.IsNullOrEmpty(spec?.clipId)) return false;
            if (!TryResolve(spec.clipId, out var clip)) return false;

            _backend.PlayOneShot(clip, DistanceField.DbToLinear(spec.gainDb));
            return true;
        }

        public void Shutdown() => _backend.Shutdown();

        private void AddSource(string id, Vector3 position, float reach, BeatAudio audio)
        {
            var db = LayerGainsDb.From(audio);
            if (!db.Any) return;

            int handle = _backend.CreateSource(id, position);
            if (handle < 0) return;

            var state = new AudioSourceState
            {
                Id = id,
                Position = position,
                Reach = reach,
                Handle = handle,
                Db = db,
                Gains = LayerGains.Silent,
                Field = DistanceField.Field(reach, db),
                Distance = float.MaxValue,
            };

            BindLayer(state, LayerSlot.Far, audio.far);
            BindLayer(state, LayerSlot.Mid, audio.mid);
            BindLayer(state, LayerSlot.Intimate, audio.intimate);

            _sources.Add(state);
            _byId[id] = state;
        }

        private void BindLayer(AudioSourceState state, LayerSlot slot, AudioLayer layer)
        {
            if (string.IsNullOrEmpty(layer?.clipId)) return;
            if (!TryResolve(layer.clipId, out var clip)) return;
            _backend.SetSourceLayer(state.Handle, slot, clip, layer.loop);
        }

        private void PushShoalBeds(in ShoalVoicingState state)
        {
            if (state.BedLow == _shoalBedLow && state.BedHigh == _shoalBedHigh) return;
            _shoalBedLow = state.BedLow;
            _shoalBedHigh = state.BedHigh;

            TryResolve(ShoalVoicing.BedClipId(state.BedLow), out var low);
            TryResolve(ShoalVoicing.BedClipId(state.BedHigh), out var high);
            _backend.SetShoalBeds(low, high);
        }

        private void PlaySplash()
        {
            if (string.IsNullOrEmpty(_settings.ReentrySplashClipId)) return;
            if (!TryResolve(_settings.ReentrySplashClipId, out var clip)) return;
            _backend.PlayOneShot(clip, DistanceField.DbToLinear(_settings.ReentrySplashGainDb));
        }

        private bool TryResolve(string clipId, out ResolvedClip clip)
        {
            clip = ResolvedClip.None;
            if (string.IsNullOrEmpty(clipId)) return false;

            if (_resolver != null && _resolver.TryResolve(clipId, out clip) && clip.IsValid) return true;

            if (!_missing.Contains(clipId)) _missing.Add(clipId);
            clip = ResolvedClip.None;
            return false;
        }

        private static LayerGains Approach(in LayerGains current, in LayerGains target, float tau, float dt)
        {
            if (dt <= 0f) return current;
            float alpha = 1f - Mathf.Exp(-dt / tau);
            return new LayerGains(
                current.Far + (target.Far - current.Far) * alpha,
                current.Mid + (target.Mid - current.Mid) * alpha,
                current.Intimate + (target.Intimate - current.Intimate) * alpha);
        }
    }
}
