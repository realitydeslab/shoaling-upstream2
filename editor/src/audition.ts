/**
 * Hearing the journey at a desk, through Resonance Audio.
 *
 * This is a stand-in for Apple PHASE, and the reason it is Resonance rather than a bare
 * Web Audio PannerNode is that Resonance is the only web renderer whose model actually lines
 * up with PHASE's:
 *
 *   PHASE                          Resonance Audio
 *   ---------------------------    ------------------------------------
 *   spatial mixer, HRTF            ambisonic encode + binaural decode
 *   source directivity             setDirectivityPattern(alpha, sharpness)
 *   rolloffFactor / cull distance  setRolloff / setMinDistance / setMaxDistance
 *   DirectPath/EarlyReflections/   room model with per-surface materials, which
 *     LateReverb sends               produces early reflections and late reverb
 *   ONE scene-wide reverb preset   ONE room, scene-wide  <- the same limitation
 *
 * That last row matters: PHASE gives you a single global reverb preset, so a piece whose
 * fifth beat is about squeezing through a constriction cannot give that spot its own
 * acoustic. Resonance has exactly the same shape, which means what you hear here is honest
 * about the constraint rather than flattering.
 *
 * What it still is NOT: PHASE's actual HRTF set, its geometric spreading, or AirPods head
 * tracking. Treat levels set here as provisional and re-set them at the creek — the site is
 * already making water noise and the composition has to sit inside it.
 *
 * The one rule it shares with the runtime: **distance is carried by content, not gain.** Each
 * source crossfades between three recordings (far / mid / intimate) as you approach, because
 * across 5-20 m the entire inverse-square budget is about 12 dB, which reads as "slightly
 * louder" rather than as arrival.
 *
 * The mix policy — why you hear one place at a time — is `mixAt` below, and the reasoning is
 * written out in `docs/audio-mix.md`.
 */

import type {
  AudioCatalogue, BeatAudio, JourneyDocument, Layer, Trigger, Vec3,
} from './types.js';

// Deliberately not imported from types.ts, which exports the same list: Node runs the test
// suite by stripping types, and it does not resolve a './types.js' specifier to types.ts, so a
// value import here would make this module unloadable from test/.
const LAYERS: readonly Layer[] = ['far', 'mid', 'intimate'];
const RESONANCE_URL = '/vendor/resonance-audio/build/resonance-audio.js';

// --------------------------------------------------------------------- Resonance, minimally

/**
 * Resonance Audio ships no type declarations. This is exactly the surface the audition uses,
 * written out rather than reached for with `any`, so that a library change turns up here as a
 * mismatch instead of as silence in the headphones.
 */
interface ResonanceSource {
  input: AudioNode;
  setPosition(x: number, y: number, z: number): void;
  setMinDistance(metres: number): void;
  setMaxDistance(metres: number): void;
  setRolloff(model: 'logarithmic' | 'linear' | 'none'): void;
  setDirectivityPattern(alpha: number, sharpness: number): void;
}

interface ResonanceRoomDimensions { width: number; height: number; depth: number }

/** One named material per surface, from Resonance's own material table. */
interface ResonanceRoomMaterials {
  left: string; right: string; front: string; back: string; up: string; down: string;
}

interface ResonanceScene {
  output: AudioNode;
  setRoomProperties(dimensions: ResonanceRoomDimensions, materials: ResonanceRoomMaterials): void;
  setListenerPosition(x: number, y: number, z: number): void;
  setListenerOrientation(
    fx: number, fy: number, fz: number,
    ux: number, uy: number, uz: number,
  ): void;
  createSource(): ResonanceSource;
}

interface ResonanceAudioConstructor {
  new (context: AudioContext, options?: { ambisonicOrder?: number }): ResonanceScene;
}

declare global {
  interface Window {
    /** Set by the script tag loaded from RESONANCE_URL; absent until then. */
    ResonanceAudio?: ResonanceAudioConstructor;
    /** Safari's prefix. Same constructor. */
    webkitAudioContext?: typeof AudioContext;
  }
}

// --------------------------------------------------------------------- what can be heard

/**
 * What the audition needs of a node: a place, some layers, and a reach.
 *
 * Beats and ambient sources both satisfy it and neither is the wider type — a beat derives its
 * reach from its trigger geometry, an ambient source states it outright.
 */
export interface AudibleNode {
  id: string;
  position: Vec3;
  audio?: BeatAudio;
  audibleRadiusM?: number;
  trigger?: Trigger;
}

/** Resolves a clip id to a URL, or to null when nothing is packaged for it. */
export type ClipUrlResolver = (clipId: string) => string | null;

/** One recording, spatialised on its own Resonance source. */
interface LayerVoice {
  el: HTMLAudioElement;
  gain: GainNode;
  source: ResonanceSource;
  db: number;
  /** Whether the element is running. A layer at zero gain is stopped, not merely silent. */
  running: boolean;
  /** The pending pause, so a layer that comes back inside the fade is not stopped underneath it. */
  stopping: ReturnType<typeof setTimeout> | null;
}

type SourceLayers = { [K in Layer]?: LayerVoice };

interface AuditionSource {
  node: AudibleNode;
  layers: SourceLayers;
  reach: number;
  playing: boolean;
  /** Ambient beds are the floor of the mix: always on, never ducked, never the leader. */
  ambient: boolean;
}

export interface AuditionOptions {
  onState?: () => void;
}

/** What the UI needs to tell the truth about what is and is not loaded. */
export interface AuditionStatus {
  ready: boolean;
  sources: number;
  missing: string[];
  engine: string;
}

/**
 * The one global reverb slot, deliberately empty.
 *
 * This used to be a 30x12x40 box with grass walls and a water floor, which is where most of the
 * "many reverb" came from. Three reasons it is now open air, in order of weight:
 *
 * 1. **The headphones are not noise-cancelling.** The visitor is standing in the real acoustic of
 *    a real creek bank, which is already supplying real early reflections and a real tail at full
 *    level. Adding a synthesised second room on top does not make the piece more spacious; it
 *    makes two rooms disagree, and the one the ear trusts is the one the body is standing in.
 * 2. **The walls did not exist.** Resonance's room model is a shoebox. Four grass walls at 15 and
 *    20 m returned reflections off surfaces that are not there, on a reach that is a bank and a
 *    slope. `up: 'transparent'` was already conceding the point for the sky.
 * 3. **It is still the honest stand-in.** PHASE gives the whole scene ONE reverb preset — beat 5
 *    cannot have its own acoustic — and that limitation is structural, so the single scene-wide
 *    room stays here rather than becoming per-source. What changed is only its content, to match
 *    the plan of record: bake space into the three recordings (`docs/audio-findings.md` §2, §6),
 *    run the global reverb dry, and EQ reality rather than rebuild it.
 *
 * All-transparent is Resonance's own default material set, and its coefficients are 1.000 across
 * every band — full absorption, so no early reflections and no tail. The dimensions are kept at
 * the size of the reach so that anything read back from here is not a fiction.
 */
const OPEN_AIR: { dimensions: ResonanceRoomDimensions; materials: ResonanceRoomMaterials } = {
  dimensions: { width: 30, height: 12, depth: 40 },
  materials: {
    left: 'transparent', right: 'transparent',
    front: 'transparent', back: 'transparent',
    up: 'transparent', down: 'transparent',
  },
};

// --------------------------------------------------------------------- the mix policy

/**
 * How much further than its reach a source stays in the graph before it is dropped.
 *
 * Also what `setMaxDistance` is set to, so Resonance's own rolloff arrives at zero exactly where
 * the cull happens. Previously the two disagreed — the rolloff still had a third of its curve
 * left when the source was cut — and the cull was therefore a step rather than an ending.
 */
const CULL_MARGIN = 1.35;

/** The floor of `reachFor`'s range, and the multiplier this file used to apply unconditionally. */
const REACH_MULTIPLE = 3.5;

/**
 * How hard the nearest point of interest wins.
 *
 * A rival at twice the distance of the nearest one sits `20*log10(2^-2)` = 12 dB down; at three
 * times, 19 dB. 1 would be no focus at all, and 3 makes the field snap.
 */
const FOCUS_EXPONENT = 2;

/** Under this a voice is not something a listener could name, so it is not worth a decoder. */
export const AUDIBLE_FLOOR = 10 ** (-42 / 20);

/** Resonance's `setMinDistance`, where the rolloff starts. */
const MIN_DISTANCE = 1;

/**
 * How far a source carries, from how far away the next point of interest is.
 *
 * `exitRadiusM * 3.5` — what this used to be, unconditionally — is a sensible tail for a beat
 * standing on its own, and `docs/audio-findings.md` §3 sizes the whole legibility argument around
 * "8-12 m spacing with ~25 m tails". The UBC garden reach is not that: it is 18.8 m long with six
 * beats 0.7-4 m apart, so a 10.6 m tail on each of them put every beat inside every other beat's
 * field for the entire walk. The multiple was never wrong; it was answering a question about one
 * beat while the problem was a question about six.
 *
 * So the reach is now bounded by the composition's own geometry: far enough that a beat is fully
 * audible everywhere it can fire (`exitRadiusM`), and no further than the point where its cull
 * radius reaches its nearest neighbour. An explicit `audibleRadiusM` always wins — that is the
 * author saying it outright, and the ambient beds do exactly that.
 *
 * With no neighbour given, this returns precisely the old value, so a lone source is unchanged.
 */
export function reachFor(node: AudibleNode, neighbourM = Infinity): number {
  if (node.audibleRadiusM != null) return node.audibleRadiusM;
  const exit = node.trigger?.exitRadiusM ?? 6;
  return Math.min(Math.max(neighbourM / CULL_MARGIN, exit), exit * REACH_MULTIPLE);
}

/** Where a source stops being rendered at all. Also its Resonance `maxDistance`. */
export function cullDistance(reach: number): number {
  return Math.max(reach * CULL_MARGIN, MIN_DISTANCE + 1);
}

/**
 * Horizontal distance to the nearest other node, or Infinity when there is no other.
 *
 * Horizontal for the same reason the blend is (see `mixAt`): the vertical separation between
 * these points is where the creek bed is, not how far apart they are along the walk.
 */
export function nearestNeighbourM(node: AudibleNode, among: readonly AudibleNode[]): number {
  let nearest = Infinity;
  for (const other of among) {
    if (other === node || other.id === node.id) continue;
    const d = Math.hypot(node.position.x - other.position.x, node.position.z - other.position.z);
    if (d < nearest) nearest = d;
  }
  return nearest;
}

/** A node with its reach already resolved, which is all the mix needs of it. */
export interface MixNode {
  node: AudibleNode;
  reach: number;
  /** Always-on bed. Exempt from the duck in both directions: never ducked, never ducks. */
  ambient?: boolean;
}

/** One source's share of the mix at one listener position. */
export interface MixVoice {
  id: string;
  /** Straight-line distance. What Resonance spatialises with, and what its rolloff uses. */
  distance: number;
  /** Horizontal distance. What chooses the recording. */
  blendDistance: number;
  /** The far / mid / intimate crossfade, before the duck. */
  weights: Record<Layer, number>;
  /** The duck: 1 for the nearest point of interest, less for everything behind it. */
  focus: number;
  /** Exactly what `update` writes to each layer's gain node. */
  gains: Record<Layer, number>;
  /** The above summed and rolled off — what actually reaches the ear. */
  amplitude: number;
  audible: boolean;
  /** Recordings that have to be running for this voice. Zero when it is silent. */
  loops: number;
}

export interface Mix {
  voices: MixVoice[];
  /** The point of interest the mix is currently about, or null between them. */
  leader: MixVoice | null;
  /** Summed amplitude of everything audible. */
  total: number;
  /** Total recordings running. This is the number the artist was hearing as "many reverb". */
  loops: number;
  /** Sources contributing anything at all. */
  audible: number;
}

/**
 * The whole mix at one listener position: the single model, played and measured.
 *
 * Two decisions live here.
 *
 * **The blend is horizontal; the spatialisation is not.** Beats are authored at bed height, in
 * the water, while the listener is a phone on a neck mount at chest height on the bank — a 0.2 to
 * 1.3 m vertical offset on this journey that is a fact about where the creek is, not about how
 * far the visitor still has to walk. It matters far more than it sounds: the intimate recording
 * only plays inside 0.35 of the reach, so under a straight 3D distance three of the six UBC beats
 * — tree, strider and falls, whose closest approach is 1.2-1.3 m in 3D but 0.05-0.61 m in plan —
 * could never reach their intimate layer at all, no matter where the visitor stood. So the
 * recording is chosen by distance across the ground, while Resonance still gets the true 3D
 * position and you still hear the redd from below you, which is the truth and is worth having.
 *
 * **The nearest point of interest is the subject.** `geom.ts` `evaluateAt` has always been
 * winner-take-all — one state machine, one firing beat — while the audio summed every source it
 * could reach. That asymmetry is the bug the artist heard: six ambiences at once, none of them
 * about anywhere. Each source is now scaled by `(nearest / its own distance) ^ 2`, which is
 * winner-take-all with the corners taken off: standing at a beat, it is the only thing playing;
 * standing midway between two, both are equal and you are crossing from one place into the next;
 * nothing ever snaps, because the ratio is continuous. It is the ducking of
 * `docs/audio-findings.md` §3c applied across space rather than across time.
 *
 * This is *not* the level-carries-distance mistake. Which recording you hear, and therefore how
 * near you are, is still decided entirely by the crossfade. The duck decides something else —
 * which of several places you are being told about — and gain is the correct tool for that.
 */
export function mixAt(listener: Vec3, nodes: readonly MixNode[]): Mix {
  const EPS = 1e-3;

  const measured = nodes.map((entry) => {
    const p = entry.node.position;
    const blendDistance = Math.hypot(listener.x - p.x, listener.z - p.z);
    const distance = Math.hypot(listener.x - p.x, listener.y - p.y, listener.z - p.z);
    return { entry, blendDistance, distance };
  });

  // The duck is measured against the nearest point of interest, so an ambient bed neither wins
  // the field nor is pushed out of it by a beat you happen to be standing on. Deliberately not
  // restricted to what is still in range: a nearest beat that drops out at its cull radius would
  // hand the reference to a further one, and every remaining voice would step up at that instant.
  let nearest = Infinity;
  for (const m of measured) {
    if (!m.entry.ambient && m.blendDistance < nearest) nearest = m.blendDistance;
  }

  const voices: MixVoice[] = [];
  let leader: MixVoice | null = null;
  let total = 0;
  let loops = 0;
  let audibleCount = 0;

  for (const { entry, blendDistance, distance } of measured) {
    const { node, reach } = entry;
    const maxD = cullDistance(reach);
    const weights = blendWeights(blendDistance / Math.max(reach, EPS));

    const focus = entry.ambient || !Number.isFinite(nearest)
      ? 1
      : clamp01((Math.max(nearest, EPS) / Math.max(blendDistance, EPS)) ** FOCUS_EXPONENT);

    const gains: Record<Layer, number> = { far: 0, mid: 0, intimate: 0 };
    let summed = 0;
    for (const name of LAYERS) {
      const spec = node.audio?.[name];
      if (!spec?.clipId) continue;
      const g = dbToLinear(spec.gainDb ?? -8) * weights[name] * focus;
      gains[name] = g;
      summed += g;
    }

    const amplitude = blendDistance > maxD ? 0 : summed * rolloffAt(distance, maxD);
    const audible = amplitude >= AUDIBLE_FLOOR;
    const voice: MixVoice = {
      id: node.id,
      distance,
      blendDistance,
      weights,
      focus,
      gains,
      amplitude,
      audible,
      // A layer at zero contributes nothing and is stopped, so it is not counted as running.
      loops: audible ? LAYERS.filter((n) => gains[n] > 0).length : 0,
    };

    voices.push(voice);
    if (audible) {
      total += amplitude;
      loops += voice.loops;
      audibleCount += 1;
      if (!entry.ambient && (!leader || amplitude > leader.amplitude)) leader = voice;
    }
  }

  return { voices, leader, total, loops, audible: audibleCount };
}

/**
 * The far / mid / intimate crossfade at normalised distance `n`.
 *
 * Intimate inside roughly a third of the reach, far beyond two thirds, mid across the middle.
 * What you hear approaching is the recording changing, not the fader moving.
 */
function blendWeights(n: number): Record<Layer, number> {
  const d = Math.min(1, n);
  return {
    intimate: clamp01(1 - d / 0.35),
    mid: clamp01(1 - Math.abs(d - 0.5) / 0.35),
    far: clamp01((d - 0.45) / 0.4),
  };
}

/**
 * Resonance's own 'logarithmic' rolloff, copied from its `attenuation.js` rather than assumed.
 *
 * The curve is 1/(d+1) offset by minDistance and renormalised so it reaches 0 at max, NOT a
 * logarithm despite the name. Guessing a log here once gave a half-life that was wrong by 20%.
 */
function rolloffAt(d: number, maxD: number): number {
  if (d > maxD) return 0;
  if (d <= MIN_DISTANCE) return 1;
  const range = maxD - MIN_DISTANCE;
  const att = 1 / (d - MIN_DISTANCE + 1);
  const attMax = 1 / (range + 1);
  return Math.max(0, (att - attMax) / (1 - attMax));
}

export class Audition {
  ctx: AudioContext | null = null;
  scene: ResonanceScene | null = null;
  master: GainNode | undefined;
  readonly sources = new Map<string, AuditionSource>();
  enabled = false;
  muted = false;
  readonly onState: (() => void) | undefined;
  readonly missing = new Set<string>();
  catalogue: AudioCatalogue | null = null;
  journey: JourneyDocument | null | undefined;
  clipUrlFor: ClipUrlResolver | undefined;
  /** The mix as of the last update: what is audible, how loud, and what it is about. */
  lastMix: Mix | null = null;

  constructor({ onState }: AuditionOptions = {}) {
    this.onState = onState;
  }

  static async #loadResonance(): Promise<ResonanceAudioConstructor> {
    if (window.ResonanceAudio) return window.ResonanceAudio;
    await new Promise((resolve, reject) => {
      const tag = document.createElement('script');
      tag.src = RESONANCE_URL;
      tag.onload = resolve;
      tag.onerror = () => reject(new Error('Resonance Audio failed to load'));
      document.head.appendChild(tag);
    });
    if (!window.ResonanceAudio) throw new Error('Resonance Audio loaded but exported nothing');
    return window.ResonanceAudio;
  }

  /** Must be called from a user gesture — browsers refuse to start audio otherwise. */
  async enable(): Promise<void> {
    if (this.ctx) {
      if (this.ctx.state === 'suspended') await this.ctx.resume();
      this.enabled = true;
      this.onState?.();
      return;
    }

    const ResonanceAudio = await Audition.#loadResonance();
    this.ctx = new (window.AudioContext ?? window.webkitAudioContext!)();

    // Third order is the highest Resonance supports and the most directionally precise. On a
    // laptop rendering a dozen sources this is comfortably affordable.
    this.scene = new ResonanceAudio(this.ctx, { ambisonicOrder: 3 });
    this.scene.setRoomProperties(OPEN_AIR.dimensions, OPEN_AIR.materials);

    this.master = this.ctx.createGain();
    this.master.gain.value = 0.9;
    this.scene.output.connect(this.master);
    this.master.connect(this.ctx.destination);

    this.enabled = true;
    // `clipUrlFor` is set by the same call that sets `journey`, so the second test only tells
    // the compiler what the first already guarantees.
    if (this.journey && this.clipUrlFor) await this.load(this.journey, this.clipUrlFor);
    this.onState?.();
  }

  suspend(): void {
    this.enabled = false;
    for (const [, src] of this.sources) this.#silence(src);
    this.onState?.();
  }

  setMuted(muted: boolean): void {
    this.muted = muted;
    // `master` and `ctx` are created together in enable(), so this one test covers both.
    if (this.master && this.ctx) {
      this.master.gain.setTargetAtTime(muted ? 0 : 0.9, this.ctx.currentTime, 0.05);
    }
    this.onState?.();
  }

  setCatalogue(catalogue: AudioCatalogue | null): void { this.catalogue = catalogue; }

  async load(
    journey: JourneyDocument | null | undefined,
    clipUrlFor: ClipUrlResolver,
  ): Promise<void> {
    this.journey = journey;
    this.clipUrlFor = clipUrlFor;
    this.missing.clear();
    if (!this.ctx || !this.scene) return;

    for (const [, src] of this.sources) this.#teardown(src);
    this.sources.clear();

    // Every beat's reach is bounded by how far away the next beat is, so this needs the whole
    // set before it can build any one of them. Ambient beds state their own radius and are not
    // points of interest, so they are not neighbours to anything.
    const beats = journey?.beats ?? [];
    for (const node of beats) {
      if (!node.audio) continue;
      const built = this.#buildSource(node, reachFor(node, nearestNeighbourM(node, beats)), false);
      if (built) this.sources.set(node.id, built);
    }
    for (const node of journey?.ambient ?? []) {
      if (!node.audio) continue;
      const built = this.#buildSource(node, reachFor(node), true);
      if (built) this.sources.set(node.id, built);
    }
    this.onState?.();
  }

  #buildSource(node: AudibleNode, reach: number, ambient: boolean): AuditionSource | null {
    // load() has already established all three; re-reading them here is what lets the compiler
    // see it, and costs nothing.
    const { ctx, scene, clipUrlFor } = this;
    if (!ctx || !scene || !clipUrlFor) return null;

    const layers: SourceLayers = {};

    for (const name of LAYERS) {
      const spec = node.audio?.[name];
      if (!spec?.clipId) continue;
      const url = clipUrlFor(spec.clipId);
      if (!url) { this.missing.add(spec.clipId); continue; }

      const el = new Audio(url);
      el.loop = spec.loop !== false;
      // Nothing is started here. A layer runs only while it has gain (see #setLayer), so a beat
      // four metres away is not holding three decoders open for a recording nobody can hear.
      el.preload = 'auto';
      el.crossOrigin = 'anonymous';

      let media: MediaElementAudioSourceNode;
      try { media = ctx.createMediaElementSource(el); } catch { continue; }

      const gain = ctx.createGain();
      gain.gain.value = 0;
      media.connect(gain);

      // One Resonance source per layer, all at the same point, so each layer gets its own
      // properly spatialised path rather than being summed before spatialisation.
      const source = scene.createSource();
      source.setPosition(node.position.x, node.position.y, node.position.z);
      source.setMinDistance(MIN_DISTANCE);
      // The rolloff now reaches zero exactly where the source is culled, so the two agree and
      // there is no step left at the boundary. It is also steeper than it was, which is what
      // `docs/audio-findings.md` §1 asks for — but the crossfade is still what carries distance.
      source.setMaxDistance(cullDistance(reach));
      source.setRolloff('logarithmic');
      // Slightly forward-biased rather than omni — a creek source faces the water.
      source.setDirectivityPattern(0.25, 1.5);
      gain.connect(source.input);

      layers[name] = { el, gain, source, db: spec.gainDb ?? -8, running: false, stopping: null };
    }

    if (!Object.keys(layers).length) return null;
    return { node, layers, reach, playing: false, ambient };
  }

  /** Called every frame of the simulation. */
  update(position: Vec3, forward: Vec3): void {
    if (!this.ctx || !this.enabled || !this.scene) return;
    const t = this.ctx.currentTime;

    this.scene.setListenerPosition(position.x, position.y, position.z);
    this.scene.setListenerOrientation(forward.x, forward.y, forward.z, 0, 1, 0);

    const mix = mixAt(position, [...this.sources.values()].map(
      (src) => ({ node: src.node, reach: src.reach, ambient: src.ambient })));
    this.lastMix = mix;

    for (const voice of mix.voices) {
      const src = this.sources.get(voice.id);
      if (!src) continue;

      // Hysteresis around the floor, which mixAt deliberately does not have: it reports one
      // steady-state truth, while a walker standing still on the boundary would otherwise start
      // and stop the same recording several times a second.
      const on = voice.amplitude >= (src.playing ? AUDIBLE_FLOOR : AUDIBLE_FLOOR * 2.5);
      if (!on) { this.#silence(src); continue; }

      src.playing = true;
      for (const name of LAYERS) {
        const layer = src.layers[name];
        if (layer) this.#setLayer(layer, voice.gains[name], t);
      }
    }
  }

  /**
   * A layer's gain, and whether its recording is running at all.
   *
   * Gain is always ramped — never stepped — and the element is stopped only after the ramp has
   * had time to arrive, so nothing is ever cut mid-level. The pending stop is cancelled if the
   * layer comes back inside that window, which is what a visitor pacing on a boundary does.
   */
  #setLayer(layer: LayerVoice, target: number, t: number): void {
    layer.gain.gain.setTargetAtTime(target, t, 0.08);
    if (target > 0) {
      if (layer.stopping) { clearTimeout(layer.stopping); layer.stopping = null; }
      if (!layer.running) { layer.running = true; layer.el.play().catch(() => {}); }
    } else if (layer.running && !layer.stopping) {
      layer.stopping = setTimeout(() => {
        layer.stopping = null;
        layer.running = false;
        try { layer.el.pause(); } catch { /* already gone */ }
      }, 400);
    }
  }

  /** A discrete confirmation — the moment a beat completes. */
  fireCompletion(beat: AudibleNode | null | undefined): void {
    if (!this.ctx || !this.enabled) return;
    const spec = beat?.audio?.completion;
    if (!spec?.clipId) return;
    // `clipUrlFor` is unset only before the first load(), when nothing can have completed.
    const url = this.clipUrlFor?.(spec.clipId);
    if (!url) { this.missing.add(spec.clipId); return; }

    // Deliberately head-relative rather than spatialised: a confirmation is about *you*,
    // not about a place in the world.
    const el = new Audio(url);
    el.volume = clamp01(dbToLinear(spec.gainDb ?? -3));
    el.play().catch(() => {});
  }

  #silence(src: AuditionSource): void {
    if (!src.playing) return;
    src.playing = false;
    const t = this.ctx?.currentTime ?? 0;
    for (const layer of voicesOf(src)) this.#setLayer(layer, 0, t);
  }

  #teardown(src: AuditionSource): void {
    for (const layer of voicesOf(src)) {
      if (layer.stopping) { clearTimeout(layer.stopping); layer.stopping = null; }
      layer.running = false;
      try { layer.el.pause(); layer.el.removeAttribute('src'); layer.el.load(); } catch {}
      try { layer.gain.disconnect(); } catch {}
    }
  }

  /** What the UI needs to tell the truth about what is and is not loaded. */
  status(): AuditionStatus {
    return {
      ready: !!this.ctx && this.enabled,
      sources: this.sources.size,
      missing: [...this.missing],
      engine: 'Resonance Audio (approximation of Apple PHASE)',
    };
  }
}

/** The layers a source actually has. A partial record loses that on the way through. */
function voicesOf(src: AuditionSource): LayerVoice[] {
  const voices: LayerVoice[] = [];
  for (const name of LAYERS) {
    const voice = src.layers[name];
    if (voice) voices.push(voice);
  }
  return voices;
}

function clamp01(v: number): number { return Math.max(0, Math.min(1, v)); }
function dbToLinear(db: number): number { return 10 ** (db / 20); }

/** The reach, the peak, and the distance at which the beat has halved. */
export interface AudibleField {
  reach: number;
  maxD: number;
  peak: number;
  peakAt: number;
  half: number;
  amplitudeAt: (d: number) => number;
}

/**
 * The audible field of a beat, and its half-life radius.
 *
 * The half-life is the distance at which the beat's amplitude has fallen to half its peak —
 * −6 dB, the point where it stops being the thing you are listening to and becomes part of the
 * background. It is the honest radius to draw on the stage, because the trigger rings say where
 * the interaction arms and say nothing at all about where the sound reaches, and on this creek
 * the sound reaches several times further than the trigger. Two beats whose trigger rings do
 * not touch can still be audible over each other the whole way between them, and that is the
 * mistake an author cannot see from a map.
 *
 * It is computed by sampling the same law the audition actually plays through — the three-layer
 * crossfade multiplied by Resonance's logarithmic rolloff — rather than by a rule of thumb, so
 * it stays true if the law changes. Both are approximations of PHASE, which is the real target.
 */
export function audibleField(node: AudibleNode, reach = reachFor(node)): AudibleField | null {
  const maxD = cullDistance(reach);

  const amplitudeAt = (d: number): number => {
    const weight = blendWeights(d / Math.max(reach, 1e-3));
    let a = 0;
    for (const name of LAYERS) {
      const spec = node.audio?.[name];
      if (!spec?.clipId) continue;
      a += dbToLinear(spec.gainDb ?? -8) * weight[name];
    }
    return a * rolloffAt(d, maxD);
  };

  let peak = 0, peakAt = 0;
  for (let d = 0; d <= maxD; d += 0.05) {
    const a = amplitudeAt(d);
    if (a > peak) { peak = a; peakAt = d; }
  }
  if (peak <= 0) return null;

  let half = maxD;
  for (let d = peakAt; d <= maxD; d += 0.05) {
    if (amplitudeAt(d) <= peak * 0.5) { half = d; break; }
  }
  return { reach, maxD, peak, peakAt, half: +half.toFixed(2), amplitudeAt };
}
