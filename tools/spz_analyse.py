#!/usr/bin/env python3
"""Find the water and the real extent of a scan.

Two jobs, both needed before a creek journey can be routed through a capture:

1. **Where is the water.** Falling and running water renders as bright, desaturated splats.
   Everything else in a riparian scan is green foliage or dark wet rock, so a
   luminance-high / saturation-low filter isolates the creek surprisingly cleanly, and the
   waterfall shows up as the brightest dense cluster.

2. **Where does the real content stop.** Every Scaniverse export carries floaters hundreds of
   metres out. A crop box has to be tight enough to remove them and loose enough to keep the
   banks.

Usage:
    python3 spz_analyse.py data/splats/ubc-nitobe-garden-creek.spz
    python3 spz_analyse.py FILE.spz --cells 24 --water-top 8
"""

from __future__ import annotations

import argparse
import gzip
import json
import struct
import sys
from pathlib import Path

MAGIC = 0x5053474E
SH_DIM = {0: 0, 1: 3, 2: 8, 3: 15}


def load(path: Path):
    raw = gzip.open(path, "rb").read()
    magic, version, count, sh, frac, flags, _ = struct.unpack("<III4B", raw[:16])
    if magic != MAGIC:
        raise SystemExit(f"{path.name}: not an SPZ file")

    widths = {"positions": 9, "alphas": 1, "colours": 3, "scales": 3, "rotations": 4,
              "sh": SH_DIM[sh] * 3}
    expected = 16 + count * sum(widths.values())
    if len(raw) != expected:
        raise SystemExit(f"{path.name}: unexpected size; layout assumption wrong")

    off = 16
    planes = {}
    for name, w in widths.items():
        planes[name] = raw[off:off + count * w]
        off += count * w

    scale = 1.0 / (1 << frac)
    return count, scale, planes


def decode_positions(plane: bytes, count: int, scale: float):
    xs = [0.0] * count
    ys = [0.0] * count
    zs = [0.0] * count
    for i in range(count):
        o = i * 9
        for axis, out in ((0, xs), (1, ys), (2, zs)):
            b = o + axis * 3
            v = plane[b] | (plane[b + 1] << 8) | (plane[b + 2] << 16)
            if v & 0x800000:
                v -= 0x1000000
            out[i] = v * scale
    return xs, ys, zs


def percentile(sorted_vals, p):
    if not sorted_vals:
        return 0.0
    i = min(len(sorted_vals) - 1, max(0, int(len(sorted_vals) * p)))
    return sorted_vals[i]


def main(argv):
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("file", type=Path)
    ap.add_argument("--cells", type=int, default=20,
                    help="grid resolution across the long axis (default 20)")
    ap.add_argument("--water-top", type=int, default=10,
                    help="how many water cells to list (default 10)")
    ap.add_argument("--margin", type=float, default=1.5,
                    help="metres of slack added around the crop box (default 1.5)")
    ap.add_argument("--json", action="store_true", help="emit machine-readable output")
    args = ap.parse_args(argv)

    count, scale, planes = load(args.file)
    xs, ys, zs = decode_positions(planes["positions"], count, scale)
    colours = planes["colours"]
    alphas = planes["alphas"]

    # --- crop box, from percentiles ------------------------------------
    sx, sy, sz = sorted(xs), sorted(ys), sorted(zs)
    lo = {"x": percentile(sx, 0.005), "y": percentile(sy, 0.005), "z": percentile(sz, 0.005)}
    hi = {"x": percentile(sx, 0.995), "y": percentile(sy, 0.995), "z": percentile(sz, 0.995)}
    crop_min = {k: round(lo[k] - args.margin, 2) for k in lo}
    crop_max = {k: round(hi[k] + args.margin, 2) for k in hi}

    kept = sum(1 for i in range(count)
               if crop_min["x"] <= xs[i] <= crop_max["x"]
               and crop_min["y"] <= ys[i] <= crop_max["y"]
               and crop_min["z"] <= zs[i] <= crop_max["z"])

    # --- water cells ---------------------------------------------------
    # Bucket into a horizontal grid; score each cell by how much bright, desaturated,
    # reasonably opaque content it holds. That is water.
    span_x = crop_max["x"] - crop_min["x"]
    span_z = crop_max["z"] - crop_min["z"]
    cell = max(span_x, span_z) / args.cells
    grid = {}

    for i in range(count):
        x, y, z = xs[i], ys[i], zs[i]
        if not (crop_min["x"] <= x <= crop_max["x"]
                and crop_min["y"] <= y <= crop_max["y"]
                and crop_min["z"] <= z <= crop_max["z"]):
            continue
        if alphas[i] < 100:
            continue

        r, g, b = colours[i * 3], colours[i * 3 + 1], colours[i * 3 + 2]
        mx, mn = max(r, g, b), min(r, g, b)
        lum = (r + g + b) / 3.0
        sat = (mx - mn) / mx if mx else 0.0
        # Bright and grey. Foliage is green and saturated; wet rock is dark.
        watery = lum > 150 and sat < 0.22

        key = (int((x - crop_min["x"]) // cell), int((z - crop_min["z"]) // cell))
        c = grid.setdefault(key, {"n": 0, "w": 0, "ysum": 0.0, "xsum": 0.0, "zsum": 0.0})
        c["n"] += 1
        c["xsum"] += x
        c["ysum"] += y
        c["zsum"] += z
        if watery:
            c["w"] += 1

    cells = []
    for (i, j), c in grid.items():
        if c["n"] < 200:
            continue
        cells.append({
            "x": round(c["xsum"] / c["n"], 2),
            "y": round(c["ysum"] / c["n"], 2),
            "z": round(c["zsum"] / c["n"], 2),
            "splats": c["n"],
            "water": c["w"],
            "waterFrac": round(c["w"] / c["n"], 3),
        })
    cells.sort(key=lambda c: c["waterFrac"], reverse=True)
    water = cells[: args.water_top]

    if args.json:
        print(json.dumps({
            "file": args.file.name, "splats": count,
            "cropMin": crop_min, "cropMax": crop_max,
            "keptSplats": kept, "cellSizeM": round(cell, 2),
            "waterCells": water,
        }, indent=2))
        return 0

    print(f"{args.file.name}  {count:,} splats\n")
    print("crop box (0.5th–99.5th percentile + "
          f"{args.margin} m margin) keeps {kept:,} splats ({kept / count:.1%})")
    print(f"  min  {crop_min['x']:>8.2f} {crop_min['y']:>8.2f} {crop_min['z']:>8.2f}")
    print(f"  max  {crop_max['x']:>8.2f} {crop_max['y']:>8.2f} {crop_max['z']:>8.2f}")
    print(f"  span {crop_max['x']-crop_min['x']:>8.2f} "
          f"{crop_max['y']-crop_min['y']:>8.2f} {crop_max['z']-crop_min['z']:>8.2f}\n")

    print(f"most watery cells ({cell:.1f} m grid) — bright and desaturated:")
    print(f"  {'x':>8}{'y':>8}{'z':>8}{'splats':>9}{'water%':>8}")
    for c in water:
        print(f"  {c['x']:>8.2f}{c['y']:>8.2f}{c['z']:>8.2f}"
              f"{c['splats']:>9,}{c['waterFrac']*100:>7.1f}%")

    if water:
        highest = max(water, key=lambda c: c["y"])
        lowest = min(water, key=lambda c: c["y"])
        print(f"\n  highest water: ({highest['x']:.1f}, {highest['y']:.1f}, {highest['z']:.1f})"
              "   <- candidate waterfall / upstream end")
        print(f"  lowest water:  ({lowest['x']:.1f}, {lowest['y']:.1f}, {lowest['z']:.1f})"
              "   <- candidate downstream end")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
