#!/usr/bin/env python3
"""Profile the creek along the authored trim box, to find the bed and the falls.

The trim box is already positioned and rotated by hand to follow the creek, which makes it
the best statement anyone has made about where the water actually runs. This walks its long
axis, and at each step reports the ground height and how much of the content in that slice
looks like water — bright and desaturated, which is what running water renders as against
green foliage and dark wet rock.

The waterfall shows up as the place where ground height climbs steeply and the water fraction
stays high.

Usage:
    python3 creek-profile.py ubc-nitobe-garden-creek [--steps 24]
"""

from __future__ import annotations

import argparse
import gzip
import json
import math
import struct
import sys
from pathlib import Path

MAGIC = 0x5053474E
SH_DIM = {0: 0, 1: 3, 2: 8, 3: 15}
ROOT = Path(__file__).resolve().parent.parent


def load_positions_and_colours(path: Path):
    raw = gzip.open(path, "rb").read()
    magic, _version, count, sh, frac, _flags, _res = struct.unpack("<III4B", raw[:16])
    if magic != MAGIC:
        raise SystemExit(f"{path.name}: not an SPZ file")

    widths = {"positions": 9, "alphas": 1, "colours": 3, "scales": 3,
              "rotations": 4, "sh": SH_DIM[sh] * 3}
    if len(raw) != 16 + count * sum(widths.values()):
        raise SystemExit(f"{path.name}: unexpected size for the assumed layout")

    off = 16
    planes = {}
    for name, w in widths.items():
        planes[name] = raw[off:off + count * w]
        off += count * w

    scale = 1.0 / (1 << frac)
    return count, scale, planes


def quat_to_yaw(q):
    x, y, z, w = q
    sinp = 2 * (w * y - z * x)
    sinp = max(-1.0, min(1.0, sinp))
    return math.asin(sinp)


def main(argv):
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("slug")
    ap.add_argument("--steps", type=int, default=22)
    ap.add_argument("--corridor", type=float, default=None,
                    help="half-width of the sampled corridor (default: the box's own)")
    args = ap.parse_args(argv)

    draft = json.loads((ROOT / "data" / "journeys" / args.slug / "draft.json").read_text())
    trim = draft["editorFrame"].get("trim")
    if not trim or "halfExtent" not in trim:
        raise SystemExit("this site has no oriented trim box; position one in the editor first")

    spz = ROOT / "data" / "splats" / draft["editorFrame"]["splatFile"].replace(".proxy.spz", ".spz")
    count, scale, planes = load_positions_and_colours(spz)

    yaw = quat_to_yaw(trim["rotation"])
    # Local +X of the box is its long axis; a Y-rotation maps it to this world direction.
    ax, az = math.cos(yaw), -math.sin(yaw)
    # Local +Z, the cross-stream direction.
    cx, cz = math.sin(yaw), math.cos(yaw)

    c = trim["position"]
    half_long = trim["halfExtent"]["x"]
    half_cross = args.corridor if args.corridor is not None else trim["halfExtent"]["z"]
    half_up = trim["halfExtent"]["y"]

    steps = args.steps
    buckets = [dict(n=0, water=0, ys=[]) for _ in range(steps)]

    pos = planes["positions"]
    col = planes["colours"]
    alp = planes["alphas"]

    for i in range(count):
        o = i * 9
        vals = []
        for axis in range(3):
            b = o + axis * 3
            v = pos[b] | (pos[b + 1] << 8) | (pos[b + 2] << 16)
            if v & 0x800000:
                v -= 0x1000000
            vals.append(v * scale)
        x, y, z = vals

        dx, dz = x - c["x"], z - c["z"]
        along = dx * ax + dz * az
        cross = dx * cx + dz * cz
        up = y - c["y"]

        if abs(along) > half_long or abs(cross) > half_cross or abs(up) > half_up:
            continue
        if alp[i] < 100:
            continue

        idx = min(steps - 1, int((along + half_long) / (2 * half_long) * steps))
        r, g, b_ = col[i * 3], col[i * 3 + 1], col[i * 3 + 2]
        mx, mn = max(r, g, b_), min(r, g, b_)
        lum = (r + g + b_) / 3.0
        sat = (mx - mn) / mx if mx else 0.0

        bk = buckets[idx]
        bk["n"] += 1
        bk["ys"].append(y)
        if lum > 150 and sat < 0.22:
            bk["water"] += 1

    print(f"{args.slug}   box yaw {math.degrees(yaw):.1f}°, "
          f"long axis {half_long*2:.1f} m, corridor ±{half_cross:.1f} m\n")
    print(f"  {'s(m)':>6}{'world x':>9}{'world z':>9}{'ground':>9}{'canopy':>9}"
          f"{'splats':>9}{'water':>8}")

    rows = []
    for i, bk in enumerate(buckets):
        if bk["n"] < 150:
            continue
        t = (i + 0.5) / steps
        along = -half_long + t * 2 * half_long
        wx = c["x"] + ax * along
        wz = c["z"] + az * along
        ys = sorted(bk["ys"])
        ground = ys[int(len(ys) * 0.10)]     # 10th percentile: the bed, not the leaves
        canopy = ys[int(len(ys) * 0.95)]
        frac = bk["water"] / bk["n"]
        rows.append(dict(s=t * 2 * half_long, x=wx, z=wz, ground=ground,
                         canopy=canopy, n=bk["n"], water=frac))
        print(f"  {rows[-1]['s']:>6.1f}{wx:>9.2f}{wz:>9.2f}{ground:>9.2f}"
              f"{canopy:>9.2f}{bk['n']:>9,}{frac*100:>7.1f}%")

    if len(rows) < 3:
        raise SystemExit("\nnot enough content inside the box to profile")

    lo, hi = rows[0], rows[-1]
    print(f"\n  ground rises {hi['ground'] - lo['ground']:+.2f} m "
          f"from s=0 to s={hi['s']:.1f}")
    print(f"  {'FIRST end is LOWER' if lo['ground'] < hi['ground'] else 'LAST end is LOWER'}"
          "  -> that end is downstream")

    # Steepest climb between adjacent slices, weighted by how watery it is: a waterfall is
    # both a step in the bed and bright moving water.
    best, best_score = None, -1
    for a, b in zip(rows, rows[1:]):
        rise = b["ground"] - a["ground"]
        span = max(0.01, b["s"] - a["s"])
        score = (rise / span) * (0.5 + b["water"])
        if score > best_score:
            best_score, best = score, (a, b, rise, span)

    if best:
        a, b, rise, span = best
        print(f"\n  steepest watery rise: s {a['s']:.1f} -> {b['s']:.1f} m, "
              f"{rise:+.2f} m over {span:.1f} m  ({b['water']*100:.0f}% water)")
        print(f"    at world ({b['x']:.2f}, {b['ground']:.2f}, {b['z']:.2f})"
              "   <- candidate waterfall")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
