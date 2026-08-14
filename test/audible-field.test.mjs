/**
 * The audible half-life, which the editor draws as a ring around every beat.
 *
 * This number is a claim about the piece, not a rendering detail: it says how far a beat's sound
 * carries before it stops being the thing you are listening to. If it is wrong, the ring is a
 * confident lie and beats get composed against it.
 *
 * The law it samples is the editor's Resonance stand-in for PHASE — a three-layer crossfade
 * multiplied by Resonance's distance rolloff. The rolloff was originally guessed as a logarithm
 * because the model is named "logarithmic"; it is actually 1/(d+1), normalised, and the guess
 * put the half-life out by 20%. These tests pin the shape so that cannot recur silently.
 */

import test from 'node:test';
import assert from 'node:assert/strict';

import { audibleField } from '../editor/js/audition.js';
import { makeBeat } from './helpers.mjs';

test('a beat with no audio has no field at all', () => {
  assert.equal(audibleField(makeBeat('silent', 0, { audio: {} })), null);
});

test('the half-life is a real distance inside the audible reach', () => {
  const field = audibleField(makeBeat('one', 0));
  assert.ok(field.half > 0, 'half-life is positive');
  assert.ok(field.half < field.reach, 'half-life falls inside the reach');
  assert.ok(Number.isFinite(field.maxD));
});

test('amplitude at the half-life really is half the peak', () => {
  const field = audibleField(makeBeat('one', 0));
  const atHalf = field.amplitudeAt(field.half);
  // Sampled at 5 cm, so the crossing lands within one step of exactly half.
  assert.ok(Math.abs(atHalf - field.peak / 2) < field.peak * 0.06,
    `at ${field.half} m amplitude was ${atHalf}, half the peak is ${field.peak / 2}`);
});

test('amplitude falls monotonically from the peak to the half-life', () => {
  // Not asserted beyond the half-life: the layer crossfade genuinely has a second lobe as the
  // mid recording fades in, and the half-life is defined as the FIRST crossing.
  const field = audibleField(makeBeat('one', 0));
  let previous = Infinity;
  for (let d = field.peakAt; d <= field.half; d += 0.05) {
    const a = field.amplitudeAt(d);
    assert.ok(a <= previous + 1e-9, `amplitude rose at ${d} m`);
    previous = a;
  }
});

test('nothing is audible past the cull distance', () => {
  const field = audibleField(makeBeat('one', 0));
  assert.equal(field.amplitudeAt(field.maxD + 1), 0);
});

test('a louder beat carries further', () => {
  const quiet = audibleField(makeBeat('q', 0));
  const loud = audibleField(makeBeat('l', 0, {
    audio: {
      far: { clipId: 'c--far', gainDb: -2, loop: true },
      mid: { clipId: 'c--mid', gainDb: 0, loop: true },
      intimate: { clipId: 'c--intimate', gainDb: 0, loop: true },
    },
  }));
  // The half-life is a ratio against each beat's own peak, so raising every layer equally does
  // NOT move it — that is the point of a half-life. What must grow is the absolute amplitude.
  assert.ok(loud.peak > quiet.peak, 'a louder beat has a higher peak');
});

test('a wider trigger radius widens the audible field', () => {
  // reach defaults to exitRadius * 3.5, so the gate geometry drives how far the sound carries.
  const narrow = audibleField(makeBeat('n', 0));
  const wide = audibleField(makeBeat('w', 0, {
    trigger: { enterRadiusM: 6, exitRadiusM: 10, dwellSeconds: 1, minimumHoldSeconds: 10,
      requiresPreviousComplete: false },
  }));
  assert.ok(wide.reach > narrow.reach);
  assert.ok(wide.half > narrow.half, 'a wider field also takes longer to halve');
});

test('an explicit audibleRadiusM overrides the radius derived from the trigger', () => {
  const field = audibleField(makeBeat('x', 0, { audibleRadiusM: 25 }));
  assert.equal(field.reach, 25);
});

test('the half-life on the garden settings is tighter than the trigger radius', () => {
  // This is the finding the ring exists to show. With the shipped gates the sound has already
  // halved well inside the radius at which the interaction arms, so the trigger rings say
  // nothing useful about what the visitor can hear.
  const beat = makeBeat('garden', 0);
  const field = audibleField(beat);
  assert.ok(field.half < beat.trigger.enterRadiusM,
    `half-life ${field.half} m should be inside the ${beat.trigger.enterRadiusM} m gate`);
});
