# VPS2 localization on device

How the phone works out where the visitor is along the creek, and — the part that took the
thinking — what it does when it cannot.

Code: `app/Assets/ShoalingUpstream/Runtime/Localization/`.
Tests: `app/Assets/ShoalingUpstream/Tests/EditMode/Localization/`.

---

## The shape

```
SitesClient / stored payload  ->  AnchorResolver  ->  base64 anchor payload
                                                          |
ARVps2Manager.TryTrackAnchor(payload)  ->  ARVps2Anchor    |
                                                          v
ARVps2Manager + ARKit  ->  Vps2VpsSource  ->  VpsSample  ->  VpsLocalizer
                                                                  |
                                        editorFrame^-1, Centreline.Project
                                                                  v
                                                    LocalizationFix { s, quality }
                                                                  |
                              JourneyProgression.Tick   <---------+---------> LocalizationReport
                                                                              (controller page)
```

Two assemblies, deliberately:

| Assembly | Contains | Depends on NSDK |
|---|---|---|
| `ShoalingUpstream.Localization` | the lifecycle, the maths, the degradation ladder, anchor selection | no |
| `ShoalingUpstream.Localization.Nsdk` | `Vps2VpsSource`, `SitesSiteAssetLookup`, `VpsLocalizationRunner` | yes |

The split is what makes the interesting half testable. NSDK's managers need an AR session, a
device, a network and a live OAuth token; none of the decisions worth arguing about are allowed
to live behind that. `IVpsSource` and `ISiteAssetLookup` are the seam, and everything on the
near side of it runs in EditMode against synthetic poses.

---

## Journey-space conversion

`editorFrame` is documented as **splat space -> anchor space** and is *not* identity once a site
has been calibrated. The journey is authored against the splat, so `site.centreline` and
`beats[].position` are in splat coordinates; the device tracks an anchor, so a camera pose
arrives in anchor coordinates. Every pose therefore has to be pulled the other way through the
transform before it means anything:

```
journey = rotation^-1 * (anchor - translation) / scale
```

`JourneyFrame.ToJourney` does this; `JourneyFrame.ToAnchor` does the authored direction, for
placing content against the anchor. Both are named rather than one being implied, because
getting the direction wrong is silent — every number stays finite, every beat still fires, and
the whole soundscape sits in the wrong place.

Then `Centreline.Project` (`Runtime/Journey/Centreline.cs`, unchanged) reduces the journey-space
position to a scalar `s` along the reach, plus a lateral offset that is reported and never gated
on. On a linear reach the cross-stream component is almost entirely pose noise.

> **Note for whoever owns `Runtime/Journey/`:** `JourneyProgression.Tick`'s first parameter is
> named `anchorLocalPosition`, but what it projects onto is `site.centreline`, which is in
> journey space. The name is a leftover from the draft schema, where waypoints were going to be
> anchor-local. The localizer feeds it `LocalizationFix.JourneyPosition`, which is correct; the
> name is the only thing wrong.

---

## The calibration refusal

`editorFrame.calibrated == false` means nobody has yet matched three physical points on site, so
the transform above is provisional. `VpsLocalizer.Gate` refuses such a journey on
`RuntimeSurface.Device` and permits it on `RuntimeSurface.Simulation`. A refused localizer is
terminal: `Tick` does nothing, no fix is ever published, and `JourneyProgression` therefore
completes nothing.

`RuntimeSurface` is a serialized field on `VpsLocalizationRunner`, not a platform define.
Somebody has to have chosen it. A build that quietly decided it was a simulation build is
exactly the failure this gate exists to prevent.

The rule is also in `service/src/journey-schema.mjs` and in the editor's calibration pill. It is
repeated here because this is the last place before a person is standing in a creek.

---

## Anchor resolution: two routes

`AnchorResolver` tries them in this order, and the order is operational rather than aesthetic.

**1. The stored payload — `site.anchorPayload`.** The payload the journey was authored against,
recorded beside `site.vpsAssetId`. It is the only route that guarantees the authored coordinates
and the anchor frame agree. It needs no network and no auth token and costs nothing. In a park
on a laptop hotspot those are not small advantages, and NSDK's tokens expire, so this has to be
the primary route for a field session.

**2. The runtime lookup — `SitesClient.RequestSiteAssetsByLocationAsync`.** Used when the stored
payload is absent or the SDK refuses it. Appropriate when the payload was never baked, or when
the asset has been re-promoted since and the baked payload is stale. It is also the only route
that can pick the site from where the visitor is standing, which is what multi-site switching
will be built on.

Selection, in preference order, filtered to `AssetDeploymentType.Production`:

| Match | Result |
|---|---|
| asset id == `site.vpsAssetId` | used, clean |
| same `nianticSiteId`, different asset | used, **flagged**: beats may be offset |
| nearest production asset anywhere | used, **flagged**: not the authored site, distance reported |
| candidates exist but none promoted | fails, and says "not Set to production" by name |
| no geolocation | fails, and says so — a different problem from an empty result |

The flag matters more than it looks. Re-promoting an asset can move the anchor origin under
authored beat positions, so localization succeeds *and the beats are still wrong* — the worst
combination available. `VpsLocalizer.AnchorFrameSuspect` stays set and every operator report
carries "Anchor asset does not match the authored one", because this is precisely the situation
where the operator should be watching for beats landing in the wrong place and firing them by
hand.

The "not Set to production" case is called out by name because it is the known blocker at the
UBC garden site (see `docs/devlog.md`). Reported as a generic empty result it would cost
somebody an afternoon on the wrong problem.

---

## Degradation

This is the part that matters. The governing fact is that **the visitor keeps walking whatever
the phone believes**, and three rules follow from it.

### 1. Never stop producing a position

The piece is a soundscape in a real creek through non-isolating headphones. Silence is not a
neutral state a visitor waits out; it reads as the work being over, or broken. So a dropout is
carried rather than surrendered to, and a total failure to localize still runs the walk.

### 2. Never claim more than is true

A carried position is labelled as carried, a frozen one as frozen, an unanchored walk as
unanchored, and the SDK's own `Vps2TrackingState` and `trackingConfidence` go out on every frame
unsmoothed. The controller is a safety net, and a safety net told a tidied-up story cannot be
used to decide whether to intervene.

`LocalizationReport` carries both: `RawState` and `RawConfidence` are what NSDK said,
`Fix.Quality` is what we act on, and `Fix.Source` / `Fix.Basis` say why they differ. `Detail` is
one plain sentence for a human reading it while walking beside a visitor.

### 3. Measured `s` may go anywhere; assumed `s` only goes upstream

The question in the brief was whether `s` should ever run backwards. The answer is that
**direction is the wrong axis to ratchet on — trust is.**

People genuinely walk back to look at something, and `JourneyProgression` already handles that
correctly: its high-water mark means a passed beat never replays. Clamping `s` monotonically
would instead freeze the audio at a place the visitor is not, and the creek is meant to get
quieter as they walk away from it. So a *measured* `s` follows the visitor freely, downstream
included. Odometry counts as measured — it is real motion in a drifting frame.

What must never run backwards is a position with no evidence under it. A blind dead-reckoned
estimate and an unanchored walk advance only, because drifting one downstream would silently
unwind progress that was never in question.

### The staleness ladder

How stale may a pose be before it is worse than no pose? Think in metres against a gate radius,
not in seconds. Enter radii are 2.5–4 m. A pose is worth acting on while its likely error is
inside that band and worth nothing outside it, because past that point it fires the wrong beat —
and a sound arriving in the wrong place is the one failure this piece cannot absorb.

| Age since last precise fix | What happens | Reported quality |
|---|---|---|
| ≤ 1.5 s | held exactly as it was | `Precise` |
| ≤ 20 s, ARKit tracking | carried on session odometry | `Precise`, confidence decaying |
| ≤ 5 s, no odometry | carried on a decayed speed estimate, upstream only, capped at 3 m | `Precise`, confidence decaying |
| past those, ≤ 30 s | **frozen**, and we stop claiming | `Coarse` |
| > 30 s | unanchored walk, or `Unavailable` if the fallback is off | — |

The derivations:

- **1.5 s hold.** About a metre of walking, inside the noise the gate hysteresis already
  absorbs. Reacting sooner means reacting to every ordinary gap between VPS requests.

- **20 s on odometry.** This is the good case and the reason `VpsSample` carries a session pose
  at all. VPS and ARKit fail independently: the phone routinely loses its fix against the site
  map while tracking its own motion perfectly well. `SessionToJourney` caches the rigid
  transform between the two frames at every precise fix, so a dropout is carried by pushing the
  current session pose through that cache. VIO drifts at roughly a percent of distance
  travelled — twenty seconds of walking is ~18 m, so ~0.2 m of drift, an order of magnitude
  inside the gate. The real limit is rotational drift, which is why this is twenty seconds and
  not two minutes.

- **5 s blind.** With no odometry there is nothing but the last measured speed. At 0.9 m/s
  decaying with a 3 s time constant, five seconds covers
  `0.9 x 3 x (1 - e^-5/3) = 2.2 m` — just inside the smallest gate, and no further. The decay
  matters as much as the horizon: the likeliest reason localization dropped is that the visitor
  stopped and turned to look at something, and holding the last speed through that would walk
  them to the headwater in forty-five seconds of blindness. Decaying to zero parks them roughly
  where they are, which is the low-error answer once the assumption underneath has broken. A
  hard 3 m cap sits on top so that no arithmetic and no bug can cover a beat spacing unobserved.

- **Freeze, then `Coarse`.** Past every horizon that can be justified in metres, we stop
  claiming. Reporting `Coarse` makes `JourneyProgression` hold whatever beat the visitor is
  standing in rather than tear it down — already its behaviour for a non-precise quality. A
  stale pose that keeps being sold as `Precise` is worse than no pose at all.

Why is bounded dead reckoning reported as `Precise` at all, when it is an estimate? Because
`JourneyProgression` fires nothing below `Precise`, and a beat firing two metres late is better
than a beat that never fires. The estimate is bounded precisely so that "two metres late" is the
worst case. `Fix.Source` and `Fix.Confidence` carry the truth for anything that wants to treat a
carried position more carefully — audio can reasonably decline to start a one-shot on a
low-confidence fix while keeping ambience alive.

### Relocalization jumps are taken, not rejected

A fix can arrive metres from where the estimate thought we were. The tempting move is outlier
rejection; it is the wrong trade here, because rejecting a jump means never recovering from a
drifted estimate, and the estimate is the thing with no evidence under it. So the correction is
always taken and its magnitude is published as `SnapMetres` instead — audio can crossfade rather
than click, and the operator can see that the estimate had drifted.

`s` is otherwise **not** smoothed. `Centreline.Project` already discards lateral noise, and the
hysteresis, dwell and minimum-hold in `JourneyProgression` are deliberately the smoothing layer.
A second smoother would fight them and add latency to every beat.

### What `Coarse` is for

`Vps2TrackingState` has three rungs, and the middle one has a real job. `Coarse` is
geolocation-grade; against a 2.5 m gate it would fire beats essentially at random, so it never
drives the trigger machine and is never promoted. What it does tell us is that the visitor has
arrived at the site — which is what the unanchored fallback needs to know before it starts, and
what makes the runtime anchor lookup possible. How many metres `Coarse` actually means is an
open question in `docs/nsdk-api-notes.md` and needs a field session.

### Never localizing at all

A visitor standing in a park with nothing happening is a failed artwork. So after
`FirstFixTimeoutSeconds` (45 s) with no precise fix — or immediately, if anchor resolution
failed outright, since VPS will never produce anything without a payload — the localizer enters
**unanchored mode**.

Unanchored mode advances `s` by the horizontal displacement of the AR session pose, scaled by
0.9 because some of any walk is wandering and turning. ARKit tracks the phone's own motion with
no VPS at all, so displacement is real even when position is not. Direction along the creek is
unknowable this way, so all displacement counts as progress — sound on a linear reach walked
upstream, and it errs only by however much the visitor wanders. The visitor stops, the piece
stops; they walk, it advances. Failing even that, it runs on a clock at 0.7 m/s: the piece as a
timed radio play.

Beats fire in unanchored mode. That is the whole point of it, and it is why every report says
`UNANCHORED` in capitals — the operator is walking alongside with a controller that can fire any
beat by hand, and needs to know that what they are hearing is not located. The mode is
abandoned the instant a real precise fix arrives, with the correction published as a snap.

Two policy switches exist for an unattended installation, both defaulting to the guided-walk
answer:

- `UnanchoredFallback` (default **true**) — turn off and the piece stays silent rather than
  running unlocated. Right when nobody is there to notice it has gone wrong.
- `RequireCoarseForUnanchored` (default **false**) — turn on and unanchored mode waits for
  geolocation-grade evidence that the visitor is at the site. Off by default because on a guided
  walk the operator already knows where the visitor is standing, and refusing to run because the
  phone could not confirm it would fail the artwork for the exact reason the fallback exists.

### `trackingConfidence` is reported, not thresholded

`MinimumTrackingConfidence` defaults to **0** — the check is off. NSDK documents the field only
as "positive number representing confidence"; its scale and distribution are undocumented. A
threshold against an unknown scale either does nothing or silently rejects every fix in the
field, and from inside the app there is no way to tell which. The raw value goes out on every
report so a session's worth can be read off the controller and this number set from evidence.

---

## Tuning

Every number lives on `LocalizationPolicy` with its derivation in the comment beside it. They
are defaults, not measurements. Real time-to-lock and real dropout length under tree canopy over
moving water are unknown, and `docs/nsdk-api-notes.md` names that as the project's dominant
risk. Expect the first field session to move `FirstFixTimeoutSeconds`, `OdometryHorizonSeconds`
and `MinimumTrackingConfidence`.

---

## What the operator sees

`LocalizationReport.Detail` is a single sentence, chosen to be readable while walking:

```
Precise, 9.4 m upstream, 0.6 m off the line.
VPS lost 7 s — carried on phone motion at 14.2 m.
VPS lost 2.3 s — estimated at 14.2 m from 0.9 m/s.
VPS lost 24 s — position frozen at 14.2 m, nothing new will fire.
UNANCHORED — no VPS. Following the walk on phone motion, 22.0 m in.
Refused: editorFrame.calibrated is false — the beat coordinates are provisional...
```

`StatusWord` maps `Fix.Quality` to the `localization` field of the WebSocket `status` message in
`docs/configuration.md` (`precise` / `coarse` / `unavailable`), and `RawConfidence` to
`trackingConfidence`. It is derived from the quality we *act* on, so the controller page and the
trigger machine can never disagree about what fired.

`VpsLocalizationRunner.ReportChanged` fires on a material change — mode, source, or a snap over
25 cm — rather than every frame. The WebSocket client is not written yet; when it is, it should
subscribe to that rather than poll.

---

## Testing

44 EditMode tests across three files — 5 on the frame transform, 9 on anchor resolution, 30 on
the localizer. The synthetic-walk pattern is taken from
`Tests/EditMode/JourneyProgressionTests.cs`: a visitor moving upstream at a plausible pace, with
the AR stack failing underneath them at known moments.

- `JourneyFrameTests` — the transform in both directions, against a fixture that is deliberately
  not its own inverse; malformed frames; the session-to-journey capture.
- `AnchorResolverTests` — route order, the network never touched when a stored payload works,
  every selection preference, and each failure reported as its own distinct problem.
- `VpsLocalizerTests` — the calibration gate, pose to `s` through a non-identity `editorFrame`,
  every rung of the staleness ladder, snap handling, both directions of the `s` ratchet rule,
  and two whole walks: one with a ten-metre VPS dropout mid-reach, one where VPS never works at
  all. Both must complete all six beats in order.

**What could not be tested without the SDK:** anything on the far side of the seam. That is
`Vps2VpsSource` — the mapping of `Vps2TrackingState` to our enum, reading the camera pose out of
`ARVps2Anchor.transform` and the session pose out of `XROrigin.TrackablesParent`, and the
`ARVps2Anchor.trackingState == Tracking` guard — and `SitesSiteAssetLookup`'s flattening of
`SiteAssetsInfo` / `AssetInfo`. All of it needs an AR session, a device, a network and a live
token. It is written to be as close to nothing as it can be for exactly that reason: no
thresholds, no smoothing, no holding, no decisions. Anything that could be wrong in a creek was
pushed to the near side of the seam.

Real time-to-lock, real dropout statistics, the metric meaning of `Coarse`, and the true scale
of `trackingConfidence` are all unmeasurable here and remain open.

```
/Applications/Unity/6000.3.21f1/Unity.app/Contents/MacOS/Unity -batchmode -nographics -runTests \
  -projectPath app -testPlatform EditMode -testResults logs/vps-tests.xml -logFile logs/unity-vps.log
```

Two traps in that command line. `-testResults` is resolved **relative to `-projectPath`**, so the
results land in `app/logs/`, not `logs/` — while `-logFile` is relative to the working directory
and lands where you expect. And a run will abort with "another Unity instance is running with
this project open" if anyone else has the project open; that failure is indistinguishable at the
shell from a real one, so check the message before believing a non-zero exit.
