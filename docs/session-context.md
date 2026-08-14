# Session context

Everything a fresh session needs to pick this up. Written 2026-08-14.
Read `docs/devlog.md` alongside this — it holds the standing instructions and the traps.

---

## What this is

**Shoaling Upstream** — a site-specific spatial-audio artwork on a creek reach. The visitor
walks upstream carrying a neck-mounted iPhone, hearing themselves as a shoal of fish. Sound is
the medium; visuals are secondary by design.

- **Lead artist / researcher:** Yangyang Yang (Berkeley coauthor, `yangyangyang@berkeley.edu`)
- **This machine's user:** Botao Hu
- Built from scratch. An earlier prototype at `../shoaling-upstream` and
  `../shoaling-upstream-waypoints` was deliberately **not** carried over.

## The three components

| | |
|---|---|
| **Editor** | Browser, no build step. Places beats, draws the walking path, trims the scan, auditions the sound, simulates the walk on a phone-shaped screen. |
| **Service** | Node. Journey drafts, append-only published revisions, WebSocket control bus. |
| **iOS app** | Unity 6.3 LTS + NSDK 4.1.0 + Apple PHASE. Core logic and tests only so far. |

Run it: `cd service && node src/server.mjs` → `http://localhost:8710/`

---

## Sites

Three Niantic orgs are visible to the signed-in user:

| Org | Site | Status |
|---|---|---|
| Reality Design Lab | `garden` — UBC Nitobe area | **working site.** Splat asset NOT set to production, which blocks device localization. |
| Trout AR | Strawberry Creek South, UC Berkeley | second site; the Berkeley coauthor tests here independently |
| Elan's organization | UBC trees | spare rehearsal ground, no creek |

Site switching is architecture, not a feature: two people, two cities, one journey structure.

Key IDs are in `data/sites/site-registry.json`. **No auth token is stored in this repo** —
NSDK uses expiring OAuth-style tokens from the portal's Credentials section.

## The UBC garden creek, as measured

Profiled with `tools/creek-profile.py` along the artist's own trim box axis (yaw 46.6°):

```
along −13.5 → −8.5   bed 3.38 → 1.74 m    the reach ABOVE the falls
along  −8.5 → −6.5   bed 1.74 → −1.09 m   THE WATERFALL — 3 m drop in 2 horizontal metres
along  −6.5 → +5.5   bed −1.09 → −2.01 m  the creek run
along  +5.5 → +16.5  bed rising 2.7 m     the far bank out of the valley
```

**Upstream is the −along direction.** An earlier plan had this backwards because it followed
the fraction of bright desaturated splats, which peaks at *both* ends — the far end is thin
sunlit canopy at the edge of the capture, not water. **Bed geometry is the reliable signal.**

The journey (`tools/replan-garden.mjs`, 13 path points, 18.8 m):

| # | beat | s | interaction |
|---|---|---|---|
| 1 | The tree | 4.3 m | proximity |
| 2 | The gravel bed | 5.9 m | crouch |
| 3 | Water striders | 11.0 m | catch (lunge and stop) |
| 4 | The heron | 15.1 m | give — shoal thins |
| 5 | **The falls** | 16.1 m | **lift** over |
| 6 | **Above the falls** | 16.3 m | **crouch — spawn** |

The last two are the artist's instruction: lift over the falls, spawn above them.

---

## Where configuration lives

One file per site: `data/journeys/<slug>/draft.json`

| What | JSON path |
|---|---|
| Walking path (chest-height camera track) | `site.centreline` |
| Points of interest | `beats[]` |
| Trim box (oriented) | `editorFrame.trim` |
| Measured scan extent | `editorFrame.bounds` |
| Calibration flag | `editorFrame.calibrated` |

Published snapshots: `data/journeys/<slug>/revisions/rNNNNNN.json`, append-only.

Each saves through its own endpoint so a background write never pushes out a half-finished
edit elsewhere:

```
PUT  /api/sites/:slug/editor-frame   trim box
PUT  /api/sites/:slug/site           walking path (recomputes every beat's s)
PUT  /api/sites/:slug/draft          whole document
POST /api/sites/:slug/publish        immutable revision
GET  /api/scans                      which scan representations exist, best first
GET  /api/audio                      packaged clip catalogue
```

**Known limitation:** two editor tabs on one site both auto-save and overwrite each other.

---

## Editor, as it stands

**Toolbar** is layer toggles — click to show/hide, state persisted in `localStorage`:
`Frame | Beats · Path · Trim · Scan · Phone | Full/Fast`

Turning Path or Trim on also opens its edit panel, because you only show scaffolding to work
on it.

**Everything is edited with the standard three.js `TransformControls` gizmo.** Clicking the 3D
scene only ever *selects* — never moves anything. Move / Rotate / Scale, or **G** / **R** /
**T**. `ViewHelper` sits top-right for orientation.

**Path vs Place are separate.** The path is the simulated walking route; the places are the
points of interest. Moving the path never moves a beat — it only recomputes each beat's `s`,
which is a derived value used for ordering and the scrubber.

**The armed-zones scrubber** shows which beats are live at any position along the reach, and
which one wins. No existing locative authoring tool has this, and with six zones overlapping
on a short creek an author cannot reason about it from a map view.

**Audition** runs through Resonance Audio as a stand-in for PHASE. Verified: approaching a
beat crossfades it from its *far* recording to its *intimate* one — distance carried by
content, not gain.

**Phone view** is a second camera riding the path at chest height with drag-to-look, rendered
in a scissored viewport, showing the splat plus sketch visuals (shoal point cloud, egg spawn,
heron). It is a placement aid, not the on-device visuals.

---

## Scan pipeline

Source `.spz` files are ~5M splats, 90–100 MB each, SPZ v3 (65 bytes/point at SH degree 3).

- `tools/spz_decimate.py` → 600k proxy, 8.4× smaller, geometry preserved to the centimetre
- `tools/spz_bounds.py` / `tools/stamp-bounds.py` → percentile extents into `editorFrame.bounds`
- `tools/build-rad.sh` → **precomputed streaming LOD**, the one the editor actually uses:
  a 13 KB manifest plus ~100 chunks, opening in 0.0 s against 22.1 s for building the tree
  in-browser. Needs spark's Rust `build-lod`:
  ```
  git clone --depth 1 https://github.com/sparkjsdev/spark.git
  cd spark && cargo build --manifest-path rust/build-lod/Cargo.toml --release
  BUILD_LOD=<path>/rust/target/release/build-lod ./tools/build-rad.sh
  ```

Scan data is **gitignored** — working data, not source.

## Audio

`tools/package-audio.sh` → mono 48 kHz, three distance layers per source
(`--far` / `--mid` / `--intimate`).

11 of 13 source files came from the shared Drive folder. The two long ambience WAVs
(`Tree creek waterplants 1/2`, 11 and 14 MB) exceed the 10 MB Drive download limit and must be
fetched by hand into `data/audio/source/`.

**The distance layers are derived, not recorded** — a low-passed take standing in for a distant
one. Recording each source at three real distances is the largest piece of audio work left.

---

## Unity app

`app/` — Unity **6000.3.21f1 (6.3 LTS)**, NSDK 4.1.0, AR Foundation 6.4.2. **22 EditMode tests
passing.**

```
Runtime/Journey/    JourneyModel, Centreline, JourneyProgression (the trigger state machine)
Runtime/Gestures/   GestureDetector — crouch / lift / lunge
Tests/EditMode/     the synthetic-walk suite
```

Run them:
```
/Applications/Unity/6000.3.21f1/Unity.app/Contents/MacOS/Unity \
  -batchmode -nographics -runTests -projectPath app \
  -testPlatform EditMode -testResults logs/editmode-results.xml -logFile logs/unity-tests.log
```

**Not yet built:** VPS localization wiring, the WebSocket client on device, PHASE audio.

---

## Open items

- **Promote the UBC garden splat asset to production** in the Niantic portal. Blocks the first
  field test. Only Botao can do it.
- **The trim box ceiling is too low** for a chest-height path at the top of the falls — the
  last path point is pinned at y = 1.22. Raise the box ~1 m with the Scale gizmo and re-run
  `node tools/replan-garden.mjs`.
- **Two ambience WAVs missing** (see above).
- **Bear vs heron:** settled as heron and water strider, because those are the recordings that
  exist and they are ecologically right for Strawberry Creek. The bear had one strong argument
  — salmon carcasses give riparian trees 22–24% of their foliar nitrogen, so the shoal you give
  away literally becomes the tree you shelter under — but no recording exists.
- **Berkeley coauthor needs to install and run without either author present** → TestFlight and
  her device on the provisioning profile.
- **Research still outstanding:** Unity's XR Simulation capability. Everything else landed and
  is written up in `docs/design-findings.md` and `docs/audio-findings.md`.
