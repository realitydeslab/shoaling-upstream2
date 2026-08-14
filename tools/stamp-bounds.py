#!/usr/bin/env python3
"""Write each scan's measured extent into its journey draft.

The editor used to walk every splat on load to find the scene bounds. That is several seconds
of work for a 5M-splat capture, and it stops working entirely once spark's LOD is enabled,
because the splats then live in the level-of-detail structure rather than a flat array.

Bounds are a static property of a scan. Measure them once, here, and let the editor read them.
"""
import json, pathlib, sys
sys.path.insert(0, str(pathlib.Path(__file__).parent))
from spz_bounds import read_positions, summarise

ROOT = pathlib.Path(__file__).resolve().parent.parent
changed = 0

for draft in sorted((ROOT / "data" / "journeys").glob("*/draft.json")):
    doc = json.loads(draft.read_text())
    proxy = doc.get("editorFrame", {}).get("splatFile")
    if not proxy:
        continue

    # Measure the full capture, not the proxy — that is what the editor shows by default.
    full = ROOT / "data" / "splats" / proxy.replace(".proxy.spz", ".spz")
    if not full.exists():
        print(f"  {draft.parent.name}: no full capture at {full.name}, skipped")
        continue

    xs, ys, zs, meta = read_positions(full)
    axes = {name: summarise(v, 1.0) for name, v in (("x", xs), ("y", ys), ("z", zs))}

    doc["editorFrame"]["bounds"] = {
        "splats": meta["count"],
        "lo":     {k: round(a["lo"], 3) for k, a in axes.items()},
        "hi":     {k: round(a["hi"], 3) for k, a in axes.items()},
        "centre": {k: round(a["median"], 3) for k, a in axes.items()},
        "span":   {k: round(a["span"], 3) for k, a in axes.items()},
        "note": "1st-99th percentile. Raw min/max is meaningless: every scan carries floater "
                "splats hundreds of metres out.",
    }
    draft.write_text(json.dumps(doc, indent=2) + "\n")
    changed += 1
    s = doc["editorFrame"]["bounds"]["span"]
    print(f"  {draft.parent.name:<32} {meta['count']:>9,} splats · "
          f"{s['x']:.1f} x {s['y']:.1f} x {s['z']:.1f} m")

print(f"\n  stamped {changed} draft(s)\n")
