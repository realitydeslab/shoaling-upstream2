# Strawberry Creek South — the journey, and how it was arrived at

Written 2026-08-14. Everything geometric here is measured from
`data/splats/ucb-strawberry-creek-south.spz` (4,848,429 splats) and is reproducible by
re-running `tools/replan-berkeley.mjs`. Nothing on this site had been touched by hand before
this: the draft still carried the seeded 3-point straight line at a flat `y = −0.50`, six beats
on the retired `barrier` / `headwater` vocabulary, and an axis-aligned trim box covering the
whole capture.

The single most important finding is negative, so it goes first.

## There is no waterfall on this reach

The garden ends by lifting yourself over a three-metre step and spawning in the still water
above it. That step is real at UBC — the profile drops 1.74 → −1.09 m in two horizontal metres.
**Nothing of the kind exists here.**

Traced along the creek's own centre (method below), the bed rises **+2.21 m over 65.2 m** — a
**3.4 % mean gradient**, monotonic apart from one pool. The steepest rise over any 2 m window of
channel is **+0.44 m**; over any 4 m, **+0.58 m**. Both sit inside ordinary riffle rather than at
a lip. Run against a correctly oriented trim box, `creek-profile.py`'s own
steepest-watery-rise heuristic — the one that found the garden's falls — nominates **+0.30 m
over 2.1 m**. That is a creek running over gravel.

This was not assumed either way. The design brief for the piece wants a barrier, and the
temptation was to find one; the arithmetic does not support it, and forcing the garden's ending
onto this creek would have put the load-bearing moment of the work in a place with nothing there.

## Where the creek runs

**It is not along the scan's long axis.** The capture is 30 × 13 × 61 m with its long axis in Z,
and the creek runs in an L across it:

| | |
|---|---|
| enters at | world (+7, −6) — the south-east corner |
| runs north-west to | (−11, +9) |
| then almost due north to | (−10, +48) |
| traced length | 65.2 m |
| bed at the two ends | −2.23 m → +0.08 m |

It was traced as a **thalweg**: for each 1 m slice of Z, the X of the lowest 10th-percentile
ground in a 2 m cell, then median-smoothed to reject scan holes. On a top-down height map the
channel is unmistakable — a continuous low band with high ground on both sides.

It is a genuinely incised channel, not a swale. Cross-sections through the northern half:

```
z = +20   x = −21  −19  −17  −15  −13  −11   −9
bed(5%)     2.8  1.7  1.0 −0.1 −0.7 −0.4  0.5
```

About 3 m of flat bottom, a west bank climbing 3.5 m in eight, an east bank climbing 1.2 m in
three. That shape holds from z ≈ +12 to the top of the capture.

## Upstream is north, toward +Z

The bed rises **+2.21 m** walking from (+7, −6) to (−10, +48). Two independent corroborations:

1. **The VPS mesh.** `docs/site-geometry.md` measures **2.27 m of rise** across the site's 26
   production mesh nodes. Same 2.2 m, different asset, computed by someone else from an ENU
   frame rather than from splats.
2. **Hydrology.** Strawberry Creek flows west; upstream is east and uphill. The two agree.

### The trap from the garden, and how it was avoided

The garden's first plan followed the fraction of bright, desaturated splats, which peaked at
*both* ends — the far end being thin sunlit canopy at the capture edge, not water. The
correction was blunt: *"you totally reverse the path."*

Here the route is laid on **bed geometry only**. Brightness is used afterwards, and in the
*opposite* sense: bright broken white water reconstructs densely; smooth still water does not.
Along this reach the bed-level brightness runs 111–137 in the riffles and drops to 96–104 in the
one still section, so a naive brightness follower would have walked the visitor away from the
deepest water rather than toward the edge of the capture. It is not trusted for direction at all.

## The trim box, and how it converged

`creek-profile.py` walks the trim box's **local +X**. The seeded box had identity rotation and
its long axis in Z, so local +X was world +X — the *short* axis. It duly reported:

```
box yaw 0.0°, long axis 31.0 m
ground rises −4.06 m from s=0 to s=30.3
steepest watery rise: +0.68 m over 1.4 m at world (8.34, −0.83, 18.52)  <- candidate waterfall
```

That is the valley **cross-section**, and the nominated waterfall is the far east bank. The
bootstrap took three steps:

1. **Profile the whole capture directly**, on a 2 m and then a 1 m grid, rather than through the
   box — because the box was useless and the box is what needed fixing. This produced the
   thalweg.
2. **Fit the box to the thalweg** as a minimum-width oriented bounding box, sweeping yaw at
   0.05° and taking the angle that minimises the cross-extent. Result **yaw 72.35°**, along span
   50.9 m, across span 11.8 m. Compare: the endpoint chord gives 14.5 m of across-span, and PCA
   gives 20.3 m. The minimum-width fit is materially better than either.
3. **Re-profile through the fitted box.** It now reports `box yaw 72.4°, long axis 54.0 m`, finds
   the last (south-east) end lower and therefore downstream, and cannot find a waterfall. That
   is the agreement that closed the loop.

The box as written:

| | |
|---|---|
| centre | (−8.049, 2.0, 18.961) |
| rotation | `[0, 0.590254, 0, 0.807218]` — pure Y, 72.35° |
| half-extent | 27.0 along, 7.0 up, 9.2 across |

**Local +X points downstream**, at world (+0.3032, 0, −0.9529). That is deliberate:
`creek-profile.py` recovers yaw with `asin`, which cannot represent an angle past ±90°, and the
upstream-pointing choice is −106.9°. Pointing +X downstream keeps the yaw at 72.35° and, as a
side effect, matches the garden's convention that upstream is at −along.

### The box is wider than it should be, and that is the bend's fault

±9.2 m of cross-extent is a lot for a channel 3 m wide. **The width is set by the 45° bend, not
by the creek**: the thalweg alone needs ±5.9 m before a single metre of bank is included, because
a straight box cannot hug a creek that turns. Everything planned sits inside it, but a good deal
of dry bank sits inside it too.

This is the thing most worth an hour of the artist's hands. Two ways out, neither taken here:
drag the box by eye to favour the northern straight (which is 40 of the 65 m), or split the
site into two boxes if the schema ever grows to allow it.

Vertically the box spans −5.0 … +9.0, which is the full stamped bounds: **it trims nothing in Y.**
Which floaters and which canopy count as noise is a judgement made by eye, and the devlog is
explicit that baking that decision into a tool is the wrong place for it.

## The ending this site actually supports

Four independent measurements agree that there is a **dark, still, enclosed reach at z ≈ 27–36**
(s 43–52 along the channel, world x ≈ −13 to −12):

| measured at bed level | either side | inside |
|---|---|---|
| splat density | 3000–5300 /m² | **127–555 /m²** |
| luminance | 111–137 | **96–104** |
| bright-desaturated fraction | 11–38 % | **1–4 %** |
| apparent bed roughness | 0.12–0.33 m | **0.46–0.68 m** |

Sparse, dark and geometrically noisy all at once is what a smooth specular surface does to
photogrammetry: a riffle is opaque and textured and reconstructs well, still deep water gives
reflections, transparency and garbage. Two more signals point the same way — the west wall
reaches +4.6 m within 7 m of the channel over that stretch, and the canopy closes over it at
+7.5–9 m.

It is **flat water, not a step.** The dry gravel margin 1.5 m to the west reads a steady −0.2 m
right through, matching the reaches above (−0.15) and below (−0.22). The pool is deeper, not
lower.

So the ending is: **you arrive in the dark still water, you get up out of it, and you spawn on
the gravel above.**

### Why that is the right barrier for *this* creek

The obstacle here is a dark passage, not a height — which happens to be the historically correct
barrier for Strawberry Creek. Per `docs/design-findings.md` §7, from UC Berkeley's own 1987
management plan: culverts in this watershed are *"totally dark passages that anadromous fish
avoid"* and *"complete barriers to upstream migration"*; Memorial Stadium put a section of the
**south fork** — this fork — into a culvert; and *"pools that provided rest areas were
obliterated."* The steelhead run ended in the 1920s. A 2025 UCB study attributes the failure of
re-population largely to the want of **high-flow refuges** — which is precisely what the one
deep still pocket on this reach is.

### The lift is measured, not invented

From the wetted channel at x ≈ −12.1 up onto the east gravel margin at x ≈ −10.9:

| z | channel bed | east margin | step |
|---:|---:|---:|---:|
| 30 | −0.53 | −0.28 | 0.25 |
| 32 | −0.46 | −0.22 | 0.24 |
| 34 | −1.36 † | −0.19 | — |
| 36 | −0.46 | −0.17 | 0.29 |

† water-surface noise; the reconstruction fails there, which is the point.

So **0.24–0.29 m of visible step**, and the true figure is larger because the channel reading is
taken *through* the water surface. Above the margin the bank benches again by another 0.5–0.7 m
(the east lip measures 0.47–0.72 m over 0.75 m horizontally through z = 26–32). Somewhere
between those two is the ~40 cm the piece asks for, and — per `design-findings.md` §8 — it lands
on an obvious object rather than on flat ground, which is what makes the gesture read as ordinary
in public.

Walking upstream (north), the east margin is on the visitor's right. The prompt says so.

## The path

**18 points, 60.1 m, climbing 1.98 m**, from world (4.73, −0.78, −3.68) to (−10.54, 1.20, 45.24).

It is the **camera track at 1.40 m chest height** — the phone hangs on a neck mount, so the
walking path *is* the camera track. The authored heights are the measured 5th-percentile bed at
each point and `CHEST` is added when the route is built. Beats are authored at bed height
instead; the stored beat `y` sits 1.22–1.62 m below the nearest path node, which is that 1.40 m
plus local bed variation.

**There is no bank path in this scan.** Sweeping lateral offsets from −9 to +9 m at every 2 m of
z, the only continuous flat band anywhere from z = +12 to the top of the capture is the channel
bottom itself, at offset −1.5 … +1.5 m. Outside that the ground climbs immediately on both
sides. So the route follows the bed with about **±0.8 m of lateral wander** — much tighter than
the garden's, because there is only ~3 m of flat to wander in.

Both ends stop short of the capture edges, where reconstruction thins below ~1000 splats/m² and
any height read there is guesswork.

## The beats

Six, on the closed `proximity / crouch / catch / give / lift` vocabulary, every clip checked
against `data/audio/catalogue.json` **and** against the packaged files on disk — 25 clip
references, 25 resolving to real `.mp3`s.

| # | id | s | world (x, y, z) | interaction | clip | why there |
|---|---|---:|---|---|---|---|
| 1 | `tree` | 2.0 | (3.4, −2.06, −2.1) | proximity | `tree-creek-waterplants-1` | the wide slow basin at the bottom of the capture; canopy to +8 m overhead |
| 2 | `redd` | 13.3 | (−6.2, −1.42, 2.1) | crouch | `chapter-1-new-life` | the steepest riffle on the reach (11 % over 4 m), bright and broken over gravel |
| 3 | `strider` | 24.3 | (−11.1, −0.78, 11.7) | catch | `strider` | bare open trough — almost nothing above +1.5 m for 12 m either way, so sky is on the water |
| 4 | `heron` | 35.6 | (−13.8, −0.44, 21.7) | give | `heron` | the widest, flattest, best-reconstructed water on the reach (5288 splats/m², 0–3.6 % gradient) |
| 5 | `darkwater` | 46.6 | (−11.0, −0.37, 32.7) | lift | `chapter-4-returning-home` | the dark still reach, on the east lip you climb out onto |
| 6 | `spawn` | 57.8 | (−10.2, −0.26, 42.8) | crouch | `chapter-5-rebirth` | the gravel riffle above it — the last reach the scan resolves |

Completion one-shots: `lay-egg`, `eat-strider`, `jump`, `lay-egg`. `heron` gives 12 fish from a
starting shoal of 40, floor 6.

## Spacing, and the number that nearly went wrong

The garden's six beats ended up 1.6–4 m apart against an audible reach of `exitRadiusM × 3.5` =
10.64 m, so **all six were audible everywhere** — eighteen loops at once, which the artist heard
as *"many reverb sound"*. **The radii were never the problem; the 19 m reach was.**

Here the beats are **11.0–11.3 m apart along the path.** But that is not the number the audible
field cares about — the field is a sphere, and this route bends, so:

| adjacent pair | s-gap | straight line |
|---|---:|---:|
| tree → redd | 11.3 | **10.49** |
| redd → strider | 11.0 | 10.86 |
| strider → heron | 11.3 | **10.33** |
| heron → darkwater | 11.0 | 11.35 |
| darkwater → spawn | 11.2 | **10.16** |

Sizing the radii against `s` would have quietly reproduced the garden's bug on a route long
enough to avoid it. Against the closest pair in *space*:

| | |
|---|---|
| enterRadiusM | **1.75** |
| exitRadiusM | **2.80** (1.6× enter — the hysteresis ratio `design-findings.md` §4 depends on) |
| audible reach | **9.80 m** |
| closest pair | 10.16 m — **0.36 m of margin** |

Measured by sampling the route every 25 cm:

| | garden | Berkeley |
|---|---|---|
| beats audible standing **at** a beat | 6 of 6 | **1 of 6** |
| beats audible anywhere on the route | 6 of 6 | 1 for 24 % of it, 2 for 76 %, never 3 |

Two-audible-in-transition is the 2–4 figure `docs/audio-findings.md` §3 designs its spectral
zoning around, rather than the six the garden got. The editor's own CHECKS panel now shows **no
overlapping-exit-band warnings at all** on Berkeley, against one per adjacent pair on the garden.

**These radii are slightly tighter than the garden's 1.9 / 3.04, and that is a real cost.**
1.75 m of enter band is tight against Niantic's 4 m median position error. It was chosen over
moving the beats because there is no room left to move them — the first sits 2.0 m from the start
of the route and the last 2.3 m from the end. The operator-carried controller exists precisely
to cover a gate that does not fire. **If the audio law changes and reach stops being 3.5× exit,
this constant is the thing to revisit — not the layout.**

## Verification

- `node tools/replan-berkeley.mjs` — **idempotent** over repeated runs (byte-identical
  `draft.json`).
- `POST /api/sites/ucb-strawberry-creek-south/validate` — **0 errors**, 1 warning:
  `editorFrame.calibrated is false`. That warning is correct and cannot be cleared from a desk;
  only someone standing on the site can calibrate the splat against the VPS anchor.
- All 24 path and beat points, and the ambient source, verified inside the trim box
  independently of the tool's own guard. The tool refuses to write if any point escapes.
- 25 of 25 clip references resolve to packaged `.mp3` files.
- `npm test` — **186 pass, 0 fail**.
- Loaded in the editor and switched to the Berkeley site: 60.1 m route, six beats at
  2.0 / 13.3 / 24.3 / 35.6 / 46.6 / 57.8 m, armed-zones scrubber showing six separated bands.
- Written entirely through `PUT /api/sites/:slug/editor-frame` and `PUT /api/sites/:slug/draft`.
  Not published — revisions are immutable and that is the artist's call.

### One bug this shook out

The tool originally wrote the trim box through `/editor-frame` and then the whole document
through `/draft`. The `/draft` body was built from the copy of the draft read at script start,
which still carried the *seeded* box — so the second write silently undid the first, and
`creek-profile.py` kept reporting `yaw 0.0°`. Both bodies now carry the same box.

### One guard added

`replan-berkeley.mjs` refuses to run if the stored trim box is neither the seed nor the one it
fits, on the grounds that anything else means a person has dragged the gizmo. The devlog's first
entry is a hand-placed trim box destroyed by a tool that regenerated it, and that box was not
recoverable.

## What the scan cannot answer — for Yangyang, standing there

These are ordered by how much they would change the plan.

1. **Can you actually walk the creek bed?** The route follows it, because it is the only
   continuous flat surface in the scan. Is it walkable, or is there water in it? How deep, at the
   season and hour the piece runs? If it is not walkable the whole route has to move onto a bank
   the scan does not show.
2. **Is there a path the scan missed?** The capture is one-sided in places — the west bank at
   z = 30–34 has almost no splats, which usually means the scanner never got there. A path could
   be sitting just outside.
3. **What is the dark reach at z ≈ 27–36, world x ≈ −12?** The measurements say deep still water
   under a closed canopy. It could equally be shadow under a footbridge, or a culvert mouth. If
   it is a culvert, the ending gets stronger and more literal; if it is just shade, the "the
   shoal will not go up there" prompt needs rewriting. **This is the single most valuable thing
   she can check**, because the whole ending rests on it. A photograph looking upstream from
   z ≈ 27 would settle it.
4. **Is the ~0.4 m lift at the head of that reach a thing a person can put a foot on?** The
   geometry says a gravel margin then a bank bench. In reality it may be undercut, wet, or
   planted.
5. **Does the creek continue below the capture's south-east corner?** The first beat sits 2.0 m
   into a 60.1 m route, so there is almost no approach and no room for the 60 seconds of
   instructed listening `design-findings.md` §9.3 argues for. If she can stand 10 m further
   downstream and still localize, the opening improves considerably.
6. **Is there water in it at all in August?** The piece is about a run that no longer happens; a
   dry bed changes what the work means, and that is the artist's decision, not a bug.
7. **Where can six people stand for 45–90 seconds each without being in anybody's way?**
   `design-findings.md` §1 puts this piece at ~95 % standing still. The scan shows the ground,
   not the footfall.
8. **Is the dark reach the loudest place on site, or the quietest?** The whole audio design is
   composed *with* the real creek through non-isolating headphones. A pool is quiet; a riffle is
   not. `audio-findings.md` §6 asks for a site survey at each anchor point at the hour the piece
   runs, and beats 2 and 5 sit at opposite extremes of that.
