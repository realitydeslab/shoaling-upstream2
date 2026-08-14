using System.Collections.Generic;
using ShoalingUpstream.Journey;
using UnityEngine;

namespace ShoalingUpstream.Control
{
    /// <summary>
    /// The scene's handle on the control bus.
    ///
    /// Almost nothing happens here, and that is the design. All this does is own the real socket,
    /// call <see cref="ControlClient.Pump"/> once a frame, and expose the pose source that
    /// everything downstream reads. Every decision worth arguing about lives in a plain class
    /// that a test can drive.
    ///
    /// The frame loop is never allowed to wait on the network. Pump drains a queue and returns;
    /// if the service is gone, or was never there, the piece keeps running on VPS and nothing
    /// here notices except a state pill on a debug overlay.
    /// </summary>
    [AddComponentMenu("Shoaling Upstream/Control Link")]
    public sealed class ControlLink : MonoBehaviour
    {
        [Header("Service")]
        [Tooltip("The laptop running service/src/server.mjs. It prints this address on startup.")]
        public string Host = "127.0.0.1";
        public int Port = 8710;
        public bool ConnectOnStart = true;

        [Header("Pose")]
        [Tooltip("Auto follows the editor's walk simulation while it is being driven, and hands "
                 + "back to VPS a couple of seconds after it stops.")]
        public PoseSourceMode Mode = PoseSourceMode.Auto;

        [Header("Identity")]
        public string BuildName = "dev";

        private readonly LocalClock _clock = new();
        private WebSocketControlTransport _transport;

        public CentrelineFrame Frame { get; private set; }
        public VpsPoseSource Vps { get; private set; }
        public SimulatedPoseSource Simulated { get; private set; }

        /// <summary>What the rest of the app reads. It cannot tell which source is behind it,
        /// which is the point of the whole folder.</summary>
        public PoseSourceSwitch Poses { get; private set; }

        public ControlClient Client { get; private set; }

        public double NowMs => _clock.NowMs;

        private void Awake()
        {
            Frame = new CentrelineFrame();
            Vps = new VpsPoseSource(Frame);
            Simulated = new SimulatedPoseSource(Frame);
            Poses = new PoseSourceSwitch(Vps, Simulated) { Mode = Mode };

            _transport = new WebSocketControlTransport();
            Client = new ControlClient(_transport, Poses, Simulated)
            {
                DeviceName = SystemInfo.deviceModel,
                OsName = SystemInfo.operatingSystem,
                BuildName = BuildName,
            };
        }

        private void Start()
        {
            if (ConnectOnStart) Connect();
        }

        /// <summary>Hand the link the journey's centreline. Both pose sources project onto it,
        /// so a journey swap moves the simulated walker and the real one together.</summary>
        public void SetCentreline(IReadOnlyList<Vec3> centreline) => Frame.SetCentreline(centreline);

        public void SetEffects(IControlEffects effects) => Client.SetEffects(effects);

        public void SetStatusSource(IControlStatusSource status) => Client.SetStatusSource(status);

        public void Connect() =>
            Client.Connect(ControlClient.DeviceUrl(Host, Port), _clock.NowMs);

        private void Update()
        {
            Poses.Mode = Mode;
            Client.Pump(_clock.NowMs);
        }

        private void OnApplicationPause(bool paused)
        {
            // A backgrounded phone stops pumping, so every queued command goes stale while it is
            // away. Dropping the socket on the way out makes the resume a plain reconnect, which
            // resyncs against a fresh snapshot rather than replaying a minute of dead schedule.
            if (paused) Client.Disconnect();
            else if (ConnectOnStart) Connect();
        }

        private void OnDestroy()
        {
            Client?.Disconnect();
            _transport?.Dispose();
        }
    }
}
