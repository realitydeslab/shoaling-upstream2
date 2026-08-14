/**
 * The 3D stage: the scan, the creek axis, the beat gizmos, and two cameras.
 *
 * God view orbits the whole reach for placement. User view stands at eye height and walks,
 * because a beat that reads fine from above can be completely hidden from a person standing
 * on the bank — and the visitor is at eye height, not overhead.
 */

import * as THREE from 'three';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';
import { TransformControls } from 'three/addons/controls/TransformControls.js';
import { ViewHelper } from 'three/addons/helpers/ViewHelper.js';
import {
  SplatMesh, SparkRenderer,
  SplatEdit, SplatEditSdf, SplatEditSdfType, SplatEditRgbaBlendMode,
} from '@sparkjsdev/spark';
import { pointAtS, centrelineLength, projectToCentreline } from './geom.js';

const EYE_HEIGHT = 1.55;

const COLOUR = {
  water: 0x6da7ad,
  accent: 0xd4707f,
  moss: 0x93a76b,
  muted: 0x55625a,
  amber: 0xd19a45,
};

export class Stage {
  constructor(container, { onPick, onTrimChanged, onPathChanged, onPathSelect, onBeatMoved } = {}) {
    this.container = container;
    this.onPick = onPick;
    this.onTrimChanged = onTrimChanged;
    this.onPathChanged = onPathChanged;
    this.onPathSelect = onPathSelect;
    this.onBeatMoved = onBeatMoved;
    this.trimActive = false;
    this.mode = 'god';
    this.placing = false;
    this.drawingPath = false;
    this.editingPath = false;
    this.selectedPathIndex = -1;
    this.splat = null;
    this.splatVisible = true;
    this.beats = [];
    this.centreline = [];
    this.selectedId = null;
    this.walkerS = 0;

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
    this.viewHelper.location = { top: 12, right: 12, bottom: null, left: null };

    this.#bindEvents();
    this.resize();
    this.renderer.setAnimationLoop(() => this.#tick());
  }

  get camera() { return this.mode === 'god' ? this.god : this.user; }

  // ---------------------------------------------------------------- scan

  /** Precomputed extent from the journey, so we never walk 5M splats in the browser. */
  setBounds(bounds) { this.storedBounds = bounds ?? null; }

  async loadSplat(url, onProgress, { paged = false } = {}) {
    if (this.splat) {
      this.scene.remove(this.splat);
      this.splat.dispose?.();
      this.splat = null;
    }
    if (!url) return null;

    onProgress?.('downloading');
    const started = performance.now();

    const mesh = await new Promise((resolve, reject) => {
      const m = new SplatMesh({
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

  #fromStoredBounds() {
    const b = this.storedBounds;
    return {
      count: b.splats,
      span: b.span,
      centre: new THREE.Vector3(b.centre.x, b.centre.y, b.centre.z),
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
  #measure(mesh) {
    const xs = [], ys = [], zs = [];
    mesh.forEachSplat((_i, centre) => { xs.push(centre.x); ys.push(centre.y); zs.push(centre.z); });
    const pick = (arr, p) => arr[Math.min(arr.length - 1, Math.max(0, Math.floor(arr.length * p)))];
    const axis = (arr) => {
      arr.sort((a, b) => a - b);
      return { lo: pick(arr, 0.01), hi: pick(arr, 0.99), mid: pick(arr, 0.5) };
    };
    const X = axis(xs), Y = axis(ys), Z = axis(zs);
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
  #attachTrim(mesh) {
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
    // Parenting to the mesh scopes the edit to this scan; an edit with no SplatMesh ancestor
    // applies to every editable mesh in the scene.
    mesh.add(this.trimEdit);

    this.#ensureGizmo();
    this.setTrim(this.trim);
  }

  #ensureGizmo() {
    if (this.gizmo) return;

    this.gizmo = new TransformControls(this.god, this.renderer.domElement);
    this.gizmo.setSpace('local');   // rotate then scale along the box's own axes
    this.gizmo.setSize(0.9);

    // Orbiting while dragging a handle would fight the drag.
    this.gizmo.addEventListener('dragging-changed', (e) => {
      this.controls.enabled = !e.value && this.mode === 'god';
      if (e.value) return;
      // Drag finished — this is the moment worth persisting.
      if (this.editingPath) this.onPathChanged?.(this.readPath());
      else if (this.trimActive) this.onTrimChanged?.(this.readTrim());
      else this.onBeatMoved?.(this.readBeatPosition());
    });
    this.gizmo.addEventListener('objectChange', () => {
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
    const helper = typeof this.gizmo.getHelper === 'function'
      ? this.gizmo.getHelper()
      : this.gizmo;
    this.scene.add(helper);
    this.gizmoHelper = helper;
    helper.visible = false;
  }

  setGizmoMode(mode) {
    this.#ensureGizmo();
    this.gizmo.setMode(mode); // 'translate' | 'rotate' | 'scale'
  }

  setGizmoVisible(visible) {
    this.#ensureGizmo();
    this.trimActive = visible;
    if (visible && this.trimSdf) {
      this.gizmo.attach(this.trimSdf);
    } else {
      this.gizmo.detach();
      this.#attachBeatGizmo();   // fall back to whatever beat is selected
    }
    if (this.gizmoHelper) this.gizmoHelper.visible = visible || !!this.gizmo.object;
    if (this.trimBox) this.trimBox.visible = visible || !!this.trim?.enabled;
  }

  /** Current box as journey data. Scale is the box half-extent. */
  readTrim() {
    const t = this.trimSdf;
    return {
      enabled: !!this.trim?.enabled,
      position: { x: +t.position.x.toFixed(3), y: +t.position.y.toFixed(3), z: +t.position.z.toFixed(3) },
      rotation: t.quaternion.toArray().map((v) => +v.toFixed(5)),
      halfExtent: { x: +t.scale.x.toFixed(3), y: +t.scale.y.toFixed(3), z: +t.scale.z.toFixed(3) },
    };
  }

  /**
   * @param trim oriented box {enabled, position, rotation[4], halfExtent}. Legacy axis-aligned
   *   {min,max} boxes are converted, so older journeys keep working.
   */
  setTrim(trim) {
    this.#ensureGizmo();

    if (trim && trim.min && trim.max && !trim.halfExtent) {
      trim = {
        enabled: !!trim.enabled,
        position: {
          x: (trim.min.x + trim.max.x) / 2,
          y: (trim.min.y + trim.max.y) / 2,
          z: (trim.min.z + trim.max.z) / 2,
        },
        rotation: [0, 0, 0, 1],
        halfExtent: {
          x: Math.max(0.05, (trim.max.x - trim.min.x) / 2),
          y: Math.max(0.05, (trim.max.y - trim.min.y) / 2),
          z: Math.max(0.05, (trim.max.z - trim.min.z) / 2),
        },
      };
    }

    this.trim = trim ?? null;
    if (!this.trimSdf) return;

    if (!trim) {
      this.trimSdf.opacity = 1;   // multiply by 1 — no effect
      this.#syncTrimBox();
      return;
    }

    // The SDF always carries the authored box, at the authored size. Switching the trim off
    // sets the multiply factor to 1 rather than growing the box: an inflated box would mean
    // readTrim() reports 1e5 as the extent and overwrites the author's work the moment the
    // gizmo is touched.
    this.trimSdf.position.set(trim.position.x, trim.position.y, trim.position.z);
    this.trimSdf.quaternion.fromArray(trim.rotation ?? [0, 0, 0, 1]);
    this.trimSdf.scale.set(
      Math.max(0.05, trim.halfExtent.x),
      Math.max(0.05, trim.halfExtent.y),
      Math.max(0.05, trim.halfExtent.z)
    );
    this.trimSdf.opacity = trim.enabled ? 0 : 1;
    this.trimSdf.updateMatrixWorld(true);
    this.#syncTrimBox();
  }

  /** A wireframe following the box, so the trim is legible even with the gizmo detached. */
  #syncTrimBox() {
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
    const t = this.trimSdf;
    this.trimBox.position.copy(t.position);
    this.trimBox.quaternion.copy(t.quaternion);
    this.trimBox.scale.copy(t.scale);
    this.trimBox.visible = !!this.trim && (this.trim.enabled || this.gizmoHelper?.visible);
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
  setPathEditing(on) {
    this.editingPath = on;
    if (!on) {
      this.selectedPathIndex = -1;
      this.gizmo?.detach();
      if (this.gizmoHelper) this.gizmoHelper.visible = false;
      this.#attachBeatGizmo();
    }
    this.#rebuildPathHandles();
  }

  selectPathPoint(index) {
    this.#ensureGizmo();
    this.selectedPathIndex = index;

    if (index < 0 || index >= this.pathHandles.children.length) {
      this.gizmo.detach();
      if (this.gizmoHelper) this.gizmoHelper.visible = false;
    } else {
      const handle = this.pathHandles.children[index];
      this.gizmo.attach(handle);
      this.gizmo.setMode('translate');
      if (this.gizmoHelper) this.gizmoHelper.visible = true;
    }
    this.#rebuildPathHandles();
    this.onPathSelect?.(this.selectedPathIndex);
  }

  #rebuildPathHandles() {
    this.pathHandles.clear();
    if (!this.editingPath) return;

    this.centreline.forEach((p, i) => {
      const selected = i === this.selectedPathIndex;
      const handle = new THREE.Mesh(
        new THREE.SphereGeometry(selected ? 0.45 : 0.34, 16, 12),
        new THREE.MeshBasicMaterial({
          color: selected ? COLOUR.accent : COLOUR.water,
          depthTest: false,
        })
      );
      handle.position.set(p.x, p.y, p.z);
      handle.renderOrder = 14;
      handle.userData.pathIndex = i;
      this.pathHandles.add(handle);
    });

    // Re-attach after a rebuild, or the gizmo points at a discarded mesh.
    if (this.editingPath && this.selectedPathIndex >= 0
        && this.selectedPathIndex < this.pathHandles.children.length) {
      this.gizmo?.attach(this.pathHandles.children[this.selectedPathIndex]);
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
  sampleGround(x, z, { from = 40, to = -20 } = {}) {
    if (!this.splat) return null;

    this.raycaster.set(
      new THREE.Vector3(x, from, z),
      new THREE.Vector3(0, -1, 0)
    );
    this.raycaster.far = from - to;

    const hits = [];
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
  readPath() {
    return this.pathHandles.children.map((h) => ({
      x: +h.position.x.toFixed(3),
      y: +h.position.y.toFixed(3),
      z: +h.position.z.toFixed(3),
    }));
  }

  setSplatVisible(visible) {
    this.splatVisible = visible;
    if (this.splat) this.splat.visible = visible;
  }

  // ---------------------------------------------------------------- content

  setJourney(journey) {
    this.centreline = journey?.site?.centreline ?? [];
    this.beats = journey?.beats ?? [];
    this.ambient = journey?.ambient ?? [];
    this.#rebuildAxis();
    this.#rebuildGizmos();
    this.#rebuildPathHandles();
    if (this.centreline.length >= 2) {
      const ground = this.centreline[0].y;
      this.grid.position.y = ground - 0.05;
      this.groundPlane.constant = -ground;
    }
  }

  setSelected(id) {
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
  #attachBeatGizmo() {
    if (this.editingPath || this.gizmoHelper?.userData?.trimMode) return;
    this.#ensureGizmo();

    const group = this.gizmos.children.find((g) => g.userData.beatId === this.selectedId);
    if (!group || this.trimActive) {
      if (!this.editingPath && !this.trimActive) {
        this.gizmo.detach();
        if (this.gizmoHelper) this.gizmoHelper.visible = false;
      }
      return;
    }
    this.gizmo.attach(group);
    this.gizmo.setMode('translate');
    if (this.gizmoHelper) this.gizmoHelper.visible = true;
  }

  /** Current position of the beat the gizmo is holding. */
  readBeatPosition() {
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

  setWalker(s) {
    this.walkerS = s;
    if (this.centreline.length < 2) return;
    const p = pointAtS(s, this.centreline);
    this.walker.position.set(p.x, p.y, p.z);
    if (this.mode === 'user') this.#placeUserCamera();
  }

  // ---------------------------------------------------------------- cameras

  setMode(mode) {
    this.mode = mode;
    this.controls.enabled = mode === 'god';
    if (mode === 'user') this.#placeUserCamera();
    this.resize();
  }

  #placeUserCamera() {
    if (this.centreline.length < 2) return;
    const total = centrelineLength(this.centreline);
    const here = pointAtS(this.walkerS, this.centreline);
    const ahead = pointAtS(Math.min(total, this.walkerS + 2), this.centreline);
    this.user.position.set(here.x, here.y + EYE_HEIGHT, here.z);
    this.user.lookAt(ahead.x, ahead.y + EYE_HEIGHT * 0.85, ahead.z);
  }

  frame() {
    const s = this.splatStats;
    const target = s ? s.centre.clone()
      : (this.centreline.length ? new THREE.Vector3(
          this.centreline[0].x, this.centreline[0].y, this.centreline[0].z) : new THREE.Vector3());

    // Look along the creek rather than across it, and stand well clear: these scans are dense
    // riparian vegetation, so a camera placed at the fitted radius ends up inside the canopy
    // with nothing legible on screen.
    const reach = s ? Math.max(s.span.x, s.span.z) : 40;
    const r = reach * 1.15 + 10;

    let along = { x: 1, z: 0 };
    if (this.centreline.length >= 2) {
      const a = this.centreline[0];
      const b = this.centreline.at(-1);
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

  #buildWalker() {
    const g = new THREE.Group();
    const post = new THREE.Mesh(
      new THREE.CylinderGeometry(0.05, 0.05, EYE_HEIGHT, 8),
      new THREE.MeshBasicMaterial({ color: COLOUR.accent, depthTest: false })
    );
    post.renderOrder = 12;
    post.position.y = EYE_HEIGHT / 2;
    g.add(post);
    const head = new THREE.Mesh(
      new THREE.SphereGeometry(0.16, 16, 12),
      new THREE.MeshBasicMaterial({ color: COLOUR.accent, depthTest: false })
    );
    head.renderOrder = 12;
    head.position.y = EYE_HEIGHT;
    g.add(head);
    const ring = new THREE.Mesh(
      new THREE.RingGeometry(0.45, 0.55, 32),
      new THREE.MeshBasicMaterial({ color: COLOUR.accent, transparent: true, opacity: 0.7,
        side: THREE.DoubleSide, depthTest: false })
    );
    ring.rotation.x = -Math.PI / 2;
    ring.position.y = 0.02;
    g.add(ring);
    return g;
  }

  #rebuildAxis() {
    this.axisGroup.clear();
    if (this.centreline.length < 2) return;

    const pts = this.centreline.map((p) => new THREE.Vector3(p.x, p.y, p.z));
    const line = new THREE.Line(
      new THREE.BufferGeometry().setFromPoints(pts),
      new THREE.LineBasicMaterial({ color: COLOUR.water, transparent: true, opacity: 0.85, depthTest: false })
    );
    line.renderOrder = 9;
    this.axisGroup.add(line);

    // Metre ticks, so distances are readable without a HUD.
    const total = centrelineLength(this.centreline);
    for (let s = 0; s <= total; s += 5) {
      const p = pointAtS(s, this.centreline);
      const tick = new THREE.Mesh(
        new THREE.SphereGeometry(s % 10 === 0 ? 0.14 : 0.08, 8, 6),
        new THREE.MeshBasicMaterial({ color: COLOUR.water, transparent: true, opacity: 0.6, depthTest: false })
      );
      tick.renderOrder = 9;
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

  #rebuildGizmos() {
    this.gizmos.clear();

    for (const beat of this.beats) {
      const selected = beat.id === this.selectedId;
      const colour = selected ? COLOUR.accent : COLOUR.moss;
      const p = beat.position;

      const group = new THREE.Group();
      group.position.set(p.x, p.y, p.z);
      group.userData.beatId = beat.id;

      const marker = new THREE.Mesh(
        new THREE.SphereGeometry(selected ? 0.34 : 0.26, 16, 12),
        new THREE.MeshBasicMaterial({ color: colour, depthTest: false })
      );
      marker.renderOrder = 10;
      marker.userData.beatId = beat.id;
      group.add(marker);

      // Enter and exit bands drawn as rings on the ground. Two rings, not one, because
      // hysteresis is the thing an author most needs to see: the gap between them is what
      // stops a beat flickering when someone stands near its edge.
      const enter = beat.trigger?.enterRadiusM ?? 0;
      const exit = beat.trigger?.exitRadiusM ?? enter;
      const y = -(p.y) + (this.centreline[0]?.y ?? p.y) + 0.03;

      group.add(this.#ring(enter, colour, selected ? 0.55 : 0.3, y));
      group.add(this.#ring(exit, colour, selected ? 0.25 : 0.12, y, true));

      // A stem to the ground so height is legible from a low camera.
      const stem = new THREE.Line(
        new THREE.BufferGeometry().setFromPoints([
          new THREE.Vector3(0, 0, 0), new THREE.Vector3(0, y, 0),
        ]),
        new THREE.LineBasicMaterial({ color: colour, transparent: true, opacity: 0.4, depthTest: false })
      );
      stem.renderOrder = 10;
      group.add(stem);

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

  #ring(radius, colour, opacity, y, dashed = false) {
    const segments = 96;
    const pts = [];
    for (let i = 0; i <= segments; i += 1) {
      const a = (i / segments) * Math.PI * 2;
      pts.push(new THREE.Vector3(Math.cos(a) * radius, y, Math.sin(a) * radius));
    }
    const geo = new THREE.BufferGeometry().setFromPoints(pts);
    const mat = dashed
      ? new THREE.LineDashedMaterial({ color: colour, transparent: true, opacity,
          dashSize: 0.5, gapSize: 0.35, depthTest: false })
      : new THREE.LineBasicMaterial({ color: colour, transparent: true, opacity, depthTest: false });
    const line = new THREE.Line(geo, mat);
    line.renderOrder = 10;
    if (dashed) line.computeLineDistances();
    return line;
  }

  // ---------------------------------------------------------------- input

  #bindEvents() {
    const el = this.renderer.domElement;

    el.addEventListener('pointerdown', (ev) => {
      // The orientation gizmo owns its corner of the screen.
      if (this.mode === 'god' && this.viewHelper?.handleClick(ev)) return;

      // While editing the path, a click picks a control point rather than placing anything.
      if (this.editingPath && this.mode === 'god') {
        const rect = el.getBoundingClientRect();
        this.pointer.x = ((ev.clientX - rect.left) / rect.width) * 2 - 1;
        this.pointer.y = -((ev.clientY - rect.top) / rect.height) * 2 + 1;
        this.raycaster.setFromCamera(this.pointer, this.god);
        const hits = this.raycaster.intersectObjects(this.pathHandles.children, false);
        if (hits.length) {
          this.selectPathPoint(hits[0].object.userData.pathIndex);
          return;
        }
      }
      if (!(this.placing || this.drawingPath) || this.mode !== 'god') return;
      const rect = el.getBoundingClientRect();
      this.pointer.x = ((ev.clientX - rect.left) / rect.width) * 2 - 1;
      this.pointer.y = -((ev.clientY - rect.top) / rect.height) * 2 + 1;
      this.raycaster.setFromCamera(this.pointer, this.god);

      // Prefer a hit on the scan surface; fall back to the ground plane so placement still
      // works with the scan hidden or in a gap between splats.
      let hit = null;
      if (this.splat && this.splatVisible && typeof this.splat.raycast === 'function') {
        const hits = [];
        try {
          this.splat.raycast(this.raycaster, hits);
        } catch { /* spark raycast is best-effort; the plane fallback covers it */ }
        if (hits.length) {
          hits.sort((a, b) => a.distance - b.distance);
          hit = hits[0].point.clone();
        }
      }
      if (!hit) {
        hit = new THREE.Vector3();
        if (!this.raycaster.ray.intersectPlane(this.groundPlane, hit)) return;
      }

      const projected = this.centreline.length >= 2
        ? projectToCentreline(hit, this.centreline)
        : { s: 0, lateral: 0 };

      this.onPick?.({
        point: { x: +hit.x.toFixed(3), y: +hit.y.toFixed(3), z: +hit.z.toFixed(3) },
        s: +projected.s.toFixed(2),
        lateral: +projected.lateral.toFixed(2),
        path: this.drawingPath,
      });
    });

    window.addEventListener('resize', () => this.resize());

    // User view walks with the keyboard so a designer can feel the spacing rather than
    // reading it off a map.
    this.keys = new Set();
    window.addEventListener('keydown', (e) => {
      if (e.target.matches('input, textarea, select')) return;
      this.keys.add(e.key.toLowerCase());
    });
    window.addEventListener('keyup', (e) => this.keys.delete(e.key.toLowerCase()));
  }

  resize() {
    const { clientWidth: w, clientHeight: h } = this.container;
    if (w === 0 || h === 0) return;
    this.renderer.setSize(w, h, false);
    for (const cam of [this.god, this.user]) {
      cam.aspect = w / h;
      cam.updateProjectionMatrix();
    }
  }

  onWalk(fn) { this.walkHandler = fn; }

  #tick() {
    if (this.mode === 'god') {
      this.controls.update();
    } else if (this.walkHandler) {
      const forward = (this.keys.has('w') || this.keys.has('arrowup')) ? 1 : 0;
      const back = (this.keys.has('s') || this.keys.has('arrowdown')) ? 1 : 0;
      const delta = (forward - back) * (this.keys.has('shift') ? 0.09 : 0.035);
      if (delta !== 0) this.walkHandler(delta);
    }
    this.walker.visible = this.mode === 'god';
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
  }
}
