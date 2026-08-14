/**
 * Scale figures: the walking visitor, and the phone she carries.
 *
 * The point of these is calibration, not decoration. Everything else on the stage is abstract
 * — rings, beams, a line along the creek — and abstract geometry over a photographic scan
 * gives no sense of size at all. A 1.70 m human standing in it does, immediately, and it is
 * the only thing on screen that answers "is that beat reachable from the bank".
 *
 * Two heights matter and they are not the same:
 *
 *   1.70 m   the visitor, head to heel — the scale reference
 *   1.40 m   the sternum, where the phone hangs on its neck mount — the CAMERA
 *
 * The walking path is stored at 1.40 m, so the group origin here is the chest, the ground is
 * at −1.40, and the head tops out at +0.30. Nothing in this file adds a height to the path;
 * that mistake is what put three different eye-height constants in three different files.
 *
 * The figure is drawn as a flat silhouette rather than a shaded body. Over a dense riparian
 * scan a shaded figure disappears into the foliage, and a silhouette in one colour reads at
 * any distance and from any angle — the architectural scale-figure convention, for the same
 * reason architects adopted it.
 */

import * as THREE from 'three';

export const STATURE = 1.70;   // head to heel
export const CHEST = 1.40;     // the neck mount, and therefore the camera

/** Local Y of each landmark, measured from the chest at 0. */
const Y = {
  headTop: STATURE - CHEST,        // +0.30
  headMid: STATURE - CHEST - 0.12, // +0.18
  neck: 0.05,
  shoulder: 0.0,                   // the phone rides here
  pelvis: -0.48,
  knee: -0.93,
  foot: -CHEST,                    // -1.40
};

const flat = (colour, opacity = 1) => new THREE.MeshBasicMaterial({
  color: colour, transparent: opacity < 1, opacity, depthTest: false, side: THREE.DoubleSide,
});

/**
 * A 1.70 m visitor with the phone at her sternum.
 *
 * Returns the group with an `update(elapsed, speed)` method: the legs and arms swing when she
 * is moving and settle when she stops, which is what makes the difference between walking and
 * standing legible on a scrubber that can be dragged at any rate.
 */
export function buildAvatar(colour, accent) {
  const g = new THREE.Group();
  g.renderOrder = 12;

  const body = flat(colour, 0.92);
  const limb = flat(colour, 0.82);

  const part = (geo, mat, y, ry = 0) => {
    const m = new THREE.Mesh(geo, mat);
    m.position.y = y;
    m.rotation.y = ry;
    m.renderOrder = 12;
    g.add(m);
    return m;
  };

  // Head, neck, torso. The torso tapers — shoulders wider than waist — because a plain box
  // reads as a crate and the whole value of this figure is being recognised as a person
  // instantly, at a glance, from across the scan.
  part(new THREE.SphereGeometry(0.115, 20, 14), body, Y.headMid);
  part(new THREE.CylinderGeometry(0.045, 0.055, 0.13, 10), body, Y.neck + 0.02);
  part(new THREE.CylinderGeometry(0.155, 0.115, Y.shoulder - Y.pelvis, 14), body,
       (Y.shoulder + Y.pelvis) / 2);
  part(new THREE.CylinderGeometry(0.115, 0.125, 0.16, 12), body, Y.pelvis - 0.05);

  // Limbs hang from pivots so they can swing about the hip and shoulder rather than sliding.
  const limbs = [];
  const hang = (x, top, bottom, radius) => {
    const pivot = new THREE.Group();
    pivot.position.set(x, top, 0);
    const len = top - bottom;
    const m = new THREE.Mesh(new THREE.CapsuleGeometry(radius, len - radius * 2, 4, 8), limb);
    m.position.y = -len / 2;
    m.renderOrder = 12;
    pivot.add(m);
    g.add(pivot);
    limbs.push(pivot);
    return pivot;
  };

  const legL = hang(-0.075, Y.pelvis, Y.foot, 0.055);
  const legR = hang(+0.075, Y.pelvis, Y.foot, 0.055);
  const armL = hang(-0.185, Y.shoulder - 0.02, Y.pelvis + 0.02, 0.04);
  const armR = hang(+0.185, Y.shoulder - 0.02, Y.pelvis + 0.02, 0.04);

  // The phone on its neck strap, at the chest, in the accent colour — this is the camera, and
  // it should be the one thing on the figure you can pick out.
  const strap = new THREE.Mesh(
    new THREE.TorusGeometry(0.10, 0.008, 6, 20), flat(accent, 0.75));
  strap.position.y = Y.neck - 0.02;
  strap.rotation.x = Math.PI / 2;
  strap.scale.set(1, 0.62, 1);
  strap.renderOrder = 13;
  g.add(strap);

  const phone = new THREE.Mesh(new THREE.BoxGeometry(0.075, 0.15, 0.012), flat(accent, 0.95));
  phone.position.set(0, Y.shoulder - 0.04, 0.14);
  phone.renderOrder = 13;
  g.add(phone);

  // A stance ring on the ground: where she is standing, independent of how tall she is.
  const ring = new THREE.Mesh(
    new THREE.RingGeometry(0.30, 0.36, 40), flat(accent, 0.55));
  ring.rotation.x = -Math.PI / 2;
  ring.position.y = Y.foot + 0.01;
  ring.renderOrder = 11;
  g.add(ring);

  g.add(buildStatureDimension(colour));

  g.userData.update = (elapsed, speed) => {
    // Swing amplitude follows speed, so a parked walker stands still instead of marching on
    // the spot. Arms counter-swing to the legs, as they do.
    const amp = Math.min(0.5, speed * 1.6);
    const phase = elapsed * 4.4;
    legL.rotation.x = Math.sin(phase) * amp;
    legR.rotation.x = -Math.sin(phase) * amp;
    armL.rotation.x = -Math.sin(phase) * amp * 0.7;
    armR.rotation.x = Math.sin(phase) * amp * 0.7;
  };
  g.userData.limbs = limbs;
  return g;
}

/** A dimension line beside the figure, labelled, so the 1.70 m is stated and not just implied. */
function buildStatureDimension(colour) {
  const g = new THREE.Group();
  const x = 0.42;
  const mat = new THREE.LineBasicMaterial({
    color: colour, transparent: true, opacity: 0.5, depthTest: false });

  const v = (pts) => {
    const l = new THREE.Line(new THREE.BufferGeometry().setFromPoints(pts), mat);
    l.renderOrder = 12;
    g.add(l);
  };
  v([new THREE.Vector3(x, Y.foot, 0), new THREE.Vector3(x, Y.headTop, 0)]);
  for (const y of [Y.foot, Y.headTop]) {
    v([new THREE.Vector3(x - 0.07, y, 0), new THREE.Vector3(x + 0.07, y, 0)]);
  }

  const label = makeLabel('1.70 m', colour);
  label.position.set(x + 0.30, (Y.foot + Y.headTop) / 2, 0);
  label.scale.set(0.52, 0.26, 1);
  g.add(label);
  return g;
}

/** A camera-facing text chip. Canvas rather than a font dependency; the editor has no build. */
export function makeLabel(text, colour) {
  const pad = 16, size = 64;
  const c = document.createElement('canvas');
  const ctx = c.getContext('2d');
  ctx.font = `500 ${size}px ui-sans-serif, -apple-system, "Helvetica Neue", sans-serif`;
  c.width = Math.ceil(ctx.measureText(text).width) + pad * 2;
  c.height = size + pad * 2;

  const ctx2 = c.getContext('2d');
  ctx2.font = `500 ${size}px ui-sans-serif, -apple-system, "Helvetica Neue", sans-serif`;
  ctx2.textAlign = 'center';
  ctx2.textBaseline = 'middle';
  ctx2.fillStyle = '#0b0f0e';
  ctx2.globalAlpha = 0.72;
  roundRect(ctx2, 0, 0, c.width, c.height, 18);
  ctx2.fill();
  ctx2.globalAlpha = 1;
  ctx2.fillStyle = `#${new THREE.Color(colour).getHexString()}`;
  ctx2.fillText(text, c.width / 2, c.height / 2 + 2);

  const tex = new THREE.CanvasTexture(c);
  tex.colorSpace = THREE.SRGBColorSpace;
  tex.minFilter = THREE.LinearFilter;
  // flipY stays at its default of true. Setting it false renders every label upside down —
  // checked directly on the running stage rather than reasoned about, because the two
  // conventions are easy to talk yourself into either way round. The re-upload below is the
  // part that matters: colorSpace and minFilter are changed after construction, and without
  // marking the texture dirty again the first upload can land before they are applied.
  tex.needsUpdate = true;
  const sprite = new THREE.Sprite(new THREE.SpriteMaterial({
    map: tex, transparent: true, depthTest: false }));
  sprite.renderOrder = 14;
  sprite.userData.aspect = c.width / c.height;
  return sprite;
}

function roundRect(ctx, x, y, w, h, r) {
  ctx.beginPath();
  ctx.moveTo(x + r, y);
  ctx.arcTo(x + w, y, x + w, y + h, r);
  ctx.arcTo(x + w, y + h, x, y + h, r);
  ctx.arcTo(x, y + h, x, y, r);
  ctx.arcTo(x, y, x + w, y, r);
  ctx.closePath();
}

/**
 * The phone's view frustum, drawn at the chest.
 *
 * Without this the phone panel is a picture from nowhere: you cannot tell what the camera is
 * pointed at, or that its cone is narrow enough to miss a beat sitting a couple of metres off
 * the path — which on this creek is most of them. Drawn to the near-field distance rather than
 * to infinity, because the useful question is what falls inside the first few metres.
 */
export function buildCameraGizmo(colour, { fov = 58, aspect = 9 / 19.5, depth = 3.2 } = {}) {
  const g = new THREE.Group();
  g.renderOrder = 13;

  const h = Math.tan((fov * Math.PI / 180) / 2) * depth;
  const w = h * aspect;
  /**
   * Forward is +Z here, not the -Z a three.js camera looks down.
   *
   * The gizmo hangs off the walker group, and the walker is turned with
   * atan2(dx, dz) — the rotation that maps +Z onto the direction of travel. Building the
   * cone down -Z to match camera convention pointed it back the way she came.
   */
  const corners = [
    new THREE.Vector3(-w, -h, depth), new THREE.Vector3(w, -h, depth),
    new THREE.Vector3(w, h, depth), new THREE.Vector3(-w, h, depth),
  ];
  const apex = new THREE.Vector3(0, 0, 0);

  const pts = [];
  for (const c of corners) { pts.push(apex.clone(), c.clone()); }
  for (let i = 0; i < 4; i += 1) { pts.push(corners[i].clone(), corners[(i + 1) % 4].clone()); }

  const lines = new THREE.LineSegments(
    new THREE.BufferGeometry().setFromPoints(pts),
    new THREE.LineBasicMaterial({ color: colour, transparent: true, opacity: 0.75, depthTest: false })
  );
  lines.renderOrder = 13;
  g.add(lines);

  // A translucent screen plane at the far end, so the frustum reads as a view and not as a
  // wireframe pyramid pointing at nothing.
  const plane = new THREE.Mesh(
    new THREE.PlaneGeometry(w * 2, h * 2),
    new THREE.MeshBasicMaterial({ color: colour, transparent: true, opacity: 0.10,
      side: THREE.DoubleSide, depthTest: false })
  );
  plane.position.z = depth;
  plane.renderOrder = 12;
  g.add(plane);

  // Up tick, so roll is visible.
  const up = new THREE.Line(
    new THREE.BufferGeometry().setFromPoints([
      new THREE.Vector3(0, h, depth), new THREE.Vector3(0, h * 1.28, depth)]),
    new THREE.LineBasicMaterial({ color: colour, transparent: true, opacity: 0.75, depthTest: false })
  );
  up.renderOrder = 13;
  g.add(up);

  return g;
}

/**
 * A soft bright point, as an additive sprite.
 *
 * A small sphere is a poor marker over a photographic scan: it is one more small bright object
 * among a million, and it competes rather than reads. A glow has no edge to lose against the
 * foliage and it survives being only a few pixels across, which is what a beat is when the
 * whole reach is framed.
 */
export function glowSprite(colour, { size = 1, intensity = 1 } = {}) {
  const key = `${colour}`;
  glowSprite.cache ??= new Map();
  let tex = glowSprite.cache.get(key);
  if (!tex) {
    const D = 128;
    const c = document.createElement('canvas');
    c.width = c.height = D;
    const ctx = c.getContext('2d');
    const col = new THREE.Color(colour);
    const rgb = `${Math.round(col.r * 255)}, ${Math.round(col.g * 255)}, ${Math.round(col.b * 255)}`;
    const g = ctx.createRadialGradient(D / 2, D / 2, 0, D / 2, D / 2, D / 2);
    // A hot near-white core falling away fast, then a wide soft skirt. A plain linear ramp
    // reads as a fuzzy blob rather than as a light.
    g.addColorStop(0.00, 'rgba(255, 255, 255, 1)');
    g.addColorStop(0.14, `rgba(${rgb}, 0.95)`);
    g.addColorStop(0.38, `rgba(${rgb}, 0.34)`);
    g.addColorStop(1.00, `rgba(${rgb}, 0)`);
    ctx.fillStyle = g;
    ctx.fillRect(0, 0, D, D);
    tex = new THREE.CanvasTexture(c);
    tex.colorSpace = THREE.SRGBColorSpace;
    tex.needsUpdate = true;
    // Cached per colour and reused, so whoever disposes a gizmo must leave this alone.
    tex.userData.shared = true;
    glowSprite.cache.set(key, tex);
  }

  const sprite = new THREE.Sprite(new THREE.SpriteMaterial({
    map: tex,
    transparent: true,
    depthTest: false,
    depthWrite: false,
    // Additive so overlapping markers build up instead of punching holes in each other.
    blending: THREE.AdditiveBlending,
    opacity: intensity,
  }));
  sprite.scale.setScalar(size);
  sprite.renderOrder = 11;
  return sprite;
}
