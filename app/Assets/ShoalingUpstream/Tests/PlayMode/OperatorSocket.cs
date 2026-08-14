using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ShoalingUpstream.Control;

namespace ShoalingUpstream.Tests.Integration
{
    /// <summary>
    /// The other half of every test in this folder: a real socket joined to the real bus as an
    /// operator, standing in for the browser editor and the controller page.
    ///
    /// It is deliberately dumb — it sends exactly the bytes those pages send and records exactly
    /// the frames the bus sends back. Nothing here shares code with the client under test, so a
    /// test that passes cannot be passing because both sides agree on the same mistake.
    ///
    /// The frames the editor sends are copied from `editor/js/link.js`; the frames the controller
    /// sends are copied from `editor/js/control.js`; both are pinned in
    /// `test/control-bus.e2e.test.mjs`.
    /// </summary>
    public sealed class OperatorSocket : IDisposable
    {
        private readonly ClientWebSocket _socket = new();
        private readonly CancellationTokenSource _cancellation = new();
        private readonly ConcurrentQueue<string> _received = new();
        private readonly List<string> _all = new();

        public static OperatorSocket Connect(string url)
        {
            var link = new OperatorSocket();
            link._socket.ConnectAsync(new Uri(url), link._cancellation.Token).GetAwaiter().GetResult();
            _ = Task.Run(link.ReceiveLoopAsync);
            return link;
        }

        public void Send(string json) =>
            _socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(json)),
                              WebSocketMessageType.Text, true, _cancellation.Token)
                   .GetAwaiter().GetResult();

        /// <summary>Every frame received so far, parsed. Drains the queue on each call, so a test
        /// can hold on to the list and keep asking.</summary>
        public IReadOnlyList<JsonValue> Frames
        {
            get
            {
                while (_received.TryDequeue(out string raw)) _all.Add(raw);
                var parsed = new List<JsonValue>(_all.Count);
                foreach (string raw in _all)
                {
                    if (JsonValue.TryParse(raw, out var value)) parsed.Add(value);
                }
                return parsed;
            }
        }

        /// <summary>The most recent frame of a type, or null if none has arrived.</summary>
        public JsonValue Latest(string type)
        {
            JsonValue found = null;
            foreach (var frame in Frames)
            {
                if (frame["type"].AsString() == type) found = frame;
            }
            return found;
        }

        public List<JsonValue> AllOf(string type)
        {
            var found = new List<JsonValue>();
            foreach (var frame in Frames)
            {
                if (frame["type"].AsString() == type) found.Add(frame);
            }
            return found;
        }

        public void Forget()
        {
            while (_received.TryDequeue(out _)) { }
            _all.Clear();
        }

        private async Task ReceiveLoopAsync()
        {
            var buffer = new byte[16 * 1024];
            try
            {
                while (_socket.State == WebSocketState.Open)
                {
                    var result = await _socket.ReceiveAsync(
                        new ArraySegment<byte>(buffer), _cancellation.Token);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    _received.Enqueue(Encoding.UTF8.GetString(buffer, 0, result.Count));
                }
            }
            catch (Exception) { /* the test is over */ }
        }

        public void Dispose()
        {
            try { _cancellation.Cancel(); } catch (ObjectDisposedException) { }
            try { _socket.Abort(); } catch (Exception) { }
            _socket.Dispose();
            _cancellation.Dispose();
        }
    }
}
