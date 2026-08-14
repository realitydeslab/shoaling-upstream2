# Devlog

Standing instructions and the decisions behind them. Read this before changing anything —
several entries exist because doing the obvious thing destroyed work.

---

## Standing instructions from the artist

These came directly from Botao during the build. They are not up for re-litigation.

**Build from scratch here.** An earlier prototype exists at `../shoaling-upstream` and
`../shoaling-upstream-waypoints`. Do not depend on it. Facts verified there (SDK versions,
site IDs, pitfalls) were re-verified independently and live in `docs/`; none of the code was
carried over.

**Apple PHASE only.** No Wwise, no FMOD. They are useful as *vocabulary* — Events vs Actions,
Switch vs State vs RTPC, snapshots — for things we reimplement in C#. Not as dependencies.

**SPZ, rendered with spark.js.** Not a mesh export, not a converted format.

**The barrier is a lift, not a jump.** About 40 cm — lifting yourself up onto something and
staying there. This turned out to be better than a jump on every axis: it is a sustained
plateau rather than a ballistic arc so it is easier to detect, it is far more dignified in a
public park, it is safer on a wet bank, and because it can be *held*, the audio can hold
"out of the water" for as long as the visitor stays up instead of firing a one-shot.

**The headphones are not noise-cancelling.** So the real creek is unavoidably in the mix at
full level the whole time. "Compose with the creek, do not synthesise water" stops being a
stylistic preference and becomes a constraint.

**Path and Place are separate things.**
- **Path** = the route the simulated walker follows. Editing it never moves a beat.
- **Place** = the points of interest, positioned freely in space.
A beat's `s` is a *derived* value — its projection onto the path — used for ordering and the
scrubber. Moving the path recomputes every `s`; it does not move anything.

**The controller is for the operator.** Botao carries it while accompanying the visitor. The
experience runs automatically by default; the controller is the safety net for when an
automatic trigger does not fire. So it leads with *why* — localization state, distance,
what has played — not with buttons. Two independent projects (Notre-Dame's *Whispers*,
Hidden Florence) arrived at the same feature after meeting GPS reality.

**UBC garden creek is the working site; Berkeley must be one switch away.** The Berkeley
coauthor (Yangyang Yang) tests there independently. Site switching is architecture, not a
feature.

**Use standard three.js gizmos.** `TransformControls` for the trim box, the path points and
the beats. `ViewHelper` for orientation, **top right** — the default bottom-right collides
with the stage toolbar and the panels.

**Trim at runtime, never in the LOD build.** See below.

**Gizmos must be strong.** Thick lines and bright points. WebGL ignores
`LineBasicMaterial.linewidth` on every platform that matters, so anything that needs to be seen
over a photographic scan uses `Line2` fat lines and additive glow sprites.

**The path is at chest height — 1.40 m.** The phone hangs on a neck mount, so the walking path
IS the camera track. Nothing adds an eye height to it anywhere. Three files once each held their
own idea of that offset and disagreed.

**The visitor is 1.70 m.** Drawn as a scale figure on the stage, with the phone at her sternum.
Two heights, and they are not the same one.

**Ctrl+Z exists and must keep working.** Every authored value on this stage is set by dragging,
and a gizmo pulled against a ground plane at a glancing angle can throw a point tens of metres
in one movement.

---

## Things that destroyed work, and the guards now in place

### Re-seeding wiped the authored trim box

`tools/seed-journeys.mjs --force` used to overwrite the whole draft. Running it during
testing destroyed a trim box that had been positioned by hand. It was not recoverable: there
were no backups, and the published revision predated the trim feature.

**Now:** `--force` regenerates beats and audio but **carries over** the trim box, a
hand-drawn walking path (more than the 3 seeded points), measured bounds, and the calibration
flag. It also writes a timestamped `.bak.json` before touching anything. `--reset-all` is the
explicit opt-in to discard authored config.

**Rule:** anything a person positioned by hand is not reproducible from a seed. Treat it as
data, not as output.

### Cropping the scan at build time broke the assets

An early version passed `--min-box` / `--max-box` to spark's `build-lod` to strip floater
splats out of the `.rad`. The cropped assets rendered incorrectly, and even had they worked it
was the wrong place for the decision: which floaters count as noise is an authoring judgement
made by eye, and baking it in turns a one-second adjustment into a three-minute rebuild of
every scan.

**Now:** trimming is a runtime `SplatEdit` with an inverted box SDF at `opacity: 0` and
`MULTIPLY` blending — spark's own documented idiom for deleting splats from a region of space.
The scan file is never modified, so a bad box costs nothing but a re-drag.

### Disabling the trim by inflating the box ate the authored extent

Growing the SDF to 1e5 to "let everything through" looked tidy. But the gizmo reads its
transform back into the journey, so the first touch after disabling overwrote the authored box
with a 100000-metre extent.

**Now:** disabling sets `opacity` to 1 — a true no-op — and the SDF always carries the real
box. Caught because the readout showed `size 200000.00`.

---

### The phone panel makes the scan flash, and this is not yet solved

The phone-screen panel draws the same splat scene from a second camera. Doing so makes the main
view flash. Measured on the running stage, as brightness variance over a fixed patch that
contains splats: **sd 0.00 with the scan hidden, sd 0.00 with the panel closed, sd ~34 with it
open.** So it is the splats, and it is the panel that provokes them.

The cause is in spark: a `SparkRenderer` keeps ONE sorted accumulator, and counts a frame as
`renderer.info.render.frame`, which increments on **every render call** rather than once per
animation frame. The second pass therefore reads as a new frame and re-sorts five million splats
for the phone camera; the main view then draws with an ordering computed for a camera pointing
somewhere else.

Four approaches were tried and measured. None worked:

| approach | result |
|---|---|
| `spark.autoUpdate = false` around the phone pass | sd 34 — the two views contend for one sort |
| a second `SparkRenderer` with its own target | sd 36, main view alternating dark: no splats are registered against it, so `activeSplats` is 0 and it blanks the shared instance count |
| rendering the phone pass first, blitting after | only changes which view is wrong |
| throttling the panel to 12 Hz | sd 37 — the disturbance persists between draws |

`preUpdate` is already `true` by default, so setting it changes nothing. Spark's per-view sort
isolation (`SparkViewpoint`, `spark.newViewpoint()`) exists only on the **legacy**
`OldSparkRenderer`; the current `SparkRenderer` the stage uses has no viewpoint API.

**The remaining option is to stop drawing the scan in the panel** and keep only the sketch
visuals. That makes the stage steady and costs the scan preview in the bezel.

## Non-obvious technical facts

Each of these cost a debugging cycle. They are also in `docs/splat-pipeline-findings.md`.

**`scene.fog` silently breaks spark.js.** Its splat shader does not implement three.js fog.
The scan loads, reports the right splat count, spark reports 600k active splats, and nothing
whatsoever is drawn.

**A `SplatMesh` needs `frustumCulled = false`.** It has no real geometry, so three.js computes
an empty bounding sphere and culls the entire scan before spark sees it.

**`Box3.setFromObject` returns an empty box on a splat mesh**, and every scan carries floaters
400–1200 m out. Any bounds or framing code must use percentiles. Bounds are now precomputed by
`tools/stamp-bounds.py` into `editorFrame.bounds`.

**Enabling LOD breaks `forEachSplat`** — the splats move into the level-of-detail structure and
walking them returns nothing, so bounds silently come back as zero.

**A `SplatEditSdf` must be a scene-graph child of its `SplatEdit`** (`edit.add(sdf)`), not
merely listed in `sdfs`. Spark collects them with `traverseVisible`, and nothing updates
`matrixWorld` for an object outside the graph — so an array-only SDF sits at the origin at unit
size doing nothing. The `SplatEdit` should in turn be a child of the `SplatMesh`, or it applies
to every editable mesh in the scene. The mesh needs `editable: true`. Box SDF `scale` is a
**half-extent**.

**`build-lod` takes no output path.** It names its own outputs beside the input; passing a
destination makes it read that as a second input file and panic.

**Unity 6.5 is unusable with NSDK 4.1.0's dependency set.** It turned `TreeView` and
`GetInstanceID` into hard errors and Burst 1.8.17, Input System 1.11.2 and 1.14.2 all fail
against it. The project is on **6000.3.21f1 (6.3 LTS)**, supported to December 2027. NSDK only
requires Unity 2021.3.

**A `CanvasTexture` on a Sprite needs `flipY` left at its default**, and needs
`needsUpdate = true` after any post-construction change to `colorSpace` or `minFilter`. Setting
`flipY = false` renders every label upside down. Checked on the running stage, not reasoned
about — the two conventions are easy to talk yourself into either way round.

**A view frustum attached to the walker points along +Z, not -Z.** Three.js cameras look down
-Z, but the walker group is turned with `atan2(dx, dz)`, which maps +Z onto the direction of
travel. Building the cone to camera convention points it back the way she came.

**Path handles are attached to the transform gizmo by `pathHandles.children[index]`.** Anything
added per path point must be a CHILD of its handle, never a sibling, or every index shifts.

**Glow textures are cached and shared per colour.** A gizmo rebuild that disposes `material.map`
indiscriminately blanks every marker from then on. Shared textures are marked
`texture.userData.shared`.

**`e.target.matches()` throws for events dispatched at `window`**, which silently swallows every
keyboard shortcut after it in the handler.

**Unity 6.x moved `PlaybackEngines` outside `Unity.app`.** Checking
`Unity.app/Contents/PlaybackEngines/iOSSupport` gives a false negative on every installed
version here.

---

## Where configuration lives

One file per site: `data/journeys/<slug>/draft.json`.

| What | Where |
|---|---|
| Walking path (simulation route) | `site.centreline` |
| Points of interest | `beats[]` |
| Trim box | `editorFrame.trim` |
| Measured scan extent | `editorFrame.bounds` |
| Calibration flag | `editorFrame.calibrated` |

Published snapshots are append-only at `data/journeys/<slug>/revisions/rNNNNNN.json`.

Each is saved through its own endpoint so one cannot clobber another, and so a background save
never pushes out a half-finished edit elsewhere in the document:

- `PUT /api/sites/:slug/editor-frame` — trim box
- `PUT /api/sites/:slug/site` — walking path (recomputes every beat's `s`)
- `PUT /api/sites/:slug/draft` — the whole document
- `POST /api/sites/:slug/publish` — immutable revision

**Known limitation:** two editor tabs on the same site will both auto-save and overwrite each
other.

---

## Tests

`npm test` — 122 and counting, Node's built-in runner, no dependencies.

```
test/geometry.test.mjs        centreline maths, run against BOTH implementations
test/journey-store.test.mjs   the store, where authored work can be lost
test/journey-schema.test.mjs  the validator, including what it does not enforce
test/audible-field.test.mjs   the half-life the editor draws
test/audio-catalogue.test.mjs every clip resolves to real audio
test/api.e2e.test.mjs         real server, real port
test/control-bus.e2e.test.mjs real sockets, operator and device
test/browser.e2e.test.mjs     Playwright; skips cleanly when absent
```

The service takes `PORT=0` and `JOURNEY_DIR` so a suite never touches the artist's journeys.

**The projection maths exists twice** — `service/src/journey-schema.mjs` and
`editor/js/geom.js` — and every geometry case runs against both. If they drift, the scrubber
shows a beat arming where the device will never fire it, and nothing reveals that until someone
is standing in a creek.

## Still open

- **UBC garden splat asset is not "Set to production"** in the Niantic portal. Device
  localization there is blocked until it is. Only Botao can clear this.
- **The walking path at UBC has not been placed against the real creek.** The seeded path is a
  straight line along the scan's long axis. The creek runs from the downstream end to a
  waterfall; the Path tool and its Auto button exist to fix this, but it needs someone who can
  recognise the waterfall in the scan.
- ~~Two ambience WAVs are missing~~ **Done.** Both are local and packaged; "The tree" had no
  audio at all before, since all three of its layers pointed at a clip with no packaged file.
  Audio stays gitignored, so the Berkeley coauthor obtains it from the Drive folder, not here.
- **The three distance layers are derived, not recorded.** `package-audio.sh` low-passes a
  single take to stand in for a distant one. Recording each source at three real distances is
  the largest piece of audio work left.
- **Not yet built:** VPS localization wiring in Unity, the WebSocket client on device, and
  Apple PHASE audio. The editor's Resonance Audio is an authoring approximation only.
