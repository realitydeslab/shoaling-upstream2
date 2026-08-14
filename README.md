# Shoaling Upstream

A site-specific spatial-audio work on a 55-metre reach of Strawberry Creek, UC Berkeley,
in which the visitor moves upstream as a shoal of fish. Niantic VPS for position, Apple
PHASE for sound, a browser editor for authoring, and a live controller between them.

**Status: editor and service running; Unity app core built and tested. Not yet on a device.**

Working today, on this machine:

- **Editor** at `http://localhost:8710/` — streams the full scan through spark.js with a
  precomputed LOD tree, opening in well under a second. God and User cameras, click-the-scan placement, beat inspector, live validation, and the armed-zones
  scrubber. Edit → save → publish → read back is verified end to end.
- **Controller** at `http://localhost:8710/control` — the operator surface, leading with
  localization state rather than buttons.
- **Service** — journey drafts, append-only published revisions, and a WebSocket control bus
  that holds authoritative state and schedules commands rather than firing them.
- **Unity app** on 6.3 LTS with NSDK 4.1.0 resolving cleanly. Journey model, centreline
  projection, the trigger state machine, and gesture detection — **22 EditMode tests passing**.

Not yet built: VPS localization wiring, the WebSocket client in Unity, and Apple PHASE audio.

## What is here

```
app/                          Unity 6.3 LTS project (NSDK 4.1.0, AR Foundation 6.4.2)
  Assets/ShoalingUpstream/
    Runtime/Journey/          manifest model, centreline, trigger state machine
    Runtime/Gestures/         crouch / lift / lunge detection
    Tests/EditMode/           22 tests, run headless
editor/                       browser editor and operator controller (plain ESM, no build step)
service/                      journey store, validation, WebSocket control bus
data/
  sites/site-registry.json    inventory of every Niantic scan, site and asset, with real IDs
  splats/*.spz                the three full-resolution Scaniverse exports (~90 MB each)
  splats/proxy/*.proxy.spz    600k-splat editor proxies (~10 MB each)
  splats/rad/                 precomputed streaming LOD, built by tools/build-rad.sh
docs/
  nsdk-api-notes.md           NSDK 4.1.0 API, read from package source rather than docs
  site-geometry.md            what is actually localizable, and how long the walk can be
  splat-pipeline-findings.md  the SPZ v3 container decoded, and decimation measurements
  journey-schema-draft.md     draft of the contract between editor, service and app
  design-findings.md          trigger craft, authoring prior art, embodiment, site history
  audio-findings.md           Apple PHASE's real API surface, and the sound design it implies
tools/
  spz_analyse.py              locate water and derive a crop box from a scan
  package-audio.sh            source recordings -> mono 48 kHz far/mid/intimate layers
  spz_decimate.py             SPZ v3 reader/decimator
  spz_bounds.py               robust percentile extent of a scan
  stamp-bounds.py             write measured extents into the journey drafts
  build-rad.sh                precompute streaming LOD assets (needs spark's build-lod)
                              — deliberately does NOT crop; trimming is a runtime editor tool
  seed-journeys.ts            generate starting drafts from measured splat geometry
```

The full-resolution `.spz` files are large. They are working data, not source — keep them
out of version control and treat the proxies as build artefacts.

## The six things worth knowing before reading anything else

**The piece is dwell-dominated, not transit-dominated.** Six beats 8–12 m apart is about forty
seconds of walking; a 15–30 minute work in this footprint is ~95% standing still. Every pacing rule
in the walking-tour literature was derived for transit walks and does not transfer. Each beat must
hold attention for 45–90 seconds. See `docs/design-findings.md` §1.

**GPS cannot separate the beats, so VPS is a precondition rather than a choice.** Under canopy
expect 3–5 m error with a tail to 30–100 m; there is no reliable consumer technology between ~2 m
and ~50 m, and 8–12 m sits in the middle of that gap. Latitude and longitude must never enter the
trigger path. See `docs/design-findings.md` §2.

**The site is 55 m, not 200 m.** The VPS-localizable footprint at Strawberry Creek South is
54.5 × 45.4 m with 2.27 m of rise west to east. The S1–S4 scan sequence traces 194 m and
further scans sit 700 m east, but only the 55 m reach is mapped. Six beats at 8–12 m spacing
fit; a longer route needs new scans promoted to production. See `docs/site-geometry.md`.

**Upstream is uphill, and that is usable.** Because the reach rises, progress upstream can be
read from anchor-relative altitude alone — no path polyline, no reliance on heading.

**The editor streams the full scan, it does not decimate it.** A `.rad` asset with a
precomputed LOD tree opens from a 13 KB manifest and fetches chunks as the view needs them —
0.0 s to open, against 22.1 s for building the tree in-browser and a visibly worse image from
the 600k proxy. Build them with `tools/build-rad.sh`. See `docs/splat-pipeline-findings.md`.

**NSDK can discover sites at runtime.** `SitesClient.RequestSiteAssetsByLocationAsync` answers
"which of my sites am I standing in", and `AssetInfo.VpsData.AnchorPayload` hands over the
anchor payload. Multi-site switching needs no hardcoded table. See `docs/nsdk-api-notes.md`.

## Niantic account context

Three organisations are visible to the signed-in user (Botao Hu):

| Org | Site | Where | Use |
|---|---|---|---|
| Reality Design Lab | garden | UBC, Nitobe area | **working site** — best creek scan; **splat asset not yet set to production, which blocks device localization** |
| Trout AR | Strawberry Creek South | UC Berkeley | **second site** — tested independently by the Berkeley coauthor |
| Elan's organization | UBC trees | UBC | spare rehearsal ground, no creek |

Site switching is therefore architecture, not a feature: two people in two cities run the
same journey structure against different coordinates. Consequences: the manifest is per-site
from the first commit; the Berkeley coauthor must be able to install and run without either
author present, so build distribution (TestFlight, and her device on the provisioning
profile) is part of the plan; and the UBC garden splat asset must be promoted to production
before phase 02 can pass.

Authentication is OAuth-style access and refresh tokens that expire, managed through the
portal's Credentials section (Service accounts and Developer tokens). A field session needs a
freshly minted token, so the build must accept one without a source edit. **No token is stored
in this repository.**

## Toolchain, verified on this machine

The project builds on **Unity 6000.3.21f1** (6.3 LTS) — see the section below for why, not 6.5.
Its iOS build support lives at `/Applications/Unity/6000.3.21f1/PlaybackEngines/iOSSupport`;
note that Unity 6.x moved `PlaybackEngines` outside `Unity.app`, so checking the conventional
path inside `Unity.app/Contents/` gives a false negative on every installed version here.

Valid Unity licence, Xcode 26.6, two Apple Development signing identities, iPhone 17 Pro and
16 Pro known to the machine. NSDK 4.1.0 resolves from
`https://github.com/nianticspatial/nsdk-library-upm.git#4.1.0-26051913` and declares
AR Foundation **6.4.2**. Editor stack is spark.js 2.1.0 with three 0.185.1.

## Relationship to earlier work

A previous prototype exists at `../shoaling-upstream` and `../shoaling-upstream-waypoints`.
This project is a deliberate rebuild from scratch and does not depend on either. Facts
verified there — which SDK versions work, the site and anchor identifiers, the pitfalls —
have been re-verified independently and carried into `docs/`; none of the code has been.

Lead artist and researcher: Yangyang Yang. Earlier prototype and scans: Botao Hu.


## Running it

```bash
cd service && npm install && npm start      # editor on http://localhost:8710/
node tools/seed-journeys.ts                # first run only, writes data/journeys/
```

Unity tests, headless:

```bash
/Applications/Unity/6000.3.21f1/Unity.app/Contents/MacOS/Unity \
  -batchmode -nographics -runTests -projectPath app \
  -testPlatform EditMode -testResults logs/editmode-results.xml \
  -logFile logs/unity-tests.log
```

## Why Unity 6.3 LTS and not 6.5

The project started on 6000.5.7f1 and moved. Unity 6.5 turned several `TreeView` and
`GetInstanceID` APIs into hard errors, and the package ecosystem has not caught up: Burst
1.8.17 (which NSDK 4.1.0 pins), Input System 1.11.2, and Input System 1.14.2 all fail to
compile against it. NSDK only requires Unity 2021.3, so 6.3 LTS — supported to December 2027,
with iOS build support already installed here — is both safer and unblocked. Everything
resolves cleanly there.

## Three traps that cost a debugging cycle each

Worth knowing before touching the editor's 3D code:

1. **`scene.fog` silently breaks spark.js.** Its splat shader does not implement three.js fog.
   The scan loads, reports the right splat count, spark reports 600k active splats — and
   nothing at all is drawn.
2. **A `SplatMesh` must have `frustumCulled = false`.** It has no real geometry, so three.js
   computes an empty bounding sphere and culls the entire scan before spark ever sees it.
3. **`Box3.setFromObject` returns an empty box on a splat mesh**, and every scan carries
   floaters 400–1200 m out. Any bounds, framing or auto-scale code must use percentiles.
4. **Enabling LOD breaks `forEachSplat`** — the splats move into the level-of-detail structure
   and walking them returns nothing, so bounds silently come back as zero. Precompute them with
   `tools/stamp-bounds.py` instead.
5. **`build-lod` takes no output path.** It names its own outputs beside the input; passing a
   destination makes it read that path as a second input and panic.


## Why Resonance Audio for the desk-side audition

Plain Web Audio `PannerNode` HRTF is a poor stand-in for PHASE. Resonance Audio is the only
maintained web renderer whose model actually lines up:

| Apple PHASE | Resonance Audio |
|---|---|
| spatial mixer, HRTF | ambisonic encode + binaural decode (3rd order) |
| source directivity | `setDirectivityPattern(alpha, sharpness)` |
| `rolloffFactor`, cull distance | `setRolloff` / `setMinDistance` / `setMaxDistance` |
| direct / early-reflection / late-reverb sends | room model with per-surface materials |
| **one scene-wide reverb preset** | **one room, scene-wide** |

That last row is the useful one: PHASE gives a single global reverb, so the editor inherits the
same limitation rather than flattering us with per-source spaces we will not have on device.

It is still an approximation — not PHASE's HRTF set, not its geometric spreading, and no
AirPods head tracking. Levels set at a desk will be wrong at the creek, which is already
making water noise of its own. The UI says so rather than pretending otherwise.

Resonance Audio is Apache-2.0 and archived upstream. That is acceptable for an authoring
approximation; it is explicitly not on the shipping audio path.

## Audio assets

`tools/package-audio.sh` turns the source recordings into what the journey references: mono
48 kHz, and **three distance layers per source** (`--far`, `--mid`, `--intimate`), because
distance is carried by content rather than gain.

The layers it generates are *derived* — a low-passed, flattened version standing in for a
genuinely distant take. They exist so the crossfade can be heard and tuned. Recording each
source at three real distances is a production task and the largest piece of audio work left.

Eleven of the thirteen source files came from the shared Drive folder; the two long ambience
WAVs (11 MB and 14 MB) exceed the 10 MB download limit and need fetching by hand into
`data/audio/source/`. The editor reports missing clips rather than failing quietly.


## The trim box

An oriented box, dragged with three.js `TransformControls`. Move / Rotate / Scale, or **G** /
**R** / **T** while the panel is open. It works in local space, so once rotated, scaling
stretches along the creek rather than along world axes.

It is stored on the journey as `editorFrame.trim` and **saved on its own**, via
`PUT /api/sites/:slug/editor-frame`, whenever a drag ends. That is deliberately not a full
draft save: the box is a view setting the author adjusts by eye, and it should survive a
reload whether or not they pressed Save — but committing the whole draft in the background
would also push out whatever half-finished beat edit happened to be open, and would be
rejected outright if that edit did not yet validate. Patching one subtree keeps the two apart.

The scan file is never modified, so a bad box costs nothing but a re-drag.

**Known limitation:** two editor tabs open on the same site will both auto-save the trim and
overwrite each other. Fine for one author at a desk; worth knowing before anyone opens a
second window.
