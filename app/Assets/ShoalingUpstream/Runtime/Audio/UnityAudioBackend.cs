using System.Collections.Generic;
using UnityEngine;

namespace ShoalingUpstream.Audio
{
    /// <summary>
    /// The desk. Unity's own audio, standing in for PHASE.
    ///
    /// Most of the work on this piece happens at a table with no creek and no device, so the
    /// fallback is not a stub — it is where the sound design is actually judged. What makes that
    /// safe is that it is handed the same gains as PHASE: the crossfade, the rolloff, the shoal
    /// and the medium are all computed above this line, so the only thing that differs between a
    /// desk and the bank is the spatialiser.
    ///
    /// Every AudioSource therefore has its distance attenuation switched off, with a flat custom
    /// rolloff curve. Letting Unity apply its own law on top of ours would double-count distance
    /// and make the desk quietly disagree with the device about the one thing the piece rests on.
    ///
    /// Two places where it is honestly an approximation, both documented in docs/unity-audio.md:
    /// panning is amplitude panning rather than an HRTF, and the shoal's per-voice decorrelation
    /// delay is applied when a voice starts rather than swept live, because sweeping it would mean
    /// restarting the player.
    /// </summary>
    public class UnityAudioBackend : IAudioBackend
    {
        private class Voice
        {
            public GameObject Go;
            public AudioSource Low, High;
            public bool Playing;
        }

        private class Source
        {
            public GameObject Go;
            public readonly AudioSource[] Layers = new AudioSource[3];
            public bool Active;
        }

        private readonly List<Source> _sources = new();
        private readonly List<Voice> _voices = new();
        private readonly float[] _voiceGains = new float[ShoalVoicing.MaxVoices];
        private readonly Transform _explicitListener;

        private GameObject _root;
        private Transform _listener;
        private AudioLowPassFilter _lowPass;
        private AudioReverbFilter _reverb;
        private AudioSource _oneShots;
        private bool _ownsListener;

        private AudioClip _bedLow, _bedHigh;

        public UnityAudioBackend(Transform listener = null) => _explicitListener = listener;

        public bool IsAvailable => true;
        public string Description => "Unity audio (desk stand-in for PHASE)";

        public void Initialise(AudioEngineSettings settings)
        {
            _root = new GameObject("[ShoalingUpstream Audio]");

            // If the app already has a listener — it will, on the camera — leave it exactly where
            // it is. The engine feeds this backend the camera's own pose, so driving a second
            // listener would either fight the camera or produce Unity's two-listeners warning.
            var existing = Object.FindFirstObjectByType<AudioListener>();
            if (_explicitListener != null)
            {
                _listener = _explicitListener;
            }
            else if (existing != null)
            {
                _listener = existing.transform;
            }
            else
            {
                var go = new GameObject("Listener");
                go.transform.SetParent(_root.transform, false);
                go.AddComponent<AudioListener>();
                _listener = go.transform;
                _ownsListener = true;
            }

            // The medium filters live on the listener, which is what makes them global: PHASE has
            // one reverb for the whole scene and no per-source rooms, so a desk stand-in that gave
            // each source its own space would flatter a constraint the piece has to live inside.
            _lowPass = _listener.gameObject.GetComponent<AudioLowPassFilter>()
                       ?? _listener.gameObject.AddComponent<AudioLowPassFilter>();
            _lowPass.cutoffFrequency = MediumEnvelope.AirCutoffHz;

            _reverb = _listener.gameObject.GetComponent<AudioReverbFilter>()
                      ?? _listener.gameObject.AddComponent<AudioReverbFilter>();
            _reverb.reverbPreset = AudioReverbPreset.User;

            _oneShots = _root.AddComponent<AudioSource>();
            _oneShots.spatialBlend = 0f;   // a confirmation is about the visitor, not a place
            _oneShots.playOnAwake = false;

            AudioListener.volume = DistanceField.DbToLinear(settings.MasterGainDb);

            for (int i = 0; i < ShoalVoicing.MaxVoices; i++) _voices.Add(BuildVoice(i));
        }

        public void SetListener(Vector3 position, Quaternion rotation)
        {
            if (!_ownsListener) return;
            _listener.SetPositionAndRotation(position, rotation);
        }

        public int CreateSource(string sourceId, Vector3 position)
        {
            var go = new GameObject($"src:{sourceId}");
            go.transform.SetParent(_root.transform, false);
            go.transform.position = position;
            _sources.Add(new Source { Go = go });
            return _sources.Count - 1;
        }

        public void SetSourceLayer(int handle, LayerSlot slot, in ResolvedClip clip, bool loop)
        {
            if (clip.Clip == null) return;   // a file path is PHASE's currency, not Unity's

            var source = _sources[handle];
            var player = source.Go.AddComponent<AudioSource>();
            player.clip = clip.Clip;
            player.loop = loop;
            player.playOnAwake = false;
            player.volume = 0f;
            player.spatialBlend = 1f;
            player.dopplerLevel = 0f;      // nobody in this piece moves fast enough to earn Doppler
            player.rolloffMode = AudioRolloffMode.Custom;
            player.SetCustomCurve(AudioSourceCurveType.CustomRolloff, AnimationCurve.Constant(0f, 1f, 1f));
            player.maxDistance = 500f;
            source.Layers[(int)slot] = player;
        }

        public void SetSourceGains(int handle, in LayerGains gains)
        {
            var layers = _sources[handle].Layers;
            for (int i = 0; i < 3; i++)
                if (layers[i] != null) layers[i].volume = gains[(LayerSlot)i];
        }

        public void SetSourceActive(int handle, bool active)
        {
            var source = _sources[handle];
            if (source.Active == active) return;
            source.Active = active;

            // All three layers start together. They are three takes of the same event and a
            // crossfade between recordings that are not in phase with each other sounds like two
            // sources rather than one approaching.
            foreach (var layer in source.Layers)
            {
                if (layer == null) continue;
                if (active) layer.Play(); else layer.Stop();
            }
        }

        public void PlayOneShot(in ResolvedClip clip, float linearGain)
        {
            if (clip.Clip == null) return;
            _oneShots.PlayOneShot(clip.Clip, linearGain);
        }

        public void SetMedium(float lowPassHz, float reverbSend)
        {
            _lowPass.cutoffFrequency = lowPassHz;
            // AudioReverbFilter.room is in millibels: −10000 is off. The floor is the dry field
            // the lift produces, so it has to reach genuinely silent rather than merely quiet.
            _reverb.room = Mathf.Lerp(-10000f, -1500f, Mathf.Clamp01(reverbSend));
        }

        public void SetShoalBeds(in ResolvedClip low, in ResolvedClip high)
        {
            _bedLow = low.Clip;
            _bedHigh = high.Clip;
            foreach (var voice in _voices)
            {
                voice.Low.clip = _bedLow;
                voice.High.clip = _bedHigh;
            }
        }

        public void SetShoal(in ShoalVoicingState state)
        {
            ShoalVoicing.VoiceGains(state, _voiceGains);

            for (int i = 0; i < _voices.Count; i++)
            {
                var voice = _voices[i];
                bool wanted = _voiceGains[i] > 0f && (_bedLow != null || _bedHigh != null);

                if (wanted && !voice.Playing)
                {
                    // The desk's stand-in for individuation: a per-voice start offset, applied once
                    // here because sweeping it would mean restarting the player and a restart is
                    // audible. On device the bed renders carry it instead.
                    double at = AudioSettings.dspTime
                              + i * ShoalVoicing.DeskVoiceDelayMs(state.Individuation) / 1000f;
                    voice.Low.PlayScheduled(at);
                    voice.High.PlayScheduled(at);
                    voice.Playing = true;
                }
                else if (!wanted && voice.Playing)
                {
                    voice.Low.Stop();
                    voice.High.Stop();
                    voice.Playing = false;
                }

                if (!voice.Playing) continue;

                // Amplitude panning, which is the honest limit of this stand-in: PHASE puts the
                // voice at a real azimuth through an HRTF, Unity can only place it between two
                // speakers. The angles are the same, so what changes between desk and bank is
                // externalisation, not the shape of the group.
                float pan = Mathf.Clamp(ShoalVoicing.VoiceAzimuthsDeg[i] / 90f, -1f, 1f);
                voice.Low.panStereo = pan;
                voice.High.panStereo = pan;

                // Linear, not equal-power. The two beds are the same grain cloud rendered at two
                // densities from the same seed, so they are correlated; an equal-power crossfade
                // between correlated signals bulges by 3 dB in the middle.
                voice.Low.volume = _voiceGains[i] * (1f - state.BedBlend);
                voice.High.volume = _voiceGains[i] * state.BedBlend;
            }
        }

        public void Shutdown()
        {
            if (_root != null) Object.Destroy(_root);
            _sources.Clear();
            _voices.Clear();
        }

        private Voice BuildVoice(int index)
        {
            var go = new GameObject($"shoal:{index}");
            go.transform.SetParent(_root.transform, false);
            return new Voice { Go = go, Low = BuildVoicePlayer(go), High = BuildVoicePlayer(go) };
        }

        private static AudioSource BuildVoicePlayer(GameObject go)
        {
            var player = go.AddComponent<AudioSource>();
            player.loop = true;
            player.playOnAwake = false;
            player.volume = 0f;
            // Head-relative, not spatialised. The shoal is at distance zero — it is the visitor —
            // so running it through a distance model would be modelling a gap that does not exist.
            player.spatialBlend = 0f;
            return player;
        }
    }
}
