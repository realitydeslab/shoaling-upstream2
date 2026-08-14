/**
 * Centreline geometry, in both copies.
 *
 * The projection maths exists twice — once in the service (journey-schema.ts, which recomputes
 * every beat's `s` when the path moves) and once in the editor (editor/js/geom.js, which drives
 * the scrubber and the walker). They must agree: if they drift, the scrubber shows a beat arming
 * at a position the device will never trigger it at, and the discrepancy is invisible until
 * someone is standing in a creek.
 *
 * So every case here runs against both implementations.
 */

import test from 'node:test';
import assert from 'node:assert/strict';

// The service is plain ES modules with no build step, so it is imported as JavaScript and its
// signatures are inferred. That asymmetry is the point of this file: two implementations, one
// typed and one not, asserted to agree.
import {
  projectToCentreline as projectService,
  centrelineLength as lengthService,
} from '../service/src/journey-schema.ts';
import {
  projectToCentreline as projectEditor,
  centrelineLength as lengthEditor,
  pointAtS,
} from '../editor/src/geom.ts';
import type { Vec3 } from '../editor/src/types.ts';

const STRAIGHT: Vec3[] = [
  { x: 0, y: 0, z: 0 },
  { x: 5, y: 0, z: 0 },
  { x: 10, y: 0, z: 0 },
];

test('length of a straight reach is the sum of its segments', () => {
  assert.equal(lengthService(STRAIGHT), 10);
  assert.equal(lengthEditor(STRAIGHT), 10);
});

test('length of a degenerate path is zero, not NaN', () => {
  for (const fn of [lengthService, lengthEditor]) {
    assert.equal(fn([]), 0);
    assert.equal(fn([{ x: 1, y: 2, z: 3 }]), 0);
  }
});

test('a point on the line projects to its own distance along it', () => {
  for (const project of [projectService, projectEditor]) {
    assert.equal(project({ x: 3, y: 0, z: 0 }, STRAIGHT).s, 3);
    assert.equal(project({ x: 7.5, y: 0, z: 0 }, STRAIGHT).s, 7.5);
  }
});

test('a point beside the line projects onto it, keeping only the along-track distance', () => {
  // Two metres off the bank at 4 m along. `s` is 4; the 2 m is cross-track and must not leak in.
  for (const project of [projectService, projectEditor]) {
    assert.equal(project({ x: 4, y: 0, z: 2 }, STRAIGHT).s, 4);
  }
});

test('projection is clamped to the ends rather than extrapolated', () => {
  // A visitor who walks past the top of the reach is at the end of it, not beyond it: an
  // unclamped projection would keep incrementing `s` and arm beats that are behind them.
  for (const project of [projectService, projectEditor]) {
    assert.equal(project({ x: -6, y: 0, z: 0 }, STRAIGHT).s, 0);
    assert.equal(project({ x: 40, y: 0, z: 0 }, STRAIGHT).s, 10);
  }
});

test('the two implementations agree across a bent path', () => {
  // An L-bend, which is where a naive per-segment projection most easily disagrees.
  const bent: Vec3[] = [
    { x: 0, y: 0, z: 0 },
    { x: 4, y: 0, z: 0 },
    { x: 4, y: 0, z: 4 },
  ];
  for (let x = -2; x <= 7; x += 0.5) {
    for (let z = -2; z <= 7; z += 0.5) {
      const point: Vec3 = { x, y: 0, z };
      const a = projectService(point, bent).s;
      const b = projectEditor(point, bent).s;
      assert.ok(Math.abs(a - b) < 1e-9,
        `disagree at (${x}, ${z}): service ${a}, editor ${b}`);
    }
  }
});

test('pointAtS walks the path and clamps at both ends', () => {
  assert.deepEqual(pointAtS(0, STRAIGHT), { x: 0, y: 0, z: 0 });
  assert.equal(pointAtS(5, STRAIGHT).x, 5);
  assert.equal(pointAtS(7.5, STRAIGHT).x, 7.5);
  assert.equal(pointAtS(-3, STRAIGHT).x, 0, 'before the start clamps to the start');
  assert.equal(pointAtS(99, STRAIGHT).x, 10, 'past the end clamps to the end');
});

test('pointAtS and projectToCentreline are inverses along the path', () => {
  for (let s = 0; s <= 10; s += 0.25) {
    const back = projectEditor(pointAtS(s, STRAIGHT), STRAIGHT).s;
    assert.ok(Math.abs(back - s) < 1e-9, `round trip failed at s=${s}, got ${back}`);
  }
});

test('the path carries height, so s is a 3D distance', () => {
  // The route is stored at chest height and climbs a waterfall; a projection that ignored y
  // would under-report the reach and put every beat's s slightly short.
  const climbing: Vec3[] = [
    { x: 0, y: 0, z: 0 },
    { x: 3, y: 4, z: 0 },   // 3-4-5
  ];
  assert.equal(lengthService(climbing), 5);
  assert.equal(lengthEditor(climbing), 5);
});
