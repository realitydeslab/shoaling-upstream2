#!/usr/bin/env python3
"""Report the robust extent of an SPZ file.

Every Scaniverse scan carries floater splats — stray gaussians a kilometre from anything real.
Min/max is therefore useless: on our creek scan the true content spans 61 m while the raw
bounds span 2400 m. Percentiles are the only sane way to ask "how big is this scene", and any
code that frames a camera or seeds a coordinate system must use them.

Usage:
    python3 spz_bounds.py FILE.spz [--percentile 1]
"""

from __future__ import annotations

import argparse
import gzip
import struct
import sys
from pathlib import Path

MAGIC = 0x5053474E
SH_DIM = {0: 0, 1: 3, 2: 8, 3: 15}
PLANES = {"positions": 9, "alphas": 1, "colours": 3, "scales": 3, "rotations": 4}


def read_positions(path: Path) -> tuple[list[float], list[float], list[float], dict]:
    raw = gzip.open(path, "rb").read()
    magic, version, count, sh, frac, flags, _res = struct.unpack("<III4B", raw[:16])
    if magic != MAGIC:
        raise SystemExit(f"{path.name}: not an SPZ file (magic 0x{magic:08x})")

    widths = dict(PLANES, sh=SH_DIM[sh] * 3)
    expected = 16 + count * sum(widths.values())
    if len(raw) != expected:
        raise SystemExit(
            f"{path.name}: expected {expected:,} bytes for {count:,} points at SH {sh}, "
            f"found {len(raw):,} — layout assumption is wrong for this file"
        )

    scale = 1.0 / (1 << frac)
    pos = raw[16 : 16 + count * 9]
    xs, ys, zs = [], [], []
    for i in range(count):
        o = i * 9
        # three little-endian signed 24-bit fixed-point values
        for axis, out in ((0, xs), (1, ys), (2, zs)):
            b = o + axis * 3
            v = pos[b] | (pos[b + 1] << 8) | (pos[b + 2] << 16)
            if v & 0x800000:
                v -= 0x1000000
            out.append(v * scale)

    meta = {"count": count, "sh": sh, "frac": frac, "version": version, "flags": flags}
    return xs, ys, zs, meta


def summarise(values: list[float], p: float) -> dict:
    values.sort()
    n = len(values)
    lo = values[max(0, min(n - 1, int(n * p / 100)))]
    hi = values[max(0, min(n - 1, int(n * (100 - p) / 100)))]
    mid = values[n // 2]
    return {
        "min": values[0], "max": values[-1],
        "lo": lo, "hi": hi, "median": mid, "span": hi - lo,
    }


def main(argv: list[str]) -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("file", type=Path)
    ap.add_argument("--percentile", type=float, default=1.0,
                    help="tail percentage to trim from each end (default 1)")
    args = ap.parse_args(argv)

    xs, ys, zs, meta = read_positions(args.file)
    p = args.percentile

    print(f"{args.file.name}")
    print(f"  {meta['count']:,} splats · SPZ v{meta['version']} · SH degree {meta['sh']}")
    print(f"  trimming {p}% from each tail\n")
    print(f"  {'axis':<6}{'lo':>10}{'median':>10}{'hi':>10}{'span':>10}"
          f"{'raw min':>12}{'raw max':>12}")

    axes = {}
    for name, vals in (("X", xs), ("Y", ys), ("Z", zs)):
        s = summarise(vals, p)
        axes[name] = s
        print(f"  {name:<6}{s['lo']:>10.2f}{s['median']:>10.2f}{s['hi']:>10.2f}"
              f"{s['span']:>10.2f}{s['min']:>12.1f}{s['max']:>12.1f}")

    long_axis = max(axes, key=lambda k: axes[k]["span"])
    print(f"\n  long axis: {long_axis} ({axes[long_axis]['span']:.1f} m)")
    print(f"  floater ratio: raw bounds are "
          f"{(axes[long_axis]['max'] - axes[long_axis]['min']) / max(axes[long_axis]['span'], 1e-6):.0f}x "
          f"the trimmed span")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
