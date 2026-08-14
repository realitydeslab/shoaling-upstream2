# Design findings

Research delivered 2026-08-14. Claims marked **[V]** verified against a source, **[U]** uncertain
or derived. Two reports (Apple PHASE specifics, Unity test/simulation) are still outstanding.

---

## 1. The finding that reorganises everything: this piece is dwell, not transit

Six beats 8–12 m apart is 40–60 m of path — **about 40 seconds of walking at 1.35 m/s**. The site
diagonal is ~71 m, about 53 seconds to cross. A 15–30 minute work in that footprint is therefore
roughly **95% standing still**. **[U — arithmetic, but the arithmetic is not in doubt]**

Every pacing rule from the walking-tour literature — one beat per 100–150 m, 60–90 s of walking
between events, word budgets computed from distance — was derived for transit walks and **does not
transfer**. Duration is set almost entirely by dwell, so each beat must hold attention for
**45–90 seconds**. Walking segments are transitions, not content.

The correct precedent is not a walking tour. It is **Riot! 1831** (Reid/Hull, HP Labs): 34 regions
in a 150 m square, GPS-triggered audio, dense and dwell-heavy. Its authors expected people to stay
twenty minutes; they often stayed over an hour. Its documented failure modes — bitty interaction,
missed content, repetition, drift making content feel unrelated to place — are exactly this
project's risk set. **[V]**

Riot!'s most actionable finding: they added a continuous ambient bed because *"initial tests showed
that users thought that the technology had broken down if there was a long silence between
scripts"* — and it became the best-loved element. **[V]**

## 2. GPS cannot do this, at all

Under canopy expect CEP50 3–5 m, DRMS 5–7 m, with a tail to 30–100 m during dropouts. Riot!
measured **15 m of systematic afternoon drift** — larger than the entire beat spacing. Position
error is 30–90% of inter-beat distance. **[V]**

The industry position, stated plainly by STQRY: geo-tagging at **50 m or more**, Bluetooth beacons
at **2 m or less**. **There is no reliable consumer technology between ~2 m and ~50 m, and 8–12 m
sits in the middle of that dead zone.** **[V]**

Corroborating: Android geofencing guidance gives ~100–150 m minimum reliable outdoor radius; ARCore
Geospatial's default thresholds before reporting a localised state are 10 m horizontal and 15°
heading. Both exceed the whole site. **[V]**

Every published trigger radius is **bigger than our beat spacing**. Microsoft Soundscape's guided-tour
arrival is 12 m with 15 m in / 30 m out hysteresis. Copying those numbers would leave all six zones
permanently armed. **[V]**

**Consequence: VPS or on-device Device Mapping is not an enhancement, it is the only thing that
makes 8–12 m spacing work.** Lat/long must never enter the trigger path; everything runs in the VPS
anchor's local frame.

Unresolved tension worth naming: Niantic's own VPS Best Practices warns against exactly this site
type — seasonal foliage, repeated structures, *"avoid localizing based on city features that might
change… such as tree placement"* — and every VPS recovery instruction requires the phone held up at
the face, which contradicts an eyes-free brief. **[V]** NSDK **Device Mapping** (fully on-device, no
network, works from our own scan) may be the better fit, degrading when lighting/weather/time differ
between mapping and localisation. **[V]**

**Fallback if VPS proves unworkable:** TaleBlazer's *indoor region* model — GPS off, beats triggered
by tap or by proximity to a physical anchor. At this scale that is a legitimate architecture, not a
degradation. **[V that the pattern exists]**

## 3. Niantic's published guidance mostly does not apply at this scale

- The "20 m minimum POI separation" rule is **folklore**. None of the four Wayfarer criteria
  articles contains any distance rule; 20 m is an Ingress game-side inclusion rule. **[V]**
- What actually governs density is the **S2 Level 17 cell** (avg 4,948 m², ~70 m a side), one
  PokéStop per cell. Our 2,475 m² site is **half a cell** — Niantic would place *one* beat where we
  want six. Their 80 m interaction radius would cover the site twice over. **[V]**
- The transferable principle is only this: Niantic chose **generous, deliberately overlapping**
  trigger zones (80 m radius against 70 m cells) over precise ones, because forgiving beats accurate
  when positioning is noisy. **[V]**
- Minimum VPS playable area is **25 m²**; our site is ~99× that. **[V]**
- Niantic's own stated position is that **thematic congruence between fiction and site is a
  first-order design lever** — *"a game themed around Water-type Pokemon will make more sense to
  players if they play it near a body of water."* **[V]**
- **Niantic publishes no session-length, thermal or battery guidance.** For a 15–30 minute
  continuous outdoor iOS session with the camera up for VPS, this is the **largest unquantified risk
  in the project** and must be characterised empirically on target hardware. **[V — verified absence]**

### Heading is a real problem

Niantic World Pose accuracy: position median 4 m / p90 11 m; heading median 3° / p90 11°, against
raw device compass p90 of **24°**. A 24° error puts a "swim this way" cue on the wrong side of the
listener. **[V]**

Worse: Soundscape's approach falls back to GPS *course* for orientation, discarding it below
0.4 m/s or after 3 s stale. **Because our visitor is stationary ~95% of the time, course is almost
never valid.** So orientation is head-tracked AirPods or magnetometer, with no fallback. **[V for
the Soundscape thresholds; U for the consequence]**

## 4. Trigger architecture

### Collapse the site to one dimension

Project the localised position onto the creek centreline and work in a single along-stream
coordinate **s ∈ [0, 55 m]**, beats at s ≈ 0, 11, 22, 33, 44, 55. Lateral offset then never triggers
anything — it only modulates the mix as distance-from-water. A *gate* perpendicular to the creek
axis discards perpendicular noise entirely, leaving only along-stream noise to debounce, and makes
monotonic-progress logic trivial since s is exactly the quantity that should only increase. **[U —
derivation, not a sourced pattern, but it is the cleanest idea in the whole research set]**

### Parameters, scaled to 11 m spacing **[U — arithmetic]**

| | |
|---|---|
| Enter band | beat ± 2.5 m along s |
| Exit band | beat ± 4.0 m along s (1.6× enter) |
| Guard gap between adjacent beats | ~3 m |
| Commit dwell | 1.0–1.5 s (*not* the usual 2–3 s) |
| **Minimum hold** | **20–30 s, cannot exit regardless of position** |

Commit dwell must be short because at ~0.7 m/s a visitor crosses a 5 m band in ~7 s; a 3 s dwell
eats half of it. **Minimum-hold is the real anti-thrash mechanism at this scale** — with zones 11 m
apart, hysteresis alone is insufficient. It converts "am I in the zone?" into "which beat am I
performing?", which is the question that matters.

The sourced rule behind hysteresis: enter/exit separation must be at least the position error or you
get fire/silence cycling. Documented failure mode — *"the phone would vibrate then go silent
repeatedly"* — fixed by requiring stable position for a few seconds. **[V]**

### One state variable, not six geofences

Keep a single `currentBeat` plus `highWaterMark`, updated by winner-take-all over s. Six independent
enter/exit monitors will interleave and double-fire near boundaries; one state machine cannot. **[U]**

### Gate on localization quality, not distance

Niantic's own sample does this: it points a directional arrow at the target while `Coarse` and only
treats placement as trustworthy at `Precise`. A distance check computed against an unreliable pose
is meaningless. **[V — read from `VPS2LocalizeDemo.cs`]**

### Handle the two failure modes as content

- **Backtracking**: never re-fire a passed beat. Below the high-water mark, play a distinct
  downstream state (drift, current resistance, shoal carried back) — diegetically correct, and it
  prevents replay. **[U]**
- **Standing still**: make it the rest state. Station-holding behind an obstruction is a real
  lateral-line-guided fish behaviour, so a stopped visitor hears the shoal working to hold position
  with slow fatigue. This converts the commonest locative failure into content. **[U, resting on a
  V fact]**
- **Localisation failure**: make it diegetic. When VPS confidence drops, don't show an error —
  **muddy the water and thin the shoal**. Turbidity is free diegetic cover for positional
  uncertainty in a creek. This is seamful design in Chalmers' sense; online players of Blast
  Theory's *Can You See Me Now?* came to treat GPS uncertainty as a designed feature. **[V for the
  concept, U for the application]**

### Never let audio start from silence

Halsey Burgund: discrete clips triggering out of nothing *"can seem out of place and jarring."* He
runs a continuous base layer with location material mixed on top and continuously re-mixed, rather
than firing events. **[V]** At 11 m spacing this is not merely aesthetic — a mistimed *mix change*
is a wobble, whereas a mistimed *sound effect* is an audible bug. **[U]**

---

## 5. Prior art we should actually copy

### Microsoft Soundscape — MIT-licensed, and almost exactly this project

[github.com/microsoft/soundscape](https://github.com/microsoft/soundscape), `svcs/soundscape-authoring`:
a **Django + React browser waypoint editor that publishes to an iOS app**, for eyes-free spatial-audio
walks. Read it before writing code. **[V]**

Schema:
```
Activity            type: Orienteering | GuidedTour
  └─ WaypointGroup  type: ordered | unordered | geofence
       └─ Waypoint  lat, lng, index, name, description,
                    arrival_callout, departure_callout
            └─ WaypointMedia  file, type, index
```

Six affordances worth taking:

1. **Ordered and unordered waypoints coexist as parallel groups in one activity.** This is the
   narrative-versus-free-roaming tension *resolved in the schema*: the story is a sequence, the
   texture is a set, both in one document. Our six beats are the ordered group; ambient creek detail
   is the unordered group. This directly answers an open question in our schema draft.
2. **`arrival_callout` and `departure_callout` as first-class per-waypoint fields** — approach and
   withdrawal asymmetry with zero machinery.
3. **No radius field at all** — radius is an app-side constant, deliberately, so authors can't tune
   a number they cannot perceive from a desk. **We must deviate here**: 8–12 m spacing demands
   per-beat tuning that a uniform 12 m cannot express.
4. **Publish artifact is GPX with a custom XML namespace.** Ordered waypoints become `<rte><rtept>`
   (routes are inherently ordered), unordered become `<wpt>` (waypoints are inherently a set), and a
   version element tells the client which reading to apply. It round-trips. Adopting GPX + namespace
   gets one format serving as authoring input, publish artifact, and test fixture.
5. **Publish state as a dirty flag propagated by ORM signals** — editing any nested media marks the
   whole activity dirty, so the author always knows whether the screen matches the walker's phone.
6. **It refuses to run on small screens.** Map-first authoring on a phone is a trap; they just block
   it.

### TaleBlazer — the best trigger UI for non-programmers

Its **Bump Settings dialog** exposes: how close to trigger; **whether agents can be re-bumped and how
far you must get away first** (hysteresis as two authored numbers, with the stated rationale *"to
prevent the agent from continuously popping up when the player is standing in one place"*);
**visibility radius independent of trigger radius**; and per-agent override of global defaults. **[V]**

TaleBlazer documents our exact failure case: *"for very small regions, the imprecise nature of the
GPS signal will be more readily apparent… the player icon will appear to jump around erratically."*
Their mitigations: use metres not game coordinates, increase the bump threshold, and *"consider
including a larger region than absolutely necessary."* **[V]**

Also worth knowing: **regions as layers over the same coordinates**, for *"different game levels,
time periods, or different outcomes"*, with an explicit `move to` transition. Given that Strawberry
Creek's story is about a lost run (§7), **a "then" region and a "now" region over identical creek
coordinates may be a better structure than six spatial beats.** **[V for the mechanism, U for the
application]**

### Echoes.xyz — the best per-element playback vocabulary

Five checkboxes that between them solve most of the free-roaming problem: **play loop**, **one-shot**,
**play complete** (*"always plays to the end, even if the listener leaves the echo"* — for
load-bearing beats), **resume** (*"resume playing where you left off when reentering"* — the
backtracking answer), **spatialization**. Plus fade in/out, max volume, **queueing with priority
1–10**, **voice groups** for priority ducking, and **3D positioning with rolloff curves and min/max
distances independent of the zone boundary** — that decoupling is exactly what is needed when zones
overlap. **[V]**

Constraint: *"only circles support spatialization"*, so a fully spatial piece means overlapping
circles by construction — which makes mixing and priority rules matter more than geometry. **[V]**

Platform ceiling: *"Most iOS devices will happily play 16 sounds at the same time."* Six overlapping
beats plus beds is within budget, but not by much. **[V]**

Their **"Lines"** feature is the affordance to copy first: import a real GPX recording of your own
walk to *"visualise the exact GPS coordinates your device is reporting"*, then place zones against
the **measured track, not the basemap**. **[V]**

### VoiceMap — the single best authoring idea found

VoiceMap **auto-calculates available talk time from the distance between markers** and shows the
author how many words fit before the next pin, capping at <750 words per location. **[V]**

Put a live budget readout next to every waypoint. **But their formula is distance-based and our
distances are ~10 m, so a naive port would budget about twelve words per beat.** We need the
dwell-based variant: budget against *expected dwell*, authored explicitly per beat rather than
derived from geometry. **[V for their system, U for our adaptation]**

### A warning about platform mortality

`arisgames.org` has lapsed and now serves a gambling site. Museum of London's Streetmuseum, the
landmark locative AR app, is no longer available. **[V]** An open, versioned, file-based publish
format is the strongest available hedge — a real argument for GPX + namespace over a proprietary
JSON.

### Basemap accuracy

Commercial satellite basemaps carry 1–5 m georegistration error. At 8–12 m beat spacing, **the
editor's basemap is itself a significant fraction of the spacing.** TaleBlazer instructs authors to
hand-edit a satellite capture into a high-contrast path map and re-upload. For us the splat *is* the
site-specific underlay, which is a genuine advantage of the chosen architecture. **[V for the error
figure and the TaleBlazer practice]**

---

## 6. Desk-side simulation

The honest baseline, from the HP Labs mediascape team after shipping real work: **"emulation tests
application logic but not the user experience."** Their failures were things a simulator structurally
cannot show — audio fades sounded different outdoors, people walked slower, the real space was
colder, wetter and noisier. **[V]**

And per the only rigorous cross-tool comparison found, **the two most capable locative authoring
tools (TaleBlazer, ARIS) have no simulation mode at all**, and none of the seven tools surveyed
supports in-situ authoring. The state of practice is worse than one would hope. **[V]**

Four approaches exist:

**(a) Tap-to-trigger in the shipping app.** TaleBlazer's tap-to-bump: *"important functionality for
the game designer to be able to test their game when not on location"*, password-protectable so
designers get it and players don't. Two-line feature; removes most content-iteration pain; **and it
doubles as the manual override we must ship anyway.** **[V]**

**(b) GPX track playback as a synthetic location provider.** Soundscape ships `GPXSimulator.swift`:
walks trackpoints, feeds a `LocationProviderDelegate` in place of CoreLocation, synthesises course
from bearing between points, synthesises speed and timestamps, supports pause, runs in background
exactly like production, and **reads configuration keywords from the GPX metadata itself** so the
test route is one self-describing artifact. It has an optional **`AudioConfiguration`** that plays a
background recording alongside the simulated walk — i.e. **play the field recording of the actual
creek while simulated position moves through it.** **[V]**

Paired with their `GPXTracker`: walk the creek once with the app recording GPX, then replay that
exact track at the desk with the real audio engine running.

**Important**: replaying a *clean* track proves nothing, because the whole risk is jitter. Record
several real tracks under different canopy, season and time conditions and replay the messy ones —
or synthesise noise onto a clean track at the measured error and check whether beats still fire in
order. **[V for the tooling, U for the advice]**

**(c) Xcode GPX simulation in tests** — real, but *"the location changes only for code running in the
test bundle"*; UI automation tests need `XCUIDevice.shared.location`. Good for CI, not for design. **[V]**

**(d) NSDK Playback** — records a real AR session (camera frames + location) to a `.tgz`, replayable
in the Unity Editor. Datasets save every minute; portrait-only; on-device playback needs datasets in
`StreamingAssets`. Niantic name this as a stated deployment principle alongside *"scout locations
across different times and lighting conditions"*. **Given that VPS is now mandatory, this is the
primary preview tool, not an optional one** — and the lighting-conditions instruction does real work
at a deciduous creek. **[V]**

### The preview idea worth stealing

Soundscape's **Street Preview** lets a blind user walk a route virtually before travelling it. The
world is a **graph of decision points and edges**, and `PreviewBehavior` is **generic over the graph
type** — the same machinery could walk our waypoint graph. You point the phone like a wand, haptics
fire as you cross each available bearing, you select an edge and travel it hearing the production
callouts, and geometry is resampled to **one step per second of walking** so it runs at real pace. It
maintains decision history so you can back out of a wrong turn. **[V]**

**The key architectural property: it reuses the production callout logic rather than
re-implementing it.** What you hear in preview is what you'd hear on site, minus the position error.
Swap only the location provider; leave everything downstream identical.

### The highest-value editor feature nobody has built

**An "armed zones at this position" scrubber**: drag a marker along the imported track; the editor
shows which zones are armed, what would be playing, and accumulated dwell budget. **None of the seven
tools surveyed has this.** With six overlapping zones at 8–12 m, the author cannot reason about
trigger behaviour from a map view alone. **[V that it is absent; U that it is highest-value]**

### Ship the manual override regardless

Two independent projects converged on this after hitting GPS reality. Notre-Dame *Whispers*
implemented *"a manual waypoint-unlocking feature… ensure that the narrative remains accessible even
when hardware limitations — such as GPS drift — interfere with the automated triggers"*, describing
the shift as moving *"from a fully automated, geolocated experience to a hybrid model allowing for
manual control."* Hidden Florence, built by the ex-HP-Labs team, added *"a Manual Mode where you
don't need to be in the location to listen to the audio."* **[V]**

**This independently validates the brief's requirement for an operator-carried controller.** It is
not a convenience; it is what the field forces on everyone who builds this.

---

## 7. The site's own story, and what it does to the design

All **[V]**, from UC Berkeley's own creek programme.

- Strawberry Creek supported **steelhead into the 1930s**; runs ceased around the **1920s** and the
  species was extirpated by urbanisation.
- The 1987 management plan on mechanism: culverts in the lower watershed are **"totally dark
  passages" that anadromous fish avoid** and are **complete barriers to upstream migration**;
  channelisation made baseflow shallower and faster; **"pools that provided rest areas were
  obliterated."**
- **Memorial Stadium** construction put a section of the south fork into a culvert.
- Restoration began **1987**; fish reintroduced **1989**; macroinvertebrate-inferred water quality
  moved from "poor" (1986) to "good" (1991).
- The species that returned — three-spined stickleback, Sacramento sucker, California roach — are
  **residents, not migrants**.
- The City daylighted ~200 feet in **1982**, among the earliest urban daylighting anywhere; UC
  Berkeley later daylighted a 900-foot stretch.
- A 2025 UCB study found re-population *"has not been as successful as planned"*, largely for want
  of **high-flow refuges**.

**The design consequence is the strongest single idea in this research: build the work around the
absence rather than simulating presence.** A shoal moving upstream through water where that journey
is no longer possible is more honest and more powerful than a restoration fable, and it dissolves the
didacticism problem before it starts. **The barrier-jump stops being a game verb and becomes the
literal thing that ended the run.** **[U — argument from V facts]**

### The structural gift

Bears carry salmon carcasses into riparian forest; isotopic analysis shows trees near spawning
streams derive roughly **22–24% of their foliar nitrogen from salmon**, and riparian stands there
grow about **three times faster** than controls. **[V]**

So **the shoal you donate to the bear becomes the tree you shelter under.** If the tree's
sonification audibly gains from what the bear took, the work states its ecology without a word of
exposition — and the visitor discovers it on a second walk. This connects beats 1 and 4 into a loop.
**[U — proposal from V facts]**

### A caution about the UBC site

The UBC Botanical Garden states **"there are no naturally occurring permanent waterbodies on campus
because of the nature of the soil"** — so the garden creek is very likely a **managed feature**, and
almost certainly not salmon-bearing. A 2014 SEEDS study of Rock Creek notes negligible summer
precipitation June–October, which constrains when it can be walked at all. **[V]**

The genuinely salmon-bearing water nearby is **Musqueam Creek**, the last salmon-bearing stream in
Vancouver (50–100 coho and 50–100 chum returning annually, up from six of each in 1996). **If a
salmon framing moves to Musqueam territory, Musqueam involvement is a design condition, not a later
consultation.** **[V for the facts, U for the condition — but it is not really arguable]**

---

## 8. Embodiment

### The tier problem, and which beat is weakest

A 2026 thematic analysis of animal embodiment in VR sorts the field into four tiers: **biomimicry**
(4% — the player physically performs the animal's movement), **limited simulation** (19%),
**hybrid** (25%), and **human behaviour with an animal avatar** (52%, where the animal form is
"primarily a visual shell"). About **77% of the corpus remains grounded in human-centred interaction
logic**. Convincing markers: movement coordinated beyond the tracked points, interaction methods
appropriate to the body, species-specific feedback. **[V]**

Squat-to-spawn and jump-the-barrier are **biomimicry — the top 4%**. **Tap-to-eat-a-mosquito is the
52% case and is the weakest of the six.** Proposed fix that preserves the beat: replace the tap with
a short **lunge-and-stop**, detectable from accelerometer and step cadence, which is what a trout
actually does taking an emerging insect. **[V for the tiers, U for the fix]**

And the spawn gesture can be made true: a female salmon digs the redd by turning **on her side and
flexing**, displacing gravel and producing a hollow up to **38 cm** deep, after which eggs and milt
are released simultaneously. **[V]** So: squat plus a single lateral rock of the phone — detectable,
and biologically correct. **[U]**

### Detection

Fused accelerometer + barometer classifies walking/running/stairs/jumping at reported accuracy
**above 95%**, and the barometer's **pressure derivative** is specifically the feature that separates
vertical from horizontal motion in a **user-independent** way — critical, since we cannot calibrate
per visitor in a park. Phone-IMU jump detection is validated against squat-jump and
countermovement-jump protocols. **[V]**

Two site-specific cautions **[U]**:
- **High-pass the barometer.** The 2.27 m total rise is within ordinary barometric drift over a
  ten-minute session, so absolute altitude is useless for progress. A squat is a ~0.4–0.5 m drop in
  under a second — a fast differential immune to slow drift. Use the barometer only for sub-second
  vertical deltas; take absolute altitude from the VPS anchor.
- **Tune for false negatives.** A missed squat costs a retry; a spurious squat spawns eggs while
  someone ties a shoelace.

Build the accessibility fallback now: a visitor who cannot squat or jump completes the beat by
dwelling, **with no acknowledgement anywhere that a substitution occurred**.

### Dignity

Rico & Brewster (CHI 2010): **location and audience**, not gesture ergonomics, drive willingness to
perform a gesture in public. **[V for the headline; UNCERTAIN — per-gesture rankings not obtained,
and no squat or jump appears in their set]**

So the lever is not "make the squat smaller" but "control who is watching and what they think is
happening." Using Reeves et al.'s four-way spectator taxonomy — *secretive*, *expressive*, *magical*,
*suspenseful* **[V]** — the assignment is **[U]**:

- **Squat to spawn — secretive.** Site it at the headwater where the path is narrowest and most
  screened. Never say "squat"; say *"get down low enough to see into the gravel."* Crouching at a
  creek edge is unremarkable, which is the point.
- **Jump the barrier — expressive, and routed to where jumping is already legible** (a stepping
  stone, a low wall, a culvert lip). A jump landing on an obvious object reads as ordinary; a jump on
  flat ground reads as a person doing something odd with a phone.
- **Feed the bear — suspenseful.**

General rule from Duncan Speakman's subtlemob practice: every individual action should be one a
person might plausibly be doing anyway. **[V]** Corollary: **if a beat fails the plausibility test,
resite it rather than redesign it.** On a 55 m site with a public path, siting is the main tool. **[U]**

### Being many

**No locative-AR precedent for embodying a collective was found.** Provisionally novel. **[UNCERTAIN
— may be a gap in searching]**

- **Attrition, not a counter.** Pikmin's attachment comes from dependence plus easy loss, with a
  deliberate point past which you cannot progress without losses. That is exactly the bear beat.
  Don't show a number going down — make the shoal **thinner in the mix**: fewer voices, narrower
  unison, audible gaps between individuals. Heard, not read. **[V for Pikmin, U for the application]**
- **Build "many" from one via granular decorrelation.** Slight per-channel differences plus grain
  spread yield perceived multiplicity from a single source; small spreads thicken like analogue
  unison, wide spreads read as a crowd. Expose three parameters to the state machine: **voice count**
  (population), **decorrelation width** (cohesion), **temporal spread** (agitation). Bear reduces
  count; tree reduces spread; barrier spikes it. **[V for the technique, U for the mapping]**
- **Boids with a fourth rule.** Reynolds' separation/alignment/cohesion plus a goal-seeking rule (as
  used for the *Batman Returns* bat swarms). The goal point is the visitor; how tightly the shoal
  tracks them communicates state with no HUD. **[V]**
- **Give the collective a felt body boundary.** Fish schooling is regulated by the mechanosensory
  lateral line: cutting the lateralis causes a large increase in neighbours at 90° bearing while
  blinding barely affects positioning, and with the lateral line inactivated fish **cannot shoal at
  all**. **[V]** So the convincing "you are many" cue is **not the sound of many fish but the sound of
  being surrounded at a specific radius.** Hold a ring of decorrelated near-field voices at fixed
  short distance in the binaural field; let obstacles deform it, so squeezing past the barrier
  audibly compresses the shoal against the visitor's head. **[U]**

  This is also the argument for sound-first: the lateral line is a **pressure** sense, so spatialised
  low-frequency cues are an analogue of fish perception rather than a metaphor for it. **[U from a V fact]**

---

## 9. Five precedents, one tactic each

1. **Cardiff & Bures Miller, audio walks** — she carries a binaural dummy head along the actual
   route so the recorded field is spatially logical for a body moving through that space. **Steal:**
   record the sonification sources in situ on a binaural rig, walking upstream at visitor pace. **[V]**
2. **Kaffe Matthews, Sonic Bikes** — the map is divided into zones, each triggering a sound on entry
   and something else on departure. **Steal:** compose in zones with explicit *depart* behaviour, and
   make them creek-shaped, not circles. **[V]**
3. **Hildegard Westerkamp, soundwalk practice** — opens with instructions that retune attention
   rather than direct movement: *"Listen to your feet. When you can hear your footsteps you are still
   in a human environment."* **Steal:** spend the first 60 seconds on instructed listening to the
   *real* creek before any AR sound. It calibrates the ear everything else depends on. **[V]**
4. **Rogers et al., Ambient Wood** — emitted non-speech audio representing *ecological processes*,
   expressly to provoke interpretation rather than tell. **Steal:** the tree, stones and insects
   should sound like what they *do*, never like a label being read. This is the anti-didacticism
   mechanism at the level of sound design. **[V]**
5. **Blast Theory, *Rider Spoke*** — a recording is audible only to someone who physically stops at
   the exact spot where it was made; scarcity is the affective payload. **Steal:** if persistence is
   added, eggs spawned at the headwater by one visitor should be findable by the next **only there**,
   with shoal audio carrying the accumulated count. Never make it browsable. **[V]**

---

## 10. The live controller — technical findings

### Library

**`endel/NativeWebSocket`**. Active (v2.0.7, 2026-08-07). **[V]** The key finding collapses the
choice: **v2.x is a thin wrapper around `System.Net.WebSockets.ClientWebSocket`** — so
"NativeWebSocket vs ClientWebSocket" is not a real decision, ClientWebSocket *does* work under
IL2CPP on iOS today, and the `PlatformNotSupportedException` folklore is out of date. **[V — read
from source]** Events auto-dispatch to the main thread via `SynchronizationContext`, which removes
the most tedious part of the job. Both iOS/IL2CPP issues in the tracker are closed. **[V]**

**Avoid `sta/websocket-sharp`**: 6,072 stars but **zero releases ever published**, a perpetual
master-branch refactor, with forks existing specifically to hand-patch it for iOS. **[V]**

### iOS

- **ATS does not block `ws://`.** ATS is enforced at the `NSURLSession`/CFNetwork layer only;
  `ClientWebSocket` opens a raw TCP socket and speaks RFC 6455 in managed code. Add
  `NSAllowsLocalNetworking` anyway as cheap insurance for any later `UnityWebRequest`. Never
  `NSAllowsArbitraryLoads`. **[V]**
- **The local-network prompt DOES fire** for outgoing TCP, per Apple TN3179, and the checks are
  *"implemented deep in the networking stack… this includes Network framework, BSD Sockets,
  URLSession, and any APIs implemented on top of those."* There is no Unity-shaped escape hatch.
  Needs `NSLocalNetworkUsageDescription`. **[V]**
- **The first connection attempt will fail by design** — the system *"may deny the operation
  immediately, before the user has responded to the alert."* Without retry logic, first launch on a
  clean install looks like a broken app. This is the most common cause of works-on-my-machine. **[V]**
- **The Simulator does not implement local network privacy at all** — success there proves nothing. **[V]**
- **Backgrounded + undetermined permission = silent denial**, and the decision is not recorded. Make
  the first connect happen in the foreground. **[V]**
- **Skip Bonjour/mDNS and UDP discovery** — needs the restricted multicast entitlement, a manual
  Apple request with ~3–5 workday turnaround. **[V]**
- **Backgrounding and screen lock kill the socket** (suspension, not backgrounding, is the operative
  distinction). But **the dead socket is the smaller half of the problem: ARKit suspends too**, so on
  resume tracking is lost and VPS must re-localize before anchored content is trustworthy. Design the
  resume path around AR recovery and let socket reconnection ride along. **[V for suspension, U for
  the framing]**

### The park problem

A Node server on the LAN with a QR handshake works only inside Wi-Fi coverage. A phone that walks out
of range falls back to cellular, at which point the server's private IP is **unroutable** — no
reconnection strategy fixes that, because there is no network path. **[U — engineering judgement]**
Three ways out:

- **Phone hotspot (invert the topology).** The phone provides the network; the operator's laptop
  joins it. As long as operator and visitor are physically together — which they are, by the brief —
  they stay on one subnet regardless of venue Wi-Fi. Zero infrastructure.
- **Public relay over `wss://`** (ngrok / Cloudflare Tunnel / small VPS). Works over cellular
  anywhere; costs an internet round trip and an outage dependency. Note this path is *not* local
  networking, so **the iOS local-network prompt and the ATS question both disappear**.
- **Tailscale or WireGuard** — the researcher's pick for real field use. Stable private IPs that
  survive the Wi-Fi→cellular handover; removes address discovery *and* network change in one move.
  Cost is a VPN profile on the phone.

**Architectural rule regardless: hold authoritative state on the server and push a full snapshot on
every reconnect**, rather than resuming a delta stream. That single decision makes network churn,
browser refresh, domain reload and screen-lock resume all the *same* code path. **Don't build HTTP
polling as a fallback** — it solves middlebox upgrade-blocking, which is not our failure mode, and
does nothing about an unroutable network. Keep only a small `GET /state` endpoint as a reachability
probe and resync hook.

### Address handshake

**QR code**: the server prints `ws://<lan-ip>:<port>` at startup and the app scans it. The camera is
already open for AR, so marginal cost is near zero and it survives DHCP changes. Plus manual IP entry
in a debug panel. **[U — judgement]**

### Prior art

**There is no open-source project doing "web editor drives a running Unity AR scene."** Verified
negative result. Two well-trodden halves — remote scene inspection, and web-based 3D editors — that
nobody has connected. Plan for bespoke work. **[V]**

Patterns worth stealing rather than depending on: **`gwaredd/unium`** (stale, 2022) exposes the live
scene graph as a queryable URL tree (`/q/scene/Player/Health`) — a good addressing scheme for our
commands; **`yasirkula/UnityIngameDebugConsole`** (very active) has a `[ConsoleMethod]` attribute →
auto-registered command with parsed arguments pattern that could be wired to WebSocket messages for
a remote command layer with almost no bespoke plumbing. **[V]**

### Waypoint schema prior art

**ARCore Geospatial Creator has the strongest waypoint model available**: POI as
`{stable id, lat, lng, alt, altitude-mode}` plus one explicit scene origin, with an editor altitude
override used only for visualisation. Worth copying the shape even though we won't ship ARCore. **[V]**

**Immersal's is a cautionary tale**: `public struct Savefile { public List<Vector3> positions; }` —
positions only, reconstructed **purely by list order**. No rotation, scale, per-item ID or type. That
collapses immediately under a web editor where items get added, deleted and retyped. Take the
round-trip *shape*, design our own schema with stable GUIDs, full TRS and a type discriminator from
day one. **[V]**

**8th Wall's hosted platform has been sunset**; the repo is now MIT and self-hostable. That
comparison is a closed question. **[V]**

---

## Still outstanding

Apple PHASE specifics (entitlements, metaparameter API, AVAudioSession and transparency mode, voice
limits) and Unity test/simulation (XR Simulation capability, batchmode invocations). Both requested;
neither delivered.
