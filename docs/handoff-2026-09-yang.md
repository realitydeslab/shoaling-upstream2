# Handoff: Yang's AR prototype branch (Aug 14 → Sep 27, 2026)

This branch carries everything since `8bc1e70` (your last commit on `main`): a working
wizard-of-oz AR build of all 17 beats, rebuilt after a Unity crash on Sep 17. It fast-forwards
from `main` — nothing of yours was rewritten. It also brings Yang's revised narration recordings. Please read the "Review
carefully" list before merging; several things here are reconstructions or deliberate shortcuts.

## What runs today

The phone does not localize yet. Instead the piece runs as a wizard-of-oz:

```
iPad B (operator)  →  Mac: service (npm start)  →  iPad A: ARKit app (AR.unity)
browser /control      control bus on :8710          plays each beat's animation in place
```

- iPad B opens `http://<Mac IP>:8710/control?site=ucb-strawberry-creek-south` and presses FIRE
  per beat. Replay current / Skip forward / Resume rebuild the scene up to (or just before) the
  current beat.
- Outdoors, all three devices join a GL.iNet Beryl 7 travel router. The Mac has a DHCP
  reservation at `192.168.8.125`; the router repeats home Wi-Fi when one is available.
- VPS is deferred. Models are anchored with plain ARKit tracking plus a LiDAR floor raycast.
- Audio is not wired into this build yet.

The journey (`data/journeys/ucb-strawberry-creek-south`, draft = published revision 4) now has
17 beats:

| Beat | Label | Beat | Label |
|---|---|---|---|
| beat-1 | 01-a New Born – Spawn | beat-17 | 05-a Fry – Swim |
| beat-2 | 01-b New Born – Hatch | beat-9 | 05-b Fry – Become Rainbow Trout |
| beat-3 | 02-a Grow – Swim | beat-10 | 06-a Return Home – Return |
| beat-4 | 02-b Grow – Alevin to Fry | beat-16 | 06-b Return Home – Swim |
| beat-5 | 03-a Strider – Show | beat-12 | 06-c Return Home – Jump |
| beat-7 | 03-b Strider – Eat Strider & Grow | beat-13 | 07-a Rebirth – Locate |
| beat-6 | 04-a Heron – Show | beat-14 | 07-b Rebirth – Spawn |
| beat-8 | 04-b Heron – Feed | beat-15 | 07-c Rebirth – Fade |
| beat-11 | 04-c Heron – Hide | | |

Each beat maps to one of 13 trigger kinds in `SimulationDriver` (Spawn, Appear, PlayClip,
SwimToEye, EatAndGrow, Cull, Jump, …). `SimulationSceneBuilder` wires beat → prefab → trigger and
generates both `Simulation.unity` and `AR.unity`, so scene changes belong in the builder, not in
the saved scenes.

## Review carefully

1. **Sep 17 crash recovery (`8644b75`).** Unity crashed mid-export and wiped the working tree,
   including days of uncommitted work. `SimulationDriver.cs` and `SimulationSceneBuilder.cs` were
   rebuilt from Claude Code's edit history; `PhaseBridge.m` came byte-for-byte from the last
   Xcode export; Player Settings, package entries (glTFast, Timeline, Input System, XR Core
   Utils), the XR loader and the models were restored from the same sources. The fish egg's
   original Timeline asset was not recoverable and is now a scripted fall. **The per-beat
   positions and `givesFish` values in the journey are placeholders**, not measured site data.
2. **`SimulationDriver` now ships on the phone.** `AR.unity` is built from the same rig, so the
   "desk only, never on a device" rule in `docs/unity-simulation.md` no longer holds. It has grown
   to ~3,000 lines and deserves a split before VPS goes in.
3. **The service address is hardcoded.** `ArServiceHost = "192.168.8.125"` in
   `SimulationSceneBuilder.cs`. Testing on another network means changing it, running *Shoaling
   Upstream → Rebuild AR Scene*, and reinstalling — or reserving that IP on your own router.
4. **`LICENSE` was added and removed again.** GitHub's new-repo template put an MIT licence
   ("Copyright (c) 2026 yyyang") into a personal mirror (`sheepy23/BecomingTrout`), and merging
   that mirror's unrelated first commit (`9344781`) brought it in. It was never an intentional
   licensing decision and could not cover the third-party assets below, so this branch deletes it.
   The repo has no licence file; choosing one is still open.
5. **Third-party Asset Store content.** `Assets/Quirky Series` (cricket, used as the strider) and
   `Assets/Quirky Series Vol 2` (kookaburra, used as the heron's chicks) are committed. Keep the
   repo private, and check that the Asset Store licence covers everyone who clones it.
6. **Git LFS.** These commits were made on machines without git-lfs. The LFS pointers are
   unchanged from your `8bc1e70`, so the real audio and `.spz` objects still live only on this
   remote; the personal mirror has none.
7. **`preloadedAssets` churn.** The XR / ARKit build processors add NSDK settings, the ARKit
   background shaders and XR settings to `ProjectSettings.asset` during a build and remove them
   after. `abaaef3` caught them mid-build and `6675b2f` sets the list back to empty. Expect this
   file to flicker after builds; it is not a real change.
8. **Revised narration, packaged clips are now stale.** The six narration MP3s in
   `data/audio/source/` are Yang's revised takes from Sep 23, renamed `00 Opening.mp3` …
   `05 Rebirth.mp3`; `tools/package-audio.sh` and `data/audio/source-inventory.md` follow the new
   names. `data/audio/packaged/` still holds layers cut from the August takes — re-run
   `tools/package-audio.sh` (needs ffmpeg) before auditioning narration in the editor. The seven
   WAVs are byte-identical to what was already in LFS. None of the audio is in the AR build yet.
9. **Docs lag the code.** The README still reads "not yet on a device", and nothing in `docs/`
   describes the wizard-of-oz mode, the trigger kinds, or the router setup. This file is the only
   write-up so far.

## Commits on this branch

| Commit | Date | Summary |
|---|---|---|
| (this) | 09-27 | Remove `LICENSE`; add this handoff note |
| (audio) | 09-27 | Replace narration with the Sep 23 takes; follow the renames in packaging and inventory |
| `6675b2f` | 09-27 | Remove empty duplicate XR folders; settle `preloadedAssets` |
| `50fdf2e` | 09-21 | Strider/heron grounding, 05-a Fry – Swim, 07-b egg tuning, alevin rise to eye height |
| `abaaef3` | 09-19 | Fish drift, heron turns on its feet (`FeetLock`), jump axis, alpha-fade material twins, GL.iNet address |
| `2301886` | 09-18 | Egg fall/scatter, alevin facing, Replay/Resume, 06-b Swim |
| `9344781` | 09-17 | Merge the personal mirror's first commit (adds `LICENSE`) |
| `8644b75` | 09-17 | Recover the project after the Unity crash |

## Open

- VPS localization in place of the operator-fired beats.
- Audio in the AR build.
- Real per-beat positions at Strawberry Creek South, then a published revision from them.
- Promote the UBC garden splat asset to production (still yours to do in the Niantic portal).
