/**
 * Centreline geometry, shared between the editor and the runtime reasoning.
 *
 * The reach is treated as a line rather than a field. Every position is reduced to `s`,
 * distance along the creek, plus a lateral offset that never triggers anything. A gate
 * perpendicular to the creek axis throws away all cross-stream error, which matters when
 * beats are 6-10 m apart and pose noise is a metre or more.
 */

export function projectToCentreline(point, centreline) {
  let best = { s: 0, lateral: Infinity, closest: null, segment: 0, t: 0 };
  let travelled = 0;

  for (let i = 0; i < centreline.length - 1; i += 1) {
    const a = centreline[i];
    const b = centreline[i + 1];
    const abx = b.x - a.x, aby = b.y - a.y, abz = b.z - a.z;
    const segLen = Math.hypot(abx, aby, abz);
    if (segLen < 1e-6) continue;

    const apx = point.x - a.x, apy = point.y - a.y, apz = point.z - a.z;
    let t = (apx * abx + apy * aby + apz * abz) / (segLen * segLen);
    t = Math.max(0, Math.min(1, t));

    const cx = a.x + abx * t, cy = a.y + aby * t, cz = a.z + abz * t;
    const lateral = Math.hypot(point.x - cx, point.y - cy, point.z - cz);

    if (lateral < best.lateral) {
      best = {
        s: travelled + segLen * t,
        lateral,
        closest: { x: cx, y: cy, z: cz },
        segment: i,
        t,
      };
    }
    travelled += segLen;
  }
  return best;
}

/** The inverse: a point on the centreline at distance `s` from its start. */
export function pointAtS(s, centreline) {
  let travelled = 0;
  for (let i = 0; i < centreline.length - 1; i += 1) {
    const a = centreline[i];
    const b = centreline[i + 1];
    const segLen = Math.hypot(b.x - a.x, b.y - a.y, b.z - a.z);
    if (segLen < 1e-6) continue;
    if (travelled + segLen >= s || i === centreline.length - 2) {
      const t = Math.max(0, Math.min(1, (s - travelled) / segLen));
      return {
        x: a.x + (b.x - a.x) * t,
        y: a.y + (b.y - a.y) * t,
        z: a.z + (b.z - a.z) * t,
      };
    }
    travelled += segLen;
  }
  return { ...centreline.at(-1) };
}

export function centrelineLength(centreline) {
  let total = 0;
  for (let i = 0; i < centreline.length - 1; i += 1) {
    const a = centreline[i], b = centreline[i + 1];
    total += Math.hypot(b.x - a.x, b.y - a.y, b.z - a.z);
  }
  return total;
}

/** Unit tangent at distance s — the direction "upstream" points at that place. */
export function tangentAtS(s, centreline) {
  const eps = 0.25;
  const a = pointAtS(Math.max(0, s - eps), centreline);
  const b = pointAtS(Math.min(centrelineLength(centreline), s + eps), centreline);
  const dx = b.x - a.x, dy = b.y - a.y, dz = b.z - a.z;
  const len = Math.hypot(dx, dy, dz) || 1;
  return { x: dx / len, y: dy / len, z: dz / len };
}

/**
 * Which beats are armed at position s, and which one wins.
 *
 * Winner-take-all over a single state variable, not six independent zone monitors. Six
 * monitors interleave and double-fire near boundaries; one state machine cannot. This
 * mirrors what the runtime does, so what the scrubber shows is what the phone will do.
 */
export function evaluateAt(s, beats, { highWaterMark = -1, respectOrder = true } = {}) {
  const armed = [];

  beats.forEach((beat, index) => {
    const enter = beat.trigger?.enterRadiusM ?? 0;
    const exit = beat.trigger?.exitRadiusM ?? enter;
    const distance = Math.abs(s - (beat.s ?? 0));

    if (distance > exit) return;

    const gatedByOrder = respectOrder
      && beat.trigger?.requiresPreviousComplete
      && index > highWaterMark + 1;

    armed.push({
      beat,
      index,
      distance,
      inEnter: distance <= enter,
      inExit: distance <= exit,
      blocked: gatedByOrder,
      // Proximity within the enter band, normalised — the value that would drive the
      // far/mid/intimate blend.
      proximity: enter > 0 ? Math.max(0, 1 - distance / exit) : 0,
    });
  });

  const eligible = armed.filter((a) => a.inEnter && !a.blocked);
  eligible.sort((a, b) => a.distance - b.distance);

  return { armed, winner: eligible[0] ?? null };
}
