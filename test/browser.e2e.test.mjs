/**
 * The editor and the controller, in a real browser.
 *
 * Everything else in test/ can pass while the editor is a blank screen. The editor is ES
 * modules, an import map, three.js and spark.js running against real WebGL — none of which
 * Node can stand in for — and its two worst regressions so far were both invisible to the
 * other suites: a `Cannot set properties of null` thrown during boot, and a call to a private
 * field that had been renamed, which only failed when the line was actually reached. Both
 * leave the page half-built and silent. So the load-bearing assertion here is not any single
 * feature: it is that booting the editor produces no page error, no console error and no
 * error toast, and that the scan really arrives.
 *
 * Playwright is not a dependency of this repository and may never be one — the machine this
 * runs on is the artist's laptop. When it is absent the whole suite skips and the run stays
 * green; see test/README.md for how to turn it on.
 *
 * The server is started with JOURNEY_DIR pointed at a throwaway directory, so nothing here can
 * write to the real journeys. The scan itself is read from data/splats, which is read-only as
 * far as the editor is concerned.
 */

import test, { describe, before, after } from 'node:test';
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { existsSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

import { makeDataRoot, makeJourney, SLUG, until } from './helpers.mjs';

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');

const HOW_TO_ENABLE = 'playwright is not available — run `npm i -D playwright && npx playwright '
  + 'install chromium` from the repository root to enable the browser suite (see test/README.md)';

let browser = null;
let unavailable = null;
try {
  const { chromium } = await import('playwright');
  browser = await chromium.launch({
    args: [
      // This machine has a GPU; a CI container and a headless shell do not, and spark.js needs
      // WebGL2 either way. SwiftShader is slow rather than absent, which is what the generous
      // timeouts below are for.
      '--enable-unsafe-swiftshader',
      '--use-gl=angle',
      '--use-angle=swiftshader',
      '--ignore-gpu-blocklist',
      '--disable-dev-shm-usage',
    ],
  });
} catch (err) {
  // Covers both "not installed" and "installed, but the browser binary was never downloaded".
  unavailable = `${HOW_TO_ENABLE} — ${err.message.split('\n')[0]}`;
}

/**
 * The scan the editor is pointed at.
 *
 * A fixture journey names a real capture in data/splats: those files are large, are never
 * written to, and are the only way to exercise the loader for real. `bounds` is the block
 * tools/stamp-bounds.py writes, and it is required rather than decorative — with LOD enabled
 * the splats live in the level-of-detail tree, `forEachSplat` walks nothing, and an unstamped
 * journey reports zero splats however well the scan loaded.
 */
const SCAN_BASE = 'ubc-nitobe-garden-creek';
const SCAN_FILE = `${SCAN_BASE}.proxy.spz`;
const SCAN_PRESENT = [
  path.join(ROOT, 'data', 'splats', 'rad', `${SCAN_BASE}.rad`),
  path.join(ROOT, 'data', 'splats', `${SCAN_BASE}.spz`),
  path.join(ROOT, 'data', 'splats', 'proxy', SCAN_FILE),
].some(existsSync);

function makeScannedJourney() {
  const journey = makeJourney();
  journey.editorFrame.splatFile = SCAN_FILE;
  journey.editorFrame.bounds = {
    splats: 4963155,
    lo: { x: -12.593, y: -1.975, z: -17.609 },
    hi: { x: 21.338, y: 9.684, z: 9.998 },
    centre: { x: 0.276, y: 0.117, z: -0.356 },
    span: { x: 33.93, y: 11.659, z: 27.606 },
  };
  return journey;
}

/** Record every toast the page raises, including ones that fade before an assertion runs. */
const WATCH_TOASTS = () => {
  window.__toasts = [];
  const watch = () => {
    const el = document.getElementById('toast');
    if (!el) return;
    const record = () => {
      const entry = { text: el.textContent, error: el.classList.contains('error') };
      const last = window.__toasts.at(-1);
      if (!el.classList.contains('show')) return;
      if (last && last.text === entry.text && last.error === entry.error) return;
      window.__toasts.push(entry);
    };
    new MutationObserver(record).observe(el, {
      attributes: true, childList: true, characterData: true, subtree: true,
    });
    record();
  };
  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', watch);
  } else {
    watch();
  }
};

describe('the editor in a real browser', { skip: unavailable ?? false }, () => {
  let server;
  let cleanup;
  let base;
  let context;
  let page;

  const pageErrors = [];
  const consoleErrors = [];

  before(async () => {
    const data = await makeDataRoot(makeScannedJourney());
    cleanup = data.cleanup;

    server = spawn(process.execPath, [path.join(ROOT, 'service', 'src', 'server.mjs')], {
      env: { ...process.env, PORT: '0', DATA_DIR: data.base, JOURNEY_DIR: data.journeysRoot },
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

    // A fresh context every run: the editor remembers layer visibility and scan quality in
    // localStorage, so a reused profile would make the suite depend on what it did last time.
    context = await browser.newContext({ viewport: { width: 1440, height: 900 } });
    await context.addInitScript(WATCH_TOASTS);

    page = await context.newPage();
    page.on('pageerror', (err) => pageErrors.push(err.stack ?? String(err)));
    page.on('console', (msg) => {
      // The page has no favicon, and Chromium logs the resulting 404 as a console error. That
      // is the only noise allowed through: everything else is the point of this suite.
      if (msg.type() === 'error' && !/favicon/.test(msg.text())) consoleErrors.push(msg.text());
    });

    await page.goto(`${base}/`, { waitUntil: 'domcontentloaded' });
    // The loading overlay is hidden at the end of loadSite(), after the scan has resolved —
    // a real condition, rather than a sleep long enough to hide a slow regression.
    await page.waitForFunction(
      () => window.__editor && document.getElementById('loading').hidden,
      undefined, { timeout: 90_000 });
  }, { timeout: 150_000 });

  after(async () => {
    await context?.close();
    await browser?.close();
    server?.kill('SIGTERM');
    await cleanup?.();
  });

  const toasts = () => page.evaluate(() => window.__toasts);
  const layers = () => page.evaluate(() => window.__editor.state.layers);
  const centrelineLength = () =>
    page.evaluate(() => window.__editor.state.journey.site.centreline.length);
  const pressed = (sel) => page.getAttribute(sel, 'aria-pressed');

  const waitForToast = (pattern, timeout = 15_000) => page.waitForFunction(
    (source) => window.__toasts.some((t) => new RegExp(source).test(t.text)),
    pattern.source, { timeout });

  test('booting raises no uncaught page error', () => {
    assert.deepEqual(pageErrors, [], `the editor threw during boot:\n${pageErrors.join('\n')}`);
  });

  test('booting logs no console error', () => {
    assert.deepEqual(consoleErrors, [],
      `the editor logged errors during boot:\n${consoleErrors.join('\n')}`);
  });

  test('booting raises no error toast', async () => {
    const bad = (await toasts()).filter((t) => t.error);
    assert.deepEqual(bad, [], `the editor reported failures:\n${bad.map((t) => t.text).join('\n')}`);
  });

  test('the scan loads and the editor reports how many splats it has', async (t) => {
    if (!SCAN_PRESENT) {
      return t.skip(`no capture in data/splats for "${SCAN_BASE}" — nothing to load`);
    }
    const stats = await page.evaluate(() => {
      const s = window.__editor.stage.splatStats;
      return s ? { count: s.count, loadMs: s.loadMs } : null;
    });
    assert.ok(stats, 'the stage never finished loading a scan');
    assert.ok(stats.count > 1_000_000, `expected a full capture, got ${stats.count} splats`);
    assert.ok(stats.loadMs > 0, 'the load was timed, so it genuinely happened');
    await waitForToast(/splats/);
  });

  test('the site loads with its beats and its walking path', async () => {
    const loaded = await page.evaluate(() => ({
      slug: window.__editor.state.slug,
      beats: window.__editor.state.journey.beats.length,
      points: window.__editor.state.journey.site.centreline.length,
    }));
    assert.equal(loaded.slug, SLUG);
    assert.equal(loaded.beats, 2);
    assert.equal(loaded.points, 3);
  });

  test('an fps readout appears and reports a plausible rate', async () => {
    await page.waitForFunction(() => /\d/.test(document.getElementById('fps').textContent),
      undefined, { timeout: 20_000 });
    const text = await page.textContent('#fps');
    const fps = Number(/([\d.]+) fps/.exec(text)?.[1]);
    // Deliberately wide. Under software WebGL a five-million-splat scan runs at single digits,
    // and this test is about the readout being wired up, not about the machine it runs on.
    assert.ok(fps > 0 && fps < 1000, `implausible frame rate: ${text}`);
  });

  test('each layer toggle flips both the button and the scene', async () => {
    // The scene-side property each button is ultimately responsible for. `phone` renders in a
    // separate view with nothing on the stage to read, so it is checked through state alone.
    const probes = {
      'btn-beats': ['beats', () => window.__editor.stage.gizmos.visible],
      'btn-path': ['path', () => window.__editor.stage.axisGroup.visible],
      'btn-trim': ['trim', () => true],
      'btn-splat': ['scan', () => window.__editor.stage.splat?.visible ?? true],
      'btn-phone': ['phone', () => true],
    };

    for (const [id, [key, probe]] of Object.entries(probes)) {
      const was = (await layers())[key];
      await page.click(`#${id}`);
      await page.waitForFunction(
        ([k, before]) => window.__editor.state.layers[k] !== before, [key, was]);

      assert.equal((await layers())[key], !was, `${id} did not toggle the "${key}" layer`);
      assert.equal(await pressed(`#${id}`), String(!was), `${id} did not update aria-pressed`);
      assert.equal(await page.evaluate(probe), !was, `${id} did not reach the scene`);

      await page.click(`#${id}`);
      await page.waitForFunction(
        ([k, restored]) => window.__editor.state.layers[k] === restored, [key, was]);
    }
  });

  test('adding a path point and undoing it restores the original path', async () => {
    if ((await layers()).path !== true) await page.click('#btn-path');
    const before = await centrelineLength();

    await page.click('#path-add');
    await page.waitForFunction((n) =>
      window.__editor.state.journey.site.centreline.length === n, before + 1);

    // Cmd on macOS, Ctrl elsewhere — the editor accepts either, but the suite should press
    // what the artist presses.
    const mod = process.platform === 'darwin' ? 'Meta' : 'Control';
    await page.keyboard.press(`${mod}+z`);
    await page.waitForFunction((n) =>
      window.__editor.state.journey.site.centreline.length === n, before, { timeout: 10_000 });
    assert.equal(await centrelineLength(), before, 'undo did not restore the path');

    await page.keyboard.press(`Shift+${mod}+z`);
    await page.waitForFunction((n) =>
      window.__editor.state.journey.site.centreline.length === n, before + 1, { timeout: 10_000 });
    assert.equal(await centrelineLength(), before + 1, 'redo did not put the point back');

    await page.keyboard.press(`${mod}+z`);
    await page.waitForFunction((n) =>
      window.__editor.state.journey.site.centreline.length === n, before, { timeout: 10_000 });
  });

  test('the history note says how many steps are available', async () => {
    const note = await page.textContent('#history-note');
    assert.match(note, /\d+ steps? to undo/);
  });

  test('the save button is never disabled, and saving says so', async () => {
    assert.equal(await page.isDisabled('#btn-save'), false,
      'an explicit save is also how you confirm what is on disk, so it is always available');
    await page.click('#btn-save');
    await waitForToast(/Draft saved|Saved with \d+ warning/);

    const failed = (await toasts()).filter((t) => t.error && /saved/i.test(t.text));
    assert.deepEqual(failed, [], `saving reported a failure:\n${failed.map((t) => t.text).join('\n')}`);
  });

  test('the draft the editor saved is the draft the service serves back', async () => {
    const draft = await (await fetch(`${base}/api/sites/${SLUG}/draft`)).json();
    assert.equal(draft.site.slug, SLUG);
    assert.equal(draft.site.centreline.length, await centrelineLength());
  });

  test('the controller page loads and connects its socket', async () => {
    const control = await context.newPage();
    const errors = [];
    control.on('pageerror', (err) => errors.push(err.stack ?? String(err)));
    control.on('console', (msg) => {
      if (msg.type() === 'error' && !/favicon/.test(msg.text())) errors.push(msg.text());
    });

    await control.goto(`${base}/control`, { waitUntil: 'domcontentloaded' });
    // The pill is set from the socket's own open event, so this is a live connection to the
    // control bus rather than a page that merely rendered.
    await control.waitForFunction(
      () => document.getElementById('conn')?.textContent === 'connected', undefined,
      { timeout: 20_000 });
    assert.match(await control.getAttribute('#conn', 'class'), /live/);

    assert.deepEqual(errors, [], `the controller failed:\n${errors.join('\n')}`);
    await control.close();
  });

  test('the controller lists the beats it can fire', async () => {
    // Published first, because that is what the controller reads: it only falls back to the
    // draft if the published fetch throws, which a 404 does not.
    const res = await fetch(`${base}/api/sites/${SLUG}/publish`, { method: 'POST' });
    assert.equal(res.status, 200);

    const control = await context.newPage();
    await control.goto(`${base}/control`, { waitUntil: 'domcontentloaded' });
    await control.waitForFunction(
      () => document.querySelectorAll('#ctl-beats .ctl-beat').length > 0, undefined,
      { timeout: 20_000 });
    const count = await control.evaluate(
      () => document.querySelectorAll('#ctl-beats .ctl-beat').length);
    assert.equal(count, 2, 'every beat in the journey is reachable by hand');
    await control.close();
  });
});
