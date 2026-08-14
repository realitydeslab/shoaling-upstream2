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
export declare const STATURE = 1.7;
export declare const CHEST = 1.4;
/**
 * Where a drawing surface comes from.
 *
 * A seam, and the only thing this port added: three.js runs under Node but `document` does not
 * exist there, so the glow cache and the label geometry would otherwise be reachable only from
 * a browser. In the editor nothing calls this and the behaviour is exactly what it was.
 */
export type CanvasFactory = () => HTMLCanvasElement;
/** Draw onto something other than a document canvas. Called with nothing, restores the default. */
export declare function setCanvasFactory(factory?: CanvasFactory): void;
/**
 * A 1.70 m visitor with the phone at her sternum.
 *
 * Returns the group with an `update(elapsed, speed)` method: the legs and arms swing when she
 * is moving and settle when she stops, which is what makes the difference between walking and
 * standing legible on a scrubber that can be dragged at any rate.
 */
export declare function buildAvatar(colour: THREE.ColorRepresentation, accent: THREE.ColorRepresentation): THREE.Group;
/** A camera-facing text chip. Canvas rather than a font dependency; the editor has no build. */
export declare function makeLabel(text: string, colour: THREE.ColorRepresentation): THREE.Sprite;
export interface CameraGizmoOptions {
    fov?: number;
    aspect?: number;
    depth?: number;
}
/**
 * The phone's view frustum, drawn at the chest.
 *
 * Without this the phone panel is a picture from nowhere: you cannot tell what the camera is
 * pointed at, or that its cone is narrow enough to miss a beat sitting a couple of metres off
 * the path — which on this creek is most of them. Drawn to the near-field distance rather than
 * to infinity, because the useful question is what falls inside the first few metres.
 */
export declare function buildCameraGizmo(colour: THREE.ColorRepresentation, { fov, aspect, depth }?: CameraGizmoOptions): THREE.Group;
export interface GlowOptions {
    size?: number;
    intensity?: number;
}
/**
 * A soft bright point, as an additive sprite.
 *
 * A small sphere is a poor marker over a photographic scan: it is one more small bright object
 * among a million, and it competes rather than reads. A glow has no edge to lose against the
 * foliage and it survives being only a few pixels across, which is what a beat is when the
 * whole reach is framed.
 */
export declare function glowSprite(colour: THREE.ColorRepresentation, { size, intensity }?: GlowOptions): THREE.Sprite;
