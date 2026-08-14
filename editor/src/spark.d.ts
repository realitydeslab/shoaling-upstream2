/**
 * spark.js, as far as the stage uses it.
 *
 * Spark ships its own declarations, but they are not reachable from here: the package is a
 * dependency of `service/`, and the editor loads it in the browser through the import map in
 * index.html (`/vendor/@sparkjsdev/spark/dist/spark.module.js`). There is no bundler and no
 * root install to resolve, and adding one would change how a clone runs.
 *
 * So this declares exactly the surface `scene.ts` touches — written out rather than reached
 * for with `any`, the same choice `audition.ts` made for Resonance Audio, so that a spark
 * release that changes one of these turns up here as a mismatch rather than as a scan that
 * silently draws nothing. Checked against @sparkjsdev/spark 2.1.0's own
 * `dist/types/{SparkRenderer,SplatMesh,SplatEdit}.d.ts`.
 */

declare module '@sparkjsdev/spark' {
  import type * as THREE from 'three';

  export interface SparkRendererOptions {
    renderer: THREE.WebGLRenderer;
    /**
     * Build a level-of-detail tree so the full capture stays interactive. Splats near the
     * camera keep every detail the scan captured; distant ones collapse.
     */
    enableLod?: boolean;
    /** Stops the renderer spending capacity on splats smaller than a couple of pixels. */
    lodRenderScale?: number;
    lodSplatScale?: number;
    /** Raycast against this many splats rather than all of them, which is what makes clicking instant. */
    lodRaycast?: number;
    autoUpdate?: boolean;
    preUpdate?: boolean;
  }

  export class SparkRenderer extends THREE.Mesh {
    constructor(options: SparkRendererOptions);
    autoUpdate: boolean;
    preUpdate: boolean;
    enableLod: boolean;
    lodSplatScale: number;
    lodRenderScale: number;
    lodRaycast?: number;
  }

  export interface SplatMeshOptions {
    url?: string;
    /** `true` builds an LOD tree in a worker; a `.rad` already carries one built offline. */
    lod?: boolean | 'quality';
    /** Stream the tree in chunks instead of decoding the whole capture first. */
    paged?: boolean;
    /** Required before a SplatEdit can touch this mesh. */
    editable?: boolean;
    raycastable?: boolean;
    onLoad?: (mesh: SplatMesh) => Promise<void> | void;
    onProgress?: (event: ProgressEvent) => void;
  }

  export class SplatMesh extends THREE.Object3D {
    constructor(options?: SplatMeshOptions);
    readonly isInitialized: boolean;
    opacity: number;
    editable: boolean;
    edits: SplatEdit[] | null;
    /**
     * Walk every splat. Returns nothing once LOD is enabled — the splats move into the
     * level-of-detail structure — so bounds come from `editorFrame.bounds` instead.
     */
    forEachSplat(
      callback: (
        index: number,
        centre: THREE.Vector3,
        scales: THREE.Vector3,
        quaternion: THREE.Quaternion,
        opacity: number,
        colour: THREE.Color,
      ) => void,
    ): void;
    dispose(): void;
  }

  export enum SplatEditSdfType {
    ALL = 'all',
    PLANE = 'plane',
    SPHERE = 'sphere',
    BOX = 'box',
    ELLIPSOID = 'ellipsoid',
    CYLINDER = 'cylinder',
    CAPSULE = 'capsule',
    INFINITE_CONE = 'infinite_cone',
  }

  export enum SplatEditRgbaBlendMode {
    MULTIPLY = 'multiply',
    SET_RGB = 'set_rgb',
    ADD_RGBA = 'add_rgba',
  }

  export interface SplatEditSdfOptions {
    type?: SplatEditSdfType;
    /** With the box marking what to KEEP, zero opacity then applies to everything outside it. */
    invert?: boolean;
    opacity?: number;
    color?: THREE.Color;
    displace?: THREE.Vector3;
    radius?: number;
  }

  /** An Object3D: its `scale` is the box HALF-extent, and its transform comes from the graph. */
  export class SplatEditSdf extends THREE.Object3D {
    constructor(options?: SplatEditSdfOptions);
    type: SplatEditSdfType;
    invert: boolean;
    opacity: number;
    radius: number;
  }

  export interface SplatEditOptions {
    name?: string;
    rgbaBlendMode?: SplatEditRgbaBlendMode;
    sdfSmooth?: number;
    softEdge?: number;
    invert?: boolean;
    sdfs?: SplatEditSdf[];
  }

  export class SplatEdit extends THREE.Object3D {
    constructor(options?: SplatEditOptions);
    rgbaBlendMode: SplatEditRgbaBlendMode;
    sdfSmooth: number;
    softEdge: number;
    invert: boolean;
    /**
     * Listing an SDF here is not enough on its own: spark collects them with
     * `traverseVisible`, so each one must also be a scene-graph CHILD of its edit
     * (`edit.add(sdf)`) or nothing updates its matrixWorld and it sits at the origin at unit
     * size doing nothing.
     */
    sdfs: SplatEditSdf[] | null;
    addSdf(sdf: SplatEditSdf): void;
    removeSdf(sdf: SplatEditSdf): void;
  }
}
