/**
 * The editor's link to whatever is running the piece.
 *
 * The editor is an operator on the control bus. It does two things there:
 *
 *   - streams the simulated walker's pose, so the Unity editor can follow the browser's walk
 *     and play the same beats through PHASE without anyone standing in a creek;
 *   - shows whether a phone is actually connected, which is the first thing you want to know
 *     before wondering why nothing is happening.
 *
 * Pose is streamed on its own message type rather than as a command. A command is discrete,
 * scheduled against a fireAtMs and acknowledged; a pose is continuous state where the latest
 * value wins and history is worthless. Sending twenty commands a second would churn the bounded
 * command log and put a lead time on a signal that wants none.
 *
 * On the creek this channel is silent. VPS2 supplies the pose there, and the app ignores this
 * entirely — the simulation exists so the piece can be built at a desk, not to drive the work.
 */
import type { PoseMessage } from './types.js';
/**
 * What the editor puts on the wire.
 *
 * `sentAtMs` is missing on purpose: the bus stamps its own clock as it fans the pose out
 * (`streamPose` in service/src/control-bus.mjs), so the follower measures its offset against
 * the server rather than against a browser's idea of the time.
 */
export type OutboundPose = Omit<PoseMessage, 'sentAtMs'>;
/** Where the walker is. Everything the caller supplies; `type` is added here. */
export type PoseUpdate = Omit<OutboundPose, 'type'>;
/** How many of each are on the bus right now. */
export interface Presence {
    devices: number;
    operators: number;
}
export interface LinkOptions {
    /** Called with {devices, operators} whenever the roster changes. */
    onPresence?: (presence: Presence) => void;
    /**
     * How to open the socket. A seam added by the TypeScript port so `test/link.test.ts` can hand
     * in a fake one; the editor never passes it and gets the operator URL below.
     */
    openSocket?: () => WebSocket;
}
export declare class Link {
    #private;
    onPresence: ((presence: Presence) => void) | undefined;
    socket: WebSocket | null;
    connected: boolean;
    devices: number;
    lastSentAt: number;
    pending: OutboundPose | null;
    retryMs: number;
    flushTimer: ReturnType<typeof setTimeout> | null;
    constructor({ onPresence, openSocket }?: LinkOptions);
    /**
     * Publish where the simulated walker is.
     *
     * Rate-limited, and the most recent pose during a quiet interval is kept and sent when the
     * interval expires. Dropping it instead would leave the follower parked a step behind
     * wherever the scrub happened to stop, which reads as a bug rather than as a throttle.
     */
    sendPose({ s, position, headingRad, slug }: PoseUpdate): void;
    /** Fire a beat on the phone by hand — the operator's safety net, from the editor. */
    fireBeat(beatId: string): boolean;
}
