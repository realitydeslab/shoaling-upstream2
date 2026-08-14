/**
 * The scale figure, and the numbers it is measured against.
 *
 * These are the things on the stage that are supposed to be true rather than merely to look
 * right: a 1.70 m visitor whose feet touch the ground, a phone at 1.40 m where the neck mount
 * puts it, a frustum pointing the way she walks, and a glow texture that is shared rather than
 * rebuilt. Every one of them has been got wrong at least once, and none of them announces the
 * mistake — an upside-down label or a backwards frustum just looks like a rendering quirk.
 *
 * Everything checked here is in front of the paint. The canvas work itself (what the label
 * actually reads, what the gradient looks like) is left to the browser suite, because it is
 * pixels and this is Node; the drawing surface is swapped for a stub so the geometry around it
 * can be reached at all.
 */

import test from 'node:test';
import assert from 'node:assert/strict';
import { registerHooks } from 'node:module';

// three.js is a dependency of the SERVICE, which is the only package here with a node_modules
// of its own; the browser reaches it through the import map in editor/index.html, not through
// Node resolution. Node walking up from editor/src never passes service/, so a bare 'three'
// cannot be resolved and this whole module would be untestable. Map it, in-process, once.
const THREE_URL = new URL('../service/node_modules/three/build/three.module.js', import.meta.url).href;
registerHooks({
  resolve(specifier, context, next) {
    return specifier === 'three' ? { url: THREE_URL, shortCircuit: true } : next(specifier, context);
  },
});

const THREE = await import('three');
const {
  STATURE, CHEST, buildAvatar, buildCameraGizmo, makeLabel, glowSprite, setCanvasFactory,
} = await import('../editor/src/figures.ts');

/**
 * Just enough canvas to hand three.js.
 *
 * Nothing below asserts on anything drawn into it. It exists so the figures have a surface,
 * and `measureText` returns something proportional to the text so the label's aspect ratio is
 * a real number rather than NaN.
 */
function stubCanvas(): HTMLCanvasElement {
  const ctx = {
    font: '', textAlign: '', textBaseline: '', fillStyle: '', globalAlpha: 1,
    measureText: (text: string) => ({ width: text.length * 32 }),
    fillText() {}, fillRect() {}, fill() {},
    beginPath() {}, moveTo() {}, arcTo() {}, closePath() {},
    createRadialGradient: () => ({ addColorStop() {} }),
  };
  return { width: 0, height: 0, getContext: () => ctx } as unknown as HTMLCanvasElement;
}

setCanvasFactory(stubCanvas);

const WATER = 0x6da7ad;
const ACCENT = 0xd4707f;

test('the visitor and the phone are two different heights', () => {
  // Three files once each held their own idea of this offset and disagreed. The stature is the
  // scale reference; the chest is where the camera actually is.
  assert.equal(STATURE, 1.70);
  assert.equal(CHEST, 1.40);
  assert.notEqual(STATURE, CHEST);
});

test('the head clears the chest by the difference between the two', () => {
  // 1.70 − 1.40 does not land on 0.30 in binary floating point, so the tolerance is about the
  // arithmetic and not about the figure.
  assert.ok(Math.abs((STATURE - CHEST) - 0.30) < 1e-9, `got ${STATURE - CHEST}`);
});

test('the figure stands on the ground at -1.40 and tops out at +0.30', () => {
  // The group origin is the sternum, because the walking path is stored at sternum height and
  // nothing adds an eye height to it. So the ground is below the origin, not at it.
  const avatar = buildAvatar(ACCENT, 0xd19a45);
  const box = new THREE.Box3().setFromObject(avatar);
  assert.ok(Math.abs(box.min.y - -CHEST) < 1e-6, `feet at ${box.min.y}, expected ${-CHEST}`);
  assert.ok(Math.abs(box.max.y - (STATURE - CHEST)) < 1e-6,
    `head at ${box.max.y}, expected ${STATURE - CHEST}`);
});

test('the walker swings her limbs when moving and settles when parked', () => {
  const avatar = buildAvatar(ACCENT, 0xd19a45);
  const update = avatar.userData.update as (elapsed: number, speed: number) => void;
  const legs = avatar.userData.limbs as { rotation: { x: number } }[];

  update(0.4, 1.4);
  assert.ok(legs.some((l) => Math.abs(l.rotation.x) > 0.01), 'a walker at speed swings');

  // Amplitude follows speed rather than time, so a scrubber parked mid-drag shows someone
  // standing still instead of marching on the spot.
  update(0.4, 0);
  assert.ok(legs.every((l) => l.rotation.x === 0), 'a parked walker is still');
});

test('the view frustum points along +Z, the way the walker faces', () => {
  // Three.js cameras look down -Z, but the walker group is turned with atan2(dx, dz), which
  // maps +Z onto the direction of travel. Building this to camera convention pointed the cone
  // back the way she came, and it read as a rendering bug rather than as a sign error.
  const depth = 3.2;
  const gizmo = buildCameraGizmo(0xd19a45, { depth });

  const plane = gizmo.children.find((c) => c instanceof THREE.Mesh);
  assert.ok(plane, 'the frustum carries a screen plane at its far end');
  assert.equal(plane.position.z, depth);

  const lines = gizmo.children.find((c) => c instanceof THREE.LineSegments);
  assert.ok(lines, 'the frustum is drawn as line segments');
  const z = lines.geometry.getAttribute('position').array;
  for (let i = 2; i < z.length; i += 3) {
    assert.ok(z[i]! >= 0, `a frustum vertex sits behind the walker at z=${z[i]}`);
  }
});

test('the glow texture is cached per colour and marked shared', () => {
  // A gizmo rebuild that disposes material.map indiscriminately blanks every marker from then
  // on, because the texture it disposed belonged to all of them. The flag is the warning.
  const a = glowSprite(WATER, { size: 1.5 });
  const b = glowSprite(WATER, { size: 3.4 });
  assert.equal(a.material.map, b.material.map, 'one colour, one texture');
  assert.equal(a.material.map?.userData.shared, true);

  const other = glowSprite(ACCENT);
  assert.notEqual(other.material.map, a.material.map, 'a different colour gets its own');
});

test('glow sprites differ in scale and opacity without differing in texture', () => {
  const small = glowSprite(WATER, { size: 0.85, intensity: 0.55 });
  const large = glowSprite(WATER, { size: 3.4, intensity: 1 });
  assert.equal(small.scale.x, 0.85);
  assert.equal(large.scale.x, 3.4);
  assert.equal(small.material.opacity, 0.55);
  assert.equal(small.material.map, large.material.map);
});

test('a label leaves flipY alone and re-uploads after its colour space changes', () => {
  // Setting flipY false renders every label upside down. It was checked on the running stage
  // rather than reasoned about, because the two conventions are easy to talk yourself into
  // either way round; this is here so nobody re-reasons it.
  const label = makeLabel('1.70 m', ACCENT);
  const tex = label.material.map;
  assert.ok(tex);
  assert.equal(tex.flipY, true);
  assert.equal(tex.colorSpace, THREE.SRGBColorSpace);
  // colorSpace and minFilter are set after construction, so without marking it dirty again the
  // first upload can land before they apply. `needsUpdate` is write-only in three — it bumps
  // `version`, and the bump is the only thing observable from here.
  assert.ok(tex.version > 0, 'the texture was never marked for re-upload');
});

test('a label records the aspect the caller needs to scale it by', () => {
  // scene.js sizes labels as `h * userData.aspect, h`, so a missing or NaN aspect collapses
  // every beat title to a sliver.
  const label = makeLabel('1.70 m', ACCENT);
  const aspect = label.userData.aspect as number;
  assert.ok(Number.isFinite(aspect) && aspect > 1, `got ${aspect}`);
});
