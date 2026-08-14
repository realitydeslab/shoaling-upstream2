import { chromium } from 'playwright';
async function run(label, layers) {
  const b = await chromium.launch();
  const c = await b.newContext({ viewport: { width: 1440, height: 900 } });
  const p = await c.newPage();
  await p.addInitScript((l) => localStorage.setItem('layers', JSON.stringify(l)), layers);
  await p.goto('http://localhost:8710/', { waitUntil: 'domcontentloaded' });
  await p.waitForFunction(() => window.__editor?.stage, null, { timeout: 90000 });
  await p.waitForTimeout(14000);
  const r = await p.evaluate(async () => {
    const g = []; let last = performance.now();
    await new Promise((res) => { let i = 0; const t = () => { const n = performance.now();
      g.push(Math.round(n - last)); last = n; if (++i < 30) requestAnimationFrame(t); else res(); };
      requestAnimationFrame(t); });
    const s = [...g].sort((a, b2) => a - b2);
    const st = window.__editor.stage;
    return { median: s[15], active: st.spark.activeSplats,
             attached: st.trimEdit?.parent === st.splat };
  }, { timeout: 120000 });
  console.log(`${label.padEnd(30)} ${String(r.median).padStart(5)} ms  ${(1000/r.median).toFixed(1).padStart(5)} fps  active=${r.active}  editAttached=${r.attached}`);
  await b.close();
}
const BASE = { beats: true, path: false, trim: false, scan: true, phone: false };
await run('trim OFF (edit detached)', BASE);
await run('trim ON  (edit attached)', { ...BASE, trim: true });
await run('trim OFF + phone ON',      { ...BASE, phone: true });
