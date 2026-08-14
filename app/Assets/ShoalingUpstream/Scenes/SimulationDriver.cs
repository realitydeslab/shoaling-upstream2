using System;
using System.Collections;
using System.Collections.Generic;
using ShoalingUpstream.Audio;
using ShoalingUpstream.Config;
using ShoalingUpstream.Control;
using ShoalingUpstream.Journey;
using ShoalingUpstream.Localization;
using UnityEngine;
using UnityEngine.Networking;
#if NSDK_PRESENT
using ShoalingUpstream.Localization.Nsdk;
#endif

namespace ShoalingUpstream.Simulation
{
    /// <summary>
    /// The desk. Everything the piece is, wired together and driven by the browser's scrubber.
    ///
    /// This exists because the artwork is made at a laptop and performed in a creek, and until
    /// now the only way to see the Unity half was to read a test report. It is glue and a heads-up
    /// display and nothing else: every decision worth arguing about is in Runtime/, which this
    /// only assembles.
    ///
    /// It lives outside Runtime/ deliberately. Nothing here should ship on a phone — the phone's
    /// entry point localizes against VPS and has no reason to draw a debug overlay or spawn a
    /// sphere at every beat.
    /// </summary>
    [AddComponentMenu("Shoaling Upstream/Simulation Driver")]
    public sealed class SimulationDriver : MonoBehaviour
    {
        [Header("Service")]
        [Tooltip("The laptop running service/src/server.mjs. It prints this address on startup.")]
        public string ServiceHost = "127.0.0.1";
        public int ServicePort = 8710;
        public string Slug = "ubc-nitobe-garden-creek";

        [Header("View")]
        [Tooltip("Raise the camera above the centreline. Zero because the centreline is authored "
                 + "at the phone's own height — it is the path the chest walks, not the creek bed.")]
        public float EyeOffsetM = 0f;

        [Tooltip("The inset map in the corner. The first-person view alone makes it hard to tell "
                 + "a walk from a stall.")]
        public bool ShowOverview = true;

        /// <summary>
        /// Set before the scene loads to point it somewhere else. For the PlayMode fixture, which
        /// runs a scratch service on a random port; nothing else should touch it.
        /// </summary>
        public static string HostOverride;
        public static int PortOverride;

        // --- the piece ------------------------------------------------------
        private ControlLink _link;
        private JourneyAudioRunner _audio;
        private JourneyProgression _progression;
        private JourneyDocument _journey;
        private VpsLocalizer _gate;
#if NSDK_PRESENT
        private VpsLocalizationRunner _vps;
#endif

        // --- the view -------------------------------------------------------
        private Camera _eye;
        private Camera _overview;
        private readonly Dictionary<string, Renderer> _markers = new();
        private readonly Dictionary<string, float> _flashUntil = new();
        private LineRenderer _path;
        private Transform _walkerPin;

        // --- what the HUD says ----------------------------------------------
        private string _journeyNote = "loading…";
        private string _gateNote = "";
        private string _audioNote = "";
        private readonly List<string> _log = new();
        private int _shoalCount;

        public JourneyDocument Journey => _journey;
        public JourneyProgression Progression => _progression;
        public ControlLink Link => _link;
        public bool Ready => _progression != null;

        // ------------------------------------------------------------------ startup

        private void Awake()
        {
            if (!string.IsNullOrEmpty(HostOverride)) ServiceHost = HostOverride;
            if (PortOverride > 0) ServicePort = PortOverride;

            _link = GetComponent<ControlLink>();
            _audio = GetComponent<JourneyAudioRunner>();
#if NSDK_PRESENT
            _vps = GetComponent<VpsLocalizationRunner>();
#endif
            _link.Host = ServiceHost;
            _link.Port = ServicePort;

            _eye = Camera.main;
            BuildOverviewCamera();
        }

        private IEnumerator Start()
        {
            yield return LoadJourney();
            if (_journey != null) Begin();
        }

        /// <summary>
        /// Where the journey comes from, and why it is not the one the phone would load.
        ///
        /// The app reads PUBLISHED revisions, and for this site the published revision is the old
        /// seeded layout — a 3-point, 34 m straight ending barrier/headwater, against a draft that
        /// is 16 points, 18.8 m, ending falls/spawn. Worse, the draft's revision is 0 and the
        /// published one is 1, and <see cref="JourneyResolver"/> settles ties by taking the higher
        /// revision. So a draft can never win that contest: offered alongside anything published
        /// it is refused as superseded, by design.
        ///
        /// A simulation scene that showed the artist a reach they are not editing would be worse
        /// than useless, so this asks the service for the DRAFT and offers it to the resolver
        /// alone. The precedence rule is deliberately not consulted — a draft is not a revision
        /// and has nothing to be ranked against. Every other check still applies: it must parse,
        /// it must be for this site, and it must pass the calibration gate.
        ///
        /// If the service is not running there is no draft to be had, and it falls back to what a
        /// phone would do. The HUD says which of the two happened, in red when it is the fallback,
        /// because the difference is four beats and sixteen metres.
        /// </summary>
        private IEnumerator LoadJourney()
        {
            string url = $"http://{ServiceHost}:{ServicePort}/api/sites/{Slug}/draft";
            using (var request = UnityWebRequest.Get(url))
            {
                request.timeout = 5;
                yield return request.SendWebRequest();

                if (request.result == UnityWebRequest.Result.Success)
                {
                    var resolution = JourneyResolver.Resolve(
                        new[] { new JourneyCandidate(JourneySourceKind.Service, request.downloadHandler.text) },
                        JourneyEnvironment.Simulation, Slug);

                    if (resolution.HasJourney)
                    {
                        _journey = resolution.Document;
                        _journeyNote = $"draft r{_journey.revision} from the service — "
                                       + $"{_journey.site.centreline.Count} points, "
                                       + $"{Centreline.Length(_journey.site.centreline):0.0} m";
                        JourneyProvider.Log(resolution);
                        yield break;
                    }

                    _journeyNote = "the service's draft was refused: "
                                   + string.Join("; ", resolution.Notes);
                    Debug.LogError($"[simulation] {_journeyNote}");
                    yield break;
                }
            }

            // No laptop. Fall back to the phone's own precedence so the scene still opens
            // something, and say loudly that it is not what is being edited.
            var provider = JourneyProvider.ForLaunch(new JourneyProviderSettings
            {
                Slug = Slug,
                Environment = JourneyEnvironment.Simulation,
            });
            var task = provider.ResolveAsync();
            while (!task.IsCompleted) yield return null;

            JourneyProvider.Log(task.Result);
            if (!task.Result.HasJourney)
            {
                _journeyNote = "no journey from anywhere — is the service running on "
                               + $"{ServiceHost}:{ServicePort}?";
                Debug.LogError($"[simulation] {_journeyNote}");
                yield break;
            }

            _journey = task.Result.Document;
            _journeyNote = $"!! PUBLISHED r{_journey.revision} from {task.Result.Source} — "
                           + "the service was unreachable, so this is NOT your draft";
        }

        private void Begin()
        {
            // The calibration gate, on the surface that is allowed to run an uncalibrated
            // journey. Every site is uncalibrated today, so this is the only surface that runs at
            // all — and it is worth seeing rather than assuming.
            _gate = new VpsLocalizer(_journey, null, RuntimeSurface.Simulation);
            _gateNote = _gate.Mode == LocalizationMode.Refused
                ? $"REFUSED: {_gate.RefusalReason}"
                : "passed (simulation surface)";
            if (_gate.Mode == LocalizationMode.Refused)
            {
                Debug.LogError($"[simulation] calibration gate {_gateNote}");
                return;
            }

#if NSDK_PRESENT
            // The device path, wired and running, so what it does at a desk is visible rather
            // than imagined: with no AR session it samples nothing and never reaches a fix, which
            // is exactly why the pose channel below exists.
            if (_vps != null) _vps.Run(_journey);
#endif

            _progression = new JourneyProgression(_journey);
            _shoalCount = _journey.shoal?.startingCount ?? 40;

            _link.SetCentreline(_journey.site.centreline);
            _link.SetEffects(new Effects(this));
            _link.SetStatusSource(new Status(this));

            _audio.forceSimulation = true;   // the desk renderer, not PHASE
            _audio.listener = _eye != null ? _eye.transform : transform;
            _audio.Begin(_journey, new StreamingAssetsClipResolver());
            _audioNote = _audio.Engine != null
                ? $"{_audio.Engine.BackendDescription}"
                  + (_audio.Engine.MissingClips.Count > 0
                     ? $" — {_audio.Engine.MissingClips.Count} clip(s) missing"
                     : "")
                : "not started";

            BuildMarkers();
            Note($"journey ready: {_journeyNote}");
        }

        // ------------------------------------------------------------------ the frame

        private void Update()
        {
            if (_progression == null) return;

            // Whichever source is live. At a desk that is always the browser's scrubber; on the
            // creek it would be VPS, and nothing below can tell the difference — which is the
            // whole point of the seam.
            bool moving = _link.Poses.TryGetPose(_link.NowMs, out var pose);
            if (moving)
            {
                _progression.Tick(pose.AnchorLocalPosition, pose.Quality, Time.deltaTime);
                PlaceWalker(pose);
            }
#if NSDK_PRESENT
            else if (_vps != null && _vps.Localizer != null)
            {
                var fix = _vps.Localizer.Fix;
                if (fix.Quality != LocalizationQuality.Unavailable)
                {
                    _progression.Tick(fix.JourneyPosition, fix.Quality, Time.deltaTime);
                }
            }
#endif

            foreach (var journeyEvent in _progression.DrainEvents())
            {
                _audio.Handle(journeyEvent);
                Note(journeyEvent.ToString());
                if (journeyEvent.Kind == JourneyProgression.Event.Type.Completed
                    && journeyEvent.Beat != null)
                {
                    _flashUntil[journeyEvent.Beat.id] = Time.time + 0.6f;
                }
            }

            _audio.SetShoalCount(_shoalCount);
            PaintMarkers();
        }

        private void PlaceWalker(PoseSample pose)
        {
            Vector3 at = pose.AnchorLocalPosition + Vector3.up * EyeOffsetM;
            if (_eye != null)
            {
                _eye.transform.position = at;
                // Heading comes from the path rather than from any look direction: the body
                // follows the route and it is the body that decides which beats are near.
                _eye.transform.rotation = Quaternion.Euler(0f, pose.HeadingRad * Mathf.Rad2Deg, 0f);
            }
            if (_walkerPin != null) _walkerPin.position = at;
        }

        // ------------------------------------------------------------------ the view

        private void BuildOverviewCamera()
        {
            if (!ShowOverview) return;

            var go = new GameObject("Overview Camera");
            go.transform.SetParent(transform, false);
            _overview = go.AddComponent<Camera>();
            _overview.orthographic = true;
            _overview.depth = 1;                       // drawn over the eye view
            _overview.rect = new Rect(0.68f, 0.02f, 0.30f, 0.30f);
            _overview.clearFlags = CameraClearFlags.SolidColor;
            _overview.backgroundColor = new Color(0.05f, 0.07f, 0.09f);
            _overview.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
        }

        /// <summary>A sphere at every beat and a line down the reach. Crude on purpose: the
        /// question this scene answers is "did anything happen", and that has to be answerable
        /// from across a room.</summary>
        private void BuildMarkers()
        {
            var centreline = _journey.site.centreline;

            var pathGo = new GameObject("Centreline");
            pathGo.transform.SetParent(transform, false);
            _path = pathGo.AddComponent<LineRenderer>();
            _path.material = new Material(Shader.Find("Sprites/Default"));
            _path.widthMultiplier = 0.06f;
            _path.positionCount = centreline.Count;
            _path.startColor = _path.endColor = new Color(0.35f, 0.6f, 0.75f);
            for (int i = 0; i < centreline.Count; i++) _path.SetPosition(i, centreline[i].ToVector3());

            foreach (var beat in _journey.beats)
            {
                var marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                marker.name = $"Beat — {beat.id}";
                marker.transform.SetParent(transform, false);
                marker.transform.position = beat.position.ToVector3();
                marker.transform.localScale = Vector3.one * 0.5f;
                Destroy(marker.GetComponent<Collider>());
                _markers[beat.id] = marker.GetComponent<Renderer>();
            }

            var pin = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            pin.name = "Walker";
            pin.transform.SetParent(transform, false);
            pin.transform.localScale = new Vector3(0.3f, 0.5f, 0.3f);
            Destroy(pin.GetComponent<Collider>());
            pin.GetComponent<Renderer>().material.color = Color.white;
            _walkerPin = pin.transform;

            FrameOverview(centreline);
        }

        private void FrameOverview(IReadOnlyList<Vec3> centreline)
        {
            if (_overview == null) return;

            var min = centreline[0].ToVector3();
            var max = min;
            foreach (var point in centreline)
            {
                min = Vector3.Min(min, point.ToVector3());
                max = Vector3.Max(max, point.ToVector3());
            }
            var centre = (min + max) * 0.5f;
            _overview.transform.position = new Vector3(centre.x, max.y + 30f, centre.z);
            _overview.orthographicSize = Mathf.Max(max.x - min.x, max.z - min.z) * 0.65f + 2f;
        }

        private void PaintMarkers()
        {
            foreach (var beat in _journey.beats)
            {
                if (!_markers.TryGetValue(beat.id, out var renderer) || renderer == null) continue;

                Color colour = _progression.StateOf(beat.id) switch
                {
                    BeatState.Approaching => new Color(1f, 0.75f, 0.2f),   // armed
                    BeatState.Committed => new Color(1f, 0.35f, 0.15f),    // firing
                    BeatState.AwaitingAction => new Color(0.9f, 0.3f, 0.9f), // needs a gesture
                    BeatState.Complete => new Color(0.3f, 0.85f, 0.45f),
                    _ => new Color(0.35f, 0.38f, 0.42f),
                };

                if (_flashUntil.TryGetValue(beat.id, out float until) && Time.time < until)
                {
                    colour = Color.Lerp(Color.white, colour, (until - Time.time) / 0.6f);
                }
                renderer.material.color = colour;
            }
        }

        // ------------------------------------------------------------------ the readout

        private void Note(string line)
        {
            _log.Add(line);
            if (_log.Count > 8) _log.RemoveAt(0);
        }

        /// <summary>
        /// IMGUI rather than a Canvas: no prefab, no font asset, no import step, and it survives
        /// a scene rebuilt from a script with nothing to reconnect.
        /// </summary>
        private void OnGUI()
        {
            var style = new GUIStyle(GUI.skin.label)
            {
                fontSize = 15,
                richText = true,
                alignment = TextAnchor.UpperLeft,
            };
            style.normal.textColor = Color.white;

            GUI.color = new Color(0f, 0f, 0f, 0.65f);
            GUI.DrawTexture(new Rect(8, 8, 560, 210), Texture2D.whiteTexture);
            GUI.color = Color.white;

            string connection = _link == null ? "—" : _link.Client.State.ToString();
            string connectionColour = _link != null && _link.Client.State == ControlLinkState.Online
                ? "#7fdca0" : "#ff8f6b";

            var text = new System.Text.StringBuilder();
            text.AppendLine($"<b>Shoaling Upstream — simulation</b>   service {ServiceHost}:{ServicePort}");
            text.AppendLine($"link      <color={connectionColour}>{connection}</color>"
                            + (string.IsNullOrEmpty(_link?.Client.LastNote) ? "" : $"  ({_link.Client.LastNote})"));
            text.AppendLine($"journey   {(_journeyNote.StartsWith("!!") ? $"<color=#ff8f6b>{_journeyNote}</color>" : _journeyNote)}");
            text.AppendLine($"gate      {_gateNote}");
            text.AppendLine($"audio     {_audioNote}");

            if (_progression != null)
            {
                bool live = _link.Poses.TryGetPose(_link.NowMs, out var pose);
                text.AppendLine($"s         <b>{_progression.S:0.00} m</b>"
                                + $"   lateral {_progression.Lateral:0.00} m"
                                + $"   {(live ? _link.Poses.Origin.ToString().ToLowerInvariant() : "<color=#ff8f6b>no pose — scrub the browser</color>")}");
                text.AppendLine($"beat      {_progression.Current?.id ?? "—"}"
                                + $"   done {_progression.Completed.Count}/{_journey.beats.Count}"
                                + $"   shoal {_shoalCount}");
            }
            else
            {
                text.AppendLine("s         —");
            }
            text.AppendLine($"<color=#9fb4c4>{string.Join("  ·  ", _log)}</color>");

            GUI.Label(new Rect(16, 12, 548, 200), text.ToString(), style);
        }

        // ------------------------------------------------------------------ the bus

        /// <summary>What the operator's buttons are allowed to do. Every method answers whether it
        /// actually did anything, because that answer is the ack.</summary>
        private sealed class Effects : IControlEffects
        {
            private readonly SimulationDriver _driver;
            public Effects(SimulationDriver driver) => _driver = driver;

            public bool FireBeat(string beatId)
            {
                if (_driver._progression == null || string.IsNullOrEmpty(beatId)) return false;
                bool fired = _driver._progression.ForceBeat(beatId);
                if (fired) _driver.Note($"operator fired {beatId}");
                return fired;
            }

            public bool ReplayCurrent()
            {
                var current = _driver._progression?.Current;
                return current != null && FireBeat(current.id);
            }

            public bool Advance()
            {
                var journey = _driver._journey;
                if (journey == null) return false;
                foreach (var beat in journey.beats)
                {
                    if (_driver._progression.StateOf(beat.id) == BeatState.Complete) continue;
                    return FireBeat(beat.id);
                }
                return false;
            }

            // Refused rather than faked. The audio engine has no mute, and an operator seeing a
            // button confirmed when nothing happened is worse than seeing it refused.
            public bool Silence() => false;
            public bool Resume() => false;
        }

        private sealed class Status : IControlStatusSource
        {
            private readonly SimulationDriver _driver;
            public Status(SimulationDriver driver) => _driver = driver;

            public ControlStatus Snapshot()
            {
                var progression = _driver._progression;
                return new ControlStatus
                {
                    Site = _driver._journey?.site?.slug,
                    Revision = _driver._journey?.revision,
                    CurrentBeat = progression?.Current?.id,
                    HighWaterMark = progression?.HighWaterMark ?? -1,
                    Completed = progression?.Completed ?? Array.Empty<string>(),
                    ShoalCount = _driver._shoalCount,
                    TrackingConfidence = progression?.Quality == LocalizationQuality.Precise ? 1f : 0f,
                    // S, LateralM and Localization are left null: the client fills them from
                    // whichever pose source is live, which is the one steering the piece.
                };
            }
        }
    }
}
