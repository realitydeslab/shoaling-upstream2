/**
 * The audio catalogue, checked against the real files.
 *
 * Unlike the other suites, this one deliberately reads `data/audio/` and the garden draft rather
 * than a fixture. A fixture would prove that the resolution rule is consistent with itself; the
 * thing that actually goes wrong is that a recording never made it into the repo, the packaging
 * script quietly wrote `"missing": true`, and the journey went on referencing clips that no
 * longer had files behind them. Only the real tree catches that.
 *
 * That did happen: two source recordings were too large for a direct Drive download, so
 * `tree-creek-waterplants-1/2` were catalogued as missing while the garden's "tree" beat still
 * named all three of their distance layers. The beat had no audio at all and nothing failed.
 *
 * The server is started against the real audio directory — it resolves `data/audio/` from its own
 * location rather than from DATA_DIR — while JOURNEY_DIR points at a throwaway, so the suite can
 * fetch real clips without any risk to the authored journeys.
 */

import test, { before, after } from 'node:test';
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import type { ChildProcess } from 'node:child_process';
import { readFile, stat } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

import { makeDataRoot, until } from './helpers.ts';
import type { AudioCatalogue, JourneyDocument, Layer } from '../editor/src/types.ts';

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const AUDIO_DIR = path.join(ROOT, 'data', 'audio');
const PACKAGED_DIR = path.join(AUDIO_DIR, 'packaged');
const GARDEN_DRAFT = path.join(ROOT, 'data', 'journeys', 'ubc-nitobe-garden-creek', 'draft.json');

/**
 * What the packaging script may write in `layers`.
 *
 * `Layer` in types.ts is the three distance takes, which is all the editor ever reads — but
 * package-audio.sh also writes `"one-shot"` for a recording that has no distance layers at all,
 * and this suite is the thing that has to tell the two apart.
 */
type CatalogueLayer = Layer | 'one-shot';

/** The three distance layers every non-one-shot source is packaged into. */
const DISTANCE_LAYERS: readonly Layer[] = ['far', 'mid', 'intimate'];

/**
 * The rule that turns a catalogue entry into the clip ids a beat may name.
 *
 * A layered source publishes one id per layer, suffixed `--far` / `--mid` / `--intimate`; a
 * one-shot publishes its bare id. An entry marked `missing` publishes nothing, which is what
 * makes the reference check below bite.
 */
function resolveClips(catalogue: AudioCatalogue): Map<string, string> {
  const byId = new Map<string, string>();
  for (const clip of catalogue.clips) {
    if (clip.missing) continue;
    const base = String(clip.file ?? `${clip.clipId}.mp3`).replace(/\.mp3$/, '');
    const layers: readonly CatalogueLayer[] = clip.layers ?? [];
    if (layers.includes('one-shot')) {
      byId.set(clip.clipId, `${base}.mp3`);
    } else {
      for (const layer of layers) byId.set(`${clip.clipId}--${layer}`, `${base}--${layer}.mp3`);
    }
  }
  return byId;
}

/**
 * Real audio, not a saved error page.
 *
 * The failure this guards against is a download that returned HTML — a sign-in page or a Drive
 * interstitial — and got saved under a .wav name, or a fetch that 404'd into the body. An MP3
 * opens with an ID3 tag or a raw frame sync; a WAV opens with RIFF. HTML opens with `<`.
 */
function looksLikeAudio(bytes: Buffer): boolean {
  if (bytes.length < 4) return false;
  const ascii = bytes.subarray(0, 4).toString('latin1');
  if (ascii === 'RIFF' || ascii.startsWith('ID3') || ascii === 'OggS' || ascii === 'fLaC') return true;
  // A bare MPEG frame: 11 sync bits. Both bytes are present — the length was checked above.
  return bytes[0] === 0xff && (bytes[1]! & 0xe0) === 0xe0;
}

let server: ChildProcess;
let base: string;
let cleanup: (() => Promise<void>) | undefined;
let catalogue: AudioCatalogue;
let clips: Map<string, string>;

before(async () => {
  catalogue = JSON.parse(await readFile(path.join(AUDIO_DIR, 'catalogue.json'), 'utf8'));
  clips = resolveClips(catalogue);

  const data = await makeDataRoot();
  cleanup = data.cleanup;

  server = spawn(process.execPath, [path.join(ROOT, 'service', 'src', 'server.ts')], {
    env: {
      ...process.env,
      PORT: '0',
      DATA_DIR: data.base,
      JOURNEY_DIR: data.journeysRoot,
    },
    stdio: ['ignore', 'pipe', 'pipe'],
  });

  let output = '';
  // Both streams are pipes by the stdio above; the general signature of spawn cannot say so.
  server.stdout!.on('data', (chunk) => { output += chunk; });
  server.stderr!.on('data', (chunk) => { output += chunk; });

  const port = await until<string | null>(() => {
    const match = /https?:\/\/[^\s]*?:(\d+)/.exec(output);
    return match?.[1] ?? null;
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

test('the catalogue lists clips, and none of them is missing its source', () => {
  assert.ok(Array.isArray(catalogue.clips), 'the catalogue has a clips array');
  assert.ok(catalogue.clips.length > 0, 'the catalogue is not empty');

  // `missing: true` is how package-audio.sh records a source recording it could not find. It is
  // information, not an error — but it must never survive into a catalogue the journey plays from.
  const missing = catalogue.clips.filter((c) => c.missing).map((c) => c.source ?? c.clipId);
  assert.deepEqual(missing, [], 'every source recording was present when the clips were packaged');
});

test('every source recording has all three distance layers', () => {
  for (const clip of catalogue.clips) {
    const layers: readonly CatalogueLayer[] = clip.layers ?? [];
    if (layers.includes('one-shot')) {
      assert.deepEqual(layers, ['one-shot'], `${clip.clipId} is a one-shot and nothing else`);
      continue;
    }
    assert.deepEqual(
      [...layers].sort(), [...DISTANCE_LAYERS].sort(),
      `${clip.clipId} carries exactly far / mid / intimate`,
    );
    for (const layer of DISTANCE_LAYERS) {
      assert.ok(clips.has(`${clip.clipId}--${layer}`), `${clip.clipId}--${layer} resolves`);
    }
  }
});

test('every clip id resolves to a non-empty file on disk', async () => {
  assert.ok(clips.size > 0, 'the catalogue resolves to at least one clip');
  for (const [clipId, file] of clips) {
    const full = path.join(PACKAGED_DIR, file);
    const info = await stat(full).catch(() => null);
    assert.ok(info?.isFile(), `${clipId} has a file at ${path.relative(ROOT, full)}`);
    assert.ok(info!.size > 0, `${clipId} is not a zero-byte file`);
  }
});

test('every packaged file is real audio, not a saved error page', async () => {
  for (const [clipId, file] of clips) {
    const handle = await readFile(path.join(PACKAGED_DIR, file));
    assert.ok(
      looksLikeAudio(handle.subarray(0, 4)),
      `${clipId} begins with an audio signature, not ${JSON.stringify(handle.subarray(0, 8).toString('latin1'))}`,
    );
  }
});

test('the served catalogue matches the one on disk', async () => {
  const res = await fetch(`${base}/api/audio`);
  assert.equal(res.status, 200);
  const served = await res.json() as AudioCatalogue;
  assert.deepEqual(
    served.clips, catalogue.clips,
    'the API serves the catalogue the packaging script wrote, not a placeholder',
  );
});

test('every clip is fetchable over HTTP as audio', async () => {
  for (const [clipId, file] of clips) {
    const res = await fetch(`${base}/audio/${encodeURIComponent(file)}`);
    assert.equal(res.status, 200, `${clipId} is served`);

    const type = res.headers.get('content-type') ?? '';
    assert.match(type, /^audio\//, `${clipId} is served as audio, not ${type}`);

    const length = Number(res.headers.get('content-length'));
    assert.ok(length > 0, `${clipId} declares a non-zero content-length`);

    // And the bytes on the wire really are audio — a body of HTML with an audio content-type
    // would still fail here.
    const bytes = Buffer.from(await res.arrayBuffer());
    assert.equal(bytes.length, length, `${clipId} delivers the bytes it promised`);
    assert.ok(looksLikeAudio(bytes), `${clipId} is audio over the wire`);
  }
});

test('a clip that does not exist is a 404, not an HTML page with a 200', async () => {
  const res = await fetch(`${base}/audio/no-such-clip--mid.mp3`);
  assert.equal(res.status, 404);
});

test('every clip the garden journey references exists, in the catalogue and on disk', async () => {
  const draft: JourneyDocument = JSON.parse(await readFile(GARDEN_DRAFT, 'utf8'));
  assert.ok(draft.beats.length > 0, 'the garden draft has beats to check');

  // Every audio slot on a beat: the three distance layers, plus the completion one-shot where a
  // beat has one. Reading the keys rather than naming them means a new slot is covered the day
  // it is added, instead of the day someone remembers to update this test.
  const referenced = new Map<string, string>();
  for (const beat of draft.beats) {
    for (const [slot, entry] of Object.entries(beat.audio ?? {})) {
      if (!entry?.clipId) continue;
      referenced.set(entry.clipId, `beat "${beat.id}" (${slot})`);
    }
  }
  assert.ok(referenced.size > 0, 'the garden draft references clips at all');

  for (const [clipId, where] of referenced) {
    const file = clips.get(clipId);
    assert.ok(file, `${where} names "${clipId}", which the catalogue publishes`);

    const info = await stat(path.join(PACKAGED_DIR, file)).catch(() => null);
    assert.ok(info?.isFile() && info.size > 0, `${where} names "${clipId}", which has a file`);
  }
});

test('every beat carries all three distance layers', async () => {
  const draft: JourneyDocument = JSON.parse(await readFile(GARDEN_DRAFT, 'utf8'));
  for (const beat of draft.beats) {
    for (const layer of DISTANCE_LAYERS) {
      assert.ok(beat.audio?.[layer]?.clipId, `beat "${beat.id}" has a ${layer} clip`);
    }
  }
});
