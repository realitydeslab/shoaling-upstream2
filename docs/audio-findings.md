# Spatial audio findings — Apple PHASE

Delivered 2026-08-14. **[V]** = verified against a cited source or read directly in the
`apple/unityplugins` source. **[U]** = inference or design proposal.

The authoritative file for every PHASE API claim below is
`plug-ins/Apple.PHASE/Apple.PHASE_Unity/Assets/Runtime/PHASEHelpers.cs` in
<https://github.com/apple/unityplugins>. **The P/Invoke signatures there are narrower than the
WWDC session implies**, and that gap is the source of most of the constraints in this document.

---

## 1. The finding that reorganises the sound design

### Level cannot be the distance cue here

At 5–20 m a source travels only **two doublings of distance** — under inverse-square that is
**about 12 dB** from "far" to "arrived". **[V — arithmetic from the −6 dB/doubling law]**

An urban-park noise floor measures **47–61 dB(A) internally, up to 67 dB(A) at the edges**. **[V]**
Add wind on the earbuds and the real creek, and 12 dB of gradient reads as "slightly louder", not
as arrival. **[U — the perceptual conclusion is inference, not measured on our site]**

**So distance must be carried by content change, not gain.** Three moves, in order of leverage:

**1. Make distance drive a blend, not a fader.** Author each beat as **three distinct recordings**
— far, mid, intimate — rather than one recording at three volumes. Far is midrange-only and
rhythmically sparse; intimate has HF detail, body noise and almost no reverb send.

The API supports this natively:
`PHASECreateSoundEventBlendNode(long inBlendParameterId, BlendNodeEntry[] entries, int numEntries, bool useAutoDistanceBlend)`.
When `useAutoDistanceBlend` is true, **PHASE drives the crossfade from source–listener distance
internally** rather than from a parameter we push per frame. **[V — read in `PHASEHelpers.cs`]**
That it therefore updates at audio rate rather than Unity frame rate is **[U]** — strongly implied
by living in the native mixer, not documented.

This is the single highest-leverage technical recommendation in the report, and it is the thing
PHASE does best.

**2. Exaggerate the rolloff.** `rolloffFactor` is documented in the plugin source: *"A value of 0.0
disables the roll-off effect. A value of 0.5 halves the roll-off. The default value is 1.0, which
produces a realistic roll-off effect. A value of 2.0 amplifies the roll-off effect."* **[V]** Start
the six beats at **1.5–2.0** and tune on site. **[U]**

**3. Reserve the last ~2.5 m as a distinct event.** Direct-to-reverberant ratio is the cheapest and
strongest intimacy cue available, and it is per-source: `DirectPathSend`, `EarlyReflectionsSend`,
`LateReverbSend` on `PHASESource`. **[V]** Drive `LateReverbSend` down hard inside 2.5 m.

### Turn culling off

Site diagonal is ~71 m and the scene is roughly 12–20 voices. Set `cullDistance = 0` — the plugin
documents 0 as disabling culling **[V]** — and do all fade-out with the curve. This also avoids a
hard cutoff at the cull radius reported by a third party. **[V that the source claims it; U as
fact — single report]**

At this scale we do **not** need voice limiting or virtualisation. We need a *temporal dodging*
manager instead (§3b). **[U]**

---

## 2. What the C# API can and cannot do

This is the practical constraint list, and several items are load-bearing.

| Capability | Status |
|---|---|
| Distance-driven blend between recordings | **Yes** — `useAutoDistanceBlend` **[V]** |
| Piecewise custom distance curves on the spatial mixer | **No.** The mixer exposes only `cullDistance` and `rolloffFactor`. Swift's `PHASEDistanceModelParameters` has them; the C# surface does not. **[V]** |
| Arbitrary response curves by another route | **Yes** — `PHASEMappedMetaParameterDefinition` reshapes an input metaparameter through a piecewise **envelope**. A real RTPC curve editor. **[V]** |
| Per-segment curve shapes | `Linear, Squared, InverseSquared, Cubed, InverseCubed, Sine, InverseSine, Sigmoid, InverseSigmoid` **[V — enum in `PHASEHelpers.cs`]** |
| Real-time synthesis into PHASE | **No.** `PHASEPushStreamNodeDefinition` exists in Swift but there is **no `stream` symbol anywhere in `PHASEHelpers.cs`**. The Unity surface plays registered audio files only. **[V — grepped]** |
| Ducking / side-chain / HDR | **No.** Implement in C# as a gain envelope on mixer gain metaparameters; `PHASESetMixerGainMetaParameter` exists. **[V]** |
| Per-source reverb spaces | **No.** `PHASESetSceneReverbPreset(int)` is **scene-wide**. Per source you get only the three sends. **[V]** |
| Listener head tracking | **Yes**, directly: `PHASESetListenerHeadTracking(bool)`. No native code required. **[V]** |
| Level authoring in real SPL | **Yes** — `CalibrationMode {None, RelativeSpl, AbsoluteSpl}`; RelativeSpl −200…+12, AbsoluteSpl 0…120, None 0…1. **[V]** |
| Sequence containers | **No** — Random, Switch, Blend and Container only. Drive a switch node's string metaparameter with an index instead. **[V]** |
| Designer-facing graph editor | **Yes** — the plugin ships an xNode-based visual sound-event editor. **[V]** |
| Minimum OS | **iOS 15.6** **[V — repo README]** |

### Two constraints that touch the design directly

**One global reverb preset.** We cannot give the culvert or barrier at beat 5 a different acoustic
space from the open bank at beat 1. That is a real limit on a piece whose fifth beat is *literally
about passing through a constriction*. Workarounds are per-source send automation and baking the
space into the recordings themselves — which fits the three-recordings-per-beat approach anyway. **[U]**

**Head tracking needs no entitlement in the plugin API.** `PHASESetListenerHeadTracking(bool)` is a
plain call. **[V]** This materially reduces the entitlement risk flagged earlier in the plan,
though it does not settle what Apple's separate "PHASE enhancement" capabilities cover. **[U]**

**Untested claim worth an early check:** a third party reports PHASE *"does not account for device
rotation at all"*, working correctly in only one device orientation. **[V that the source says it;
U as fact — single report]** If true it forces us to build the listener transform ourselves from
Core Motion. **Test in week one.**

**Author levels in `AbsoluteSpl`.** It forces a statement of what each thing's real-world loudness
*is*, which makes the relationship to the actual creek explicit rather than guessed. **[U]**

---

## 3. Keeping the field legible

At 8–12 m spacing with ~25 m tails, expect **2–4 beats audible at all times**, plus shoal, ambience
and the real creek. **[V — geometric consequence]** Three orthogonal tools.

### (a) Spectral zoning, with the subject supplying the theory

Bernie Krause's **acoustic niche hypothesis**: in a healthy habitat, vocalising species partition
the soundscape into distinct frequency bands *and* distinct temporal patterns to avoid
interference. **[V]** This is the actual organising principle of the place the visitor is standing
in — not a borrowed metaphor, which is why it is the right one.

Proposed allocation **[U — design proposal]**:

| Source | Spectral niche | Temporal behaviour |
|---|---|---|
| Water / flow | 200 Hz–2 kHz broadband + HF hiss | continuous, unpatterned |
| Stone | 60–250 Hz, long decay | rare, impulsive |
| Tree | 1–4 kHz filtered noise | slow AM at wind rate (0.2–2 Hz) |
| Insects | 4–8 kHz narrowband | fast, granular, erratic |
| Bear | 40–120 Hz + 300–800 Hz growl formant | very rare, long |
| Shoal (self) | 300 Hz–3 kHz dense | continuous, tied to gait |

**Water and shoal will fight hardest** — both continuous and broadband. Give the shoal a
formant/vowel character so it segregates by timbre where the bands overlap.

**Acceptance test:** if a listener cannot name which beat they are hearing from 15 m with three
neighbours audible, the allocation failed and mixing will not save it. **[U]**

**No discriminable information below ~150 Hz.** Wind noise level *"is relatively high in the low
frequency band and gradually decreases with increasing frequency"* **[V]**, and traffic sits in the
same region. Sub-bass is excellent for the bear's dread — an affective cue that survives partial
masking — and useless for anything the visitor must tell apart. **[U]**

### (b) Temporal partitioning — the dodging manager

Masking is a time-domain problem as much as a frequency one. **[V]** Enforce duty cycles in C#: an
insect phrase cannot start if one started within N ms; the stone speaks only after 400 ms of
relative quiet. **Silence is the cheapest legibility tool available**, and on a site where
everything is always in earshot it is the main one. **[U]**

### (c) Habituation-aware ducking

Grond & Berger note that masking, habituation and satiation all *"diminish the effectiveness"* of a
parameter-mapping display. **[V — *Sonification Handbook* ch.15]** A source droning for two minutes
carries near-zero information, so duck it: on a loud event pull the field down over ~150 ms and
recover over ~1.5 s. **[U]**

### Interpolate filter cutoff logarithmically

Linear interpolation of cutoff *"may produce extreme and unnatural sounding results"* while
logarithmic *"produces a perceptually linear frequency sweep"*. **[V — Unity audio manual]**

Note the spatial mixer's directivity model works in three subbands hardcoded at **200 Hz, 1500 Hz,
5000 Hz**. **[V — `PHASESpatialMixer.cs`]** Whether distance-based air absorption is separately
controllable is **[U — could not confirm]**. If it is not, we get it free from the three-recordings
approach: the "far" layer is simply a duller recording, and blending against a pre-filtered version
of the same material is cheaper than a runtime filter. **[U]**

---

## 4. Confirming actions through sound alone

### Auditory icons, not earcons — and it is not close

Gaver's auditory icons are *"caricatures of naturally occurring sounds"* with a real pre-existing
sound-meaning relationship; Blattner's earcons are abstract and their symbolic mappings *"still
require considerable learning by the user"*. **[V]**

Dingler et al. (ICAD 2008) measured the gap **[all V]**:

- **Spearcons and speech: mean 1.14 training cycles** to 100% accuracy (SD 0.378); aggregate
  accuracy **99.64%**.
- **Earcons: mean 8.50 cycles** (SD 4.087); significantly worse accuracy than every other type.
- Auditory icons and hybrids in between.
- MANOVA F(10,64)=9.66, p<.001; training cycles F(5,33)=10.77, p<.001; accuracy F(5,33)=20.15, p<.001.

**Roughly 7.5× the learning cost.** And the diegetic argument compounds it: an earcon is by
definition a non-diegetic UI sound, and a synthetic beep in a creek breaks the fiction that the
visitor is a fish — which is the whole artwork. **[U]**

If an abstract cue is ever needed: **timbre and rhythm carry identity; pitch and register must never
be the sole differentiator**, and musical timbres with multiple harmonics beat simple tones. **[V —
Brewster et al.]**

### Three rules from the audio-game canon

Sources: *Papa Sangre*, *The Nightjar*, *A Blind Legend*. **[V]**

1. **Every action gets an immediate, distinct, *invariant* confirmation.** *Invariant* is the
   underrated word: **randomise ambience, never feedback.** A randomised confirmation stops
   functioning as a confirmation. **[V for the principle; U for the emphasis]**
2. **Confirmation and consequence are two different sounds.** Merge them and the visitor cannot
   distinguish a missed input from a failed outcome. **[U]**
3. **A diegetic guide voice is legitimate** — *A Blind Legend* delivers orientation in-fiction
   through the daughter character. **[V]**

### Proposals for the five moments **[all U]**

| Event | Sound | Why it works with no legend |
|---|---|---|
| **Took an insect** | wet surface-break snap + one grain of the shoal texture briefly rising in pitch | The snap is causal — a fish taking something at the surface is a sound everyone knows. The pitch blip on *your own* voice marks it as yours, not the world's. |
| **Jumped** | **the whole field goes dry and bright** (reverb send → 0, LPF opens), then re-enters with a splash | Leaving the water is a **change of medium**, not an event. Change the world, not one object. The most legible cue available, and it needs no learning at all. |
| **Spawned** | a widening — decorrelation and grain density increase over ~2 s, plus a low sub-bass swell | Uses the same dimensions as the loss cue in reverse, so it *teaches* the shoal-size mapping by contrast before the bear ever appears. |
| **Bear took your fish** | field silence (~400 ms) → one low impact → shoal density drops and narrows | Silence *before* is the danger cue; the density drop is the consequence. Two sounds, two meanings. |
| **Entering or leaving a zone** | **nothing discrete at all** | A "you entered" chime is the one thing that would make this feel like an app rather than a place. |

**Make danger subtractive.** In a real soundscape the strongest danger cue is subtraction — when a
predator arrives, the biophony stops. Dropping the field as the bear approaches should read as
dread more reliably than adding a scary sound, and it simultaneously clears the masking problem for
the bear itself. **[U — but the strongest single recommendation in the report]**

**Haptics twice, not five times.** Core Haptics is *"designed for low latency and real-time
modulation"*, and AHAP files can play audio with guaranteed audio/haptic synchronisation. **[V]**
`Apple.CoreHaptics` ships alongside `Apple.PHASE` in the same plugin set. **[V]** Use haptics only
where the body is implicated — the jump and the bear — and fire them as a **single AHAP** rather
than two players, so sync is guaranteed. **[U]**

---

## 5. The shoal

### We cannot granulate in real time through PHASE

There is no push-stream in the C# surface (§2). So **pre-render density states and blend**:

Bake roughly seven seamless shoal beds — say 200, 140, 100, 70, 45, 25, 10 fish — into a blend node
driven by a `shoalSize` number metaparameter, shaped through a mapped-metaparameter envelope.
Runtime cost is two sampler voices plus a crossfade. **[U — proposal; the API affordances are V]**

**The critical detail: keep the underlying grain source and RNG seed identical across renders**, so
the beds are *the same cloud at different densities*. Otherwise the transition sounds like a
crossfade between two different recordings rather than like loss.

Render the shoal on the **ambient mixer** (`PHASEAmbientMixerDefinition`), not the spatial mixer —
head-relative, externalised, no distance modelling, which is correct for a source at distance zero.
Apple's own example for this mixer is *"a background of crickets chirping in a large forest"*. **[V]**

### Making "your school is now smaller" audible

Three cues used together **[all U]**:

1. **Density drops** — fewer grains per second. This is what encodes number.
2. **Spatial extent narrows** — less decorrelation. Perceptually a *shrinking*, not a quieting.
3. **Individuation increases** — counter-intuitively, a smaller group is *more* legible as
   individuals. Lengthen grains, reduce overlap so single fish poke out. **This is the cue that
   makes people say "there are fewer of them" rather than "it got quieter."**

**Timing: fast on the loss, slow on the settle.** A ~300 ms abrupt drop at the moment of donation so
it is unmistakably *caused by the bear*, then a 5–10 s settle. Instant-and-permanent reads as a bug;
slow-and-gradual reads as unrelated to the bear.

---

## 6. Composing with the real creek

Cardiff's walks layer binaural recordings over the actual location so recorded events feel *"present
in the actual environment"*. **[V]** Four consequences **[all U, built on V material]**:

1. **Do not synthesise creek water.** The visitor is standing beside real creek water; a synthetic
   version loses that comparison and takes the credibility of everything else with it. Use the real
   water as carrier and add only what it lacks — sub-bass below where the real riffle has energy,
   narrow resonances that read as "underneath". **We are EQ'ing reality.**
2. **Site-survey before composing.** Record all six anchor points at the times the piece will run,
   then allocate the §3a niches into the gaps that are *actually there*. The acoustic niche
   hypothesis used as a production method rather than a metaphor.
3. **The real soundscape is a free reference axis.** Grond & Berger: *"just as the use of axes and
   tick marks in visual graphs provide a referential context, it is often essential to provide an
   auditory reference"* — but such context helps *"only when it adds information, and not just
   clutter"*. **[V]** The creek gives us a rich, non-cluttering one at no cost.
4. **Mix on site.** The −23 LUFS games target **[V as an industry recommendation]** assumes a quiet
   room and is wrong for a 47–67 dB(A) creek bank.

---

## 7. Transparency mode: safety and art coincide

McGill et al. (CHI 2020) ran participants over a ~400 m outdoor route covering green space, a road
with pavement, and a cobbled pedestrian street, comparing noise-cancelling headphones against
acoustically transparent headwear. **[all V]**

- **Just under half of participants believed their safety was compromised** by the noise-cancelling
  headphones.
- Isolation forced compensatory visual effort that damaged the experience itself. One participant:
  *"with the noise cancelling headphones it was a lot more difficult to become aware of the
  surroundings so I noticed that instead of focusing on the audio I was having to exert extra effort
  towards the outside… it made the audio experience less enjoyable because… it was kind of trying to
  suck me in, but I had to resist because… there are a few instances with all these cars around."*
- Conversely, acoustic transparency gave a **stronger** sense of being in an active, changing
  environment for dramatic content.
- Caveat: transparency leaks audio outward, and participants repeatedly raised concerns about being
  overheard. On a shared path, keep levels modest.

**Require transparency/passthrough and say so in onboarding.** The safe choice and the artistically
correct choice are the same one here.

### Head tracking

- AirPods Pro / 3 / Max support it; the plugin exposes `PHASESetListenerHeadTracking(bool)`
  directly. **[V]**
- **Without head tracking**, binaural still works but the dynamic cue that resolves **front-back
  confusion** is gone. Mitigate by never placing critical sources dead ahead or behind, and by
  rendering some cues as if from slightly *above* — the trick Microsoft Soundscape uses to give
  callouts the character of "audible signs". **[V for Soundscape; U for the mitigation]**
- **Personalized Spatial Audio** (ear/head scan) exists and is worth mentioning in onboarding, but
  cannot be required. **[V]**

---

## 8. Latency

### The numbers **[all V]**

| Threshold | Figure | Source |
|---|---|---|
| "Reacting instantaneously" | **0.1 s** | Miller 1968 via Nielsen |
| Flow of thought uninterrupted | 1.0 s | same |
| Action–sound: 0 ms vs 10 ms | **no significant quality difference** | Jack et al., *Music Perception* 36(1) |
| Action–sound: 10 ms **± 3 ms jitter**, and 20 ms | both **significantly worse** than 0 ms | same |
| Audio latency JND from 0 ms base | mean **49 ms** | Schmid et al., Audio Mostly 2024 |
| Human response time to a mobile stimulus | **320 ± 43 ms** best, **528 ± 105 ms** worst | arXiv 2305.17180 |
| iOS default session preprocessing | **~30 ms**; `.measurement` mode minimises it | Overloud / Superpowered |

**The most actionable finding: jitter hurts more than latency.** A steady 10 ms was
indistinguishable from zero; 10 ms ± 3 ms was rated significantly worse across six quality
measures. **Prioritise determinism over raw speed** — no allocations on the trigger path, no waiting
on the next Unity frame if avoidable. **[V for the finding; U for the prescription]**

### Budgets **[all U — derived from the verified thresholds]**

| Path | Budget | Rationale |
|---|---|---|
| Continuous field parameters | 50–100 ms, smoothed | Nobody localises the onset of a gradual change. Prefer auto-distance blend so it happens in the native mixer. |
| **On-device discrete confirmation** | **< 30 ms, jitter < 5 ms** | Well inside the 49 ms JND. Achievable on iOS — our code will blow this, not the OS. |
| Haptic with audio | ± 20 ms | Single AHAP, not two players. |
| **Web controller → phone** | < 200 ms target; ~1 s tolerable with local operator feedback | See below. |

### The controller across a network

The 30 ms budget exists because a tap and its sound must fuse *in the same nervous system*. Across a
network the presser and the hearer are different people, so nothing is fusing. **[U — but it
follows]** Measured WebSocket RTT is ~44 ms local and ~87 ms to cloud **[V]**; 4G adds 20–50 ms.
Realistic total is ~100–210 ms with a long tail — against a ~380 ms human reaction time, **human
variance dwarfs network variance.**

Four rules **[all U]**:

1. **Give the operator instant *local* feedback** (<100 ms in the controller UI) regardless of when
   the phone acts. Perceived responsiveness decouples from delivery.
2. **Never route a *visitor-initiated* action through the network.** Taking an insect must confirm
   on-device in <30 ms. Only operator- or world-initiated events may cross.
3. **Schedule, don't trigger.** Send "fire at timestamp T" with T 300–500 ms ahead against loosely
   synced clocks. This converts variable latency into fixed latency — exactly the trade the jitter
   finding says we want — and makes us robust to packet loss, since a message arriving with a past
   timestamp is dropped rather than fired late.
4. **Give every networked event a natural approach.** Three seconds of audible bear approach makes
   200 ms of jitter invisible.

---

## 9. Middleware concepts worth reimplementing in C#

Wwise and FMOD are ruled out as dependencies; this is vocabulary only.

**Worth building:**

- **Events vs Actions.** Gameplay raises named fiction-level events (`InsectTaken`, `BearTookFish`);
  an audio-facing layer decides what they do. One afternoon's abstraction, pays back continuously.
- **The Switch / State / RTPC distinction.** The distinction is *scope and type*, not function:
  Switches are discrete and per-object, States discrete and global, RTPCs continuous. **[V]** Rule:
  gradients are RTPCs, categories are Switches, whole-piece properties are States. **The commonest
  structural error is implementing something continuous — shoal shrinkage — as discrete buckets.**
  Maps cleanly onto PHASE: string metaparameter + switch node = Switch; number metaparameter +
  blend node = RTPC; States we write ourselves. **[V]**
- **Snapshots.** A C# struct of target mixer gains plus an interpolation time, applied on State
  change. This is how the bear encounter gets room to breathe.
- **Entry/exit cue *thinking*, not the system.** No bars or beats needed, but every long breathing
  sound should carry an editorial "safe to leave here" marker. **A stone cut off mid-decay sounds
  broken.**

**Not worth it at this scale [U]:** virtual voices and voice limiting (justified at hundreds of
emitters, not 12–20), full HDR audio (snapshots plus simple ducking gets ~90%), sequence containers
(drive a switch node's index instead — three lines), musical sync.

---

## 10. Could not establish

- **PHASE CPU cost, max simultaneous sources, voice-stealing behaviour — no published figures found
  anywhere.** Profile a full-density scene on the oldest target device in week one.
- Whether PHASE models distance-dependent air absorption separately from directivity's three
  subbands, and whether it applies near-field HRTF compensation — undocumented.
- Granular synthesis cost on current iPhones — the relative ordering is reasoning, not measurement.
- Whether `apple/unityplugins` `main` has changed since this reading. **Check the pinned package's
  `PHASEHelpers.cs` for `PushStream` and for custom distance curves specifically** — either would
  reopen §2 and §5.

### One reconciliation note

This report's §0.2 argued from raw GPS error and proposed decoupling beats from position. That
analysis predates the separate finding that **VPS is mandatory and lat/long never enters the trigger
path** (see `design-findings.md` §2). The audio conclusions are unaffected; only the positioning
premise changed. Its fallback suggestion — fire discrete beats from the controller while the
continuous field absorbs positional noise — remains a sound contingency if VPS underperforms.
