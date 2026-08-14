/**
 * Generate starting journey drafts for each site from measured splat geometry.
 *
 * These are scaffolds, not compositions: they put six beats on the creek axis at sane
 * spacing so the editor has something to open and the app has something to load. Real
 * placement happens in the editor against the splat, and every coordinate here is
 * provisional until the editor frame is calibrated.
 *
 * Run:  node tools/seed-journeys.ts [--force] [--reset-all]
 *
 *   --force      regenerate beats and audio, but CARRY OVER authored configuration:
 *                the trim box, a hand-drawn walking path, measured bounds, calibration.
 *   --reset-all  discard those too. Backs up the old draft first regardless.
 */

import { mkdir, writeFile, access, readFile, copyFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { validateJourney, SCHEMA_VERSION } from '../service/src/journey-schema.ts';
import type {
  AmbientSource, Beat, BeatAudio, Interaction, JourneyDocument, SiteRef, Vec3,
} from '../editor/src/types.ts';

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const OUT = path.join(ROOT, 'data', 'journeys');
const force = process.argv.includes('--force');
// --force regenerates the *content* of a journey. It deliberately does NOT discard authored
// configuration: the trim box, the walking path and the measured bounds are things a person
// positioned by hand, sometimes over a long session, and they are not reproducible from a
// seed. Re-running the seed used to wipe them, which cost real work. Use --reset-all to
// throw those away too, and know that you are doing it.
const resetAll = process.argv.includes('--reset-all');

interface SeedSite {
  slug: string;
  title: string;
  nianticOrgId: string;
  nianticSiteId: string;
  vpsAssetId: string;
  splatFile: string;
  /** Which local axis the reach runs along. */
  axis: 'x' | 'y' | 'z';
  from: number;
  to: number;
  /** The other two coordinates, held constant across the reach. */
  lateral: Partial<Vec3>;
  note: string;
}

/**
 * What this script actually writes.
 *
 * Two departures from `JourneyDocument`, both pre-existing and both left alone: the seed adds a
 * top-level `note`, which the validator neither requires nor forbids; and it omits `site.title`,
 * which types.ts declares as required but which no stored draft has ever carried and nothing
 * reads. Adding one here would change the bytes on disk, so it is described rather than fixed.
 */
type SeededJourney = Omit<JourneyDocument, 'site'> & {
  note: string;
  site: Omit<SiteRef, 'title'>;
};

/**
 * Measured with tools/spz_bounds.py at the 1st/99th percentile — raw min/max is meaningless
 * because every scan carries floaters 400-1200 m out.
 */
const SITES: SeedSite[] = [
  {
    slug: 'ubc-nitobe-garden-creek',
    title: 'Shoaling Upstream — UBC garden creek',
    nianticOrgId: '62e5d093-9275-48d5-9f4a-16b1a1d917a7',
    nianticSiteId: '1a1df855-d2fc-4337-ae6f-c0c2277584b2',
    vpsAssetId: 'ce4dd8c5-4b83-4081-8efb-b5d38b0a5f50',
    splatFile: 'ubc-nitobe-garden-creek.proxy.spz',
    // long axis X, span 34.0 m; ground near y = -1.0
    axis: 'x',
    from: -12.6,
    to: 21.4,
    lateral: { y: -0.6, z: -0.35 },
    note: 'Splat asset is NOT set to production in the portal, so device localization will '
        + 'fail here until it is promoted. Upstream direction along +X is unconfirmed.',
  },
  {
    slug: 'ucb-strawberry-creek-south',
    title: 'Shoaling Upstream — Strawberry Creek South',
    nianticOrgId: 'f35a6672-fc09-4c74-ad33-488c721850fb',
    nianticSiteId: '187bbd62-4d21-480e-8f8d-e484754aa22b',
    vpsAssetId: 'aa815b01-bed7-416c-9d52-b5aef03892f4',
    splatFile: 'ucb-strawberry-creek-south.proxy.spz',
    // long axis Z, span 61.4 m; ground near y = -0.5
    axis: 'z',
    from: -12.2,
    to: 49.2,
    lateral: { x: -9.0, y: -0.5 },
    note: 'Upstream is east and rises ~2.3 m across the mapped reach. Which local axis is '
        + 'east has not been confirmed; +Z is a guess to be fixed at calibration.',
  },
];

interface SeedBeat {
  id: string;
  title: string;
  interaction: Interaction;
  /** Fraction along the reach, so one structure lands on both a 34 m and a 61 m site. */
  at: number;
  clip: string;
  completion: string | null;
  givesFish?: number;
  prompt: string;
}

/**
 * The six beats. Positions are fractions along the reach so the same structure lands on
 * both a 34 m and a 61 m site. Radii are absolute metres and get re-scaled below, because
 * a gate has to relate to real position error, not to the length of the creek.
 */
const BEATS: SeedBeat[] = [
  {
    id: 'tree',
    title: 'The tree',
    interaction: 'proximity',
    at: 0.08,
    clip: 'tree-creek-waterplants-1',
    completion: null,
    prompt: 'Come in under the branches where the water runs slow.',
  },
  {
    id: 'redd',
    title: 'The gravel bed',
    interaction: 'crouch',
    at: 0.26,
    clip: 'chapter-1-new-life',
    completion: 'lay-egg',
    prompt: 'Get low enough to see into the gravel.',
  },
  {
    id: 'strider',
    title: 'Water striders',
    interaction: 'catch',
    at: 0.44,
    clip: 'strider',
    completion: 'eat-strider',
    prompt: 'Something is moving on the surface.',
  },
  {
    id: 'heron',
    title: 'The heron',
    interaction: 'give',
    at: 0.62,
    clip: 'heron',
    completion: null,
    givesFish: 12,
    prompt: 'It has been waiting. Let some of the shoal go.',
  },
  {
    id: 'barrier',
    title: 'The barrier',
    interaction: 'lift',
    at: 0.80,
    clip: 'chapter-4-returning-home',
    completion: 'jump',
    prompt: 'Lift yourself over. Water is above you again on the other side.',
  },
  {
    id: 'headwater',
    title: 'The headwater',
    interaction: 'crouch',
    at: 0.95,
    clip: 'chapter-5-rebirth',
    completion: 'lay-egg',
    prompt: 'This is as far upstream as the creek goes.',
  },
];

interface LayerGains {
  far?: number;
  mid?: number;
  intimate?: number;
}

/** Three layers per source. Distance is carried by content, not by gain. */
function layers(clipId: string, { far = -14, mid = -8, intimate = -4 }: LayerGains = {}): BeatAudio {
  return {
    far: { clipId: `${clipId}--far`, gainDb: far, loop: true },
    mid: { clipId: `${clipId}--mid`, gainDb: mid, loop: true },
    intimate: { clipId: `${clipId}--intimate`, gainDb: intimate, loop: true },
  };
}

function buildJourney(site: SeedSite): SeededJourney {
  const length = Math.abs(site.to - site.from);
  const sign = site.to >= site.from ? 1 : -1;

  const pointAt = (fraction: number): Vec3 => {
    const along = site.from + sign * length * fraction;
    const p: Vec3 = { x: 0, y: 0, z: 0, ...site.lateral };
    p[site.axis] = along;
    return p;
  };

  const centreline = [pointAt(0), pointAt(0.5), pointAt(1)];

  // Gate geometry scales gently with the reach so a 34 m site does not end up with beats
  // whose exit bands swallow their neighbours, but never shrinks below what pose jitter
  // demands.
  const spacing = length / (BEATS.length - 1);
  const enter = Math.max(2.0, Math.min(3.0, spacing * 0.32));
  const exit = enter * 1.6;

  const beats: Beat[] = BEATS.map((beat) => {
    const position = pointAt(beat.at);
    const s = length * beat.at;
    const audio = layers(beat.clip);
    const node: Beat = {
      id: beat.id,
      title: beat.title,
      prompt: beat.prompt,
      interaction: beat.interaction,
      position,
      s: Number(s.toFixed(2)),
      trigger: {
        enterRadiusM: Number(enter.toFixed(2)),
        exitRadiusM: Number(exit.toFixed(2)),
        dwellSeconds: 1.2,
        // Minimum hold, not hysteresis, is what stops thrash at this spacing. It turns
        // "am I inside the zone?" into "which beat am I performing?".
        minimumHoldSeconds: 25,
        requiresPreviousComplete: true,
      },
      audio,
    };
    if (beat.completion) {
      audio.completion = { clipId: beat.completion, gainDb: -3, loop: false };
    }
    if (beat.givesFish) node.givesFish = beat.givesFish;
    return node;
  });

  // The continuous bed. Unordered, never completes, and always playing — because a long
  // silence reads as the technology having broken, which is the single most reported
  // failure in this kind of work.
  const ambient: AmbientSource[] = [
    {
      id: 'creek-bed',
      title: 'The creek itself',
      position: pointAt(0.5),
      audibleRadiusM: Math.max(40, length),
      audio: layers('tree-creek-waterplants-2', { far: -20, mid: -17, intimate: -15 }),
    },
  ];

  return {
    schemaVersion: SCHEMA_VERSION,
    journeyId: site.slug,
    title: site.title,
    revision: 0,
    note: site.note,
    site: {
      slug: site.slug,
      nianticOrgId: site.nianticOrgId,
      nianticSiteId: site.nianticSiteId,
      vpsAssetId: site.vpsAssetId,
      anchorPayload: '',
      centreline,
      upstreamAxis: { kind: 'altitude', risesToward: 'unconfirmed' },
    },
    editorFrame: {
      splatFile: site.splatFile,
      // False until three physical points are matched on site. The app must refuse to run
      // an uncalibrated journey on device while still running it in simulation, because
      // authoring in splat space and shipping as anchor space is the most dangerous
      // mistake available in this system.
      calibrated: false,
      rotation: [0, 0, 0, 1],
      translation: { x: 0, y: 0, z: 0 },
      scale: 1,
    },
    beats,
    ambient,
    shoal: { startingCount: 40, minimumCount: 6 },
    safety: {
      short: 'Keep your eyes on the ground, not the screen.',
      full: 'Wear headphones that let the world through. Move at your own pace; nothing is '
          + 'timed. Do not enter the water where the bank is undercut or the footing is '
          + 'loose. The heron and the shoal are not real; the creek is.',
    },
  };
}

let wrote = 0;
for (const site of SITES) {
  const journey = buildJourney(site);
  const result = validateJourney(journey);

  if (!result.ok) {
    console.error(`\n  ${site.slug} FAILED validation:`);
    for (const e of result.errors) console.error(`    - ${e}`);
    process.exitCode = 1;
    continue;
  }

  const dir = path.join(OUT, site.slug);
  const file = path.join(dir, 'draft.json');

  const exists = await access(file).then(() => true).catch(() => false);
  if (exists && !force) {
    console.log(`  ${site.slug.padEnd(30)} skipped (draft exists; --force to overwrite)`);
    continue;
  }

  await mkdir(dir, { recursive: true });

  if (exists) {
    // Keep a copy before overwriting anything. Cheap, and the one time it matters it matters
    // a lot.
    const stamp = new Date().toISOString().replace(/[:.]/g, '-');
    await copyFile(file, path.join(dir, `draft.${stamp}.bak.json`));

    if (!resetAll) {
      const previous = JSON.parse(await readFile(file, 'utf8')) as JourneyDocument;
      const carried: string[] = [];

      if (previous.editorFrame?.trim) {
        journey.editorFrame.trim = previous.editorFrame.trim;
        carried.push('trim box');
      }
      if (previous.editorFrame?.bounds) {
        journey.editorFrame.bounds = previous.editorFrame.bounds;
        carried.push('scan bounds');
      }
      if (previous.editorFrame?.calibrated) {
        journey.editorFrame.calibrated = true;
        carried.push('calibration');
      }
      // A hand-drawn path has more than the three points the seed generates.
      if ((previous.site?.centreline?.length ?? 0) > 3) {
        journey.site.centreline = previous.site.centreline;
        carried.push(`walking path (${previous.site.centreline.length} pts)`);
      }
      if (carried.length) {
        console.log(`  ${site.slug.padEnd(30)} carried over: ${carried.join(', ')}`);
      }
    }
  }

  await writeFile(file, `${JSON.stringify(journey, null, 2)}\n`, 'utf8');
  wrote += 1;

  const reach = Math.abs(site.to - site.from);
  console.log(`  ${site.slug.padEnd(30)} ${reach.toFixed(1)} m reach · `
            + `${journey.beats.length} beats · gates ${journey.beats[0]!.trigger.enterRadiusM}/`
            + `${journey.beats[0]!.trigger.exitRadiusM} m`);
  for (const w of result.warnings) console.log(`      warning: ${w}`);
}

console.log(`\n  wrote ${wrote} draft${wrote === 1 ? '' : 's'} to data/journeys/\n`);
