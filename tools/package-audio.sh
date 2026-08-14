#!/usr/bin/env bash
#
# Package source recordings into the clips the journey references.
#
# Two things happen here.
#
# **Mono, 48 kHz.** PHASE spatialises a point source from a mono signal; a stereo file would
# have its image thrown away, so we do the downmix deliberately rather than let the engine
# do it silently.
#
# **Three distance layers per source.** The whole sound design rests on distance being carried
# by *content* rather than gain: at 5-20 m the entire inverse-square budget is about 12 dB,
# which against a 47-67 dB(A) creek reads as "slightly louder", not as arrival. So every source
# becomes `--far`, `--mid` and `--intimate`, and what you hear approaching is a crossfade
# between three different recordings.
#
# The layers generated here are DERIVED, not recorded — a low-passed, flattened version is a
# stand-in for a genuinely distant take, not the real thing. They exist so the mechanic can be
# heard and tuned at a desk. Recording each source at three real distances is a production
# task, and it is the largest single piece of audio work left in the project.
#
#   ./tools/package-audio.sh
#
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SRC="$ROOT/data/audio/source"
OUT="$ROOT/data/audio/packaged"
CATALOGUE="$ROOT/data/audio/catalogue.json"

command -v ffmpeg >/dev/null || { echo "ffmpeg is required" >&2; exit 1; }
mkdir -p "$OUT"

# Source file -> clip id. Anything not listed is ignored, so dropping a stray file into
# data/audio/source/ cannot silently change what the journey plays.
declare -a MAP=(
  "0_Opening.mp3|opening"
  "1_New Life.mp3|chapter-1-new-life"
  "2_Growing.mp3|chapter-2-growing"
  "3_Journey to the ocean.mp3|chapter-3-journey-to-the-ocean"
  "4_Returning Home.mp3|chapter-4-returning-home"
  "5_Rebirth.mp3|chapter-5-rebirth"
  "Heron.wav|heron"
  "Strider.wav|strider"
  "Eat strider.wav|eat-strider"
  "Lay egg.wav|lay-egg"
  "Jump.wav|jump"
  "Tree creek waterplants 1.wav|tree-creek-waterplants-1"
  "Tree creek waterplants 2.wav|tree-creek-waterplants-2"
)

# Layered sources get three distance variants; one-shot confirmations do not — a completion
# sound plays at the moment of the action, where there is no distance to convey.
ONE_SHOT="eat-strider lay-egg jump"

RATE=48000
entries=()

encode() { # in out filter
  ffmpeg -nostdin -loglevel error -y -i "$1" \
    -ac 1 -ar "$RATE" -af "$3" -c:a libmp3lame -q:a 4 "$2"
}

echo "packaging into $OUT"
echo

for pair in "${MAP[@]}"; do
  file="${pair%%|*}"
  clip="${pair##*|}"
  src="$SRC/$file"

  if [[ ! -f "$src" ]]; then
    echo "  $clip  MISSING ($file)"
    entries+=("{\"clipId\":\"$clip\",\"missing\":true,\"source\":\"$file\"}")
    continue
  fi

  if [[ " $ONE_SHOT " == *" $clip "* ]]; then
    encode "$src" "$OUT/$clip.mp3" "loudnorm=I=-18:TP=-1.5:LRA=11"
    echo "  $clip  one-shot"
    entries+=("{\"clipId\":\"$clip\",\"file\":\"$clip.mp3\",\"layers\":[\"one-shot\"]}")
    continue
  fi

  # far      — distance eats the top octaves first, so roll off hard and flatten dynamics.
  # mid      — a gentle shelf; present but not in the room with you.
  # intimate — full bandwidth with a touch of low shelf, the proximity effect of being close.
  encode "$src" "$OUT/$clip--far.mp3" \
    "lowpass=f=2200,highpass=f=90,dynaudnorm=g=9,volume=-3dB"
  encode "$src" "$OUT/$clip--mid.mp3" \
    "lowpass=f=6500,highpass=f=60,volume=-1dB"
  encode "$src" "$OUT/$clip--intimate.mp3" \
    "equalizer=f=180:t=q:w=1.2:g=2.5,volume=0dB"

  echo "  $clip  far / mid / intimate"
  entries+=("{\"clipId\":\"$clip\",\"file\":\"$clip.mp3\",\"layers\":[\"far\",\"mid\",\"intimate\"]}")
done

{
  echo "{"
  echo "  \"generatedBy\": \"tools/package-audio.sh\","
  echo "  \"sampleRateHz\": $RATE,"
  echo "  \"channels\": 1,"
  echo "  \"note\": \"Distance layers are derived from a single source recording, not captured at three distances. They demonstrate and tune the crossfade; they are not a substitute for real takes.\","
  echo "  \"rights\": { \"status\": \"unverified\", \"owner\": \"yangyangyang@berkeley.edu\", \"note\": \"Source folder exposes filenames and metadata but not authorship or licence. Confirm before any public showing.\" },"
  echo "  \"clips\": ["
  printf '    %s' "${entries[0]}"
  for e in "${entries[@]:1}"; do printf ',\n    %s' "$e"; done
  echo
  echo "  ]"
  echo "}"
} > "$CATALOGUE"

echo
echo "catalogue: $CATALOGUE"
ls "$OUT" | wc -l | xargs printf "  %s files packaged\n"
