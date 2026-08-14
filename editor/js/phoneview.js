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
// The walking path is dropped onto the ground by raycast, so the camera stands on it the same
// way scene.ts places the User camera and app.ts places the audio listener. Keep these three
// in step: at UBC the path sits around y = -0.6 with beats down at y = -1.8, so an offset of
// zero puts the phone in the gravel and every beat overhead.
/**
 * How often the panel redraws, in milliseconds.
 *
 * Every redraw costs the main view one frame of wrong splat ordering (see render()), so this is
 * the dial between a smooth panel and a steady stage. 12 Hz is fast enough to follow a walk and
 * slow enough that the stage settles between disturbances.
 */
const PANEL_INTERVAL_MS = 1000 / 12;
// Phone proportions, so the framing is honest about what fits on screen.
const ASPECT = 19.5 / 9;
const SHOAL_MAX = 200;
const SHOAL_START = 40;
// Clear of the ViewHelper — 128 px square inset 12 from the top right — and of the stage
// toolbar and caption along the bottom. Overlapping either would make it unusable, because
// the phone is drawn last and wins.
const CLEAR_TOP = 152;
const CLEAR_BOTTOM = 86;
const CLEAR_RIGHT = 16;
const BEZEL = 11;
const MAX_HEIGHT = 440;
const MIN_HEIGHT = 140;
export class PhoneView {
    stage;
    enabled = true;
    s = 0;
    yaw = 0;
    pitch = -0.12;
    dragging = false;
    rect = null;
    camera;
    effects;
    shoal;
    clock;
    eggs = [];
    flashes = [];
    heron = null;
    el;
    screen;
    caption;
    last = { x: 0, y: 0 };
    target;
    quad;
    quadScene;
    quadCamera;
    lastDraw = 0;
    lastLabel = null;
    pending = null;
    constructor(stage) {
        this.stage = stage;
        this.camera = new THREE.PerspectiveCamera(62, 1 / ASPECT, 0.02, 300);
        this.effects = new THREE.Group();
        stage.scene.add(this.effects);
        this.shoal = this.#buildShoal();
        this.effects.add(this.shoal);
        this.clock = new THREE.Clock();
        this.#buildBezel();
    }
    // ---------------------------------------------------------------- shoal
    /**
     * The shoal, as a point cloud that thins when fish are given away.
     *
     * Count is the thing that carries meaning: giving twelve fish to the heron has to be
     * visible as *fewer*, not as a number changing. Drawn near the camera because the visitor
     * IS the shoal — they are inside it, not watching it.
     */
    #buildShoal() {
        const geo = new THREE.BufferGeometry();
        const pos = new Float32Array(SHOAL_MAX * 3);
        const phase = new Float32Array(SHOAL_MAX);
        for (let i = 0; i < SHOAL_MAX; i += 1)
            phase[i] = Math.random() * Math.PI * 2;
        geo.setAttribute('position', new THREE.BufferAttribute(pos, 3));
        geo.userData.phase = phase;
        const points = new THREE.Points(geo, new THREE.PointsMaterial({
            color: 0xd8e6e0, size: 0.09, transparent: true, opacity: 0.85,
            depthTest: false, sizeAttenuation: true,
        }));
        points.renderOrder = 20;
        points.frustumCulled = false;
        points.userData.count = SHOAL_START;
        // Held around the camera, so in the God view it would read as a cloud of dots orbiting
        // the walker gizmo. Shown only while the phone viewport is being drawn.
        points.visible = false;
        return points;
    }
    setShoalCount(n) {
        this.shoal.userData.count = Math.max(0, Math.min(SHOAL_MAX, n | 0));
    }
    get shoalCount() { return this.shoal.userData.count; }
    #updateShoal(t, origin, forward) {
        const geo = this.shoal.geometry;
        // The attribute is the Float32Array built in #buildShoal; three types it as the union of
        // every typed array it could have been.
        const attr = geo.attributes.position;
        const pos = attr.array;
        const phase = geo.userData.phase;
        const n = this.shoal.userData.count;
        // A ring of fish held at a short radius around the listener. The convincing cue for
        // "you are many" is being surrounded at a distance, not the sound or sight of a crowd
        // somewhere else — fish hold station by their lateral line, a pressure sense.
        const right = new THREE.Vector3(forward.z, 0, -forward.x).normalize();
        for (let i = 0; i < n; i += 1) {
            const a = phase[i] + t * (0.25 + (i % 7) * 0.03);
            const radius = 0.7 + (i % 5) * 0.35;
            const drop = -0.35 - (i % 4) * 0.18;
            const ahead = Math.cos(a) * radius + 0.8;
            const side = Math.sin(a) * radius;
            pos[i * 3] = origin.x + forward.x * ahead + right.x * side;
            pos[i * 3 + 1] = origin.y + drop + Math.sin(a * 2.1) * 0.06;
            pos[i * 3 + 2] = origin.z + forward.z * ahead + right.z * side;
        }
        attr.needsUpdate = true;
        geo.setDrawRange(0, n);
    }
    // ---------------------------------------------------------------- effects
    /** Eggs settling into gravel — the crouch beats. */
    spawnEggs(at) {
        const geo = new THREE.SphereGeometry(0.035, 8, 6);
        const mat = new THREE.MeshBasicMaterial({ color: 0xffd9b0, transparent: true, opacity: 0.95 });
        const group = new THREE.Group();
        for (let i = 0; i < 26; i += 1) {
            const egg = new THREE.Mesh(geo, mat);
            const a = Math.random() * Math.PI * 2;
            const r = Math.random() * 0.45;
            egg.position.set(at.x + Math.cos(a) * r, at.y + 0.9 + Math.random() * 0.3, at.z + Math.sin(a) * r);
            egg.userData.restY = at.y - 0.05 - Math.random() * 0.04;
            egg.userData.v = 0;
            group.add(egg);
        }
        group.userData.resources = [geo, mat];
        this.effects.add(group);
        this.eggs.push(group);
        return group;
    }
    /** The heron: a still shape at the pool that leaves when it has been fed. */
    showHeron(at) {
        this.#remove(this.heron);
        const feather = new THREE.MeshBasicMaterial({ color: 0x9aa7ad, transparent: true });
        const shank = new THREE.MeshBasicMaterial({ color: 0x6d7a63, transparent: true });
        const bodyGeo = new THREE.CapsuleGeometry(0.16, 0.42, 4, 8);
        const neckGeo = new THREE.CylinderGeometry(0.045, 0.06, 0.55, 6);
        const legGeo = new THREE.CylinderGeometry(0.02, 0.02, 0.72, 5);
        const body = new THREE.Mesh(bodyGeo, feather);
        body.position.y = 0.95;
        body.rotation.z = 0.25;
        const neck = new THREE.Mesh(neckGeo, feather);
        neck.position.set(0.08, 1.42, 0);
        neck.rotation.z = -0.35;
        const legs = new THREE.Mesh(legGeo, shank);
        legs.position.y = 0.38;
        const g = new THREE.Group();
        g.add(body, neck, legs);
        g.position.set(at.x, at.y, at.z);
        g.userData.resources = [bodyGeo, neckGeo, legGeo, feather, shank];
        this.effects.add(g);
        this.heron = g;
        return g;
    }
    /** The heron takes: fish stripped from the shoal, and the bird lifts away. */
    heronFeeds(count) {
        this.setShoalCount(this.shoal.userData.count - (count ?? 0));
        if (this.heron)
            this.heron.userData.leavingAt = performance.now();
    }
    /**
     * Taking an insect — the catch beats.
     *
     * One mote lifting off the surface and a ring where it broke it, over in under a second.
     * The gesture it stands for is a lunge and a stop; anything that lingers would read as a
     * place to wait at, which is the opposite of what a catch is.
     */
    takeInsect(at) {
        const moteGeo = new THREE.SphereGeometry(0.05, 8, 6);
        const moteMat = new THREE.MeshBasicMaterial({
            color: 0xf3e3c0, transparent: true, depthTest: false,
        });
        const ringGeo = new THREE.RingGeometry(0.9, 1, 40);
        const ringMat = new THREE.MeshBasicMaterial({
            color: 0xd8e6e0, transparent: true, side: THREE.DoubleSide, depthTest: false,
        });
        const mote = new THREE.Mesh(moteGeo, moteMat);
        mote.position.set(at.x, at.y + 0.1, at.z);
        mote.renderOrder = 21;
        const ring = new THREE.Mesh(ringGeo, ringMat);
        ring.rotation.x = -Math.PI / 2;
        ring.position.set(at.x, at.y + 0.02, at.z);
        ring.renderOrder = 21;
        const g = new THREE.Group();
        g.add(mote, ring);
        g.userData.born = performance.now();
        g.userData.mote = mote;
        g.userData.ring = ring;
        g.userData.resources = [moteGeo, moteMat, ringGeo, ringMat];
        this.effects.add(g);
        this.flashes.push(g);
        return g;
    }
    clearEffects() {
        for (const g of this.eggs)
            this.#remove(g);
        this.eggs.length = 0;
        for (const g of this.flashes)
            this.#remove(g);
        this.flashes.length = 0;
        this.#remove(this.heron);
        this.heron = null;
    }
    /** Back to the start of the journey: no eggs cut, no heron fed, the shoal whole. */
    reset() {
        this.clearEffects();
        this.setShoalCount(SHOAL_START);
    }
    #remove(group) {
        if (!group)
            return;
        this.effects.remove(group);
        for (const resource of (group.userData.resources ?? []))
            resource.dispose();
    }
    #tickEffects(dt) {
        // Eggs fall and settle.
        for (const group of this.eggs) {
            for (const egg of group.children) {
                if (egg.position.y > egg.userData.restY) {
                    egg.userData.v += 2.2 * dt;
                    egg.position.y = Math.max(egg.userData.restY, egg.position.y - egg.userData.v * dt);
                }
            }
        }
        // The heron lifts away after it has been fed.
        if (this.heron?.userData.leavingAt) {
            const age = (performance.now() - this.heron.userData.leavingAt) / 1000;
            this.heron.position.y += dt * 0.9;
            this.heron.rotation.y += dt * 0.4;
            for (const part of this.heron.children) {
                part
                    .material.opacity = Math.max(0, 1 - age / 3);
            }
            if (age > 3.2) {
                this.#remove(this.heron);
                this.heron = null;
            }
        }
        // The insect flash: a mote up, a ring out, both gone inside a second.
        for (let i = this.flashes.length - 1; i >= 0; i -= 1) {
            const g = this.flashes[i];
            const k = Math.min(1, (performance.now() - g.userData.born) / 900);
            g.userData.mote.position.y += dt * 0.85;
            g.userData.mote.material.opacity = 1 - k;
            g.userData.ring.scale.setScalar(0.06 + k * 0.5);
            g.userData.ring.material.opacity = 0.7 * (1 - k);
            if (k >= 1) {
                this.#remove(g);
                this.flashes.splice(i, 1);
            }
        }
    }
    // ---------------------------------------------------------------- bezel and look
    #buildBezel() {
        const frame = document.createElement('div');
        frame.className = 'phone';
        frame.style.display = 'none';
        const screen = document.createElement('div');
        screen.className = 'phone-screen';
        screen.title = 'Drag to look around, double-click to face back down the reach. '
            + 'The body follows the walking path; only the head turns.';
        const caption = document.createElement('div');
        caption.className = 'phone-caption';
        frame.append(screen, caption);
        this.stage.container.appendChild(frame);
        this.el = frame;
        this.screen = screen;
        this.caption = caption;
        screen.addEventListener('pointerdown', (ev) => {
            this.dragging = true;
            this.last = { x: ev.clientX, y: ev.clientY };
            screen.setPointerCapture(ev.pointerId);
            ev.preventDefault();
        });
        screen.addEventListener('pointermove', (ev) => {
            if (!this.dragging)
                return;
            this.yaw -= (ev.clientX - this.last.x) * 0.005;
            this.pitch = Math.max(-1.2, Math.min(1.0, this.pitch - (ev.clientY - this.last.y) * 0.005));
            this.last = { x: ev.clientX, y: ev.clientY };
        });
        const release = (ev) => {
            this.dragging = false;
            if (screen.hasPointerCapture(ev.pointerId))
                screen.releasePointerCapture(ev.pointerId);
        };
        screen.addEventListener('pointerup', release);
        screen.addEventListener('pointercancel', release);
        // Facing back down the reach is otherwise a matter of dragging until it looks right.
        screen.addEventListener('dblclick', () => { this.yaw = 0; this.pitch = -0.12; });
    }
    setEnabled(on) {
        this.enabled = on;
        this.dragging = false;
        // Swallow the elapsed time, or the effects jump the whole pause on the first frame back.
        if (on)
            this.clock.getDelta();
        else
            this.hide();
    }
    /** Take the bezel off the stage without forgetting where the walker had got to. */
    hide() {
        this.rect = null;
        this.el.style.display = 'none';
    }
    setS(s) { this.s = s; }
    // ---------------------------------------------------------------- render
    /**
     * Called from the stage's animation loop, after the main scene and the ViewHelper.
     *
     * @param pointAtS  (s) => {x, y, z} on the walking path; the caller binds the centreline.
     * @param totalLength  reach length in metres, so the look-ahead never runs off the end.
     */
    render(pointAtS, totalLength) {
        if (!this.enabled)
            return;
        const renderer = this.stage.renderer;
        const canvas = renderer.domElement;
        const W = canvas.clientWidth, H = canvas.clientHeight;
        const rect = this.#layout(W, H);
        if (!rect)
            return;
        const dt = Math.min(0.05, this.clock.getDelta());
        const t = performance.now() / 1000;
        const here = pointAtS(this.s);
        const ahead = pointAtS(Math.min(totalLength, this.s + 2));
        const dir = new THREE.Vector3(ahead.x - here.x, 0, ahead.z - here.z);
        if (dir.lengthSq() < 1e-6)
            dir.set(0, 0, 1);
        dir.normalize();
        // Path heading plus the visitor's own look — this is the 6DOF part: the body follows the
        // route, the head turns independently, which is exactly how the phone is held.
        const heading = Math.atan2(dir.x, dir.z) + this.yaw;
        const look = new THREE.Vector3(Math.sin(heading) * Math.cos(this.pitch), Math.sin(this.pitch), Math.cos(heading) * Math.cos(this.pitch));
        // The path is already the camera track: it is stored at the 1.40 m sternum where the phone
        // hangs. Nothing is added here. Adding an eye height on top is what put the camera a head
        // above the phone and made the panel disagree with the frustum on the stage.
        const eye = new THREE.Vector3(here.x, here.y, here.z);
        this.camera.position.copy(eye);
        this.camera.lookAt(eye.clone().add(look));
        this.camera.aspect = rect.w / rect.h;
        this.camera.updateProjectionMatrix();
        this.#updateShoal(t, eye, new THREE.Vector3(Math.sin(heading), 0, Math.cos(heading)));
        this.#tickEffects(dt);
        /**
         * Rendered to an offscreen target, then blitted into the bezel.
         *
         * The earlier version drew straight into a scissored corner of the main canvas. That works,
         * but it leaves the phone's aspect ratio at the mercy of whatever rectangle the layout
         * happens to give it, and it makes the scene render and the phone render share viewport and
         * scissor state — which is precisely how the main view ended up with a viewport twice the
         * size of its canvas. An offscreen target is a self-contained pass at a known size, and the
         * blit afterwards is a flat quad that cannot disturb the camera it did not touch.
         */
        // Authoring gizmos have no business on a visitor's screen.
        const hidden = [this.stage.gizmos, this.stage.axisGroup, this.stage.pathHandles,
            this.stage.trimBox, this.stage.grid, this.stage.walker,
            this.stage.gizmoHelper];
        const was = hidden.map((o) => o && o.visible);
        for (const o of hidden) {
            if (o)
                o.visible = false;
        }
        this.shoal.visible = true;
        /**
         * Drawn into an offscreen target of our own, and rate-limited.
         *
         * Spark keeps ONE sorted splat accumulator per SparkRenderer and counts every render call
         * as a new frame (`renderer.info.render.frame`), so drawing the scan a second time from
         * this camera re-sorts five million splats for it and leaves the main view showing an
         * ordering computed for a camera pointing somewhere else. That is the flashing.
         *
         * There is no clean fix available in this version. Spark's per-view sort isolation lives on
         * the legacy `OldSparkRenderer`/`OldSparkViewpoint` pair; the current `SparkRenderer` the
         * stage uses exposes no viewpoint API, and a second instance accumulates no splats of its
         * own because the SplatMesh is registered against the first. Measured attempts:
         *
         *   autoUpdate = false for this pass     the two views then contend for one sort   sd 34
         *   a second SparkRenderer               activeSplats 0, main view alternates dark  sd 36
         *   reordering the passes                only changes which view is wrong          sd 35
         *
         * So the panel is throttled instead. At 12 Hz against 60 it perturbs one frame in five
         * rather than every frame, which takes the flashing from constant to occasional. The panel
         * is a placement aid and does not need to be smooth; the view being authored in does.
         */
        const now = performance.now();
        if (now - this.lastDraw >= PANEL_INTERVAL_MS) {
            this.lastDraw = now;
            this.#ensureTarget(rect);
            renderer.setRenderTarget(this.target);
            renderer.clear();
            renderer.render(this.stage.scene, this.camera);
            renderer.setRenderTarget(null);
        }
        this.shoal.visible = false;
        hidden.forEach((o, i) => { if (o)
            o.visible = was[i]; });
        // The blit is deliberately NOT done here. This pass runs before the main view, which clears
        // the canvas — anything painted now would be wiped. The stage calls present() afterwards.
        this.pending = { rect, W, H };
    }
    /** Blit the frame prepared by render() onto the canvas. Called after the main view has drawn. */
    present() {
        if (!this.enabled || !this.pending || !this.target)
            return;
        const { rect, W, H } = this.pending;
        this.#present(rect, W, H);
    }
    /** The offscreen buffer, at the bezel's size in device pixels. */
    #ensureTarget(rect) {
        const dpr = Math.min(devicePixelRatio, 2);
        const w = Math.max(2, Math.round(rect.w * dpr));
        const h = Math.max(2, Math.round(rect.h * dpr));
        if (this.target && this.target.width === w && this.target.height === h)
            return;
        this.target?.dispose();
        // Left in the renderer's working colour space on purpose: three converts to sRGB when the
        // quad is drawn to the canvas, and tagging the target sRGB as well converts twice and
        // washes the whole panel out.
        this.target = new THREE.WebGLRenderTarget(w, h, {
            minFilter: THREE.LinearFilter,
            magFilter: THREE.LinearFilter,
            depthBuffer: true,
        });
    }
    /** Blit the target into the bezel's rectangle, over the frame already drawn. */
    #present(rect, W, H) {
        const renderer = this.stage.renderer;
        if (!this.quad) {
            this.quadScene = new THREE.Scene();
            this.quadCamera = new THREE.OrthographicCamera(-0.5, 0.5, 0.5, -0.5, 0, 1);
            this.quad = new THREE.Mesh(new THREE.PlaneGeometry(1, 1), new THREE.MeshBasicMaterial({ depthTest: false, depthWrite: false, toneMapped: false }));
            this.quad.frustumCulled = false;
            this.quadScene.add(this.quad);
        }
        this.quad.material.map = this.target.texture;
        // setViewport and setScissor take CSS pixels — three.js multiplies by the pixel ratio
        // itself. Applying it here as well puts the viewport off the canvas at double size on
        // every retina display, which is most of them.
        const bottom = H - rect.y - rect.h;
        const autoClear = renderer.autoClear;
        renderer.autoClear = false;
        renderer.setScissorTest(true);
        renderer.setViewport(rect.x, bottom, rect.w, rect.h);
        renderer.setScissor(rect.x, bottom, rect.w, rect.h);
        renderer.render(this.quadScene, this.quadCamera);
        renderer.setScissorTest(false);
        renderer.setViewport(0, 0, W, H);
        renderer.setScissor(0, 0, W, H);
        renderer.autoClear = autoClear;
    }
    dispose() {
        this.target?.dispose();
        this.quad?.geometry.dispose();
        this.quad?.material.dispose();
        this.el?.remove();
    }
    /** Where the screen sits, in CSS pixels relative to the canvas. Null when there is no room. */
    #layout(W, H) {
        if (!W || !H) {
            this.el.style.display = 'none';
            return null;
        }
        const h = Math.min(MAX_HEIGHT, H - CLEAR_TOP - CLEAR_BOTTOM - BEZEL * 2);
        if (h < MIN_HEIGHT) {
            this.el.style.display = 'none';
            return null;
        }
        const w = Math.round(h / ASPECT);
        const x = W - CLEAR_RIGHT - BEZEL - w;
        const y = CLEAR_TOP + BEZEL;
        this.rect = { x, y, w, h };
        const style = this.el.style;
        style.display = '';
        style.left = `${x - BEZEL}px`;
        style.top = `${y - BEZEL}px`;
        style.width = `${w + BEZEL * 2}px`;
        style.height = `${h + BEZEL * 2}px`;
        // Every frame, so only touch the DOM when the text actually changes.
        const label = `${this.s.toFixed(1)} m · ${this.shoalCount} fish`;
        if (label !== this.lastLabel) {
            this.caption.textContent = label;
            this.lastLabel = label;
        }
        return this.rect;
    }
}
//# sourceMappingURL=phoneview.js.map