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
import {
  SplatMesh, SparkRenderer,
  SplatEdit, SplatEditSdf, SplatEditSdfType, SplatEditRgbaBlendMode,
} from '@sparkjsdev/spark';
import { pointAtS, centrelineLength } from './geom.js';
import { audibleField } from './audition.js';
import { Line2 } from 'three/addons/lines/Line2.js';
import { LineGeometry } from 'three/addons/lines/LineGeometry.js';
import { LineMaterial } from 'three/addons/lines/LineMaterial.js';
import { buildAvatar, buildCameraGizmo, makeLabel, glowSprite } from './figures.js';
import type {
  Beat, AmbientSource, JourneyDocument, LegacyTrimBox, ScanBounds, TrimBox, Vec3,
} from './types.js';
import type { PhoneView } from './phoneview.js';

/**
 * The walking path is stored at chest height, because the phone hangs on a neck mount: the
 * camera rides the visitor's sternum and does not move when they turn their head. So the path
 * IS the camera track and nothing is added to it here. Only the walker gizmo needs a body
 * drawn beneath it.
 */
const CHEST_HEIGHT = 1.40;

const COLOUR = {
  water: 0x6da7ad,
  accent: 0xd4707f,
  moss: 0x93a76b,
  muted: 0x55625a,
  amber: 0xd19a45,
  lift: 0x9d8ec9,
};

/**
 * Beats are coloured by what the visitor does, not by their order.
 *
 * With six beats inside twenty metres the map is crowded, and the question an author asks of it
 * is "where do the crouches fall" or "is the lift the only one of its kind" — a question about
 * kind, which a rainbow gradient along the path cannot answer. Order is already carried by the
 * path itself and by the list.
 */
const INTERACTION_COLOUR: Record<string, number> = {
  proximity: COLOUR.water,
  crouch: COLOUR.moss,
  catch: COLOUR.amber,
  give: COLOUR.accent,
  lift: COLOUR.lift,
};
export const colourFor = (beat: Pick<Beat, 'interaction'> | null | undefined): number =>
  INTERACTION_COLOUR[beat?.interaction ?? ''] ?? COLOUR.moss;

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

interface FatLineOptions {
  colour: number;
  width?: number;
  opacity?: number;
  dashed?: boolean;
  order?: number;
}

/** One audible-half-life ring, mid-flight. */
interface Pulse {
  mesh: THREE.Mesh<THREE.RingGeometry, THREE.MeshBasicMaterial>;
  radius: number;
  phase: number;
}

/**
 * three.js marks its drawable subclasses with `isMesh` / `isLine` / `isSprite` rather than
 * giving them a common base, and on anything else those flags are simply absent. Walking a
 * mixed scene graph means asking for them on an Object3D, which is what this allows.
 */
type MaybeDrawable = THREE.Object3D & Partial<{
  isMesh: boolean;
  isLine: boolean;
  isSprite: boolean;
  geometry: THREE.BufferGeometry;
  material: THREE.Material | THREE.Material[];
}>;

/** Only the concrete materials declare a map; this walks a mixed bag of them. */
type MaterialWithMap = THREE.Material & { map?: THREE.Texture | null };

/**
 * One axis summarised at the 1st, 50th and 99th percentiles. Sorts in place.
 *
 * Percentiles rather than min and max, because every Scaniverse scan carries floaters hundreds
 * of metres out and a box drawn around those frames the camera on nothing. `Box3.setFromObject`
 * is no help either — it returns an empty box on a splat mesh. Lifted out of #measure() by the
 * TypeScript port so the rule can be checked without a GPU; the behaviour is unchanged.
 */
export function percentileAxis(values: number[]): MeasuredAxis {
  const pick = (p: number): number =>
    values[Math.min(values.length - 1, Math.max(0, Math.floor(values.length * p)))]!;
  values.sort((a, b) => a - b);
  return { lo: pick(0.01), hi: pick(0.99), mid: pick(0.5) };
}

/**
 * The trim box in the shape the stage uses, whatever shape it arrived in.
 *
 * Older drafts stored an axis-aligned {min, max}; those are converted here so a journey
 * authored before the box became oriented keeps working. A trim box is positioned by hand and
 * cannot be reproduced from a seed, so losing one is losing work — which is why this is lifted
 * out of setTrim() and tested rather than left inline. The behaviour is unchanged.
 */
export function normaliseTrim(trim: TrimBox | LegacyTrimBox | null | undefined): TrimBox | null {
  if (!trim) return null;
  const legacy = trim as TrimBox & LegacyTrimBox;
  if (!legacy.min || !legacy.max || legacy.halfExtent) return legacy;
  const { min, max } = legacy;
  return {
    enabled: !!legacy.enabled,
    position: {
      x: (min.x + max.x) / 2,
      y: (min.y + max.y) / 2,
      z: (min.z + max.z) / 2,
    },
    rotation: [0, 0, 0, 1],
    // A zero half-extent is a box with no inside, and the SDF would then delete the whole scan.
    halfExtent: {
      x: Math.max(0.05, (max.x - min.x) / 2),
      y: Math.max(0.05, (max.y - min.y) / 2),
      z: Math.max(0.05, (max.z - min.z) / 2),
    },
  };
}

export class Stage {
  readonly container: HTMLElement;
  readonly onSelectBeat: ((id: string) => void) | undefined;
  readonly onTrimChanged: ((box: TrimBox, opts?: StageChangeOptions) => void) | undefined;
  readonly onPathChanged: ((points: Vec3[], opts?: StageChangeOptions) => void) | undefined;
  readonly onPathSelect: ((index: number) => void) | undefined;
  readonly onBeatMoved: ((moved: BeatMove | null, opts?: StageChangeOptions) => void) | undefined;

  trimActive = false;
  mode: StageMode = 'god';
  editingPath = false;
  selectedPathIndex = -1;
  splat: SplatMesh | null = null;
  splatVisible = true;
  beats: Beat[] = [];
  ambient: AmbientSource[] = [];
  centreline: Vec3[] = [];
  selectedId: string | null = null;
  walkerS = 0;

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

  storedBounds: ScanBounds | null = null;
  splatStats?: SplatStats;

  trim: TrimBox | null = null;
  trimEdit?: SplatEdit;
  trimSdf?: SplatEditSdf;
  trimMesh?: SplatMesh;
  trimBox?: THREE.LineSegments;
  trimLayerVisible?: boolean;
  beatsVisible?: boolean;

  gizmo?: TransformControls;
  gizmoHelper?: THREE.Object3D;

  pulses: Pulse[] = [];
  fatMaterials: LineMaterial[] = [];

  frameCount = 0;
  fpsStamp = 0;
  fps = 0;
  onFps?: (fps: number) => void;

  walkerStampedAt = 0;
  walkerSpeed = 0;

  keys = new Set<string>();
  walkHandler?: (delta: number) => void;
  phone?: PhoneView;

  constructor(
    container: HTMLElement,
    { onTrimChanged, onPathChanged, onPathSelect, onBeatMoved, onSelectBeat }: StageOptions = {},
  ) {
    this.container = container;
    this.onSelectBeat = onSelectBeat;
    this.onTrimChanged = onTrimChanged;
    this.onPathChanged = onPathChanged;
    this.onPathSelect = onPathSelect;
    this.onBeatMoved = onBeatMoved;

    this.renderer = new THREE.WebGLRenderer({ antialias: true, powerPreference: 'high-performance' });
    this.renderer.setPixelRatio(Math.min(devicePixelRatio, 2));
    container.appendChild(this.renderer.domElement);

    this.scene = new THREE.Scene();
    this.scene.background = new THREE.Color(0x0e1210);
    // Do NOT set scene.fog. Spark's splat ShaderMaterial does not implement three.js fog,
    // and the failure is silent: the scan loads, reports the right splat count, spark reports
    // 600k active splats, and precisely nothing is drawn. Depth cueing has to come from
    // elsewhere if we ever want it.

    // LOD lets us load the full capture rather than a decimated proxy and still stay
    // interactive: spark builds the level-of-detail tree client-side from a plain .spz, so
    // there is no special asset format to prepare. Splats near the camera keep every detail
    // the scan captured; distant ones collapse.
    //
    // lodRenderScale is the cheapest win — it stops the renderer spending capacity on splats
    // smaller than a couple of pixels, which at a 34 m reach viewed from 50 m is most of them.
    this.spark = new SparkRenderer({
      renderer: this.renderer,
      enableLod: true,
      lodRenderScale: 2.0,
      lodSplatScale: 1.0,
      // Raycasting runs against a coarse LOD set rather than five million gaussians, which is
      // what makes click-to-place feel instant.
      lodRaycast: 25_000,
    });
    this.scene.add(this.spark);

    this.god = new THREE.PerspectiveCamera(55, 1, 0.05, 800);
    this.god.position.set(24, 18, 24);
    this.user = new THREE.PerspectiveCamera(68, 1, 0.02, 400);

    this.controls = new OrbitControls(this.god, this.renderer.domElement);
    this.controls.enableDamping = true;
    this.controls.dampingFactor = 0.08;
    this.controls.maxPolarAngle = Math.PI * 0.495; // never go under the ground

    this.scene.add(new THREE.AmbientLight(0xffffff, 1.4));

    this.grid = new THREE.GridHelper(120, 120, 0x243029, 0x18201b);
    this.grid.position.y = -1.2;
    this.scene.add(this.grid);

    this.gizmos = new THREE.Group();
    this.scene.add(this.gizmos);

    this.axisGroup = new THREE.Group();
    this.scene.add(this.axisGroup);

    // Draggable control points for the creek axis.
    this.pathHandles = new THREE.Group();
    this.scene.add(this.pathHandles);

    this.walker = this.#buildWalker();
    this.scene.add(this.walker);

    this.clock = new THREE.Clock();
    this.raycaster = new THREE.Raycaster();
    this.pointer = new THREE.Vector2();
    this.groundPlane = new THREE.Plane(new THREE.Vector3(0, 1, 0), 1.0);

    // The standard three.js orientation gizmo, bottom-right. Click an axis to snap the camera
    // to it — which matters here because the creek runs diagonally and "look straight down the
    // reach" is otherwise a fiddly orbit every time.
    this.viewHelper = new ViewHelper(this.god, this.renderer.domElement);
    this.viewHelper.setLabels('E', 'up', 'N');
    // Top-right. The default is bottom-right, which collides with the stage toolbar and the
    // trim/path panels. `top` and `bottom` are mutually exclusive — set one to null.
    // @types/three types `bottom` and `right` as plain numbers; the helper itself reads null
    // as "not this edge", which is the only way to move it off the bottom.
    this.viewHelper.location = { top: 12, right: 12, bottom: null, left: null } as
      unknown as ViewHelper['location'];

    this.#bindEvents();
    this.resize();
    this.renderer.setAnimationLoop(() => this.#tick());
  }

  get camera(): THREE.PerspectiveCamera { return this.mode === 'god' ? this.god : this.user; }

  // ---------------------------------------------------------------- scan

  /** Precomputed extent from the journey, so we never walk 5M splats in the browser. */
  setBounds(bounds: ScanBounds | null): void { this.storedBounds = bounds ?? null; }

  async loadSplat(
    url: string,
    onProgress?: (phase: string) => void,
    { paged = false }: LoadSplatOptions = {},
  ): Promise<SplatStats | null> {
    if (this.splat) {
      this.scene.remove(this.splat);
      this.splat.dispose?.();
      this.splat = null;
    }
    if (!url) return null;

    onProgress?.('downloading');
    const started = performance.now();

    const mesh = await new Promise<SplatMesh>((resolve, reject) => {
      const m: SplatMesh = new SplatMesh({
        url,
        // With a .rad the LOD tree is already built offline (bhatt-lod, the better method) and
        // `paged` streams it in chunks, so the editor shows something almost immediately
        // instead of decoding a whole 100 MB capture first. Falling back to a plain .spz,
        // `lod: true` builds a tiny-lod tree in a WebWorker instead — correct, but it costs
        // roughly twenty seconds on a five-million-splat scan, every single load.
        lod: true,
        paged,
        // Required for runtime SplatEdit trimming.
        editable: true,
        onLoad: () => resolve(m),
      });
      // SplatMesh has no error callback in 2.x, so guard with a timeout rather than hanging
      // the editor forever on a missing file.
      // Generous, because a full 5M-splat capture is ~100 MB to decode plus an LOD tree to
      // build. This exists to fail loudly on a missing file, not to police slow hardware.
      setTimeout(() => reject(new Error(`scan did not load within 180 s: ${url}`)), 180_000);
    });

    // A SplatMesh has no real geometry, so three.js computes an empty bounding sphere for it
    // and frustum-culls the whole scan before SparkRenderer ever sees it — the symptom is a
    // scene that loads, reports the right splat count, and draws nothing at all.
    mesh.frustumCulled = false;

    this.scene.add(mesh);
    this.splat = mesh;
    this.splat.visible = this.splatVisible;
    this.#attachTrim(mesh);

    onProgress?.('measuring');
    const stats = this.storedBounds ? this.#fromStoredBounds() : this.#measure(mesh);
    this.splatStats = { ...stats, loadMs: Math.round(performance.now() - started) };
    return this.splatStats;
  }

  #fromStoredBounds(): SplatMeasurement {
    // Every field here is written together by tools/stamp-bounds.py; a hand-written block that
    // carries only some of them is a mistake worth failing on rather than papering over.
    const b = this.storedBounds!;
    return {
      count: b.splats!,
      span: b.span!,
      centre: new THREE.Vector3(b.centre!.x, b.centre!.y, b.centre!.z),
      stored: true,
    };
  }

  /**
   * Robust bounds by percentile.
   *
   * Fallback only. This does not work once LOD is enabled — the splats then live in the
   * level-of-detail structure and forEachSplat walks nothing — so a journey without stored
   * bounds will report zero. Run tools/stamp-bounds.py.
   *
   * THREE.Box3().setFromObject returns an *empty* box on a splat mesh — splats are not
   * ordinary geometry — and every Scaniverse scan carries floaters hundreds of metres out,
   * so min/max would frame the camera on nothing. Both facts cost a debugging cycle each.
   */
  #measure(mesh: SplatMesh): SplatMeasurement {
    const xs: number[] = [], ys: number[] = [], zs: number[] = [];
    mesh.forEachSplat((_i, centre) => { xs.push(centre.x); ys.push(centre.y); zs.push(centre.z); });
    const X = percentileAxis(xs), Y = percentileAxis(ys), Z = percentileAxis(zs);
    return {
      count: xs.length,
      x: X, y: Y, z: Z,
      span: { x: X.hi - X.lo, y: Y.hi - Y.lo, z: Z.hi - Z.lo },
      centre: new THREE.Vector3(X.mid, Y.mid, Z.mid),
    };
  }

  /**
   * Runtime trim: an ORIENTED box you drag with the standard three.js gizmo.
   *
   * Axis-aligned was the wrong shape. A creek runs diagonally across the scan and bends, so an
   * AABB tight enough to remove the floaters also cuts the banks off the ends. An oriented box
   * — move, rotate, scale — follows the reach and throws away far more noise for far less
   * lost content.
   *
   * It stays a *display* trim: a spark SplatEdit with an inverted box SDF at zero opacity,
   * multiplied over the scan. The asset is never modified, so a bad box costs nothing.
   */
  #attachTrim(mesh: SplatMesh): void {
    this.trimEdit = new SplatEdit({
      rgbaBlendMode: SplatEditRgbaBlendMode.MULTIPLY,
      softEdge: 0,
    });
    this.trimSdf = new SplatEditSdf({
      type: SplatEditSdfType.BOX,
      // Inverted: the box marks what to KEEP, so zero opacity applies to everything outside.
      invert: true,
      opacity: 0,
    });
    // The SDF must be a scene-graph child, not merely listed in `sdfs`: its transform is an
    // Object3D matrix and nothing updates matrixWorld for an object outside the graph.
    this.trimEdit.add(this.trimSdf);
    // NOT added to the mesh here. An attached SplatEdit keeps the scan permanently dirty, and
    // spark then re-runs its edit pass synchronously on every frame — measured at 4.5 s per
    // frame against 68 ms with the scan hidden, on a scan whose LOD was correctly serving only
    // 311k of 4.9M splats. The edit is attached by #applyTrimAttachment() only while the trim
    // is actually enabled. See setTrim().
    this.trimMesh = mesh;

    this.#ensureGizmo();
    this.setTrim(this.trim);
  }

  /**
   * Attach the edit only while it is doing something.
   *
   * Spark treats a mesh with a live SplatEdit as dirty and re-evaluates the edit every frame,
   * synchronously, whatever the edit's opacity. Leaving it attached at opacity 1 — a true no-op
   * in output terms — still cost 4.5 s per frame. Detaching costs nothing to reverse: the
   * authored box lives in `this.trim` and in the journey, and the SDF keeps its transform, so
   * this cannot repeat the failure where disabling the trim overwrote the author's extent.
   */
  #applyTrimAttachment(): void {
    if (!this.trimEdit || !this.trimMesh) return;
    const wanted = !!(this.trim?.enabled);
    const attached = this.trimEdit.parent === this.trimMesh;
    if (wanted && !attached) this.trimMesh.add(this.trimEdit);
    else if (!wanted && attached) this.trimMesh.remove(this.trimEdit);
  }

  #ensureGizmo(): void {
    if (this.gizmo) return;

    const gizmo = new TransformControls(this.god, this.renderer.domElement);
    this.gizmo = gizmo;
    gizmo.setSpace('local');   // rotate then scale along the box's own axes
    gizmo.setSize(0.9);

    // Orbiting while dragging a handle would fight the drag.
    gizmo.addEventListener('dragging-changed', (e) => {
      this.controls.enabled = !e.value && this.mode === 'god';
      if (e.value) return;
      // Drag finished — this is the moment worth persisting.
      if (this.editingPath) this.onPathChanged?.(this.readPath());
      else if (this.trimActive) this.onTrimChanged?.(this.readTrim());
      else this.onBeatMoved?.(this.readBeatPosition());
    });
    gizmo.addEventListener('objectChange', () => {
      if (this.editingPath) {
        this.centreline = this.readPath();
        this.#rebuildAxis();
        this.onPathChanged?.(this.centreline, { live: true });
      } else if (this.trimActive) {
        this.#syncTrimBox();
        this.onTrimChanged?.(this.readTrim(), { live: true });
      } else {
        this.onBeatMoved?.(this.readBeatPosition(), { live: true });
      }
    });

    // r160+ exposes the gizmo's own scene graph via getHelper(); older builds are Object3D.
    const helper: THREE.Object3D = typeof gizmo.getHelper === 'function'
      ? gizmo.getHelper()
      : (gizmo as unknown as THREE.Object3D);
    this.scene.add(helper);
    this.gizmoHelper = helper;
    helper.visible = false;
  }

  setGizmoMode(mode: GizmoMode): void {
    this.#ensureGizmo();
    this.gizmo!.setMode(mode); // 'translate' | 'rotate' | 'scale'
  }

  setGizmoVisible(visible: boolean): void {
    this.#ensureGizmo();
    this.trimActive = visible;
    if (visible && this.trimSdf) {
      this.gizmo!.attach(this.trimSdf);
    } else {
      this.gizmo!.detach();
      this.#attachBeatGizmo();   // fall back to whatever beat is selected
    }
    if (this.gizmoHelper) this.gizmoHelper.visible = visible || !!this.gizmo!.object;
    if (this.trimBox) this.trimBox.visible = visible || !!this.trim?.enabled;
  }

  /** Current box as journey data. Scale is the box half-extent. */
  readTrim(): TrimBox {
    const t = this.trimSdf!;
    const q = t.quaternion;
    return {
      enabled: !!this.trim?.enabled,
      position: { x: +t.position.x.toFixed(3), y: +t.position.y.toFixed(3), z: +t.position.z.toFixed(3) },
      rotation: [+q.x.toFixed(5), +q.y.toFixed(5), +q.z.toFixed(5), +q.w.toFixed(5)],
      halfExtent: { x: +t.scale.x.toFixed(3), y: +t.scale.y.toFixed(3), z: +t.scale.z.toFixed(3) },
    };
  }

  /**
   * @param trim oriented box {enabled, position, rotation[4], halfExtent}. Legacy axis-aligned
   *   {min,max} boxes are converted, so older journeys keep working.
   */
  setTrim(input: TrimBox | LegacyTrimBox | null | undefined): void {
    this.#ensureGizmo();

    // Bound to its own const rather than reassigning the parameter: TypeScript keeps a
    // parameter at its declared type however it is reassigned, so narrowing the legacy union
    // away only sticks on a fresh binding.
    const trim = normaliseTrim(input);
    this.trim = trim;
    if (!this.trimSdf) return;

    if (!trim) {
      this.trimSdf.opacity = 1;   // multiply by 1 — no effect, for the frames before detaching
      this.#applyTrimAttachment();
      this.#syncTrimBox();
      return;
    }

    // The SDF always carries the authored box, at the authored size. Switching the trim off
    // sets the multiply factor to 1 rather than growing the box: an inflated box would mean
    // readTrim() reports 1e5 as the extent and overwrites the author's work the moment the
    // gizmo is touched.
    this.trimSdf.position.set(trim.position.x, trim.position.y, trim.position.z);
    this.trimSdf.quaternion.fromArray([...(trim.rotation ?? [0, 0, 0, 1])]);
    this.trimSdf.scale.set(
      Math.max(0.05, trim.halfExtent.x),
      Math.max(0.05, trim.halfExtent.y),
      Math.max(0.05, trim.halfExtent.z)
    );
    this.trimSdf.opacity = trim.enabled ? 0 : 1;
    this.trimSdf.updateMatrixWorld(true);
    this.#applyTrimAttachment();
    this.#syncTrimBox();
  }

  /** A wireframe following the box, so the trim is legible even with the gizmo detached. */
  #syncTrimBox(): void {
    if (!this.trimBox) {
      const geo = new THREE.BoxGeometry(2, 2, 2); // unit half-extent, scaled by the SDF
      this.trimBox = new THREE.LineSegments(
        new THREE.EdgesGeometry(geo),
        new THREE.LineBasicMaterial({
          color: COLOUR.amber, transparent: true, opacity: 0.75, depthTest: false,
        })
      );
      this.trimBox.renderOrder = 8;
      this.scene.add(this.trimBox);
    }
    const t = this.trimSdf!;
    this.trimBox.position.copy(t.position);
    this.trimBox.quaternion.copy(t.quaternion);
    this.trimBox.scale.copy(t.scale);
    this.trimBox.visible = this.trimLayerVisible !== false
      && !!this.trim && (this.trim.enabled || !!this.gizmoHelper?.visible);
  }

  // ---------------------------------------------------------------- path editing

  /**
   * The creek axis as draggable control points.
   *
   * Clicking new points onto the scan is fine for roughing a path in, but useless for the
   * thing you actually spend time on — nudging one bend until it follows the water. So the
   * points get the same gizmo as the trim box: click a handle, drag it, everything downstream
   * (beat `s` values, the scrubber, the walker) recomputes.
   */
  setPathEditing(on: boolean): void {
    this.editingPath = on;
    if (!on) {
      this.selectedPathIndex = -1;
      this.gizmo?.detach();
      if (this.gizmoHelper) this.gizmoHelper.visible = false;
      this.#attachBeatGizmo();
    }
    this.#rebuildPathHandles();
  }

  selectPathPoint(index: number): void {
    this.#ensureGizmo();
    this.selectedPathIndex = index;

    if (index < 0 || index >= this.pathHandles.children.length) {
      this.gizmo!.detach();
      if (this.gizmoHelper) this.gizmoHelper.visible = false;
    } else {
      const handle = this.pathHandles.children[index]!;
      this.gizmo!.attach(handle);
      this.gizmo!.setMode('translate');
      if (this.gizmoHelper) this.gizmoHelper.visible = true;
    }
    this.#rebuildPathHandles();
    this.onPathSelect?.(this.selectedPathIndex);
  }

  #rebuildPathHandles(): void {
    this.pathHandles.clear();
    if (!this.editingPath) return;

    this.centreline.forEach((p, i) => {
      const selected = i === this.selectedPathIndex;
      const colour = selected ? COLOUR.accent : COLOUR.water;

      // The mesh stays the pick target — the glow behind it is a Sprite and would give a
      // sloppy, oversized hit area if it were raycast against.
      const handle = new THREE.Mesh(
        new THREE.SphereGeometry(selected ? 0.30 : 0.22, 18, 14),
        new THREE.MeshBasicMaterial({ color: 0xffffff, depthTest: false })
      );
      handle.position.set(p.x, p.y, p.z);
      handle.renderOrder = 14;
      handle.userData.pathIndex = i;
      this.pathHandles.add(handle);

      // A child of the handle, not a sibling: selectPathPoint() attaches the transform gizmo
      // via pathHandles.children[index], so one child per path point is load-bearing. It also
      // means the glow follows the handle through a drag for free. The raycast above is
      // non-recursive, so the sprite never steals the hit from the mesh.
      handle.add(glowSprite(colour, { size: selected ? 2.3 : 1.5, intensity: selected ? 1 : 0.8 }));
    });

    // Re-attach after a rebuild, or the gizmo points at a discarded mesh.
    if (this.editingPath && this.selectedPathIndex >= 0
        && this.selectedPathIndex < this.pathHandles.children.length) {
      this.gizmo?.attach(this.pathHandles.children[this.selectedPathIndex]!);
    }
  }

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
  sampleGround(x: number, z: number, { from = 40, to = -20 }: SampleGroundOptions = {}): number | null {
    if (!this.splat) return null;

    this.raycaster.set(
      new THREE.Vector3(x, from, z),
      new THREE.Vector3(0, -1, 0)
    );
    this.raycaster.far = from - to;

    const hits: THREE.Intersection[] = [];
    try {
      this.splat.raycast(this.raycaster, hits);
    } catch {
      return null;   // spark raycast is best-effort
    }
    if (!hits.length) return null;

    // Lowest hit, not nearest: a ray down through a creek passes leaves and branches long
    // before it reaches the bank, and the canopy is not something you can stand on.
    let lowest = Infinity;
    for (const h of hits) lowest = Math.min(lowest, h.point.y);
    return Number.isFinite(lowest) ? lowest : null;
  }

  /** Read the handles back into a centreline. */
  readPath(): Vec3[] {
    return this.pathHandles.children.map((h) => ({
      x: +h.position.x.toFixed(3),
      y: +h.position.y.toFixed(3),
      z: +h.position.z.toFixed(3),
    }));
  }

  /**
   * Layer visibility.
   *
   * Each of these is something the author turns on to work on and off to get out of the way.
   * Beats stay visible by default because they are the content; the path and the trim box are
   * scaffolding and only earn screen space while being edited.
   */
  setLayerVisible(layer: StageLayer, visible: boolean): void {
    switch (layer) {
      case 'beats':
        this.gizmos.visible = visible;
        this.beatsVisible = visible;
        // A hidden beat should not still be holding the gizmo.
        if (!visible && this.gizmo?.object?.userData?.beatId) {
          this.gizmo.detach();
          if (this.gizmoHelper) this.gizmoHelper.visible = false;
        }
        break;
      case 'path':
        this.axisGroup.visible = visible;
        this.pathHandles.visible = visible;
        this.walker.visible = visible;
        break;
      case 'trim':
        if (this.trimBox) this.trimBox.visible = visible;
        break;
      default:
        break;
    }
  }

  setSplatVisible(visible: boolean): void {
    this.splatVisible = visible;
    if (this.splat) this.splat.visible = visible;
  }

  // ---------------------------------------------------------------- content

  setJourney(journey: JourneyDocument | null | undefined): void {
    this.centreline = journey?.site?.centreline ?? [];
    this.beats = journey?.beats ?? [];
    this.ambient = journey?.ambient ?? [];
    this.#rebuildAxis();
    this.#rebuildGizmos();
    this.#rebuildPathHandles();
    if (this.centreline.length >= 2) {
      const ground = this.centreline[0]!.y;
      this.grid.position.y = ground - 0.05;
      this.groundPlane.constant = -ground;
    }
  }

  setSelected(id: string | null): void {
    this.selectedId = id;
    this.#rebuildGizmos();
    this.#attachBeatGizmo();
  }

  /**
   * Put the gizmo on the selected beat.
   *
   * Clicking the scan to place a beat is good for the first placement and bad for every one
   * after it: you cannot nudge, and a click near the wrong surface throws the beat somewhere
   * unexpected. Dragging is what the job actually is, so the selected beat gets the same
   * handles as the trim box and the path points.
   */
  #attachBeatGizmo(): void {
    if (this.editingPath || this.gizmoHelper?.userData?.trimMode) return;
    this.#ensureGizmo();

    const group = this.gizmos.children.find((g) => g.userData.beatId === this.selectedId);
    if (!group || this.trimActive) {
      if (!this.editingPath && !this.trimActive) {
        this.gizmo!.detach();
        if (this.gizmoHelper) this.gizmoHelper.visible = false;
      }
      return;
    }
    this.gizmo!.attach(group);
    this.gizmo!.setMode('translate');
    if (this.gizmoHelper) this.gizmoHelper.visible = true;
  }

  /** Current position of the beat the gizmo is holding. */
  readBeatPosition(): BeatMove | null {
    const obj = this.gizmo?.object;
    if (!obj || !obj.userData.beatId) return null;
    return {
      id: obj.userData.beatId,
      position: {
        x: +obj.position.x.toFixed(3),
        y: +obj.position.y.toFixed(3),
        z: +obj.position.z.toFixed(3),
      },
    };
  }

  setWalker(s: number): void {
    const previous = this.walkerS;
    this.walkerS = s;
    if (this.centreline.length < 2) return;
    const p = pointAtS(s, this.centreline);
    this.walker.position.set(p.x, p.y, p.z);

    // Face along the route. The avatar and the frustum are children of the walker, so orienting
    // it here is what makes the phone panel and the gizmo agree about which way is forward.
    const total = centrelineLength(this.centreline);
    const ahead = pointAtS(Math.min(total, s + 1.2), this.centreline);
    const behind = pointAtS(Math.max(0, s - 1.2), this.centreline);
    const dx = ahead.x - behind.x, dz = ahead.z - behind.z;
    if (Math.hypot(dx, dz) > 1e-4) this.walker.rotation.y = Math.atan2(dx, dz);

    // Metres per second, smoothed, so the gait matches how fast the scrubber is being moved
    // rather than marching on the spot whenever the walker is parked.
    const dt = Math.max(1e-3, this.clock.getElapsedTime() - this.walkerStampedAt);
    this.walkerStampedAt = this.clock.getElapsedTime();
    const instant = Math.abs(s - previous) / dt;
    this.walkerSpeed = this.walkerSpeed * 0.7 + Math.min(instant, 3) * 0.3;

    if (this.mode === 'user') this.#placeUserCamera();
  }

  /** Shown while the path is playing: the frustum is noise when nobody is walking. */
  setCameraGizmoVisible(visible: boolean): void {
    if (this.cameraGizmo) this.cameraGizmo.visible = !!visible;
  }

  // ---------------------------------------------------------------- cameras

  setMode(mode: StageMode): void {
    this.mode = mode;
    this.controls.enabled = mode === 'god';
    if (mode === 'user') this.#placeUserCamera();
    this.resize();
  }

  #placeUserCamera(): void {
    if (this.centreline.length < 2) return;
    const total = centrelineLength(this.centreline);
    const here = pointAtS(this.walkerS, this.centreline);
    const ahead = pointAtS(Math.min(total, this.walkerS + 2), this.centreline);
    this.user.position.set(here.x, here.y, here.z);
    this.user.lookAt(ahead.x, ahead.y, ahead.z);
  }

  frame(): void {
    const s = this.splatStats;
    const first = this.centreline[0];
    const target = s ? s.centre.clone()
      : (first ? new THREE.Vector3(first.x, first.y, first.z) : new THREE.Vector3());

    // Look along the creek rather than across it, and stand well clear: these scans are dense
    // riparian vegetation, so a camera placed at the fitted radius ends up inside the canopy
    // with nothing legible on screen.
    const reach = s ? Math.max(s.span.x, s.span.z) : 40;
    const r = reach * 1.15 + 10;

    let along = { x: 1, z: 0 };
    if (this.centreline.length >= 2) {
      const a = this.centreline[0]!;
      const b = this.centreline.at(-1)!;
      const dx = b.x - a.x, dz = b.z - a.z;
      const len = Math.hypot(dx, dz) || 1;
      along = { x: dx / len, z: dz / len };
    }
    // Behind the downstream end, offset to one side, and high enough to see the whole run.
    const side = { x: -along.z, z: along.x };
    this.controls.target.copy(target);
    this.god.position.set(
      target.x - along.x * r * 0.72 + side.x * r * 0.42,
      target.y + r * 0.55,
      target.z - along.z * r * 0.72 + side.z * r * 0.42
    );
    this.god.far = Math.max(400, r * 12);
    this.god.updateProjectionMatrix();
    this.controls.update();
  }

  // ---------------------------------------------------------------- gizmos

  /**
   * The visitor: a 1.70 m figure whose sternum sits on the path, carrying the phone.
   *
   * She replaces an abstract post-and-sphere marker. The post encoded the same 1.40 m but
   * conveyed no scale, so nothing on the stage answered whether a beat two metres off the path
   * is somewhere a person can actually stand, or whether the falls are a step or a wall. A
   * recognisable body answers both on sight.
   *
   * The camera frustum is attached to her chest, so what the phone panel shows and where she is
   * looking are visibly the same thing rather than two readings you have to reconcile.
   */
  #buildWalker(): THREE.Group {
    const g = new THREE.Group();

    this.avatar = buildAvatar(COLOUR.accent, COLOUR.amber);
    g.add(this.avatar);

    this.cameraGizmo = buildCameraGizmo(COLOUR.amber);
    this.cameraGizmo.visible = false;
    g.add(this.cameraGizmo);

    return g;
  }

  #rebuildAxis(): void {
    this.axisGroup.clear();
    if (this.centreline.length < 2) return;

    // The route the visitor walks, drawn as a thick bright ribbon rather than a hairline. It is
    // the spine of the piece and everything else is positioned against it, so it should be the
    // first thing found on the stage, not something you have to hunt for against the foliage.
    const pts = this.centreline.map((p) => ({ x: p.x, y: p.y, z: p.z }));
    this.axisGroup.add(this.#fatLine(pts, { colour: COLOUR.water, width: 5.5, opacity: 0.95, order: 9 }));
    // A soft wide pass underneath, so it holds up against bright sunlit water.
    this.axisGroup.add(this.#fatLine(pts, { colour: COLOUR.water, width: 13, opacity: 0.16, order: 8 }));

    // Metre ticks, so distances are readable without a HUD.
    const total = centrelineLength(this.centreline);
    for (let s = 0; s <= total; s += 5) {
      const p = pointAtS(s, this.centreline);
      const major = s % 10 === 0;
      const tick = glowSprite(COLOUR.water, { size: major ? 0.85 : 0.5, intensity: major ? 0.9 : 0.55 });
      tick.position.set(p.x, p.y, p.z);
      this.axisGroup.add(tick);
    }

    // An arrow at the upstream end — the whole piece is about direction.
    const end = pointAtS(total, this.centreline);
    const before = pointAtS(Math.max(0, total - 2), this.centreline);
    const dir = new THREE.Vector3(end.x - before.x, end.y - before.y, end.z - before.z).normalize();
    const arrow = new THREE.ArrowHelper(
      dir, new THREE.Vector3(end.x, end.y, end.z), 2.5, COLOUR.water, 0.9, 0.5
    );
    this.axisGroup.add(arrow);
  }

  #rebuildGizmos(): void {
    // Dispose before clearing. Each rebuild builds fresh geometries, materials and a canvas
    // texture per label, and clear() only detaches them — dragging a beat rebuilds on every
    // pointer move, so anything not released here accumulates for the length of the session.
    this.gizmos.traverse((node) => {
      const n = node as MaybeDrawable;
      if (n.isMesh || n.isLine || n.isSprite) {
        n.geometry?.dispose?.();
        for (const material of [n.material].flat()) {
          const m = material as MaterialWithMap | undefined;
          // Label textures are built per marker and must go. Glow textures are cached and
          // shared by every sprite of that colour — disposing one blanks all of them from the
          // next rebuild onwards.
          if (!m?.map?.userData?.shared) m?.map?.dispose?.();
          m?.dispose?.();
        }
      }
    });
    this.gizmos.clear();
    this.pulses = [];

    for (const beat of this.beats) {
      const selected = beat.id === this.selectedId;
      const colour = colourFor(beat);
      const p = beat.position;

      const group = new THREE.Group();
      group.position.set(p.x, p.y, p.z);
      group.userData.beatId = beat.id;

      // Beat positions are authored at bed height, so the ground is here, not at the path — the
      // path now runs 1.40 m overhead at the visitor's sternum.
      const y = 0.03;

      /**
       * A beam of light standing in the water.
       *
       * A sphere alone loses against a dense scan: it is a small object among a million small
       * objects, and at any distance it reads as one more splat. A vertical element does not
       * occur naturally in this scene, so it is found immediately and from any angle, and it
       * ties the marker to a specific point of ground rather than floating over the canopy.
       */
      const beamH = selected ? 3.0 : 2.2;
      const beam = new THREE.Mesh(
        new THREE.CylinderGeometry(selected ? 0.055 : 0.035, selected ? 0.012 : 0.008, beamH, 10, 1, true),
        new THREE.MeshBasicMaterial({ color: colour, transparent: true,
          opacity: selected ? 0.42 : 0.24, depthTest: false, side: THREE.DoubleSide })
      );
      beam.position.y = y + beamH / 2;
      beam.renderOrder = 10;
      group.add(beam);

      // The core, and a wire shell around it. Two shapes rather than one, because the solid
      // reads at distance and the shell gives it an edge against bright foliage.
      const core = new THREE.Mesh(
        new THREE.OctahedronGeometry(selected ? 0.30 : 0.21, 0),
        // White at the centre with the interaction colour carried by the glow around it: a
        // saturated core and a saturated halo of the same hue read as one flat blob.
        new THREE.MeshBasicMaterial({ color: 0xffffff, depthTest: false })
      );
      core.position.y = y + 0.55;
      core.renderOrder = 13;
      core.userData.beatId = beat.id;
      group.add(core);

      const glow = glowSprite(colour, { size: selected ? 3.4 : 2.2, intensity: selected ? 1 : 0.75 });
      glow.position.y = core.position.y;
      group.add(glow);

      const shell = new THREE.Mesh(
        new THREE.IcosahedronGeometry(selected ? 0.62 : 0.44, 0),
        new THREE.MeshBasicMaterial({ color: colour, wireframe: true, transparent: true,
          opacity: selected ? 0.5 : 0.28, depthTest: false })
      );
      shell.position.y = core.position.y;
      shell.renderOrder = 11;
      group.add(shell);

      // A foot marker, so the beam has somewhere to land.
      const foot = new THREE.Mesh(
        new THREE.CircleGeometry(selected ? 0.20 : 0.14, 24),
        new THREE.MeshBasicMaterial({ color: colour, transparent: true, opacity: 0.8,
          side: THREE.DoubleSide, depthTest: false })
      );
      foot.rotation.x = -Math.PI / 2;
      foot.position.y = y;
      foot.renderOrder = 10;
      group.add(foot);

      const footGlow = glowSprite(colour, { size: selected ? 1.7 : 1.15, intensity: 0.7 });
      footGlow.position.y = y + 0.02;
      group.add(footGlow);

      const label = makeLabel(beat.title ?? beat.id, colour);
      const h = selected ? 0.46 : 0.34;
      label.scale.set(h * (label.userData.aspect ?? 3), h, 1);
      label.position.y = y + beamH + 0.32;
      group.add(label);

      // Trigger geometry: enter solid, exit dashed. The gap between them is the hysteresis.
      const enter = beat.trigger?.enterRadiusM ?? 0;
      const exit = beat.trigger?.exitRadiusM ?? enter;
      group.add(this.#ring(enter, colour, selected ? 0.95 : 0.6, y, false, selected ? 5 : 3.5));
      group.add(this.#ring(exit, colour, selected ? 0.6 : 0.32, y, true, selected ? 3.5 : 2.5));

      /**
       * The audible half-life, as an expanding pulse.
       *
       * The trigger rings say where the interaction arms and say nothing about where the sound
       * carries, and those are different distances by a factor of several. Two beats whose
       * trigger rings never touch can still be audible over each other for the whole walk
       * between them, which is the composition problem on a reach this short and the one thing
       * a static map cannot show. It is animated because it is a wave, and because a moving
       * ring separates "this is sound" from "this is a threshold" without a legend.
       */
      const field = audibleField(beat);
      if (field) {
        const halo = new THREE.Mesh(
          new THREE.RingGeometry(0.965, 1.0, 96),
          new THREE.MeshBasicMaterial({ color: colour, transparent: true, opacity: 0.5,
            side: THREE.DoubleSide, depthTest: false })
        );
        halo.rotation.x = -Math.PI / 2;
        halo.position.y = y + 0.015;
        halo.renderOrder = 10;
        group.add(halo);
        this.pulses.push({ mesh: halo, radius: field.half, phase: this.pulses.length * 0.37 });

        // The half-life circle held steady, so there is something to measure against while the
        // pulse is mid-flight. The full audible reach is deliberately NOT drawn: at 10.6 m
        // against a 1.3 m half-life it swamped the beat it belonged to and overlapped every
        // neighbour, which is noise rather than information. The scrubber's armed-zone bars
        // already answer the overlap question, and answer it better.
        group.add(this.#ring(field.half, colour, selected ? 0.7 : 0.42, y + 0.01, false, 3));
      }

      this.gizmos.add(group);
    }

    for (const src of this.ambient ?? []) {
      const p = src.position;
      const marker = new THREE.Mesh(
        new THREE.SphereGeometry(0.22, 12, 8),
        new THREE.MeshBasicMaterial({ color: COLOUR.muted, transparent: true, opacity: 0.8 })
      );
      marker.position.set(p.x, p.y, p.z);
      this.gizmos.add(marker);
    }
  }

  /**
   * Frame rate, reported four times a second.
   *
   * Worth having on screen permanently rather than in a devtools panel: this stage renders a
   * five-million-splat scan twice per frame once the phone panel is open, and the cost of a
   * change is not something you can judge by eye — a drop from 60 to 24 looks like "fine" until
   * you try to drag a beat with it.
   */
  #measureFps(elapsed: number): void {
    this.frameCount += 1;
    const since = elapsed - this.fpsStamp;
    if (since < 0.25) return;
    this.fps = this.frameCount / since;
    this.frameCount = 0;
    this.fpsStamp = elapsed;
    this.onFps?.(this.fps);
  }

  /** Advances the audible-half-life pulses. Called once per frame. */
  #animatePulses(elapsed: number): void {
    for (const pulse of this.pulses) {
      // 0..1 over 2.6 s, staggered per beat so six beats do not beat in unison like a metronome.
      const t = ((elapsed / 2.6) + pulse.phase) % 1;
      const r = Math.max(0.001, t * pulse.radius);
      pulse.mesh.scale.set(r, r, 1);
      // Fades out as it goes, and eases in at the very start so it does not pop at the centre.
      pulse.mesh.material.opacity = 0.55 * Math.min(1, t * 6) * (1 - t) ** 1.4;
    }
  }

  /**
   * A line with real thickness, in pixels.
   *
   * WebGL ignores LineBasicMaterial.linewidth on every platform that matters — it is always one
   * pixel — so the path and the trigger rings were a single hairline over a dense scan and
   * effectively invisible unless you already knew where to look. Line2 draws each segment as an
   * instanced quad, which costs a little more and can actually be seen.
   *
   * LineMaterial needs the drawing-buffer size to convert pixels to clip space, so every
   * material made here is kept and updated in resize().
   */
  #fatLine(
    points: readonly Vec3[],
    { colour, width = 3, opacity = 1, dashed = false, order = 10 }: FatLineOptions,
  ): Line2 {
    const flat: number[] = [];
    for (const p of points) flat.push(p.x, p.y, p.z);

    const geo = new LineGeometry();
    geo.setPositions(flat);

    const mat = new LineMaterial({
      color: colour,
      linewidth: width,
      transparent: true,
      opacity,
      depthTest: false,
      dashed,
      dashSize: 0.42,
      gapSize: 0.3,
    });
    const { clientWidth: w, clientHeight: h } = this.container;
    mat.resolution.set(Math.max(1, w), Math.max(1, h));

    const line = new Line2(geo, mat);
    line.renderOrder = order;
    line.frustumCulled = false;
    if (dashed) line.computeLineDistances();

    this.fatMaterials.push(mat);
    return line;
  }

  #ring(radius: number, colour: number, opacity: number, y: number,
        dashed = false, width = 3): Line2 {
    const segments = 128;
    const pts: Vec3[] = [];
    for (let i = 0; i <= segments; i += 1) {
      const a = (i / segments) * Math.PI * 2;
      pts.push({ x: Math.cos(a) * radius, y, z: Math.sin(a) * radius });
    }
    return this.#fatLine(pts, { colour, width, opacity, dashed });
  }

  // ---------------------------------------------------------------- input

  #bindEvents(): void {
    const el = this.renderer.domElement;

    el.addEventListener('pointerdown', (ev) => {
      if (this.mode !== 'god') return;

      // Clicking the scene SELECTS. It never edits.
      //
      // Click-to-place was the original behaviour and it is a trap: a click that misses the
      // intended surface throws a waypoint somewhere unexpected, and there is no way to nudge
      // anything. Selection is safe and repeatable; moving things is the gizmo's job, where
      // the handle you grab says exactly which axis you are changing.
      // Only in God view, and only the helper's own corner. handleClick starts a camera
      // animation when it hits, so letting it see every click makes the camera jump on
      // clicks that were meant for the scene.
      if (this.mode === 'god' && this.viewHelper?.handleClick(ev)) return;

      const rect = el.getBoundingClientRect();
      this.pointer.x = ((ev.clientX - rect.left) / rect.width) * 2 - 1;
      this.pointer.y = -((ev.clientY - rect.top) / rect.height) * 2 + 1;
      this.raycaster.setFromCamera(this.pointer, this.god);

      if (this.editingPath) {
        const hits = this.raycaster.intersectObjects(this.pathHandles.children, false);
        if (hits.length) this.selectPathPoint(hits[0]!.object.userData.pathIndex);
        return;
      }

      // Beat markers are the only other thing worth hitting.
      const markers: THREE.Object3D[] = [];
      this.gizmos.traverse((n) => {
        if (n.userData?.beatId && (n as MaybeDrawable).isMesh) markers.push(n);
      });
      const hit = this.raycaster.intersectObjects(markers, false)[0];
      if (hit) this.onSelectBeat?.(hit.object.userData.beatId);
    });

    window.addEventListener('resize', () => this.resize());

    // User view walks with the keyboard so a designer can feel the spacing rather than
    // reading it off a map.
    window.addEventListener('keydown', (e) => {
      // Optional call: a keydown dispatched at window has no matches(), and throwing here
      // would swallow every shortcut after it.
      if ((e.target as Element | null)?.matches?.('input, textarea, select')) return;
      this.keys.add(e.key.toLowerCase());
    });
    window.addEventListener('keyup', (e) => this.keys.delete(e.key.toLowerCase()));
  }

  resize(): void {
    const { clientWidth: w, clientHeight: h } = this.container;
    if (w === 0 || h === 0) return;
    this.renderer.setSize(w, h, false);
    // LineMaterial converts its pixel width using this, so a stale value makes every thick line
    // the wrong weight after a window resize.
    for (const mat of this.fatMaterials) mat.resolution.set(w, h);
    for (const cam of [this.god, this.user]) {
      cam.aspect = w / h;
      cam.updateProjectionMatrix();
    }
  }

  onWalk(fn: (delta: number) => void): void { this.walkHandler = fn; }

  /** The phone panel draws itself into this stage's canvas, so the stage has to know about it. */
  attachPhone(phone: PhoneView): void { this.phone = phone; }

  #tick(): void {
    if (this.mode === 'god') {
      this.controls.update();
    } else if (this.walkHandler) {
      const forward = (this.keys.has('w') || this.keys.has('arrowup')) ? 1 : 0;
      const back = (this.keys.has('s') || this.keys.has('arrowdown')) ? 1 : 0;
      const delta = (forward - back) * (this.keys.has('shift') ? 0.09 : 0.035);
      if (delta !== 0) this.walkHandler(delta);
    }
    // getElapsedTime, not getDelta: the ViewHelper below consumes getDelta and resets it, so
    // driving animation from the same clock would freeze everything whenever it is not
    // animating.
    const elapsed = this.clock.getElapsedTime();
    this.#measureFps(elapsed);
    this.#animatePulses(elapsed);
    this.avatar?.userData.update?.(elapsed, this.walkerSpeed);

    /**
     * The phone panel renders BEFORE the main view, not after.
     *
     * It draws the same scan from a second camera, and spark re-sorts its splats on every
     * render call. Whichever pass runs last leaves the sort configured for its own camera, so
     * running the panel first means the main view's own pass is the one that has the final say.
     * With the panel drawing last instead, the main view spent every frame displaying an
     * ordering computed for a camera pointing somewhere else, and the whole scan flashed.
     *
     * It renders into an offscreen target here and blits into its bezel at the end of the same
     * call, so nothing it draws lands on the canvas before the main view clears it.
     */
    if (this.mode === 'god' && this.phone?.enabled && this.centreline.length >= 2) {
      this.phone.render((s) => pointAtS(s, this.centreline), centrelineLength(this.centreline));
    }

    // She is the scale reference for placement, so she belongs in God view; in user view the
    // camera is inside her head and all you would see is the inside of a torso.
    this.walker.visible = this.mode === 'god' && this.axisGroup.visible;
    this.renderer.autoClear = true;
    this.renderer.render(this.scene, this.camera);

    if (this.mode === 'god') {
      // Draws over the frame in its own viewport, so it must come after the scene render and
      // must not clear what is already there.
      this.renderer.autoClear = false;
      if (this.viewHelper.animating) this.viewHelper.update(this.clock.getDelta());
      this.viewHelper.render(this.renderer);
      this.renderer.autoClear = true;
    }

    // The panel's pixels, prepared before the main view and blitted now that the canvas has
    // been cleared and drawn.
    if (this.mode === 'god') this.phone?.present();

  }
}
