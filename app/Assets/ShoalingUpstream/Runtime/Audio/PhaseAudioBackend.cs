using System.Runtime.InteropServices;
using UnityEngine;

namespace ShoalingUpstream.Audio
{
    /// <summary>
    /// Apple PHASE, through a native bridge.
    ///
    /// The managed side is deliberately thin and the native side is deliberately boring. PHASE
    /// can drive a distance blend inside its own mixer, at audio rate, which is the thing it does
    /// best — and we do not use it. The crossfade is computed in <see cref="DistanceField"/> and
    /// pushed down as three gains instead, for three reasons: it is the only way the desk and the
    /// bank can be guaranteed to hear the same law, it is the only way that law can be tested
    /// without a device, and PHASE's own distance model exposes a single rolloff scalar that
    /// cannot be made to match the editor's curve anyway. The cost is that the blend updates at
    /// frame rate rather than audio rate — 16–33 ms against a 50–100 ms budget for continuous
    /// field parameters, so it is paid out of slack.
    ///
    /// The spatial mixer's own rolloff is therefore disabled on the native side and PHASE is left
    /// doing what only it can do: HRTF, direction, head tracking and the one global reverb.
    /// </summary>
    public class PhaseAudioBackend : IAudioBackend
    {
        private readonly float[] _voiceGains = new float[ShoalVoicing.MaxVoices];
        private bool _started;
        private bool _warnedNoLowPass;

        // The bridge returns an int, because a C entry point crossing P/Invoke has no bool. The
        // comparison is the conversion, and without it the whole Runtime assembly fails to
        // compile, which takes every EditMode test in the project down with it.
        public bool IsAvailable => Native.Available() != 0;

        public string Description => "Apple PHASE";

        public void Initialise(AudioEngineSettings settings)
        {
            _started = Native.Start(settings.MasterGainDb,
                                    settings.HeadTracking ? 1 : 0,
                                    ReverbPresetIndex(settings.ReverbPreset)) != 0;
            if (!_started) Debug.LogWarning("[audio] PHASE engine failed to start; the field will be silent.");
        }

        public void SetListener(Vector3 position, Quaternion rotation)
        {
            if (!_started) return;
            var p = ToRightHanded(position);
            var q = ToRightHanded(rotation);
            Native.SetListener(p.x, p.y, p.z, q.x, q.y, q.z, q.w);
        }

        public int CreateSource(string sourceId, Vector3 position)
        {
            if (!_started) return -1;
            var p = ToRightHanded(position);
            return Native.CreateSource(sourceId, p.x, p.y, p.z);
        }

        public void SetSourceLayer(int handle, LayerSlot slot, in ResolvedClip clip, bool loop)
        {
            // PHASE registers sound assets from a URL and streams them itself, so a resolution
            // that only produced an in-memory AudioClip is no use here.
            if (!_started || string.IsNullOrEmpty(clip.FilePath)) return;
            Native.SetSourceLayer(handle, (int)slot, clip.FilePath, loop ? 1 : 0);
        }

        public void SetSourceGains(int handle, in LayerGains gains)
        {
            if (!_started) return;
            Native.SetSourceGains(handle, gains.Far, gains.Mid, gains.Intimate);
        }

        public void SetSourceActive(int handle, bool active)
        {
            if (!_started) return;
            Native.SetSourceActive(handle, active ? 1 : 0);
        }

        public void PlayOneShot(in ResolvedClip clip, float linearGain)
        {
            if (!_started || string.IsNullOrEmpty(clip.FilePath)) return;
            Native.PlayOneShot(clip.FilePath, linearGain);
        }

        public void SetMedium(float lowPassHz, float reverbSend)
        {
            if (!_started) return;

            // The reverb half of the medium cue is real: PHASE's global preset goes to None and
            // the whole field goes dry the moment the visitor lifts out of the water.
            Native.SetReverb(reverbSend);

            // The brightness half is not available. PHASE's public surface has no filter node and
            // no insert point, so "the low-pass opens" has to be carried by content — a dry, open
            // variant of each bed — or by a post-chain PHASE does not currently permit. Warned
            // once rather than silently ignored, because a cue that half-fires is worse than one
            // that is known to be missing.
            if (_warnedNoLowPass || Native.SupportsLowPass() != 0) return;
            _warnedNoLowPass = true;
            Debug.LogWarning($"[audio] PHASE exposes no global filter; the {lowPassHz:F0} Hz medium " +
                             "cue is carried by reverb alone on device. See docs/unity-audio.md.");
        }

        public void SetShoalBeds(in ResolvedClip low, in ResolvedClip high)
        {
            if (!_started) return;
            Native.SetShoalBeds(low.FilePath ?? "", high.FilePath ?? "");
        }

        public void SetShoal(in ShoalVoicingState state)
        {
            if (!_started) return;
            // Individuation is not pushed: it lives in the bed renders, because PHASE cannot sweep
            // a start offset on a running ambient event. See ShoalVoicingState.Individuation.
            ShoalVoicing.VoiceGains(state, _voiceGains);
            for (int i = 0; i < _voiceGains.Length; i++)
            {
                Native.SetShoalVoice(i,
                    _voiceGains[i] * (1f - state.BedBlend),
                    _voiceGains[i] * state.BedBlend);
            }
        }

        public void Shutdown()
        {
            if (!_started) return;
            _started = false;
            Native.Stop();
        }

        /// <summary>
        /// Unity is left-handed with +Z forward; PHASE, like the rest of AVFoundation and ARKit,
        /// is right-handed with −Z forward. Flipping Z on positions and negating x and y on the
        /// quaternion is the whole conversion — and getting it wrong mirrors the entire soundfield
        /// left to right, which is the kind of bug that sounds plausible until someone walks past
        /// a beat on the wrong side.
        /// </summary>
        private static Vector3 ToRightHanded(Vector3 v) => new(v.x, v.y, -v.z);

        private static Quaternion ToRightHanded(Quaternion q) => new(-q.x, -q.y, q.z, q.w);

        private static int ReverbPresetIndex(string preset) => preset switch
        {
            "None" => 0,
            "SmallRoom" => 1,
            "MediumRoom" => 2,
            "LargeRoom" => 3,
            "Cathedral" => 4,
            _ => 2,
        };

        /// <summary>
        /// The bridge. Every signature here has a mate in Plugins/iOS/PhaseBridge.m.
        ///
        /// Off iOS the entry points are stubs that report the engine unavailable, so the same code
        /// compiles and the same engine runs in the editor against a different backend rather than
        /// being conditionally absent.
        /// </summary>
        private static class Native
        {
#if UNITY_IOS && !UNITY_EDITOR
            private const string Lib = "__Internal";

            [DllImport(Lib, EntryPoint = "SUPhaseIsAvailable")] private static extern int _Available();
            [DllImport(Lib, EntryPoint = "SUPhaseStart")] private static extern int _Start(float masterGainDb, int headTracking, int reverbPreset);
            [DllImport(Lib, EntryPoint = "SUPhaseStop")] private static extern void _Stop();
            [DllImport(Lib, EntryPoint = "SUPhaseSetListener")] private static extern void _SetListener(float px, float py, float pz, float qx, float qy, float qz, float qw);
            [DllImport(Lib, EntryPoint = "SUPhaseCreateSource")] private static extern int _CreateSource(string sourceId, float x, float y, float z);
            [DllImport(Lib, EntryPoint = "SUPhaseSetSourceLayer")] private static extern void _SetSourceLayer(int handle, int slot, string filePath, int loop);
            [DllImport(Lib, EntryPoint = "SUPhaseSetSourceGains")] private static extern void _SetSourceGains(int handle, float far, float mid, float intimate);
            [DllImport(Lib, EntryPoint = "SUPhaseSetSourceActive")] private static extern void _SetSourceActive(int handle, int active);
            [DllImport(Lib, EntryPoint = "SUPhasePlayOneShot")] private static extern void _PlayOneShot(string filePath, float gain);
            [DllImport(Lib, EntryPoint = "SUPhaseSetReverb")] private static extern void _SetReverb(float send);
            [DllImport(Lib, EntryPoint = "SUPhaseSupportsLowPass")] private static extern int _SupportsLowPass();
            [DllImport(Lib, EntryPoint = "SUPhaseSetShoalBeds")] private static extern void _SetShoalBeds(string lowPath, string highPath);
            [DllImport(Lib, EntryPoint = "SUPhaseSetShoalVoice")] private static extern void _SetShoalVoice(int index, float lowGain, float highGain);

            public static int Available() => _Available();
            public static int Start(float g, int h, int r) => _Start(g, h, r);
            public static void Stop() => _Stop();
            public static void SetListener(float px, float py, float pz, float qx, float qy, float qz, float qw) => _SetListener(px, py, pz, qx, qy, qz, qw);
            public static int CreateSource(string id, float x, float y, float z) => _CreateSource(id, x, y, z);
            public static void SetSourceLayer(int h, int s, string p, int l) => _SetSourceLayer(h, s, p, l);
            public static void SetSourceGains(int h, float f, float m, float i) => _SetSourceGains(h, f, m, i);
            public static void SetSourceActive(int h, int a) => _SetSourceActive(h, a);
            public static void PlayOneShot(string p, float g) => _PlayOneShot(p, g);
            public static void SetReverb(float s) => _SetReverb(s);
            public static int SupportsLowPass() => _SupportsLowPass();
            public static void SetShoalBeds(string lo, string hi) => _SetShoalBeds(lo, hi);
            public static void SetShoalVoice(int i, float lo, float hi) => _SetShoalVoice(i, lo, hi);
#else
            public static int Available() => 0;
            public static int Start(float g, int h, int r) => 0;
            public static void Stop() { }
            public static void SetListener(float px, float py, float pz, float qx, float qy, float qz, float qw) { }
            public static int CreateSource(string id, float x, float y, float z) => -1;
            public static void SetSourceLayer(int h, int s, string p, int l) { }
            public static void SetSourceGains(int h, float f, float m, float i) { }
            public static void SetSourceActive(int h, int a) { }
            public static void PlayOneShot(string p, float g) { }
            public static void SetReverb(float s) { }
            public static int SupportsLowPass() => 0;
            public static void SetShoalBeds(string lo, string hi) { }
            public static void SetShoalVoice(int i, float lo, float hi) { }
#endif
        }
    }
}
