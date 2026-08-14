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
// Deliberately not imported from types.ts, which exports the same list: Node runs the test
// suite by stripping types, and it does not resolve a './types.js' specifier to types.ts, so a
// value import here would make this module unloadable from test/.
const LAYERS = ['far', 'mid', 'intimate'];
const RESONANCE_URL = '/vendor/resonance-audio/build/resonance-audio.js';
/** A creek in the open: mostly absorptive, a little reflection off water and rock. */
const OUTDOOR_ROOM = {
    dimensions: { width: 30, height: 12, depth: 40 },
    materials: {
        left: 'grass', right: 'grass',
        front: 'grass', back: 'grass',
        up: 'transparent', // open sky — no ceiling reflection
        down: 'water-or-ice-surface',
    },
};
export class Audition {
    ctx = null;
    scene = null;
    master;
    sources = new Map();
    enabled = false;
    muted = false;
    onState;
    missing = new Set();
    catalogue = null;
    journey;
    clipUrlFor;
    constructor({ onState } = {}) {
        this.onState = onState;
    }
    static async #loadResonance() {
        if (window.ResonanceAudio)
            return window.ResonanceAudio;
        await new Promise((resolve, reject) => {
            const tag = document.createElement('script');
            tag.src = RESONANCE_URL;
            tag.onload = resolve;
            tag.onerror = () => reject(new Error('Resonance Audio failed to load'));
            document.head.appendChild(tag);
        });
        if (!window.ResonanceAudio)
            throw new Error('Resonance Audio loaded but exported nothing');
        return window.ResonanceAudio;
    }
    /** Must be called from a user gesture — browsers refuse to start audio otherwise. */
    async enable() {
        if (this.ctx) {
            if (this.ctx.state === 'suspended')
                await this.ctx.resume();
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
        // `clipUrlFor` is set by the same call that sets `journey`, so the second test only tells
        // the compiler what the first already guarantees.
        if (this.journey && this.clipUrlFor)
            await this.load(this.journey, this.clipUrlFor);
        this.onState?.();
    }
    suspend() {
        this.enabled = false;
        for (const [, src] of this.sources)
            this.#silence(src);
        this.onState?.();
    }
    setMuted(muted) {
        this.muted = muted;
        // `master` and `ctx` are created together in enable(), so this one test covers both.
        if (this.master && this.ctx) {
            this.master.gain.setTargetAtTime(muted ? 0 : 0.9, this.ctx.currentTime, 0.05);
        }
        this.onState?.();
    }
    setCatalogue(catalogue) { this.catalogue = catalogue; }
    async load(journey, clipUrlFor) {
        this.journey = journey;
        this.clipUrlFor = clipUrlFor;
        this.missing.clear();
        if (!this.ctx || !this.scene)
            return;
        for (const [, src] of this.sources)
            this.#teardown(src);
        this.sources.clear();
        for (const node of [...(journey?.beats ?? []), ...(journey?.ambient ?? [])]) {
            if (!node.audio)
                continue;
            const built = this.#buildSource(node);
            if (built)
                this.sources.set(node.id, built);
        }
        this.onState?.();
    }
    #buildSource(node) {
        // load() has already established all three; re-reading them here is what lets the compiler
        // see it, and costs nothing.
        const { ctx, scene, clipUrlFor } = this;
        if (!ctx || !scene || !clipUrlFor)
            return null;
        const reach = node.audibleRadiusM ?? (node.trigger?.exitRadiusM ?? 6) * 3.5;
        const layers = {};
        for (const name of LAYERS) {
            const spec = node.audio?.[name];
            if (!spec?.clipId)
                continue;
            const url = clipUrlFor(spec.clipId);
            if (!url) {
                this.missing.add(spec.clipId);
                continue;
            }
            const el = new Audio(url);
            el.loop = spec.loop !== false;
            el.preload = 'auto';
            el.crossOrigin = 'anonymous';
            let media;
            try {
                media = ctx.createMediaElementSource(el);
            }
            catch {
                continue;
            }
            const gain = ctx.createGain();
            gain.gain.value = 0;
            media.connect(gain);
            // One Resonance source per layer, all at the same point, so each layer gets its own
            // properly spatialised path rather than being summed before spatialisation.
            const source = scene.createSource();
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
        if (!Object.keys(layers).length)
            return null;
        return { node, layers, reach, playing: false };
    }
    /** Called every frame of the simulation. */
    update(position, forward) {
        if (!this.ctx || !this.enabled || !this.scene)
            return;
        const t = this.ctx.currentTime;
        this.scene.setListenerPosition(position.x, position.y, position.z);
        this.scene.setListenerOrientation(forward.x, forward.y, forward.z, 0, 1, 0);
        for (const [, src] of this.sources) {
            const p = src.node.position;
            const distance = Math.hypot(position.x - p.x, position.y - p.y, position.z - p.z);
            if (distance > src.reach * 1.35) {
                this.#silence(src);
                continue;
            }
            this.#ensurePlaying(src);
            // Intimate inside roughly a third of the reach, far beyond two thirds, mid across the
            // middle. What you hear approaching is the recording changing, not the fader moving.
            const n = Math.min(1, distance / Math.max(src.reach, 0.001));
            const weight = {
                intimate: clamp01(1 - n / 0.35),
                mid: clamp01(1 - Math.abs(n - 0.5) / 0.35),
                far: clamp01((n - 0.45) / 0.4),
            };
            for (const name of LAYERS) {
                const layer = src.layers[name];
                if (!layer)
                    continue;
                const linear = dbToLinear(layer.db) * weight[name];
                layer.gain.gain.setTargetAtTime(linear, t, 0.08);
            }
        }
    }
    /** A discrete confirmation — the moment a beat completes. */
    fireCompletion(beat) {
        if (!this.ctx || !this.enabled)
            return;
        const spec = beat?.audio?.completion;
        if (!spec?.clipId)
            return;
        // `clipUrlFor` is unset only before the first load(), when nothing can have completed.
        const url = this.clipUrlFor?.(spec.clipId);
        if (!url) {
            this.missing.add(spec.clipId);
            return;
        }
        // Deliberately head-relative rather than spatialised: a confirmation is about *you*,
        // not about a place in the world.
        const el = new Audio(url);
        el.volume = clamp01(dbToLinear(spec.gainDb ?? -3));
        el.play().catch(() => { });
    }
    #ensurePlaying(src) {
        if (src.playing)
            return;
        src.playing = true;
        for (const layer of voicesOf(src))
            layer.el.play().catch(() => { });
    }
    #silence(src) {
        if (!src.playing)
            return;
        src.playing = false;
        const t = this.ctx?.currentTime ?? 0;
        for (const layer of voicesOf(src)) {
            layer.gain.gain.setTargetAtTime(0, t, 0.12);
            setTimeout(() => { try {
                layer.el.pause();
            }
            catch { } }, 400);
        }
    }
    #teardown(src) {
        for (const layer of voicesOf(src)) {
            try {
                layer.el.pause();
                layer.el.removeAttribute('src');
                layer.el.load();
            }
            catch { }
            try {
                layer.gain.disconnect();
            }
            catch { }
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
/** The layers a source actually has. A partial record loses that on the way through. */
function voicesOf(src) {
    const voices = [];
    for (const name of LAYERS) {
        const voice = src.layers[name];
        if (voice)
            voices.push(voice);
    }
    return voices;
}
function clamp01(v) { return Math.max(0, Math.min(1, v)); }
function dbToLinear(db) { return 10 ** (db / 20); }
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
export function audibleField(node) {
    const reach = node.audibleRadiusM ?? (node.trigger?.exitRadiusM ?? 6) * 3.5;
    const maxD = Math.max(12, reach * 1.6);
    const MIN = 1; // matches source.setMinDistance(1)
    const amplitudeAt = (d) => {
        const n = Math.min(1, d / Math.max(reach, 1e-3));
        const weight = {
            intimate: clamp01(1 - n / 0.35),
            mid: clamp01(1 - Math.abs(n - 0.5) / 0.35),
            far: clamp01((n - 0.45) / 0.4),
        };
        let a = 0;
        for (const name of LAYERS) {
            const spec = node.audio?.[name];
            if (!spec?.clipId)
                continue;
            a += dbToLinear(spec.gainDb ?? -8) * weight[name];
        }
        // Resonance's own 'logarithmic' law, copied from its attenuation.js rather than assumed:
        // the curve is 1/(d+1) offset by minDistance and renormalised so it reaches 0 at max, NOT
        // a logarithm despite the name. Guessing a log here gave a half-life that was wrong.
        let rolloff = 1;
        if (d > maxD) {
            rolloff = 0;
        }
        else if (d > MIN) {
            const range = maxD - MIN;
            const att = 1 / (d - MIN + 1);
            const attMax = 1 / (range + 1);
            rolloff = (att - attMax) / (1 - attMax);
        }
        return a * Math.max(0, rolloff);
    };
    let peak = 0, peakAt = 0;
    for (let d = 0; d <= maxD; d += 0.05) {
        const a = amplitudeAt(d);
        if (a > peak) {
            peak = a;
            peakAt = d;
        }
    }
    if (peak <= 0)
        return null;
    let half = maxD;
    for (let d = peakAt; d <= maxD; d += 0.05) {
        if (amplitudeAt(d) <= peak * 0.5) {
            half = d;
            break;
        }
    }
    return { reach, maxD, peak, peakAt, half: +half.toFixed(2), amplitudeAt };
}
//# sourceMappingURL=audition.js.map