/**
 * Re-place the UBC garden journey against the creek as it actually runs.
 *
 * The seeded layout was a straight line along the scan's bounding box, which is a scaffold,
 * not a route. This replaces it with a path and six beats derived from a measured profile
 * along the trim box's own axis (tools/creek-profile.py), read at ±4 m of corridor over the
 * full height of the scan.
 *
 * What the profile showed, walking the box axis (world direction 0.687, −0.727):
 *
 *   along −13.5 → −8.5  high ground, bed 3.38 → 1.74 m: the reach ABOVE the falls
 *   along  −8.5 → −6.5  bed drops 1.74 → −1.09 m — nearly three metres in two horizontal
 *                       metres. THIS IS THE WATERFALL.
 *   along  −6.5 → +5.5  the creek run, falling gently from −1.09 to −2.01 m
 *   along  +5.5 → +16.5 bed climbing again, 2.7 m over ten: the far bank out of the valley
 *
 * So the falls are at the −along end, and the visitor walks from the pool at +along up to
 * them. An earlier version of this file had the route reversed: it followed the fraction of
 * bright, desaturated splats, which peaks at BOTH ends — at the far end because that is thin,
 * sunlit canopy at the edge of the capture, not water. Bed geometry is the reliable signal.
 * A three-metre step is a waterfall; a 2.7 m rise over ten metres is a slope.
 *
 * The two beats at the end are the artist's instruction: lift yourself over the falls, then
 * spawn in the water above them.
 *
 *   node tools/replan-garden.mjs
 */

import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const SLUG = 'ubc-nitobe-garden-creek';
const BASE = process.env.SERVICE ?? 'http://localhost:8710';

const draft = JSON.parse(
  await readFile(path.join(ROOT, 'data', 'journeys', SLUG, 'draft.json'), 'utf8')
);

// The trim box the artist positioned is the best statement anyone has made about where the
// creek runs, so the route is built in its frame rather than in world axes.
const trim = draft.editorFrame.trim;
const [qx, qy, qz, qw] = trim.rotation;
const yaw = Math.asin(Math.max(-1, Math.min(1, 2 * (qw * qy - qz * qx))));
const ax = Math.cos(yaw), az = -Math.sin(yaw);   // along the creek
const cx = Math.sin(yaw), cz = Math.cos(yaw);    // across it
const c = trim.position;

/**
 * The trim box is the working area, and nothing may be planned outside it.
 *
 * The box is what the artist decided the piece is; a beat beyond its face sits in splats that
 * have been trimmed away, so there is nothing there to stand next to. An earlier version of
 * this file ran to along −10.8 m and y +2.79 m, both well outside, which put the last two
 * beats in empty space.
 */
const LIMIT = {
  along: trim.halfExtent.x - 0.15,
  across: trim.halfExtent.z - 0.15,
  yLo: c.y - trim.halfExtent.y + 0.1,
  yHi: c.y + trim.halfExtent.y - 0.1,
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
 * The phone hangs on a neck mount, so the camera rides at the visitor's sternum — about
 * 1.40 m above the ground — and it does not move when they turn their head. The path is
 * therefore the camera track itself, not a ground line with an offset applied later. Storing
 * ground and adding a height at render time meant three different places had to agree on what
 * that height was, and they did not.
 *
 * Heights below are the 5th-percentile ground from the measured profile — the bed, not the
 * leaves above it — and CHEST is added when the route is built.
 */
const CHEST = 1.40;
const PATH = [
  [ +7.2, +0.4, -2.01],   // downstream face of the box — the pool at the bottom
  [ +5.8, -0.5, -2.01],
  [ +4.4, +0.6, -2.00],
  [ +3.0, -0.6, -1.96],
  [ +1.6, +0.6, -1.90],
  [ +0.2, -0.7, -1.72],
  [ -1.2, +0.6, -1.62],
  [ -2.6, -0.6, -1.55],
  [ -4.0, +0.5, -1.48],
  [ -5.2, -0.4, -1.32],
  [ -6.2, +0.3, -1.09],   // the plunge pool at the foot of the falls
  [ -6.9,  0.0, -0.34],   // the step begins
  [ -7.3,  0.0, +0.55],   // climbing it, as far as the box reaches
];

/**
 * Six beats. The last two are the artist's instruction and they are what gives the piece its
 * ending: you lift yourself over the falls, and you spawn in the water above them. That is
 * the salmon story, and it is also the only place on this reach where a lift is physically
 * legible — there is a real step there to get over.
 */
const BEATS = [
  { id: 'tree',      along: +6.4, across: -1.2, y: -2.01,
    title: 'The tree',
    prompt: 'Come in under the branches where the water runs slow.',
    interaction: 'proximity', clip: 'tree-creek-waterplants-1', completion: null },

  { id: 'redd',      along: +3.6, across: +0.9, y: -1.97,
    title: 'The gravel bed',
    prompt: 'Get low enough to see into the gravel.',
    interaction: 'crouch', clip: 'chapter-1-new-life', completion: 'lay-egg' },

  { id: 'strider',   along: +0.6, across: -0.8, y: -1.78,
    title: 'Water striders',
    prompt: 'Something is moving on the surface.',
    interaction: 'catch', clip: 'strider', completion: 'eat-strider' },

  { id: 'heron',     along: -2.4, across: +1.1, y: -1.56,
    title: 'The heron',
    prompt: 'It has been waiting below the falls. Let some of the shoal go.',
    interaction: 'give', clip: 'heron', completion: null, givesFish: 12 },

  { id: 'falls',     along: -6.0, across: 0.0, y: -1.05,
    title: 'The falls',
    prompt: 'The water comes down here. Lift yourself over it.',
    interaction: 'lift', clip: 'chapter-4-returning-home', completion: 'jump' },

  { id: 'spawn',     along: -7.2, across: -0.2, y: +0.50,
    title: 'Above the falls',
    prompt: 'Over the lip. Still water on gravel — as far up as the scan reaches.',
    interaction: 'crouch', clip: 'chapter-5-rebirth', completion: 'lay-egg' },
];

const layers = (clip, g = { far: -14, mid: -8, intimate: -4 }) => ({
  far: { clipId: `${clip}--far`, gainDb: g.far, loop: true },
  mid: { clipId: `${clip}--mid`, gainDb: g.mid, loop: true },
  intimate: { clipId: `${clip}--intimate`, gainDb: g.intimate, loop: true },
});

const centreline = PATH.map(([along, across, ground], i) =>
  at(along, across, ground + CHEST, `path[${i}]`));

// Distance along the route, for gate geometry.
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

// Beats sit ~3.3 m apart on this reach, so the gates have to be tighter than the seed's.
const ENTER = 1.9, EXIT = ENTER * 1.6;

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
      exitRadiusM: +EXIT.toFixed(2),
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

// --- write it through the service so both stores agree -------------------
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

const updated = { ...draft, site: { ...draft.site, centreline }, beats };
updated.site.upstreamAxis = {
  kind: 'altitude',
  risesToward: 'the falls, at the −along end of the trim box, world about (−7, +4). '
             + 'Measured from a 3 m bed step over 2 horizontal metres, not from brightness.',
};

await put('/draft', updated);

console.log(`\n  path      ${centreline.length} points, ${total.toFixed(1)} m`);
console.log(`            from ${JSON.stringify(centreline[0])}`);
console.log(`            to   ${JSON.stringify(centreline.at(-1))}`);
console.log(`            climbing ${(centreline.at(-1).y - centreline[0].y).toFixed(2)} m\n`);
console.log(`  ${'beat'.padEnd(10)}${'s'.padStart(7)}  interaction  position`);
for (const b of beats) {
  console.log(`  ${b.id.padEnd(10)}${b.s.toFixed(1).padStart(7)}  ${b.interaction.padEnd(11)}  `
    + `(${b.position.x.toFixed(1)}, ${b.position.y.toFixed(1)}, ${b.position.z.toFixed(1)})`);
}
// Refuse to ship a layout that leaves the box. Better to fail here than to discover a beat
// standing in trimmed-away space on site.
const outside = [];
for (const p of [...centreline, ...beats.map((b) => b.position)]) {
  const dx = p.x - c.x, dz = p.z - c.z;
  const a = dx * ax + dz * az, r = dx * cx + dz * cz;
  if (Math.abs(a) > trim.halfExtent.x + 1e-3
      || Math.abs(r) > trim.halfExtent.z + 1e-3
      || p.y < c.y - trim.halfExtent.y - 1e-3
      || p.y > c.y + trim.halfExtent.y + 1e-3) {
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
  + ` (±${trim.halfExtent.x.toFixed(1)} along, ±${trim.halfExtent.z.toFixed(1)} across,`
  + ` y ${(c.y - trim.halfExtent.y).toFixed(2)}…${(c.y + trim.halfExtent.y).toFixed(2)})`);
console.log('');
