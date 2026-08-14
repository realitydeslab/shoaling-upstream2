# Gaussian splat pipeline — measured findings

Measured 2026-08-13/14 on this Mac (Apple Silicon, Chrome) against the three real
Scaniverse exports. Every number below was produced by running the thing, not estimated.

## The SPZ v3 container, decoded

Scaniverse exports SPZ v3. The reference implementation is C++ and there was no
Python/CLI tool to hand, so the layout was derived from the files themselves and
confirmed by an exact byte-count match on all three.

```
header 16 bytes : magic u32 = 0x5053474e ("NGSP" little-endian)
                  version u32 = 3
                  numPoints u32
                  shDegree u8 | fractionalBits u8 | flags u8 | reserved u8
then five contiguous planes (struct-of-arrays, NOT interleaved):
    positions  9 bytes/pt   3 x 24-bit fixed point, divide by 2**fractionalBits
    alphas     1 byte/pt
    colours    3 bytes/pt
    scales     3 bytes/pt
    rotations  4 bytes/pt   <- SPZ v2 used 3; v3 adds one byte per point
    sh        3*shDim/pt    shDim = 0 / 3 / 8 / 15 for degree 0 / 1 / 2 / 3
whole file is gzip-wrapped
```

Total = `20 + 3*shDim` bytes per point: **65 at SH degree 3, 20 at degree 0**.

The `rotations` field being 4 bytes rather than 3 is the one non-obvious detail; assuming
3 leaves a residual of exactly `numPoints` bytes, which is how it was caught.
`tools/spz_decimate.py` asserts the expected size and refuses to run on a mismatch, so a
future format change fails loudly instead of silently producing garbage.

## Source files

| File | Splats | Bytes | SH |
|---|---:|---:|:--:|
| `ucb-strawberry-creek-south.spz` | 4,848,429 | 89,594,659 | 3 |
| `ubc-nitobe-garden-creek.spz` | 4,963,155 | 100,395,367 | 3 |
| `ubc-trees.spz` | 4,794,667 | 93,861,094 | 3 |

## Decimation results

`tools/spz_decimate.py --target 600000 --sh-degree 1`, uniform stride subsample plus SH
truncation (low SH degrees are a prefix of high ones, so truncation is exact, not resampled):

| File | Splats | Bytes | Shrink |
|---|---:|---:|---:|
| `ucb-strawberry-creek-south.proxy.spz` | 600,000 | 10,663,026 | 8.4× |
| `ubc-nitobe-garden-creek.proxy.spz` | 600,000 | 10,821,338 | 9.3× |
| `ubc-trees.proxy.spz` | 600,000 | 10,730,245 | 8.7× |

## Verification in spark.js 2.1.0 / three 0.185.1

Both the full file and its proxy were loaded by `SplatMesh({url})` and every splat centre
walked with `forEachSplat`. Comparing the Strawberry Creek full file against its proxy:

| Measure | Full (4,848,429) | Proxy (600,000) |
|---|---:|---:|
| Parse + upload | **5,732 ms** | **419 ms** |
| X 98% span | 29.97 m | 29.97 m |
| Y 98% span | 13.32 m | 13.29 m |
| Z 98% span | 61.41 m | 61.45 m |
| X median | −9.03 | −9.04 |
| Z median | 16.59 | 16.59 |

Geometry is preserved to the centimetre and load is **13.7× faster**. The proxy is
suitable as the editor's working representation.

## Two things this revealed about the scene itself

**The site is a 61-metre linear reach.** The 98% extent is ~30 m × 13 m × 61 m, with the
long axis in Z. A creek run of that length is exactly the right scale for a walked linear
journey, and it means waypoint spacing should be thought of in the 5–15 m range, not metres.

**There are extreme floater splats.** Min/max sit at roughly ±1200 m on every axis while
99% of the splats are inside a 61 m box. Any code that frames the camera, computes bounds,
or auto-scales must use percentiles, not min/max, or it will zoom out to nothing. This is
the reason the first probe rendered an empty scene.

## Notes for implementation

- `THREE.Box3().setFromObject(splatMesh)` returns an **empty box** — splats are not
  ordinary geometry. Use `forEachSplat` (or cached percentile bounds) instead.
- spark.js is ESM-only; `@sparkjsdev/spark` exposes `SplatMesh`, `SparkRenderer`,
  `PackedSplats`, `PlyReader`, `transcodeSpz`, `getSplatFileType`, and a `PointerControls`
  / `FpsMovement` set that may serve the first-person "user view".
- A WebGL canvas in a background tab does not paint and rAF throttles to ~1 fps. Any
  automated visual check must foreground the tab or it will screenshot black.
- Storing ~90 MB binaries directly in a git repo (as the earlier prototype did, without
  LFS) puts the repo at 178 MB of history for two files. Proxies at ~10 MB are still large
  for git; prefer keeping full-resolution splats out of version control entirely and
  publishing proxies as release/CDN artifacts.

---

# Update: precomputed streaming LOD (`.rad`)

The editor originally showed a 600k decimated proxy, which looked visibly worse than Niantic's
own portal viewer — correctly so, since the portal renders all 4.96M splats at SH degree 3.
The proxy was sized for web delivery, but the editor runs on localhost where bandwidth is free.

Three representations were measured on the UBC garden creek scan (4,963,155 splats):

| Representation | Size | Editor open time | Notes |
|---|---:|---:|---|
| 600k proxy, SH 1 | 10.7 MB | **0.4 s** | visibly degraded; gaps between splats read as haze |
| Full `.spz`, no LOD | 96 MB | **7.2 s** | full fidelity, no level of detail |
| Full `.spz`, `lod: true` | 96 MB | **22.1 s** | tree built in-browser every load |
| **`.rad`, precomputed** | **13 KB manifest** | **0.0 s** | + 106 chunks streamed on demand |

## Why `.rad` wins

Spark ships a Rust tool, `build-lod`, that precomputes the level-of-detail tree offline and
writes a chunked `.rad` asset. Two things follow:

**The editor opens instantly.** `onLoad` fires once the 13 KB manifest is parsed. Chunks are
then fetched only as the view needs them, so nothing waits on a 96 MB download or a 22-second
WebWorker tree build.

**The tree is better.** In-browser generation uses `tiny-lod`; offline we can afford
`bhatt-lod`, which spark's own changelog calls the higher-quality method.

## Building them

```bash
git clone --depth 1 https://github.com/sparkjsdev/spark.git
cd spark && cargo build --manifest-path rust/build-lod/Cargo.toml --release
cd /path/to/ShoalingUpstream
BUILD_LOD=/path/to/spark/rust/target/release/build-lod ./tools/build-rad.sh
```

About three minutes for all three scans. Output:

| Scan | Manifest | Chunks | Total |
|---|---:|---:|---:|
| ubc-nitobe-garden-creek | 16 KB | 106 | 173 MB |
| ubc-trees | 12 KB | 98 | 147 MB |
| ucb-strawberry-creek-south | 16 KB | 98 | 135 MB |

## Do not crop at build time

An earlier version of this pipeline passed `--min-box` / `--max-box` to `build-lod` to strip
floater splats out of the asset. **That was wrong twice over and has been reverted.**

It **damaged the assets** — the cropped `.rad` files rendered incorrectly.

And even had it worked, it put the decision in the wrong place. Which floaters count as noise
is an authoring judgement made by looking at the scan, and baking it into the asset turns a
one-second adjustment into a three-minute rebuild of every scan.

The trim now happens **at runtime in the editor**, via a spark `SplatEdit` carrying an inverted
box SDF with `opacity: 0` and `MULTIPLY` blending — spark's own documented idiom for deleting
splats from a region of space. The box is stored on the journey as `editorFrame.trim` and the
scan file is never modified.

It is an **oriented** box, driven by the standard three.js `TransformControls` gizmo with
move / rotate / scale modes. Axis-aligned was the wrong shape: the creek runs diagonally across
the scan, so an AABB tight enough to drop the floaters also cuts the banks off the ends.

Two details that make the difference between working and silently doing nothing:

- **The SDF must be a scene-graph child of the `SplatEdit`** (`edit.add(sdf)`), not merely
  listed in its `sdfs` array. Its position and scale are an `Object3D` transform, and nothing
  updates `matrixWorld` for an object outside the graph — so an array-only SDF sits at the
  origin at unit size, doing nothing at all.
- **The `SplatEdit` should be a child of the `SplatMesh`** to scope it to that scan. An edit
  with no `SplatMesh` ancestor applies globally to every mesh whose `editable` is true.
- The `SplatMesh` needs `editable: true` at construction.
- Box SDF `scale` is a **half-extent**, not a full size.
- **Disable the trim by setting `opacity` to 1, never by inflating the box.** Growing the SDF
  to a huge scale looks like a tidy way to let everything through, but the gizmo reads its
  transform back into the journey — so the first touch after disabling overwrites the authored
  extent with 100000. Multiplying by 1 is a true no-op and leaves the box intact.

Note that trimming this way sets opacity to zero rather than culling, so the triangle count
does not drop — do not try to verify it that way.

## One trap in the build itself

**`build-lod` takes no output path.** It derives its own names from the input and writes them
beside it — a `<name>-lod.rad` manifest plus `<name>-lod-N.radc` chunks. Passing a destination
makes it treat that path as a second input file and panic. `tools/build-rad.sh` runs it and
then moves the outputs. The manifest references chunks by bare filename, so they must stay in
the same directory as the manifest.

**Enabling LOD breaks `forEachSplat`.** Once the splats live in the level-of-detail structure,
walking them returns nothing — so the editor's bounds measurement silently reported zero and
the camera framed on empty space. Bounds are static data about a scan, so they are now
precomputed by `tools/stamp-bounds.py` into each journey's `editorFrame.bounds`, which also
removes several seconds of per-load work.

## What is not verified

Rendered frame rate with streaming LOD. The browser-automation environment used here keeps
backgrounding the tab, which throttles requestAnimationFrame to about 1 fps and stalls the
pager — chunks are only fetched for views that actually render. The data path is verified
(manifest parses, `paged` is true, chunks fetch and serve correctly, the editor opens in 0.0 s);
the frame rate needs a human with the window in front of them.
