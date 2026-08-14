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

## Still open

- **UBC garden splat asset is not "Set to production"** in the Niantic portal. Device
  localization there is blocked until it is. Only Botao can clear this.
- **The walking path at UBC has not been placed against the real creek.** The seeded path is a
  straight line along the scan's long axis. The creek runs from the downstream end to a
  waterfall; the Path tool and its Auto button exist to fix this, but it needs someone who can
  recognise the waterfall in the scan.
- **Two ambience WAVs are missing** — `Tree creek waterplants 1/2` exceed the 10 MB Drive
  download limit and need fetching by hand into `data/audio/source/`, then
  `tools/package-audio.sh`.
- **The three distance layers are derived, not recorded.** `package-audio.sh` low-passes a
  single take to stand in for a distant one. Recording each source at three real distances is
  the largest piece of audio work left.
- **Not yet built:** VPS localization wiring in Unity, the WebSocket client on device, and
  Apple PHASE audio. The editor's Resonance Audio is an authoring approximation only.
