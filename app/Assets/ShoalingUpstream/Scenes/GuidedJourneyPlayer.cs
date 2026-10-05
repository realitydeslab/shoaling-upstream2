using System.Collections;
using System.Collections.Generic;
using ShoalingUpstream.Experience;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace ShoalingUpstream.Simulation
{
    [System.Serializable]
    public sealed class GuidedNarrationCues
    {
        public float GrowingSearchStart = 9.2f, GrowingSearchEnd = 18.3f, GrowingPoolEnd = 27.4f, GrowingPatienceEnd = 30.7f;
        public float OceanMoveStart = 8.5f, OceanMeetEnd = 12.8f, OceanCreekEnd = 15.35f, OceanReceiveEnd = 18.95f;
        public float OceanGiftEnd = 22.1f, OceanDownstreamEnd = 23.65f, OceanHeronLookEnd = 25.95f;
        public float OceanOfferEnd = 29.7f, OceanOfferInstructionEnd = 36.5f;
        public float OceanContinueEnd = 40.95f, OceanCreekAgainEnd = 43.65f, OceanRainbowEnd = 55.25f;
        public float ReturningInstructionStart = 14.25f;
        public float RebirthInstructionStart = 5.8f, RebirthHomeEnd = 22.15f, RebirthGravelEnd = 25.6f;
    }

    // Device adapter for the user's narrated chapter script. The choice gate has no Unity dependency.
    public sealed class GuidedJourneyPlayer : MonoBehaviour
    {
        public GuidedNarrationCues Cues = new();
        public bool Finished { get; private set; }
        public string Error { get; private set; }
        public GuidedChoiceGate Choices { get; } = new();
        public int Chapter { get; private set; } = 2;
        public string ChapterTwoStage { get; private set; }
        public float PoolArrivalTime { get; private set; }
        public float PatienceStartTime { get; private set; }
        public float GrowthStartTime { get; private set; }
        public float ActiveNarrationStart { get; private set; }
        public bool WaitingForHeronWalk { get; private set; }
        public float HeronWalkReachedAt { get; private set; } = -1f;
        public bool PoolRejoinPending => _poolRejoinPending;
        public bool FollowingParticipant => _follow;
        private SimulationDriver _driver;
        private AudioSource _audio, _signal;
        private AudioClip[] _clips;
        private AudioClip _notification;
        private Coroutine _routine;
        private bool _suspended, _follow = true, _poolRejoinPending;
        private Vector3 _poolDepartureOrigin;
        private readonly List<ARRaycastHit> _hits = new();
        private readonly Dictionary<string, AudioClip> _segments = new();

        private IEnumerator WalkThenWaitForHeron()
        {
            Vector3 origin = Camera.main != null ? Camera.main.transform.position : Vector3.zero;
            WaitingForHeronWalk = true;
            HeronWalkReachedAt = -1f;
            while (Camera.main == null || _suspended ||
                Vector3.ProjectOnPlane(Camera.main.transform.position - origin, Vector3.up).magnitude < 1f)
                yield return null;
            WaitingForHeronWalk = false;
            HeronWalkReachedAt = Time.time;
            yield return new WaitForSeconds(15f);
        }

        private void Awake()
        {
            _driver = GetComponent<SimulationDriver>();
            _signal = gameObject.AddComponent<AudioSource>();
            _signal.playOnAwake = false; _signal.spatialBlend = 0f;
        }
        public void SetSuspended(bool value)
        {
            _suspended = value;
            if (_signal == null) return;
            if (value) _signal.Pause(); else _signal.UnPause();
        }
        public void Begin(AudioSource audio, AudioClip[] clips)
        {
            if (_routine != null || Finished || Error != null) return;
            _audio = audio; _clips = clips;
            _routine = StartCoroutine(Run());
        }
        private void Update()
        {
            if (_routine == null || _suspended || Finished) return;
            if (!string.IsNullOrEmpty(_driver.LocalError) || Error != null)
            {
                Error ??= _driver.LocalError;
                Choices.Hide(); _audio.Stop(); StopCoroutine(_routine); _routine = null; return;
            }
            bool tracking = Camera.main == null || Camera.main.GetComponent<ARCameraManager>() == null
                || ARSession.state == ARSessionState.SessionTracking;
            if (_poolRejoinPending && Camera.main != null && !_driver.LocalSceneBusy)
            {
                Vector3 walked = Camera.main.transform.position - _poolDepartureOrigin; walked.y = 0f;
                if (walked.sqrMagnitude > .25f * .25f)
                {
                    string flock = _driver.LocalFlockCount("beat-4") > 0 ? "beat-4" : "beat-7";
                    if (_driver.FollowPoolFryLocally(flock))
                    { _poolRejoinPending = false; _follow = true; }
                }
            }
            _driver.FollowLocally(_follow && tracking);
        }

        private IEnumerator Audio(int chapter, float start, float end = -1f)
        {
            Choices.Hide();
            var clip = _clips[chapter];
            if (clip == null || start < 0f || start >= clip.length || (end >= 0f && (end <= start || end > clip.length)))
            { Error = "Narration timing needs adjustment."; yield break; }
            ActiveNarrationStart = start;
            _audio.Stop();
            if (clip.loadState != AudioDataLoadState.Loaded) clip.LoadAudioData();
            while (clip.loadState == AudioDataLoadState.Loading) yield return null;
            string key = $"{chapter}:{start:R}:{end:R}";
            if (!_segments.TryGetValue(key, out var segment))
            {
                int first = Mathf.Clamp(Mathf.RoundToInt(start * clip.frequency), 0, clip.samples - 1);
                int last = end < 0f ? clip.samples : Mathf.Clamp(Mathf.RoundToInt(end * clip.frequency), first + 1, clip.samples);
                var samples = new float[(last - first) * clip.channels];
                if (!clip.GetData(samples, first))
                { Error = "Narration could not be prepared."; yield break; }
                segment = AudioClip.Create(key, last - first, clip.channels, clip.frequency, false);
                segment.SetData(samples, 0); _segments.Add(key, segment);
            }
            _audio.clip = segment; _audio.timeSamples = 0; _audio.Play();
            while (_suspended || _audio.isPlaying) yield return null;
        }
        private IEnumerator Beat(string id)
        {
            Choices.Hide();
            while (_suspended || _driver.LocalSceneBusy) yield return null;
            if (!_driver.FireLocally(id)) { Error = "The shoal could not begin its next response."; yield break; }
            while (_suspended || _driver.LocalSceneBusy) yield return null;
        }
        private IEnumerator LocalMovement(string flock, Vector3 position, float duration)
        {
            Choices.Hide();
            if (!_driver.MoveLocalFlock(flock, position, duration)) { Error = "The shoal could not reach its destination."; yield break; }
            while (_suspended || _driver.LocalSceneBusy) yield return null;
        }
        private IEnumerator Gate(JourneyGate gate)
        {
            Choices.Open(gate);
            while (_suspended || Choices.Choice == JourneyChoice.None) yield return null;
            Choices.Hide();
        }
        private IEnumerator Shelter()
        {
            Choices.Open(JourneyGate.Shelter, resetShelter: true);
            do
            {
                yield return Gate(JourneyGate.Shelter);
                if (Choices.Choice == JourneyChoice.Repeat)
                    yield return Audio(2, Cues.GrowingSearchStart, Cues.GrowingSearchEnd);
                else break;
            } while (true);
        }
        public bool Choose(JourneyChoice choice) => !_suspended && Choices.Choose(choice);

        private Vector3 Aim(bool ground)
        {
            var camera = Camera.main;
            if (camera == null) return Vector3.zero;
            var manager = FindFirstObjectByType<ARRaycastManager>();
            if (manager != null && manager.Raycast(new Vector2(Screen.width * .5f, Screen.height * .5f), _hits, TrackableType.PlaneWithinPolygon))
                return _hits[0].pose.position + (ground ? Vector3.zero : Vector3.up * .3f);
            Vector3 forward = camera.transform.forward; forward.y = 0f;
            Vector3 fallback = camera.transform.position + forward.normalized * (ground ? 3f : 1.5f);
            if (ground) fallback.y = _driver.LocalGroundY;
            return fallback;
        }
        private void Notify()
        {
            Handheld.Vibrate();
            if (_notification == null)
            {
                const int rate = 44100;
                var samples = new float[rate / 4];
                for (int i = 0; i < samples.Length; i++)
                {
                    float t = (float)i / samples.Length;
                    samples[i] = Mathf.Sin(2f * Mathf.PI * 660f * i / rate) * Mathf.Sin(Mathf.PI * t) * .16f;
                }
                _notification = AudioClip.Create("Creek invitation", samples.Length, 1, rate, false);
                _notification.SetData(samples, 0);
            }
            _signal.PlayOneShot(_notification);
        }

        private IEnumerator Run()
        {
            // Chapter 2: both shelter confirmations, then pool / grow / three-second pause.
            Chapter = 2;
            ChapterTwoStage = "Shelter";
            yield return Audio(2, 0f, Cues.GrowingSearchEnd);
            yield return Shelter();
            yield return Audio(2, Cues.GrowingSearchEnd, Cues.GrowingPoolEnd);
            _follow = false;
            _driver.SetLocalTarget(Aim(ground: false));
            ChapterTwoStage = "PoolSwim";
            yield return Beat("beat-3");
            PoolArrivalTime = Time.time;
            ChapterTwoStage = "PoolPause";
            yield return new WaitForSeconds(3f);
            PatienceStartTime = Time.time;
            ChapterTwoStage = "Patience";
            yield return Audio(2, Cues.GrowingPoolEnd, Cues.GrowingPatienceEnd);
            GrowthStartTime = Time.time;
            ChapterTwoStage = "Growing";
            if (!_driver.FireLocally("beat-4")) { Error = "The shoal could not begin growing."; yield break; }
            // Growth and these three spoken lines share the same time window.
            yield return Audio(2, Cues.GrowingPatienceEnd);
            while (_suspended || _driver.LocalSceneBusy) yield return null;
            _driver.BeginPoolSwimmingLocally("beat-4");
            ChapterTwoStage = "PoolRest";

            // Chapter 3: pause invitations rather than expose generic animation controls.
            Chapter = 3;
            _poolDepartureOrigin = Camera.main != null ? Camera.main.transform.position : Vector3.zero;
            _poolRejoinPending = true;
            yield return Audio(3, 0f, Cues.OceanMoveStart);
            yield return Audio(3, Cues.OceanMoveStart, Cues.OceanMeetEnd);
            yield return new WaitForSeconds(15f); Notify();
            yield return Audio(3, Cues.OceanMeetEnd, Cues.OceanCreekEnd);
            // Striders appear beyond a shoal that is already returning to the user's front.
            if (_poolRejoinPending && _driver.FollowPoolFryLocally("beat-4"))
            { _poolRejoinPending = false; _follow = true; }
            yield return Beat("beat-5");
            yield return Audio(3, Cues.OceanCreekEnd, Cues.OceanReceiveEnd);
            yield return Gate(JourneyGate.Strider);
            yield return Beat("beat-7");
            yield return Audio(3, Cues.OceanReceiveEnd, Cues.OceanGiftEnd);

            bool heronSeen = false;
            bool offered = false;
            while (!offered)
            {
                if (!heronSeen) yield return Audio(3, Cues.OceanGiftEnd, Cues.OceanDownstreamEnd);
                yield return WalkThenWaitForHeron(); Notify();
                yield return Audio(3, Cues.OceanDownstreamEnd, Cues.OceanHeronLookEnd);
                if (!heronSeen) { yield return Beat("beat-6"); heronSeen = true; }
                else
                {
                    _driver.ReappearHeronLocally();
                    while (_suspended || _driver.LocalSceneBusy) yield return null;
                }
                yield return Audio(3, Cues.OceanHeronLookEnd, Cues.OceanOfferEnd);
                do
                {
                    yield return Gate(JourneyGate.Heron);
                    if (Choices.Choice == JourneyChoice.Repeat)
                        yield return Audio(3, Cues.OceanHeronLookEnd, Cues.OceanOfferInstructionEnd);
                    else break;
                } while (true);
                if (Choices.Choice == JourneyChoice.Ignore)
                {
                    foreach (var actor in _driver.LocalFlock("beat-6"))
                        if (actor != null) actor.gameObject.SetActive(false);
                    continue;
                }
                offered = true;
            }
            yield return Audio(3, Cues.OceanOfferEnd, Cues.OceanOfferInstructionEnd);
            yield return Beat("beat-8");
            yield return Audio(3, Cues.OceanOfferInstructionEnd, 39.4f);
            yield return new WaitForSeconds(2f);
            yield return Audio(3, 39.52f, Cues.OceanContinueEnd);
            yield return Audio(3, Cues.OceanContinueEnd, Cues.OceanCreekAgainEnd);
            _follow = false; _poolRejoinPending = false;
            yield return Beat("beat-17"); // six metres in the standalone scene
            yield return Audio(3, Cues.OceanCreekAgainEnd, Cues.OceanRainbowEnd);
            yield return Beat("beat-9");
            yield return Beat("beat-11");
            _driver.BeginUpstreamTurnLocally();
            yield return Beat("beat-10");
            _driver.FinishUpstreamTurnLocally();
            _driver.RejoinParticipantLocally("beat-9", around: false, seconds: 5f);
            while (_suspended || _driver.LocalSceneBusy) yield return null;
            _follow = true;
            yield return Audio(3, Cues.OceanRainbowEnd);

            Chapter = 4;
            yield return Audio(4, 0f);
            while (true)
            {
                yield return Gate(JourneyGate.Return);
                if (Choices.Choice == JourneyChoice.Locate) break;
                if (Choices.Choice == JourneyChoice.Repeat)
                    yield return Audio(4, Cues.ReturningInstructionStart);
                else
                {
                    _follow = false;
                    _driver.JumpLocally();
                    while (_suspended || _driver.LocalSceneBusy) yield return null;
                    _follow = true;
                }
            }

            Chapter = 5;
            yield return Audio(5, 0f, Cues.RebirthHomeEnd);
            while (true)
            {
                yield return Gate(JourneyGate.Home);
                if (Choices.Choice == JourneyChoice.Home) break;
                yield return Audio(5, Cues.RebirthInstructionStart, Cues.RebirthHomeEnd);
            }
            _follow = false;
            yield return Beat("beat-13");
            yield return Audio(5, Cues.RebirthHomeEnd, Cues.RebirthGravelEnd);
            yield return Gate(JourneyGate.Spawn);
            _follow = false;
            _driver.AlignLocalFish = false;
            Vector3 gravel = Aim(ground: true);
            _driver.SetLocalTarget(gravel);
            yield return LocalMovement("beat-13", gravel + Vector3.up * .08f, 3f);
            yield return Beat("beat-14");
            yield return Beat("beat-15");
            yield return Audio(5, Cues.RebirthGravelEnd);
            Choices.Hide(); Finished = true;
        }

        public IReadOnlyList<BottomAction> VisibleActions()
        {
            var result = new List<BottomAction>();
            void Add(string text, JourneyChoice choice, bool enabled = true, bool selected = false)
                => result.Add(new BottomAction(text, () => Choose(choice), enabled, selected));
            switch (Choices.Gate)
            {
                case JourneyGate.Shelter:
                    Add("Deep Pool Found", JourneyChoice.FirstFound, !Choices.FirstFound, Choices.FirstFound);
                    Add("Water Plants Found", JourneyChoice.SecondFound, !Choices.SecondFound, Choices.SecondFound);
                    Add("Repeat Prompt", JourneyChoice.Repeat); break;
                case JourneyGate.Strider: Add("Receive water strider’s offer", JourneyChoice.Receive); break;
                case JourneyGate.Heron:
                    Add("Offer to Heron", JourneyChoice.Offer); Add("Ignore", JourneyChoice.Ignore); Add("Repeat Prompt", JourneyChoice.Repeat); break;
                case JourneyGate.Return:
                    Add("Jump", JourneyChoice.Jump); Add("We are home now", JourneyChoice.Locate); Add("Repeat Prompt", JourneyChoice.Repeat); break;
                case JourneyGate.Home:
                    Add("We are home now", JourneyChoice.Home); Add("Repeat Prompt", JourneyChoice.Repeat); break;
                case JourneyGate.Spawn: Add("Spawn", JourneyChoice.Spawn); break;
            }
            return result;
        }
        private void OnDestroy()
        {
            foreach (var clip in _segments.Values) if (clip != null) Destroy(clip);
            if (_notification != null) Destroy(_notification);
        }
    }
}
