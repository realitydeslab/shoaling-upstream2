/**
 * The operator's controller.
 *
 * Carried alongside the visitor while the experience runs automatically. It exists because
 * every project that has built this kind of work has ended up needing it: Notre-Dame's
 * "Whispers" added manual waypoint unlocking so the narrative stays reachable when GPS drift
 * defeats the automatic triggers, and Hidden Florence shipped a Manual Mode for the same
 * reason. It is not a convenience.
 *
 * Two rules it follows:
 *   - Local feedback is immediate regardless of when the phone acts. The person pressing and
 *     the person hearing are different people, so nothing is fusing across the network anyway;
 *     what matters is that the operator is never left wondering whether the press registered.
 *   - Commands are scheduled, never fired. The server stamps a fire time a few hundred
 *     milliseconds ahead, which turns variable latency into fixed latency and makes a late
 *     packet get dropped rather than fire at the wrong moment.
 */

const $ = (s) => document.querySelector(s);

const state = {
  socket: null,
  connected: false,
  serverState: null,
  devices: [],
  journey: null,
  reachLength: 0,
  backoffMs: 500,
};

// ------------------------------------------------------------------ socket

function connect() {
  const url = `${location.protocol === 'https:' ? 'wss' : 'ws'}://${location.host}/ws?role=operator`;
  const socket = new WebSocket(url);
  state.socket = socket;

  socket.addEventListener('open', () => {
    state.connected = true;
    state.backoffMs = 500;
    setPill('conn', 'live', 'connected');
    log('connected to service');
  });

  socket.addEventListener('message', (ev) => {
    const msg = JSON.parse(ev.data);
    switch (msg.type) {
      case 'state':
        state.serverState = msg.state;
        render();
        break;
      case 'presence':
        state.devices = msg.devices;
        render();
        break;
      case 'commandIssued':
        log(`→ <b>${msg.command.action}</b>${msg.command.beatId ? ` ${msg.command.beatId}` : ''} in ${msg.command.fireAtMs - msg.command.issuedAtMs} ms`);
        break;
      case 'commandAck':
        log(msg.applied
          ? `<span class="ok">✓ applied</span> ${msg.commandId}`
          : `<span class="no">✗ refused</span> ${msg.commandId}${msg.note ? ` — ${msg.note}` : ''}`);
        break;
      case 'published':
        log(`published revision ${msg.revision} of ${msg.site}`);
        loadJourney(msg.site);
        break;
      default:
        break;
    }
  });

  const drop = () => {
    if (!state.connected) return;
    state.connected = false;
    setPill('conn', 'off', 'reconnecting');
    render();
    // Exponential backoff. A phone that walks out of Wi-Fi range makes this server
    // unroutable entirely, so reconnection is about the operator's own browser, not the
    // phone — the phone's own client handles its side the same way.
    setTimeout(connect, state.backoffMs);
    state.backoffMs = Math.min(state.backoffMs * 2, 10_000);
  };
  socket.addEventListener('close', drop);
  socket.addEventListener('error', drop);
}

function issue(action, beatId = null, value = null) {
  // Local feedback first, always.
  log(`<b>${action}</b>${beatId ? ` ${beatId}` : ''} …`);
  if (state.socket?.readyState === 1) {
    state.socket.send(JSON.stringify({ type: 'command', action, beatId, value }));
  } else {
    // Fall back to the HTTP path so a flaky socket does not silence the controller.
    fetch('/api/control/command', {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ action, beatId, value }),
    }).catch(() => log('<span class="no">command failed — no route to service</span>'));
  }
}

// ------------------------------------------------------------------ content

async function loadJourney(slug) {
  const site = slug ?? (await (await fetch('/api/sites')).json())[0]?.slug;
  if (!site) return;
  try {
    state.journey = await (await fetch(`/api/sites/${site}/published`)).json();
  } catch {
    state.journey = await (await fetch(`/api/sites/${site}/draft`)).json();
  }
  const cl = state.journey?.site?.centreline ?? [];
  state.reachLength = cl.reduce((total, p, i) =>
    i === 0 ? 0 : total + Math.hypot(p.x - cl[i - 1].x, p.y - cl[i - 1].y, p.z - cl[i - 1].z), 0);
  renderBeats();
  render();
}

// ------------------------------------------------------------------ render

function setPill(id, kind, text) {
  const el = $(`#${id}`);
  el.className = `pill ${kind}`;
  el.textContent = text;
}

function render() {
  const s = state.serverState;
  const device = state.devices.find((d) => d.alive);

  const strip = $('#strip');
  const headline = $('#headline');
  const detail = $('#detail');

  if (!state.connected) {
    strip.className = 'status-strip lost';
    headline.textContent = 'No connection to the service';
    detail.textContent = 'Reconnecting. If this persists, check you are on the same network as the laptop.';
  } else if (!device) {
    strip.className = 'status-strip lost';
    headline.textContent = 'Waiting for the phone';
    detail.textContent = 'No device has connected to this session yet. Open the app and scan the QR code.';
  } else if (!s || s.localization === 'unavailable') {
    strip.className = 'status-strip lost';
    headline.textContent = 'The phone is not localized';
    detail.textContent = 'Nothing will fire until it recognises the site. Point the camera at the bank, not the water — moving water gives it nothing to match.';
  } else if (s.localization === 'coarse') {
    strip.className = 'status-strip coarse';
    headline.textContent = 'Roughly located';
    detail.textContent = 'Position is not trustworthy yet, so beats stay held. Walk a few steps and let the camera see the bank.';
  } else {
    strip.className = 'status-strip precise';
    const current = currentBeat();
    headline.textContent = current ? `Playing “${current.title}”` : 'Located, between beats';
    detail.textContent = current
      ? (current.prompt || 'Waiting for the visitor to complete this beat.')
      : `Next: ${nextBeat()?.title ?? 'nothing — the journey is finished'}.`;
  }

  $('#r-s').innerHTML = s?.s != null
    ? `${s.s.toFixed(1)}<small> m</small>` : '—<small> m</small>';
  $('#r-conf').textContent = s?.trackingConfidence != null
    ? s.trackingConfidence.toFixed(2) : '—';
  $('#r-shoal').textContent = s?.shoalCount != null ? String(s.shoalCount) : '—';

  drawTrack();
  markBeats();
}

function currentBeat() {
  const id = state.serverState?.currentBeat;
  return state.journey?.beats.find((b) => b.id === id) ?? null;
}

function nextBeat() {
  const done = new Set(state.serverState?.completed ?? []);
  return state.journey?.beats.find((b) => !done.has(b.id)) ?? null;
}

function renderBeats() {
  const beats = state.journey?.beats ?? [];
  $('#ctl-beats').innerHTML = beats.map((b, i) => `
    <button class="ctl-beat" data-id="${b.id}">
      <span class="n">${String(i + 1).padStart(2, '0')}</span>
      <span>
        <span class="t">${escapeHtml(b.title)}</span>
        <span class="sub">${escapeHtml(b.interaction)} · ${(b.s ?? 0).toFixed(1)} m</span>
      </span>
      <span class="go">Fire</span>
    </button>`).join('');

  $('#ctl-beats').querySelectorAll('.ctl-beat').forEach((el) => {
    el.addEventListener('click', () => issue('fireBeat', el.dataset.id));
  });
}

function markBeats() {
  const done = new Set(state.serverState?.completed ?? []);
  const current = state.serverState?.currentBeat;
  document.querySelectorAll('.ctl-beat').forEach((el) => {
    el.classList.toggle('done', done.has(el.dataset.id));
    el.classList.toggle('current', el.dataset.id === current);
  });
}

function drawTrack() {
  const canvas = $('#ctl-track');
  const dpr = Math.min(devicePixelRatio, 2);
  const w = canvas.clientWidth, h = canvas.clientHeight;
  if (!w || !h) return;
  if (canvas.width !== w * dpr) { canvas.width = w * dpr; canvas.height = h * dpr; }
  const ctx = canvas.getContext('2d');
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  ctx.clearRect(0, 0, w, h);

  const total = state.reachLength || 1;
  const x = (s) => 6 + (s / total) * (w - 12);
  const midY = h * 0.55;

  ctx.strokeStyle = '#262F29';
  ctx.lineWidth = 2;
  ctx.beginPath(); ctx.moveTo(x(0), midY); ctx.lineTo(x(total), midY); ctx.stroke();

  const done = new Set(state.serverState?.completed ?? []);
  for (const b of state.journey?.beats ?? []) {
    ctx.fillStyle = done.has(b.id) ? '#93A76B'
      : b.id === state.serverState?.currentBeat ? '#D4707F' : '#55625A';
    ctx.beginPath(); ctx.arc(x(b.s ?? 0), midY, 5, 0, Math.PI * 2); ctx.fill();
  }

  const s = state.serverState?.s;
  if (s != null) {
    ctx.strokeStyle = '#6DA7AD';
    ctx.lineWidth = 2;
    ctx.beginPath(); ctx.moveTo(x(s), 4); ctx.lineTo(x(s), h - 4); ctx.stroke();
  }

  ctx.fillStyle = '#55625A';
  ctx.font = '9px ui-monospace, monospace';
  ctx.fillText('0', x(0) - 2, h - 1);
  ctx.fillText(`${total.toFixed(0)} m upstream →`, x(total) - 78, h - 1);
}

// ------------------------------------------------------------------ log

function log(html) {
  const el = $('#log');
  const time = new Date().toLocaleTimeString('en-GB', { hour12: false });
  el.insertAdjacentHTML('afterbegin', `<div>${time} ${html}</div>`);
  while (el.children.length > 60) el.lastElementChild.remove();
}

function escapeHtml(s) {
  return String(s ?? '').replace(/[&<>"']/g, (c) =>
    ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}

// ------------------------------------------------------------------ go

$('#a-replay').addEventListener('click', () => issue('replayCurrent'));
$('#a-skip').addEventListener('click', () => issue('advance'));
$('#a-silence').addEventListener('click', () => issue('silence'));
$('#a-resume').addEventListener('click', () => issue('resume'));
window.addEventListener('resize', drawTrack);

connect();
loadJourney();
setInterval(render, 1000);
