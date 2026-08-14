/**
 * The journey document, typed.
 *
 * This is the keystone of the editor: the same shape is validated by
 * `service/src/journey-schema.mjs` and deserialised by
 * `app/Assets/ShoalingUpstream/Runtime/Journey/JourneyModel.cs`. Three languages describing one
 * file, and the file is the artwork. When this disagrees with either of the others, the failure
 * is silent and turns up as a beat that never fires in a creek.
 *
 * So: derive from the validator, not from memory, and change all three together.
 */
/** Metres, in the journey's anchor frame. */
export interface Vec3 {
    x: number;
    y: number;
    z: number;
}
/** `[x, y, z, w]` — the order three.js and the schema both use. */
export type Quat = readonly [number, number, number, number];
/**
 * What the visitor does at a beat.
 *
 * The vocabulary is closed and is served from `/api/interactions`; the editor renders a picker
 * from it rather than hard-coding a list. `lift` is a sustained 40 cm rise that can be HELD, not
 * a ballistic jump — the audio holds "out of the water" for as long as the visitor stays up.
 */
export type Interaction = 'proximity' | 'crouch' | 'catch' | 'give' | 'lift';
/**
 * The three recordings a source is made of.
 *
 * Distance is carried by content, not by gain: across 5-20 m the whole inverse-square budget is
 * about 12 dB, which against a 47-67 dB(A) park floor reads as "slightly louder" rather than as
 * arrival. Approaching crossfades between three different takes.
 */
export type Layer = 'far' | 'mid' | 'intimate';
export declare const LAYERS: readonly Layer[];
export interface InteractionInfo {
    label: string;
    hint: string;
    needsGesture: boolean;
}
export interface AudioLayer {
    clipId: string;
    gainDb: number;
    loop?: boolean;
}
export type BeatAudio = {
    [K in Layer]?: AudioLayer;
} & {
    /** Fired once when the interaction completes. Never looped. */
    completion?: AudioLayer;
};
/**
 * When a beat arms, fires and lets go.
 *
 * `enterRadiusM` arms it and `exitRadiusM` sustains it — two radii, not one, because a visitor
 * standing near a single boundary makes it thrash. `minimumHoldSeconds` is the real anti-thrash
 * mechanism: once a beat has fired it holds for its full minimum regardless of position.
 */
export interface Trigger {
    enterRadiusM: number;
    exitRadiusM: number;
    dwellSeconds: number;
    minimumHoldSeconds: number;
    requiresPreviousComplete: boolean;
}
export interface Beat {
    id: string;
    title: string;
    prompt: string;
    interaction: Interaction;
    /** Placed freely in space. Editing the path never moves this. */
    position: Vec3;
    /** DERIVED: this beat's projection onto the walking path. Never authored by hand. */
    s: number;
    trigger: Trigger;
    audio?: BeatAudio;
    /** Only meaningful on a `give` beat: how much of the shoal the heron takes. */
    givesFish?: number;
    /** Overrides the reach derived from the trigger geometry. */
    audibleRadiusM?: number;
}
/** Unordered, always-on sound. The story is a sequence; the creek is not. */
export interface AmbientSource {
    id: string;
    title?: string;
    position: Vec3;
    audibleRadiusM: number;
    audio?: BeatAudio;
}
export interface UpstreamAxis {
    kind: string;
    risesToward: string;
}
export interface SiteRef {
    slug: string;
    title: string;
    nianticOrgId: string;
    nianticSiteId: string;
    vpsAssetId?: string;
    anchorPayload?: string;
    /**
     * The walking path, and the camera track — they are the same thing. Stored at the 1.40 m
     * sternum where the phone hangs on its neck mount, so nothing adds an eye height to it.
     */
    centreline: Vec3[];
    upstreamAxis?: UpstreamAxis;
}
/**
 * An oriented box that hides floater splats.
 *
 * Display only — it is not content, and it is applied at runtime as a SplatEdit rather than
 * baked into the LOD. `halfExtent` is a half-extent, matching spark's box SDF.
 */
export interface TrimBox {
    enabled: boolean;
    position: Vec3;
    rotation: Quat;
    halfExtent: Vec3;
}
/** Percentile extents, precomputed offline: every scan carries floaters 400-1200 m out. */
export interface ScanBounds {
    centre?: Vec3;
    span?: Vec3;
    min?: Vec3;
    max?: Vec3;
}
export interface EditorFrame {
    splatFile?: string;
    translation: Vec3;
    rotation: Quat;
    scale: number;
    /**
     * False means the coordinates are provisional. The app runs such a journey in simulation and
     * must REFUSE it on device — provisional coordinates in a real creek put a visitor in the
     * wrong place.
     */
    calibrated: boolean;
    trim?: TrimBox;
    bounds?: ScanBounds;
}
export interface Shoal {
    startingCount: number;
    /** The shoal may be given away entirely; a floor of zero is legal. */
    minimumCount: number;
}
export interface Safety {
    [key: string]: unknown;
}
export interface JourneyDocument {
    schemaVersion: string;
    journeyId: string;
    title: string;
    revision: number;
    site: SiteRef;
    editorFrame: EditorFrame;
    beats: Beat[];
    ambient?: AmbientSource[];
    shoal: Shoal;
    safety?: Safety;
}
export interface ValidationResult {
    ok: boolean;
    errors: string[];
    warnings: string[];
}
export interface SiteSummary {
    slug: string;
    title: string;
}
/** One representation of a scan, best first. `.rad` streams a precomputed LOD tree. */
export interface ScanOption {
    kind: 'rad' | 'full' | 'proxy' | string;
    label: string;
    url: string;
}
export interface AudioClipInfo {
    clipId: string;
    file?: string;
    durationSeconds?: number;
}
export interface AudioCatalogue {
    clips: AudioClipInfo[];
    note?: string;
}
export interface PoseMessage {
    type: 'pose';
    s: number | null;
    position: Vec3 | null;
    headingRad: number | null;
    slug: string | null;
    sentAtMs: number;
}
export interface PresenceMessage {
    type: 'presence';
    devices: unknown[];
    operators: unknown[];
}
/** Scheduled, never fired: every device acts on `fireAtMs` so two phones stay together. */
export interface CommandMessage {
    type: 'command';
    id: string;
    action: string;
    beatId: string | null;
    value: unknown;
    issuedAtMs: number;
    fireAtMs: number;
    expiresAtMs: number;
    sessionId: string;
}
/** Which scaffolding is on screen. Persisted in localStorage. */
export interface Layers {
    beats: boolean;
    path: boolean;
    trim: boolean;
    scan: boolean;
    phone: boolean;
}
/** A beat's zone at a given position along the reach, for the armed-zones scrubber. */
export interface ArmedZone {
    beat: Beat;
    distance: number;
    inEnter: boolean;
    inExit: boolean;
}
export interface ProjectionResult {
    s: number;
    distance: number;
    lateral?: number;
}
