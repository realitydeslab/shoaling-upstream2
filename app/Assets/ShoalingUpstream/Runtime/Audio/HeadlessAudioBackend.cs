using System.Collections.Generic;
using UnityEngine;

namespace ShoalingUpstream.Audio
{
    /// <summary>
    /// A backend that renders nothing and remembers everything.
    ///
    /// It exists twice over. In EditMode it is how a whole walk gets asserted without PHASE,
    /// without Unity's audio thread and without a device. On a build server or a headless run it
    /// is what keeps the engine ticking rather than throwing, so the rest of the app can be
    /// exercised on a machine with no output device at all.
    ///
    /// It deliberately does not approximate anything. A backend that silently half-worked would
    /// be the worst of the three.
    /// </summary>
    public class HeadlessAudioBackend : IAudioBackend
    {
        public class Recorded
        {
            public string Id;
            public Vector3 Position;
            public bool Active;
            public LayerGains Gains;
            public readonly string[] ClipIds = new string[3];
            public readonly bool[] Loop = new bool[3];
        }

        public readonly struct OneShot
        {
            public readonly string ClipId;
            public readonly float Gain;
            public OneShot(string clipId, float gain) { ClipId = clipId; Gain = gain; }
        }

        private readonly List<Recorded> _sources = new();

        public bool IsAvailable => true;
        public string Description => "headless (no output)";

        public AudioEngineSettings Settings { get; private set; }
        public IReadOnlyList<Recorded> Sources => _sources;
        public List<OneShot> OneShots { get; } = new();

        public Vector3 ListenerPosition { get; private set; }
        public Quaternion ListenerRotation { get; private set; } = Quaternion.identity;
        public float LowPassHz { get; private set; }
        public float ReverbSend { get; private set; }
        public ShoalVoicingState Shoal { get; private set; }
        public string ShoalBedLowClipId { get; private set; }
        public string ShoalBedHighClipId { get; private set; }
        public int ShoalBedChanges { get; private set; }
        public bool IsShutDown { get; private set; }

        public void Initialise(AudioEngineSettings settings) => Settings = settings;

        public void SetListener(Vector3 position, Quaternion rotation)
        {
            ListenerPosition = position;
            ListenerRotation = rotation;
        }

        public int CreateSource(string sourceId, Vector3 position)
        {
            _sources.Add(new Recorded { Id = sourceId, Position = position });
            return _sources.Count - 1;
        }

        public void SetSourceLayer(int handle, LayerSlot slot, in ResolvedClip clip, bool loop)
        {
            _sources[handle].ClipIds[(int)slot] = clip.ClipId;
            _sources[handle].Loop[(int)slot] = loop;
        }

        public void SetSourceGains(int handle, in LayerGains gains) => _sources[handle].Gains = gains;

        public void SetSourceActive(int handle, bool active) => _sources[handle].Active = active;

        public void PlayOneShot(in ResolvedClip clip, float linearGain) =>
            OneShots.Add(new OneShot(clip.ClipId, linearGain));

        public void SetMedium(float lowPassHz, float reverbSend)
        {
            LowPassHz = lowPassHz;
            ReverbSend = reverbSend;
        }

        public void SetShoalBeds(in ResolvedClip low, in ResolvedClip high)
        {
            ShoalBedLowClipId = low.ClipId;
            ShoalBedHighClipId = high.ClipId;
            ShoalBedChanges++;
        }

        public void SetShoal(in ShoalVoicingState state) => Shoal = state;

        public void Shutdown() => IsShutDown = true;

        public Recorded Find(string id) => _sources.Find(s => s.Id == id);
    }
}
