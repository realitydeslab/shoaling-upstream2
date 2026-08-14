# Source audio inventory

Folder: <https://drive.google.com/drive/folders/1NkqFP499sq8XFzoTh_CccWkMm-vIsp_c>
Owner: **yangyangyang@berkeley.edu** · read 2026-08-14 · 13 files

Durations for the first three MP3s are exact (from an earlier catalogue); the rest are derived
from file size at the measured 135 kbps and marked ~. WAV durations assume 44.1 kHz / 16-bit
stereo and are therefore **upper bounds** — at 48 kHz / 24-bit they are ~0.6× these figures.
Confirm on download.

## Narration spine — six MP3s, ~3 min 50 s total

| Title | Drive ID | Bytes | Duration |
|---|---|---:|---:|
| `0_Opening.mp3` | `1c1uFlHtfHBVkJ5FQRRw1X5V_6txddQTz` | 677,551 | 40.1 s |
| `1_New Life.mp3` | `1GL6QUn3w1LGkqhIgBquz0SuGQsKi-Yzc` | 527,944 | 30.8 s |
| `2_Growing.mp3` | `1IFR4mt3L16QrQauUi6U8KBt5gIy_lycT` | 635,755 | 37.5 s |
| `3_Journey to the ocean.mp3` | `15OiMrx6nVuGCIgriwjPDoKwkwbNa3D94` | 993,110 | ~58.8 s |
| `4_Returning Home.mp3` | `1WIG29pjaJavT_GkwR8nnUrxbHWpvxmPq` | 496,180 | ~29.4 s |
| `5_Rebirth.mp3` | `1aoKP0Gnj9SfzCYyYeUIh_tvUkP0Yp2F5` | 561,358 | ~33.2 s |

## Ambience — two long WAVs

| Title | Drive ID | Bytes | Duration |
|---|---|---:|---:|
| `Tree creek waterplants 1.wav` | `1el86qXmsON7Tp8e9XPLtWzQxQpeb2EbV` | 11,007,438 | ≤62 s |
| `Tree creek waterplants 2.wav` | `1Jdb9ez4C8rfQFCqOGdS23JwwrqUeyj6m` | 13,873,690 | ≤79 s |

## Creatures and actions — five WAVs

| Title | Drive ID | Bytes | Duration |
|---|---|---:|---:|
| `Heron.wav` | `1b_72ZL34MS-kI7rJ1onI5GsnEORUkYdR` | 1,994,214 | ≤11 s |
| `Strider.wav` | `1Zqvoo49-lEnXdRhotqpzr1WDv3zttV8Q` | 880,964 | ≤5 s |
| `Eat strider.wav` | `1cHBJvTanHWQXud5O-4TBGjz1a7hDPw3Z` | 92,786 | ≤0.5 s |
| `Jump.wav` | `13KdTdAT6QwXBbgJCnzkuUMxbAvsJcR8O` | 1,919,404 | ≤11 s |
| `Lay egg.wav` | `14SoW2ioecMYI6n0qIJqYVddEKlEWUO2P` | 81,097 | ≤0.5 s |

---

## What the inventory decides

### The animals are settled: heron and water strider, not bear and mosquito

There is a `Heron.wav`, a `Strider.wav` and an `Eat strider.wav`. There is no bear and no mosquito.
The assets that exist are also the ecologically correct ones for Strawberry Creek, and they match
the five-chapter structure of the project's own storyboard.

This retires the nitrogen argument for keeping a bear. What replaces it is the storyboard's own and
arguably better idea: **reciprocity within one reach**. The strider feeds you; you feed the heron.
Both exchanges happen at the creek rather than being carried off into a forest, which fits a 55 m
site far better than a bear's territory ever would.

Note the asymmetry the sounds imply: `Eat strider.wav` is a half-second event, while `Heron.wav`
runs several seconds. Taking is quick; being taken from is not.

### There is about six minutes of authored material for a fifteen-to-thirty minute piece

Narration is ~3 min 50 s across six chapters; ambience is at most ~2 min 20 s. Everything else has
to come from the continuous field — looped, layered, and varied at runtime.

This is not a shortfall to fix by recording more narration. It is the correct ratio for a
dwell-dominated work: the chapters are the spine, and the field is what the visitor actually spends
their time inside. But it does mean **the ambient bed is the single most load-bearing asset in the
piece**, and two minutes of it will need careful seamless looping and enough runtime variation to
survive twenty minutes of exposure without becoming audibly cyclic.

### `Tree creek waterplants` is already the right idea

The two ambience files are named for three of the six sonified elements at once — tree, creek,
water plants. That is a composed bed rather than isolated sources, which fits the "never start from
silence" requirement directly and is what should be playing before any beat fires.

### Rights are unresolved

An earlier catalogue recorded rights status as `unverified`: the folder exposes filenames and
metadata but not authorship or licence. Since the owner is the project's own coauthor, this is
probably straightforward — but it should be written down before anything is published or exhibited,
not assumed.

## Pipeline notes

- Package as **mono 48 kHz** point sources for PHASE — mono is what lets it spatialise direction
  accurately, and stereo files would be wasted on a point source.
- Keep the narration and the field on **different mixers**: narration is head-relative and belongs
  on the ambient mixer; the creature and element sources belong on the spatial mixer.
- The three-recordings-per-beat approach (`docs/audio-findings.md` §1) means each element needs
  **far / mid / intimate** variants. Only one variant exists per sound today, so this is a
  production task, not just an import task — probably the largest single piece of audio work in the
  project.
