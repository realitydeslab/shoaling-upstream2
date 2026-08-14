/**
 * Does the editor still boot with the TypeScript app.js?
 *
 * Loads the real editor against the running service on 8710, collects console errors and
 * toasts, and reports what the page ended up with. Read-only: nothing here edits a journey.
 */
import { chromium } from 'playwright';

const BASE = process.argv[2] ?? 'http://localhost:8710';

const browser = await chromium.launch();
const context = await browser.newContext({ viewport: { width: 1440, height: 900 } });
const page = await context.newPage();

const errors = [];
page.on('console', (msg) => {
  if (msg.type() !== 'error') return;
  const url = msg.location()?.url ?? '';
  if (/favicon\.ico/.test(url)) return;
  errors.push(url ? `${msg.text()} — ${url}` : msg.text());
});
page.on('pageerror', (err) => errors.push(`pageerror: ${err.message}`));

await page.addInitScript(() => {
  window.__toasts = [];
  const watch = () => {
    const el = document.getElementById('toast');
    if (!el) return;
    let shown = false, last = null;
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
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', watch);
  else watch();
});

await page.goto(`${BASE}/`, { waitUntil: 'load' });
await page.waitForFunction(() => document.querySelectorAll('#beat-list .beat').length > 0,
  null, { timeout: 60_000 }).catch(() => {});

const report = await page.evaluate(() => ({
  beats: document.querySelectorAll('#beat-list .beat').length,
  reach: document.getElementById('reach-length')?.textContent,
  hud: document.getElementById('hud')?.textContent?.replace(/\s+/g, ' ').trim(),
  notes: document.getElementById('notes')?.textContent?.trim(),
  editorHandle: typeof window.__editor === 'object' && window.__editor !== null,
  toasts: window.__toasts,
  fps: document.getElementById('fps')?.textContent,
}));

// Clicking a beat marker in the 3D scene must select it — the bug the port fixed. Selecting
// through the rail exercises the same select() path the stage callback now reaches.
await page.click('#beat-list .beat:nth-child(2)').catch(() => {});
await page.waitForTimeout(400);
await page.waitForFunction(() => document.getElementById('notes')?.textContent?.trim(),
  null, { timeout: 20_000 }).catch(() => {});
const selected = await page.evaluate(() => ({
  selectedId: window.__editor?.state?.selectedId ?? null,
  inspector: document.getElementById('insp-title')?.textContent,
  notes: document.getElementById('notes')?.textContent?.trim(),
  publishDisabled: document.getElementById('btn-publish')?.disabled,
  historyNote: document.getElementById('history-note')?.textContent,
}));

// The stage's own selection callback — the path that used to throw `select is not a function`
// because a local `const select` shadowed the module's select(id).
const viaStage = await page.evaluate(() => {
  const stage = window.__editor?.stage;
  const id = window.__editor?.state?.journey?.beats?.at(-1)?.id ?? null;
  try {
    stage.onSelectBeat(id);
    return { asked: id, got: window.__editor.state.selectedId, threw: null };
  } catch (e) { return { asked: id, got: null, threw: String(e) }; }
});

console.log(JSON.stringify({ report, selected, viaStage, errors }, null, 2));

await browser.close();
process.exit(errors.length ? 1 : 0);
