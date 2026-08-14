/**
 * The mix policy: how many places you are being told about at once.
 *
 * The artist's report was "when I play, I can hear many reverb sound — make it clean", and the
 * cause was not reverb. Every beat's audible reach was `exitRadiusM * 3.5` regardless of where
 * the other beats were, so on an 18.8 m reach with six beats 0.7-4 m apart, all six were inside
 * each other's fields for the whole walk and the audio summed all of them — while the trigger
 * state machine next door (`geom.ts` `evaluateAt`) had always been winner-take-all.
 *
 * These cases pin the three decisions that fixed it: the reach is bounded by how far away the
 * next point of interest is, the nearest one is the subject and the rest duck behind it, and the
 * recording is chosen by distance across the ground rather than through the creek bed.
 *
 * `test/audible-field.test.ts` covers the single-beat half-life this shares its law with; the
 * two suites are deliberately separate files so they do not collide.
 */

import test from 'node:test';
import assert from 'node:assert/strict';

import {
  AUDIBLE_FLOOR, audibleField, cullDistance, mixAt, nearestNeighbourM, reachFor,
  type AudibleNode, type MixNode, type MixVoice,
} from '../editor/src/audition.ts';
import { makeBeat } from './helpers.ts';
import type { Vec3 } from '../editor/src/types.ts';

/** Beats along +X at chest height's counterpart, spaced as given. */
function beatsAt(...xs: number[]): AudibleNode[] {
  return xs.map((x, i) => makeBeat(`b${i}`, x));
}

/** What the audition builds at load(): every beat bounded by its nearest neighbour. */
function mixNodes(beats: readonly AudibleNode[]): MixNode[] {
  return beats.map((node) => ({ node, reach: reachFor(node, nearestNeighbourM(node, beats)) }));
}

/** The reach this replaced: exitRadiusM * 3.5, whoever else was standing nearby. */
function unboundedNodes(beats: readonly AudibleNode[]): MixNode[] {
  return beats.map((node) => ({ node, reach: (node.trigger?.exitRadiusM ?? 6) * 3.5 }));
}

/** How many of these sources are rendered at all at `listener` — reach only, no mix policy. */
function inRange(nodes: readonly MixNode[], listener: Vec3): number {
  return mixAt(listener, nodes).voices
    .filter((v, i) => v.blendDistance <= cullDistance(nodes[i]!.reach)).length;
}

function at(x: number, y = 0, z = 0): Vec3 { return { x, y, z }; }

function voice(nodes: readonly MixNode[], listener: Vec3, id: string): MixVoice {
  const found = mixAt(listener, nodes).voices.find((v) => v.id === id);
  assert.ok(found, `no voice for ${id}`);
  return found;
}

// --------------------------------------------------------------------- how far a beat carries

test('a beat with no neighbours keeps exactly the reach it always had', () => {
  const lone = makeBeat('lone', 0);
  assert.equal(reachFor(lone), 3.04 * 3.5);
  assert.equal(reachFor(lone, Infinity), 3.04 * 3.5);
});

test('a crowded beat carries only as far as its neighbour, not 3.5 trigger radii', () => {
  // 8 m apart: the cull radius lands on the neighbour rather than 6 m past it.
  const reach = reachFor(makeBeat('b', 0), 8);
  assert.ok(Math.abs(reach - 8 / 1.35) < 1e-9, `expected 8/1.35, got ${reach}`);
  assert.equal(cullDistance(reach), 8);
});

test('the reach never falls below the radius in which the beat can fire', () => {
  // The UBC garden case: beats 0.68-4 m apart, all of them closer together than one exit radius.
  // Nothing can separate these, and a beat that went quiet inside its own trigger zone would be
  // a worse bug than the one being fixed, so the floor holds.
  const reach = reachFor(makeBeat('b', 0), 0.68);
  assert.equal(reach, 3.04, 'floored at exitRadiusM');
});

test('an authored audibleRadiusM outranks the neighbours entirely', () => {
  // Which is what the ambient beds do: "The creek itself" states 40 m and means it.
  assert.equal(reachFor(makeBeat('b', 0, { audibleRadiusM: 40 }), 0.5), 40);
});

test('nearest neighbour is measured across the ground and ignores the node itself', () => {
  const beats = beatsAt(0, 2.5, 9);
  assert.equal(nearestNeighbourM(beats[0]!, beats), 2.5);
  assert.equal(nearestNeighbourM(beats[2]!, beats), 6.5);
  assert.equal(nearestNeighbourM(beats[0]!, [beats[0]!]), Infinity, 'a lone beat has no neighbour');
});

test('a beat below the walker is no further away for it', () => {
  const flat = makeBeat('flat', 0);
  const sunk = makeBeat('sunk', 0, { position: { x: 0, y: -3, z: 0 } });
  assert.equal(nearestNeighbourM(flat, [flat, sunk]), 0, 'the same place on the plan');
});

// --------------------------------------------------------------------- one place at a time

test('the nearest point of interest leads and the rest duck behind it', () => {
  const nodes = mixNodes(beatsAt(0, 6));
  const mix = mixAt(at(2), nodes);
  assert.equal(mix.leader?.id, 'b0');
  // Twice the distance, squared: 12 dB down, and the exponent is what that 12 dB is made of.
  assert.ok(Math.abs(voice(nodes, at(2), 'b1').focus - 0.25) < 1e-9);
  assert.equal(voice(nodes, at(2), 'b0').focus, 1, 'the leader is never ducked');
});

test('midway between two beats neither is ducked, so one place crosses into the next', () => {
  const nodes = mixNodes(beatsAt(0, 6));
  const mix = mixAt(at(3), nodes);
  for (const v of mix.voices) assert.equal(v.focus, 1);
  assert.ok(Math.abs(mix.voices[0]!.amplitude - mix.voices[1]!.amplitude) < 1e-9,
    'equidistant beats are equally present');
});

test('arriving at a beat is no quieter for having neighbours', () => {
  // The whole point of ducking the others rather than turning the distant ones down: what you
  // hear on arrival is exactly what you would hear if this beat were alone on the reach. Both
  // sides are given the same reach, since that is the other half of the change and this case is
  // about the duck.
  const beats = beatsAt(0, 3.5, 7);
  const crowded: MixNode[] = beats.map((node) => ({ node, reach: 3.04 }));
  const here = at(0.2);
  assert.equal(voice(crowded, here, 'b0').amplitude, voice([crowded[0]!], here, 'b0').amplitude);
});

test('the duck is continuous — nothing snaps as the walker crosses between beats', () => {
  // Sampled at two resolutions, because a slope and a step look identical at one. Walking into a
  // beat is genuinely steep — the intimate recording arrives over about a metre — so the coarse
  // reading is large and must fall away as the step shrinks. Whatever is left at 0.5 mm is a real
  // discontinuity, and a hard winner-take-all would leave a whole voice of it here.
  const nodes = mixNodes(beatsAt(0, 4, 8, 11));
  const worstStep = (h: number) => {
    let previous = mixAt(at(-2), nodes).total;
    let worst = 0;
    for (let x = -2; x <= 13; x += h) {
      const total = mixAt(at(x), nodes).total;
      worst = Math.max(worst, Math.abs(total - previous));
      previous = total;
    }
    return worst;
  };

  const coarse = worstStep(0.05);
  const fine = worstStep(0.0005);
  assert.ok(fine < coarse * 0.3, `${fine.toFixed(4)} left at 0.5 mm out of ${coarse.toFixed(4)} at 5 cm`);
  // What remains is one voice crossing the audible floor and being dropped from the sum: a step
  // of at most -42 dB, under the creek, and update() puts hysteresis around it as well.
  assert.ok(fine <= AUDIBLE_FLOOR * 1.05,
    `a discontinuity of ${fine.toFixed(4)}, larger than the audible floor`);
});

test('nothing is left playing past the cull, and it arrives there without a step', () => {
  const nodes = mixNodes(beatsAt(0, 30));
  const reach = nodes[0]!.reach;
  const far = voice(nodes, at(cullDistance(reach) + 0.5), 'b0');
  assert.equal(far.amplitude, 0);
  assert.equal(far.audible, false);
  assert.equal(far.loops, 0, 'a culled source holds no decoder open');

  // The rolloff now reaches zero exactly at the cull, so the last thing heard before the cut is
  // already inaudible. It used to still have a third of its curve left.
  const edge = voice(nodes, at(cullDistance(reach) * 0.98), 'b0');
  const peak = audibleField(nodes[0]!.node, reach)?.peak ?? 0;
  assert.ok(edge.amplitude < peak * 0.02,
    `at the cull the beat was still at ${(edge.amplitude / peak * 100).toFixed(1)}% of peak`);
});

test('only the layers that have gain are counted as running', () => {
  const nodes = mixNodes(beatsAt(0, 30));
  // Standing on the beat, the intimate recording is the only one with any weight at all.
  const here = mixAt(at(0), nodes);
  assert.equal(here.voices[0]!.gains.far, 0);
  assert.equal(here.voices[0]!.gains.mid, 0);
  assert.ok(here.voices[0]!.gains.intimate > 0);
  assert.equal(here.loops, 1, 'one recording playing, not three');
});

// --------------------------------------------------------------------- the bed underneath

test('an ambient bed is never ducked and never becomes the subject', () => {
  const beats = beatsAt(0, 6);
  const bed = makeBeat('creek', 3, { audibleRadiusM: 40 });
  const nodes: MixNode[] = [...mixNodes(beats), { node: bed, reach: 40, ambient: true }];

  const mix = mixAt(at(0.1), nodes);
  const creek = mix.voices.find((v) => v.id === 'creek')!;
  assert.equal(creek.focus, 1, 'standing on a beat does not push the creek out of the mix');
  assert.ok(creek.audible);
  assert.equal(mix.leader?.id, 'b0', 'the bed is not a point of interest');
});

test('a bed at the far end of the reach does not stop the beats ducking each other', () => {
  const beats = beatsAt(0, 6);
  const bed = makeBeat('creek', 100, { audibleRadiusM: 200 });
  const nodes: MixNode[] = [...mixNodes(beats), { node: bed, reach: 200, ambient: true }];
  assert.ok(Math.abs(voice(nodes, at(2), 'b1').focus - 0.25) < 1e-9);
});

// --------------------------------------------------------------------- the recording, not the bed

test('a beat in the water below you is near, not far', () => {
  // Beats are authored at bed height and the phone rides at chest height, so on the UBC reach
  // the walker's closest approach to `tree`, `strider` and `falls` is 1.2-1.3 m in 3D but
  // 0.05-0.61 m in plan. The intimate recording only plays inside 0.35 of the reach, so a 3D
  // blend distance would lock the arrival recording out of those three beats entirely — the
  // visitor could stand directly over the redd and never hear it arrive.
  const sunk = makeBeat('sunk', 0, { position: { x: 0, y: -1.3, z: 0 } });
  const nodes: MixNode[] = [{ node: sunk, reach: 3.04 }];
  const v = voice(nodes, at(0), 'sunk');

  assert.equal(v.blendDistance, 0, 'directly above it is directly at it');
  assert.equal(v.weights.intimate, 1, 'the arrival recording plays');
  assert.ok(Math.abs(v.distance - 1.3) < 1e-9, 'but Resonance still hears it below the listener');

  // What the old 3D blend would have done with the same beat: n = 1.3/3.04 = 0.43, past the
  // 0.35 at which the intimate layer has already faded out completely.
  assert.equal(Math.max(0, 1 - (1.3 / 3.04) / 0.35), 0);
});

test('height changes where a beat is, not which recording it plays', () => {
  const flat: MixNode = { node: makeBeat('flat', 0), reach: 3.04 };
  const sunk: MixNode = {
    node: makeBeat('sunk', 0, { position: { x: 0, y: -1.3, z: 0 } }), reach: 3.04,
  };
  const here = at(1.2);
  assert.deepEqual(voice([flat], here, 'flat').weights, voice([sunk], here, 'sunk').weights);
  assert.ok(voice([sunk], here, 'sunk').distance > voice([flat], here, 'flat').distance);
});

// --------------------------------------------------------------------- the fix, on the reach

/**
 * The garden, to scale: six beats at the UBC spacings along a straight reach.
 *
 * The numbers below are the ones the artist was complaining about, and the ones that answer them.
 * They are asserted as bands rather than exact values because the point is the difference in kind
 * — six ambiences at once against one place at a time — not a fixed figure.
 */
const GARDEN = [0, 1.6, 5.8, 9.2, 11, 13.2];

test('a 3.5-radius reach put every beat in range from every point on the walk', () => {
  // The diagnosis, as pure geometry — no mix policy in it. The old audition started every layer
  // of every source it had not culled, so "in range" was also the count of running recordings,
  // three at a time: 18 loops, everywhere, all walk.
  const beats = beatsAt(...GARDEN);
  const before = unboundedNodes(beats);
  const after = mixNodes(beats);

  for (let x = 0; x <= 13.2; x += 0.2) {
    assert.equal(inRange(before, at(x)), 6, `only ${inRange(before, at(x))} beats in range at ${x}`);
  }

  let worst = 0;
  for (let x = 0; x <= 13.2; x += 0.2) worst = Math.max(worst, inRange(after, at(x)));
  // Four rather than two because the floor is holding: these beats are 1.6-3.4 m apart and one
  // exit radius is 3.04 m, so their ranges cannot help but overlap. What separates them now is
  // the duck, and the count that matters is how many are audible — the case below.
  assert.ok(worst <= 4, `expected a beat and its immediate neighbours in range, saw ${worst}`);
});

test('at most three sources are audible anywhere on garden spacing', () => {
  const after = mixNodes(beatsAt(...GARDEN));
  let worst = 0;
  for (let x = 0; x <= 13.2; x += 0.2) worst = Math.max(worst, mixAt(at(x), after).audible);
  assert.ok(worst <= 3, `expected at most a beat, its neighbour and a tail, saw ${worst}`);
});

test('what you are listening to is most of what you can hear', () => {
  // The legibility number: the leading beat's share of the total amplitude. `audio-findings.md`
  // §3a sets the acceptance test as being able to name the beat you are hearing with neighbours
  // audible, and a leader carrying a third of the field cannot pass it.
  //
  // The unducked share is the same mix with the duck divided back out — `focus` is a linear
  // factor on every gain, so `amplitude / focus` is exactly what this position would have summed
  // to without it. That isolates what the duck is worth from what the reach is worth.
  const after = mixNodes(beatsAt(...GARDEN));
  let worstDucked = 1;
  let worstClear = 1;
  let worstFlat = 1;
  for (let x = 0; x <= 13.2; x += 0.2) {
    const mix = mixAt(at(x), after);
    if (!mix.leader) continue;
    const audible = mix.voices.filter((v) => v.audible);
    const flatTotal = audible.reduce((sum, v) => sum + v.amplitude / v.focus, 0);
    const ducked = mix.leader.amplitude / mix.total;
    worstDucked = Math.min(worstDucked, ducked);
    worstFlat = Math.min(worstFlat, mix.leader.amplitude / flatTotal);

    // Where there is an unambiguous nearest beat — half again as near as the next — the leader
    // should be most of what is in the ear. Exactly midway between two beats there is no such
    // thing to be, and an even split is the correct answer rather than a failure.
    const rivals = mix.voices.filter((v) => v.id !== mix.leader!.id && !v.id.startsWith('creek'));
    const second = Math.min(...rivals.map((v) => v.blendDistance));
    if (second > mix.leader.blendDistance * 1.5) worstClear = Math.min(worstClear, ducked);
  }
  assert.ok(worstFlat < 0.55, `unducked, the leader already held ${worstFlat.toFixed(2)}`);
  assert.ok(worstDucked > 0.49, `the mix fell below an even split, at ${worstDucked.toFixed(2)}`);
  assert.ok(worstClear > 0.75,
    `with a clear nearest beat the mix still led with only ${worstClear.toFixed(2)}`);
});

test('the number of recordings running at once falls by most of itself', () => {
  const after = mixNodes(beatsAt(...GARDEN));
  let peak = 0;
  for (let x = 0; x <= 13.2; x += 0.2) peak = Math.max(peak, mixAt(at(x), after).loops);
  assert.ok(peak <= 6, `expected a handful of recordings running, saw ${peak} against 18 before`);
});

test('between two beats far enough apart, the mix reaches actual silence', () => {
  // It cannot on the UBC garden — 0.68 to 4 m apart, closer than one trigger zone — but that is
  // a fact about the journey, not about the mix, and this is the case that proves the mix would.
  const nodes = mixNodes(beatsAt(0, 40));
  const mix = mixAt(at(20), nodes);
  assert.equal(mix.audible, 0);
  assert.equal(mix.total, 0);
  assert.equal(mix.leader, null);
  assert.equal(mix.loops, 0);
});

test('the audible floor is where a voice stops being worth a decoder', () => {
  // -42 dB under a unity source, which is under the creek at the quietest point of the walk.
  assert.ok(AUDIBLE_FLOOR > 0 && AUDIBLE_FLOOR < 0.01);
  const nodes = mixNodes(beatsAt(0, 30));
  const reach = nodes[0]!.reach;
  for (let d = 0; d <= cullDistance(reach); d += 0.05) {
    const v = voice(nodes, at(d), 'b0');
    assert.equal(v.audible, v.amplitude >= AUDIBLE_FLOOR);
    assert.equal(v.loops > 0, v.audible && Object.values(v.gains).some((g) => g > 0));
  }
});
