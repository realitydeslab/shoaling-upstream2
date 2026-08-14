#!/usr/bin/env bash
#
# Precompute streaming LOD assets for every scan.
#
# The editor was building its level-of-detail tree in the browser on every load — about 22
# seconds of WebWorker time for a 5 M-splat capture, repeated every time anyone opened the
# page. Spark can instead load a `.rad` file with the tree already built, and stream it in
# chunks rather than downloading 100 MB before showing anything.
#
# It also gets us a better tree. In-browser generation uses `tiny-lod`; offline we can afford
# `bhatt-lod`, which spark's own changelog describes as the higher-quality method.
#
# Requires spark's `build-lod` Rust tool. Point BUILD_LOD at the binary, or let this script
# find it in the default scratch location.
#
#   ./tools/build-rad.sh
#
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SRC="$ROOT/data/splats"
OUT="$ROOT/data/splats/rad"

BUILD_LOD="${BUILD_LOD:-}"
if [[ -z "$BUILD_LOD" ]]; then
  for candidate in \
    "$ROOT/../.spark-src/rust/target/release/build-lod" \
    /private/tmp/claude-501/*/*/scratchpad/spark-src/rust/target/release/build-lod
  do
    [[ -x "$candidate" ]] && BUILD_LOD="$candidate" && break
  done
fi

if [[ -z "$BUILD_LOD" || ! -x "$BUILD_LOD" ]]; then
  cat >&2 <<'MSG'
build-lod not found.

Build it from spark's source tree (Rust toolchain required):

    git clone --depth 1 https://github.com/sparkjsdev/spark.git
    cd spark
    cargo build --manifest-path rust/build-lod/Cargo.toml --release

then re-run with BUILD_LOD=/path/to/spark/rust/target/release/build-lod
MSG
  exit 1
fi

mkdir -p "$OUT"
echo "using $BUILD_LOD"
echo

shopt -s nullglob
for src in "$SRC"/*.spz; do
  name="$(basename "$src" .spz)"
  dest="$OUT/$name.rad"

  if [[ -f "$dest" && "$dest" -nt "$src" ]]; then
    echo "  $name  up to date"
    continue
  fi

  echo "  $name  building..."
  # build-lod names its own outputs and writes them beside the input: a small "<name>-lod.rad"
  # manifest plus a "<name>-lod-N.radc" chunk per page. It takes no output path — passing one
  # makes it treat the path as a second input file and panic.
  #
  # --quality     : bhatt-lod, the better offline tree (vs the tiny-lod the browser builds)
  # --rad-chunked : one file per page, so the editor fetches only what the view needs
  # Crop to the authored box if the journey has one. Every Scaniverse export carries floater
  # splats hundreds of metres from anything real; cropping at build time removes them from the
  # asset entirely rather than asking the renderer to draw and then hide them.
  crop=()
  draft="$ROOT/data/journeys/$name/draft.json"
  if [[ -f "$draft" ]]; then
    box=$(python3 -c "
import json,sys
d=json.load(open('$draft')).get('editorFrame',{}).get('crop')
if d: print('--min-box=%s,%s,%s --max-box=%s,%s,%s' % (
    d['min']['x'],d['min']['y'],d['min']['z'],d['max']['x'],d['max']['y'],d['max']['z']))
" 2>/dev/null)
    [[ -n "$box" ]] && read -r -a crop <<< "$box" && echo "    crop ${crop[*]}"
  fi

  "$BUILD_LOD" --quality --rad-chunked --max-sh=3 ${crop[@]+"${crop[@]}"} "$src" > /dev/null

  mv "$SRC/$name-lod.rad" "$dest"
  mv "$SRC/$name-lod-"*.radc "$OUT/"
  # The manifest references chunks by bare filename, so they must sit beside it.
done

echo
echo "wrote to $OUT"
for rad in "$OUT"/*.rad; do
  [[ -e "$rad" ]] || continue
  base="$(basename "$rad" .rad)"
  chunks=$(ls "$OUT/$base-lod-"*.radc 2>/dev/null | wc -l | tr -d " ")
  total=$(du -ch "$rad" "$OUT/$base-lod-"*.radc 2>/dev/null | tail -1 | cut -f1)
  printf "  %-34s manifest %s + %s chunks (%s)\n" \
    "$base" "$(du -h "$rad" | cut -f1)" "$chunks" "$total"
done
