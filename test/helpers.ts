/**
 * Shared fixtures.
 *
 * Every test builds its own journey rather than loading the garden draft. The garden draft is
 * authored work that changes whenever someone drags a beat, and a suite that reads it fails for
 * reasons that have nothing to do with the code — and worse, would tempt someone to "fix" a
 * test by editing the artwork.
 */

import { mkdtemp, mkdir, writeFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';

import type { Beat, JourneyDocument } from '../editor/src/types.ts';

export const SLUG = 'test-creek';

/** A minimal journey that passes validation. Callers mutate what they are testing. */
export function makeJourney(overrides: Partial<JourneyDocument> = {}): JourneyDocument {
  return {
    schemaVersion: '2.0',
    journeyId: 'test-creek-journey',
    title: 'Test Creek',
    revision: 0,
    site: {
      slug: SLUG,
      title: 'Test Creek',
      // Real journeys carry the Niantic identifiers that VPS localises against. The values are
      // fake, but the fields are required, and a fixture that skipped them would let a schema
      // change that broke every real draft pass the suite.
      nianticSiteId: 'test-site-0000',
      nianticOrgId: 'test-org-0000',
      // A straight 10 m reach along +X at chest height, so distances are checkable by hand.
      centreline: [
        { x: 0, y: 1.4, z: 0 },
        { x: 5, y: 1.4, z: 0 },
        { x: 10, y: 1.4, z: 0 },
      ],
    },
    // The transform from the scan's coordinates to the journey's. Identity here, which is what
    // an uncalibrated site has: the app runs it in simulation and refuses it on device.
    editorFrame: {
      calibrated: false,
      translation: { x: 0, y: 0, z: 0 },
      rotation: [0, 0, 0, 1],
      scale: 1,
    },
    shoal: { startingCount: 40, minimumCount: 8 },
    beats: [makeBeat('one', 2), makeBeat('two', 7)],
    ...overrides,
  };
}

export function makeBeat(id: string, alongX: number, overrides: Partial<Beat> = {}): Beat {
  return {
    id,
    title: `Beat ${id}`,
    prompt: 'Something happens here.',
    interaction: 'proximity',
    position: { x: alongX, y: 0, z: 0 },
    s: alongX,
    trigger: {
      enterRadiusM: 1.9,
      exitRadiusM: 3.04,
      dwellSeconds: 1.2,
      minimumHoldSeconds: 25,
      requiresPreviousComplete: true,
    },
    audio: {
      far: { clipId: 'clip--far', gainDb: -14, loop: true },
      mid: { clipId: 'clip--mid', gainDb: -8, loop: true },
      intimate: { clipId: 'clip--intimate', gainDb: -4, loop: true },
    },
    ...overrides,
  };
}

/**
 * A throwaway journeys directory laid out the way JourneyStore expects.
 *
 * `journeysRoot` is what the store takes: it joins the slug straight onto it, so this is the
 * directory that holds one subdirectory per site, not its parent.
 */
export async function makeDataRoot(journey: JourneyDocument = makeJourney()) {
  const base = await mkdtemp(path.join(tmpdir(), 'shoaling-test-'));
  const journeysRoot = path.join(base, 'journeys');
  const dir = path.join(journeysRoot, SLUG);
  await mkdir(dir, { recursive: true });
  await writeFile(path.join(dir, 'draft.json'), `${JSON.stringify(journey, null, 2)}\n`);
  return { base, journeysRoot, dir, cleanup: () => rm(base, { recursive: true, force: true }) };
}

/** Resolve when `check()` is true, or reject at the deadline. Keeps socket tests honest. */
export function until<T>(
  check: () => T | Promise<T>,
  { timeoutMs = 2500, label = 'condition' }: { timeoutMs?: number; label?: string } = {},
): Promise<T> {
  return new Promise<T>((resolve, reject) => {
    const started = Date.now();
    const poll = () => {
      let value: T | Promise<T>;
      try { value = check(); } catch (err) { return reject(err); }
      if (value) return resolve(value);
      if (Date.now() - started > timeoutMs) return reject(new Error(`timed out waiting for ${label}`));
      setTimeout(poll, 10);
    };
    poll();
  });
}
