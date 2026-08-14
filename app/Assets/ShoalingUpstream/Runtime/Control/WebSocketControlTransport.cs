using System;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ShoalingUpstream.Control
{
    /// <summary>
    /// The real socket, and the only file in this folder that knows about threads.
    ///
    /// ClientWebSocket's receive loop is an await chain on the thread pool. Nothing in here
    /// touches a Unity API, allocates a GameObject, or calls back into anything: received frames
    /// go into a queue and the frame loop takes them out. That containment is deliberate — the
    /// standard way this component fails is a callback that reaches an AudioSource from a pool
    /// thread and takes the app down somewhere in a park.
    ///
    /// One send loop, one receive loop, one CancellationTokenSource per connection attempt.
    /// ClientWebSocket permits exactly one outstanding SendAsync and one ReceiveAsync, so
    /// serialising sends through a queue is a requirement, not tidiness.
    /// </summary>
    public sealed class WebSocketControlTransport : IControlTransport, IDisposable
    {
        /// <summary>A frame this large is not our protocol. The cap is on the assembled message,
        /// so a stuck or hostile peer cannot grow the buffer without bound.</summary>
        public int MaxMessageBytes = 256 * 1024;

        private readonly ConcurrentQueue<string> _inbound = new();
        private readonly ConcurrentQueue<string> _outbound = new();
        private readonly SemaphoreSlim _sendSignal = new(0);

        private ClientWebSocket _socket;
        private CancellationTokenSource _cancellation;
        private int _state = (int)TransportState.Closed;
        private string _lastError;

        /// <summary>
        /// Bumped on every Connect and Close.
        ///
        /// A pool thread belonging to the socket we just abandoned can still surface an
        /// exception a moment after we have opened its replacement, and without this it would
        /// mark the NEW socket failed — a reconnect that drops itself, once, unreproducibly, on
        /// a bad afternoon. Loops only report state while they are still the current generation.
        /// </summary>
        private int _generation;

        public TransportState State => (TransportState)Volatile.Read(ref _state);

        public string LastError => Volatile.Read(ref _lastError);

        public void Connect(string url)
        {
            Close();

            int generation = Interlocked.Increment(ref _generation);
            _cancellation = new CancellationTokenSource();
            _socket = new ClientWebSocket();
            // Well under the bus's 12 s presence timeout, so a half-open socket is noticed by
            // the keepalive rather than by the operator watching a device go grey.
            _socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(5);

            Volatile.Write(ref _state, (int)TransportState.Connecting);
            Volatile.Write(ref _lastError, null);

            var socket = _socket;
            var token = _cancellation.Token;
            _ = Task.Run(() => RunAsync(socket, url, token, generation), token);
        }

        private bool IsCurrent(int generation) => Volatile.Read(ref _generation) == generation;

        private void Report(int generation, TransportState state, string error = null)
        {
            if (!IsCurrent(generation)) return;
            if (error != null) Volatile.Write(ref _lastError, error);
            Volatile.Write(ref _state, (int)state);
        }

        public void Send(string json)
        {
            if (json is null) return;
            _outbound.Enqueue(json);
            try { _sendSignal.Release(); } catch (ObjectDisposedException) { /* closing */ }
        }

        public bool TryReceive(out string json) => _inbound.TryDequeue(out json);

        public void Close()
        {
            Interlocked.Increment(ref _generation);
            try { _cancellation?.Cancel(); } catch (ObjectDisposedException) { /* already gone */ }
            // Not disposed: a receive loop may still be sitting on this token, and disposing it
            // under them turns an orderly cancel into an ObjectDisposedException on a pool
            // thread. The generation above has already made anything they report irrelevant.
            _cancellation = null;

            var socket = _socket;
            _socket = null;
            if (socket != null)
            {
                // Abort rather than a courteous close handshake. A close handshake needs the far
                // end to answer, and the case being handled is precisely the one where it will
                // not — the phone is behind a hedge.
                try { socket.Abort(); } catch (Exception) { /* nothing left to do */ }
                socket.Dispose();
            }

            while (_outbound.TryDequeue(out _)) { }
            Volatile.Write(ref _state, (int)TransportState.Closed);
        }

        public void Dispose()
        {
            Close();
            _sendSignal.Dispose();
        }

        private async Task RunAsync(ClientWebSocket socket, string url, CancellationToken token,
                                    int generation)
        {
            try
            {
                await socket.ConnectAsync(new Uri(url), token).ConfigureAwait(false);
                Report(generation, TransportState.Open);

                var pump = SendLoopAsync(socket, token, generation);
                await ReceiveLoopAsync(socket, token, generation).ConfigureAwait(false);
                await pump.ConfigureAwait(false);

                if (State == TransportState.Open) Report(generation, TransportState.Closed);
            }
            catch (OperationCanceledException)
            {
                Report(generation, TransportState.Closed);
            }
            catch (Exception ex)
            {
                // Every socket failure lands here and becomes a state, never an exception on a
                // pool thread with nobody to catch it. The client above reconnects on Failed.
                Report(generation, TransportState.Failed, ex.Message);
            }
        }

        private async Task SendLoopAsync(ClientWebSocket socket, CancellationToken token,
                                         int generation)
        {
            try
            {
                while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
                {
                    await _sendSignal.WaitAsync(token).ConfigureAwait(false);
                    while (_outbound.TryDequeue(out var json))
                    {
                        var bytes = Encoding.UTF8.GetBytes(json);
                        await socket.SendAsync(new ArraySegment<byte>(bytes),
                            WebSocketMessageType.Text, true, token).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) { /* closing */ }
            catch (Exception ex)
            {
                Report(generation, TransportState.Failed, ex.Message);
            }
        }

        private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken token,
                                            int generation)
        {
            var buffer = new byte[8192];
            var assembled = new MemoryStreamLite();

            while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token)
                    .ConfigureAwait(false);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    Report(generation, TransportState.Closed);
                    return;
                }

                assembled.Write(buffer, result.Count);
                if (assembled.Length > MaxMessageBytes)
                {
                    throw new InvalidOperationException("control frame exceeded the size cap");
                }
                if (!result.EndOfMessage) continue;

                _inbound.Enqueue(assembled.ToUtf8String());
                assembled.Reset();
            }
        }

        /// <summary>A growable byte buffer that is reset rather than reallocated. A pose stream
        /// at scrub rate is the busiest thing on this socket, and it runs for an hour at a time
        /// on a phone that also has to render.</summary>
        private sealed class MemoryStreamLite
        {
            private byte[] _bytes = new byte[8192];
            public int Length { get; private set; }

            public void Write(byte[] source, int count)
            {
                if (Length + count > _bytes.Length)
                {
                    int size = _bytes.Length;
                    while (size < Length + count) size *= 2;
                    Array.Resize(ref _bytes, size);
                }
                Buffer.BlockCopy(source, 0, _bytes, Length, count);
                Length += count;
            }

            public string ToUtf8String() => Encoding.UTF8.GetString(_bytes, 0, Length);

            public void Reset() => Length = 0;
        }
    }
}
