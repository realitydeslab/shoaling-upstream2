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

import { makeDataRoot, makeJourney, SLUG, until } from './helpers.ts';

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');

const HOW_TO_ENABLE = 'playwright is not available — run `npm i -D playwright && npx playwright '
  + 'install chromium` from the repository root to enable the browser suite (see test/README.md)';

let browser = null;
let unavailable = null;
try {
  const { chromium } = await import('playwright');
  browser = await chromium.launch({
    args: [
      // spark.js needs WebGL2 and there is no CPU path through this editor at all. These let
      // Chromium fall back to software rendering where there is no usable GPU — slow, which is
      // what the generous timeouts below are for, rather than a blank canvas and no explanation.
      '--enable-unsafe-swiftshader',
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

/**
 * A console error worth failing over, with the URL that produced it, or null.
 *
 * Neither page links a favicon, and Chromium logs the resulting 404 as a console error whose
 * *text* says only "Failed to load resource" — the URL is in the location. So the filter has to
 * read the location, and the message has to carry it, or a genuinely missing module reads as
 * an unattributed line in the failure output.
 */
function noteConsoleError(msg) {
  if (msg.type() !== 'error') return null;
  const url = msg.location()?.url ?? '';
  if (/favicon\.ico/.test(url)) return null;
  return url ? `${msg.text()} — ${url}` : msg.text();
}

/**
 * Record every toast the page raises.
 *
 * Toasts fade after a couple of seconds, so polling for one is a race the suite would lose on
 * a slow machine — and an error toast that appeared during boot is exactly the thing worth
 * failing on. One entry per appearance: the observer fires several times per toast, so a
 * record is only kept when the banner becomes visible or when its wording changes.
 */
const WATCH_TOASTS = () => {
  window.__toasts = [];
  const watch = () => {
    const el = document.getElementById('toast');
    if (!el) return;                       // the controller page has no toast of its own
    let shown = false;
    let last = null;
    const record = () => {
      const showing = el.classList.contains('show');
      const text = el.textContent;
      if (showing && (!shown || text !== last)) {
        window.__toasts.push({ text, error: el.classList.contains('error') });
        last = text;
      }
      shown = showing;
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

    server = spawn(process.execPath, [path.join(ROOT, 'service', 'src', 'server.ts')], {
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
      const noted = noteConsoleError(msg);
      if (noted) consoleErrors.push(noted);
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

  const toastCount = () => page.evaluate(() => window.__toasts.length);
  // `from` keeps an assertion honest when the expected wording has been seen before: a save
  // that silently did nothing would otherwise pass on the previous save's toast.
  const waitForToast = (pattern, { from = 0, timeout = 15_000 } = {}) => page.waitForFunction(
    ({ source, since }) => window.__toasts.slice(since).some((t) => new RegExp(source).test(t.text)),
    { source: pattern.source, since: from }, { timeout });

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
    const loaded = await page.evaluate(() => {
      const s = window.__editor.stage.splatStats;
      // splatStats is only assigned once SplatMesh has called back, so its existence — not the
      // count, which comes from the stamped bounds — is what says the scan actually arrived.
      return { stats: s ? { count: s.count } : null, inScene: !!window.__editor.stage.splat };
    });
    assert.ok(loaded.stats, 'the stage never finished loading a scan');
    assert.ok(loaded.inScene, 'the scan loaded but was never added to the scene');
    assert.ok(loaded.stats.count > 1_000_000,
      `expected a full capture, got ${loaded.stats.count} splats`);
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

  test('the beats, scan and phone toggles reach the scene', async () => {
    // The scene-side property each button is ultimately responsible for. The phone renders
    // into its own bezel with nothing on the stage to read, so it is checked through state.
    const probes = [
      ['btn-beats', 'beats', () => window.__editor.stage.gizmos.visible],
      // null when there is no scan to hide, which is a valid state on a machine without the
      // captures rather than a failure.
      ['btn-splat', 'scan', () => window.__editor.stage.splat?.visible ?? null],
      ['btn-phone', 'phone', null],
    ];

    for (const [id, key, probe] of probes) {
      const was = (await layers())[key];
      await page.click(`#${id}`);
      await page.waitForFunction(
        ([k, before]) => window.__editor.state.layers[k] !== before, [key, was]);

      assert.equal((await layers())[key], !was, `${id} did not toggle the "${key}" layer`);
      assert.equal(await pressed(`#${id}`), String(!was), `${id} did not update aria-pressed`);
      const seen = probe ? await page.evaluate(probe) : null;
      if (seen !== null) assert.equal(seen, !was, `${id} did not reach the scene`);

      await page.click(`#${id}`);
      await page.waitForFunction(
        ([k, restored]) => window.__editor.state.layers[k] === restored, [key, was]);
    }
  });

  test('the path and trim toggles bring their panels with them', async () => {
    // Both panels are closed with their own Done button rather than by pressing the toolbar
    // toggle again: an open panel sits over that end of the toolbar, so the toggle is not
    // actually reachable while its panel is up.
    for (const [id, key, panel, done] of [
      ['btn-path', 'path', '#path-panel', '#path-close'],
      ['btn-trim', 'trim', '#trim-panel', '#trim-close'],
    ]) {
      if ((await layers())[key]) await page.click(done);

      await page.click(`#${id}`);
      await page.waitForSelector(`${panel}:visible`, { timeout: 5000 });
      assert.equal((await layers())[key], true, `${id} did not switch the "${key}" layer on`);
      assert.equal(await pressed(`#${id}`), 'true');

      await page.click(done);
      await page.waitForSelector(panel, { state: 'hidden', timeout: 5000 });
      assert.equal((await layers())[key], false, `${done} did not switch the "${key}" layer off`);
      assert.equal(await pressed(`#${id}`), 'false');
    }
  });

  test('adding a path point and undoing it restores the original path', async () => {
    if (!(await layers()).path) await page.click('#btn-path');
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
    if (!(await layers()).path) await page.click('#btn-path');
    // Asserted after an edit of its own rather than after the test above, so this reads the
    // note the editor writes for a known change instead of whatever history happens to be left.
    await page.click('#path-add');
    await page.waitForFunction(
      () => /\d+ steps? to undo/.test(document.getElementById('history-note').textContent),
      undefined, { timeout: 5000 });

    const mod = process.platform === 'darwin' ? 'Meta' : 'Control';
    await page.keyboard.press(`${mod}+z`);
    await page.click('#path-close');
  });

  test('the save button is never disabled, and saving says so', async () => {
    assert.equal(await page.isDisabled('#btn-save'), false,
      'an explicit save is also how you confirm what is on disk, so it is always available');
    const from = await toastCount();
    await page.click('#btn-save');
    await waitForToast(/Draft saved|Saved with \d+ warning/, { from });

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
      const noted = noteConsoleError(msg);
      if (noted) errors.push(noted);
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
