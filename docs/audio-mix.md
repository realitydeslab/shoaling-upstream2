# The mix: how many places you are told about at once

Written 2026-08-14, after the artist's report: *"when I play, I can hear many reverb sound. make
it clean"*, and then *"it based on the camera distance to the point of interest"*.

Both halves of that turned out to be right, and neither was about reverb.

---

## What was actually happening

Every source's audible reach was `audibleRadiusM ?? exitRadiusM * 3.5`. On the UBC garden that is
**10.6 m for every beat**, culled at `reach * 1.35` = **14.4 m**.

The garden reach is **18.8 m long**, and its six beats sit at s = 4.0, 5.6, 9.8, 13.2, 14.9, 17.1
— **0.68 to 4 m apart**. So every beat was inside every other beat's field from one end of the
walk to the other. Measured along the real journey, at **every** position on the reach:

| | before |
|---|---|
| sources within cull distance | **7 of 7** (six beats + the creek bed), everywhere |
| recordings running | **21**, everywhere — the old audition started all three layers of anything it had not culled |
| sources audible (≥ −42 dB) | 4 to 7 |
| the leading beat's share of the summed amplitude | **0.36 to 0.70** |

Six ambiences playing at once, none of them louder than about half the field, is what "many
reverb sound" is: not a reverb tail, a smear. `docs/audio-findings.md` §3 predicted 2–4 audible at
a time — but it sized that estimate on *"8–12 m spacing with ~25 m tails"*, and this reach has
neither. The 3.5 multiple was answering a question about one beat while the problem was a
question about six.

There was also a genuine synthesised reverb on top, and it should not have been there. See below.

## What changed

Three things, all in `editor/src/audition.ts`.

### 1. The reach is bounded by the composition's own geometry

```
reach = clamp(nearestNeighbourM / 1.35, exitRadiusM, exitRadiusM * 3.5)
```

Far enough that a beat is fully audible everywhere it can fire; no further than the point where
its cull radius reaches its nearest neighbour. With no neighbour it returns exactly the old
number, so a lone source is unchanged, and an authored `audibleRadiusM` still wins outright —
which is what the ambient beds do ("The creek itself" states 40 m and means it).

On the UBC garden **every beat floors at 3.04 m**, because all six are closer together than one
exit radius. That is not the rule failing; it is the rule reporting a fact about the journey (see
"What only the artist can fix"). On Berkeley, whose beats are 9–11 m apart, it produces 6.8–8.2 m
rather than a flat 10.6 m, and the beats there separate completely.

`setMaxDistance` is now the cull distance too, so Resonance's rolloff arrives at zero exactly
where the source is dropped. It used to still have a third of its curve left at the cut, which
made the cull a step. The side effect is a steeper near-field law, which is what
`docs/audio-findings.md` §1 asks for anyway.

### 2. The nearest point of interest is the subject

`geom.ts` `evaluateAt` has always been winner-take-all: one state machine, one firing beat. The
audio was a plain sum over everything it could reach. **That asymmetry was the bug** — the
scrubber said "Heron wins" while the headphones played six places at once.

Each beat is now scaled by

```
focus = (distance to the nearest beat / distance to this one) ^ 2
```

which is winner-take-all with the corners taken off. Standing at a beat, it is the only thing
playing. Standing midway between two, both are at 1.0 and you are crossing out of one place into
the next. A rival at twice the distance sits 12 dB down, at three times 19 dB. Nothing ever
snaps, because the ratio is continuous — verified in `test/audition-mix.test.ts` by sampling the
total at two resolutions and showing that what does not shrink with the step is a single voice
crossing the −42 dB floor.

**This is not the level-carries-distance mistake.** Which recording you hear, and therefore how
near you are, is still decided entirely by the far/mid/intimate crossfade; the whole 12 dB
inverse-square budget across 5–20 m is still not being asked to do work it cannot do. The duck
decides a different question — which of several places you are being told about — and gain is the
right tool for that one. It is the ducking of `docs/audio-findings.md` §3c, applied across space
instead of across time.

Ambient beds are exempt in both directions: never ducked, never the leader, never a neighbour.
They are the floor of the mix, and silencing them to win would be cheating.

### 3. A recording runs only while it has gain

The old code called `play()` on all three layers of any source in range. Since at most two layers
have any weight at once, a third of those decoders were always producing silence, and a beat 4 m
away held three of them. Layers now start when their gain rises above zero and stop 400 ms after
it returns to zero, with the pending stop cancelled if they come back inside that window.

`update()` also keeps hysteresis around the audible floor — a voice must reach 2.5× the floor to
start, and stays until it falls below the floor — so a visitor standing still on the boundary
does not restart the same recording several times a second. `mixAt` deliberately has no
hysteresis: it reports one steady-state truth, and the state lives in the caller.

## The measurement

The same model plays and is measured: `mixAt` is what `update` writes to the gain nodes, and what
the tables below sample. Positions are the walker's, from `pointAtS` along `site.centreline` —
the phone on its neck mount at chest height, which is what the path already is.

**UBC garden, before and after.** `loops` is recordings running; `audible` counts sources above
−42 dB; `share` is the leading beat's fraction of the summed amplitude.

| s | loops (before → after) | audible (before → after) | total (before → after) | share (before → after) |
|---:|---|---|---|---|
| 0 | 21 → **2** | 4 → **1** | 0.137 → 0.009 | 0.39 → — |
| 2 | 21 → **4** | 5 → **2** | 0.232 → 0.085 | 0.52 → **0.85** |
| 4 | 21 → **4** | 6 → **3** | 0.638 → 0.293 | 0.49 → **0.89** |
| 6 | 21 → **2** | 7 → **2** | 0.819 → 0.399 | 0.67 → **0.95** |
| 8 | 21 → **5** | 7 → **3** | 0.493 → 0.219 | 0.39 → **0.80** |
| 10 | 21 → **4** | 7 → **2** | 0.649 → 0.258 | 0.52 → **0.95** |
| 12 | 21 → **6** | 7 → **3** | 0.740 → 0.246 | 0.57 → **0.90** |
| 14 | 21 → **5** | 7 → **3** | 0.993 → 0.389 | 0.48 → **0.66** |
| 16 | 21 → **3** | 6 → **2** | 0.989 → 0.378 | 0.41 → **0.93** |
| 18 | 21 → **3** | 5 → **2** | 0.733 → 0.356 | 0.70 → **0.96** |

The field is quieter *between* beats and no quieter *at* them: at s = 13, half a metre from the
heron, the leader reads 0.601 against 0.560 before. Arrival is not what was turned down.

At s = 0 nothing but the creek bed is audible at all — the walk now starts in the creek rather
than in the middle of six overlapping ambiences.

**And read off the running editor**, driving the walker through the real page on :8710 with all
seven sources loaded and playing, then reading the actual `GainNode` values and whether each
`<audio>` element was really running:

| s | recordings running | what was on, and at what gain |
|---:|---:|---|
| 0 | 0 | — |
| 2 | 2 | `tree.far 0.117` `tree.mid 0.189` |
| 4 | 1 | `tree.intimate 0.371` |
| 6 | 2 | `redd.intimate 0.378` `creek-bed.intimate 0.114` |
| 8 | 5 | `strider.mid 0.366` `strider.far 0.039` `redd.far 0.076` `redd.mid 0.030` `creek-bed.intimate 0.112` |
| 10 | 4 | `strider.intimate 0.258` `strider.mid 0.064` `creek-bed.intimate 0.096` `creek-bed.mid 0.004` |
| 12 | 6 | `heron.mid 0.205` `heron.intimate 0.036` `strider.far 0.029` `strider.mid 0.028` `creek-bed.intimate 0.078` `creek-bed.mid 0.019` |
| 14 | 3 | `heron.mid 0.178` `falls.mid 0.164` `heron.intimate 0.079` |
| 16 | 2 | `falls.intimate 0.400` `spawn.mid 0.031` |
| 18 | 3 | `spawn.intimate 0.313` `falls.mid 0.055` `spawn.mid 0.030` |

Against 21 recordings running at every one of those positions before. The running count sits at
or just under what `mixAt` asks for, which is the floor hysteresis and the 400 ms stop window
doing their jobs — at s = 0 the creek bed is below the 2.5× starting threshold and has not come
in yet, which is why the walk opens on nothing at all.

## Two questions the mix had to answer

### Does the vertical distance count?

**For where a sound is, yes. For which recording plays, no.**

Beats are authored at bed height, in the water; the listener is a phone at chest height on the
bank. On the UBC garden the walker's closest approach is:

| beat | closest approach, in plan | vertical | in 3D |
|---|---|---|---|
| tree | 0.44 m | 1.22 m | 1.29 m |
| redd | 0.02 m | −0.17 m | 0.17 m |
| strider | 0.61 m | 1.05 m | 1.21 m |
| heron | 0.05 m | 0.41 m | 0.42 m |
| falls | 0.05 m | 1.27 m | 1.27 m |
| spawn | 0.26 m | 0.23 m | 0.35 m |

The intimate recording only plays inside 0.35 of the reach — 1.06 m at the garden's 3.04 m. So
under a straight 3D distance, **tree, strider and falls could never play their intimate layer at
all**, anywhere on the walk. The visitor would stand directly over the redd and never hear it
arrive. That vertical component is a fact about where the creek bed is, not about how much
further there is to walk, and once the reach tightened it became load-bearing.

So the crossfade is chosen by distance across the ground. Resonance still gets the true 3D
position, still rolls off on the true 3D distance, and you still hear a beat below you as below
you — which is worth having, and is the cue `docs/audio-findings.md` §7 recommends leaning on
when there is no head tracking.

### Does the mix fall to silence before the next beat's zone?

**Not on this journey, and it cannot.** For a beat to be inaudible before its neighbour's zone
begins, `reach * 1.35` would have to be under half the spacing — under 0.34 m for falls and
spawn, which are 0.68 m apart. A beat that went quiet inside its own trigger radius would be a
worse bug than the one being fixed, so the floor holds and adjacent beats overlap.

What the mix does instead is make the overlap read as one place with a neighbour behind it rather
than as two places at once: the second voice sits 6–19 dB down for almost all of the crossing,
and is only equal at the exact midpoint, which is the moment it should be.

Where the spacing allows it, silence does arrive: Berkeley's beats are 9–11 m apart and the mix
is a single beat at a time for most of that 61 m reach, and `test/audition-mix.test.ts` pins the
case of two beats 40 m apart falling to nothing in between.

## `OUTDOOR_ROOM` should not exist, and no longer does

It was a 30×12×40 box with grass walls and a water floor. It is now the same box with every
surface `transparent` — Resonance's own default materials, absorption 1.000 in every band, so no
early reflections and no tail. Three reasons, in order of weight:

1. **The headphones are not noise-cancelling.** The visitor is standing in the real acoustic of a
   real creek bank, which supplies real early reflections and a real tail at full level the whole
   time. A synthesised second room does not add space; it makes two rooms disagree, and the one
   the ear believes is the one the body is standing in. `docs/audio-findings.md` §6 already says
   this about water — *"we are EQ'ing reality"* — and it applies to the room as much as to the
   riffle.
2. **The walls were not there.** Resonance's room model is a shoebox. Four grass walls at 15 and
   20 m out returned reflections off surfaces that do not exist on an open bank. `up: transparent`
   was already conceding the point for the sky.
3. **It is still the honest stand-in, and that is why the room stays.** PHASE gives the whole
   scene ONE reverb preset — `PHASESetSceneReverbPreset(int)`, scene-wide — so the fifth beat
   cannot have its own acoustic even though it is literally about a constriction. That limitation
   is structural and the single scene-wide room here still represents it. What changed is only
   what is in the slot, and running it dry is the plan of record: bake the space into the three
   recordings (§2, §6), automate the per-source sends, and let the site supply the rest.

If a reverb is ever wanted on site, it belongs in this one slot and nowhere else — that
restriction is the thing worth keeping honest.

## What only the artist can fix

None of this is in `data/journeys/**`, and none of it should be: these are recommendations, not
edits.

**The six beats are closer together than their own trigger zones.** 0.68 m (falls → spawn), 1.47 m
(tree → redd), 2.20 m, 3.20 m, 4.01 m, against an `exitRadiusM` of 3.04 m. That is the one thing
preventing real silence between beats, and it costs nothing in the mix to leave as it is — it
just means adjacent beats always overlap a little. Spreading the pairs to 5 m or more, or
tightening `exitRadiusM`, would buy genuine separation. Worth knowing that the devlog still lists
the UBC walking path as never having been placed against the real creek, so these spacings are
provisional anyway.

**`audibleRadiusM` is the override, and it is worth using where the fiction wants it.** A
waterfall is audible from much further than a water strider, and the automatic reach cannot know
that: it will give both the same 3.04 m. Authoring `audibleRadiusM` on `falls` — 8 to 12 m — would
let it be heard approaching, and the duck would keep it from swamping its neighbours. Nothing
else in the journey needs a number written into it.

## What is still not right

- **The editor's audible-half-life ring is now drawn from the wrong reach.** `editor/js/scene.js`
  calls `audibleField(beat)`, which falls back to the unbounded `exitRadiusM * 3.5` because it
  does not know about the neighbours; the audition plays 3.04 m. The ring is roughly three times
  too big until that call passes the same reach —
  `audibleField(beat, reachFor(beat, nearestNeighbourM(beat, this.beats)))`. That file belongs to
  another agent this session, so it is reported rather than changed.
- **Levels here remain provisional.** Everything above is relative. `docs/audio-findings.md` §6
  is unambiguous that the mix has to be set on the bank against the real creek, and the −23 LUFS
  reflex is wrong for a 47–67 dB(A) site.
- **The three distance layers are still derived, not recorded.** `package-audio.sh` low-passes a
  single take to stand in for a distant one, so the crossfade is currently carrying less content
  difference than the design assumes. That is the largest piece of audio work left, and it will
  make the duck matter less, not more.
