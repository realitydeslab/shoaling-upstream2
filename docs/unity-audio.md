# The audio engine in Unity

What the app does with sound, why it is arranged this way, and where it is honest about its
limits. The research this implements is in `docs/audio-findings.md`; the standing constraints are
in `docs/devlog.md`. This document is about the code.

---

## 1. The shape of it

```
                      pure C#, no Unity audio, no PHASE, runs in EditMode
  ┌──────────────────────────────────────────────────────────────────────┐
  │ DistanceField     the three-layer crossfade + the distance law       │
  │ ShoalVoicing      count → voices, width, individuation, bed pair     │
  │ MediumEnvelope    in the water / out of it, and the sweep between    │
  └──────────────────────────────────────────────────────────────────────┘
                                    │  gains, never distances
  ┌──────────────────────────────────────────────────────────────────────┐
  │ SpatialAudioEngine   owns the sources, ticks the field               │
  └──────────────────────────────────────────────────────────────────────┘
                                    │  IAudioBackend
      ┌─────────────────────┬───────┴────────────┬──────────────────────┐
   PhaseAudioBackend    UnityAudioBackend   HeadlessAudioBackend
   Apple PHASE, iOS     the desk            tests, and machines with no
   via Plugins/iOS      stand-in            output device at all
```

`JourneyAudioRunner` is the only MonoBehaviour: it picks a backend, follows the camera, and turns
fiction-level events (`Completed`, "gave fish", "is lifted") into calls on the engine.

**The interface is told gains, not distances.** That single rule is what makes the desk trustworthy:
no backend has an opinion about the distance law, so there is nothing for them to disagree about.

### The clip seam

`IAudioClipResolver` has one method. It returns a `ResolvedClip` carrying an `AudioClip`, a file
path, or both, because PHASE registers assets from a URL and Unity wants a clip in memory. The
catalogue being built separately will be adapted onto this in a few lines; nothing in the engine
knows about it. A clip that resolves to neither is reported in `SpatialAudioEngine.MissingClips`
and never substituted — the three layer ids are one suffix apart, and a resolver that guessed
would play the intimate recording from twenty metres away without anyone knowing why the piece
felt wrong.

---

## 2. Distance is carried by content

Each source has three recordings — `<id>--far`, `<id>--mid`, `<id>--intimate` — and approaching
crossfades between them. The law is a port of `editor/src/audition.ts`, so the desk audition and the
device agree, and `Tests/EditMode/Audio/DistanceFieldTests.cs` pins the same numbers that
`test/audible-field.test.ts` pins on the JavaScript side.

With `n = distance / reach`:

```
intimate = clamp01(1 − n / 0.35)
mid      = clamp01(1 − abs(n − 0.5) / 0.35)
far      = clamp01((n − 0.45) / 0.4)
```

`reach = audibleRadiusM ?? exitRadiusM × 3.5`. Sources are stopped past `1.35 × reach`, where the
law has already reached about −53 dB.

The bands overlap everywhere, so there is no distance at which a source falls silent between
recordings, and the summed field genuinely has a second lobe around `n = 0.5` where the mid
recording arrives. The audible half-life is defined as the **first** crossing of half the peak for
exactly that reason. On the shipped garden gate (3.04 m exit radius, layers at −14 / −8 / −4 dB)
that is **1.30 m** — well inside the 1.9 m radius at which the interaction arms. The trigger rings
say nothing about what the visitor can hear.

### On top of the crossfade: the rolloff

The editor multiplies the crossfade by Resonance's `'logarithmic'` attenuation, which is not a
logarithm: it is `1/(d+1)`, offset by the minimum distance and renormalised to reach zero at the
maximum. Guessing a real logarithm from the name put the half-life out by 20%, so the shape is
copied rather than described.

**We apply that law ourselves and switch the engine's own rolloff off** — `rolloffFactor = 0` on
the PHASE spatial mixer, a flat custom curve on every Unity `AudioSource`. PHASE exposes only a
single rolloff scalar, so there is no setting that could be made to match the editor's curve, and
letting each engine apply its own would mean the desk and the bank disagreeing about the one law
the whole sound design rests on.

### About the 12 dB budget

The 12 dB figure is the inverse-square budget over two doublings of distance, and it is a statement
about **content**, not about the total field. The crossfade's own level — `ContentGainAt`, before
any distance attenuation — moves by exactly the 10 dB the layers were authored at across the whole
reach, and never exceeds the loudest single layer. That is the claim the test asserts.

The *total*, rolloff included, moves much more: over 5→20 m on a 20 m-reach source the editor's law
spends **22.5 dB** where physical inverse-square would spend 12. That is not a discrepancy, it is
the exaggerated rolloff the findings ask for — an effective `rolloffFactor` of about **1.87**,
inside the recommended 1.5–2.0 range. Worth knowing before anyone "corrects" it.

### Smoothing

Layer gains approach their target exponentially with a 0.08 s time constant rising and 0.12 s
falling, matching the editor's `setTargetAtTime`. Continuous field parameters have a 50–100 ms
budget, so this costs nothing that can be heard, and the alternative — pushing raw per-frame
distances — puts pose jitter straight onto the gains.

---

## 3. The shoal

The visitor is a shoal, and it has to be *heard*. There is no number on screen and there should
never be one.

**Beds.** Density is pre-rendered, because there is no push-stream in PHASE and no way to granulate
in real time. The ladder is geometric across the journey's own range — for `startingCount: 40,
minimumCount: 6` that is **6, 10, 15, 25, 40**, clip ids `shoal--6` … `shoal--40`. Geometric because
auditory numerosity is Weber-like: six to twelve is the same perceived change as twelve to
twenty-four. Two beds sound at once and crossfade **linearly**, not equal-power: they are the same
grain cloud rendered at two densities from the same seed, so they are correlated and an equal-power
crossfade would bulge by 3 dB in the middle.

> **The beds do not exist yet.** `tools/package-audio.sh` renders no `shoal--*` clips, so the shoal
> is currently silent and reported as missing. Rendering them — same grain source, same RNG seed,
> seven densities — is outstanding work, and the seed-identity is the part that matters: separate
> takes would sound like a crossfade between two recordings rather than like loss.

**Voices.** `count → voices` is logarithmic, from 2 at the floor to 8 at the starting count. Why
voices at all: a bed at a given grain density encodes how busy the cloud is, but density alone
reads as "busier", and level alone reads as "it got quieter" — which is the exact failure to avoid,
since the shoal only shrinks when the visitor gives fish to the heron and that has to land as loss.
What makes a crowd read as a crowd is *decorrelation*: several copies of the same texture arriving
from different directions. So the parameter that carries headcount is the number of decorrelated
voices, and it is a small integer because a small integer is what the ear can count. Past about
eight the ear stops counting and hears "many", so the ladder saturates rather than tracking forty
fish with forty voices.

The count is quantised but its *edge* is not: the outermost voice fades across one whole voice's
worth of headcount, so the integer changes while nothing clicks.

**Width is derived, not set.** The unison width is the angular extent of whichever voices are
actually sounding. It used to be an independent parameter and the tests caught the consequence
immediately — a width narrow enough to silence a voice the count said was sounding. "Fewer voices"
and "narrower unison" are one mechanism, not two.

**Individuation** rises from 0 to 1 as the shoal thins. This is the counter-intuitive cue and the
one that works — a smaller group is *more* legible as individuals, and grains that lengthen and
overlap less until single fish poke out are what make people say "there are fewer of them" rather
than "it got quieter".

It is **carried by the bed renders, not by the runtime**. PHASE cannot sweep a start offset on a
running ambient event, so a runtime decorrelation parameter would have quietly done nothing on the
real backend; the number is published so the renders have a curve to hit. The desk stand-in
approximates it with a per-voice start offset of up to 45 ms, kept under the ~50 ms echo threshold
so the copies still fuse into a cloud rather than becoming a slapback.

**Level does almost nothing.** Three decibels across the entire range, and the per-voice gains are
power-normalised so losing a voice changes where the shoal is and not how loud it is.

**Timing: fast on the loss, slow on the settle.** Density and bed selection drop with a 0.13 s time
constant — about 95% inside 400 ms, unmistakably caused by the heron. Voices, width and
individuation follow a 2.5 s constant, so the group is still reorganising for several seconds
afterwards. Instant-and-permanent reads as a bug; slow-and-gradual reads as unrelated to the bear.
Growth is deliberately not the mirror of loss: spawning widens over about two seconds.

---

## 4. The barrier is a lift, and the lift is holdable

`MediumEnvelope` models being out of the water as a **state**, not an event. The barrier
interaction is a 40 cm lift — hauling yourself onto something and staying there — so the audio has
to hold "out of the water" for as long as the visitor stays up. A one-shot would end while they
were still standing on it.

What changes is the medium, not an object: the whole field goes dry and bright at once, reverb send
to zero and the low-pass wide open, which needs no learning from anyone who has ever surfaced in a
swimming pool. Rising takes about 150 ms, falling about 250 ms, and re-entry raises a splash at the
instant the visitor comes down rather than when the filter finishes moving — the splash is the
cause and the filter is the consequence.

Cutoff interpolates **logarithmically**: halfway between 1.4 kHz and 18 kHz is 5 kHz, not 9.7 kHz.
A linear sweep would spend almost all its travel in the top octave where nothing is audible.

`AudioEngineSettings.ReentrySplashClipId` is unset by default, because the `falls` beat already
carries `jump` as its completion clip and two splashes for one landing would read as a double
trigger. Set it only if that changes.

---

## 5. The PHASE bridge

`Plugins/iOS/PhaseBridge.m` is thirteen C entry points over Apple's PHASE. It is deliberately dull:
every decision about how the piece sounds is made in C#, and anything implemented on the native
side could only ever be checked by standing at a creek with a device.

| Concern | How |
|---|---|
| Spatialisation | `PHASESpatialMixerDefinition`, direct path + late reverb |
| Distance | **disabled** (`rolloffFactor = 0`) — the law is ours |
| Culling | off (1000 m against a ~71 m site); the engine stops sources at 1.35× reach |
| Three recordings | three `PHASESamplerNodeDefinition`s in a container, one gain metaparameter each |
| Shoal | eight `PHASEAmbientMixerDefinition`s at baked azimuths across ±110°, head-relative |
| Confirmations | `PHASEChannelMixerDefinition`, head-relative, held until the completion handler fires |
| Head tracking | `PHASEListener.automaticHeadTrackingFlags` — no entitlement needed |
| Reverb | `PHASEEngine.defaultReverbPreset`, one preset for the whole piece |

**Not using `useAutoDistanceBlend`.** PHASE can drive the distance crossfade inside its own mixer at
audio rate, which is the thing it does best, and the findings call it the highest-leverage
recommendation available. We do not use it, for three reasons: it is the only way to guarantee the
desk and the bank hear the same law, it is the only way the law can be asserted without a device,
and PHASE's rolloff scalar cannot be made to match the editor's curve anyway. The cost is that the
blend updates at frame rate rather than audio rate — 16–33 ms against a 50–100 ms budget, paid out
of slack. If that ever proves audible, this is the first decision to revisit.

**Coordinates.** Unity is left-handed with +Z forward; PHASE, like the rest of AVFoundation, is
right-handed with −Z forward. Positions flip Z; quaternions negate x and y. Getting it wrong mirrors
the entire soundfield left to right, which sounds plausible until someone walks past a beat on the
wrong side.

### Two things PHASE cannot do

**No global filter.** PHASE's public surface has no filter node and no insert point, so the
*bright* half of the lift cue is not available on device — only the dry half, via the reverb preset.
`SUPhaseSupportsLowPass()` returns 0 and the managed side warns once rather than silently ignoring
it. The fix is content: a dry, open variant of each bed, crossfaded by `AirBlend` the same way the
distance layers are. That is more recording work and it is the honest solution.

**One reverb, no per-source rooms.** Known and designed around: the space is baked into the
recordings, which the three-recordings approach was going to require anyway.

### Linking

`PhaseBridge.m` uses `@import PHASE;`, relying on clang module autolinking, which Unity's generated
Xcode project has on by default. **This has not been built for a device from this repo yet.** If
autolinking is off, PHASE.framework must be added to the UnityFramework target, which needs an
Xcode post-process build script under an `Editor/` folder that does not exist yet.

---

## 6. Running the tests

```
/Applications/Unity/6000.3.21f1/Unity.app/Contents/MacOS/Unity -batchmode -nographics -runTests \
  -projectPath app -testPlatform EditMode -testResults logs/audio-tests.xml -logFile logs/unity-audio.log
```

**A relative `-testResults` is resolved against `-projectPath`**, so that lands at
`app/Logs/audio-tests.xml`, not `logs/audio-tests.xml`. The log file is not — `-logFile` really is
relative to the working directory. Pass an absolute path if you want them together.

45 tests in `Tests/EditMode/Audio/`. Nothing in them touches PHASE, Unity's audio thread or a
GameObject; they run against `HeadlessAudioBackend`, which is exactly the seam the design exists to
provide.

**`-runTests` aborts on any compile error anywhere in the project**, in any assembly, whether or not
the test assembly depends on it. So a broken file in a sibling area stops this suite from running
even though nothing links the two, and stubbing it out would not help — it has to be fixed. If the
run aborts with "Scripts have compiler errors", read `logs/unity-audio.log` and find out whose it is
before assuming it is yours.

A non-zero exit is also what you get when **another Unity instance has the project open**, which at
the shell looks identical to a real failure. Check the message before believing it.

Where behaviour is meant to match the editor, the same numbers are asserted on both sides — reach
10.64 m, max distance 17.024 m, peak 0.6310, half-life 1.30 m. If the two implementations ever
drift, the rings an author composed against in the browser stop describing what a visitor hears at
the creek, and nothing else in either codebase would say so.

---

## 7. Not built

Named rather than quietly missing.

- **Shoal beds.** No `shoal--*` renders exist. See §3.
- **The dodging manager.** Duty cycles and enforced silence between sources
  (`audio-findings.md` §3b). At 2–4 audible beats this will matter on site.
- **Habituation ducking and snapshots.** A source droning for two minutes carries no information.
- **The bear.** Field silence, the low impact, the subtractive danger cue.
- **Haptics.** Core Haptics on the lift and the bear, as a single AHAP so audio/haptic sync is
  guaranteed.
- **A device build.** Nothing here has been compiled for or heard on an iPhone.
