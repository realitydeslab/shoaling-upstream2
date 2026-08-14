/**
 * The armed-zones scrubber.
 *
 * Drag along the reach and see, at every position, which beats are live, which one wins,
 * and where the gates overlap. None of the seven locative authoring tools surveyed has this,
 * and with six zones on a 34 m creek an author simply cannot reason about trigger behaviour
 * from a map view — the exit bands overlap everywhere and the map shows only circles.
 *
 * It draws the same evaluation the runtime performs, so what you see here is what the phone
 * will do.
 */
import { type Evaluation } from './geom.js';
import type { Beat, JourneyDocument } from './types.js';
export interface ScrubberOptions {
    onScrub?: (s: number) => void;
}
export declare class Scrubber {
    #private;
    readonly canvas: HTMLCanvasElement;
    readonly ctx: CanvasRenderingContext2D;
    readonly onScrub: ((s: number) => void) | undefined;
    beats: Beat[];
    length: number;
    s: number;
    selectedId: string | null;
    dragging: boolean;
    constructor(canvas: HTMLCanvasElement, { onScrub }?: ScrubberOptions);
    setJourney(journey: JourneyDocument | null | undefined): void;
    setSelected(id: string | null): void;
    setS(s: number): void;
    /** What is armed right now — also used by the app to drive the readout and rail. */
    evaluate(): Evaluation;
    draw(): void;
}
