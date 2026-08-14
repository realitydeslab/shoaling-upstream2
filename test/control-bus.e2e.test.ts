/**
 * The control bus, end to end over real WebSockets.
 *
 * This is the safety net in the field: the piece runs automatically, and the operator walking
 * with the visitor uses the controller when a trigger does not fire. Both halves are separate
 * processes on separate devices over a park's wifi, so the only honest test is two real sockets
 * against a real server.
 *
 * The property that matters most is the one in the source comment on issue(): commands are
 * SCHEDULED, not fired. Every device gets the same fireAtMs and acts on it, because the person
 * pressing the button and the person hearing the sound are different people and nothing is
 * fusing across the network.
 */

import test, { before, after } from 'node:test';
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import type { ChildProcess } from 'node:child_process';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { makeDataRoot, makeJourney, until } from './helpers.ts';
import type { CommandMessage, PoseMessage, PresenceMessage } from '../editor/src/types.ts';

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');

/** A frame as it comes off the socket. It claims a `type`; nothing else has been checked yet. */
interface Frame {
  type?: string;
}

/**
 * The greeting every client gets.
 *
 * Not in types.ts — the editor's Link reads the session id and nothing else of it — but this
 * suite needs the whole thing, because the session is what makes an acknowledgement from a
 * previous run of the service meaningless rather than a phantom success.
 */
interface WelcomeMessage extends Frame {
  type: 'welcome';
  clientId: number;
  role: string;
  sessionId: string;
}

/**
 * Authoritative device state, as the bus fans it out to operators.
 *
 * `state` is deliberately left as a bag of keys: what these tests check is WHICH keys survive
 * the merge — the allow-list in control-bus.ts — rather than what any one of them means.
 */
interface StateMessage extends Frame {
  type: 'state';
  state: Record<string, unknown>;
  serverNowMs: number;
}

/** The operator's own copy of a command it issued, so its UI need not wait for the phone. */
interface CommandIssuedMessage extends Frame {
  type: 'commandIssued';
  command: CommandMessage;
}

interface CommandAckMessage extends Frame {
  type: 'commandAck';
  commandId: string;
  applied: boolean;
  note: string | null;
}

interface ErrorMessage extends Frame {
  type: 'error';
  message: string;
}

/**
 * A pose as a device receives it, plus the three fields this suite asserts are ABSENT.
 *
 * A pose is state, not a command: no id, no fire time, no expiry. Declaring them here as
 * never-present is how that question can be asked without pretending a pose has them.
 */
type ReceivedPose = PoseMessage & {
  id?: undefined;
  fireAtMs?: undefined;
  expiresAtMs?: undefined;
};

/**
 * Everything this suite reads off the bus, keyed by the `type` it arrives under.
 *
 * These are claims rather than facts — nothing has checked a frame beyond its type field, and
 * the assertions below are the check. Reading each one as its declared shape is exactly what
 * makes a disagreement between types.ts and the bus fail here instead of in a browser:
 * `PresenceMessage.operators` was declared a list until this suite was pointed at the real bus.
 */
interface BusMessages {
  welcome: WelcomeMessage;
  presence: PresenceMessage;
  command: CommandMessage;
  commandIssued: CommandIssuedMessage;
  commandAck: CommandAckMessage;
  state: StateMessage;
  pose: ReceivedPose;
  error: ErrorMessage;
}

interface Client {
  socket: WebSocket;
  received: Frame[];
  welcome: WelcomeMessage;
  of: <K extends keyof BusMessages>(type: K) => BusMessages[K][];
}

let server: ChildProcess;
let port: string | null;
let cleanup: (() => Promise<void>) | undefined;
const sockets: WebSocket[] = [];

before(async () => {
  const data = await makeDataRoot(makeJourney());
  cleanup = data.cleanup;

  server = spawn(process.execPath, [path.join(ROOT, 'service', 'src', 'server.ts')], {
    env: { ...process.env, PORT: '0', JOURNEY_DIR: data.journeysRoot },
    stdio: ['ignore', 'pipe', 'pipe'],
  });

  let output = '';
  // Both streams are pipes by the stdio above; the general signature of spawn cannot say so.
  server.stdout!.on('data', (c) => { output += c; });
  server.stderr!.on('data', (c) => { output += c; });

  port = await until<string | null>(() => {
    const m = /https?:\/\/[^\s]*?:(\d+)/.exec(output);
    return m?.[1] ?? null;
  }, { timeoutMs: 8000, label: 'the server to report a port' });

  await until(async () => {
    try { return (await fetch(`http://127.0.0.1:${port}/api/health`)).ok; } catch { return false; }
  }, { timeoutMs: 8000, label: 'health' });
});

after(async () => {
  for (const s of sockets) { try { s.close(); } catch { /* already gone */ } }
  server?.kill('SIGTERM');
  await cleanup?.();
});

/**
 * Connect in a role, and record everything it is sent.
 *
 * Uses Node's built-in WebSocket rather than the `ws` package. `ws` is a dependency of the
 * service, not of the repository root, so importing it here would resolve only by accident of
 * directory layout — and the platform now has a client that speaks the same protocol.
 */
async function connect(role: string): Promise<Client> {
  const socket = new WebSocket(`ws://127.0.0.1:${port}/ws?role=${role}`);
  const received: Frame[] = [];
  socket.addEventListener('message', (event) => {
    try { received.push(JSON.parse(String(event.data))); } catch { /* not our protocol */ }
  });
  sockets.push(socket);

  await new Promise((resolve, reject) => {
    socket.addEventListener('open', resolve, { once: true });
    socket.addEventListener('error', reject, { once: true });
  });
  // Every client is greeted, and the greeting carries the session id that scopes command ids.
  // `until` rejects rather than resolving falsy, so a welcome that never came is a timeout here
  // rather than an undefined three lines further on.
  const welcome = (await until(
    () => received.find((m): m is WelcomeMessage => m.type === 'welcome'),
    { label: `a welcome for the ${role}` },
  ))!;
  return {
    socket,
    received,
    welcome,
    of: <K extends keyof BusMessages>(type: K) =>
      received.filter((m) => m.type === type) as BusMessages[K][],
  };
}

test('a client is welcomed with its role and the server session', async () => {
  const device = await connect('device');
  assert.equal(device.welcome.role, 'device');
  assert.ok(device.welcome.clientId);
  assert.ok(device.welcome.sessionId, 'the session scopes command ids across restarts');
});

test('the operator sees the phone appear', async () => {
  const operator = await connect('operator');
  const device = await connect('device');

  const presence = (await until(
    () => operator.of('presence').find((m) => m.devices?.length > 0),
    { label: 'presence showing a device' }))!;
  assert.ok(presence.devices.length >= 1);
  device.socket.close();
});

test('presence reports devices as a list and operators as a count', async () => {
  // These two are shaped differently on purpose and the difference is easy to get wrong in a
  // client: `.length` on a number is undefined, which reads as "no operators" forever and never
  // throws. types.ts declared both as arrays until this was checked against the bus.
  const operator = await connect('operator');
  const device = await connect('device');

  const presence = (await until(
    () => operator.of('presence').find((m) => m.devices?.length > 0),
    { label: 'presence with a device' }))!;

  assert.ok(Array.isArray(presence.devices), 'devices is a list');
  assert.equal(typeof presence.operators, 'number', 'operators is a count, not a list');
  assert.ok(presence.operators >= 1);
  assert.equal(typeof presence.devices[0]!.id, 'number', 'a per-session counter, not a device identity');
  assert.equal(typeof presence.devices[0]!.alive, 'boolean');
  device.socket.close();
});

test('a command reaches the phone, scheduled rather than fired', async () => {
  const device = await connect('device');
  const operator = await connect('operator');

  const issuedAt = Date.now();
  const res = await fetch(`http://127.0.0.1:${port}/api/control/command`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ action: 'fireBeat', beatId: 'one' }),
  });
  assert.equal(res.status, 200);

  const command = (await until(() => device.of('command').find((c) => c.beatId === 'one'),
    { label: 'the phone to receive the command' }))!;

  assert.equal(command.action, 'fireBeat');
  assert.ok(command.fireAtMs > command.issuedAtMs,
    'the phone is told WHEN to act, not to act now — that lead time is what keeps two '
    + 'devices together');
  assert.ok(command.expiresAtMs > command.fireAtMs,
    'a command that arrives after the moment has passed must be dropped, not played late');
  assert.ok(command.fireAtMs >= issuedAt);
  assert.equal(command.sessionId, device.welcome.sessionId);

  // The operator gets its own confirmation, so its UI can respond without waiting for the phone.
  await until(() => operator.of('commandIssued').length > 0,
    { label: 'the operator to be told its command was issued' });
});

test('the operator is told when the phone has acted on a command', async () => {
  const device = await connect('device');
  const operator = await connect('operator');

  await fetch(`http://127.0.0.1:${port}/api/control/command`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ action: 'fireBeat', beatId: 'two' }),
  });
  const command = (await until(() => device.of('command').find((c) => c.beatId === 'two'),
    { label: 'the command' }))!;

  // The sessionId is required, and is not ceremony: after a service restart the old command
  // ids refer to nothing, and an ack carrying one would report a success that never happened.
  device.socket.send(JSON.stringify({
    type: 'ack',
    commandId: command.id,
    applied: true,
    note: 'played',
    sessionId: device.welcome.sessionId,
  }));

  const ack = (await until(() => operator.of('commandAck').find((a) => a.commandId === command.id),
    { label: 'the acknowledgement to reach the operator' }))!;
  assert.equal(ack.applied, true);
});

test('an acknowledgement from a previous service session is dropped', async () => {
  const device = await connect('device');
  const operator = await connect('operator');
  const before = operator.of('commandAck').length;

  device.socket.send(JSON.stringify({
    type: 'ack', commandId: 'stale-session-7', applied: true, sessionId: 'a-previous-session',
  }));

  // Give it room to arrive if it were going to, then assert it did not.
  await new Promise((r) => setTimeout(r, 250));
  assert.equal(operator.of('commandAck').length, before,
    'a stale ack must not be reported as a success');
});

test('commands are not broadcast back to operators as commands', async () => {
  // An operator that received its own command as a device command would double-fire the beat
  // if the controller page were ever open on a phone running the piece.
  const operator = await connect('operator');
  await fetch(`http://127.0.0.1:${port}/api/control/command`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ action: 'fireBeat', beatId: 'one' }),
  });
  await until(() => operator.of('commandIssued').length > 0, { label: 'commandIssued' });
  assert.equal(operator.of('command').length, 0, 'operators get commandIssued, devices get command');
});

test('device state reaches the operator, which is what the controller leads with', async () => {
  const device = await connect('device');
  const operator = await connect('operator');

  // The phone reports with a flat `status` message; the server merges an allow-list of fields
  // into authoritative state and fans that out to operators as `state`.
  device.socket.send(JSON.stringify({
    type: 'status',
    localization: 'Precise',
    trackingConfidence: 0.92,
    s: 9.4,
    shoalCount: 33,
    currentBeat: 'strider',
  }));

  const state = (await until(
    () => operator.of('state').find((m) => m.state?.localization === 'Precise'),
    { label: 'the operator to see localization state' }))!;
  assert.equal(state.state.s, 9.4);
  assert.equal(state.state.currentBeat, 'strider');
  assert.ok(state.serverNowMs, 'a server clock, so the operator can age what it is looking at');
});

test('a status report cannot set fields outside the allow-list', async () => {
  const device = await connect('device');
  const operator = await connect('operator');

  device.socket.send(JSON.stringify({
    type: 'status', s: 3.3, sessionId: 'hijacked', clients: 'nonsense',
  }));

  const state = (await until(() => operator.of('state').find((m) => m.state?.s === 3.3),
    { label: 'the status to be applied' }))!;
  // sessionId is a real field of the authoritative state — the point is that a device cannot
  // overwrite it, not that it is absent.
  assert.notEqual(state.state.sessionId, 'hijacked',
    'a device cannot rewrite the session that scopes command ids');
  assert.equal(state.state.sessionId, device.welcome.sessionId);
  assert.equal(state.state.clients, undefined, 'keys outside the allow-list are not merged');
});

test('the editor streams a pose and the phone receives it', async () => {
  // This is how the piece is developed: the browser scrubs the walk, and a Unity editor follows
  // it and plays the same beats through PHASE. On the creek VPS2 supplies the pose instead and
  // this channel is silent.
  const device = await connect('device');
  const editor = await connect('operator');

  editor.socket.send(JSON.stringify({
    type: 'pose',
    s: 11.0,
    position: { x: 1.2, y: -0.6, z: -3.4 },
    headingRad: 0.87,
    slug: 'test-creek',
  }));

  const pose = (await until(() => device.of('pose')[0],
    { label: 'the phone to receive a pose' }))!;
  assert.equal(pose.s, 11.0);
  assert.deepEqual(pose.position, { x: 1.2, y: -0.6, z: -3.4 });
  assert.equal(pose.headingRad, 0.87);
  assert.ok(pose.sentAtMs, 'carries a server clock, so the follower can estimate its offset');
});

test('a pose is state, not a command — no scheduling, no acknowledgement', async () => {
  // A pose arriving twenty times a second must not churn the bounded command log, carry a lead
  // time, or ask anyone for a receipt.
  const device = await connect('device');
  const editor = await connect('operator');

  editor.socket.send(JSON.stringify({ type: 'pose', s: 4.2 }));
  const pose = (await until(() => device.of('pose').find((p) => p.s === 4.2),
    { label: 'the pose' }))!;

  assert.equal(pose.fireAtMs, undefined, 'a pose is acted on when it arrives, not scheduled');
  assert.equal(pose.expiresAtMs, undefined);
  assert.equal(pose.id, undefined, 'nothing to acknowledge means nothing to identify');
  assert.equal(device.of('command').length, 0, 'it did not travel as a command');
});

test('only operators may stream a pose', async () => {
  // A device echoing a pose back would drive every other device from a follower rather than
  // from the walk.
  const device = await connect('device');
  const other = await connect('device');
  const before = other.of('pose').length;

  device.socket.send(JSON.stringify({ type: 'pose', s: 99 }));
  await new Promise((r) => setTimeout(r, 250));
  assert.equal(other.of('pose').length, before, 'a device cannot stream poses');
});

test('the current state is readable over HTTP as well as over the socket', async () => {
  const res = await fetch(`http://127.0.0.1:${port}/api/control/state`);
  assert.equal(res.status, 200);
  assert.ok(await res.json());
});

test('an unsupported method on a control route does not silently succeed', async () => {
  const res = await fetch(`http://127.0.0.1:${port}/api/control/state`, { method: 'DELETE' });
  assert.ok(res.status >= 400);
});

test('an unknown socket message type is answered with an error, not silence', async () => {
  const device = await connect('device');
  device.socket.send(JSON.stringify({ type: 'not-a-real-type' }));
  const err = (await until(() => device.of('error')[0], { label: 'an error reply' }))!;
  assert.match(err.message, /unknown message type/);
});

test('rubbish on the socket does not take the server down', async () => {
  const device = await connect('device');
  device.socket.send('not json at all');
  device.socket.send(JSON.stringify({ type: 'nonsense-message-type' }));

  // Still serving afterwards, which is the assertion that matters in a park.
  const res = await fetch(`http://127.0.0.1:${port}/api/health`);
  assert.equal(res.status, 200);

  const operator = await connect('operator');
  assert.equal(operator.welcome.role, 'operator');
});
