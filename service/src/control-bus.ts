/**
 * The live control bus between the operator's browser and the phone.
 *
 * Two decisions here come straight from measured findings and are worth not undoing:
 *
 * 1. The server holds authoritative state and pushes a full snapshot on every connect.
 *    Not a delta stream. That makes network churn, a browser refresh, a Unity domain reload
 *    and a screen-lock resume all the *same* code path, so a reconnecting client resyncs
 *    instead of drifting.
 *
 * 2. Commands are scheduled, not triggered. A command carries `fireAtMs`, a few hundred
 *    milliseconds ahead, rather than meaning "now". A steady 10 ms of latency is
 *    indistinguishable from zero, but 10 ms +/- 3 ms of jitter measurably degrades the
 *    experience — so we convert variable latency into fixed latency. It also makes packet
 *    loss harmless: a command that arrives after its own timestamp is dropped rather than
 *    fired late, which is the failure mode that would actually ruin a walk.
 */

import type { WebSocket } from 'ws';
import type { CommandMessage, PoseMessage, Vec3 } from '../../editor/src/types.ts';

const DEFAULT_LEAD_MS = 400;
const COMMAND_TTL_MS = 10_000;
const HEARTBEAT_TIMEOUT_MS = 12_000;

export type ClientRole = 'device' | 'operator';

/** What a `hello` told us about a phone. Null rather than absent: the phone may not know. */
export interface DeviceInfo {
  device?: string | null;
  os?: string | null;
  build?: string | null;
}

interface Client {
  socket: WebSocket;
  role: ClientRole;
  lastSeen: number;
  info: DeviceInfo;
}

/**
 * The authoritative view of what the phone is doing.
 *
 * Mirrored by `ServerState` in editor/src/control.ts, which is its only reader in the browser,
 * and by the Unity side in app/…/Control/ControlProtocol.cs.
 */
export interface BusState {
  sessionId: string;
  site: string | null;
  revision: number | null;
  /** unavailable | coarse | precise */
  localization: string;
  trackingConfidence: number | null;
  /** Metres along the creek centreline. */
  s: number | null;
  lateralM: number | null;
  currentBeat: string | null;
  highWaterMark: number;
  completed: string[];
  shoalCount: number | null;
  updatedAt: number;
}

/** A pose as it arrives on the wire from the editor's walk simulation. */
export interface PoseInput {
  s?: number | null;
  position?: Vec3 | null;
  headingRad?: number | null;
  slug?: string | null;
}

/** A command as an operator sends it. Only `action` is required, and even that is untrusted. */
export interface CommandInput {
  action: string;
  beatId?: string | null;
  value?: unknown;
}

export interface AckInput {
  sessionId?: string;
  commandId?: string;
  applied?: unknown;
  note?: string | null;
}

export interface ControlBusOptions {
  leadMs?: number;
}

export class ControlBus {
  leadMs: number;
  clients: Map<number, Client>;
  nextId: number;
  sessionId: string;
  state: BusState;
  commandLog: CommandMessage[];
  lastPose?: PoseMessage;

  constructor({ leadMs = DEFAULT_LEAD_MS }: ControlBusOptions = {}) {
    this.leadMs = leadMs;
    this.clients = new Map();
    this.nextId = 1;
    this.sessionId = `s${Date.now().toString(36)}`;

    /** Authoritative view of what the phone is doing. The operator UI reads this, and it is
     *  the whole reason the controller can answer "why is it quiet" rather than just offer
     *  buttons. */
    this.state = {
      sessionId: this.sessionId,
      site: null,
      revision: null,
      localization: 'unavailable', // unavailable | coarse | precise
      trackingConfidence: 0,
      s: null,                     // metres along the creek centreline
      lateralM: null,
      currentBeat: null,
      highWaterMark: -1,
      completed: [],
      shoalCount: null,
      updatedAt: Date.now(),
    };

    this.commandLog = [];
  }

  add(socket: WebSocket, role: ClientRole): number {
    const id = this.nextId++;
    this.clients.set(id, { socket, role, lastSeen: Date.now(), info: {} });
    this.send(socket, { type: 'welcome', clientId: id, role, sessionId: this.sessionId });
    this.send(socket, { type: 'state', state: this.state, serverNowMs: Date.now() });
    this.broadcastPresence();
    return id;
  }

  remove(id: number): void {
    this.clients.delete(id);
    this.broadcastPresence();
  }

  send(socket: WebSocket, payload: unknown): void {
    if (socket.readyState === 1) {
      socket.send(JSON.stringify(payload));
    }
  }

  broadcast(payload: unknown, { role }: { role?: ClientRole } = {}): void {
    for (const [, client] of this.clients) {
      if (role && client.role !== role) continue;
      this.send(client.socket, payload);
    }
  }

  broadcastPresence(): void {
    const now = Date.now();
    const devices = [];
    let operators = 0;
    for (const [id, c] of this.clients) {
      if (c.role === 'device') {
        devices.push({
          id,
          alive: now - c.lastSeen < HEARTBEAT_TIMEOUT_MS,
          lastSeenMsAgo: now - c.lastSeen,
          ...c.info,
        });
      } else {
        operators += 1;
      }
    }
    this.broadcast({ type: 'presence', devices, operators });
  }

  /** Merge a device's status report into authoritative state and fan it out to operators. */
  applyStatus(id: number, patch: Record<string, unknown>): void {
    const client = this.clients.get(id);
    if (client) {
      client.lastSeen = Date.now();
    }
    const allowed: readonly (keyof BusState)[] = [
      'site', 'revision', 'localization', 'trackingConfidence',
      's', 'lateralM', 'currentBeat', 'highWaterMark', 'completed', 'shoalCount',
    ];
    let changed = false;
    for (const key of allowed) {
      if (Object.hasOwn(patch, key) && patch[key] !== undefined) {
        // The allowlist is the check. What a device puts under an allowed key is its own
        // report of itself and is written through as-is, exactly as the JavaScript did.
        (this.state as unknown as Record<string, unknown>)[key] = patch[key];
        changed = true;
      }
    }
    if (changed) {
      this.state.updatedAt = Date.now();
      this.broadcast({ type: 'state', state: this.state, serverNowMs: Date.now() });
    }
  }

  /**
   * Stream a simulated pose from the editor to every device.
   *
   * Deliberately NOT a command. A command is a discrete instruction that is scheduled against a
   * fireAtMs and acknowledged, and the command log is bounded at 200 — a pose arriving twenty
   * times a second would churn that log every ten seconds, put a lead time on a continuous
   * signal, and ask every device to acknowledge a stream nobody needs receipts for.
   *
   * A pose is state: best-effort, latest wins, no history. It exists so the Unity editor can
   * follow the browser's walk simulation and play the same beats without anyone standing in a
   * creek. On device this channel is silent — VPS2 supplies the pose, and the app ignores this.
   */
  streamPose(pose: PoseInput): PoseMessage {
    const message: PoseMessage = {
      type: 'pose',
      s: pose.s ?? null,
      position: pose.position ?? null,
      headingRad: pose.headingRad ?? null,
      slug: pose.slug ?? null,
      // The receiver estimates its clock offset against this the same way it does for `pong`.
      sentAtMs: Date.now(),
    };
    this.lastPose = message;
    this.broadcast(message, { role: 'device' });
    return message;
  }

  /**
   * Queue an operator command for the phone.
   * Returns the scheduled command so the operator UI can confirm locally at once — perceived
   * responsiveness has to be decoupled from delivery, because the person pressing and the
   * person hearing are different people and nothing is fusing across the network anyway.
   */
  issue(command: CommandInput): CommandMessage {
    const now = Date.now();
    const scheduled: CommandMessage = {
      type: 'command',
      id: `${this.sessionId}-${this.commandLog.length + 1}`,
      action: command.action,
      beatId: command.beatId ?? null,
      value: command.value ?? null,
      issuedAtMs: now,
      fireAtMs: now + this.leadMs,
      expiresAtMs: now + COMMAND_TTL_MS,
      sessionId: this.sessionId,
    };
    this.commandLog.push(scheduled);
    if (this.commandLog.length > 200) this.commandLog.shift();

    this.broadcast(scheduled, { role: 'device' });
    this.broadcast({ type: 'commandIssued', command: scheduled });
    return scheduled;
  }

  acknowledge(id: number, ack: AckInput): boolean {
    const client = this.clients.get(id);
    if (client) client.lastSeen = Date.now();
    // Acknowledgements from a previous server session are meaningless — the command IDs they
    // reference no longer exist — so drop them rather than reporting a phantom success.
    if (ack.sessionId !== this.sessionId) return false;
    this.broadcast({ type: 'commandAck', commandId: ack.commandId, applied: !!ack.applied, note: ack.note ?? null });
    return true;
  }

  touch(id: number): void {
    const client = this.clients.get(id);
    if (client) {
      client.lastSeen = Date.now();
      if (client.role === 'device') this.broadcastPresence();
    }
  }

  describeDevice(id: number, info: DeviceInfo): void {
    const client = this.clients.get(id);
    if (client) {
      client.info = { ...client.info, ...info };
      this.broadcastPresence();
    }
  }
}
