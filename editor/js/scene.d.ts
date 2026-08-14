/**
 * The 3D stage: the scan, the creek axis, the beat gizmos, and three cameras.
 *
 * God view orbits the whole reach for placement. User view stands at eye height and walks,
 * because a beat that reads fine from above can be completely hidden from a person standing
 * on the bank — and the visitor is at eye height, not overhead. The phone view (phoneview.ts)
 * is a third camera drawn into a scissored corner of the same canvas, so both readings are on
 * screen at once rather than one camera mode away from each other.
 */
import * as THREE from 'three';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';
import { TransformControls } from 'three/addons/controls/TransformControls.js';
import { ViewHelper } from 'three/addons/helpers/ViewHelper.js';
import { SplatMesh, SparkRenderer, SplatEdit, SplatEditSdf } from '@sparkjsdev/spark';
import { LineMaterial } from 'three/addons/lines/LineMaterial.js';
import type { Beat, AmbientSource, JourneyDocument, LegacyTrimBox, ScanBounds, TrimBox, Vec3 } from './types.js';
import type { PhoneView } from './phoneview.js';
export declare const colourFor: (beat: Pick<Beat, 'interaction'> | null | undefined) => number;
/** Which camera the stage is driving. */
export type StageMode = 'god' | 'user';
/** The three.js transform gizmo's modes, as TransformControls names them. */
export type GizmoMode = 'translate' | 'rotate' | 'scale';
/** The scaffolding layers the toolbar switches. */
export type StageLayer = 'beats' | 'path' | 'trim';
/**
 * `live` marks a callback fired continuously through a drag rather than at the end of one.
 * The end of a drag is the moment worth persisting and worth recording for undo.
 */
export interface StageChangeOptions {
    live?: boolean;
}
/** A beat's identity and where its gizmo has been dragged to. */
export interface BeatMove {
    id: string;
    position: Vec3;
}
export interface StageOptions {
    onTrimChanged?: (box: TrimBox, opts?: StageChangeOptions) => void;
    onPathChanged?: (points: Vec3[], opts?: StageChangeOptions) => void;
    onPathSelect?: (index: number) => void;
    onBeatMoved?: (moved: BeatMove | null, opts?: StageChangeOptions) => void;
    onSelectBeat?: (id: string) => void;
}
/** One axis of the percentile measurement, when the fallback has had to run. */
export interface MeasuredAxis {
    lo: number;
    hi: number;
    mid: number;
}
export interface SplatMeasurement {
    count: number;
    span: Vec3;
    centre: THREE.Vector3;
    /** True when the numbers came from the journey's stamped bounds rather than a measurement. */
    stored?: boolean;
    x?: MeasuredAxis;
    y?: MeasuredAxis;
    z?: MeasuredAxis;
}
/** What the scan turned out to be, once loaded. */
export interface SplatStats extends SplatMeasurement {
    loadMs: number;
}
export interface LoadSplatOptions {
    paged?: boolean;
}
export interface SampleGroundOptions {
    from?: number;
    to?: number;
}
/** One audible-half-life ring, mid-flight. */
interface Pulse {
    mesh: THREE.Mesh<THREE.RingGeometry, THREE.MeshBasicMaterial>;
    radius: number;
    phase: number;
}
/**
 * One axis summarised at the 1st, 50th and 99th percentiles. Sorts in place.
 *
 * Percentiles rather than min and max, because every Scaniverse scan carries floaters hundreds
 * of metres out and a box drawn around those frames the camera on nothing. `Box3.setFromObject`
 * is no help either — it returns an empty box on a splat mesh. Lifted out of #measure() by the
 * TypeScript port so the rule can be checked without a GPU; the behaviour is unchanged.
 */
export declare function percentileAxis(values: number[]): MeasuredAxis;
/**
 * The trim box in the shape the stage uses, whatever shape it arrived in.
 *
 * Older drafts stored an axis-aligned {min, max}; those are converted here so a journey
 * authored before the box became oriented keeps working. A trim box is positioned by hand and
 * cannot be reproduced from a seed, so losing one is losing work — which is why this is lifted
 * out of setTrim() and tested rather than left inline. The behaviour is unchanged.
 */
export declare function normaliseTrim(trim: TrimBox | LegacyTrimBox | null | undefined): TrimBox | null;
export declare class Stage {
    #private;
    readonly container: HTMLElement;
    readonly onSelectBeat: ((id: string) => void) | undefined;
    readonly onTrimChanged: ((box: TrimBox, opts?: StageChangeOptions) => void) | undefined;
    readonly onPathChanged: ((points: Vec3[], opts?: StageChangeOptions) => void) | undefined;
    readonly onPathSelect: ((index: number) => void) | undefined;
    readonly onBeatMoved: ((moved: BeatMove | null, opts?: StageChangeOptions) => void) | undefined;
    trimActive: boolean;
    mode: StageMode;
    editingPath: boolean;
    selectedPathIndex: number;
    splat: SplatMesh | null;
    splatVisible: boolean;
    beats: Beat[];
    ambient: AmbientSource[];
    centreline: Vec3[];
    selectedId: string | null;
    walkerS: number;
    readonly renderer: THREE.WebGLRenderer;
    readonly scene: THREE.Scene;
    readonly spark: SparkRenderer;
    readonly god: THREE.PerspectiveCamera;
    readonly user: THREE.PerspectiveCamera;
    readonly controls: OrbitControls;
    readonly grid: THREE.GridHelper;
    readonly gizmos: THREE.Group;
    readonly axisGroup: THREE.Group;
    readonly pathHandles: THREE.Group;
    readonly walker: THREE.Group;
    readonly clock: THREE.Clock;
    readonly raycaster: THREE.Raycaster;
    readonly pointer: THREE.Vector2;
    readonly groundPlane: THREE.Plane;
    readonly viewHelper: ViewHelper;
    avatar?: THREE.Group;
    cameraGizmo?: THREE.Group;
    storedBounds: ScanBounds | null;
    splatStats?: SplatStats;
    trim: TrimBox | null;
    trimEdit?: SplatEdit;
    trimSdf?: SplatEditSdf;
    trimMesh?: SplatMesh;
    trimBox?: THREE.LineSegments;
    trimLayerVisible?: boolean;
    beatsVisible?: boolean;
    gizmo?: TransformControls;
    gizmoHelper?: THREE.Object3D;
    pulses: Pulse[];
    fatMaterials: LineMaterial[];
    frameCount: number;
    fpsStamp: number;
    fps: number;
    onFps?: (fps: number) => void;
    walkerStampedAt: number;
    walkerSpeed: number;
    keys: Set<string>;
    walkHandler?: (delta: number) => void;
    phone?: PhoneView;
    constructor(container: HTMLElement, { onTrimChanged, onPathChanged, onPathSelect, onBeatMoved, onSelectBeat }?: StageOptions);
    get camera(): THREE.PerspectiveCamera;
    /** Precomputed extent from the journey, so we never walk 5M splats in the browser. */
    setBounds(bounds: ScanBounds | null): void;
    loadSplat(url: string, onProgress?: (phase: string) => void, { paged }?: LoadSplatOptions): Promise<SplatStats | null>;
    setGizmoMode(mode: GizmoMode): void;
    setGizmoVisible(visible: boolean): void;
    /** Current box as journey data. Scale is the box half-extent. */
    readTrim(): TrimBox;
    /**
     * @param trim oriented box {enabled, position, rotation[4], halfExtent}. Legacy axis-aligned
     *   {min,max} boxes are converted, so older journeys keep working.
     */
    setTrim(input: TrimBox | LegacyTrimBox | null | undefined): void;
    /**
     * The creek axis as draggable control points.
     *
     * Clicking new points onto the scan is fine for roughing a path in, but useless for the
     * thing you actually spend time on — nudging one bend until it follows the water. So the
     * points get the same gizmo as the trim box: click a handle, drag it, everything downstream
     * (beat `s` values, the scrubber, the walker) recomputes.
     */
    setPathEditing(on: boolean): void;
    selectPathPoint(index: number): void;
    /**
     * Sample the ground height at an (x, z) by dropping a ray onto the scan.
     *
     * The walking path has to sit ON the ground, because the user camera is placed at
     * path height + eye height. A path authored at a constant Y either buries the walker in
     * the bank or floats them above it, and on a creek with a 2 m fall that error is the whole
     * elevation change of the piece.
     *
     * Returns null when nothing is hit, so the caller can decide on a fallback rather than
     * silently accepting a wrong height.
     */
    sampleGround(x: number, z: number, { from, to }?: SampleGroundOptions): number | null;
    /** Read the handles back into a centreline. */
    readPath(): Vec3[];
    /**
     * Layer visibility.
     *
     * Each of these is something the author turns on to work on and off to get out of the way.
     * Beats stay visible by default because they are the content; the path and the trim box are
     * scaffolding and only earn screen space while being edited.
     */
    setLayerVisible(layer: StageLayer, visible: boolean): void;
    setSplatVisible(visible: boolean): void;
    setJourney(journey: JourneyDocument | null | undefined): void;
    setSelected(id: string | null): void;
    /** Current position of the beat the gizmo is holding. */
    readBeatPosition(): BeatMove | null;
    setWalker(s: number): void;
    /** Shown while the path is playing: the frustum is noise when nobody is walking. */
    setCameraGizmoVisible(visible: boolean): void;
    setMode(mode: StageMode): void;
    frame(): void;
    resize(): void;
    onWalk(fn: (delta: number) => void): void;
    /** The phone panel draws itself into this stage's canvas, so the stage has to know about it. */
    attachPhone(phone: PhoneView): void;
}
export {};
