/**
 * End to end over HTTP, against a real server on a real port.
 *
 * This is the contract the iOS app depends on. The editor writes a draft, someone publishes it,
 * and the device fetches the published revision — three processes that only ever meet through
 * these endpoints. Everything here goes through fetch rather than through the store, so a route
 * that stops being wired up fails the suite even though the store beneath it still works.
 *
 * The server is started with DATA_DIR pointed at a throwaway directory, so the suite never
 * touches the real journeys.
 */

import test, { before, after } from 'node:test';
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

import { makeDataRoot, makeJourney, SLUG, until } from './helpers.mjs';

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');

let server;
let base;
let cleanup;

before(async () => {
  const data = await makeDataRoot(makeJourney());
  cleanup = data.cleanup;

  server = spawn(process.execPath, [path.join(ROOT, 'service', 'src', 'server.mjs')], {
    env: {
      ...process.env,
      // Port 0 asks the OS for a free one, so the suite never collides with a dev server the
      // artist has open — which is the normal state of this machine.
      PORT: '0',
      DATA_DIR: data.base,
      JOURNEY_DIR: data.journeysRoot,
    },
    stdio: ['ignore', 'pipe', 'pipe'],
  });

  let output = '';
  server.stdout.on('data', (chunk) => { output += chunk; });
  server.stderr.on('data', (chunk) => { output += chunk; });

  const port = await until(() => {
    const match = /https?:\/\/[^\s]*?:(\d+)/.exec(output);
    return match ? match[1] : null;
  }, { timeoutMs: 8000, label: `the server to report a port (output so far: ${output})` });

  base = `http://127.0.0.1:${port}`;
  await until(async () => {
    try { return (await fetch(`${base}/api/health`)).ok; } catch { return false; }
  }, { timeoutMs: 8000, label: 'the server to answer /api/health' });
});

after(async () => {
  server?.kill('SIGTERM');
  await cleanup?.();
});

const get = async (p) => {
  const res = await fetch(`${base}${p}`);
  return { status: res.status, body: await res.json().catch(() => null) };
};

const send = async (p, method, body) => {
  const res = await fetch(`${base}${p}`, {
    method,
    headers: { 'content-type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  return { status: res.status, body: await res.json().catch(() => null) };
};

test('health check answers', async () => {
  const { status, body } = await get('/api/health');
  assert.equal(status, 200);
  assert.ok(body);
});

test('the site list includes the seeded site', async () => {
  const { status, body } = await get('/api/sites');
  assert.equal(status, 200);
  assert.ok(body.some((s) => s.slug === SLUG), 'test site is listed');
});

test('the interaction vocabulary is served, and covers what the piece uses', async () => {
  const { status, body } = await get('/api/interactions');
  assert.equal(status, 200);
  // The six interactions the artwork is built from. A rename here silently breaks every draft.
  for (const kind of ['proximity', 'crouch', 'catch', 'give', 'lift']) {
    assert.ok(body[kind], `interaction "${kind}" is defined`);
  }
});

test('a draft can be read', async () => {
  const { status, body } = await get(`/api/sites/${SLUG}/draft`);
  assert.equal(status, 200);
  assert.equal(body.site.slug, SLUG);
});

test('an unknown site 404s rather than 500s', async () => {
  const { status } = await get('/api/sites/no-such-site/draft');
  assert.equal(status, 404);
});

test('an invalid draft is refused with 422 and the reasons', async () => {
  const broken = makeJourney();
  broken.beats[0].trigger.enterRadiusM = -5;
  const { status, body } = await send(`/api/sites/${SLUG}/draft`, 'PUT', broken);
  assert.equal(status, 422);
  assert.ok(body.errors.length > 0, 'the client is told what was wrong');
});

test('the walking path saves on its own and re-derives every beat s', async () => {
  const { status, body } = await send(`/api/sites/${SLUG}/site`, 'PUT', {
    centreline: [
      { x: 0, y: 1.4, z: 0 },
      { x: 4, y: 1.4, z: 0 },
    ],
  });
  assert.equal(status, 200);
  assert.equal(body.beats[0].s, 2);
  assert.equal(body.beats[1].s, 4, 'a beat past the new end clamps to it');

  // And it is genuinely on disk, not just in the response.
  const after = await get(`/api/sites/${SLUG}/draft`);
  assert.equal(after.body.site.centreline.length, 2);
});

test('the trim box saves without touching the journey', async () => {
  const trim = {
    enabled: true,
    position: { x: -1.262, y: -0.784, z: -1.62 },
    rotation: [0, 0.39564, 0, 0.91841],
    halfExtent: { x: 7.466, y: 2.005, z: 3.476 },
  };
  const { status } = await send(`/api/sites/${SLUG}/editor-frame`, 'PUT', { trim });
  assert.equal(status, 200);

  const { body } = await get(`/api/sites/${SLUG}/draft`);
  assert.deepEqual(body.editorFrame.trim.halfExtent, trim.halfExtent);
  assert.equal(body.beats.length, 2, 'the beats are untouched');
});

test('validate reports problems without saving anything', async () => {
  const broken = makeJourney();
  // A journey from an older schema. An empty beats array is deliberately NOT used here: it is
  // legal, because a journey under construction has no beats yet.
  broken.schemaVersion = '1.0';
  const { status, body } = await send(`/api/sites/${SLUG}/validate`, 'POST', broken);
  assert.equal(status, 200, 'validation itself succeeds, it just reports');
  assert.equal(body.ok, false);
  assert.ok(body.errors.some((e) => /schemaVersion/.test(e)));

  const { body: draft } = await get(`/api/sites/${SLUG}/draft`);
  assert.equal(draft.beats.length, 2, 'nothing was written');
});

test('a journey with no beats yet is valid — it is just unfinished', async () => {
  const empty = makeJourney();
  empty.beats = [];
  const { body } = await send(`/api/sites/${SLUG}/validate`, 'POST', empty);
  assert.equal(body.ok, true);
});

test('publish, then read back what the device would fetch', async () => {
  const { status, body } = await send(`/api/sites/${SLUG}/publish`, 'POST');
  assert.equal(status, 200);
  assert.ok(body.revision >= 1);

  // This is the exact call the iOS app makes.
  const published = await get(`/api/sites/${SLUG}/published`);
  assert.equal(published.status, 200);
  assert.equal(published.body.site.slug, SLUG);
  assert.ok(Array.isArray(published.body.beats));
});

test('editing after publishing does not change the published revision', async () => {
  const before = await get(`/api/sites/${SLUG}/published`);

  const { body: draft } = await get(`/api/sites/${SLUG}/draft`);
  draft.title = 'Edited after publishing';
  const put = await send(`/api/sites/${SLUG}/draft`, 'PUT', draft);
  assert.equal(put.status, 200);

  const after = await get(`/api/sites/${SLUG}/published`);
  assert.equal(after.body.title, before.body.title,
    'the device keeps seeing the published revision until someone publishes again');
});

test('the scan and audio catalogues are served', async () => {
  for (const route of ['/api/scans', '/api/audio']) {
    const { status } = await get(route);
    assert.equal(status, 200, `${route} answers`);
  }
});

test('a malformed body is a 400, not a crash', async () => {
  const res = await fetch(`${base}/api/sites/${SLUG}/draft`, {
    method: 'PUT',
    headers: { 'content-type': 'application/json' },
    body: '{ this is not json',
  });
  assert.equal(res.status, 400);

  // The server is still alive afterwards, which is the real assertion.
  assert.equal((await get('/api/health')).status, 200);
});
