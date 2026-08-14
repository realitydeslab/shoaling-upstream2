/**
 * The manifest contract, which is the only thing the editor, the service and the app all agree on.
 *
 * Everything downstream trusts this validator. The store refuses a write that fails it, so a hole
 * here is not a bad error message — it is a draft that reaches a phone in a creek and behaves in a
 * way nobody authored. The distinction the suite leans on hardest is errors versus warnings:
 * errors are things the runtime cannot act on at all, warnings are authoring judgements the
 * validator is not entitled to overrule. Getting a warning promoted to an error would block real
 * work; getting an error demoted to a warning would ship it.
 */

import test from 'node:test';
import assert from 'node:assert/strict';

import {
  validateJourney,
  INTERACTIONS,
  LAYERS,
  SCHEMA_VERSION,
} from '../service/src/journey-schema.mjs';
import { makeJourney, makeBeat } from './helpers.mjs';

/** Build the passing fixture, break one thing, validate. */
function check(mutate) {
  const journey = makeJourney();
  mutate?.(journey);
  return validateJourney(journey);
}

const hasError = (result, fragment) =>
  result.errors.some((e) => e.includes(fragment));

const hasWarning = (result, fragment) =>
  result.warnings.some((w) => w.includes(fragment));

/** Assert a mutation is rejected, and rejected for the reason we meant to test. */
function rejects(mutate, fragment) {
  const result = check(mutate);
  assert.equal(result.ok, false, `expected rejection, got ${JSON.stringify(result.errors)}`);
  assert.ok(hasError(result, fragment),
    `no error mentioning "${fragment}"; got ${JSON.stringify(result.errors)}`);
  return result;
}

// --- errors versus warnings ---------------------------------------------

test('the fixture journey passes', () => {
  const result = check();
  assert.equal(result.ok, true, result.errors.join('\n'));
  assert.deepEqual(result.errors, []);
});

test('warnings do not make a journey invalid', () => {
  // The fixture is deliberately uncalibrated and its two beats overlap, so it arrives flagged.
  // A warning is advice to the author; only an error stops the document being saved.
  const result = check();
  assert.ok(result.warnings.length >= 2);
  assert.equal(result.ok, true);
});

test('an uncalibrated editor frame is a warning, not an error', () => {
  // This is the warning that decides whether the piece can run outdoors: an uncalibrated frame
  // means the coordinates are provisional, so the app simulates it and refuses it on device. It
  // must stay a warning, because a journey is authored uncalibrated and has to be saveable.
  const result = check();
  assert.equal(result.ok, true);
  assert.ok(hasWarning(result, 'calibrated is false'));
  assert.ok(hasWarning(result, 'refuse it on device'));
});

test('a calibrated journey carries no calibration warning', () => {
  const result = check((j) => { j.editorFrame.calibrated = true; });
  assert.equal(result.ok, true);
  assert.ok(!hasWarning(result, 'calibrated'));
});

test('a missing calibrated flag is an error rather than an assumed false', () => {
  // Absence must not read as "not yet calibrated": that would quietly downgrade a broken
  // editorFrame into the warning path and let it through.
  rejects((j) => { delete j.editorFrame.calibrated; }, 'calibrated must be a boolean');
  rejects((j) => { j.editorFrame.calibrated = 'yes'; }, 'calibrated must be a boolean');
});

test('beats whose exit bands overlap are a warning', () => {
  // Two armed beats at once is resolved winner-take-all at runtime, so it is legal — but at
  // 8-12 m spacing it is usually a placement slip, and the author is the one who can tell.
  const result = check();
  assert.ok(hasWarning(result, 'overlapping exit bands'));

  const spread = check((j) => { j.beats = [makeBeat('one', 2), makeBeat('two', 40)]; });
  assert.ok(!hasWarning(spread, 'overlapping exit bands'));
});

test('a journey that doubles back on itself is a warning', () => {
  // Beat order is the order of the story, and s only ever increases as the visitor walks up the
  // creek. A later beat with a smaller s asks them to walk back downstream.
  const result = check((j) => { j.beats[1].s = 1; });
  assert.equal(result.ok, true);
  assert.ok(hasWarning(result, 'doubles back'));
});

// --- malformed input ----------------------------------------------------

test('input that is not an object at all is rejected, not thrown at', () => {
  for (const bad of [null, undefined, 'a string', 42, [], true]) {
    const result = validateJourney(bad);
    assert.equal(result.ok, false, `${JSON.stringify(bad)} should not validate`);
    assert.deepEqual(result.errors, ['(root): manifest must be a JSON object']);
    assert.deepEqual(result.warnings, []);
  }
});

test('a beats value that is not an array throws instead of failing validation', () => {
  // Documenting a bug, not endorsing it. `beats` is checked with Array.isArray and the error is
  // recorded, but validation carries on and the give-total pass calls .filter on it regardless,
  // so anything truthy and non-array crashes the validator instead of being reported. The store
  // calls this on whatever the editor PUTs, so malformed JSON takes out the request rather than
  // coming back as INVALID_JOURNEY.
  assert.throws(() => check((j) => { j.beats = {}; }), TypeError);
  assert.throws(() => check((j) => { j.beats = 'one, two'; }), TypeError);

  // Only the nullish cases reach the intended error.
  rejects((j) => { j.beats = null; }, 'beats must be an array');
  rejects((j) => { delete j.beats; }, 'beats must be an array');
});

test('a beat that is not an object is rejected by index', () => {
  rejects((j) => { j.beats = [null]; }, 'beats[0] must be an object');
});

test('unknown keys are ignored rather than rejected', () => {
  // The manifest is forwards-compatible on purpose: an older service must not refuse a document
  // a newer editor wrote.
  const result = check((j) => {
    j.experimentalThing = 42;
    j.beats[0].nonsense = true;
  });
  assert.equal(result.ok, true);
});

// --- interactions -------------------------------------------------------

test('every interaction kind is accepted', () => {
  for (const kind of Object.keys(INTERACTIONS)) {
    const extra = kind === 'give' ? { givesFish: 2 } : {};
    const result = check((j) => {
      j.beats = [makeBeat(kind, 2, { interaction: kind, ...extra })];
    });
    assert.equal(result.ok, true, `${kind} was rejected: ${result.errors.join('; ')}`);
  }
});

test('a beat with an unknown interaction is rejected', () => {
  const result = rejects((j) => { j.beats[0].interaction = 'teleport'; }, 'interaction must be one of');
  // The message lists the legal kinds, because the person reading it is usually hand-editing JSON.
  for (const kind of Object.keys(INTERACTIONS)) {
    assert.ok(hasError(result, kind), `${kind} missing from the error message`);
  }
});

test('a beat with no interaction at all is rejected', () => {
  rejects((j) => { delete j.beats[0].interaction; }, 'interaction must be one of');
});

test('a give beat must state how much of the shoal it takes', () => {
  // `give` is the only interaction that permanently changes state, so the amount cannot default.
  rejects((j) => { j.beats[0].interaction = 'give'; }, 'must state givesFish');
  rejects((j) => {
    j.beats[0].interaction = 'give';
    j.beats[0].givesFish = 0;
  }, 'givesFish must be between 1 and 10000');
});

test('givesFish on a non-give beat is ignored, and does not count against the shoal', () => {
  const result = check((j) => { j.beats[0].givesFish = 500; });
  assert.equal(result.ok, true);
  assert.ok(!hasWarning(result, 'given away past its minimum'));
});

test('giving the shoal away past its minimum is a warning', () => {
  // Arithmetic the author cannot easily do in their head across a whole journey, but a legal
  // state to save mid-edit — the fix might be to raise startingCount rather than give less.
  const result = check((j) => {
    j.beats[0].interaction = 'give';
    j.beats[0].givesFish = 35;   // 40 - 35 = 5, under the minimum of 8
  });
  assert.equal(result.ok, true);
  assert.ok(hasWarning(result, 'given away past its minimum'));
});

// --- trigger geometry ---------------------------------------------------

test('an exit radius equal to the enter radius is rejected', () => {
  // Hysteresis is not optional. With one radius, VPS pose jitter alone re-crosses the boundary
  // and the beat fires and silences on the spot.
  rejects((j) => {
    j.beats[0].trigger.exitRadiusM = j.beats[0].trigger.enterRadiusM;
  }, 'must be greater than enterRadiusM');
});

test('an exit radius inside the enter radius is rejected', () => {
  rejects((j) => { j.beats[0].trigger.exitRadiusM = 1.0; }, 'must be greater than enterRadiusM');
});

test('a missing exit radius is caught before the hysteresis check', () => {
  const result = rejects((j) => { delete j.beats[0].trigger.exitRadiusM; },
    'exitRadiusM must be a finite number');
  assert.ok(!hasError(result, 'must be greater than enterRadiusM'),
    'an absent radius should not also produce a comparison error');
});

test('negative radii are rejected on both gates', () => {
  const result = check((j) => {
    j.beats[0].trigger.enterRadiusM = -2;
    j.beats[0].trigger.exitRadiusM = -1;
  });
  assert.equal(result.ok, false);
  assert.ok(hasError(result, 'enterRadiusM must be between 0.5 and 30'));
  assert.ok(hasError(result, 'exitRadiusM must be between 0.5 and 40'));
});

test('a trigger without a dwell or a minimum hold is rejected', () => {
  // minimumHoldSeconds, not the hysteresis band, is what actually stops thrash at 8-12 m beat
  // spacing: it turns "am I inside this zone?" into "which beat am I performing?". Neither
  // timing may be left to a runtime default.
  rejects((j) => { delete j.beats[0].trigger.dwellSeconds; },
    'dwellSeconds must be a finite number');
  rejects((j) => { delete j.beats[0].trigger.minimumHoldSeconds; },
    'minimumHoldSeconds must be a finite number');
});

test('zero dwell is legal but a negative one is not', () => {
  assert.equal(check((j) => { j.beats[0].trigger.dwellSeconds = 0; }).ok, true);
  rejects((j) => { j.beats[0].trigger.dwellSeconds = -1; }, 'dwellSeconds must be between');
});

test('a beat with no trigger at all is rejected', () => {
  rejects((j) => { delete j.beats[0].trigger; }, 'trigger is required');
});

test('requiresPreviousComplete must be a boolean when present', () => {
  rejects((j) => { j.beats[0].trigger.requiresPreviousComplete = 'yes'; },
    'requiresPreviousComplete must be a boolean');
  assert.equal(check((j) => { delete j.beats[0].trigger.requiresPreviousComplete; }).ok, true);
});

// --- beat identity and placement ----------------------------------------

test('duplicate beat ids are rejected', () => {
  // Ids key completion state on the device; two beats sharing one means finishing either marks
  // both done.
  rejects((j) => { j.beats[1].id = j.beats[0].id; }, 'duplicate id "one"');
});

test('a beat needs an id, a title, a position and an s', () => {
  rejects((j) => { delete j.beats[0].id; }, 'id is required');
  rejects((j) => { j.beats[0].id = '   '; }, 'id is required');
  rejects((j) => { delete j.beats[0].title; }, 'title is required');
  rejects((j) => { delete j.beats[0].position; }, 'position must be an object with x, y, z');
  rejects((j) => { delete j.beats[0].s; }, 's must be a finite number');
});

test('a position is metres in the anchor frame, so it is bounded and finite', () => {
  // The bound is a sanity check on units: a value in that range is someone having pasted
  // lat/long or millimetres into a field that means metres from the VPS anchor.
  rejects((j) => { j.beats[0].position.x = 20000; }, 'position.x must be between -10000 and 10000');
  rejects((j) => { j.beats[0].position.y = NaN; }, 'position.y must be a finite number');
  rejects((j) => { j.beats[0].position = [1, 2, 3]; },
    'position must be an object with x, y, z');
});

test('a negative distance along the creek is rejected', () => {
  rejects((j) => { j.beats[0].s = -1; }, 's must be between 0 and 5000');
});

test('an empty beats array is legal', () => {
  // A journey under construction has a site, a frame and a shoal before it has any beats, and it
  // has to be saveable in that state or the editor cannot create one.
  const result = check((j) => { j.beats = []; });
  assert.equal(result.ok, true);
});

// --- audio --------------------------------------------------------------

test('LAYERS is the three recordings a source is made of', () => {
  // Three separate recordings, not one clip at three gains: across 5-20 m the whole
  // inverse-square budget is about 12 dB, which over a park noise floor reads as "slightly
  // louder" rather than as arrival.
  assert.deepEqual([...LAYERS], ['far', 'mid', 'intimate']);
});

test('a beat needs at least one proximity layer if it has audio at all', () => {
  rejects((j) => { j.beats[0].audio = {}; }, 'at least one of far/mid/intimate');
  // A completion clip alone does not count — it plays after the interaction, not on approach.
  rejects((j) => { j.beats[0].audio = { completion: { clipId: 'done' } }; },
    'at least one of far/mid/intimate');
});

test('any one of the three layers satisfies the requirement', () => {
  for (const layer of LAYERS) {
    const result = check((j) => { j.beats[0].audio = { [layer]: { clipId: `clip--${layer}` } }; });
    assert.equal(result.ok, true, `${layer} alone was rejected: ${result.errors.join('; ')}`);
  }
});

test('a beat with no audio block at all is legal', () => {
  // Silent beats exist — a threshold that is only a visual arrival. The layer requirement bites
  // only once a beat claims to have sound.
  assert.equal(check((j) => { delete j.beats[0].audio; }).ok, true);
});

test('every audio layer must name a clip', () => {
  rejects((j) => { delete j.beats[0].audio.far.clipId; }, 'audio.far: clipId is required');
  rejects((j) => { j.beats[0].audio.mid.clipId = '  '; }, 'audio.mid: clipId is required');
});

test('a completion clip is optional but validated like any other', () => {
  const ok = check((j) => {
    j.beats[0].audio.completion = { clipId: 'one--done', gainDb: -3, loop: false };
  });
  assert.equal(ok.ok, true, ok.errors.join('; '));
  rejects((j) => { j.beats[0].audio.completion = { gainDb: -3 }; },
    'audio.completion: clipId is required');
});

test('layer gain is bounded and loop is a boolean', () => {
  rejects((j) => { j.beats[0].audio.mid.gainDb = 13; }, 'gainDb must be between -80 and 12');
  rejects((j) => { j.beats[0].audio.mid.loop = 'yes'; }, 'loop must be a boolean');
});

test('an audio block that is not an object is rejected', () => {
  rejects((j) => { j.beats[0].audio = 'clip--far'; }, 'audio must be an object');
  rejects((j) => { j.beats[0].audio.far = 'clip--far'; }, 'audio.far must be an object');
});

test('a layer set to null is treated as absent rather than as an error', () => {
  // Round-tripping through JSON turns a cleared layer into null, and the author is allowed to
  // clear one — but nulling all three empties the beat past the layer requirement.
  assert.equal(check((j) => { j.beats[0].audio.far = null; }).ok, true);
  rejects((j) => {
    for (const layer of LAYERS) j.beats[0].audio[layer] = null;
  }, 'at least one of far/mid/intimate');
});

// --- site ---------------------------------------------------------------

test('the top-level fields the runtime cannot start without are required', () => {
  rejects((j) => { delete j.journeyId; }, 'journeyId is required');
  rejects((j) => { delete j.title; }, 'title is required');
  rejects((j) => { delete j.revision; }, 'revision must be a finite number');
  rejects((j) => { delete j.site; }, 'site is required');
  rejects((j) => { delete j.editorFrame; }, 'editorFrame is required');
  rejects((j) => { delete j.shoal; }, 'shoal is required');
});

test('the schema version must match exactly', () => {
  assert.equal(SCHEMA_VERSION, '2.0');
  rejects((j) => { j.schemaVersion = '1.0'; }, `schemaVersion must be "${SCHEMA_VERSION}"`);
  rejects((j) => { delete j.schemaVersion; }, `schemaVersion must be "${SCHEMA_VERSION}"`);
});

test('the Niantic identifiers are required', () => {
  // Without them there is nothing to localise against: the site id names the scan the app asks
  // VPS for, and the org id scopes it. A journey missing either cannot be placed at all.
  rejects((j) => { delete j.site.nianticSiteId; }, 'nianticSiteId is required');
  rejects((j) => { delete j.site.nianticOrgId; }, 'nianticOrgId is required');
  rejects((j) => { delete j.site.slug; }, 'slug is required');
});

test('a baked anchor payload must record the asset it came from', () => {
  // Re-promoting a VPS asset can move the origin, which silently shifts every authored
  // coordinate. Keeping vpsAssetId alongside the payload is how that is ever detected.
  rejects((j) => { j.site.anchorPayload = 'AAAA'; }, 'vpsAssetId is missing');

  const both = check((j) => {
    j.site.anchorPayload = 'AAAA';
    j.site.vpsAssetId = 'asset-1';
  });
  assert.equal(both.ok, true, both.errors.join('; '));

  // Fetching the payload at runtime instead of baking it is the other legal shape.
  assert.equal(check((j) => { j.site.vpsAssetId = 'asset-1'; }).ok, true);
});

test('the centreline needs at least two points to be an axis', () => {
  rejects((j) => { j.site.centreline = [{ x: 0, y: 0, z: 0 }]; }, 'at least 2 points');
  rejects((j) => { delete j.site.centreline; }, 'at least 2 points');
  rejects((j) => { j.site.centreline[1] = { x: 0, y: 'high', z: 0 }; },
    'centreline[1].y must be a finite number');
});

// --- editor frame -------------------------------------------------------

test('the frame transform must be a full pose', () => {
  rejects((j) => { delete j.editorFrame.translation; },
    'translation must be an object with x, y, z');
  rejects((j) => { j.editorFrame.rotation = [0, 0, 0]; }, 'rotation must be a quaternion');
  rejects((j) => { j.editorFrame.rotation = [0, 0, 0, 'w']; }, 'rotation must be a quaternion');
  rejects((j) => { delete j.editorFrame.scale; }, 'scale must be a finite number');
  rejects((j) => { j.editorFrame.scale = 0; }, 'scale must be between 0.001 and 1000');
});

test('both trim box shapes are accepted', () => {
  // The oriented box is the one that matters — the creek runs diagonally, so an axis-aligned
  // trim cannot follow it — but old drafts carry the min/max form and must still load.
  const oriented = check((j) => {
    j.editorFrame.trim = {
      enabled: true,
      position: { x: 0, y: 0, z: 0 },
      halfExtent: { x: 7.4, y: 2, z: 3.4 },
      rotation: [0, 0.39564, 0, 0.91841],
    };
  });
  assert.equal(oriented.ok, true, oriented.errors.join('; '));

  const legacy = check((j) => {
    j.editorFrame.trim = {
      enabled: true,
      min: { x: -1, y: -1, z: -1 },
      max: { x: 1, y: 1, z: 1 },
    };
  });
  assert.equal(legacy.ok, true, legacy.errors.join('; '));
});

test('a trim box must say whether it is enabled', () => {
  // Disabling has to be expressible without discarding the authored extent, so `enabled` is a
  // separate required flag rather than being inferred from the geometry's presence.
  rejects((j) => {
    j.editorFrame.trim = { min: { x: -1, y: -1, z: -1 }, max: { x: 1, y: 1, z: 1 } };
  }, 'trim.enabled must be a boolean');
});

test('a half-specified trim box is reported as an oriented one', () => {
  // Questionable: the legacy form is recognised by min AND max both being present, so a box with
  // only min falls through to the oriented branch and reports four missing-field errors that
  // never mention min or max. It fails closed, which is what matters, but the message misleads.
  const result = rejects((j) => {
    j.editorFrame.trim = { enabled: true, min: { x: -1, y: -1, z: -1 } };
  }, 'trim.position must be an object');
  assert.ok(hasError(result, 'trim.halfExtent'));
  assert.ok(!hasError(result, 'max'));
});

test('trim must be an object when present', () => {
  rejects((j) => { j.editorFrame.trim = true; }, 'trim must be an object');
});

// --- shoal --------------------------------------------------------------

test('the shoal needs both counts and cannot start below its own floor', () => {
  rejects((j) => { delete j.shoal.startingCount; }, 'startingCount must be a finite number');
  rejects((j) => { delete j.shoal.minimumCount; }, 'minimumCount must be a finite number');
  rejects((j) => { j.shoal.minimumCount = 99; }, 'minimumCount cannot exceed startingCount');
  rejects((j) => { j.shoal.startingCount = 0; }, 'startingCount must be between 1 and 10000');
});

test('a minimum of zero is legal — the shoal may be given away entirely', () => {
  const result = check((j) => { j.shoal.minimumCount = 0; });
  assert.equal(result.ok, true, result.errors.join('; '));
});

// --- ambient ------------------------------------------------------------

test('ambient sources are optional and validated when present', () => {
  const result = check((j) => {
    j.ambient = [{
      id: 'weir',
      position: { x: 1, y: 0, z: 1 },
      audibleRadiusM: 20,
      audio: { far: { clipId: 'weir--far' } },
    }];
  });
  assert.equal(result.ok, true, result.errors.join('; '));

  rejects((j) => { j.ambient = {}; }, 'ambient must be an array');
  rejects((j) => { j.ambient = [{ id: 'weir', position: { x: 0, y: 0, z: 0 } }]; },
    'audibleRadiusM must be a finite number');
  rejects((j) => { j.ambient = [{ position: { x: 0, y: 0, z: 0 }, audibleRadiusM: 5 }]; },
    'ambient[0]: id is required');
});

test('duplicate ambient ids are rejected', () => {
  rejects((j) => {
    j.ambient = [
      { id: 'weir', position: { x: 0, y: 0, z: 0 }, audibleRadiusM: 5 },
      { id: 'weir', position: { x: 1, y: 0, z: 0 }, audibleRadiusM: 5 },
    ];
  }, 'ambient[1]: duplicate id "weir"');
});

test('an ambient source is allowed to be silent, unlike a beat', () => {
  // Deliberate asymmetry to note, not a bug to rely on: beats must carry a proximity layer if
  // they carry audio at all, ambient sources may carry an empty audio block or none. A silent
  // ambient source is inert, so nothing detects one that lost its clips.
  assert.equal(check((j) => {
    j.ambient = [{ id: 'weir', position: { x: 0, y: 0, z: 0 }, audibleRadiusM: 5 }];
  }).ok, true);
  assert.equal(check((j) => {
    j.ambient = [{ id: 'weir', position: { x: 0, y: 0, z: 0 }, audibleRadiusM: 5, audio: {} }];
  }).ok, true);
});

test('ambient ids share no namespace with beat ids', () => {
  // Documents the current behaviour so a future collision check is a deliberate change: the two
  // lists are deduplicated against separate sets, so a source may reuse a beat's id.
  assert.equal(check((j) => {
    j.ambient = [{ id: j.beats[0].id, position: { x: 0, y: 0, z: 0 }, audibleRadiusM: 5 }];
  }).ok, true);
});

// --- exported constants -------------------------------------------------

test('INTERACTIONS describes each kind well enough to build a picker from', () => {
  assert.deepEqual(Object.keys(INTERACTIONS), ['proximity', 'crouch', 'catch', 'give', 'lift']);
  for (const [kind, spec] of Object.entries(INTERACTIONS)) {
    assert.equal(typeof spec.label, 'string', `${kind} needs a label`);
    assert.ok(spec.hint.length > 0, `${kind} needs a hint`);
    assert.equal(typeof spec.needsGesture, 'boolean');
  }
  // Which kinds need bespoke gesture detection drives real work, so pin the set: `give` changes
  // state but is chosen, not performed, and `proximity` is arrival alone.
  assert.deepEqual(
    Object.keys(INTERACTIONS).filter((k) => INTERACTIONS[k].needsGesture),
    ['crouch', 'catch', 'lift']
  );
});

test('the shared constants are frozen', () => {
  // The editor imports these to build its UI; a stray mutation there would change what the
  // service validates against in the same process.
  assert.ok(Object.isFrozen(INTERACTIONS));
  assert.ok(Object.isFrozen(LAYERS));
});
