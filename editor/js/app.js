/**
 * Editor wiring: load a site, render it, edit beats, publish.
 */

import { Stage } from './scene.js';
import { Scrubber } from './scrubber.js';
import { Audition } from './audition.js';
import { centrelineLength, projectToCentreline, pointAtS } from './geom.js';

const $ = (sel) => document.querySelector(sel);

const state = {
  sites: [],
  slug: null,
  journey: null,
  selectedId: null,
  interactions: {},
  dirty: false,
  quality: localStorage.getItem('quality') ?? 'full',
  scans: {},
  audio: null,          // clipId -> packaged file
  playing: false,
  speed: 0.7,           // m/s — an unhurried creek pace
  fired: new Set(),
};

let stage, scrubber, audition;

// ------------------------------------------------------------------ api

async function api(path, options = {}) {
  const res = await fetch(path, {
    headers: { 'content-type': 'application/json' },
    ...options,
    body: options.body ? JSON.stringify(options.body) : undefined,
  });
  const text = await res.text();
  const data = text ? JSON.parse(text) : null;
  if (!res.ok) {
    const err = new Error(data?.error ?? `HTTP ${res.status}`);
    err.detail = data;
    throw err;
  }
  return data;
}

function toast(message, isError = false) {
  const el = $('#toast');
  el.textContent = message;
  el.classList.toggle('error', isError);
  el.classList.add('show');
  clearTimeout(toast.timer);
  toast.timer = setTimeout(() => el.classList.remove('show'), isError ? 6000 : 2600);
}

// ------------------------------------------------------------------ load

async function boot() {
  state.interactions = await api('/api/interactions');
  state.sites = await api('/api/sites');
  state.scans = await api('/api/scans').catch(() => ({}));
  state.audio = await api('/api/audio').catch(() => ({ clips: [] }));

  const select = $('#site-select');
  select.innerHTML = state.sites
    .map((s) => `<option value="${s.slug}">${s.title}</option>`)
    .join('');
  select.addEventListener('change', () => loadSite(select.value));

  stage = new Stage($('#stage'), {
    onPick: handlePick,
    onTrimChanged: (box, opts) => {
      const trim = ensureTrim();
      Object.assign(trim, box, { enabled: trim.enabled });
      renderTrimReadout(trim);
      // `live` fires continuously through a drag; the drag ending is the moment worth writing.
      if (!opts?.live) persistTrim();
    },

    // The walking path. Independent of the beats: moving the route never moves a beat, it
    // only changes how far along the route each one sits.
    onPathChanged: (points, opts) => {
      state.journey.site.centreline = points;
      for (const beat of state.journey.beats) {
        beat.s = +projectToCentreline(beat.position, points).s.toFixed(2);
      }
      scrubber.setJourney(state.journey);
      renderPathReadout();
      if (!opts?.live) { renderRail(); persistPath(); }
    },
    onPathSelect: () => renderPathReadout(),

    // A point of interest, dragged freely in space.
    onBeatMoved: (moved, opts) => {
      if (!moved) return;
      const beat = state.journey.beats.find((b) => b.id === moved.id);
      if (!beat) return;
      beat.position = moved.position;
      const cl = state.journey.site.centreline;
      if (cl?.length >= 2) beat.s = +projectToCentreline(beat.position, cl).s.toFixed(2);
      scrubber.setJourney(state.journey);
      // Mark dirty before saving: save() early-returns on a clean document, so without this
      // the drag is applied in memory and silently never written.
      state.dirty = true;
      if (!opts?.live) {
        renderRail();
        renderInspector();
        updateChrome();
        save();          // a moved beat is a complete, valid edit — keep it
      }
    },
  });
  // Handy from the browser console when a scan will not show up, which is the failure this
  // editor is most likely to hit on a new machine or a new spark.js release.
  window.__editor = { stage, state, get scrubber() { return scrubber; },
    get audition() { return audition; }, setWalker: (v) => setWalker(v) };
  stage.onWalk((delta) => {
    const total = journeyLength();
    setWalker(Math.max(0, Math.min(total, scrubber.s + delta)));
  });

  scrubber = new Scrubber($('#scrub-canvas'), { onScrub: (s) => setWalker(s, false) });
  audition = new Audition({ onState: renderAudioNote });
  audition.setCatalogue(state.audio);

  bindToolbar();
  $('#q-full').setAttribute('aria-pressed', String(state.quality === 'full'));
  $('#q-fast').setAttribute('aria-pressed', String(state.quality === 'fast'));
  $('#t-speed-label').textContent = `${state.speed.toFixed(1)} m/s`;
  renderAudioNote();

  if (state.sites.length) await loadSite(state.sites[0].slug);
  else toast('No sites found. Run: node tools/seed-journeys.mjs', true);
}

function journeyLength() {
  const cl = state.journey?.site?.centreline;
  return cl && cl.length >= 2 ? centrelineLength(cl) : 0;
}

async function loadSite(slug) {
  state.slug = slug;
  state.selectedId = null;
  state.dirty = false;

  const loading = $('#loading');
  loading.hidden = false;
  $('#loading-detail').textContent = slug;

  state.journey = await api(`/api/sites/${slug}/draft`);

  stage.setJourney(state.journey);
  scrubber.setJourney(state.journey);
  renderRail();
  renderInspector();
  updateChrome();

  await loadScan();

  await audition.load(state.journey, clipUrl);
  stage.frame();
  setWalker(0);
  loading.hidden = true;
  await validate();
}

async function loadScan() {
  const options = state.scans[state.slug] ?? [];
  if (!options.length) return;

  // Best available, unless the operator asked for the fast proxy. The server lists what
  // actually exists, so a site without a prebuilt .rad quietly falls back.
  const choice = state.quality === 'fast'
    ? (options.find((o) => o.kind === 'proxy') ?? options.at(-1))
    : options[0];

  $('#loading').hidden = false;
  $('#loading-detail').textContent = `${choice.url.split('/').pop()} — ${choice.label}`;
  stage.setBounds(state.journey?.editorFrame?.bounds ?? null);
  stage.setTrim(state.journey?.editorFrame?.trim ?? null);

  try {
    const stats = await stage.loadSplat(choice.url, (phase) => {
      $('#loading-detail').textContent = `${choice.url.split('/').pop()} — ${phase}`;
    }, { paged: choice.paged });
    if (stats) {
      toast(`${stats.count.toLocaleString()} splats · ${choice.label} · `
          + `${(stats.loadMs / 1000).toFixed(1)} s`);
    }
  } catch (err) {
    toast(`Scan failed to load: ${err.message}`, true);
  }
  $('#loading').hidden = true;
}

async function setQuality(quality) {
  if (state.quality === quality) return;
  state.quality = quality;
  localStorage.setItem('quality', quality);
  $('#q-full').setAttribute('aria-pressed', String(quality === 'full'));
  $('#q-fast').setAttribute('aria-pressed', String(quality === 'fast'));
  await loadScan();
}

// ------------------------------------------------------------------ audio

/**
 * Resolve a clip id to a packaged file. Layered ids carry a "--far" style suffix; the
 * catalogue lists the base clip, so strip the suffix before looking it up.
 */
function clipUrl(clipId) {
  if (!clipId) return null;
  const base = clipId.replace(/--(far|mid|intimate)$/, '');
  const entry = (state.audio?.clips ?? []).find((c) => c.clipId === base);
  if (!entry || entry.missing) return null;
  return `/audio/${encodeURIComponent(clipId)}.mp3`;
}

function renderAudioNote() {
  const st = audition?.status();
  const btn = $('#t-audio');
  const note = $('#t-audio-note');
  if (!st?.ready) {
    btn.textContent = '🔇';
    btn.setAttribute('aria-pressed', 'false');
    note.textContent = 'sound off';
    note.className = '';
    return;
  }
  btn.textContent = audition.muted ? '🔇' : '🔊';
  btn.setAttribute('aria-pressed', String(!audition.muted));
  // Say plainly what this is. Levels set here will be wrong at the creek, which is already
  // making water noise of its own.
  note.textContent = st.missing.length
    ? `${st.sources} sources · ${st.missing.length} clip(s) missing`
    : `${st.sources} sources · Resonance, not PHASE`;
  note.className = st.missing.length ? 'warn' : '';
}

// ------------------------------------------------------------------ transport

function setPlaying(playing) {
  state.playing = playing;
  $('#t-play').textContent = playing ? '❚❚' : '▶';
  $('#t-play').setAttribute('aria-pressed', String(playing));
  if (playing) {
    state.lastTick = performance.now();
    requestAnimationFrame(tick);
  }
}

function tick(now) {
  if (!state.playing) return;
  const dt = Math.min(0.1, (now - state.lastTick) / 1000);
  state.lastTick = now;

  const total = journeyLength();
  let s = scrubber.s + state.speed * dt;
  if (s >= total) { s = total; setPlaying(false); }
  setWalker(s);

  // Fire each beat's completion sound once, as the walker crosses its centre — the discrete
  // confirmation, as distinct from the continuous field.
  for (const beat of state.journey?.beats ?? []) {
    if (state.fired.has(beat.id)) continue;
    if (Math.abs(s - beat.s) <= (beat.trigger?.enterRadiusM ?? 2)) {
      state.fired.add(beat.id);
      audition.fireCompletion(beat);
    }
  }

  if (state.playing) requestAnimationFrame(tick);
}

function rewind() {
  state.fired.clear();
  setWalker(0);
}

// ------------------------------------------------------------------ walker

function setWalker(s, syncScrubber = true) {
  stage.setWalker(s);
  if (syncScrubber) scrubber.setS(s);
  else scrubber.s = s;

  const { armed, winner } = scrubber.evaluate();
  $('#scrub-readout').textContent = `s = ${s.toFixed(1)} m`;
  $('#scrub-armed').textContent = armed.length
    ? `${armed.length} armed · ${winner ? `“${winner.beat.title}” wins` : 'none firing'}`
    : 'nothing armed';

  const armedIds = new Set(armed.filter((a) => a.inEnter).map((a) => a.beat.id));
  document.querySelectorAll('.beat').forEach((el) => {
    const id = el.dataset.id;
    el.classList.toggle('armed', armedIds.has(id));
    el.classList.toggle('firing', winner?.beat.id === id);
  });

  updateHud(s, armed, winner);

  // Move the listener with the walker so the mix follows the simulated visitor.
  if (audition?.enabled) {
    const cl = state.journey?.site?.centreline;
    if (cl?.length >= 2) {
      const here = pointAtS(s, cl);
      const ahead = pointAtS(Math.min(journeyLength(), s + 2), cl);
      const dx = ahead.x - here.x, dy = ahead.y - here.y, dz = ahead.z - here.z;
      const len = Math.hypot(dx, dy, dz) || 1;
      audition.update({ x: here.x, y: here.y + 1.55, z: here.z },
                      { x: dx / len, y: dy / len, z: dz / len });
    }
  }
}

function updateHud(s, armed, winner) {
  const total = journeyLength();
  const stats = stage.splatStats;
  const lines = [
    `<span class="k">reach   </span><b>${total.toFixed(1)} m</b>   <span class="k">at</span> <b>${s.toFixed(1)} m</b>`,
    `<span class="k">armed   </span><b>${armed.length}</b>   <span class="k">firing</span> <b>${winner ? winner.beat.title : '—'}</b>`,
  ];
  if (stats) {
    lines.push(`<span class="k">scan    </span>${stats.count.toLocaleString()} splats · ${stats.span.x.toFixed(0)}×${stats.span.y.toFixed(0)}×${stats.span.z.toFixed(0)} m`);
  }
  lines.push(`<span class="k">view    </span>${stage.mode === 'god' ? 'God — drag to orbit' : 'User — W/S to walk'}`);
  $('#hud').innerHTML = lines.join('\n');
}

// ------------------------------------------------------------------ rail

function renderRail() {
  const list = $('#beat-list');
  const beats = state.journey?.beats ?? [];

  list.innerHTML = beats.map((beat, i) => {
    const kind = state.interactions[beat.interaction]?.label ?? beat.interaction;
    return `
      <li class="beat" data-id="${beat.id}" role="option" draggable="true"
          aria-selected="${beat.id === state.selectedId}">
        <span class="beat-index">${String(i + 1).padStart(2, '0')}</span>
        <span class="beat-name">${escapeHtml(beat.title)}</span>
        <span class="beat-s">${(beat.s ?? 0).toFixed(1)} m</span>
        <span class="beat-meta">${escapeHtml(kind)} · gate ${beat.trigger?.enterRadiusM ?? '?'}/${beat.trigger?.exitRadiusM ?? '?'} m</span>
      </li>`;
  }).join('');

  list.querySelectorAll('.beat').forEach((el) => {
    el.addEventListener('click', () => select(el.dataset.id));

    // Drag to reorder. Order is what the runtime gates on when a beat requires its
    // predecessor, so it is content, not presentation — and dragging is how anyone expects
    // to change the order of a list.
    el.addEventListener('dragstart', (ev) => {
      state.dragId = el.dataset.id;
      el.classList.add('dragging');
      ev.dataTransfer.effectAllowed = 'move';
      // Firefox refuses to start a drag without data set.
      ev.dataTransfer.setData('text/plain', el.dataset.id);
    });
    el.addEventListener('dragend', () => {
      state.dragId = null;
      list.querySelectorAll('.beat').forEach((n) => n.classList.remove('dragging', 'drop-before', 'drop-after'));
    });
    el.addEventListener('dragover', (ev) => {
      if (!state.dragId || state.dragId === el.dataset.id) return;
      ev.preventDefault();
      const box = el.getBoundingClientRect();
      const after = ev.clientY > box.top + box.height / 2;
      el.classList.toggle('drop-after', after);
      el.classList.toggle('drop-before', !after);
    });
    el.addEventListener('dragleave', () => {
      el.classList.remove('drop-before', 'drop-after');
    });
    el.addEventListener('drop', (ev) => {
      ev.preventDefault();
      if (!state.dragId || state.dragId === el.dataset.id) return;
      const box = el.getBoundingClientRect();
      const after = ev.clientY > box.top + box.height / 2;
      reorderBeat(state.dragId, el.dataset.id, after);
    });
  });

  $('#ambient-list').innerHTML = (state.journey?.ambient ?? []).map((a) => `
    <li style="padding:5px 6px;display:flex;justify-content:space-between;gap:8px">
      <span style="font-size:12.5px">${escapeHtml(a.title ?? a.id)}</span>
      <span class="beat-s">${a.audibleRadiusM} m</span>
    </li>`).join('') || '<li class="hint" style="color:var(--muted);font-size:11.5px">none</li>';

  $('#reach-length').textContent = `${journeyLength().toFixed(1)} m`;
}

function select(id) {
  state.selectedId = id;
  stage.setSelected(id);
  scrubber.setSelected(id);
  renderRail();
  renderInspector();

  const beat = currentBeat();
  if (beat) setWalker(beat.s ?? 0);
}

function currentBeat() {
  return state.journey?.beats.find((b) => b.id === state.selectedId) ?? null;
}

// ------------------------------------------------------------------ inspector

function renderInspector() {
  const beat = currentBeat();
  const body = $('#insp-body');

  if (!beat) {
    $('#insp-title').textContent = 'Nothing selected';
    body.innerHTML = '<p class="empty">Select a beat on the left, or press <b>+ Beat</b> to add one.</p>';
    return;
  }

  $('#insp-title').textContent = `Beat · ${beat.id}`;
  const options = Object.entries(state.interactions)
    .map(([key, meta]) => `<option value="${key}" ${key === beat.interaction ? 'selected' : ''}>${meta.label}</option>`)
    .join('');

  body.innerHTML = `
    <div class="row">
      <label for="f-title">Title</label>
      <input id="f-title" type="text" value="${escapeAttr(beat.title)}">
    </div>

    <div class="row">
      <label for="f-prompt">Prompt</label>
      <textarea id="f-prompt" rows="2">${escapeHtml(beat.prompt ?? '')}</textarea>
      <span class="hint">What the visitor is invited to do. Never name the gesture: say “get low enough to see into the gravel”, not “crouch”.</span>
    </div>

    <div class="row">
      <label for="f-interaction">Interaction</label>
      <select id="f-interaction">${options}</select>
      <span class="hint">${escapeHtml(state.interactions[beat.interaction]?.hint ?? '')}</span>
    </div>

    <div class="row">
      <label>Position — metres in the anchor frame</label>
      <div class="triple">
        <input id="f-x" type="number" step="0.1" value="${beat.position.x}">
        <input id="f-y" type="number" step="0.1" value="${beat.position.y}">
        <input id="f-z" type="number" step="0.1" value="${beat.position.z}">
      </div>
      <div class="triple">
        <span class="axis-label">X</span><span class="axis-label">Y up</span><span class="axis-label">Z</span>
      </div>
    </div>

    <div class="row">
      <label for="f-s">Along the creek — s</label>
      <input id="f-s" type="number" step="0.1" value="${beat.s}">
      <span class="hint">This is what the runtime gates on. Use <b>Place</b> and click the scan to set position and s together.</span>
    </div>

    <div class="row">
      <label>Gate — enter / exit metres</label>
      <div class="triple" style="grid-template-columns:1fr 1fr">
        <input id="f-enter" type="number" step="0.1" value="${beat.trigger.enterRadiusM}">
        <input id="f-exit" type="number" step="0.1" value="${beat.trigger.exitRadiusM}">
      </div>
      <span class="hint">Exit must exceed enter. The gap is hysteresis — without it the beat fires and silences repeatedly when someone stands near the edge.</span>
    </div>

    <div class="row">
      <label>Dwell / minimum hold — seconds</label>
      <div class="triple" style="grid-template-columns:1fr 1fr">
        <input id="f-dwell" type="number" step="0.1" value="${beat.trigger.dwellSeconds}">
        <input id="f-hold" type="number" step="1" value="${beat.trigger.minimumHoldSeconds}">
      </div>
      <span class="hint">Minimum hold, not hysteresis, is what stops thrash at this spacing. It turns “am I in the zone” into “which beat am I performing”.</span>
    </div>

    ${beat.interaction === 'give' ? `
    <div class="row">
      <label for="f-gives">Fish given</label>
      <input id="f-gives" type="number" step="1" value="${beat.givesFish ?? 0}">
      <span class="hint">The shoal is audibly thinner afterwards — fewer voices, narrower unison. Heard, never read.</span>
    </div>` : ''}

    <div class="row">
      <label>Audio layers</label>
      <span class="hint">Distance is carried by content, not gain: at 5–20 m the whole level budget is about 12 dB, which reads as “slightly louder” rather than arrival. Three recordings, not three volumes.</span>
      ${['far', 'mid', 'intimate'].map((layer) => `
        <div style="display:grid;grid-template-columns:56px 1fr 64px;gap:6px;align-items:center;margin-top:5px">
          <span class="axis-label" style="text-align:left">${layer}</span>
          <input id="f-clip-${layer}" type="text" value="${escapeAttr(beat.audio?.[layer]?.clipId ?? '')}" placeholder="clip id">
          <input id="f-gain-${layer}" type="number" step="0.5" value="${beat.audio?.[layer]?.gainDb ?? ''}" placeholder="dB">
        </div>`).join('')}
      <div style="display:grid;grid-template-columns:56px 1fr 64px;gap:6px;align-items:center;margin-top:8px">
        <span class="axis-label" style="text-align:left">done</span>
        <input id="f-clip-completion" type="text" value="${escapeAttr(beat.audio?.completion?.clipId ?? '')}" placeholder="completion clip">
        <input id="f-gain-completion" type="number" step="0.5" value="${beat.audio?.completion?.gainDb ?? ''}" placeholder="dB">
      </div>
    </div>

    <div style="display:flex;gap:6px;margin-top:4px">
      <button id="btn-up" ${state.journey.beats[0] === beat ? 'disabled' : ''}>↑ Earlier</button>
      <button id="btn-down" ${state.journey.beats.at(-1) === beat ? 'disabled' : ''}>↓ Later</button>
      <button id="btn-delete" class="ghost" style="margin-left:auto;color:var(--alarm)">Delete</button>
    </div>
  `;

  bindInspector(beat);
}

function bindInspector(beat) {
  const on = (id, event, fn) => {
    const el = document.getElementById(id);
    if (el) el.addEventListener(event, fn);
  };
  const num = (id) => Number(document.getElementById(id).value);

  on('f-title', 'input', (e) => { beat.title = e.target.value; touch({ rail: true }); });
  on('f-prompt', 'input', (e) => { beat.prompt = e.target.value; touch(); });
  on('f-interaction', 'change', (e) => {
    beat.interaction = e.target.value;
    if (beat.interaction === 'give' && beat.givesFish === undefined) beat.givesFish = 10;
    touch({ rail: true, inspector: true });
  });

  for (const axis of ['x', 'y', 'z']) {
    on(`f-${axis}`, 'change', () => {
      beat.position = { x: num('f-x'), y: num('f-y'), z: num('f-z') };
      const cl = state.journey.site.centreline;
      if (cl?.length >= 2) beat.s = +projectToCentreline(beat.position, cl).s.toFixed(2);
      touch({ rail: true, scene: true, inspector: true });
    });
  }

  on('f-s', 'change', () => {
    beat.s = num('f-s');
    const cl = state.journey.site.centreline;
    if (cl?.length >= 2) {
      const p = pointAtS(beat.s, cl);
      // Keep the lateral offset the author chose; only slide along the creek.
      const old = projectToCentreline(beat.position, cl);
      const dx = beat.position.x - old.closest.x;
      const dy = beat.position.y - old.closest.y;
      const dz = beat.position.z - old.closest.z;
      beat.position = { x: +(p.x + dx).toFixed(3), y: +(p.y + dy).toFixed(3), z: +(p.z + dz).toFixed(3) };
    }
    touch({ rail: true, scene: true, inspector: true });
  });

  on('f-enter', 'change', () => { beat.trigger.enterRadiusM = num('f-enter'); touch({ rail: true, scene: true }); });
  on('f-exit', 'change', () => { beat.trigger.exitRadiusM = num('f-exit'); touch({ rail: true, scene: true }); });
  on('f-dwell', 'change', () => { beat.trigger.dwellSeconds = num('f-dwell'); touch(); });
  on('f-hold', 'change', () => { beat.trigger.minimumHoldSeconds = num('f-hold'); touch(); });
  on('f-gives', 'change', () => { beat.givesFish = num('f-gives'); touch(); });

  for (const layer of ['far', 'mid', 'intimate', 'completion']) {
    const apply = () => {
      const clip = document.getElementById(`f-clip-${layer}`).value.trim();
      const gain = document.getElementById(`f-gain-${layer}`).value;
      beat.audio ??= {};
      if (!clip) { delete beat.audio[layer]; }
      else {
        beat.audio[layer] = {
          clipId: clip,
          gainDb: gain === '' ? -8 : Number(gain),
          loop: layer !== 'completion',
        };
      }
      touch();
    };
    on(`f-clip-${layer}`, 'change', apply);
    on(`f-gain-${layer}`, 'change', apply);
  }

  on('btn-up', 'click', () => move(beat, -1));
  on('btn-down', 'click', () => move(beat, +1));
  on('btn-delete', 'click', () => {
    if (!confirm(`Delete “${beat.title}”?`)) return;
    state.journey.beats = state.journey.beats.filter((b) => b !== beat);
    state.selectedId = null;
    touch({ rail: true, scene: true, inspector: true });
  });
}

/**
 * Move `dragId` to sit before or after `targetId`.
 *
 * Note this changes narrative order only — it does not move the beat in space, and it does
 * not touch `s`. A beat can legitimately sit downstream of the one before it (the validator
 * warns about that rather than forbidding it), because the journey doubling back may be the
 * intention.
 */
function reorderBeat(dragId, targetId, after) {
  const beats = state.journey.beats;
  const from = beats.findIndex((b) => b.id === dragId);
  const beat = beats[from];
  if (from < 0) return;

  beats.splice(from, 1);
  const to = beats.findIndex((b) => b.id === targetId);
  beats.splice(after ? to + 1 : to, 0, beat);

  state.selectedId = dragId;
  state.dirty = true;
  touch({ rail: true, inspector: true });
  save();
}

function move(beat, dir) {
  const beats = state.journey.beats;
  const i = beats.indexOf(beat);
  const j = i + dir;
  if (j < 0 || j >= beats.length) return;
  [beats[i], beats[j]] = [beats[j], beats[i]];
  state.dirty = true;
  touch({ rail: true, inspector: true });
  save();
}

/** One place that marks the document dirty and refreshes whatever needs it. */
function touch({ rail = false, scene = true, inspector = false } = {}) {
  state.dirty = true;
  if (scene) stage.setJourney(state.journey);
  if (rail) renderRail();
  if (inspector) renderInspector();
  scrubber.setJourney(state.journey);
  setWalker(scrubber.s, true);
  updateChrome();
  scheduleValidate();
}

// ------------------------------------------------------------------ placement

function handlePick({ point, s }) {
  const beat = currentBeat();
  if (!beat) { toast('Select a beat first, then click the scan.', true); return; }
  beat.position = point;
  beat.s = s;
  touch({ rail: true, inspector: true });
  setWalker(s);
}

// ------------------------------------------------------------------ path and trim
//
// Two separate things that happen to share one gizmo:
//
//   PATH  — the route the simulated walker follows. Editing it never moves a beat; it only
//           changes how far along the route each beat sits.
//   PLACE — the points of interest themselves, positioned freely in space.
//   TRIM  — a display box that hides floater splats. Not content at all.
//
// All three are saved on their own so a reload never loses a drag.

/** Persist the walking path, independent of the beats. */
let pathTimer;
function persistPath() {
  clearTimeout(pathTimer);
  pathTimer = setTimeout(async () => {
    if (!state.slug || !state.journey?.site?.centreline) return;
    try {
      const res = await api(`/api/sites/${state.slug}/site`, {
        method: 'PUT',
        body: { centreline: state.journey.site.centreline },
      });
      if (res.beats) state.journey.beats = res.beats;
      renderRail();
    } catch (err) {
      toast(`Path not saved: ${err.message}`, true);
    }
  }, 400);
}

/**
 * Route a walking path past every beat.
 *
 * Three things make this a walk rather than a polyline through the markers:
 *
 * 1. **It passes beside a beat, not through it.** You stand near the gravel bed and look at
 *    it; you do not stand inside it. Each beat contributes a point offset to one side, and
 *    the side alternates, so the route weaves the way a person picks their way along a bank.
 * 2. **It has intermediate points.** Two points between beats let the route bend instead of
 *    turning a hard corner, and give the simulated walk something to follow.
 * 3. **It sits on the ground.** Every point is dropped onto the scan by raycast, because the
 *    user camera is placed at path height plus eye height. A flat path either buries the
 *    walker in the bank or floats them over it — and on a creek falling two metres end to
 *    end, that error is the entire elevation change of the piece.
 */
function autoPath() {
  const beats = state.journey?.beats ?? [];
  if (beats.length < 2) { toast('Need at least two beats to route a path.', true); return; }

  const raw = [];
  const OFFSET = 1.6;          // metres to the side of a point of interest
  const BETWEEN = 2;           // intermediate points per gap

  const sideOffset = (a, b, sign) => {
    // Perpendicular in the horizontal plane — a walker steps aside, not up.
    const dx = b.position.x - a.position.x;
    const dz = b.position.z - a.position.z;
    const len = Math.hypot(dx, dz) || 1;
    return { x: (-dz / len) * OFFSET * sign, z: (dx / len) * OFFSET * sign };
  };

  beats.forEach((beat, i) => {
    const prev = beats[Math.max(0, i - 1)];
    const next = beats[Math.min(beats.length - 1, i + 1)];
    const ref = i === 0 ? next : prev;
    // Alternate which side of the creek the route passes on, so it reads as a wander rather
    // than a rail running parallel to the beats.
    const off = sideOffset(i === 0 ? beat : ref, i === 0 ? ref : beat, i % 2 === 0 ? 1 : -1);
    raw.push({ x: beat.position.x + off.x, z: beat.position.z + off.z });

    if (i < beats.length - 1) {
      const b2 = beats[i + 1];
      for (let k = 1; k <= BETWEEN; k += 1) {
        const t = k / (BETWEEN + 1);
        raw.push({
          x: beat.position.x + (b2.position.x - beat.position.x) * t,
          z: beat.position.z + (b2.position.z - beat.position.z) * t,
        });
      }
    }
  });

  // Drop every point onto the scan. Where the ray misses — a gap in the capture, or a point
  // that has wandered off the edge — fall back to the nearest beat's height rather than
  // inventing one, and say how often that happened.
  let missed = 0;
  const bounds = state.journey.editorFrame?.bounds;
  const fallbackY = bounds ? bounds.lo.y + 0.15 : 0;

  const points = raw.map((p) => {
    const ground = stage.sampleGround(p.x, p.z);
    if (ground === null) missed += 1;
    return {
      x: +p.x.toFixed(3),
      y: +(ground ?? fallbackY).toFixed(3),
      z: +p.z.toFixed(3),
    };
  });

  state.journey.site.centreline = points;
  for (const beat of state.journey.beats) {
    beat.s = +projectToCentreline(beat.position, points).s.toFixed(2);
  }

  stage.setJourney(state.journey);
  scrubber.setJourney(state.journey);
  stage.selectPathPoint(-1);
  renderPathReadout();
  renderRail();
  setWalker(0);
  persistPath();

  toast(missed
    ? `Routed ${points.length} points, ${journeyLength().toFixed(1)} m. `
      + `${missed} could not find ground and used a fallback height — check those.`
    : `Routed ${points.length} points over ${journeyLength().toFixed(1)} m, on the ground.`);
}

function renderPathReadout() {
  const el = $('#path-readout');
  if (!el) return;
  const pts = state.journey?.site?.centreline ?? [];
  const i = stage.selectedPathIndex;
  el.innerHTML =
    `<b>points</b>  ${pts.length}\n` +
    `<b>length</b>  ${journeyLength().toFixed(1)} m\n` +
    (i >= 0 && pts[i]
      ? `<b>selected</b> #${i + 1}  ${pts[i].x.toFixed(2)}  ${pts[i].y.toFixed(2)}  ${pts[i].z.toFixed(2)}`
      : '<span style="color:var(--amber)">click a handle to select</span>');
}

/** Default the trim box to the measured extent of the scan. */
function defaultTrim() {
  const b = state.journey?.editorFrame?.bounds;
  const m = 0.5;
  const lo = b ? b.lo : { x: -20, y: -5, z: -20 };
  const hi = b ? b.hi : { x: 20, y: 10, z: 20 };
  return {
    enabled: true,
    position: {
      x: +((lo.x + hi.x) / 2).toFixed(2),
      y: +((lo.y + hi.y) / 2).toFixed(2),
      z: +((lo.z + hi.z) / 2).toFixed(2),
    },
    rotation: [0, 0, 0, 1],
    halfExtent: {
      x: +((hi.x - lo.x) / 2 + m).toFixed(2),
      y: +((hi.y - lo.y) / 2 + m).toFixed(2),
      z: +((hi.z - lo.z) / 2 + m).toFixed(2),
    },
  };
}

function ensureTrim() {
  const ef = state.journey.editorFrame;
  if (!ef.trim) {
    ef.trim = { ...defaultTrim(), enabled: false };
  } else if (ef.trim.min && ef.trim.max && !ef.trim.halfExtent) {
    // Migrate the older axis-aligned box in place.
    const { min, max, enabled } = ef.trim;
    ef.trim = {
      enabled: !!enabled,
      position: {
        x: +((min.x + max.x) / 2).toFixed(3),
        y: +((min.y + max.y) / 2).toFixed(3),
        z: +((min.z + max.z) / 2).toFixed(3),
      },
      rotation: [0, 0, 0, 1],
      halfExtent: {
        x: +Math.max(0.05, (max.x - min.x) / 2).toFixed(3),
        y: +Math.max(0.05, (max.y - min.y) / 2).toFixed(3),
        z: +Math.max(0.05, (max.z - min.z) / 2).toFixed(3),
      },
    };
  }
  return ef.trim;
}

/**
 * Save the trim on its own. It is a view setting the author adjusts by eye, and should
 * survive a reload whether or not they pressed Save — but committing the whole draft in the
 * background would also push out any half-finished beat edit, and would be rejected if that
 * edit did not yet validate.
 */
let persistTimer;
function persistTrim() {
  clearTimeout(persistTimer);
  persistTimer = setTimeout(async () => {
    const frame = state.journey?.editorFrame;
    if (!frame?.trim || !state.slug) return;
    try {
      await api(`/api/sites/${state.slug}/editor-frame`, {
        method: 'PUT', body: { trim: frame.trim },
      });
    } catch (err) {
      toast(`Trim not saved: ${err.message}`, true);
    }
  }, 400);
}

function renderTrimPanel() {
  const trim = ensureTrim();
  $('#trim-on').checked = !!trim.enabled;
  renderTrimReadout(trim);
}

/** The numbers are for reading and for the record; the gizmo is for changing them. */
function renderTrimReadout(trim) {
  const el = $('#trim-readout');
  if (!el) return;
  const p = trim.position, h = trim.halfExtent;
  const deg = quaternionToEulerDegrees(trim.rotation ?? [0, 0, 0, 1]);
  el.innerHTML =
    `<b>centre</b>  ${p.x.toFixed(2)}  ${p.y.toFixed(2)}  ${p.z.toFixed(2)}\n` +
    `<b>size</b>    ${(h.x * 2).toFixed(2)}  ${(h.y * 2).toFixed(2)}  ${(h.z * 2).toFixed(2)}\n` +
    `<b>yaw</b>     ${deg.y.toFixed(1)}°\n` +
    (trim.enabled
      ? '<span style="color:var(--moss)">trimming the view</span>'
      : '<span style="color:var(--amber)">box only — not trimming</span>');
}

function quaternionToEulerDegrees([x, y, z, w]) {
  const sinp = 2 * (w * y - z * x);
  return {
    x: Math.atan2(2 * (w * x + y * z), 1 - 2 * (x * x + y * y)) * 180 / Math.PI,
    y: (Math.abs(sinp) >= 1 ? Math.sign(sinp) * Math.PI / 2 : Math.asin(sinp)) * 180 / Math.PI,
    z: Math.atan2(2 * (w * z + x * y), 1 - 2 * (y * y + z * z)) * 180 / Math.PI,
  };
}

function applyTrim(trim) {
  stage.setTrim(trim);
  renderTrimReadout(trim);
  state.dirty = true;
  updateChrome();
}

function setPathPanelQuiet(open) {
  $('#path-panel').hidden = !open;
  $('#btn-path').setAttribute('aria-pressed', String(open));
  $('#btn-path').classList.toggle('primary', open);
  stage.setPathEditing(open);
}

function setPathPanel(open) {
  $('#path-panel').hidden = !open;
  $('#btn-path').setAttribute('aria-pressed', String(open));
  $('#btn-path').classList.toggle('primary', open);
  if (open) { setTrimPanel(false); stage.placing = false; $('#btn-place').classList.remove('primary'); }
  stage.setPathEditing(open);
  if (open) {
    renderPathReadout();
    toast('Click a handle to select it, then drag. This is the simulated walking route.');
  }
}

function setGizmoMode(mode) {
  stage.setGizmoMode(mode);
  for (const [id, m] of [['gz-move', 'translate'], ['gz-rotate', 'rotate'], ['gz-scale', 'scale']]) {
    $(`#${id}`).setAttribute('aria-pressed', String(m === mode));
  }
}

function setTrimPanel(open) {
  if (open) setPathPanelQuiet(false);
  $('#trim-panel').hidden = !open;
  $('#btn-trim').setAttribute('aria-pressed', String(open));
  $('#btn-trim').classList.toggle('primary', open);
  stage.setGizmoVisible(open);
  if (open) {
    const trim = ensureTrim();
    // Turn the trim on as soon as the panel opens. Opening it with the effect switched off
    // means you drag a wireframe and nothing happens to the scan, which reads as the preview
    // being broken rather than as the effect being disabled. What you drag should be what you
    // see; the checkbox is there to compare against the untrimmed scan, not as a first step.
    if (!trim.enabled) {
      trim.enabled = true;
      persistTrim();
    }
    stage.setTrim(trim);
    renderTrimPanel();
    setGizmoMode('translate');
  } else {
    // Leaving the panel keeps whatever the author decided; nothing is silently reverted.
    stage.setTrim(state.journey?.editorFrame?.trim ?? null);
  }
}

// ------------------------------------------------------------------ chrome

function updateChrome() {
  const calibrated = state.journey?.editorFrame?.calibrated;
  const pill = $('#calib-pill');
  pill.textContent = calibrated ? 'calibrated' : 'uncalibrated';
  pill.className = `pill ${calibrated ? 'ok' : 'warn'}`;

  $('#btn-save').textContent = state.dirty ? 'Save draft •' : 'Save draft';
  $('#btn-save').disabled = !state.dirty;
}

let validateTimer;
function scheduleValidate() {
  clearTimeout(validateTimer);
  validateTimer = setTimeout(validate, 400);
}

async function validate() {
  if (!state.journey) return;
  let result;
  try {
    result = await api(`/api/sites/${state.slug}/validate`, {
      method: 'POST', body: state.journey,
    });
  } catch (err) {
    $('#notes').innerHTML = `<div class="note error">${escapeHtml(err.message)}</div>`;
    return;
  }

  const notes = [];
  for (const e of result.errors) notes.push(`<div class="note error">${escapeHtml(e)}</div>`);
  for (const w of result.warnings) notes.push(`<div class="note warn">${escapeHtml(w)}</div>`);
  if (!notes.length) notes.push('<div class="note ok">No problems found.</div>');
  $('#notes').innerHTML = notes.join('');

  $('#btn-publish').disabled = !result.ok;
}

// ------------------------------------------------------------------ toolbar

function bindToolbar() {
  $('#view-god').addEventListener('click', () => setView('god'));
  $('#view-user').addEventListener('click', () => setView('user'));

  $('#btn-frame').addEventListener('click', () => stage.frame());

  $('#btn-place').addEventListener('click', (e) => {
    stage.placing = !stage.placing;
    if (stage.placing) setPathPanel(false);
    e.target.setAttribute('aria-pressed', String(stage.placing));
    e.target.classList.toggle('primary', stage.placing);
    toast(stage.placing ? 'Click the scan to move the selected beat.' : 'Placement off.');
  });

  $('#btn-path').addEventListener('click', () => setPathPanel($('#path-panel').hidden));
  $('#path-close').addEventListener('click', () => setPathPanel(false));

  $('#path-auto').addEventListener('click', autoPath);

  $('#path-add').addEventListener('click', () => {
    const pts = state.journey.site.centreline;
    const i = stage.selectedPathIndex;
    // Insert after the selected point, halfway to the next one — which is where you want a
    // new control point when a bend needs more resolution.
    const at = i >= 0 ? i : pts.length - 2;
    const a = pts[Math.max(0, at)];
    const b = pts[Math.min(pts.length - 1, at + 1)] ?? a;
    pts.splice(at + 1, 0, {
      x: +((a.x + b.x) / 2).toFixed(3),
      y: +((a.y + b.y) / 2).toFixed(3),
      z: +((a.z + b.z) / 2).toFixed(3),
    });
    stage.setJourney(state.journey);
    stage.selectPathPoint(at + 1);
    renderPathReadout();
    persistPath();
  });

  $('#path-del').addEventListener('click', () => {
    const pts = state.journey.site.centreline;
    const i = stage.selectedPathIndex;
    if (i < 0) { toast('Select a handle first.', true); return; }
    if (pts.length <= 2) { toast('A path needs at least two points.', true); return; }
    pts.splice(i, 1);
    stage.setJourney(state.journey);
    stage.selectPathPoint(Math.min(i, pts.length - 1));
    renderPathReadout();
    persistPath();
  });

  $('#btn-trim').addEventListener('click', () => setTrimPanel($('#trim-panel').hidden));
  $('#trim-close').addEventListener('click', () => setTrimPanel(false));
  $('#trim-on').addEventListener('change', (e) => {
    const trim = ensureTrim();
    trim.enabled = e.target.checked;
    applyTrim(trim);
    persistTrim();
    toast(trim.enabled ? 'Trim applied to the view.' : 'Trim off — showing every splat.');
  });
  $('#gz-move').addEventListener('click', () => setGizmoMode('translate'));
  $('#gz-rotate').addEventListener('click', () => setGizmoMode('rotate'));
  $('#gz-scale').addEventListener('click', () => setGizmoMode('scale'));

  $('#trim-fit').addEventListener('click', () => {
    state.journey.editorFrame.trim = defaultTrim();
    renderTrimPanel();
    applyTrim(state.journey.editorFrame.trim);
    persistTrim();
    toast('Box fitted to the scan\u2019s measured extent.');
  });

  $('#btn-splat').addEventListener('click', (e) => {
    const next = !stage.splatVisible;
    stage.setSplatVisible(next);
    e.target.setAttribute('aria-pressed', String(next));
  });

  $('#t-play').addEventListener('click', () => setPlaying(!state.playing));
  $('#t-rewind').addEventListener('click', rewind);
  $('#t-speed').addEventListener('input', (e) => {
    state.speed = Number(e.target.value);
    $('#t-speed-label').textContent = `${state.speed.toFixed(1)} m/s`;
  });
  $('#t-audio').addEventListener('click', async () => {
    if (!audition.status().ready) {
      try {
        await audition.enable();
        await audition.load(state.journey, clipUrl);
        toast('Sound on. This is Resonance Audio standing in for PHASE — set real levels at the creek.');
      } catch (err) {
        toast(`Could not start audio: ${err.message}`, true);
      }
    } else {
      audition.setMuted(!audition.muted);
    }
    renderAudioNote();
  });

  $('#q-full').addEventListener('click', () => setQuality('full'));
  $('#q-fast').addEventListener('click', () => setQuality('fast'));

  $('#btn-add').addEventListener('click', addBeat);
  $('#btn-save').addEventListener('click', save);
  $('#btn-publish').addEventListener('click', publish);

  window.addEventListener('keydown', (e) => {
    if (e.target.matches('input, textarea, select')) return;
    if ((e.metaKey || e.ctrlKey) && e.key === 's') { e.preventDefault(); save(); }
    if (e.key === ' ') { e.preventDefault(); setPlaying(!state.playing); }
    // Gizmo modes while the trim panel is open; otherwise g/u switch camera.
    if (!$('#trim-panel').hidden) {
      if (e.key === 'g') { setGizmoMode('translate'); return; }
      if (e.key === 'r') { setGizmoMode('rotate'); return; }
      if (e.key === 't') { setGizmoMode('scale'); return; }
    }
    if (e.key === 'g') setView('god');
    if (e.key === 'u') setView('user');
  });

  window.addEventListener('beforeunload', (e) => {
    if (state.dirty) { e.preventDefault(); e.returnValue = ''; }
  });

  // Browsers stop requestAnimationFrame entirely in a background tab, so a simulation left
  // running while you switch away would freeze mid-walk with the audio still playing at a
  // stale listener position — which sounds like a bug rather than a pause. Stop cleanly and
  // say so.
  document.addEventListener('visibilitychange', () => {
    if (document.hidden && state.playing) {
      setPlaying(false);
      audition?.setMuted(true);
      state.autoPaused = true;
    } else if (!document.hidden && state.autoPaused) {
      state.autoPaused = false;
      audition?.setMuted(false);
      toast('Paused while the tab was in the background.');
    }
  });
}

function setView(mode) {
  stage.setMode(mode);
  $('#view-god').setAttribute('aria-pressed', String(mode === 'god'));
  $('#view-user').setAttribute('aria-pressed', String(mode === 'user'));
  setWalker(scrubber.s);
}

function addBeat() {
  const beats = state.journey.beats;
  const total = journeyLength();
  const s = Math.min(total, scrubber.s);
  const cl = state.journey.site.centreline;
  const p = cl?.length >= 2 ? pointAtS(s, cl) : { x: 0, y: 0, z: 0 };

  const base = beats.at(-1)?.trigger ?? { enterRadiusM: 2.5, exitRadiusM: 4, dwellSeconds: 1.2, minimumHoldSeconds: 25 };
  const id = uniqueId('beat', beats.map((b) => b.id));

  const beat = {
    id,
    title: 'New beat',
    prompt: '',
    interaction: 'proximity',
    position: { x: +p.x.toFixed(3), y: +p.y.toFixed(3), z: +p.z.toFixed(3) },
    s: +s.toFixed(2),
    trigger: { ...base, requiresPreviousComplete: true },
    audio: { mid: { clipId: 'tree-creek-waterplants-1--mid', gainDb: -8, loop: true } },
  };

  // Insert in order of s so the list always reads upstream.
  const index = beats.findIndex((b) => (b.s ?? 0) > s);
  if (index === -1) beats.push(beat); else beats.splice(index, 0, beat);

  state.selectedId = id;
  state.dirty = true;
  touch({ rail: true, inspector: true });
  save();
  toast(`Added “${beat.title}” at ${s.toFixed(1)} m. Drag it in the scene, or drag it in the list to reorder.`);
}

function uniqueId(prefix, taken) {
  let n = 1;
  let id = `${prefix}-${n}`;
  while (taken.includes(id)) { n += 1; id = `${prefix}-${n}`; }
  return id;
}

async function save() {
  if (!state.dirty) return;
  try {
    const res = await api(`/api/sites/${state.slug}/draft`, { method: 'PUT', body: state.journey });
    state.dirty = false;
    updateChrome();
    toast(res.warnings?.length ? `Saved with ${res.warnings.length} warning(s).` : 'Draft saved.');
  } catch (err) {
    const detail = err.detail?.errors?.slice(0, 3).join(' · ') ?? err.message;
    toast(`Not saved — ${detail}`, true);
  }
}

async function publish() {
  if (state.dirty) await save();
  try {
    const res = await api(`/api/sites/${state.slug}/publish`, { method: 'POST' });
    state.journey.revision = res.revision;
    toast(`Published revision ${res.revision}. The phone will pick it up on next load.`);
    updateChrome();
  } catch (err) {
    const detail = err.detail?.errors?.slice(0, 3).join(' · ') ?? err.message;
    toast(`Not published — ${detail}`, true);
  }
}

// ------------------------------------------------------------------ util

function escapeHtml(s) {
  return String(s ?? '').replace(/[&<>"']/g, (c) =>
    ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}
function escapeAttr(s) { return escapeHtml(s); }

boot().catch((err) => {
  console.error(err);
  document.getElementById('loading').hidden = true;
  toast(`Editor failed to start: ${err.message}`, true);
});
