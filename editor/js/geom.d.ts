/**
 * Centreline geometry, shared between the editor and the runtime reasoning.
 *
 * The reach is treated as a line rather than a field. Every position is reduced to `s`,
 * distance along the creek, plus a lateral offset that never triggers anything. A gate
 * perpendicular to the creek axis throws away all cross-stream error, which matters when
 * beats are 6-10 m apart and pose noise is a metre or more.
 *
 * This maths exists twice: here, and in `service/src/journey-schema.mjs`, which recomputes every
 * beat's `s` when the walking path moves. `test/geometry.test.ts` runs every case against BOTH,
 * because if they drift the scrubber shows a beat arming where the device will never fire it,
 * and nothing reveals that until someone is standing in a creek.
 */
import type { Beat, Vec3 } from './types.js';
export interface Projection {
    /** Distance along the centreline from its start, in metres. */
    s: number;
    /** Perpendicular distance from the line. Never triggers anything. */
    lateral: number;
    closest: Vec3 | null;
    segment: number;
    /** Position within that segment, 0 to 1. */
    t: number;
}
export declare function projectToCentreline(point: Vec3, centreline: readonly Vec3[]): Projection;
/** The inverse: a point on the centreline at distance `s` from its start. */
export declare function pointAtS(s: number, centreline: readonly Vec3[]): Vec3;
export declare function centrelineLength(centreline: readonly Vec3[]): number;
/** Unit tangent at distance s — the direction "upstream" points at that place. */
export declare function tangentAtS(s: number, centreline: readonly Vec3[]): Vec3;
export interface ArmedBeat {
    beat: Beat;
    index: number;
    distance: number;
    inEnter: boolean;
    inExit: boolean;
    /** Held back because an earlier beat has not been completed yet. */
    blocked: boolean;
    /** Proximity within the band, normalised — what would drive the far/mid/intimate blend. */
    proximity: number;
}
export interface Evaluation {
    armed: ArmedBeat[];
    winner: ArmedBeat | null;
}
export interface EvaluateOptions {
    highWaterMark?: number;
    respectOrder?: boolean;
}
/**
 * Which beats are armed at position s, and which one wins.
 *
 * Winner-take-all over a single state variable, not six independent zone monitors. Six
 * monitors interleave and double-fire near boundaries; one state machine cannot. This
 * mirrors what the runtime does, so what the scrubber shows is what the phone will do.
 */
export declare function evaluateAt(s: number, beats: readonly Beat[], { highWaterMark, respectOrder }?: EvaluateOptions): Evaluation;
