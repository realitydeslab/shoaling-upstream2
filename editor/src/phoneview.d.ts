/**
 * TEMPORARY — delete when editor/src/phoneview.ts lands. See scene.d.ts for why this exists.
 */

import type * as THREE from 'three';
import type { Vec3 } from './types.js';
import type { Stage } from './scene.js';

export declare class PhoneView {
  constructor(stage: Stage);

  enabled: boolean;
  s: number;

  setEnabled(on: boolean): void;
  setS(s: number): void;
  hide(): void;

  setShoalCount(n: number): void;
  get shoalCount(): number;

  spawnEggs(at: Vec3): THREE.Group;
  showHeron(at: Vec3): THREE.Group;
  heronFeeds(count: number): void;
  takeInsect(at: Vec3): THREE.Group;
  clearEffects(): void;
  reset(): void;

  /** Called from the stage's animation loop; the stage binds its own centreline into it. */
  render(pointAtS: (s: number) => Vec3, totalLength: number): void;
  present(): void;
  dispose(): void;
}
