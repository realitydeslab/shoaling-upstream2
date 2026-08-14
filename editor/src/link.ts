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

import type { PoseMessage, PresenceMessage } from './types.js';

/** Twenty a second. Fast enough to feel continuous, slow enough not to saturate park wifi. */
const POSE_INTERVAL_MS = 50;

/**
 * What the editor puts on the wire.
 *
 * `sentAtMs` is missing on purpose: the bus stamps its own clock as it fans the pose out
 * (`streamPose` in service/src/control-bus.mjs), so the follower measures its offset against
 * the server rather than against a browser's idea of the time.
 */
export type OutboundPose = Omit<PoseMessage, 'sentAtMs'>;

/** Where the walker is. Everything the caller supplies; `type` is added here. */
export type PoseUpdate = Omit<OutboundPose, 'type'>;

/** How many of each are on the bus right now. */
export interface Presence {
  devices: number;
  operators: number;
}

export interface LinkOptions {
  /** Called with {devices, operators} whenever the roster changes. */
  onPresence?: (presence: Presence) => void;
  /**
   * How to open the socket. A seam added by the TypeScript port so `test/link.test.ts` can hand
   * in a fake one; the editor never passes it and gets the operator URL below.
   */
  openSocket?: () => WebSocket;
}

function operatorSocket(): WebSocket {
  const scheme = location.protocol === 'https:' ? 'wss' : 'ws';
  return new WebSocket(`${scheme}://${location.host}/ws?role=operator`);
}

export class Link {
  onPresence: ((presence: Presence) => void) | undefined;
  socket: WebSocket | null = null;
  connected = false;
  devices = 0;
  lastSentAt = 0;
  pending: OutboundPose | null = null;
  retryMs = 1000;
  flushTimer: ReturnType<typeof setTimeout> | null = null;

  readonly #openSocket: () => WebSocket;

  constructor({ onPresence, openSocket }: LinkOptions = {}) {
    this.onPresence = onPresence;
    this.#openSocket = openSocket ?? operatorSocket;
    this.#connect();
  }

  #connect(): void {
    let socket: WebSocket;
    try {
      socket = this.#openSocket();
    } catch {
      return this.#retry();
    }
    this.socket = socket;

    socket.addEventListener('open', () => {
      this.connected = true;
      this.retryMs = 1000;      // a good connection resets the backoff
    });

    socket.addEventListener('message', (event) => {
      // Off the wire, so its shape is a claim rather than a fact.
      let msg: Partial<PresenceMessage>;
      try { msg = JSON.parse(event.data); } catch { return; }
      if (msg.type === 'presence') {
        this.devices = msg.devices?.length ?? 0;
        // The bus broadcasts `operators` as a COUNT and `devices` as a list; the asymmetry is
        // deliberate and is documented on PresenceMessage. The JavaScript read
        // `msg.operators?.length`, which is undefined on a number, so this reported zero
        // operators forever and never threw. Typing the message correctly is what surfaced it.
        this.onPresence?.({ devices: this.devices, operators: msg.operators ?? 0 });
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

  #retry(): void {
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
  sendPose({ s, position, headingRad, slug }: PoseUpdate): void {
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

  #flush(): void {
    if (!this.pending || this.socket?.readyState !== WebSocket.OPEN) return;
    this.lastSentAt = performance.now();
    try { this.socket.send(JSON.stringify(this.pending)); } catch { /* the close handler retries */ }
  }

  /** Fire a beat on the phone by hand — the operator's safety net, from the editor. */
  fireBeat(beatId: string): boolean {
    if (this.socket?.readyState !== WebSocket.OPEN) return false;
    this.socket.send(JSON.stringify({ type: 'command', action: 'fireBeat', beatId }));
    return true;
  }
}
