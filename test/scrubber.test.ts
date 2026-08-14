/**
 * The armed-zones scrubber: what is live at a position along the reach, and which beat wins.
 *
 * The scrubber is the only place an author can see trigger behaviour as behaviour rather than
 * as circles on a map, so what it claims has to be exactly what the runtime would do. It does
 * not do that arithmetic itself — it delegates to `evaluateAt` in geom — and these cases pin
 * both halves of that: the delegation, and the pointer-to-position mapping that is genuinely
 * the scrubber's own.
 *
 * It is imported from `editor/js/`, not `editor/src/`, and deliberately: the scrubber has a
 * runtime import of its sibling geometry module, and Node does not resolve a './geom.js'
 * specifier to `geom.ts`, so the source tree cannot be loaded here. The emitted module is also
 * exactly what the browser loads — but it does mean this suite is checking the last build, so
 * run it as `npm run verify` (build, typecheck, test) rather than `npm test` alone.
 */

import test from 'node:test';
import assert from 'node:assert/strict';

import { Scrubber } from '../editor/js/scrubber.js';
import { evaluateAt } from '../editor/src/geom.ts';
import { makeJourney, makeBeat } from './helpers.ts';
import type { Beat, JourneyDocument } from '../editor/src/types.ts';

// PAD.left and PAD.right are 10 each, so a 210 px canvas plots the reach across 190 px and a
// 10 m reach lands on round numbers.
const WIDTH = 210;
const PLOT = 190;

interface FakePointer { clientX: number; pointerId: number }
type FakeListener = (event: FakePointer) => void;

/**
 * A canvas with just enough behind it to run the scrubber: the geometry it measures itself
 * against, the pointer listeners it installs, and a 2D context that swallows every call.
 *
 * The pixels are not the contract. Stubbing the drawing calls one by one would make this suite
 * fail for changes to how the bands look, which is not what it is defending.
 */
class FakeCanvas {
  clientWidth = WIDTH;
  clientHeight = 60;
  width = 0;
  height = 0;
  readonly listeners = new Map<string, FakeListener>();

  getContext(): unknown {
    return new Proxy({} as Record<string, unknown>, {
      get: (target, key: string) => (key in target ? target[key] : () => {}),
      set: (target, key: string, value) => { target[key] = value; return true; },
    });
  }

  addEventListener(type: string, fn: FakeListener): void { this.listeners.set(type, fn); }
  setPointerCapture(): void {}
  releasePointerCapture(): void {}
  getBoundingClientRect(): { left: number; top: number } { return { left: 0, top: 0 }; }

  /** Deliver a pointer event at a page x, the way a real drag would. */
  emit(type: string, clientX: number): void {
    const fn = this.listeners.get(type);
    if (!fn) throw new Error(`the scrubber registered no ${type} listener`);
    fn({ clientX, pointerId: 1 });
  }
}

// The scrubber watches its own canvas for resize and reads the device pixel ratio. Node has
// neither; the observer only has to exist, and never fires, because nothing here resizes.
globalThis.ResizeObserver = class {
  observe(): void {}
  unobserve(): void {}
  disconnect(): void {}
} as unknown as typeof ResizeObserver;
globalThis.devicePixelRatio = 2;

function mount(journey?: JourneyDocument | null) {
  const canvas = new FakeCanvas();
  // The stub satisfies everything the scrubber touches, which is a small corner of the real
  // element; nothing short of a full DOM would satisfy the interface itself.
  const scrubber = new Scrubber(canvas as unknown as HTMLCanvasElement);
  if (journey !== undefined) scrubber.setJourney(journey);
  return { canvas, scrubber };
}

/** A reach with one beat, so the bands can be reasoned about by hand. */
function oneBeatAt(s: number, overrides: Partial<Beat> = {}): JourneyDocument {
  return makeJourney({ beats: [makeBeat('solo', s, overrides)] });
}

test('the reach it plots is the length of the walking path', () => {
  const { scrubber } = mount(makeJourney());
  assert.equal(scrubber.length, 10);
  assert.equal(scrubber.beats.length, 2);
});

test('a journey with no centreline scrubs to nowhere rather than to NaN', () => {
  // The editor opens on a site before a path has been drawn, and a NaN here propagates into the
  // walker and the audition listener position.
  const { canvas, scrubber } = mount(makeJourney({
    site: { ...makeJourney().site, centreline: [] },
  }));
  assert.equal(scrubber.length, 0);
  canvas.emit('pointerdown', 100);
  assert.equal(scrubber.s, 0);
});

test('a scrubber with no journey at all still evaluates to nothing', () => {
  const { scrubber } = mount(null);
  assert.deepEqual(scrubber.evaluate(), { armed: [], winner: null });
});

test('a drag maps the pointer across the plot to a position along the reach', () => {
  const { canvas, scrubber } = mount(makeJourney());
  canvas.emit('pointerdown', 10);
  assert.equal(scrubber.s, 0, 'the left edge of the plot is the bottom of the reach');
  canvas.emit('pointermove', 10 + PLOT / 2);
  assert.equal(scrubber.s, 5);
  canvas.emit('pointermove', 10 + PLOT);
  assert.equal(scrubber.s, 10, 'the right edge is the top of the reach');
});

test('a drag past either end clamps rather than running off the reach', () => {
  const { canvas, scrubber } = mount(makeJourney());
  canvas.emit('pointerdown', -400);
  assert.equal(scrubber.s, 0);
  canvas.emit('pointermove', 4000);
  assert.equal(scrubber.s, 10);
});

test('moving the pointer without pressing it does not move the playhead', () => {
  // The canvas is a few pixels tall under the stage and the pointer crosses it constantly.
  const { canvas, scrubber } = mount(makeJourney());
  scrubber.setS(3);
  canvas.emit('pointermove', 10 + PLOT);
  assert.equal(scrubber.s, 3);
  canvas.emit('pointerup', 10 + PLOT);
  canvas.emit('pointermove', 10);
  assert.equal(scrubber.s, 3, 'and releasing ends the drag');
});

test('a beat is armed where the position falls inside its exit band', () => {
  const { scrubber } = mount(oneBeatAt(5));   // enter 1.9, exit 3.04
  scrubber.setS(5);
  assert.deepEqual(scrubber.evaluate().armed.map((a) => a.beat.id), ['solo']);

  scrubber.setS(9);
  assert.deepEqual(scrubber.evaluate().armed, [], '4 m out is beyond the exit band');
});

test('a position inside the exit band but outside the enter band arms a beat without firing it', () => {
  // The gap between the two radii is the whole anti-thrash mechanism: a visitor standing near
  // one boundary would otherwise make the beat chatter. In that gap the beat is live but no
  // beat wins, and the scrubber has to show that state rather than rounding it to one or other.
  const { scrubber } = mount(oneBeatAt(5));
  scrubber.setS(7.5);   // 2.5 m out: inside exit 3.04, outside enter 1.9
  const { armed, winner } = scrubber.evaluate();
  assert.equal(armed.length, 1);
  assert.equal(armed[0]?.inExit, true);
  assert.equal(armed[0]?.inEnter, false);
  assert.equal(winner, null, 'nothing fires in the hysteresis gap');
});

test('where two enter bands overlap the nearer beat wins outright', () => {
  // Winner-take-all over one state variable rather than six independent zone monitors: two
  // monitors near a boundary interleave and double-fire, one state machine cannot.
  const journey = makeJourney({ beats: [makeBeat('lower', 3), makeBeat('upper', 5)] });
  const { scrubber } = mount(journey);

  scrubber.setS(4.2);
  const near = scrubber.evaluate();
  assert.deepEqual(near.armed.map((a) => a.beat.id), ['lower', 'upper']);
  assert.equal(near.winner?.beat.id, 'upper', '0.8 m beats 1.2 m');

  scrubber.setS(3.8);
  assert.equal(scrubber.evaluate().winner?.beat.id, 'lower');
});

test('a later beat still wins although it requires the previous one to be complete', () => {
  // The scrubber evaluates with the order gate OFF, which is a real divergence from the runtime
  // and the one place the module comment's "the same evaluation the runtime performs" overstates
  // it. Dragging completes nothing, so there is no high-water mark to clear the gate against;
  // respecting it would leave every beat after the first permanently unreachable and the tool
  // would show a blank rail for the whole reach.
  const journey = makeJourney({ beats: [makeBeat('first', 1), makeBeat('second', 8)] });
  assert.equal(journey.beats[1]?.trigger.requiresPreviousComplete, true);

  const { scrubber } = mount(journey);
  scrubber.setS(8);
  const { winner } = scrubber.evaluate();
  assert.equal(winner?.beat.id, 'second');
  assert.equal(winner?.blocked, false);
});

test('the scrubber evaluates through the shared geometry rather than a copy of it', () => {
  // The projection maths already exists twice, in the editor and in the service, and the
  // geometry suite exists because drift between them is invisible until someone is standing in
  // a creek. A third copy inside the scrubber would be the same failure one level up.
  const journey = makeJourney({ beats: [makeBeat('a', 2), makeBeat('b', 4), makeBeat('c', 9)] });
  const { scrubber } = mount(journey);
  for (let s = 0; s <= 10; s += 0.5) {
    scrubber.setS(s);
    assert.deepEqual(
      scrubber.evaluate(),
      evaluateAt(s, journey.beats, { respectOrder: false }),
      `scrubber and geom disagree at s=${s}`,
    );
  }
});

test('selecting a beat changes nothing about what is armed', () => {
  // Selection is a drawing concern. It once looked tempting to let the selected beat win.
  const { scrubber } = mount(oneBeatAt(5));
  scrubber.setS(4);
  const before = scrubber.evaluate();
  scrubber.setSelected('solo');
  assert.deepEqual(scrubber.evaluate(), before);
});
