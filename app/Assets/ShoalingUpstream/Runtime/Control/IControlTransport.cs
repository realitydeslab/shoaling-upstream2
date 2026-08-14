namespace ShoalingUpstream.Control
{
    public enum TransportState { Closed, Connecting, Open, Failed }

    /// <summary>
    /// The socket, reduced to something with no threads in it.
    ///
    /// Polled rather than event-driven, and that is the whole reason it exists as an interface.
    /// A WebSocket delivers on a thread pool; Unity's API is main-thread-only; and the obvious
    /// shape — a callback that touches an AudioSource — is a crash that happens once a fortnight
    /// in a park. Handing frames across as strings through a queue that the frame loop drains
    /// means the threading question is answered in exactly one file, and everything above this
    /// line is ordinary single-threaded code that an EditMode test can drive.
    /// </summary>
    public interface IControlTransport
    {
        TransportState State { get; }

        /// <summary>Reason the last connection ended, for the operator-facing log. Null while healthy.</summary>
        string LastError { get; }

        void Connect(string url);

        /// <summary>Queue a frame. Never blocks and never throws: a send failure is a socket
        /// state change, not an exception for the caller to handle mid-walk.</summary>
        void Send(string json);

        /// <summary>Take one received frame, if any. Called from the frame loop.</summary>
        bool TryReceive(out string json);

        void Close();
    }
}
