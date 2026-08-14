/**
 * The service and browser ends of the Unity integration, checked against what Unity really does.
 *
 * The Unity half is proven in app/Assets/ShoalingUpstream/Tests/PlayMode/ — real play mode, real
 * socket, a real `node server.ts`. Two things that fixture cannot reach are covered here: the
 * shape of the pose channel as the bus actually broadcasts it, and the two pages — the editor
 * whose walk simulation is meant to drive the app, and the controller meant to display what the
 * app reports.
 *
 * The join between the halves is deliberate rather than agreed:
 *
 * * The pose tests record what a DEVICE receives. Those are the bytes the Unity client is handed,
 *   so what this file observes and what the PlayMode fixture feeds its client are the same
 *   message by construction.
 *
 * * The controller test replays `logs/unity-status-frame.json`, written by the PlayMode run from
 *   real `ControlProtocol.Status` output. The page is checked against bytes a Unity build emitted,
 *   not against a fixture somebody typed. Without that file it skips and says how to produce it,
 *   rather than inventing a frame and proving nothing.
 *
 * The browser half is opt-in — `SHOALING_BROWSER=1 node --test test/unity-integration.e2e.test.mjs`
 * — for the same reason browser.e2e.test.mjs lives outside `npm test`: it launches Chromium on
 * the artist's laptop and takes tens of seconds. The socket tests above it are fast and always run.
 */

import test, { describe, before, after } from 'node:test';
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { readFile, writeFile, mkdir } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

import { makeDataRoot, makeJourney, SLUG, until } from './helpers.ts';

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const STATUS_RECORDING = path.join(ROOT, 'logs', 'unity-status-frame.json');
const POSE_RECORDING = path.join(ROOT, 'logs', 'editor-pose-frames.jsonl');

/**
 * Chromium, resolved before anything is registered.
 *
 * Registration has to be synchronous from here on: a top-level `await` between two `describe`
 * calls lets the runner drain the first suite and finish the file before the second is declared,
 * and its tests are then cancelled by a parent that has already ended.
 */
const HOW_TO_ENABLE = 'the browser half is opt-in: SHOALING_BROWSER=1 node --test '
  + 'test/unity-integration.e2e.test.mjs';

let browser = null;
let unavailable = process.env.SHOALING_BROWSER ? null : HOW_TO_ENABLE;
if (!unavailable) {
  try {
    const { chromium } = await import('playwright');
    browser = await chromium.launch({
      // spark.js needs WebGL2 and there is no CPU path through the editor; these let Chromium
      // fall back to software rendering where there is no usable GPU.
      args: ['--enable-unsafe-swiftshader', '--ignore-gpu-blocklist', '--disable-dev-shm-usage'],
    });
  } catch (err) {
    unavailable = 'playwright is not available — run `npm i -D playwright && npx playwright '
      + `install chromium\` (see test/README.md) — ${err.message.split('\n')[0]}`;
  }
}

/** A service of its own, so nothing here can touch the artist's journeys or their bus. */
async function startService() {
  const data = await makeDataRoot(makeJourney());
  const server = spawn(process.execPath, [path.join(ROOT, 'service', 'src', 'server.ts')], {
    env: { ...process.env, PORT: '0', DATA_DIR: data.base, JOURNEY_DIR: data.journeysRoot },
    stdio: ['ignore', 'pipe', 'pipe'],
  });

  let output = '';
  server.stdout.on('data', (c) => { output += c; });
  server.stderr.on('data', (c) => { output += c; });

  const port = await until(() => {
    const m = /https?:\/\/[^\s]*?:(\d+)/.exec(output);
    return m ? m[1] : null;
  }, { timeoutMs: 8000, label: `the server to report a port (output so far: ${output})` });

  const base = `http://127.0.0.1:${port}`;
  await until(async () => {
    try { return (await fetch(`${base}/api/health`)).ok; } catch { return false; }
  }, { timeoutMs: 8000, label: 'the server to answer /api/health' });

  return {
    base,
    stop: async () => { server.kill('SIGTERM'); await data.cleanup(); },
  };
}

/** Node's built-in WebSocket, for the reason given in control-bus.e2e.test.mjs: `ws` belongs to
 *  the service, not to this package. */
async function join(base, role) {
  const socket = new WebSocket(`${base.replace('http', 'ws')}/ws?role=${role}`);
  const received = [];
  socket.addEventListener('message', (event) => {
    try { received.push(JSON.parse(String(event.data))); } catch { /* not our protocol */ }
  });
  await new Promise((resolve, reject) => {
    socket.addEventListener('open', resolve, { once: true });
    socket.addEventListener('error', reject, { once: true });
  });
  await until(() => received.find((m) => m.type === 'welcome'), { label: `a welcome for the ${role}` });
  return {
    socket,
    received,
    of: (type) => received.filter((m) => m.type === type),
    send: (message) => socket.send(JSON.stringify(message)),
    close: () => socket.close(),
  };
}

// ---------------------------------------------------------------- the pose channel

describe('the pose channel, as the phone receives it', () => {
  let service;
  const sockets = [];

  before(async () => { service = await startService(); });
  after(async () => {
    for (const s of sockets) { try { s.close(); } catch { /* already gone */ } }
    await service?.stop();
  });

  test('a streamed pose reaches the phone as its own message type, not as a command', async () => {
    const device = await join(service.base, 'device');
    const operator = await join(service.base, 'operator');
    sockets.push(device, operator);

    operator.send({
      type: 'pose',
      s: 4.2,
      position: { x: 4.2, y: 1.4, z: 0 },
      headingRad: 1.2,
      slug: SLUG,
    });

    const pose = await until(() => device.of('pose').find((p) => p.s === 4.2),
                             { label: 'the phone to receive a pose' });

    // The shape matters as much as the arrival: this is the frame the app has to read.
    assert.equal(pose.type, 'pose');
    assert.equal(typeof pose.sentAtMs, 'number', 'the receiver estimates its clock offset from this');
    assert.deepEqual(pose.position, { x: 4.2, y: 1.4, z: 0 },
                     'the point travels nested under `position`, not as flat x/y/z');

    // The negative half, which is why this suite exists. For a day the Unity client read only
    // `{type:'command', action:'simulatePose'}`, and nothing on this bus has ever produced one —
    // so a simulated walk in the browser moved nothing in Unity, silently, with 201 unit tests
    // passing on both sides. `ControlProtocol` now has a `case "pose"`. This assertion stays
    // because it is the fact that made the mismatch invisible.
    assert.deepEqual(device.of('command').filter((c) => c.action === 'simulatePose'), [],
                     'the bus streams poses and never wraps one in a command');
  });

  test('the envelope the Unity client does understand still works through the bus', async () => {
    const device = await join(service.base, 'device');
    const operator = await join(service.base, 'operator');
    sockets.push(device, operator);

    // The older carrier, kept alive on the Unity side because a phone in the field may still be
    // sent one and removing it would be a second break. Nothing produces these today; this pins
    // that the bus would still deliver one in the shape ControlProtocol.TryReadPose parses.
    operator.send({
      type: 'command',
      action: 'simulatePose',
      value: { s: 4.2, x: 4.2, y: 1.4, z: 0, headingRad: 1.2 },
    });

    const command = await until(() => device.of('command').find((c) => c.action === 'simulatePose'),
                                { label: 'the phone to receive a simulatePose command' });

    assert.equal(command.value.s, 4.2);
    assert.equal(typeof command.fireAtMs, 'number');
    assert.equal(typeof command.expiresAtMs, 'number');
    assert.equal(typeof command.sessionId, 'string');
  });
});

// ---------------------------------------------------------------- the two pages

// `?? false`, not the bare value: node:test treats a null `skip` as a skip, and the suite then
// ends before its own tests, which report as cancelled by their parent rather than as skipped.
describe('the editor and the controller, in a real browser', { skip: unavailable ?? false }, () => {
  let service, context;
  const sockets = [];

  before(async () => {
    service = await startService();
    // A fresh context: both pages remember things in localStorage, and a reused profile would
    // make the suite depend on what it did last time.
    context = await browser.newContext();
  });

  after(async () => {
    for (const s of sockets) { try { s.close(); } catch { /* already gone */ } }
    await context?.close();
    await browser?.close();
    await service?.stop();
  });

  test('scrubbing the editor puts a pose on the wire, addressed to the phone', async () => {
    const device = await join(service.base, 'device');
    sockets.push(device);

    const page = await context.newPage();
    await page.goto(`${service.base}/`, { waitUntil: 'domcontentloaded' });
    await page.waitForFunction(() => window.__editor?.state?.journey, null, { timeout: 30_000 });

    // Scrub, repeatedly. This is the call the scrubber's drag handler makes, and a drag is many
    // of them — which also rides out the moment before the editor's own socket has opened.
    const arrived = until(() => device.of('pose').find((p) => Math.abs((p.s ?? -1) - 4.2) < 0.01),
                          { timeoutMs: 20_000, label: 'a pose for s = 4.2 m' });
    const scrubbing = setInterval(
      () => page.evaluate(() => window.__editor.setWalker(4.2)).catch(() => {}), 200);
    const pose = await arrived.finally(() => clearInterval(scrubbing));

    assert.equal(pose.slug, SLUG);
    assert.ok(pose.position && typeof pose.position.x === 'number');
    assert.equal(typeof pose.sentAtMs, 'number');

    // Kept for the PlayMode fixture to replay byte for byte. One frame per line so nothing has to
    // parse and re-serialise them on the way in — a round trip through two of my own functions
    // would only prove that they agree with each other.
    await mkdir(path.dirname(POSE_RECORDING), { recursive: true });
    await writeFile(POSE_RECORDING,
                    `${device.of('pose').map((p) => JSON.stringify(p)).join('\n')}\n`);

    await page.close();
  });

  test('the controller displays a status frame recorded from a real Unity build', async (t) => {
    let recorded;
    try {
      recorded = JSON.parse(await readFile(STATUS_RECORDING, 'utf8'));
    } catch {
      t.skip(`no ${path.relative(ROOT, STATUS_RECORDING)} — produce it by running the PlayMode `
             + 'suite: Unity -batchmode -runTests -projectPath app -testPlatform PlayMode');
      return;
    }
    assert.equal(recorded.type, 'status', 'the recording should be a status frame');

    const device = await join(service.base, 'device');
    sockets.push(device);
    device.send({ type: 'hello', device: 'iPhone', os: 'iOS', build: 'dev' });
    device.send(recorded);

    const page = await context.newPage();
    await page.goto(`${service.base}/control`, { waitUntil: 'domcontentloaded' });

    await until(async () => (await page.textContent('#r-shoal')) === String(recorded.shoalCount),
                { timeoutMs: 20_000, label: "the controller to show the phone's shoal count" });

    assert.match(await page.textContent('#r-s'), new RegExp(recorded.s.toFixed(1)),
                 'the distance along the creek should be displayed');
    assert.equal(await page.textContent('#r-conf'), recorded.trackingConfidence.toFixed(2),
                 'tracking confidence should be displayed');

    // The allow-list from the other side: the bus merged what Unity sent, and the page reads the
    // merged state. Anything outside the list would have reached neither.
    const { state } = await (await fetch(`${service.base}/api/control/state`)).json();
    for (const key of Object.keys(recorded)) {
      if (key === 'type') continue;
      assert.ok(key in state, `the bus dropped '${key}', which Unity bothered to send`);
    }
    assert.equal(state.currentBeat, recorded.currentBeat);
    assert.equal(state.localization, recorded.localization);

    await page.close();
  });
});
