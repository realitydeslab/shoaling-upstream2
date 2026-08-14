/**
 * The editor's link to whatever is running the piece.
 *
 * The editor is an operator on the control bus. It does two things there:
 *
 *   - streams the simulated walker's pose, so the Unity editor can follow the browser's walk
 *     and play the same beats through PHASE without anyone standing in a creek;
 *   - shows whether a phone is actually connected, which is the first thing you want to know
 *     before wondering why nothing is happening.
 *
 * Pose is streamed on its own message type rather than as a command. A command is discrete,
 * scheduled against a fireAtMs and acknowledged; a pose is continuous state where the latest
 * value wins and history is worthless. Sending twenty commands a second would churn the bounded
 * command log and put a lead time on a signal that wants none.
 *
 * On the creek this channel is silent. VPS2 supplies the pose there, and the app ignores this
 * entirely — the simulation exists so the piece can be built at a desk, not to drive the work.
 */

/** Twenty a second. Fast enough to feel continuous, slow enough not to saturate park wifi. */
const POSE_INTERVAL_MS = 50;

export class Link {
  /** @param onPresence called with {devices, operators} whenever the roster changes */
  constructor({ onPresence } = {}) {
    this.onPresence = onPresence;
    this.socket = null;
    this.connected = false;
    this.devices = 0;
    this.lastSentAt = 0;
    this.pending = null;
    this.retryMs = 1000;
    this.#connect();
  }

  #connect() {
    const scheme = location.protocol === 'https:' ? 'wss' : 'ws';
    let socket;
    try {
      socket = new WebSocket(`${scheme}://${location.host}/ws?role=operator`);
    } catch {
      return this.#retry();
    }
    this.socket = socket;

    socket.addEventListener('open', () => {
      this.connected = true;
      this.retryMs = 1000;      // a good connection resets the backoff
    });

    socket.addEventListener('message', (event) => {
      let msg;
      try { msg = JSON.parse(event.data); } catch { return; }
      if (msg.type === 'presence') {
        this.devices = msg.devices?.length ?? 0;
        this.onPresence?.({ devices: this.devices, operators: msg.operators?.length ?? 0 });
      }
    });

    // Both paths land here; close fires after error, so guard against retrying twice.
    const drop = () => {
      if (!this.socket) return;
      this.socket = null;
      this.connected = false;
      this.devices = 0;
      this.onPresence?.({ devices: 0, operators: 0 });
      this.#retry();
    };
    socket.addEventListener('close', drop);
    socket.addEventListener('error', drop);
  }

  #retry() {
    // The service is usually restarting rather than gone, so back off gently and cap it low
    // enough that reconnecting never feels like something you have to do by hand.
    setTimeout(() => this.#connect(), this.retryMs);
    this.retryMs = Math.min(this.retryMs * 1.8, 10_000);
  }

  /**
   * Publish where the simulated walker is.
   *
   * Rate-limited, and the most recent pose during a quiet interval is kept and sent when the
   * interval expires. Dropping it instead would leave the follower parked a step behind
   * wherever the scrub happened to stop, which reads as a bug rather than as a throttle.
   */
  sendPose({ s, position, headingRad, slug }) {
    this.pending = { type: 'pose', s, position, headingRad, slug };
    if (!this.connected || this.socket?.readyState !== WebSocket.OPEN) return;

    const now = performance.now();
    if (now - this.lastSentAt < POSE_INTERVAL_MS) {
      if (!this.flushTimer) {
        this.flushTimer = setTimeout(() => {
          this.flushTimer = null;
          this.#flush();
        }, POSE_INTERVAL_MS - (now - this.lastSentAt));
      }
      return;
    }
    this.#flush();
  }

  #flush() {
    if (!this.pending || this.socket?.readyState !== WebSocket.OPEN) return;
    this.lastSentAt = performance.now();
    try { this.socket.send(JSON.stringify(this.pending)); } catch { /* the close handler retries */ }
  }

  /** Fire a beat on the phone by hand — the operator's safety net, from the editor. */
  fireBeat(beatId) {
    if (this.socket?.readyState !== WebSocket.OPEN) return false;
    this.socket.send(JSON.stringify({ type: 'command', action: 'fireBeat', beatId }));
    return true;
  }
}
