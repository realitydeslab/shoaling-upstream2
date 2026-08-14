using System;
using System.Collections.Generic;
using ShoalingUpstream.Journey;

namespace ShoalingUpstream.Control
{
    public enum ControlLinkState { Offline, Connecting, Online }

    /// <summary>
    /// The device's half of the control bus, with no Unity in it.
    ///
    /// A plain class driven by <see cref="Pump"/> rather than a MonoBehaviour, because every
    /// interesting behaviour here is about time and ordering — a command arriving after its
    /// moment, a clock that is 300 ms out, a socket dropping mid-beat — and none of those are
    /// reproducible from a scene. <see cref="ControlLink"/> is the thin MonoBehaviour that calls
    /// Pump each frame; everything below is exercised in EditMode against a fake transport and
    /// a clock the test moves by hand.
    ///
    /// Nothing in this class blocks. A dead service, a dropped socket and a laptop that never
    /// existed are all the same to it: the piece keeps running on VPS, silently, which is what
    /// it does at the creek anyway.
    /// </summary>
    public sealed class ControlClient
    {
        // --- what the far end is told about us ------------------------------
        public string DeviceName = "iPhone";
        public string OsName = "iOS";
        public string BuildName = "dev";

        // --- cadence ---------------------------------------------------------
        /// <summary>Steady heartbeat. Well inside the bus's 12 s presence timeout, so a device
        /// that is merely quiet never goes grey in the operator's window.</summary>
        public double HeartbeatIntervalMs = 3000;

        /// <summary>
        /// The first few heartbeats go out fast.
        ///
        /// Not for presence — for the clock. Each pong is one offset sample and the estimate is
        /// useless until there are a few, so a burst at connect buys a trusted offset within a
        /// couple of seconds instead of ten. Until then commands run on relative timing, which
        /// works but loses multi-device agreement.
        /// </summary>
        public double FastHeartbeatIntervalMs = 400;
        public int FastHeartbeatCount = 5;

        /// <summary>A heartbeat with no answer by now is written off, so one lost pong does not
        /// stall the clock estimate forever.</summary>
        public double HeartbeatTimeoutMs = 8000;

        /// <summary>
        /// After this many unanswered heartbeats the link is declared dead and reconnected.
        ///
        /// A park's wifi fails by staying associated and routing nowhere, so the socket happily
        /// reports itself open while nothing crosses it. Without this the device would look
        /// connected, report nothing, and act on nothing, which is the one failure the operator
        /// cannot diagnose from their end.
        /// </summary>
        public int UnansweredHeartbeatLimit = 3;

        /// <summary>
        /// A socket that opens and then never greets us is a socket to the wrong service, or one
        /// whose far end died between the handshake and the welcome. Either way, waiting forever
        /// in Connecting is worse than starting again.
        /// </summary>
        public double WelcomeTimeoutMs = 5000;

        public double StatusIntervalMs = 250;

        /// <summary>Even an unchanged status is re-sent this often: the operator's window ages
        /// what it displays, and silence there is indistinguishable from a dead phone.</summary>
        public double StatusKeepAliveMs = 2000;

        private readonly IControlTransport _transport;
        private readonly SimulatedPoseSource _simulated;
        private readonly PoseSourceSwitch _poses;
        private readonly ServerClock _clock = new();
        private readonly CommandScheduler _scheduler;
        private readonly ReconnectPolicy _reconnect;
        private readonly List<CommandOutcome> _due = new();

        private IControlEffects _effects;
        private IControlStatusSource _status;

        private string _url;
        private string _sessionId;
        private bool _saidHello;
        private double _reconnectAtMs = double.NegativeInfinity;
        private double _heartbeatSentAtMs = double.NaN;
        private double _nextHeartbeatAtMs;
        private int _heartbeatsSent;
        private int _unansweredHeartbeats;
        private double _connectingSinceMs;
        private double _nextStatusAtMs;
        private double _lastStatusSentAtMs = double.NegativeInfinity;
        private ControlStatus _lastStatus;
        private bool _hasSentStatus;

        public ControlClient(IControlTransport transport,
                             PoseSourceSwitch poses,
                             SimulatedPoseSource simulated,
                             IControlEffects effects = null,
                             IControlStatusSource status = null,
                             ReconnectPolicy reconnect = null)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _poses = poses ?? throw new ArgumentNullException(nameof(poses));
            _simulated = simulated ?? throw new ArgumentNullException(nameof(simulated));
            _effects = effects;
            _status = status ?? EmptyStatusSource.Instance;
            _reconnect = reconnect ?? new ReconnectPolicy();
            _scheduler = new CommandScheduler(_clock);
        }

        public ControlLinkState State { get; private set; } = ControlLinkState.Offline;

        public string SessionId => _sessionId;

        public ServerClock Clock => _clock;

        public CommandScheduler Scheduler => _scheduler;

        /// <summary>Last thing worth telling a human. Shown on the in-app debug overlay, since
        /// nobody is reading a console in a creek.</summary>
        public string LastNote { get; private set; }

        public void SetEffects(IControlEffects effects) => _effects = effects;

        public void SetStatusSource(IControlStatusSource status) =>
            _status = status ?? EmptyStatusSource.Instance;

        /// <summary>Build the device URL for a host. The role matters: an operator connection
        /// would receive commandIssued instead of command and never fire anything.</summary>
        public static string DeviceUrl(string host, int port = 8710) =>
            $"ws://{host}:{port}/ws?role=device";

        public void Connect(string url, double nowLocalMs)
        {
            _url = url;
            _reconnectAtMs = double.NegativeInfinity;
            _reconnect.Succeeded();
            OpenSocket(nowLocalMs);
        }

        public void Disconnect()
        {
            _url = null;
            _transport.Close();
            GoOffline();
        }

        /// <summary>
        /// One step. Called every frame; safe to call at any rate, including not at all for a
        /// while, which is what happens when the app is backgrounded.
        /// </summary>
        public void Pump(double nowLocalMs)
        {
            PumpConnection(nowLocalMs);
            PumpInbound(nowLocalMs);
            PumpSchedule(nowLocalMs);
            PumpHeartbeat(nowLocalMs);
            PumpStatus(nowLocalMs);
        }

        // ------------------------------------------------------------------ connection

        private void OpenSocket(double nowLocalMs)
        {
            _saidHello = false;
            _heartbeatsSent = 0;
            _unansweredHeartbeats = 0;
            _heartbeatSentAtMs = double.NaN;
            _nextHeartbeatAtMs = nowLocalMs;
            _connectingSinceMs = nowLocalMs;
            // A new socket may be a new service on a new machine. Offsets measured against the
            // old one describe a relationship that no longer holds.
            _clock.Reset();
            _scheduler.Clear();
            State = ControlLinkState.Connecting;
            _transport.Connect(_url);
        }

        private void PumpConnection(double nowLocalMs)
        {
            if (_url is null) return;

            switch (_transport.State)
            {
                case TransportState.Open:
                case TransportState.Connecting:
                    // Open is not yet Online. Online means welcomed, because the welcome carries
                    // the session id that scopes every ack we are about to send.
                    if (State == ControlLinkState.Offline) State = ControlLinkState.Connecting;
                    if (State == ControlLinkState.Connecting
                        && nowLocalMs - _connectingSinceMs > WelcomeTimeoutMs)
                    {
                        Fail("connected but never greeted", nowLocalMs);
                    }
                    break;

                default:
                    if (State != ControlLinkState.Offline)
                    {
                        Fail(_transport.LastError ?? "socket closed", nowLocalMs);
                    }
                    else if (nowLocalMs >= _reconnectAtMs)
                    {
                        OpenSocket(nowLocalMs);
                    }
                    break;
            }
        }

        /// <summary>Give up on this connection and arrange another. The transport is closed
        /// explicitly because a half-open socket will not close itself.</summary>
        private void Fail(string note, double nowLocalMs)
        {
            LastNote = note;
            _transport.Close();
            GoOffline();
            _reconnectAtMs = nowLocalMs + _reconnect.NextDelayMs();
        }

        private void GoOffline()
        {
            State = ControlLinkState.Offline;
            _sessionId = null;
            _hasSentStatus = false;
            _scheduler.Clear();
            // The operator is gone, so nobody is scrubbing. Dropping the simulated pose rather
            // than letting its lease run out hands the piece back to VPS at once, instead of
            // leaving the visitor pinned to wherever the last frame put them.
            _simulated.Clear();
        }

        // ------------------------------------------------------------------ inbound

        private void PumpInbound(double nowLocalMs)
        {
            while (_transport.TryReceive(out var frame))
            {
                var message = ControlProtocol.Parse(frame);
                if (message is null)
                {
                    // Rubbish on the socket is not a reason to stop reading it.
                    LastNote = "unparseable frame";
                    continue;
                }
                Handle(message, nowLocalMs);
            }
        }

        private void Handle(ControlMessage message, double nowLocalMs)
        {
            switch (message.Kind)
            {
                case ControlMessageKind.Welcome:
                    _sessionId = message.SessionId;
                    State = ControlLinkState.Online;
                    _reconnect.Succeeded();
                    if (!_saidHello)
                    {
                        _transport.Send(ControlProtocol.Hello(DeviceName, OsName, BuildName));
                        _saidHello = true;
                    }
                    break;

                case ControlMessageKind.Pong:
                    _unansweredHeartbeats = 0;
                    if (!double.IsNaN(_heartbeatSentAtMs))
                    {
                        _clock.Observe(_heartbeatSentAtMs, nowLocalMs, message.ServerNowMs);
                        _heartbeatSentAtMs = double.NaN;
                    }
                    break;

                case ControlMessageKind.Pose:
                    ApplyStreamedPose(message, nowLocalMs);
                    break;

                case ControlMessageKind.Command:
                    Receive(message.Command, nowLocalMs);
                    break;

                case ControlMessageKind.Error:
                    LastNote = $"service: {message.Message}";
                    break;
            }
        }

        /// <summary>
        /// The editor's walk simulation, applied the moment it lands.
        ///
        /// Not scheduled, not queued, not acked. A pose is state rather than an instruction:
        /// latest wins and there is no history worth keeping — after a wifi stall the operator
        /// wants where the walker is now, not ten seconds of replay at ten times speed. The bus
        /// sends a pose with no fireAtMs at all, so unlike the simulatePose command there is no
        /// schedule to honour even if one were wanted.
        ///
        /// The cost, and it is a real one: a beat the device fires by crossing a trigger against
        /// a followed pose lands up to a lead time before an operator's fireBeat aimed at the
        /// same moment, because the command is scheduled and this is not. Two time bases, and
        /// that is the trade the separate message type makes. On a desk, where this channel is
        /// the whole point, the walk being live is worth more than agreeing with a button.
        ///
        /// The pose's `slug` is not checked against the site this build is walking. Nothing here
        /// knows that slug — see docs/unity-integration.md.
        /// </summary>
        private void ApplyStreamedPose(ControlMessage message, double nowLocalMs)
        {
            if (!message.HasPose)
            {
                // Every field nulled is what the bus sends when the editor has nothing to say.
                // Leaving the previous pose standing is right: it expires on its own lease.
                LastNote = "a pose arrived carrying neither s nor a position";
                return;
            }
            _simulated.Apply(message.Pose, nowLocalMs);
        }

        private void Receive(ControlCommand command, double nowLocalMs)
        {
            var outcome = _scheduler.Enqueue(command, nowLocalMs, _sessionId);
            if (outcome is null) return;

            switch (outcome.Disposition)
            {
                case CommandDisposition.ExpiredOnArrival:
                    // Worth acking: the operator pressed a button and needs to know it did not
                    // play, rather than watching for a sound that is never coming.
                    Ack(command, false, outcome.Note);
                    LastNote = $"{command.Action} arrived too late";
                    break;

                case CommandDisposition.StaleSession:
                    // No ack at all. The server discards acks from another session, so sending
                    // one would only be noise; and the command itself refers to a service that
                    // is no longer there.
                    LastNote = "dropped a command from a previous service session";
                    break;
            }
        }

        // ------------------------------------------------------------------ schedule

        private void PumpSchedule(double nowLocalMs)
        {
            _due.Clear();
            _scheduler.Pump(nowLocalMs, _due);

            foreach (var outcome in _due)
            {
                if (outcome.Disposition == CommandDisposition.ExpiredWaiting)
                {
                    Ack(outcome.Command, false, outcome.Note);
                    continue;
                }
                Apply(outcome.Command, nowLocalMs);
            }
        }

        private void Apply(ControlCommand command, double nowLocalMs)
        {
            if (command.Action == ControlActions.SimulatePose)
            {
                if (ControlProtocol.TryReadPose(command, out var pose))
                {
                    _simulated.Apply(pose, nowLocalMs);
                }
                // Not acked. At scrub rate this is a position stream, and acking every frame
                // would spend the socket on bookkeeping the operator cannot read anyway.
                return;
            }

            if (_effects is null)
            {
                Ack(command, false, "nothing wired to play it");
                return;
            }

            bool applied = command.Action switch
            {
                ControlActions.FireBeat => _effects.FireBeat(command.BeatId),
                ControlActions.ReplayCurrent => _effects.ReplayCurrent(),
                ControlActions.Advance => _effects.Advance(),
                ControlActions.Silence => _effects.Silence(),
                ControlActions.Resume => _effects.Resume(),
                _ => false,
            };

            Ack(command, applied, applied ? null : $"unhandled: {command.Action}");
        }

        private void Ack(ControlCommand command, bool applied, string note)
        {
            // The session echoed is the COMMAND's, not the connection's. They differ only after
            // a service restart, and in that case the server drops the ack — which is correct,
            // because the id refers to a command log that no longer exists.
            if (string.IsNullOrEmpty(command.SessionId)) return;
            _transport.Send(ControlProtocol.Ack(command.Id, applied, note, command.SessionId));
        }

        // ------------------------------------------------------------------ heartbeat

        private void PumpHeartbeat(double nowLocalMs)
        {
            if (State != ControlLinkState.Online) return;

            if (!double.IsNaN(_heartbeatSentAtMs))
            {
                if (nowLocalMs - _heartbeatSentAtMs < HeartbeatTimeoutMs) return;

                _heartbeatSentAtMs = double.NaN;   // written off; the round trip is unmeasurable
                if (++_unansweredHeartbeats >= UnansweredHeartbeatLimit)
                {
                    Fail("no answer to heartbeats — the socket is open but routes nowhere", nowLocalMs);
                    return;
                }
            }

            if (nowLocalMs < _nextHeartbeatAtMs) return;

            _transport.Send(ControlProtocol.Heartbeat());
            _heartbeatSentAtMs = nowLocalMs;
            _heartbeatsSent++;
            _nextHeartbeatAtMs = nowLocalMs + (_heartbeatsSent < FastHeartbeatCount
                ? FastHeartbeatIntervalMs
                : HeartbeatIntervalMs);
        }

        // ------------------------------------------------------------------ status

        private void PumpStatus(double nowLocalMs)
        {
            if (State != ControlLinkState.Online) return;
            if (nowLocalMs < _nextStatusAtMs) return;
            _nextStatusAtMs = nowLocalMs + StatusIntervalMs;

            var status = Compose(nowLocalMs);
            bool changed = !_hasSentStatus || status.MateriallyDiffers(_lastStatus);
            bool stale = nowLocalMs - _lastStatusSentAtMs >= StatusKeepAliveMs;
            if (!changed && !stale) return;

            _transport.Send(ControlProtocol.Status(status));
            _lastStatus = status;
            _lastStatusSentAtMs = nowLocalMs;
            _hasSentStatus = true;
        }

        /// <summary>
        /// Take the journey's own view of itself and fill the gaps from the live pose.
        ///
        /// Note whose pose: <see cref="PoseSourceSwitch"/>'s, not the VPS source's. Whatever is
        /// steering the piece is what gets reported, so an operator watching a simulated walk
        /// sees the same numbers the beats are actually being fired against.
        /// </summary>
        private ControlStatus Compose(double nowLocalMs)
        {
            var status = _status.Snapshot();

            if (_poses.TryGetPose(nowLocalMs, out var pose))
            {
                status.S ??= pose.S;
                status.LateralM ??= pose.LateralM;
                status.Localization ??= Describe(pose.Quality);
            }
            else
            {
                status.Localization ??= "unavailable";
            }

            status.Completed ??= Array.Empty<string>();
            return status;
        }

        private static string Describe(LocalizationQuality quality) => quality switch
        {
            LocalizationQuality.Precise => "precise",
            LocalizationQuality.Coarse => "coarse",
            _ => "unavailable",
        };
    }
}
