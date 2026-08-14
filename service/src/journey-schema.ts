/**
 * The journey manifest — the one contract the editor, the service and the iOS app share.
 *
 * Everything else in this project is free to change independently. This is not, which is why
 * it lives on its own and is versioned on its own.
 *
 * Three ideas shape it, each from a measured constraint rather than a preference:
 *
 * 1. Positions are metres in the VPS anchor's local frame. Never latitude/longitude. Under
 *    canopy GPS carries 3-5 m of error with a tail to 30-100 m, and our beats are 8-12 m
 *    apart, so lat/long cannot tell one beat from another. NSDK hands us an anchor Transform;
 *    content parented to it is correct by construction.
 *
 * 2. The reach is treated as a line. Each beat carries `s`, its distance along the creek
 *    centreline. A gate perpendicular to the creek discards all cross-stream noise, leaving
 *    only along-stream error to debounce, and makes "have I moved upstream" trivial because
 *    s is exactly the quantity that should only increase.
 *
 * 3. Ordered beats and unordered ambient sources are separate lists. The story is a sequence;
 *    the texture is a set. Keeping them apart in the document is what stops the runtime from
 *    having to guess which is which.
 *
 * The shapes this file checks FOR are declared once, in editor/src/types.ts, and imported here
 * as types only — the import is erased, so the service still runs from source with no build and
 * no dependency on the editor. What arrives at `validateJourney` is `unknown`, deliberately:
 * typing the input as the thing being checked for would be a lie, and would hide exactly the
 * class of bug this validator exists to catch.
 */

import type { Interaction, InteractionInfo, Layer, ValidationResult, Vec3 }
  from '../../editor/src/types.ts';

export const SCHEMA_VERSION = '2.0';

/** Interaction kinds. Only `catch` and `lift` need bespoke gesture detection. */
export const INTERACTIONS: Readonly<Record<Interaction, InteractionInfo>> = Object.freeze({
  proximity: {
    label: 'Draw near',
    hint: 'Arriving is the whole action. Used for the tree, and for thresholds.',
    needsGesture: false,
  },
  crouch: {
    label: 'Crouch and cut the redd',
    hint: 'Drop ~0.4 m, then a single lateral roll of the phone — how a female salmon actually digs.',
    needsGesture: true,
  },
  catch: {
    label: 'Take an insect',
    hint: 'A short lunge and stop, read from accelerometer and step cadence.',
    needsGesture: true,
  },
  give: {
    label: 'Give part of the shoal',
    hint: 'The only interaction that permanently changes state. The shoal is thinner afterwards.',
    needsGesture: false,
  },
  lift: {
    label: 'Lift yourself over',
    hint: 'Rise ~0.4 m and hold. A sustained plateau, not a ballistic jump — easier to detect and far less conspicuous.',
    needsGesture: true,
  },
});

/** Audio proximity layers. Distance is carried by content, not by gain: at 5-20 m the whole
 *  inverse-square budget is only ~12 dB, which against a 47-67 dB(A) park floor reads as
 *  "slightly louder" rather than as arrival. So each source is three different recordings. */
export const LAYERS: readonly Layer[] = Object.freeze(['far', 'mid', 'intimate'] as const);

/** What an untrusted object looks like once we know it is an object at all. */
type UnknownRecord = Record<string, unknown>;

const isFiniteNumber = (v: unknown): v is number => typeof v === 'number' && Number.isFinite(v);
const isString = (v: unknown): v is string => typeof v === 'string';
const isNonEmptyString = (v: unknown): v is string => isString(v) && v.trim().length > 0;
const isPlainObject = (v: unknown): v is UnknownRecord =>
  v !== null && typeof v === 'object' && !Array.isArray(v);
/** `Array.isArray` narrows to `any[]`, which would quietly re-open everything below it. */
const isArray = (v: unknown): v is unknown[] => Array.isArray(v);

/** `value?.key` on something that may be any shape at all. Missing, wrong type — undefined. */
const prop = (value: unknown, key: string): unknown =>
  isPlainObject(value) ? value[key] : undefined;

/**
 * `x ?? 0`, kept verbatim from the JavaScript rather than tightened.
 *
 * It only replaces nullish values, so a non-numeric exitRadiusM — which validateTrigger has
 * already recorded as an error, but which does not stop the warning pass that uses this —
 * survives into the arithmetic and makes `reach` a string, whose `.toFixed` then throws. Same
 * family as the `beats` hole noted further down. Left alone because fixing it changes behaviour.
 */
const orZero = (v: unknown): number => (v ?? 0) as number;

interface NumberBounds {
  min?: number;
  max?: number;
  label: string;
}

class Validator {
  errors: string[] = [];
  path: string[] = [];

  at(key: string, fn: () => void): void {
    this.path.push(key);
    try {
      fn();
    } finally {
      this.path.pop();
    }
  }

  fail(message: string): void {
    const where = this.path.length ? this.path.join('.') : '(root)';
    this.errors.push(`${where}: ${message}`);
  }

  require(condition: boolean, message: string): boolean {
    if (!condition) this.fail(message);
    return condition;
  }

  number(value: unknown, { min = -Infinity, max = Infinity, label }: NumberBounds): boolean {
    if (!isFiniteNumber(value)) {
      this.fail(`${label} must be a finite number, got ${JSON.stringify(value)}`);
      return false;
    }
    if (value < min || value > max) {
      this.fail(`${label} must be between ${min} and ${max}, got ${value}`);
      return false;
    }
    return true;
  }
}

function validateVector3(v: Validator, name: string, val: unknown): void {
  if (!isPlainObject(val)) {
    v.fail(`${name} must be an object with x, y, z in metres`);
    return;
  }
  for (const axis of ['x', 'y', 'z']) {
    v.number(val[axis], { min: -10000, max: 10000, label: `${name}.${axis}` });
  }
}

function validateAudioLayer(v: Validator, layer: string, val: unknown): void {
  if (val === undefined || val === null) return;
  if (!isPlainObject(val)) {
    v.fail(`audio.${layer} must be an object`);
    return;
  }
  v.at(`audio.${layer}`, () => {
    v.require(isNonEmptyString(val.clipId), 'clipId is required');
    if (val.gainDb !== undefined) {
      v.number(val.gainDb, { min: -80, max: 12, label: 'gainDb' });
    }
    if (val.loop !== undefined && typeof val.loop !== 'boolean') {
      v.fail('loop must be a boolean');
    }
  });
}

function validateTrigger(v: Validator, trigger: unknown, index: number): void {
  if (!isPlainObject(trigger)) {
    v.fail('trigger is required');
    return;
  }
  v.at('trigger', () => {
    const enter = trigger.enterRadiusM;
    const exit = trigger.exitRadiusM;

    v.number(enter, { min: 0.5, max: 30, label: 'enterRadiusM' });
    v.number(exit, { min: 0.5, max: 40, label: 'exitRadiusM' });

    // Hysteresis is not optional. A single radius makes a beat flicker on and off when
    // someone stands near its boundary, and VPS pose jitter guarantees they will.
    if (isFiniteNumber(enter) && isFiniteNumber(exit) && exit <= enter) {
      v.fail(
        `exitRadiusM (${exit}) must be greater than enterRadiusM (${enter}) — ` +
          'equal radii cause the beat to fire and silence repeatedly at the boundary'
      );
    }

    v.number(trigger.dwellSeconds, { min: 0, max: 20, label: 'dwellSeconds' });

    // Minimum hold, not hysteresis, is what actually prevents thrash at 8-12 m spacing.
    // It converts "am I inside the zone?" into "which beat am I performing?".
    v.number(trigger.minimumHoldSeconds, { min: 0, max: 300, label: 'minimumHoldSeconds' });

    if (trigger.requiresPreviousComplete !== undefined
        && typeof trigger.requiresPreviousComplete !== 'boolean') {
      v.fail('requiresPreviousComplete must be a boolean');
    }
  });
}

function validateBeat(v: Validator, beat: unknown, index: number, seenIds: Set<string>): void {
  if (!isPlainObject(beat)) {
    v.fail(`beats[${index}] must be an object`);
    return;
  }

  v.at(`beats[${index}]`, () => {
    const id = beat.id;
    if (isNonEmptyString(id)) {
      if (seenIds.has(id)) v.fail(`duplicate id "${id}"`);
      seenIds.add(id);
    } else {
      v.fail('id is required');
    }
    v.require(isNonEmptyString(beat.title), 'title is required');

    if (!Object.hasOwn(INTERACTIONS, beat.interaction as PropertyKey)) {
      v.fail(
        `interaction must be one of ${Object.keys(INTERACTIONS).join(', ')}, ` +
          `got ${JSON.stringify(beat.interaction)}`
      );
    }

    validateVector3(v, 'position', beat.position);

    // s is the along-creek coordinate the runtime actually gates on.
    v.number(beat.s, { min: 0, max: 5000, label: 's' });

    validateTrigger(v, beat.trigger, index);

    const audio = beat.audio;
    if (audio !== undefined) {
      if (!isPlainObject(audio)) {
        v.fail('audio must be an object');
      } else {
        for (const layer of LAYERS) validateAudioLayer(v, layer, audio[layer]);
        validateAudioLayer(v, 'completion', audio.completion);
        const hasAny = LAYERS.some((l) => isPlainObject(audio[l]));
        if (!hasAny) {
          v.fail('at least one of far/mid/intimate must be present — distance is carried by content, not gain');
        }
      }
    }

    if (beat.interaction === 'give') {
      if (beat.givesFish === undefined) {
        v.fail('a `give` beat must state givesFish — how much of the shoal is taken');
      } else {
        v.number(beat.givesFish, { min: 1, max: 10000, label: 'givesFish' });
      }
    }
  });
}

function validateAmbient(v: Validator, source: unknown, index: number, seenIds: Set<string>): void {
  if (!isPlainObject(source)) {
    v.fail(`ambient[${index}] must be an object`);
    return;
  }
  v.at(`ambient[${index}]`, () => {
    const id = source.id;
    if (isNonEmptyString(id)) {
      if (seenIds.has(id)) v.fail(`duplicate id "${id}"`);
      seenIds.add(id);
    } else {
      v.fail('id is required');
    }
    validateVector3(v, 'position', source.position);
    v.number(source.audibleRadiusM, { min: 1, max: 200, label: 'audibleRadiusM' });
    const audio = source.audio;
    if (audio !== undefined) {
      if (!isPlainObject(audio)) v.fail('audio must be an object');
      else for (const layer of LAYERS) validateAudioLayer(v, layer, audio[layer]);
    }
  });
}

/**
 * Validate a journey manifest.
 *
 * The parameter is `unknown` because the caller is a PUT body, a file on disk or a tool's
 * output — never something already known to be a JourneyDocument.
 */
export function validateJourney(doc: unknown): ValidationResult {
  const v = new Validator();
  const warnings: string[] = [];

  if (!isPlainObject(doc)) {
    return { ok: false, errors: ['(root): manifest must be a JSON object'], warnings };
  }

  if (doc.schemaVersion !== SCHEMA_VERSION) {
    v.fail(`schemaVersion must be "${SCHEMA_VERSION}", got ${JSON.stringify(doc.schemaVersion)}`);
  }
  v.require(isNonEmptyString(doc.journeyId), 'journeyId is required');
  v.require(isNonEmptyString(doc.title), 'title is required');
  v.number(doc.revision, { min: 0, max: 1e9, label: 'revision' });

  // --- site -------------------------------------------------------------
  const site = doc.site;
  if (!isPlainObject(site)) {
    v.fail('site is required');
  } else {
    v.at('site', () => {
      const s = site;
      v.require(isNonEmptyString(s.slug), 'slug is required');
      v.require(isNonEmptyString(s.nianticSiteId), 'nianticSiteId is required');
      v.require(isNonEmptyString(s.nianticOrgId), 'nianticOrgId is required');

      // The payload may be fetched at runtime via the NSDK Sites API instead of baked in,
      // so it is optional — but if it is baked, the asset it came from must be recorded,
      // because re-promoting an asset can move the origin under authored coordinates.
      if (isNonEmptyString(s.anchorPayload) && !isNonEmptyString(s.vpsAssetId)) {
        v.fail('anchorPayload is baked in but vpsAssetId is missing — '
             + 'without it we cannot detect that the site has been re-promoted');
      }

      const centreline = s.centreline;
      if (!isArray(centreline) || centreline.length < 2) {
        v.fail('centreline must be an array of at least 2 points defining the creek axis');
      } else {
        centreline.forEach((p, i) => validateVector3(v, `centreline[${i}]`, p));
      }
    });
  }

  // --- editor frame -----------------------------------------------------
  const editorFrame = doc.editorFrame;
  if (!isPlainObject(editorFrame)) {
    v.fail('editorFrame is required');
  } else {
    v.at('editorFrame', () => {
      const f = editorFrame;
      if (typeof f.calibrated !== 'boolean') {
        v.fail('calibrated must be a boolean');
      }
      if (f.splatFile !== undefined && !isString(f.splatFile)) {
        v.fail('splatFile must be a string');
      }
      validateVector3(v, 'translation', f.translation);
      if (!isArray(f.rotation) || f.rotation.length !== 4
          || !f.rotation.every(isFiniteNumber)) {
        v.fail('rotation must be a quaternion [x, y, z, w]');
      }
      v.number(f.scale, { min: 0.001, max: 1000, label: 'scale' });

      // Runtime display trim. Deliberately not baked into the scan asset: which floaters
      // count as noise is an authoring judgement, and it has to be adjustable in a second.
      const trim = f.trim;
      if (trim !== undefined) {
        if (!isPlainObject(trim)) {
          v.fail('trim must be an object');
        } else if (trim.min && trim.max) {
          // Legacy axis-aligned box. Still accepted; the editor converts it on load.
          validateVector3(v, 'trim.min', trim.min);
          validateVector3(v, 'trim.max', trim.max);
          if (typeof trim.enabled !== 'boolean') v.fail('trim.enabled must be a boolean');
        } else {
          // Oriented box: the creek runs diagonally, so an axis-aligned trim cannot follow it.
          validateVector3(v, 'trim.position', trim.position);
          validateVector3(v, 'trim.halfExtent', trim.halfExtent);
          if (!isArray(trim.rotation) || trim.rotation.length !== 4
              || !trim.rotation.every(isFiniteNumber)) {
            v.fail('trim.rotation must be a quaternion [x, y, z, w]');
          }
          if (typeof trim.enabled !== 'boolean') v.fail('trim.enabled must be a boolean');
        }
      }
    });
  }

  // --- beats ------------------------------------------------------------
  const beats = doc.beats;
  if (!isArray(beats)) {
    v.fail('beats must be an array');
  } else {
    const ids = new Set<string>();
    beats.forEach((b, i) => validateBeat(v, b, i, ids));

    // Order is meaningful, so s should increase along it. A beat that sits upstream of the
    // next one means the visitor is asked to walk backwards, which is almost always a
    // placement mistake rather than an intention.
    for (let i = 1; i < beats.length; i += 1) {
      const prev = prop(beats[i - 1], 's');
      const cur = prop(beats[i], 's');
      if (isFiniteNumber(prev) && isFiniteNumber(cur) && cur < prev) {
        warnings.push(
          `beats[${i}] "${String(prop(beats[i], 'id'))}" sits downstream of the beat before it `
          + `(s=${cur.toFixed(1)} m vs ${prev.toFixed(1)} m) — the journey doubles back here`
        );
      }
    }

    // Overlapping exit bands mean two beats can be armed at once. The runtime resolves this
    // winner-take-all, but it is worth telling the author.
    for (let i = 1; i < beats.length; i += 1) {
      const a = beats[i - 1];
      const b = beats[i];
      const aS = prop(a, 's');
      const bS = prop(b, 's');
      if (!isFiniteNumber(aS) || !isFiniteNumber(bS)) continue;
      const gap = Math.abs(bS - aS);
      const reach = orZero(prop(prop(a, 'trigger'), 'exitRadiusM'))
                  + orZero(prop(prop(b, 'trigger'), 'exitRadiusM'));
      if (gap < reach) {
        warnings.push(
          `beats "${String(prop(a, 'id'))}" and "${String(prop(b, 'id'))}" have overlapping exit bands `
          + `(${gap.toFixed(1)} m apart, ${reach.toFixed(1)} m of combined reach)`
        );
      }
    }
  }

  // --- ambient ----------------------------------------------------------
  const ambient = doc.ambient;
  if (ambient !== undefined) {
    if (!isArray(ambient)) {
      v.fail('ambient must be an array');
    } else {
      const ids = new Set<string>();
      ambient.forEach((a, i) => validateAmbient(v, a, i, ids));
    }
  }

  // --- shoal ------------------------------------------------------------
  const shoal = doc.shoal;
  if (!isPlainObject(shoal)) {
    v.fail('shoal is required');
  } else {
    v.at('shoal', () => {
      v.number(shoal.startingCount, { min: 1, max: 10000, label: 'startingCount' });
      v.number(shoal.minimumCount, { min: 0, max: 10000, label: 'minimumCount' });
      if (isFiniteNumber(shoal.startingCount) && isFiniteNumber(shoal.minimumCount)
          && shoal.minimumCount > shoal.startingCount) {
        v.fail('minimumCount cannot exceed startingCount');
      }
    });
  }

  // --- cross-cutting warnings -------------------------------------------
  if (doc.editorFrame && prop(doc.editorFrame, 'calibrated') === false) {
    warnings.push(
      'editorFrame.calibrated is false — coordinates are provisional. '
      + 'The app will run this in simulation but must refuse it on device.'
    );
  }

  // `?? []` only catches null and undefined, so a truthy non-array — `{}` or a string, which is
  // exactly what a malformed PUT carries — reached .filter and threw. validateJourney is called
  // on whatever the editor sends, so that turned a bad request into a 500 instead of the 422
  // with reasons the client is built to display.
  const giveTotal = (isArray(doc.beats) ? doc.beats : [])
    .filter((b) => prop(b, 'interaction') === 'give' && isFiniteNumber(prop(b, 'givesFish')))
    .reduce((sum: number, b) => sum + (prop(b, 'givesFish') as number), 0);
  if (isPlainObject(doc.shoal) && isFiniteNumber(doc.shoal.startingCount)) {
    const remaining = doc.shoal.startingCount - giveTotal;
    if (isFiniteNumber(doc.shoal.minimumCount) && remaining < doc.shoal.minimumCount) {
      warnings.push(
        `the shoal is given away past its minimum: ${doc.shoal.startingCount} `
        + `− ${giveTotal} = ${remaining}, below minimumCount ${doc.shoal.minimumCount}`
      );
    }
  }

  return { ok: v.errors.length === 0, errors: v.errors, warnings };
}

/** Where a point falls on the centreline: distance along it, and how far off it. */
export interface CentrelineProjection {
  /** Distance along the polyline from its start, in metres. */
  s: number;
  /** Perpendicular distance from the line. */
  lateral: number;
  /** Index of the segment the point projected onto. */
  segment: number;
}

/** Distance along the centreline polyline, and lateral offset, for an anchor-local point. */
export function projectToCentreline(
  point: Vec3,
  centreline: readonly Vec3[],
): CentrelineProjection {
  let best: CentrelineProjection = { s: 0, lateral: Infinity, segment: 0 };
  let travelled = 0;

  for (let i = 0; i < centreline.length - 1; i += 1) {
    const a = centreline[i]!;
    const b = centreline[i + 1]!;
    const abx = b.x - a.x, aby = b.y - a.y, abz = b.z - a.z;
    const segLen = Math.hypot(abx, aby, abz);
    if (segLen < 1e-6) continue;

    const apx = point.x - a.x, apy = point.y - a.y, apz = point.z - a.z;
    let t = (apx * abx + apy * aby + apz * abz) / (segLen * segLen);
    t = Math.max(0, Math.min(1, t));

    const cx = a.x + abx * t, cy = a.y + aby * t, cz = a.z + abz * t;
    const lateral = Math.hypot(point.x - cx, point.y - cy, point.z - cz);

    if (lateral < best.lateral) {
      best = { s: travelled + segLen * t, lateral, segment: i };
    }
    travelled += segLen;
  }

  return best;
}

/** Total length of a centreline polyline, in metres. */
export function centrelineLength(centreline: readonly Vec3[]): number {
  let total = 0;
  for (let i = 0; i < centreline.length - 1; i += 1) {
    const a = centreline[i]!, b = centreline[i + 1]!;
    total += Math.hypot(b.x - a.x, b.y - a.y, b.z - a.z);
  }
  return total;
}
