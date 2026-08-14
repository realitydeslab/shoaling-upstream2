/**
 * The editor's own logic, as far as it goes without a browser.
 *
 * `app.ts` is wiring: most of it reads the DOM, drives three.js or talks to the service, and
 * none of that is worth faking. What is worth pinning down is the part that decides what the
 * authored document becomes — the undo history, the trim box's defaults and its migration from
 * the older axis-aligned shape, and the small resolvers the rail and the inspector are built
 * from. Those are pure, so they are here.
 *
 * The module is the editor's entry point and boots itself on import. It skips that when there
 * is no `document`, which is the one seam this port added and the reason this file can exist;
 * see `stored` at the top of app.ts.
 *
 * Left deliberately to `test/browser.e2e.test.mjs`, because faking them would test the fake:
 *
 *   - Ctrl+Z / Shift+Ctrl+Z and Ctrl+Y reaching undo() and redo() through the keydown handler,
 *     including the `e.target?.matches?.()` guard for events dispatched at `window`
 *   - applySnapshot() putting a restored document back onto the stage, the scrubber and the rail
 *   - undo after a path drag, a beat drag, a trim drag, Auto, and + Point / Delete — each of
 *     which commits from a gizmo callback that only exists in a WebGL context
 *   - the rail's drag-to-reorder, the inspector's fields, and every toast
 */

import test from 'node:test';
import assert from 'node:assert/strict';
import { registerHooks } from 'node:module';

import { makeJourney, makeBeat } from './helpers.ts';
import type { JourneyDocument, ScanBounds, TrimBox } from '../editor/src/types.ts';

// app.ts reaches the stage, which reaches three.js and spark.js. Both are dependencies of the
// SERVICE, which is the only package here with a node_modules of its own; the browser gets them
// from the import map in editor/index.html rather than from Node resolution, and Node walking up
// from editor/ never passes service/. Map them in-process, the way test/figures.test.ts does.
const ADDON_PREFIX = 'three/addons/';
const VENDOR: Record<string, string> = {
  three: new URL('../service/node_modules/three/build/three.module.js', import.meta.url).href,
  '@sparkjsdev/spark':
    new URL('../service/node_modules/@sparkjsdev/spark/dist/spark.module.js', import.meta.url).href,
};
registerHooks({
  resolve(specifier, context, next) {
    const mapped = VENDOR[specifier];
    if (mapped) return { url: mapped, shortCircuit: true };
    if (specifier.startsWith(ADDON_PREFIX)) {
      const rest = specifier.slice(ADDON_PREFIX.length);
      return {
        url: new URL(`../service/node_modules/three/examples/jsm/${rest}`, import.meta.url).href,
        shortCircuit: true,
      };
    }
    return next(specifier, context);
  },
});

// The build output rather than the source, because that is what the browser loads and because
// its relative imports resolve as written — the same reason test/scrubber.test.ts does it.
const {
  state, history, snapshotJourney, commitHistory,
  defaultTrim, ensureTrim, clipUrl, quaternionToEulerDegrees, uniqueId, escapeHtml,
} = await import('../editor/js/app.js');

/**
 * Put a journey in front of the editor, with a clean history.
 *
 * `state` and `history` are module singletons — there is one editor per page — so each test
 * starts by replacing what they hold rather than by constructing anything.
 */
function load(journey: JourneyDocument = makeJourney()): JourneyDocument {
  state.journey = journey;
  state.audio = null;
  history.past.length = 0;
  history.future.length = 0;
  history.baseline = snapshotJourney();
  return journey;
}

// --------------------------------------------------------------------- what is on screen

test('the layers a fresh editor opens with are the content, not the scaffolding', () => {
  // Beats and the scan are the piece; the path and the trim box are things you switch on to
  // work on and off again. Persisted per browser, so this is what someone sees the first time.
  assert.deepEqual(state.layers,
    { beats: true, path: false, trim: false, scan: true, phone: true });
});

// --------------------------------------------------------------------- undo history

test('a snapshot is a copy, not a view of the document', () => {
  const journey = load();
  const before = snapshotJourney();

  journey.beats[0]!.position.x = 999;
  journey.site.centreline[0]!.x = -34;

  assert.equal(before.beats[0]!.position.x, 2, 'the snapshot kept the old beat position');
  assert.equal(before.site.centreline[0]!.x, 0, 'the snapshot kept the old path');
});

test('a snapshot is of the authored geometry, and only that', () => {
  load();
  assert.deepEqual(Object.keys(snapshotJourney()).sort(), ['beats', 'editorFrame', 'site']);
});

test('a commit records the state as it was before the edit', () => {
  // The baseline holds the last committed state, so commitHistory() is called AFTER the change
  // and still pushes the version to return to. Getting this backwards makes undo a no-op.
  const journey = load();
  journey.beats[0]!.position.x = 42;
  commitHistory('moving "one"');

  assert.equal(history.past.length, 1);
  assert.equal(history.past[0]!.label, 'moving "one"');
  assert.equal(history.past[0]!.doc.beats[0]!.position.x, 2, 'undo returns to where it was');
  assert.equal(history.baseline!.beats[0]!.position.x, 42, 'the baseline moved on');
});

test('the first commit after loading a site has something to return to', () => {
  // loadSite() seeds the baseline, so the very first drag on a freshly opened site is undoable.
  const journey = load();
  journey.beats.push(makeBeat('three', 9));
  commitHistory('adding a beat');
  assert.equal(history.past[0]!.doc.beats.length, 2);
});

test('committing a new edit discards the redo stack', () => {
  // Anything else would let a redo replay an edit onto a document it was never taken from.
  load();
  history.future.push({ label: 'undone', doc: snapshotJourney() });
  commitHistory('a fresh edit');
  assert.equal(history.future.length, 0);
});

test('the history is capped, dropping the oldest step first', () => {
  const journey = load();
  for (let i = 0; i < history.limit + 10; i += 1) {
    journey.beats[0]!.position.x = i;
    commitHistory(`step ${i}`);
  }
  assert.equal(history.past.length, history.limit);
  assert.equal(history.past[0]!.label, `step ${10}`, 'the ten oldest steps were dropped');
});

test('nothing is committed when no site is open', () => {
  load();
  state.journey = null;
  commitHistory('an edit that cannot exist');
  assert.equal(history.past.length, 0);
});

// --------------------------------------------------------------------- trim box

const BOUNDS: ScanBounds = {
  splats: 4_963_155,
  lo: { x: -10, y: -2, z: -6 },
  hi: { x: 10, y: 8, z: 14 },
  centre: { x: 0, y: 0, z: 0 },
  span: { x: 20, y: 10, z: 20 },
};

test('the default trim box is the measured extent with half a metre of margin', () => {
  load(makeJourney({
    editorFrame: { ...makeJourney().editorFrame, bounds: BOUNDS },
  }));
  const trim = defaultTrim();

  assert.deepEqual(trim.position, { x: 0, y: 3, z: 4 }, 'centred between lo and hi');
  // A half-extent, matching spark's box SDF — half the span, plus the margin.
  assert.deepEqual(trim.halfExtent, { x: 10.5, y: 5.5, z: 10.5 });
  assert.deepEqual([...trim.rotation], [0, 0, 0, 1], 'axis-aligned until someone rotates it');
  assert.equal(trim.enabled, true);
});

test('an unstamped journey still gets a box, from the fallback extent', () => {
  load();
  const trim = defaultTrim();
  assert.deepEqual(trim.position, { x: 0, y: 2.5, z: 0 });
  assert.deepEqual(trim.halfExtent, { x: 20.5, y: 8, z: 20.5 });
});

test('a partial bounds block is a failure, not a wrong box', () => {
  // `bounds` comes from tools/stamp-bounds.py and carries lo/hi percentile corners. A block
  // with only a span — which is what someone hand-writing one tends to produce — has no
  // corners to centre on, and this throws rather than fitting the box to invented numbers.
  load(makeJourney({
    editorFrame: { ...makeJourney().editorFrame, bounds: { span: { x: 20, y: 10, z: 20 } } },
  }));
  assert.throws(() => defaultTrim(), TypeError);
});

test('a journey with no trim gets one, switched off', () => {
  // Off, because the box is created the moment anything asks for it — including the readout —
  // and a box that started out trimming would delete splats nobody asked to lose.
  const journey = load();
  const trim = ensureTrim();
  assert.equal(trim.enabled, false);
  assert.equal(journey.editorFrame.trim, trim, 'and it is stored on the document');
});

test('an authored box is returned untouched', () => {
  const journey = load();
  const authored = {
    enabled: true,
    position: { x: -1.262, y: -0.784, z: -1.62 },
    rotation: [0, 0.39564, 0, 0.91841] as const,
    halfExtent: { x: 7.466, y: 2.005, z: 3.476 },
  };
  journey.editorFrame.trim = { ...authored };
  const trim = ensureTrim();
  assert.deepEqual(trim, authored, 'the authored extent survives being read');
});

test('an older axis-aligned box is migrated in place', () => {
  const journey = load();
  // What drafts carried before the trim became an oriented box.
  journey.editorFrame.trim = {
    enabled: true, min: { x: -4, y: -1, z: -6 }, max: { x: 6, y: 3, z: 4 },
  } as unknown as TrimBox;

  const trim = ensureTrim();
  assert.deepEqual(trim.position, { x: 1, y: 1, z: -1 });
  assert.deepEqual(trim.halfExtent, { x: 5, y: 2, z: 5 });
  assert.deepEqual([...trim.rotation], [0, 0, 0, 1]);
  assert.equal(trim.enabled, true, 'a box that was trimming keeps trimming');
  assert.equal(journey.editorFrame.trim, trim, 'and the document now holds the new shape');
});

test('a flat legacy box keeps a usable thickness', () => {
  // A zero half-extent is a box with no inside, so the SDF would delete the whole scan.
  const journey = load();
  journey.editorFrame.trim = {
    enabled: false, min: { x: 0, y: 2, z: 0 }, max: { x: 4, y: 2, z: 4 },
  } as unknown as TrimBox;
  assert.equal(ensureTrim().halfExtent.y, 0.05);
});

// --------------------------------------------------------------------- clips

test('a layered clip id resolves through its base entry', () => {
  load();
  state.audio = { clips: [{ clipId: 'tree-creek-waterplants-1', file: 'x.mp3' }] };

  // The catalogue lists the base clip; the journey names the layer. Both far and intimate are
  // packaged files of their own, so the URL keeps the suffix that the lookup dropped.
  assert.equal(clipUrl('tree-creek-waterplants-1--far'),
    '/audio/tree-creek-waterplants-1--far.mp3');
  assert.equal(clipUrl('tree-creek-waterplants-1'), '/audio/tree-creek-waterplants-1.mp3');
});

test('a clip with no packaged file resolves to nothing', () => {
  load();
  state.audio = { clips: [{ clipId: 'gone', missing: true, source: 'gone.wav' }] };
  assert.equal(clipUrl('gone--mid'), null, 'a missing source is not a URL');
  assert.equal(clipUrl('never-catalogued'), null);
  assert.equal(clipUrl(''), null);
});

test('a clip id is escaped into its URL', () => {
  load();
  state.audio = { clips: [{ clipId: 'two words' }] };
  assert.equal(clipUrl('two words'), '/audio/two%20words.mp3');
});

// --------------------------------------------------------------------- readouts

test('the yaw readout reads the trim box rotation', () => {
  assert.deepEqual(quaternionToEulerDegrees([0, 0, 0, 1]), { x: 0, y: 0, z: 0 });

  // The garden's authored box, which sits at about 46.6 degrees across the scan.
  const yaw = quaternionToEulerDegrees([0, 0.39564, 0, 0.91841]).y;
  assert.ok(Math.abs(yaw - 46.6) < 0.1, `expected about 46.6 degrees, got ${yaw}`);
});

test('the yaw readout clamps at the pole rather than returning NaN', () => {
  // asin() outside [-1, 1] is NaN, and a NaN readout is indistinguishable from a broken gizmo.
  const half = Math.SQRT1_2;
  assert.ok(Math.abs(quaternionToEulerDegrees([0, half, 0, half]).y - 90) < 1e-6);
  assert.ok(Math.abs(quaternionToEulerDegrees([0, -half, 0, half]).y + 90) < 1e-6);
  assert.ok(Number.isFinite(quaternionToEulerDegrees([0, 1, 0, 0]).y), 'and never NaN');
});

test('a new beat takes the first id nobody is using', () => {
  assert.equal(uniqueId('beat', []), 'beat-1');
  assert.equal(uniqueId('beat', ['beat-1', 'beat-2', 'beat-4']), 'beat-3');
});

test('a beat title cannot inject markup into the rail', () => {
  // The rail and the inspector are built with innerHTML, and a title is free text.
  assert.equal(escapeHtml('<script>alert(1)</script>'),
    '&lt;script&gt;alert(1)&lt;/script&gt;');
  assert.equal(escapeHtml(`"Otter's" & <b>`), '&quot;Otter&#39;s&quot; &amp; &lt;b&gt;');
  assert.equal(escapeHtml(undefined), '', 'an untitled beat is empty, not "undefined"');
});
