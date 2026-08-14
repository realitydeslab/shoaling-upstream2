/**
 * The phone screen: what the visitor is actually holding.
 *
 * A second camera rides the walking path at eye height with 6DOF look-around, rendered into
 * its own viewport in the same WebGL context as the main stage. It exists because the God
 * view answers "is the layout right" and cannot answer the only question that matters — what
 * does a person see when they get there. A beat that reads perfectly from above can be behind
 * a tree at eye height.
 *
 * Everything drawn here is a sketch of the on-device visuals, not the visuals themselves:
 * the phone runs Apple PHASE and NSDK, and the piece is sound-first by design. These exist so
 * an author can see roughly where an egg lands or where the heron stands relative to the
 * scan, and judge whether the *placement* works.
 *
 * One renderer, two viewports. Spark's SparkRenderer binds to a single WebGLRenderer, so a
 * second renderer would need a second SparkRenderer over the same scene — which fights over
 * the splat accumulator. Scissored viewports avoid the whole problem.
 *
 * The bezel is a DOM element rather than WebGL, and it carries the pointer target. That is
 * not decoration: OrbitControls and the stage's own selection raycast both bind pointerdown
 * on the canvas in the Stage constructor, so a listener added later on the same element
 * cannot get ahead of them — at the target, listeners run in registration order and
 * stopPropagation does not reach siblings. An element sitting over the canvas never lets the
 * event reach them at all, which is what keeps a look-around from orbiting the God camera or
 * selecting a beat behind the phone.
 */
import * as THREE from 'three';
import type { Vec3 } from './types.js';
import type { Stage } from './scene.js';
/** Where the screen sits on the canvas, in CSS pixels. */
export interface PanelRect {
    x: number;
    y: number;
    w: number;
    h: number;
}
/** A frame prepared by render(), waiting for present() to blit it. */
interface PendingFrame {
    rect: PanelRect;
    W: number;
    H: number;
}
type ShoalPoints = THREE.Points<THREE.BufferGeometry, THREE.PointsMaterial>;
export declare class PhoneView {
    #private;
    readonly stage: Stage;
    enabled: boolean;
    s: number;
    yaw: number;
    pitch: number;
    dragging: boolean;
    rect: PanelRect | null;
    readonly camera: THREE.PerspectiveCamera;
    readonly effects: THREE.Group;
    readonly shoal: ShoalPoints;
    readonly clock: THREE.Clock;
    eggs: THREE.Group[];
    flashes: THREE.Group[];
    heron: THREE.Group | null;
    el: HTMLDivElement;
    screen: HTMLDivElement;
    caption: HTMLDivElement;
    last: {
        x: number;
        y: number;
    };
    target?: THREE.WebGLRenderTarget;
    quad?: THREE.Mesh<THREE.PlaneGeometry, THREE.MeshBasicMaterial>;
    quadScene?: THREE.Scene;
    quadCamera?: THREE.OrthographicCamera;
    lastDraw: number;
    lastLabel: string | null;
    pending: PendingFrame | null;
    constructor(stage: Stage);
    setShoalCount(n: number): void;
    get shoalCount(): number;
    /** Eggs settling into gravel — the crouch beats. */
    spawnEggs(at: Vec3): THREE.Group;
    /** The heron: a still shape at the pool that leaves when it has been fed. */
    showHeron(at: Vec3): THREE.Group;
    /** The heron takes: fish stripped from the shoal, and the bird lifts away. */
    heronFeeds(count: number): void;
    /**
     * Taking an insect — the catch beats.
     *
     * One mote lifting off the surface and a ring where it broke it, over in under a second.
     * The gesture it stands for is a lunge and a stop; anything that lingers would read as a
     * place to wait at, which is the opposite of what a catch is.
     */
    takeInsect(at: Vec3): THREE.Group;
    clearEffects(): void;
    /** Back to the start of the journey: no eggs cut, no heron fed, the shoal whole. */
    reset(): void;
    setEnabled(on: boolean): void;
    /** Take the bezel off the stage without forgetting where the walker had got to. */
    hide(): void;
    setS(s: number): void;
    /**
     * Called from the stage's animation loop, after the main scene and the ViewHelper.
     *
     * @param pointAtS  (s) => {x, y, z} on the walking path; the caller binds the centreline.
     * @param totalLength  reach length in metres, so the look-ahead never runs off the end.
     */
    render(pointAtS: (s: number) => Vec3, totalLength: number): void;
    /** Blit the frame prepared by render() onto the canvas. Called after the main view has drawn. */
    present(): void;
    dispose(): void;
}
export {};
