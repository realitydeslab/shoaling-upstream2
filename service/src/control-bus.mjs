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

const DEFAULT_LEAD_MS = 400;
const COMMAND_TTL_MS = 10_000;
const HEARTBEAT_TIMEOUT_MS = 12_000;

export class ControlBus {
  constructor({ leadMs = DEFAULT_LEAD_MS } = {}) {
    this.leadMs = leadMs;
    this.clients = new Map(); // id -> {socket, role, lastSeen, info}
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

  add(socket, role) {
    const id = this.nextId++;
    this.clients.set(id, { socket, role, lastSeen: Date.now(), info: {} });
    this.send(socket, { type: 'welcome', clientId: id, role, sessionId: this.sessionId });
    this.send(socket, { type: 'state', state: this.state, serverNowMs: Date.now() });
    this.broadcastPresence();
    return id;
  }

  remove(id) {
    this.clients.delete(id);
    this.broadcastPresence();
  }

  send(socket, payload) {
    if (socket.readyState === 1) {
      socket.send(JSON.stringify(payload));
    }
  }

  broadcast(payload, { role } = {}) {
    for (const [, client] of this.clients) {
      if (role && client.role !== role) continue;
      this.send(client.socket, payload);
    }
  }

  broadcastPresence() {
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
  applyStatus(id, patch) {
    const client = this.clients.get(id);
    if (client) {
      client.lastSeen = Date.now();
    }
    const allowed = [
      'site', 'revision', 'localization', 'trackingConfidence',
      's', 'lateralM', 'currentBeat', 'highWaterMark', 'completed', 'shoalCount',
    ];
    let changed = false;
    for (const key of allowed) {
      if (Object.hasOwn(patch, key) && patch[key] !== undefined) {
        this.state[key] = patch[key];
        changed = true;
      }
    }
    if (changed) {
      this.state.updatedAt = Date.now();
      this.broadcast({ type: 'state', state: this.state, serverNowMs: Date.now() });
    }
  }

  /**
   * Queue an operator command for the phone.
   * Returns the scheduled command so the operator UI can confirm locally at once — perceived
   * responsiveness has to be decoupled from delivery, because the person pressing and the
   * person hearing are different people and nothing is fusing across the network anyway.
   */
  issue(command) {
    const now = Date.now();
    const scheduled = {
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

  acknowledge(id, ack) {
    const client = this.clients.get(id);
    if (client) client.lastSeen = Date.now();
    // Acknowledgements from a previous server session are meaningless — the command IDs they
    // reference no longer exist — so drop them rather than reporting a phantom success.
    if (ack.sessionId !== this.sessionId) return false;
    this.broadcast({ type: 'commandAck', commandId: ack.commandId, applied: !!ack.applied, note: ack.note ?? null });
    return true;
  }

  touch(id) {
    const client = this.clients.get(id);
    if (client) {
      client.lastSeen = Date.now();
      if (client.role === 'device') this.broadcastPresence();
    }
  }

  describeDevice(id, info) {
    const client = this.clients.get(id);
    if (client) {
      client.info = { ...client.info, ...info };
      this.broadcastPresence();
    }
  }
}
