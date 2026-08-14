using UnityEngine;

namespace ShoalingUpstream.Audio
{
    /// <summary>Whole-engine settings, fixed for a walk.</summary>
    public class AudioEngineSettings
    {
        public float MasterGainDb = -1f;

        /// <summary>AirPods head tracking. Costs nothing to ask for and is the cue that resolves
        /// front-back confusion; without it, nothing critical may be placed dead ahead or behind.</summary>
        public bool HeadTracking = true;

        /// <summary>One global reverb preset for the whole piece — PHASE has no per-source rooms,
        /// so the fifth beat cannot have its own acoustic and the space has to be baked into the
        /// recordings instead.</summary>
        public string ReverbPreset = "MediumRoom";

        /// <summary>Played when the visitor comes back down off the barrier. Left unset by
        /// default: the <c>falls</c> beat already carries a splash as its completion clip, and
        /// two splashes for one landing would read as a double trigger.</summary>
        public string ReentrySplashClipId;

        public float ReentrySplashGainDb = -3f;
    }

    /// <summary>
    /// The seam between the sound design and whatever is rendering it.
    ///
    /// Everything above this line — the crossfade, the shoal, the medium — is plain C# and runs
    /// anywhere. Everything below it is a renderer: Apple PHASE on device, Unity's own audio at a
    /// desk, nothing at all in a test. The interface is deliberately dumb: it is told gains, not
    /// distances, so no implementation gets to have an opinion about the distance law. That is
    /// what keeps the desk honest about the bank.
    ///
    /// Handles are ints rather than objects so the PHASE implementation can pass them straight
    /// through to the native side without marshalling anything per frame.
    /// </summary>
    public interface IAudioBackend
    {
        bool IsAvailable { get; }

        /// <summary>Shown in the operator's status line. It matters that a listener can tell
        /// which renderer they are hearing before they trust a level judgement.</summary>
        string Description { get; }

        void Initialise(AudioEngineSettings settings);

        void SetListener(Vector3 position, Quaternion rotation);

        /// <summary>Returns a handle, or a negative number if the source could not be created.</summary>
        int CreateSource(string sourceId, Vector3 position);

        void SetSourceLayer(int handle, LayerSlot slot, in ResolvedClip clip, bool loop);

        void SetSourceGains(int handle, in LayerGains gains);

        /// <summary>Start or stop a source's players. Called only at the cull boundary, where the
        /// gains have already reached silence, so it never cuts anything audible.</summary>
        void SetSourceActive(int handle, bool active);

        /// <summary>Head-relative, not spatialised. A confirmation is about the visitor, not
        /// about a place in the world, and it must land inside 30 ms of the action that caused it.</summary>
        void PlayOneShot(in ResolvedClip clip, float linearGain);

        /// <summary>The medium. One low-pass and one reverb send across the whole field.</summary>
        void SetMedium(float lowPassHz, float reverbSend);

        void SetShoalBeds(in ResolvedClip low, in ResolvedClip high);

        void SetShoal(in ShoalVoicingState state);

        void Shutdown();
    }
}
