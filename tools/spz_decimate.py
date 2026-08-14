#!/usr/bin/env python3
"""Decimate a Niantic SPZ v3 Gaussian-splat file for browser use.

Scaniverse exports ~5M splats at spherical-harmonic degree 3, which lands at 85-96 MB
gzipped and roughly 300 MB in memory. That is fine on device and hostile in a browser
editor. This tool subsamples points and optionally lowers the SH degree.

SPZ v3 binary layout, verified against real Scaniverse exports:

    header 16 bytes  : magic u32 (0x5053474e) | version u32 | numPoints u32
                       shDegree u8 | fractionalBits u8 | flags u8 | reserved u8
    then, per point, in five contiguous planes (struct-of-arrays, not interleaved):
        positions   9 bytes   3 x 24-bit fixed point, scaled by 2**fractionalBits
        alphas      1 byte
        colours     3 bytes
        scales      3 bytes
        rotations   4 bytes   (v2 used 3; v3 adds one byte per point)
        sh          shDim*3   shDim = 0 / 3 / 8 / 15 for degree 0 / 1 / 2 / 3

Total is 20 + 3*shDim bytes per point: 65 at degree 3, 20 at degree 0.

Because the planes are contiguous, decimation is a per-plane gather with one shared
index array, and lowering the SH degree is just dropping the SH plane's tail.

Usage:
    python3 spz_decimate.py IN.spz OUT.spz --target 600000 --sh-degree 1
"""

from __future__ import annotations

import argparse
import gzip
import struct
import sys
from pathlib import Path

SPZ_MAGIC = 0x5053474E
HEADER = struct.Struct("<III4B")
HEADER_BYTES = 16

# bytes per point in each plane, and the SH coefficient count per degree
PLANE_WIDTHS = {"positions": 9, "alphas": 1, "colours": 3, "scales": 3, "rotations": 4}
SH_DIM = {0: 0, 1: 3, 2: 8, 3: 15}


class SpzError(RuntimeError):
    pass


def read_spz(path: Path) -> tuple[dict, dict[str, bytes]]:
    """Return (header_fields, planes). Planes are raw bytes, one entry per plane."""
    raw = gzip.open(path, "rb").read()
    if len(raw) < HEADER_BYTES:
        raise SpzError(f"{path.name}: too short to be an SPZ file")

    magic, version, count, sh_degree, frac_bits, flags, reserved = HEADER.unpack(
        raw[:HEADER_BYTES]
    )
    if magic != SPZ_MAGIC:
        raise SpzError(f"{path.name}: bad magic 0x{magic:08x}, expected 0x{SPZ_MAGIC:08x}")
    if sh_degree not in SH_DIM:
        raise SpzError(f"{path.name}: unsupported SH degree {sh_degree}")

    widths = dict(PLANE_WIDTHS, sh=SH_DIM[sh_degree] * 3)
    expected = HEADER_BYTES + count * sum(widths.values())
    if len(raw) != expected:
        raise SpzError(
            f"{path.name}: size mismatch — expected {expected:,} bytes for {count:,} points "
            f"at SH degree {sh_degree}, found {len(raw):,}. The layout assumption is wrong "
            f"for this file; refusing to corrupt it."
        )

    planes: dict[str, bytes] = {}
    offset = HEADER_BYTES
    for name, width in widths.items():
        size = count * width
        planes[name] = raw[offset : offset + size]
        offset += size

    header = {
        "version": version,
        "count": count,
        "sh_degree": sh_degree,
        "frac_bits": frac_bits,
        "flags": flags,
        "reserved": reserved,
    }
    return header, planes


def pick_indices(count: int, target: int) -> range | list[int]:
    """Evenly spaced subsample. Splats are emitted in reconstruction order rather than
    spatial order, so a uniform stride samples the whole volume without clustering."""
    if target >= count:
        return range(count)
    stride = count / target
    return [int(i * stride) for i in range(target)]


def gather(plane: bytes, width: int, indices) -> bytes:
    if width == 0:
        return b""
    if isinstance(indices, range) and indices == range(len(plane) // width):
        return plane
    out = bytearray(len(indices) * width)
    for slot, src in enumerate(indices):
        start = src * width
        out[slot * width : (slot + 1) * width] = plane[start : start + width]
    return bytes(out)


def write_spz(path: Path, header: dict, planes: dict[str, bytes], count: int,
              sh_degree: int, level: int) -> None:
    body = b"".join(
        planes[name] for name in ("positions", "alphas", "colours", "scales", "rotations", "sh")
    )
    head = HEADER.pack(
        SPZ_MAGIC, header["version"], count,
        sh_degree, header["frac_bits"], header["flags"], header["reserved"],
    )
    with gzip.open(path, "wb", compresslevel=level) as fh:
        fh.write(head + body)


def decimate(src: Path, dst: Path, target: int, sh_degree: int | None,
             level: int = 6) -> dict:
    header, planes = read_spz(src)
    count = header["count"]
    out_sh = header["sh_degree"] if sh_degree is None else sh_degree
    if out_sh > header["sh_degree"]:
        raise SpzError(
            f"cannot raise SH degree from {header['sh_degree']} to {out_sh}; "
            f"the coefficients do not exist in the source"
        )

    indices = pick_indices(count, target)
    out_count = len(indices)

    out_planes = {
        name: gather(planes[name], width, indices)
        for name, width in PLANE_WIDTHS.items()
    }

    # SH is stored as shDim RGB triples per point, lowest degree first, so a lower
    # degree is a prefix of a higher one — truncate rather than resample.
    src_sh_bytes = SH_DIM[header["sh_degree"]] * 3
    keep_sh_bytes = SH_DIM[out_sh] * 3
    if keep_sh_bytes == 0:
        out_planes["sh"] = b""
    else:
        sh = planes["sh"]
        buf = bytearray(out_count * keep_sh_bytes)
        for slot, src_i in enumerate(indices):
            start = src_i * src_sh_bytes
            buf[slot * keep_sh_bytes : (slot + 1) * keep_sh_bytes] = sh[start : start + keep_sh_bytes]
        out_planes["sh"] = bytes(buf)

    dst.parent.mkdir(parents=True, exist_ok=True)
    write_spz(dst, header, out_planes, out_count, out_sh, level)

    return {
        "in_points": count,
        "out_points": out_count,
        "in_sh": header["sh_degree"],
        "out_sh": out_sh,
        "in_bytes": src.stat().st_size,
        "out_bytes": dst.stat().st_size,
    }


def main(argv: list[str]) -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("source", type=Path)
    ap.add_argument("destination", type=Path)
    ap.add_argument("--target", type=int, default=600_000,
                    help="approximate output splat count (default 600000)")
    ap.add_argument("--sh-degree", type=int, choices=[0, 1, 2, 3], default=None,
                    help="output SH degree; omit to keep the source's")
    ap.add_argument("--compresslevel", type=int, default=6, choices=range(1, 10))
    args = ap.parse_args(argv)

    try:
        stats = decimate(args.source, args.destination, args.target,
                         args.sh_degree, args.compresslevel)
    except SpzError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 1

    shrink = stats["in_bytes"] / max(stats["out_bytes"], 1)
    print(
        f"{args.source.name} -> {args.destination.name}\n"
        f"  points  {stats['in_points']:>10,} -> {stats['out_points']:>9,}"
        f"   ({stats['out_points'] / stats['in_points']:.1%})\n"
        f"  SH deg  {stats['in_sh']:>10} -> {stats['out_sh']:>9}\n"
        f"  bytes   {stats['in_bytes']:>10,} -> {stats['out_bytes']:>9,}"
        f"   ({shrink:.1f}x smaller)"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
