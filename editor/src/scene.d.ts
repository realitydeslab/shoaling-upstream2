/**
 * TEMPORARY — delete when editor/src/scene.ts lands.
 *
 * `app.ts` imports './scene.js', and tsc only sees editor/src/**\/*.ts, so that import cannot
 * resolve while the stage is still the hand-written editor/js/scene.js. This declares exactly
 * the surface app.ts uses. tsc emits nothing for a .d.ts, so editor/js/scene.js keeps running
 * untouched — which is the point: the performance work in that file is still in flight, and a
 * premature port would overwrite it.
 *
 * Two files cannot both claim './scene.js'. The real scene.ts replaces this one; it exports
 * these same names, so app.ts does not change when it does.
 */

import type * as THREE from 'three';
import type { Beat, JourneyDocument, ScanBounds, TrimBox, Vec3 } from './types.js';
import type { PhoneView } from './phoneview.js';

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

/** What the scan turned out to be, once loaded. */
export interface SplatStats {
  count: number;
  span: Vec3;
  centre: THREE.Vector3;
  loadMs: number;
  /** True when the numbers came from the journey's stamped bounds rather than a measurement. */
  stored?: boolean;
}

export interface LoadSplatOptions {
  paged?: boolean;
}

export interface SampleGroundOptions {
  from?: number;
  to?: number;
}

export declare class Stage {
  constructor(container: HTMLElement, options?: StageOptions);

  mode: StageMode;
  selectedPathIndex: number;
  splatStats?: SplatStats;
  trimLayerVisible?: boolean;
  beats: Beat[];
  centreline: Vec3[];
  onFps?: (fps: number) => void;

  setBounds(bounds: ScanBounds | null): void;
  loadSplat(
    url: string,
    onProgress?: (phase: string) => void,
    options?: LoadSplatOptions,
  ): Promise<SplatStats | null>;

  setJourney(journey: JourneyDocument | null | undefined): void;
  setSelected(id: string | null): void;
  setWalker(s: number): void;
  frame(): void;
  setMode(mode: StageMode): void;

  setTrim(trim: TrimBox | null | undefined): void;
  readTrim(): TrimBox;
  setGizmoMode(mode: GizmoMode): void;
  setGizmoVisible(visible: boolean): void;

  setPathEditing(on: boolean): void;
  selectPathPoint(index: number): void;
  readPath(): Vec3[];
  sampleGround(x: number, z: number, options?: SampleGroundOptions): number | null;

  setLayerVisible(layer: StageLayer, visible: boolean): void;
  setSplatVisible(visible: boolean): void;
  setCameraGizmoVisible(visible: boolean): void;

  onWalk(fn: (delta: number) => void): void;
  attachPhone(phone: PhoneView): void;
  resize(): void;
}
