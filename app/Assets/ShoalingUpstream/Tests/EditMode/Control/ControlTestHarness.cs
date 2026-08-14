using System.Collections.Generic;
using System.Globalization;
using ShoalingUpstream.Control;

namespace ShoalingUpstream.Tests.Control
{
    /// <summary>
    /// A socket with no socket in it.
    ///
    /// The transport seam exists precisely so these tests can exist: no real WebSocket in
    /// EditMode, no ports, no waiting. Frames go in by hand and come out into a list, and every
    /// timing question is answered by a number the test chooses.
    /// </summary>
    public sealed class FakeControlTransport : IControlTransport
    {
        public TransportState State { get; set; } = TransportState.Closed;
        public string LastError { get; set; }

        public readonly List<string> Sent = new();
        public readonly Queue<string> Inbound = new();
        public readonly List<string> Urls = new();
        public int ConnectCount;

        /// <summary>Nothing is listening. A laptop that is switched off, which is most of the
        /// time the app is running.</summary>
        public bool NothingListening;

        public void Connect(string url)
        {
            ConnectCount++;
            Urls.Add(url);
            if (NothingListening)
            {
                LastError = "no route to host";
                State = TransportState.Failed;
                return;
            }
            State = TransportState.Open;
        }

        public void Send(string json) => Sent.Add(json);

        public bool TryReceive(out string json)
        {
            if (Inbound.Count > 0) { json = Inbound.Dequeue(); return true; }
            json = null;
            return false;
        }

        public void Close() => State = TransportState.Closed;

        public void Deliver(string json) => Inbound.Enqueue(json);

        /// <summary>The phone walked behind a hedge.</summary>
        public void Drop(string reason = "connection reset")
        {
            LastError = reason;
            State = TransportState.Failed;
        }

        public List<string> SentOfType(string type)
        {
            var found = new List<string>();
            foreach (var frame in Sent)
            {
                if (JsonValue.TryParse(frame, out var v) && v["type"].AsString() == type) found.Add(frame);
            }
            return found;
        }

        public JsonValue LastSentOfType(string type)
        {
            var frames = SentOfType(type);
            return frames.Count == 0 || !JsonValue.TryParse(frames[^1], out var v) ? null : v;
        }
    }

    /// <summary>Builds the frames the service would send, in the exact shape
    /// test/control-bus.e2e.test.mjs pins.</summary>
    public static class Frames
    {
        public const string Session = "s-test";

        public static string Welcome(string sessionId = Session, int clientId = 1) =>
            $"{{\"type\":\"welcome\",\"clientId\":{clientId},\"role\":\"device\",\"sessionId\":\"{sessionId}\"}}";

        public static string Pong(double serverNowMs) =>
            $"{{\"type\":\"pong\",\"serverNowMs\":{N(serverNowMs)}}}";

        public static string Command(string action, double issuedAtMs, double leadMs = 400,
                                     double ttlMs = 10_000, string beatId = null,
                                     string valueJson = "null", string sessionId = Session,
                                     string id = "s-test-1")
        {
            string beat = beatId is null ? "null" : $"\"{beatId}\"";
            return "{\"type\":\"command\""
                 + $",\"id\":\"{id}\",\"action\":\"{action}\",\"beatId\":{beat},\"value\":{valueJson}"
                 + $",\"issuedAtMs\":{N(issuedAtMs)},\"fireAtMs\":{N(issuedAtMs + leadMs)}"
                 + $",\"expiresAtMs\":{N(issuedAtMs + ttlMs)},\"sessionId\":\"{sessionId}\"}}";
        }

        public static string Pose(double s, double issuedAtMs, double leadMs = 400,
                                  string id = "s-test-1", double headingRad = 0) =>
            Command(ControlActions.SimulatePose, issuedAtMs, leadMs,
                    valueJson: $"{{\"s\":{N(s)},\"headingRad\":{N(headingRad)}}}", id: id);

        private static string N(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    }
}
