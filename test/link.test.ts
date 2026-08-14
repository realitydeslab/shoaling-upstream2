/**
 * The editor's operator connection to the control bus.
 *
 * Two things are worth pinning down here and neither is visible from the stage. The first is
 * the pose throttle: it must coalesce rather than drop, or a Unity editor following the walk
 * parks a step behind wherever the scrub stopped and it reads as a bug in the follower. The
 * second is what happens when the service restarts, which it does constantly during authoring
 * — the link has to come back on its own or the editor quietly stops publishing.
 *
 * No socket is opened. The Link takes its socket from a factory, and everything below hands it
 * a fake one whose events are fired by hand, so the ordering is exact rather than raced. Timers
 * are mocked wherever the test turns on one, which is also what keeps a nine-second backoff
 * from becoming a nine-second test.
 */

import test from 'node:test';
import assert from 'node:assert/strict';

import { Link } from '../editor/src/link.ts';
import type { PoseUpdate } from '../editor/src/link.ts';

type Listener = (event: unknown) => void;

/** A WebSocket as far as Link is concerned: a readyState, a send, and events fired on demand. */
class FakeSocket {
  readyState: number = WebSocket.OPEN;
  readonly sent: string[] = [];
  readonly #listeners = new Map<string, Listener[]>();

  send(data: string): void {
    this.sent.push(data);
  }

  addEventListener(type: string, listener: Listener): void {
    const list = this.#listeners.get(type) ?? [];
    list.push(listener);
    this.#listeners.set(type, list);
  }

  emit(type: string, event: unknown = {}): void {
    for (const listener of this.#listeners.get(type) ?? []) listener(event);
  }

  /** What went on the wire, parsed. */
  frames(): Record<string, unknown>[] {
    return this.sent.map((s) => JSON.parse(s) as Record<string, unknown>);
  }
}

const asWebSocket = (socket: FakeSocket) => socket as unknown as WebSocket;

const POSE: PoseUpdate = {
  s: 11.0,
  position: { x: 1.2, y: -0.6, z: -3.4 },
  headingRad: 0.87,
  slug: 'test-creek',
};

/** An open link and the socket behind it, which is the starting point for most of these. */
function openLink(onPresence?: (p: { devices: number; operators: number }) => void) {
  const socket = new FakeSocket();
  const link = onPresence
    ? new Link({ openSocket: () => asWebSocket(socket), onPresence })
    : new Link({ openSocket: () => asWebSocket(socket) });
  socket.emit('open');
  return { link, socket };
}

// ------------------------------------------------------------------ pose

test('the first pose goes straight out', () => {
  const { link, socket } = openLink();
  link.sendPose(POSE);

  assert.equal(socket.frames().length, 1);
  assert.deepEqual(socket.frames()[0], { type: 'pose', ...POSE });
});

test('a pose is state, not a command — nothing to schedule and nothing to acknowledge', () => {
  const { link, socket } = openLink();
  link.sendPose(POSE);

  const frame = socket.frames()[0]!;
  assert.equal(frame.id, undefined);
  assert.equal(frame.fireAtMs, undefined);
  // The clock is the bus's, stamped in streamPose as it fans the pose out, so a follower
  // measures its offset against the server rather than against a browser.
  assert.equal(frame.sentAtMs, undefined);
});

test('the most recent pose during a quiet interval is sent when the interval expires', (t) => {
  // The whole point of the throttle. Dropping the last pose instead of holding it would leave
  // a follower parked a step behind wherever the scrub happened to stop.
  t.mock.timers.enable({ apis: ['setTimeout'] });
  const { link, socket } = openLink();

  link.sendPose({ ...POSE, s: 1 });   // goes immediately
  link.sendPose({ ...POSE, s: 2 });   // inside the interval, superseded
  link.sendPose({ ...POSE, s: 3 });   // inside the interval, and the one that matters

  assert.equal(socket.frames().length, 1, 'the interval is holding the later two back');
  t.mock.timers.tick(50);

  const frames = socket.frames();
  assert.equal(frames.length, 2, 'one immediate send and one flush, not three sends');
  assert.equal(frames[1]!.s, 3, 'the newest pose survives, not the oldest');
});

test('a pose sent while the socket is shut is kept rather than thrown away', () => {
  const socket = new FakeSocket();
  const link = new Link({ openSocket: () => asWebSocket(socket) });
  // No 'open' event, so the link has never been connected.
  link.sendPose(POSE);

  assert.equal(socket.sent.length, 0);
  assert.deepEqual(link.pending, { type: 'pose', ...POSE });
});

// ------------------------------------------------------------------ presence

test('a presence message reports how many phones are on the bus', () => {
  const seen: { devices: number; operators: number }[] = [];
  const { socket } = openLink((p) => seen.push(p));

  socket.emit('message', {
    data: JSON.stringify({ type: 'presence', devices: [{ id: 1, alive: true }], operators: 2 }),
  });

  assert.equal(seen.length, 1);
  assert.equal(seen[0]!.devices, 1);
});

test('the operator count is a count, not a list to be measured', () => {
  // The bus sends `devices` as a list and `operators` as a number. The JavaScript this was
  // ported from read `msg.operators?.length` for both, which is undefined on a number, so it
  // reported zero operators forever and never threw — the kind of failure that only shows up
  // as a panel that is always empty. Typing the message is what surfaced it.
  const seen: { devices: number; operators: number }[] = [];
  const { socket } = openLink((p) => seen.push(p));

  socket.emit('message', {
    data: JSON.stringify({ type: 'presence', devices: [], operators: 3 }),
  });

  assert.equal(seen[0]!.operators, 3);
});

test('a message that is not JSON is ignored rather than thrown', () => {
  const seen: unknown[] = [];
  const { socket } = openLink((p) => seen.push(p));

  socket.emit('message', { data: 'not json at all' });
  socket.emit('message', { data: JSON.stringify({ type: 'state', state: {} }) });

  assert.equal(seen.length, 0);
});

test('losing the socket reports an empty roster instead of leaving the last count on screen', (t) => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  const seen: { devices: number }[] = [];
  const { link, socket } = openLink((p) => seen.push(p));

  socket.emit('message', {
    data: JSON.stringify({ type: 'presence', devices: [{ id: 1, alive: true }], operators: 1 }),
  });
  socket.emit('close');

  assert.equal(link.connected, false);
  assert.equal(link.devices, 0);
  assert.equal(seen.at(-1)!.devices, 0);
});

// ------------------------------------------------------------------ reconnection

test('error followed by close retries once, not twice', (t) => {
  // Both events land on the same handler and close always follows error, so without the guard
  // every drop would schedule two reconnects and the backoff would double-step.
  t.mock.timers.enable({ apis: ['setTimeout'] });
  const { link, socket } = openLink();

  socket.emit('error');
  socket.emit('close');

  assert.equal(link.retryMs, 1800, 'one step of backoff, not two');
});

test('a socket that will not open backs off before trying again', (t) => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  const link = new Link({ openSocket: () => { throw new Error('no route to service'); } });

  assert.equal(link.socket, null);
  assert.equal(link.connected, false);
  assert.equal(link.retryMs, 1800);
});

test('a reconnect opens a new socket and a good connection resets the backoff', (t) => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  const sockets: FakeSocket[] = [];
  const link = new Link({
    openSocket: () => {
      const socket = new FakeSocket();
      sockets.push(socket);
      return asWebSocket(socket);
    },
  });
  sockets[0]!.emit('open');
  sockets[0]!.emit('close');
  assert.equal(sockets.length, 1, 'the retry is on a timer, not immediate');

  t.mock.timers.tick(1000);
  assert.equal(sockets.length, 2, 'the link reopened on its own');
  assert.equal(link.retryMs, 1800);

  sockets[1]!.emit('open');
  assert.equal(link.connected, true);
  assert.equal(link.retryMs, 1000, 'a working connection forgets the backoff');
});

test('the backoff is capped at ten seconds', (t) => {
  // The service is usually restarting rather than gone, so reconnecting must never become
  // something the operator has to do by hand.
  t.mock.timers.enable({ apis: ['setTimeout'] });
  const { link, socket } = openLink();
  link.retryMs = 9000;
  socket.emit('close');

  assert.equal(link.retryMs, 10_000);
});

// ------------------------------------------------------------------ the safety net

test('fireBeat puts a command on the wire and says that it went', () => {
  const { link, socket } = openLink();

  assert.equal(link.fireBeat('one'), true);
  assert.deepEqual(socket.frames()[0], { type: 'command', action: 'fireBeat', beatId: 'one' });
});

test('fireBeat reports failure rather than pretending, when there is no socket', () => {
  // The operator is standing in a park watching nothing happen; a silent false is the signal.
  const { link, socket } = openLink();
  socket.readyState = WebSocket.CLOSED;

  assert.equal(link.fireBeat('one'), false);
  assert.equal(socket.sent.length, 0);
});
