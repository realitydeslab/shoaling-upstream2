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
}
type SourceLayers = {
    [K in Layer]?: LayerVoice;
};
interface AuditionSource {
    node: AudibleNode;
    layers: SourceLayers;
    reach: number;
    playing: boolean;
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
export declare function audibleField(node: AudibleNode): AudibleField | null;
export {};
