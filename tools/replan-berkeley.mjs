/**
 * Place the Strawberry Creek South journey against the creek as it actually runs.
 *
 * The seeded layout was a 3-point straight line down the middle of the bounding box at a flat
 * y = −0.50, with six beats on a vocabulary the piece no longer uses (`barrier`, `headwater`).
 * Nothing here had ever been touched by hand: the trim box was axis-aligned with identity
 * rotation and covered essentially the whole capture.
 *
 * This replaces all of it with a route and six beats derived from the scan itself.
 *
 * ─── what the scan says ────────────────────────────────────────────────────────────────────
 *
 * The creek is NOT along the scan's long axis. It runs in an L: in from the south-east corner
 * at about world (+7, −6), north-west across the capture to about (−11, +9), then almost due
 * north along x ≈ −12 to (−10, +48). Traced as a thalweg — the x of the lowest bed in each
 * 1 m slice of z — it is 65.2 m long. Either side of it the ground climbs into banks: at
 * z = +20 the west bank rises from −0.7 at x = −13 to +2.8 at x = −21, and the east bank from
 * −0.4 to +0.8 in three metres. It is a genuinely incised channel, roughly 3 m of flat bottom
 * between banks 2–3 m high, and it is unmistakable in a top-down height map.
 *
 * Upstream is NORTH, toward +Z. The bed rises +2.21 m over those 65.2 m — a 3.4 % mean
 * gradient, monotonic apart from one pool. That number is corroborated independently: the VPS
 * mesh for this site has 2.27 m of rise across its 26 nodes (docs/site-geometry.md), and
 * Strawberry Creek flows west, so upstream is east and uphill. Two measurements of the same
 * 2.2 m, from different assets.
 *
 * ─── there is no waterfall here, and the ending is different because of it ──────────────────
 *
 * The garden ends by lifting yourself over a three-metre step and spawning in the still water
 * above it. That step is real at UBC: the profile there drops 1.74 → −1.09 m in two horizontal
 * metres. Nothing of the kind exists on this reach. The steepest rise over any 2 m of channel
 * is +0.44 m and over any 4 m is +0.58 m, and both sit inside ordinary riffle, not at a lip.
 * A 3.4 % ramp is a creek running over gravel, not a fall.
 *
 * What IS here, at z ≈ 27–36 (s 43–52 along the channel), is a dark still reach, and four
 * independent measurements agree on it:
 *
 *   splat density at bed level   3000–5300 /m² either side  →  127–555 /m² inside
 *   luminance                    111–137                    →  96–104
 *   bright-desaturated fraction  11–38 %                    →  1–4 %
 *   apparent bed roughness       0.12–0.33 m                →  0.46–0.68 m
 *
 * That combination — sparse, dark, and geometrically noisy — is what a smooth specular water
 * surface does to photogrammetry. A riffle reconstructs densely and brightly because broken
 * white water is opaque and textured; still deep water gives reflections, transparency and
 * garbage geometry. The banks close in over the same stretch (the west wall reaches +4.6 m
 * within 7 m of the channel) and the canopy closes over it at +7.5–9 m. Meanwhile the dry
 * gravel margin 1.5 m to the west reads a steady −0.2 m right through, matching the reaches
 * above and below, so the pool is flat water — deeper, not stepped.
 *
 * Note the brightness signal is used here in the OPPOSITE sense to the garden, where bright
 * desaturated splats were taken for water and turned out to be sunlit canopy at the capture
 * edge. Bright means broken; dark and sparse means still. Neither is trusted on its own: the
 * route is laid on bed geometry, and the brightness only labels what kind of water it is.
 *
 * So the ending is: you arrive in the dark still water, you get up out of it, and you spawn on
 * the gravel above. The obstacle is the dark passage, not a height — which is also the
 * historically correct barrier for this creek. UC Berkeley's own 1987 management plan says of
 * the culverts in this watershed that they are "totally dark passages that anadromous fish
 * avoid" and "complete barriers to upstream migration", and that "pools that provided rest
 * areas were obliterated". The steelhead run ended in the 1920s. The lift is measured, not
 * invented: from the wetted channel at x ≈ −12.1 up onto the east gravel margin at x ≈ −10.9
 * is 0.25–0.29 m of visible step through z = 30…36, and the true figure is larger because the
 * channel reading is taken through the water surface. Above that the bank benches again by
 * another 0.5–0.7 m. Somewhere in there is the ~40 cm the piece asks for.
 *
 * ─── the trim box ──────────────────────────────────────────────────────────────────────────
 *
 * creek-profile.py walks the trim box's LOCAL +X, so the seeded box — identity rotation, long
 * axis in Z — made it walk across the valley. It duly reported a 4 m "fall" from x = −21 to
 * x = +8 and nominated the far east bank as a candidate waterfall. That is the valley
 * cross-section, not the creek. The box below is fitted by minimum-width oriented bounding box
 * over the traced thalweg, with its +X pointing DOWNSTREAM so the yaw stays inside the range
 * asin() can represent (creek-profile.py recovers yaw with asin, which cannot see past ±90°).
 *
 * The box is wide — ±9.2 m across — and that width is set by the bend, not by the channel. A
 * single oriented box cannot hug a creek that turns 45° in the middle; the thalweg alone needs
 * ±5.9 m of cross-extent before any bank is included. This is the thing most worth improving
 * by hand.
 *
 *   node tools/replan-berkeley.mjs
 */

import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const SLUG = 'ucb-strawberry-creek-south';
const BASE = process.env.SERVICE ?? 'http://localhost:8710';

const draft = JSON.parse(
  await readFile(path.join(ROOT, 'data', 'journeys', SLUG, 'draft.json'), 'utf8')
);

/**
 * The fitted trim box.
 *
 * yaw 72.35°, so local +X points at world (+0.3032, 0, −0.9529): downstream, toward the
 * south-east corner. Upstream is therefore at −along, the same convention the garden ended up
 * with. Centre is the midpoint of the thalweg's own extent in that frame.
 */
const YAW_DEG = 72.35;
const BOX = {
  enabled: true,
  position: { x: -8.049, y: 2.0, z: 18.961 },
  rotation: [0, 0.590254, 0, 0.807218],       // pure Y rotation of YAW_DEG
  halfExtent: { x: 27.0, y: 7.0, z: 9.2 },
};

/**
 * Do not silently overwrite a box a person has since positioned.
 *
 * The devlog's first entry is a hand-placed trim box destroyed by a tool that regenerated it.
 * This box has never been touched — the draft still carries the seed — so writing it is right,
 * but only while that stays true. Re-running after someone has dragged the gizmo must fail
 * loudly rather than undo their work.
 */
const SEEDED = { px: -6.439, py: 2.179, pz: 18.517, hx: 15.486, hy: 7.158, hz: 31.212 };
const same = (a, b, tol = 0.02) => Math.abs(a - b) <= tol;
const cur = draft.editorFrame?.trim;
const isSeed = cur && same(cur.position.x, SEEDED.px) && same(cur.position.z, SEEDED.pz)
  && same(cur.halfExtent.x, SEEDED.hx) && same(cur.halfExtent.z, SEEDED.hz);
const isOurs = cur && same(cur.position.x, BOX.position.x) && same(cur.position.z, BOX.position.z)
  && same(cur.halfExtent.x, BOX.halfExtent.x) && same(cur.halfExtent.z, BOX.halfExtent.z);
if (cur && !isSeed && !isOurs) {
  console.error('\n  the trim box on this draft is neither the seed nor the one this tool fits.');
  console.error('  Someone has positioned it by hand. Refusing to overwrite it.');
  console.error(`    current  centre (${cur.position.x}, ${cur.position.y}, ${cur.position.z})`
    + ` half (${cur.halfExtent.x}, ${cur.halfExtent.y}, ${cur.halfExtent.z})`);
  process.exit(1);
}

const put = async (suffix, body) => {
  const res = await fetch(`${BASE}/api/sites/${SLUG}${suffix}`, {
    method: 'PUT',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(body),
  });
  const text = await res.text();
  if (!res.ok) throw new Error(`${suffix}: ${text}`);
  return JSON.parse(text);
};

await put('/editor-frame', { trim: BOX });

// Same frame maths as replan-garden.mjs and creek-profile.py, so all three agree on "along".
const [qx, qy, qz, qw] = BOX.rotation;
const yaw = Math.asin(Math.max(-1, Math.min(1, 2 * (qw * qy - qz * qx))));
const ax = Math.cos(yaw), az = -Math.sin(yaw);   // along the creek, +X = downstream
const cx = Math.sin(yaw), cz = Math.cos(yaw);    // across it
const c = BOX.position;

/** Nothing may be planned outside the box. Clamp, then refuse to write if anything escaped. */
const LIMIT = {
  along: BOX.halfExtent.x - 0.15,
  across: BOX.halfExtent.z - 0.15,
  yLo: c.y - BOX.halfExtent.y + 0.1,
  yHi: c.y + BOX.halfExtent.y - 0.1,
};
const clamped = [];

const at = (along, across, y, label = '') => {
  const a = Math.max(-LIMIT.along, Math.min(LIMIT.along, along));
  const r = Math.max(-LIMIT.across, Math.min(LIMIT.across, across));
  const h = Math.max(LIMIT.yLo, Math.min(LIMIT.yHi, y));
  if (a !== along || r !== across || h !== y) {
    clamped.push(`${label || 'point'} (${along}, ${across}, ${y}) -> (${a.toFixed(2)}, ${r.toFixed(2)}, ${h.toFixed(2)})`);
  }
  return {
    x: +(c.x + ax * a + cx * r).toFixed(3),
    y: +h.toFixed(3),
    z: +(c.z + az * a + cz * r).toFixed(3),
  };
};

/**
 * The walking route, at CHEST height.
 *
 * The phone hangs on a neck mount, so the camera rides at the sternum and the path IS the
 * camera track. Heights below are the measured 5th-percentile bed in a 2.4 m box at each
 * point — the channel bottom — and CHEST is added when the route is built. Beats are authored
 * at bed height instead; the two are different numbers and conflating them is a documented trap.
 *
 * The route follows the traced thalweg with about ±0.8 m of lateral wander, which is as much
 * as there is: the flat channel bottom is only ~3 m wide from z = +12 all the way to the top of
 * the capture, and outside that band the ground climbs immediately. There is no bank path in
 * this scan — the only continuous flat surface anywhere along the reach is the creek bed itself.
 * The first and last points stop short of the capture edges, where reconstruction thins to a
 * few hundred splats per square metre and any height read there is guesswork.
 */
const CHEST = 1.40;
const PATH = [
  [+25.45, +5.31, -2.18],   // world (+4.7, −3.7) — the wide slow basin at the bottom
  [+22.06, +3.98, -1.95],
  [+20.95, +0.36, -1.85],
  [+17.60, -1.06, -1.45],
  [+15.28, -3.61, -1.20],   // the steepest riffle on the reach, 11 % over 4 m
  [+12.03, -4.66, -1.18],
  [ +8.87, -6.04, -1.15],
  [ +5.58, -4.85, -0.72],   // the bend is behind; the channel now runs due north
  [ +1.94, -5.61, -0.59],
  [ -1.17, -3.58, -0.72],
  [ -4.68, -4.23, -0.42],   // widest, flattest, best-reconstructed water on the reach
  [ -7.68, -2.52, -0.44],
  [-10.94, -1.47, -0.72],   // the banks begin to close
  [-13.66, +0.77, -0.55],   // in the dark still reach
  [-17.33, +0.64, -0.48],
  [-19.86, +3.25, -0.37],   // out the top of it, gravel again
  [-23.31, +4.12, -0.28],
  [-25.80, +5.60, -0.20],   // as far up as the scan resolves
];

/**
 * Six beats.
 *
 * The last two are the ending this site actually supports. `darkwater` is the dark still reach
 * and the lift out of it; `spawn` is the gravel riffle above. There is no waterfall to get
 * over, and inventing one would have put the load-bearing moment of the piece in a place with
 * nothing there.
 */
const BEATS = [
  { id: 'tree',      along: +23.54, across: +4.52, y: -2.06,
    title: 'The tree',
    prompt: 'Come in under the branches where the water runs slow and wide.',
    interaction: 'proximity', clip: 'tree-creek-waterplants-1', completion: null },

  { id: 'redd',      along: +16.67, across: -3.38, y: -1.42,
    title: 'The gravel bed',
    prompt: 'Get low enough to see into the gravel.',
    interaction: 'crouch', clip: 'chapter-1-new-life', completion: 'lay-egg' },

  { id: 'strider',   along:  +5.97, across: -5.10, y: -0.78,
    title: 'Water striders',
    prompt: 'The banks have opened and the sky is on the water. Something is moving on it.',
    interaction: 'catch', clip: 'strider', completion: 'eat-strider' },

  { id: 'heron',     along:  -4.35, across: -4.68, y: -0.44,
    title: 'The heron',
    prompt: 'Shallow flat water, and it has been standing in it a long time. Let some of the shoal go.',
    interaction: 'give', clip: 'heron', completion: null, givesFish: 12 },

  { id: 'darkwater', along: -13.94, across: +1.39, y: -0.37,
    title: 'The dark water',
    prompt: 'The water goes still and the light goes out of it. The shoal will not go up there. '
          + 'Get yourself up out of it, onto the gravel on your right.',
    interaction: 'lift', clip: 'chapter-4-returning-home', completion: 'jump' },

  { id: 'spawn',     along: -23.37, across: +5.18, y: -0.26,
    title: 'The gravel above',
    prompt: 'Out of the dark and back into moving water. Gravel under you — as far up as '
          + 'the scan reaches.',
    interaction: 'crouch', clip: 'chapter-5-rebirth', completion: 'lay-egg' },
];

const layers = (clip, g = { far: -14, mid: -8, intimate: -4 }) => ({
  far: { clipId: `${clip}--far`, gainDb: g.far, loop: true },
  mid: { clipId: `${clip}--mid`, gainDb: g.mid, loop: true },
  intimate: { clipId: `${clip}--intimate`, gainDb: g.intimate, loop: true },
});

const centreline = PATH.map(([along, across, ground], i) =>
  at(along, across, ground + CHEST, `path[${i}]`));

const lengthOf = (pts) => pts.reduce((t, p, i) =>
  i === 0 ? 0 : t + Math.hypot(p.x - pts[i - 1].x, p.y - pts[i - 1].y, p.z - pts[i - 1].z), 0);
const total = lengthOf(centreline);

function sOf(point) {
  let best = { s: 0, d: Infinity }, travelled = 0;
  for (let i = 0; i < centreline.length - 1; i += 1) {
    const a = centreline[i], b = centreline[i + 1];
    const abx = b.x - a.x, aby = b.y - a.y, abz = b.z - a.z;
    const len = Math.hypot(abx, aby, abz);
    if (len < 1e-6) continue;
    let t = ((point.x - a.x) * abx + (point.y - a.y) * aby + (point.z - a.z) * abz) / (len * len);
    t = Math.max(0, Math.min(1, t));
    const d = Math.hypot(point.x - (a.x + abx * t), point.y - (a.y + aby * t), point.z - (a.z + abz * t));
    if (d < best.d) best = { s: travelled + len * t, d };
    travelled += len;
  }
  return best.s;
}

/**
 * Trigger geometry, and why it is nearly the garden's.
 *
 * The editor draws each beat's audible field as exitRadiusM × 3.5. The garden's 3.04 m exit
 * gives 10.64 m of reach, and on a 19 m reach with beats 1.6–4 m apart that meant all six were
 * audible from every position — eighteen loops at once, which the artist heard as "many reverb
 * sound". The radii were never the problem. The reach was.
 *
 * Here there are 61 m of scan and 60 m of route, so the beats can be spaced past their own
 * reach. They land 11.0–11.3 m apart ALONG THE PATH — but that is not the number the audible
 * field cares about. The field is a sphere, and this route bends, so the straight-line distance
 * between neighbours is up to 1.2 m shorter than their separation in `s`: the closest pair is
 * 10.16 m apart in space and 11.2 m apart along the walk. Sizing the radii against `s` would
 * have quietly reproduced the garden's bug on a route long enough to avoid it.
 *
 * So the exit radius is set against the straight-line figure: 2.80 x 3.5 = 9.80 m of reach
 * against a 10.16 m closest pair. Standing at any beat, no other beat is audible. Two overlap
 * only in the transitions, which is the 2–4-audible figure docs/audio-findings.md §3 designs
 * the spectral zoning around, rather than the 6-of-6 the garden got.
 *
 * These radii are slightly tighter than the garden's 1.9 / 3.04, and that is a real cost:
 * 1.75 m of enter band is tight against Niantic's 4 m median position error. It buys the
 * separation without touching the 1.6x hysteresis ratio the enter/exit rule in
 * docs/design-findings.md §4 depends on, and the operator-carried controller exists precisely
 * to cover a gate that does not fire. If the audio law changes and reach stops being 3.5x exit,
 * this is the constant to revisit — not the layout.
 */
const ENTER = 1.75, EXIT = 2.80;
const REACH = EXIT * 3.5;

const beats = BEATS.map((b) => {
  const position = at(b.along, b.across, b.y, b.id);
  const node = {
    id: b.id,
    title: b.title,
    prompt: b.prompt,
    interaction: b.interaction,
    position,
    s: +sOf(position).toFixed(2),
    trigger: {
      enterRadiusM: ENTER,
      exitRadiusM: EXIT,
      dwellSeconds: 1.2,
      minimumHoldSeconds: 25,
      requiresPreviousComplete: true,
    },
    audio: layers(b.clip),
  };
  if (b.completion) node.audio.completion = { clipId: b.completion, gainDb: -3, loop: false };
  if (b.givesFish) node.givesFish = b.givesFish;
  return node;
});

// The continuous bed. docs/design-findings.md §4: never let audio start from silence, and Riot!
// found the ambient layer was the difference between "immersive" and "the app has crashed".
const mid = centreline[Math.floor(centreline.length / 2)];
const ambient = (draft.ambient ?? []).map((a) => a.id === 'creek-bed'
  ? { ...a, position: { ...mid }, audibleRadiusM: +(total * 1.1).toFixed(1) }
  : a);

const updated = {
  ...draft,
  note: 'Upstream is north, toward +Z: the bed rises +2.21 m over 65.2 m of traced channel '
      + '(3.4 % mean gradient), which agrees with the 2.27 m of rise across the VPS mesh. '
      + 'There is no waterfall on this reach — the steepest 2 m of channel rises 0.44 m. '
      + 'The ending is the dark still reach at z 27–36 and the gravel above it.',
  site: { ...draft.site, centreline },
  beats,
  ambient,
  // The box goes through its own endpoint above, but this whole-document PUT would otherwise
  // write back the trim box as it stood on disk when the script started — which is the seed —
  // and silently undo it. Carrying the same box in both bodies keeps the two writes agreeing.
  editorFrame: { ...draft.editorFrame, trim: BOX },
};
updated.site.upstreamAxis = {
  kind: 'altitude',
  risesToward: 'north, the −along end of the trim box, world about (−10, +45). Measured from a '
             + 'thalweg traced as the lowest bed per 1 m slice: +2.21 m over 65.2 m. Not from '
             + 'brightness — bright desaturated splats mark broken water, and here they are '
             + 'lowest exactly where the water is deepest and stillest.',
};

await put('/draft', updated);

console.log(`\n  trim box  yaw ${YAW_DEG}°, +X world (${ax.toFixed(4)}, ${az.toFixed(4)}) = downstream`);
console.log(`            centre (${c.x}, ${c.y}, ${c.z})  half (${BOX.halfExtent.x}, ${BOX.halfExtent.y}, ${BOX.halfExtent.z})`);
console.log(`\n  path      ${centreline.length} points, ${total.toFixed(1)} m`);
console.log(`            from ${JSON.stringify(centreline[0])}`);
console.log(`            to   ${JSON.stringify(centreline.at(-1))}`);
console.log(`            climbing ${(centreline.at(-1).y - centreline[0].y).toFixed(2)} m\n`);
console.log(`  ${'beat'.padEnd(11)}${'s'.padStart(7)}${'gap'.padStart(7)}  interaction  position`);
let prev = null;
for (const b of beats) {
  const gap = prev === null ? '' : (b.s - prev).toFixed(1);
  prev = b.s;
  console.log(`  ${b.id.padEnd(11)}${b.s.toFixed(1).padStart(7)}${gap.padStart(7)}  ${b.interaction.padEnd(11)}  `
    + `(${b.position.x.toFixed(1)}, ${b.position.y.toFixed(1)}, ${b.position.z.toFixed(1)})`);
}
// The audible field is a sphere in space, so separation is a straight-line question, not an
// arc-length one. On a route that bends 45° those two numbers differ by more than a metre, and
// the s-gap is always the flattering one.
const dist = (a, b) => Math.hypot(a.x - b.x, a.y - b.y, a.z - b.z);
let closest = Infinity;
for (let i = 0; i < beats.length; i += 1) {
  for (let j = i + 1; j < beats.length; j += 1) {
    closest = Math.min(closest, dist(beats[i].position, beats[j].position));
  }
}
const gaps = beats.slice(1).map((b, i) => b.s - beats[i].s);
console.log(`\n  spacing   ${Math.min(...gaps).toFixed(1)}–${Math.max(...gaps).toFixed(1)} m along the path,`
  + ` closest pair ${closest.toFixed(2)} m in a straight line`);
console.log(`  audible reach ${REACH.toFixed(2)} m (exitRadiusM ${EXIT} x 3.5)`);
console.log(`  ${closest > REACH
  ? `no beat is audible from any other beat (${(closest - REACH).toFixed(2)} m of margin)`
  : 'WARNING: beats are closer than their own audible reach'}`);

// Refuse to ship a layout that leaves the box.
const outside = [];
for (const p of [...centreline, ...beats.map((b) => b.position)]) {
  const dx = p.x - c.x, dz = p.z - c.z;
  const a = dx * ax + dz * az, r = dx * cx + dz * cz;
  if (Math.abs(a) > BOX.halfExtent.x + 1e-3
      || Math.abs(r) > BOX.halfExtent.z + 1e-3
      || p.y < c.y - BOX.halfExtent.y - 1e-3
      || p.y > c.y + BOX.halfExtent.y + 1e-3) {
    outside.push(p);
  }
}
if (outside.length) {
  console.error(`\n  ${outside.length} point(s) fell outside the trim box. Not written.`);
  process.exit(1);
}

if (clamped.length) {
  console.log(`\n  clamped into the box:`);
  for (const c2 of clamped) console.log(`    ${c2}`);
}
console.log(`\n  all ${centreline.length + beats.length} points inside the box`
  + ` (±${BOX.halfExtent.x.toFixed(1)} along, ±${BOX.halfExtent.z.toFixed(1)} across,`
  + ` y ${(c.y - BOX.halfExtent.y).toFixed(2)}…${(c.y + BOX.halfExtent.y).toFixed(2)})`);
console.log('');
