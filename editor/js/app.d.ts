/**
 * Editor wiring: load a site, render it, edit beats, publish.
 */
import { Stage } from './scene.js';
import { Scrubber } from './scrubber.js';
import { Audition } from './audition.js';
import type { AudioCatalogue, InteractionInfo, JourneyDocument, Layers, Quat, ScanOption, SiteSummary, TrimBox, Vec3 } from './types.js';
/**
 * The interaction vocabulary, as served by /api/interactions.
 *
 * Keyed by string rather than by `Interaction` because it arrives off the wire: the editor
 * renders its picker from whatever the service sends, which is how the vocabulary stays in one
 * place rather than being hard-coded here as well.
 */
type InteractionCatalogue = Record<string, InteractionInfo>;
export interface EditorState {
    sites: SiteSummary[];
    slug: string | null;
    journey: JourneyDocument | null;
    selectedId: string | null;
    interactions: InteractionCatalogue;
    dirty: boolean;
    quality: string;
    layers: Layers;
    scans: Record<string, ScanOption[]>;
    audio: AudioCatalogue | null;
    playing: boolean;
    speed: number;
    fired: Set<string>;
    /** Timestamp the walk simulation last advanced from. */
    lastTick?: number;
    /** The beat currently being dragged in the rail, if any. */
    dragId?: string | null;
    /** Set when the tab went to the background mid-walk, so returning can say so. */
    autoPaused?: boolean;
}
export declare const state: EditorState;
/**
 * Undo history for authored geometry.
 *
 * Everything on this stage is positioned by dragging, and a transform gizmo pulled against a
 * ground plane at a glancing angle can throw a point tens of metres in one movement — the
 * garden path picked up a control point at x = -34 that way, which turned a 25 m route into
 * 78 m. Without an undo the only recovery is git, and the draft is written continuously, so by
 * the time the damage is noticed the good version may be several saves back.
 *
 * Snapshots are of the whole authored document rather than of individual operations. It is a
 * few kilobytes, it cannot get out of step with the edit that produced it, and it means an
 * operation added later is covered without anyone remembering to write an inverse for it.
 */
export type DocSnapshot = Pick<JourneyDocument, 'site' | 'beats' | 'editorFrame'>;
export interface HistoryEntry {
    label: string;
    doc: DocSnapshot;
}
export declare const history: {
    past: HistoryEntry[];
    future: HistoryEntry[];
    limit: number;
    baseline: DocSnapshot | null;
};
export declare function snapshotJourney(): DocSnapshot;
/**
 * Record that an edit has just been committed.
 *
 * Called after the change, not before: the baseline holds the previous committed state, so
 * there is no need to hook the start of every drag.
 */
export declare function commitHistory(label: string): void;
/**
 * Resolve a clip id to a packaged file. Layered ids carry a "--far" style suffix; the
 * catalogue lists the base clip, so strip the suffix before looking it up.
 */
export declare function clipUrl(clipId: string): string | null;
/** Default the trim box to the measured extent of the scan. */
export declare function defaultTrim(): TrimBox;
export declare function ensureTrim(): TrimBox;
export declare function quaternionToEulerDegrees([x, y, z, w]: Quat): Vec3;
export declare function uniqueId(prefix: string, taken: string[]): string;
export declare function escapeHtml(s: unknown): string;
export declare function escapeAttr(s: unknown): string;
declare global {
    interface Window {
        /**
         * A console handle for the failure this editor is most likely to hit on a new machine:
         * a scan that does not show up.
         */
        __editor?: {
            stage: Stage;
            state: EditorState;
            readonly scrubber: Scrubber;
            readonly audition: Audition;
            setWalker: (v: number) => void;
        };
    }
}
export {};
