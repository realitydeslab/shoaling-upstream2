using System.Collections.Generic;
using ShoalingUpstream.Experience;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace ShoalingUpstream.Simulation
{
    // Device-specific presentation; the sequence itself does not reference iPad, ARKit or UI.
    public sealed class StandalonePlayer : MonoBehaviour
    {
        public AudioClip[] Narration;
        [Header("Chapter 1 narration cues — seconds in 01 New Life")]
        public float SearchStartSeconds = 6.5f;
        public float SearchEndSeconds = 16.65f;
        public float SpawnPromptEndSeconds = 33.5f;
        private readonly OpeningChapterOneSequence _intro = new();
        private float _eggsLandedAt;
        public OpeningChapterOneSequence OpeningAndChapterOne => _intro;
        private bool ValidCues => Narration != null && Narration.Length > 1 && Narration[1] != null
            && SearchStartSeconds >= 0f && SearchEndSeconds > SearchStartSeconds
            && SpawnPromptEndSeconds > SearchEndSeconds && SpawnPromptEndSeconds < Narration[1].length;
        private SimulationDriver _driver;
        private GuidedJourneyPlayer _guided;
        private readonly StandaloneJourneySequence _sequence = new();
        private AudioSource _narrator;
        private bool _started, _paused;
        private int _narrationIndex = -1;
        private float _previousTimeScale = 1f;
        private readonly List<ARRaycastHit> _hits = new();

        public StandaloneJourneySequence Sequence => _sequence;
        public AudioSource NarrationSource => _narrator;

        private void Awake()
        {
            _driver = GetComponent<SimulationDriver>();
            _guided = GetComponent<GuidedJourneyPlayer>();
            _narrator = gameObject.AddComponent<AudioSource>();
            _narrator.playOnAwake = false;
            _narrator.loop = false;
            _narrator.spatialBlend = 0f; // narration accompanies the participant, independent of world targets
            _narrator.volume = 0.85f;
            Screen.sleepTimeout = SleepTimeout.NeverSleep;
        }

        private void Update()
        {
            if (!_started || _paused || !_driver.Ready) return;
            if (_intro.State != OpeningChapterOneState.Complete)
            {
                if (_driver.LocalFlockCount("beat-2") > 0) _driver.FollowLocally(true);
                UpdateOpeningAndChapterOne();
                return;
            }
            if (_guided != null) _guided.Begin(_narrator, Narration);
        }


        private void PlayChapterOneAt(float seconds)
        {
            PlayNarration(1);
            _narrator.timeSamples = Mathf.Clamp(Mathf.RoundToInt(seconds * _narrator.clip.frequency), 0, _narrator.clip.samples - 1);
        }

        private void PauseChapterOneAt(float seconds)
        {
            _narrator.Pause();
            _narrator.timeSamples = Mathf.Clamp(Mathf.RoundToInt(seconds * _narrator.clip.frequency), 0, _narrator.clip.samples - 1);
        }

        private void UpdateOpeningAndChapterOne()
        {
            switch (_intro.State)
            {
                case OpeningChapterOneState.OpeningAudio:
                    if (!_narrator.isPlaying) _intro.OpeningEnded();
                    break;
                case OpeningChapterOneState.ChapterOneSearchAudio:
                case OpeningChapterOneState.SearchRepeatAudio:
                    if (_narrator.time >= SearchEndSeconds || !_narrator.isPlaying)
                    {
                        PauseChapterOneAt(SearchEndSeconds);
                        _intro.SearchEnded();
                    }
                    break;
                case OpeningChapterOneState.ChapterOneSpawnAudio:
                    if (_narrator.time >= SpawnPromptEndSeconds || !_narrator.isPlaying)
                    {
                        PauseChapterOneAt(SpawnPromptEndSeconds);
                        _intro.SpawnPromptEnded();
                    }
                    break;
                case OpeningChapterOneState.EggsDropping:
                    if (!_driver.LocalSceneBusy && string.IsNullOrEmpty(_driver.LocalError))
                    {
                        _sequence.CompletePlayback();
                        _intro.EggsLanded();
                        _eggsLandedAt = Time.time;
                    }
                    break;
                case OpeningChapterOneState.EggWait:
                    if (Time.time - _eggsLandedAt >= 6f && _driver.FireLocally("beat-2"))
                    {
                        _sequence.TryBegin();
                        _intro.WaitEnded();
                    }
                    break;
                case OpeningChapterOneState.Hatching:
                    if (!_driver.LocalSceneBusy && string.IsNullOrEmpty(_driver.LocalError))
                    {
                        _sequence.CompletePlayback();
                        _intro.HatchEnded();
                        PlayChapterOneAt(SpawnPromptEndSeconds);
                    }
                    break;
                case OpeningChapterOneState.ChapterOneEndingAudio:
                    FollowWhenTracking();
                    if (!_narrator.isPlaying) _intro.EndingEnded();
                    break;
            }
        }

        private void FollowWhenTracking()
        {
            bool tracking = Camera.main == null || Camera.main.GetComponent<ARCameraManager>() == null
                || ARSession.state == ARSessionState.SessionTracking;
            _driver.FollowLocally(tracking);
        }

        private void RecordGravelPosition()
        {
            if (Camera.main == null) return;
            var camera = Camera.main;
            var manager = FindFirstObjectByType<ARRaycastManager>();
            Vector3 target;
            if (manager != null && manager.Raycast(new Vector2(Screen.width * 0.5f, Screen.height * 0.5f),
                _hits, TrackableType.PlaneWithinPolygon))
                target = _hits[0].pose.position;
            else
            {
                Vector3 forward = camera.transform.forward;
                forward.y = 0f;
                target = camera.transform.position + forward.normalized * 2f - Vector3.up * 1.2f;
            }
            _driver.SetLocalTarget(target);
        }

        public bool StartExperience()
        {
            if (!_driver.Ready || !_intro.Start()) return false;
            _started = true;
            PlayNarration(0);
            return true;
        }

        public bool ContinueToChapterOne()
        {
            if (_paused || !ValidCues || !_intro.Continue()) return false;
            PlayChapterOneAt(0f);
            return true;
        }

        public bool SpawnNewLife()
        {
            if (_paused || _intro.State != OpeningChapterOneState.SpawnChoice || _driver.LocalSceneBusy) return false;
            RecordGravelPosition();
            if (!_driver.FireLocally("beat-1")) return false;
            _sequence.TryBegin();
            _intro.Spawn();
            return true;
        }

        public void ConfirmShelter(bool tree)
        {
            if (_intro.Found(tree) && _intro.State == OpeningChapterOneState.ChapterOneSpawnAudio)
                PlayChapterOneAt(SearchEndSeconds);
        }

        private void PlayNarration(int index)
        {
            _narrationIndex = index;
            _narrator.Stop();
            _narrator.clip = Narration != null && index >= 0 && index < Narration.Length ? Narration[index] : null;
            if (_narrator.clip != null) _narrator.Play();
        }

        private void SetPaused(bool value)
        {
            if (_paused == value) return;
            _paused = value;
            if (_guided != null) _guided.SetSuspended(value);
            if (value)
            {
                _previousTimeScale = Time.timeScale;
                Time.timeScale = 0f;
                _narrator.Pause();
            }
            else
            {
                Time.timeScale = _previousTimeScale;
                _narrator.UnPause();
            }
        }

        private void OnApplicationPause(bool value)
        {
            if (value && _started) SetPaused(true);
        }

        private void OnDisable()
        {
            if (_paused) Time.timeScale = _previousTimeScale;
            Screen.sleepTimeout = SleepTimeout.SystemSetting;
        }

        public IReadOnlyList<BottomAction> VisibleActions()
        {
            var actions = new List<BottomAction>();
            if (_paused) { actions.Add(new BottomAction("Resume", () => SetPaused(false))); return actions; }
            if (_intro.State == OpeningChapterOneState.Complete)
                return _guided != null ? _guided.VisibleActions() : actions;
            switch (_intro.State)
            {
                case OpeningChapterOneState.Start:
                    actions.Add(new BottomAction("Start the Journey", () => StartExperience())); break;
                case OpeningChapterOneState.OpeningChoice:
                    actions.Add(new BottomAction("We are in the creek facing downstream now", () => ContinueToChapterOne(), ValidCues));
                    actions.Add(new BottomAction("Repeat Prompt", () => { if (_intro.Start()) PlayNarration(0); })); break;
                case OpeningChapterOneState.FindShelter:
                    actions.Add(new BottomAction("Tree Found", () => ConfirmShelter(true), !_intro.TreeFound, _intro.TreeFound));
                    actions.Add(new BottomAction("Gravel Found", () => ConfirmShelter(false), !_intro.GravelFound, _intro.GravelFound));
                    actions.Add(new BottomAction("Repeat Prompt", () => { if (_intro.RepeatSearch()) PlayChapterOneAt(SearchStartSeconds); })); break;
                case OpeningChapterOneState.SpawnChoice:
                    actions.Add(new BottomAction("Spawn", () => SpawnNewLife())); break;
            }
            return actions;
        }

        private void OnGUI()
        {
            if (!string.IsNullOrEmpty(_driver.LocalError)) { BottomActionBar.Message(_driver.LocalError); return; }
            if (_guided != null && !string.IsNullOrEmpty(_guided.Error)) { BottomActionBar.Message(_guided.Error); return; }
            if (!_driver.Ready) return;
            if (_guided != null && _guided.Finished) { BottomActionBar.Message("The End", centre: true); return; }
            BottomActionBar.Draw(VisibleActions());
        }
    }
}
