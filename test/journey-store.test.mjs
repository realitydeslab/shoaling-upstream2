/**
 * The store, which is where authored work can actually be lost.
 *
 * The rule these tests defend is in docs/devlog.md: anything a person positioned by hand is
 * data, not output. The editor saves the trim box, the walking path and the beats through
 * separate endpoints precisely so a background write of one cannot clobber an in-progress edit
 * of another, and that separation is only real if the patch methods genuinely leave the rest of
 * the document alone.
 */

import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import path from 'node:path';

import { JourneyStore } from '../service/src/journey-store.mjs';
import { makeDataRoot, makeJourney, makeBeat, SLUG } from './helpers.mjs';

/** The trim box the artist positioned at UBC, as a realistic shape to patch with. */
const FULL_TRIM = {
  enabled: true,
  position: { x: -1.262, y: -0.784, z: -1.62 },
  rotation: [0, 0.39564, 0, 0.91841],
  halfExtent: { x: 7.466, y: 2.005, z: 3.476 },
};

async function withStore(run, journey) {
  const { journeysRoot, dir, cleanup } = await makeDataRoot(journey);
  try {
    await run(new JourneyStore(journeysRoot), dir);
  } finally {
    await cleanup();
  }
}

test('a draft round-trips', async () => {
  await withStore(async (store) => {
    const draft = await store.readDraft(SLUG);
    assert.equal(draft.site.slug, SLUG);
    assert.equal(draft.beats.length, 2);
  });
});

test('writeDraft rejects a journey that fails validation, and leaves the old one on disk',
  async () => {
    await withStore(async (store, dir) => {
      const broken = makeJourney();
      broken.beats[0].interaction = 'teleport';       // not a known interaction

      await assert.rejects(() => store.writeDraft(SLUG, broken), (err) => {
        assert.equal(err.code, 'INVALID_JOURNEY');
        assert.ok(err.errors.length > 0);
        return true;
      });

      // The point of refusing is that the good version survives.
      const onDisk = JSON.parse(await readFile(path.join(dir, 'draft.json'), 'utf8'));
      assert.equal(onDisk.beats[0].interaction, 'proximity');
    });
  });

test('patchEditorFrame stores the trim box without disturbing the journey', async () => {
  await withStore(async (store) => {
    const trim = {
      enabled: true,
      position: { x: -1.262, y: -0.784, z: -1.62 },
      rotation: [0, 0.39564, 0, 0.91841],
      halfExtent: { x: 7.466, y: 2.005, z: 3.476 },
    };
    await store.patchEditorFrame(SLUG, { trim });

    const draft = await store.readDraft(SLUG);
    assert.deepEqual(draft.editorFrame.trim, trim);
    // The rest of the document is untouched — this is the whole reason the endpoint is separate.
    assert.equal(draft.beats.length, 2);
    assert.equal(draft.site.centreline.length, 3);
  });
});

test('patchEditorFrame preserves keys it was not given', async () => {
  await withStore(async (store) => {
    await store.patchEditorFrame(SLUG, { bounds: { span: { x: 34, y: 12, z: 28 } } });
    await store.patchEditorFrame(SLUG, { trim: { ...FULL_TRIM, enabled: false } });

    const draft = await store.readDraft(SLUG);
    assert.ok(draft.editorFrame.bounds, 'measured bounds survived a later trim write');
    assert.equal(draft.editorFrame.trim.enabled, false);
    // Disabling must keep the authored extent. Growing the box to 1e5 to "let everything
    // through" once overwrote a hand-positioned trim, unrecoverably — see docs/devlog.md.
    assert.equal(draft.editorFrame.trim.halfExtent.x, FULL_TRIM.halfExtent.x);
  });
});

test('a trim patch missing its geometry is refused rather than half-applied', async () => {
  await withStore(async (store) => {
    await store.patchEditorFrame(SLUG, { trim: FULL_TRIM });
    await assert.rejects(() => store.patchEditorFrame(SLUG, { trim: { enabled: false } }),
      (err) => err.code === 'INVALID_JOURNEY');

    // The authored box is still intact, which is the point of failing closed.
    const draft = await store.readDraft(SLUG);
    assert.equal(draft.editorFrame.trim.enabled, true);
    assert.deepEqual(draft.editorFrame.trim.halfExtent, FULL_TRIM.halfExtent);
  });
});

test('patchSite recomputes every beat s when the path moves', async () => {
  await withStore(async (store) => {
    // Halve the reach: the same beats now sit at twice their fraction along it.
    await store.patchSite(SLUG, {
      centreline: [
        { x: 0, y: 1.4, z: 0 },
        { x: 5, y: 1.4, z: 0 },
      ],
    });

    const draft = await store.readDraft(SLUG);
    assert.equal(draft.beats[0].s, 2, 'beat at x=2 is 2 m along the new path');
    // The beat at x=7 is beyond the new 5 m end, so it clamps there rather than extrapolating.
    assert.equal(draft.beats[1].s, 5);
  });
});

test('patchSite never moves a beat — path and place are separate', async () => {
  await withStore(async (store) => {
    const before = (await store.readDraft(SLUG)).beats.map((b) => ({ ...b.position }));
    await store.patchSite(SLUG, {
      centreline: [
        { x: 0, y: 1.4, z: 9 },     // shifted 9 m across
        { x: 10, y: 1.4, z: 9 },
      ],
    });
    const after = (await store.readDraft(SLUG)).beats.map((b) => b.position);
    assert.deepEqual(after, before, 'beats stayed where they were put');
  });
});

test('publish writes an immutable numbered revision and leaves the draft editable', async () => {
  await withStore(async (store) => {
    const first = await store.publish(SLUG);
    assert.equal(first.revision, 1);

    // Keep editing after publishing; the revision must not follow.
    const draft = await store.readDraft(SLUG);
    draft.title = 'Renamed after publishing';
    await store.writeDraft(SLUG, draft);

    const published = await store.readPublished(SLUG);
    assert.equal(published.title, 'Test Creek', 'the published revision is frozen');

    const second = await store.publish(SLUG);
    assert.equal(second.revision, 2, 'revisions are append-only and monotonic');
    assert.equal((await store.readPublished(SLUG)).title, 'Renamed after publishing');

    const revisions = await store.listRevisions(SLUG);
    assert.equal(revisions.length, 2);
    assert.equal((await store.readRevision(SLUG, 1)).title, 'Test Creek',
      'the earlier revision is still readable');
  });
});

test('readPublished fails cleanly when nothing has been published', async () => {
  await withStore(async (store) => {
    await assert.rejects(() => store.readPublished(SLUG));
  });
});

test('a slug that could escape the data directory is refused', async () => {
  await withStore(async (store) => {
    for (const bad of ['../etc', 'a/b', 'UPPER', '', '.']) {
      assert.throws(() => store.siteDir(bad), /invalid site slug/,
        `"${bad}" should not resolve to a site directory`);
    }
  });
});

test('listSites finds the sites that exist', async () => {
  await withStore(async (store) => {
    const sites = await store.listSites();
    assert.equal(sites.length, 1);
    assert.equal(sites[0].slug, SLUG);
  });
});

test('a beat added off the path still gets a sensible s', async () => {
  const journey = makeJourney();
  // Four metres along, three metres off to the side — a beat on the far bank.
  journey.beats.push(makeBeat('bank', 4, { position: { x: 4, y: 0, z: 3 }, s: 0 }));
  await withStore(async (store) => {
    await store.patchSite(SLUG, { centreline: journey.site.centreline });
    const draft = await store.readDraft(SLUG);
    const bank = draft.beats.find((b) => b.id === 'bank');
    assert.equal(bank.s, 4, 'cross-track distance does not contribute to s');
  }, journey);
});
