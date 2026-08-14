using UnityEngine;
using ShoalingUpstream.Gestures;
using ShoalingUpstream.Journey;

namespace ShoalingUpstream.Audio
{
    /// <summary>
    /// The only MonoBehaviour in the audio system: it chooses a renderer, follows the camera, and
    /// translates fiction-level events into sound.
    ///
    /// Events and actions are kept apart on purpose. The trigger machine raises "the visitor gave
    /// fish to the heron"; deciding that this thins the shoal over 300 ms is this layer's business
    /// and nobody else's. That separation costs an afternoon and pays back every time the sound
    /// design changes without the gameplay changing, which is most of the time.
    ///
    /// It does not drain <see cref="JourneyProgression"/> itself. Draining is destructive and the
    /// app has its own consumer; an audio system that quietly ate half the events would be very
    /// hard to diagnose from the symptom.
    /// </summary>
    public class JourneyAudioRunner : MonoBehaviour
    {
        [Tooltip("The phone. Leave empty to use the main camera.")]
        public Transform listener;

        [Tooltip("Force the desk stand-in even on a device, to compare the two renderers.")]
        public bool forceSimulation;

        private SpatialAudioEngine _engine;
        private GestureDetector _gestures;

        public SpatialAudioEngine Engine => _engine;

        /// <summary>
        /// Pick a renderer and build the field.
        ///
        /// PHASE if it is there, Unity's own audio if it is not, and headless if neither can start
        /// — a walk must not fail to run because a machine has no output device.
        /// </summary>
        public void Begin(JourneyDocument journey, IAudioClipResolver resolver,
                          AudioEngineSettings settings = null)
        {
            Shutdown();

            var phase = new PhaseAudioBackend();
            IAudioBackend backend =
                !forceSimulation && phase.IsAvailable ? phase
                : Application.isPlaying ? new UnityAudioBackend(listener)
                : new HeadlessAudioBackend();

            _engine = new SpatialAudioEngine(journey, backend, resolver, settings);

            if (_engine.MissingClips.Count > 0)
            {
                // Loud, once, at the start. A missing clip is silence in the field, and silence is
                // indistinguishable from a beat that is simply out of earshot.
                Debug.LogWarning($"[audio] {_engine.MissingClips.Count} clip(s) missing: " +
                                 string.Join(", ", _engine.MissingClips));
            }
        }

        /// <summary>Optional. When bound, the field follows the held lift state every frame, which
        /// is what lets "out of the water" last exactly as long as the visitor stays up.</summary>
        public void Bind(GestureDetector gestures) => _gestures = gestures;

        /// <summary>Fed one event at a time by whoever owns the trigger machine.</summary>
        public void Handle(in JourneyProgression.Event journeyEvent)
        {
            if (_engine == null) return;
            if (journeyEvent.Kind == JourneyProgression.Event.Type.Completed)
                _engine.FireCompletion(journeyEvent.Beat);
        }

        public void SetShoalCount(int count) => _engine?.SetShoalCount(count);

        public void SetOutOfWater(bool outOfWater) => _engine?.SetOutOfWater(outOfWater);

        public void Shutdown()
        {
            _engine?.Shutdown();
            _engine = null;
        }

        private void Update()
        {
            if (_engine == null) return;

            var pose = listener != null ? listener : Camera.main != null ? Camera.main.transform : null;
            if (pose == null) return;

            if (_gestures != null) _engine.SetOutOfWater(_gestures.IsLifted);
            _engine.Tick(pose.position, pose.rotation, Time.deltaTime);
        }

        private void OnDestroy() => Shutdown();
    }
}
