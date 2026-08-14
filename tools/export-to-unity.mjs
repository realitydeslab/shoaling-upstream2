/**
 * Package a published journey and every clip it plays into the iOS build.
 *
 * The app has to work with no laptop anywhere near it: the Berkeley coauthor installs from
 * TestFlight and walks a creek alone. So the build carries a complete journey and a complete
 * set of audio, and a reachable service is only ever an override on top of that. This script
 * is what makes the build complete.
 *
 *   app/Assets/StreamingAssets/ShoalingUpstream/
 *     manifest.json               which sites are packaged, and which one this build walks
 *     journeys/<slug>.json        the published revision, verbatim
 *     audio/index.json            clip ids with their byte lengths
 *     audio/<clipId>.mp3          one file per clip id the journey names
 *
 * Three rules, each of them from a failure that is invisible until someone is standing in a
 * creek:
 *
 * 1. A REFERENCED CLIP WITH NO FILE STOPS THE EXPORT. Shipping the journey anyway produces a
 *    beat that fires correctly and plays nothing, which reads on site as a bug in the trigger
 *    machine — the hardest thing here to debug, and the thing least likely to be believed.
 *
 * 2. AN UNCALIBRATED JOURNEY NEEDS --allow-uncalibrated. The app refuses one on device anyway;
 *    without the flag, packaging it would produce a build that installs, launches and then
 *    declines to run, with the reason buried in a device log.
 *
 * 3. NOTHING WRITTEN CARRIES A TIMESTAMP. Re-running with no changes upstream must leave the
 *    build byte-identical, so that "the assets changed" always means something changed. The
 *    time of the run goes to the terminal, not into the build.
 *
 *   node tools/export-to-unity.mjs --site ubc-nitobe-garden-creek --allow-uncalibrated
 *   node tools/export-to-unity.mjs --site ubc-nitobe-garden-creek --check
 *   node tools/export-to-unity.mjs --site ucb-strawberry-creek-south --from-service
 */

import { readFile, writeFile, mkdir, readdir, stat, unlink } from 'node:fs/promises';
import { existsSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { validateJourney, SCHEMA_VERSION, LAYERS } from '../service/src/journey-schema.mjs';

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');

/** Paths inside the repo read better relative; anything else has to stay absolute. */
const show = (p) => (p.startsWith(ROOT + path.sep) ? path.relative(ROOT, p) : p);

const DEFAULTS = {
  site: 'ubc-nitobe-garden-creek',
  service: process.env.SERVICE ?? 'http://localhost:8710',
  journeyDir: path.join(ROOT, 'data', 'journeys'),
  audioDir: path.join(ROOT, 'data', 'audio', 'packaged'),
  out: path.join(ROOT, 'app', 'Assets', 'StreamingAssets', 'ShoalingUpstream'),
};

// --- arguments ------------------------------------------------------------

function parseArgs(argv) {
  const opts = {
    ...DEFAULTS,
    allowUncalibrated: false,
    fromService: false,
    check: false,
    prune: false,
    makeDefault: false,
  };
  for (let i = 0; i < argv.length; i += 1) {
    const arg = argv[i];
    const value = () => {
      const next = argv[i + 1];
      if (next === undefined || next.startsWith('--')) die(`${arg} needs a value`);
      i += 1;
      return next;
    };
    switch (arg) {
      case '--site': opts.site = value(); break;
      case '--out': opts.out = path.resolve(value()); break;
      case '--audio-dir': opts.audioDir = path.resolve(value()); break;
      case '--journey-dir': opts.journeyDir = path.resolve(value()); break;
      case '--service': opts.service = value(); opts.fromService = true; break;
      case '--from-service': opts.fromService = true; break;
      case '--allow-uncalibrated': opts.allowUncalibrated = true; break;
      case '--check': opts.check = true; break;
      case '--prune': opts.prune = true; break;
      case '--default': opts.makeDefault = true; break;
      case '--help': case '-h': usage(); process.exit(0); break;
      default: die(`unknown argument ${arg}`);
    }
  }
  return opts;
}

function usage() {
  console.log(`
  node tools/export-to-unity.mjs [options]

    --site <slug>            which site to package (default ${DEFAULTS.site})
    --from-service           read GET /published from the running service instead of the
                             revisions on disk
    --service <url>          service base URL, implies --from-service
    --allow-uncalibrated     package a journey whose coordinates have not been matched to
                             three physical points. It will run in simulation only.
    --check                  report what would change and write nothing; exits non-zero if
                             the build is not already up to date
    --prune                  delete packaged clips no bundled journey references
    --default                make this site the one the build launches into
    --out <dir>              output directory (default app/Assets/StreamingAssets/ShoalingUpstream)
    --audio-dir <dir>        packaged clips (default data/audio/packaged)
    --journey-dir <dir>      journey store (default data/journeys)
`);
}

function die(message) {
  console.error(`\n  ${message}\n`);
  process.exit(2);
}

// --- reading the published revision ---------------------------------------

async function latestRevisionOnDisk(journeyDir, slug) {
  const dir = path.join(journeyDir, slug, 'revisions');
  if (!existsSync(dir)) return null;
  const numbers = (await readdir(dir))
    .map((f) => /^r(\d+)\.json$/.exec(f))
    .filter(Boolean)
    .map((m) => Number(m[1]))
    .sort((a, b) => a - b);
  if (numbers.length === 0) return null;

  const file = path.join(dir, `r${String(numbers.at(-1)).padStart(6, '0')}.json`);
  return { document: JSON.parse(await readFile(file, 'utf8')), from: show(file) };
}

async function publishedFromService(base, slug) {
  const url = `${base.replace(/\/$/, '')}/api/sites/${slug}/published`;
  const res = await fetch(url).catch((err) => die(`${url}: ${err.message}`));
  if (res.status === 404) die(`${slug} has no published revision — press Publish in the editor`);
  if (!res.ok) die(`${url}: HTTP ${res.status}`);
  return { document: await res.json(), from: url };
}

// --- what a journey plays -------------------------------------------------

/**
 * Every clip id a journey names, beats and ambient sources alike.
 *
 * Mirrored by AudioCatalogue.ReferencedClipIds in the app. If the two ever disagree the app
 * asks for something this script never packaged, which is exactly the silent beat the whole
 * script exists to prevent — so keep them in step.
 */
function clipIdsIn(document) {
  const ids = new Set();
  const add = (layer) => {
    if (layer && typeof layer.clipId === 'string' && layer.clipId.trim()) ids.add(layer.clipId);
  };
  const addAll = (audio) => {
    if (!audio) return;
    for (const layer of LAYERS) add(audio[layer]);
    add(audio.completion);
  };
  for (const beat of document.beats ?? []) addAll(beat.audio);
  for (const source of document.ambient ?? []) addAll(source.audio);
  return [...ids].sort();
}

// --- idempotent writes ----------------------------------------------------

/** Write only when the bytes differ, and say which happened. */
async function put(file, contents, { check }) {
  const buffer = Buffer.isBuffer(contents) ? contents : Buffer.from(contents, 'utf8');
  if (existsSync(file)) {
    const current = await readFile(file);
    if (current.equals(buffer)) return 'unchanged';
  }
  if (!check) {
    await mkdir(path.dirname(file), { recursive: true });
    await writeFile(file, buffer);
  }
  return 'wrote';
}

const json = (value) => `${JSON.stringify(value, null, 2)}\n`;

// --- main -----------------------------------------------------------------

const opts = parseArgs(process.argv.slice(2));

const source = opts.fromService
  ? await publishedFromService(opts.service, opts.site)
  : await latestRevisionOnDisk(opts.journeyDir, opts.site);

if (!source) {
  die(`no published revision for "${opts.site}" in ${show(opts.journeyDir)} — `
    + 'press Publish in the editor, or pass --from-service');
}

const doc = source.document;

// The app refuses a schema it does not read rather than half-parsing it, so packaging one
// would only move the failure to a device log.
if (doc.schemaVersion !== SCHEMA_VERSION) {
  die(`${opts.site} is schemaVersion ${JSON.stringify(doc.schemaVersion)}, the app reads `
    + `"${SCHEMA_VERSION}"`);
}

const validation = validateJourney(doc);
if (!validation.ok) {
  console.error(`\n  ${opts.site} r${doc.revision} does not validate:\n`);
  for (const error of validation.errors) console.error(`    ${error}`);
  console.error('');
  process.exit(1);
}

const calibrated = doc.editorFrame?.calibrated === true;
if (!calibrated && !opts.allowUncalibrated) {
  die(`${opts.site} r${doc.revision} is not calibrated — its coordinates have never been `
    + 'matched to three physical points, and the app refuses one on device. Pass '
    + '--allow-uncalibrated to package it for simulation anyway.');
}

if (doc.site?.slug !== opts.site) {
  die(`the document says site.slug is ${JSON.stringify(doc.site?.slug)}, not "${opts.site}"`);
}

// --- clips ----------------------------------------------------------------

const clipIds = clipIdsIn(doc);
const clips = [];
const missing = [];

for (const clipId of clipIds) {
  const file = path.join(opts.audioDir, `${clipId}.mp3`);
  if (!existsSync(file)) {
    missing.push(clipId);
    continue;
  }
  clips.push({ clipId, file, bytes: (await stat(file)).size });
}

if (missing.length) {
  console.error(`\n  ${missing.length} clip(s) the journey plays are not packaged:\n`);
  for (const clipId of missing) {
    console.error(`    ${clipId}  (expected ${show(path.join(opts.audioDir, `${clipId}.mp3`))})`);
  }
  console.error('\n  Nothing was written. A journey shipped without its audio fires its beats');
  console.error('  correctly and plays silence, which on site looks like a broken trigger.');
  console.error('  Fetch the sources into data/audio/source/ and run tools/package-audio.sh.\n');
  process.exit(1);
}

const emptyClips = clips.filter((c) => c.bytes === 0);
if (emptyClips.length) {
  console.error(`\n  ${emptyClips.length} clip file(s) are empty: `
    + `${emptyClips.map((c) => c.clipId).join(', ')}\n`);
  process.exit(1);
}

// --- write ----------------------------------------------------------------

const audioDir = path.join(opts.out, 'audio');
const results = [];

results.push(['journey', `journeys/${opts.site}.json`, await put(
  path.join(opts.out, 'journeys', `${opts.site}.json`), json(doc), opts)]);

for (const clip of clips) {
  results.push(['clip', `audio/${clip.clipId}.mp3`, await put(
    path.join(audioDir, `${clip.clipId}.mp3`), await readFile(clip.file), opts)]);
}

results.push(['index', 'audio/index.json', await put(
  path.join(audioDir, 'index.json'),
  json({ clips: clips.map(({ clipId, bytes }) => ({ clipId, file: `${clipId}.mp3`, bytes })) }),
  opts)]);

// The manifest accumulates: packaging UBC must not unpackage Berkeley, because switching site
// is meant to be a switch rather than a rebuild of the assets.
const manifestPath = path.join(opts.out, 'manifest.json');
const manifest = existsSync(manifestPath)
  ? JSON.parse(await readFile(manifestPath, 'utf8'))
  : { generatedBy: 'tools/export-to-unity.mjs', defaultSlug: null, sites: [] };

manifest.generatedBy = 'tools/export-to-unity.mjs';
manifest.sites = [
  ...(manifest.sites ?? []).filter((s) => s.slug !== opts.site),
  { slug: opts.site, title: doc.title ?? opts.site, revision: doc.revision, calibrated },
].sort((a, b) => a.slug.localeCompare(b.slug));

if (opts.makeDefault || !manifest.defaultSlug
    || !manifest.sites.some((s) => s.slug === manifest.defaultSlug)) {
  manifest.defaultSlug = opts.site;
}

results.push(['manifest', 'manifest.json', await put(manifestPath, json(manifest), opts)]);

// --- prune ----------------------------------------------------------------

const keep = new Set(clips.map((c) => `${c.clipId}.mp3`));
keep.add('index.json');
for (const other of manifest.sites) {
  if (other.slug === opts.site) continue;
  const file = path.join(opts.out, 'journeys', `${other.slug}.json`);
  if (!existsSync(file)) continue;
  for (const clipId of clipIdsIn(JSON.parse(await readFile(file, 'utf8')))) {
    keep.add(`${clipId}.mp3`);
  }
}

// Unity's .meta files belong to Unity. Deleting one orphans the importer settings and the
// asset's guid, which is a worse mess than a stale clip.
const orphans = existsSync(audioDir)
  ? (await readdir(audioDir)).filter((f) => !keep.has(f) && !f.endsWith('.meta'))
  : [];

if (orphans.length && opts.prune && !opts.check) {
  for (const file of orphans) await unlink(path.join(audioDir, file));
}

// --- report ---------------------------------------------------------------

const wrote = results.filter(([, , what]) => what === 'wrote');
const bytes = clips.reduce((total, clip) => total + clip.bytes, 0);
const stamp = new Date().toISOString().replace('T', ' ').slice(0, 19);

console.log('');
console.log(`  ${opts.check ? 'checking' : 'packaging'} ${opts.site} r${doc.revision}`
  + `${calibrated ? '' : '  UNCALIBRATED — simulation only'}`);
console.log(`  from  ${source.from}`);
console.log(`  into  ${show(opts.out)}`);
console.log('');

for (const [kind, name, what] of results) {
  if (what === 'wrote' || opts.check) {
    console.log(`    ${what === 'wrote' ? (opts.check ? 'would write' : 'wrote     ') : 'unchanged '} ${name}`);
  }
}
if (!opts.check && wrote.length === 0) console.log('    everything was already up to date');

console.log('');
console.log(`  ${clips.length} clips, ${(bytes / 1e6).toFixed(1)} MB`
  + `  ·  ${wrote.length} file(s) ${opts.check ? 'would change' : 'changed'}`
  + `  ·  ${manifest.sites.length} site(s) bundled, launching into ${manifest.defaultSlug}`);

if (orphans.length) {
  console.log('');
  console.log(`  ${orphans.length} packaged clip(s) no bundled journey references`
    + `${opts.prune && !opts.check ? ' — deleted' : ' — pass --prune to delete'}:`);
  for (const file of orphans) console.log(`    ${file}`);
}

if (validation.warnings?.length) {
  console.log('');
  for (const warning of validation.warnings) console.log(`  warning: ${warning}`);
}

console.log(`\n  ${stamp}\n`);

// --check is for the Unity menu and for a build step: a non-zero exit means the build does not
// match what the editor has published.
if (opts.check && wrote.length > 0) process.exit(1);
