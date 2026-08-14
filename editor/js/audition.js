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
 */

const LAYERS = ['far', 'mid', 'intimate'];
const RESONANCE_URL = '/vendor/resonance-audio/build/resonance-audio.js';

/** A creek in the open: mostly absorptive, a little reflection off water and rock. */
const OUTDOOR_ROOM = {
  dimensions: { width: 30, height: 12, depth: 40 },
  materials: {
    left: 'grass', right: 'grass',
    front: 'grass', back: 'grass',
    up: 'transparent',          // open sky — no ceiling reflection
    down: 'water-or-ice-surface',
  },
};

export class Audition {
  constructor({ onState } = {}) {
    this.ctx = null;
    this.scene = null;
    this.sources = new Map();
    this.enabled = false;
    this.muted = false;
    this.onState = onState;
    this.missing = new Set();
    this.catalogue = null;
  }

  static async #loadResonance() {
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
  async enable() {
    if (this.ctx) {
      if (this.ctx.state === 'suspended') await this.ctx.resume();
      this.enabled = true;
      this.onState?.();
      return;
    }

    const ResonanceAudio = await Audition.#loadResonance();
    this.ctx = new (window.AudioContext ?? window.webkitAudioContext)();

    // Third order is the highest Resonance supports and the most directionally precise. On a
    // laptop rendering a dozen sources this is comfortably affordable.
    this.scene = new ResonanceAudio(this.ctx, { ambisonicOrder: 3 });
    this.scene.setRoomProperties(OUTDOOR_ROOM.dimensions, OUTDOOR_ROOM.materials);

    this.master = this.ctx.createGain();
    this.master.gain.value = 0.9;
    this.scene.output.connect(this.master);
    this.master.connect(this.ctx.destination);

    this.enabled = true;
    if (this.journey) await this.load(this.journey, this.clipUrlFor);
    this.onState?.();
  }

  suspend() {
    this.enabled = false;
    for (const [, src] of this.sources) this.#silence(src);
    this.onState?.();
  }

  setMuted(muted) {
    this.muted = muted;
    this.master?.gain.setTargetAtTime(muted ? 0 : 0.9, this.ctx.currentTime, 0.05);
    this.onState?.();
  }

  setCatalogue(catalogue) { this.catalogue = catalogue; }

  async load(journey, clipUrlFor) {
    this.journey = journey;
    this.clipUrlFor = clipUrlFor;
    this.missing.clear();
    if (!this.ctx || !this.scene) return;

    for (const [, src] of this.sources) this.#teardown(src);
    this.sources.clear();

    for (const node of [...(journey?.beats ?? []), ...(journey?.ambient ?? [])]) {
      if (!node.audio) continue;
      const built = this.#buildSource(node);
      if (built) this.sources.set(node.id, built);
    }
    this.onState?.();
  }

  #buildSource(node) {
    const reach = node.audibleRadiusM ?? (node.trigger?.exitRadiusM ?? 6) * 3.5;
    const layers = {};

    for (const name of LAYERS) {
      const spec = node.audio[name];
      if (!spec?.clipId) continue;
      const url = this.clipUrlFor(spec.clipId);
      if (!url) { this.missing.add(spec.clipId); continue; }

      const el = new Audio(url);
      el.loop = spec.loop !== false;
      el.preload = 'auto';
      el.crossOrigin = 'anonymous';

      let media;
      try { media = this.ctx.createMediaElementSource(el); } catch { continue; }

      const gain = this.ctx.createGain();
      gain.gain.value = 0;
      media.connect(gain);

      // One Resonance source per layer, all at the same point, so each layer gets its own
      // properly spatialised path rather than being summed before spatialisation.
      const source = this.scene.createSource();
      source.setPosition(node.position.x, node.position.y, node.position.z);
      source.setMinDistance(1);
      source.setMaxDistance(Math.max(12, reach * 1.6));
      // Gentle: the layer crossfade is doing the work of conveying distance, so a steep gain
      // law on top of it would double-count and make everything disappear at once.
      source.setRolloff('logarithmic');
      // Slightly forward-biased rather than omni — a creek source faces the water.
      source.setDirectivityPattern(0.25, 1.5);
      gain.connect(source.input);

      layers[name] = { el, gain, source, db: spec.gainDb ?? -8 };
    }

    if (!Object.keys(layers).length) return null;
    return { node, layers, reach, playing: false };
  }

  /** Called every frame of the simulation. */
  update(position, forward) {
    if (!this.ctx || !this.enabled || !this.scene) return;
    const t = this.ctx.currentTime;

    this.scene.setListenerPosition(position.x, position.y, position.z);
    this.scene.setListenerOrientation(forward.x, forward.y, forward.z, 0, 1, 0);

    for (const [, src] of this.sources) {
      const p = src.node.position;
      const distance = Math.hypot(position.x - p.x, position.y - p.y, position.z - p.z);

      if (distance > src.reach * 1.35) { this.#silence(src); continue; }
      this.#ensurePlaying(src);

      // Intimate inside roughly a third of the reach, far beyond two thirds, mid across the
      // middle. What you hear approaching is the recording changing, not the fader moving.
      const n = Math.min(1, distance / Math.max(src.reach, 0.001));
      const weight = {
        intimate: clamp01(1 - n / 0.35),
        mid: clamp01(1 - Math.abs(n - 0.5) / 0.35),
        far: clamp01((n - 0.45) / 0.4),
      };

      for (const [name, layer] of Object.entries(src.layers)) {
        const linear = dbToLinear(layer.db) * (weight[name] ?? 0);
        layer.gain.gain.setTargetAtTime(linear, t, 0.08);
      }
    }
  }

  /** A discrete confirmation — the moment a beat completes. */
  fireCompletion(beat) {
    if (!this.ctx || !this.enabled) return;
    const spec = beat?.audio?.completion;
    if (!spec?.clipId) return;
    const url = this.clipUrlFor(spec.clipId);
    if (!url) { this.missing.add(spec.clipId); return; }

    // Deliberately head-relative rather than spatialised: a confirmation is about *you*,
    // not about a place in the world.
    const el = new Audio(url);
    el.volume = clamp01(dbToLinear(spec.gainDb ?? -3));
    el.play().catch(() => {});
  }

  #ensurePlaying(src) {
    if (src.playing) return;
    src.playing = true;
    for (const [, layer] of Object.entries(src.layers)) layer.el.play().catch(() => {});
  }

  #silence(src) {
    if (!src.playing) return;
    src.playing = false;
    const t = this.ctx?.currentTime ?? 0;
    for (const [, layer] of Object.entries(src.layers)) {
      layer.gain.gain.setTargetAtTime(0, t, 0.12);
      setTimeout(() => { try { layer.el.pause(); } catch {} }, 400);
    }
  }

  #teardown(src) {
    for (const [, layer] of Object.entries(src.layers)) {
      try { layer.el.pause(); layer.el.removeAttribute('src'); layer.el.load(); } catch {}
      try { layer.gain.disconnect(); } catch {}
    }
  }

  /** What the UI needs to tell the truth about what is and is not loaded. */
  status() {
    return {
      ready: !!this.ctx && this.enabled,
      sources: this.sources.size,
      missing: [...this.missing],
      engine: 'Resonance Audio (approximation of Apple PHASE)',
    };
  }
}

function clamp01(v) { return Math.max(0, Math.min(1, v)); }
function dbToLinear(db) { return 10 ** (db / 20); }
