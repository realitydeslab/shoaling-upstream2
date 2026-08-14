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
import type { AudioCatalogue, BeatAudio, JourneyDocument, Layer, Trigger, Vec3 } from './types.js';
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
interface ResonanceRoomDimensions {
    width: number;
    height: number;
    depth: number;
}
/** One named material per surface, from Resonance's own material table. */
interface ResonanceRoomMaterials {
    left: string;
    right: string;
    front: string;
    back: string;
    up: string;
    down: string;
}
interface ResonanceScene {
    output: AudioNode;
    setRoomProperties(dimensions: ResonanceRoomDimensions, materials: ResonanceRoomMaterials): void;
    setListenerPosition(x: number, y: number, z: number): void;
    setListenerOrientation(fx: number, fy: number, fz: number, ux: number, uy: number, uz: number): void;
    createSource(): ResonanceSource;
}
interface ResonanceAudioConstructor {
    new (context: AudioContext, options?: {
        ambisonicOrder?: number;
    }): ResonanceScene;
}
declare global {
    interface Window {
        /** Set by the script tag loaded from RESONANCE_URL; absent until then. */
        ResonanceAudio?: ResonanceAudioConstructor;
        /** Safari's prefix. Same constructor. */
        webkitAudioContext?: typeof AudioContext;
    }
}
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
type SourceLayers = {
    [K in Layer]?: LayerVoice;
};
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
/** Under this a voice is not something a listener could name, so it is not worth a decoder. */
export declare const AUDIBLE_FLOOR: number;
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
export declare function reachFor(node: AudibleNode, neighbourM?: number): number;
/** Where a source stops being rendered at all. Also its Resonance `maxDistance`. */
export declare function cullDistance(reach: number): number;
/**
 * Horizontal distance to the nearest other node, or Infinity when there is no other.
 *
 * Horizontal for the same reason the blend is (see `mixAt`): the vertical separation between
 * these points is where the creek bed is, not how far apart they are along the walk.
 */
export declare function nearestNeighbourM(node: AudibleNode, among: readonly AudibleNode[]): number;
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
export declare function mixAt(listener: Vec3, nodes: readonly MixNode[]): Mix;
export declare class Audition {
    #private;
    ctx: AudioContext | null;
    scene: ResonanceScene | null;
    master: GainNode | undefined;
    readonly sources: Map<string, AuditionSource>;
    enabled: boolean;
    muted: boolean;
    readonly onState: (() => void) | undefined;
    readonly missing: Set<string>;
    catalogue: AudioCatalogue | null;
    journey: JourneyDocument | null | undefined;
    clipUrlFor: ClipUrlResolver | undefined;
    /** The mix as of the last update: what is audible, how loud, and what it is about. */
    lastMix: Mix | null;
    constructor({ onState }?: AuditionOptions);
    /** Must be called from a user gesture — browsers refuse to start audio otherwise. */
    enable(): Promise<void>;
    suspend(): void;
    setMuted(muted: boolean): void;
    setCatalogue(catalogue: AudioCatalogue | null): void;
    load(journey: JourneyDocument | null | undefined, clipUrlFor: ClipUrlResolver): Promise<void>;
    /** Called every frame of the simulation. */
    update(position: Vec3, forward: Vec3): void;
    /** A discrete confirmation — the moment a beat completes. */
    fireCompletion(beat: AudibleNode | null | undefined): void;
    /** What the UI needs to tell the truth about what is and is not loaded. */
    status(): AuditionStatus;
}
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
export declare function audibleField(node: AudibleNode, reach?: number): AudibleField | null;
export {};
