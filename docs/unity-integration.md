# Is Unity actually connected?

Yes. All three connections now work, and each one has been driven end to end against a live
service rather than a mock.

| | connection | verdict |
|---|---|---|
| 1 | editor walk simulation → Unity | **works** — after a fix. It was silently broken. |
| 2 | controller → Unity | **works,** including firing at `fireAtMs` rather than on arrival, expiry, and acks. |
| 3 | Unity → controller | **works,** end to end, including the operator page displaying it. |

Before this, the answer was "201 EditMode tests pass". Those drive a fake transport. They prove the
parser and the scheduler and say nothing about whether the two halves have ever spoken — which is
exactly how connection 1 stayed broken for a day with unit tests green on both sides of it.

---

## The bug that was here

**The editor streamed `{type:'pose', …}`. The Unity client only read
`{type:'command', action:'simulatePose', …}`. Neither end had ever seen the other's message.**

`ControlProtocol.Parse` had no `case "pose"`, so the frame fell to the default branch and was filed
as `ControlMessageKind.Unknown` — deliberately not an error, so an older phone in the field keeps
walking when the bus grows a message it does not know. The consequence was that a scrub in the
browser moved nothing in Unity, with no error, no log and nothing to notice.

Both sides were internally consistent and both were unit-tested. That is the whole lesson: the
tests that would have caught it are the ones that put the two halves on the same socket.

### The fix

`case "pose"` in `ControlProtocol.Parse`, and `ControlClient` applies it the moment it lands —
not scheduled, not queued, not acked. A pose is state rather than an instruction: latest wins,
no history, no receipt. The bus sends it with no `fireAtMs` at all, so there is no schedule to
honour even if one were wanted.

The `simulatePose` command path is untouched and still works. A phone in the field may still be
sent one, and removing it would have been a second break. `BothPoseCarriersDriveTheSameWalker`
pins that either carrier moves the same walker and that nothing downstream can tell them apart.

**The two payloads are not the same shape, and are deliberately not read by one shared reader.**
The streamed pose nests its point under `position: {x,y,z}`; the command's value is flat
`{s, x, y, z, headingRad}`. A reader permissive enough to take either shape from either message
would go on working the day one end changed — which is how the original mismatch survived. Two
readers, two tests, one of them (`TheTwoPoseLayoutsAreNotInterchangeable`) asserting that each
refuses the other's shape.

### One thing the service asks for that cannot be done

`control-bus.mjs:139` says the receiver estimates its clock offset from the pose's `sentAtMs`
"the same way it does for `pong`". It cannot: an offset estimate needs a round trip to halve, and a
one-way timestamp gives offset plus latency with no way to separate them. Feeding it into the
window would import exactly the error the minimum-round-trip filter exists to keep out. `sentAtMs`
is parsed and exposed on the message; it is not wired to the clock, and the heartbeat already
answers the question. The comment on the service side is the one that is wrong.

### Two consequences worth knowing about

**Two time bases.** A beat the device fires by crossing a trigger against a followed pose lands up
to a lead time before an operator's `fireBeat` aimed at the same moment, because the command is
scheduled and the pose is not. `docs/unity-control.md` §4 argues the other way — "Poses still
honour `fireAtMs` … One time base is worth the lag" — and that paragraph now describes the
`simulatePose` path only. It is someone else's doc; it needs a line, and I have not touched it.

**The pose's `slug` is not checked.** The editor says which site it is scrubbing and nothing in
`ControlClient` compares it to the site this build is walking, because nothing there knows that
slug. An operator authoring one creek while a device runs another would move the walker along the
wrong centreline. Not fixed: it needs a slug on the client, which is more runtime surface than this
task's scope allows.

---

## How this was tested

Nothing here mocks a transport.

**`app/Assets/ShoalingUpstream/Tests/PlayMode/`** — Unity play mode, the real
`WebSocketControlTransport`, a real `node service/src/server.mjs`, and a second real socket joined
to the same bus as an operator standing in for the browser. The operator half shares no code with
the client under test, so a passing test cannot be two halves agreeing on the same mistake.

```bash
/Applications/Unity/6000.3.21f1/Unity.app/Contents/MacOS/Unity -batchmode -nographics -runTests \
  -projectPath app -testPlatform PlayMode \
  -testResults "$PWD/logs/playmode-tests.xml" -logFile "$PWD/logs/unity-playmode.log"
```

**`test/unity-integration.e2e.test.mjs`** — the browser ends. Two socket tests that always run, and
two Playwright tests that are opt-in because they launch Chromium:

```bash
node --test test/unity-integration.e2e.test.mjs                    # sockets only
SHOALING_BROWSER=1 node --test test/unity-integration.e2e.test.mjs # + the two pages
```

### The suites are joined by recordings, not by agreement

Each side writes down what it really put on the wire, and the other side replays it:

* The Playwright run scrubs the real editor page and writes every frame a device received to
  `logs/editor-pose-frames.jsonl`, one per line. `TheRealEditorsOwnFrameMovesTheWalker` replays a
  line **verbatim** into Unity — no parse, no re-serialise, because a round trip through two of my
  own functions would only prove that they agree with each other.
* The PlayMode run writes the exact bytes `ControlProtocol.Status` produced to
  `logs/unity-status-frame.json`, and the Playwright test replays that into the real `/control`
  page.

Either recording missing makes its test skip with instructions, rather than inventing a frame and
proving nothing.

### Results

| suite | result |
|---|---|
| Unity EditMode | **213 / 213** — was 201, plus 12 new for the pose message |
| Unity PlayMode | **15 / 15** |
| `node --test` (whole repo) | **all green** — 186 at the time of writing, and growing as other work lands |
| Playwright, opt-in | **4 / 4** |

`EditorPoseStream_MovesTheSimulatedWalker` was written as the requirement, failed, and now passes
without having been edited.

### The service these tests use is not yours

Every fixture starts its own `node server.mjs` on its own port with its own copy of
`data/journeys`. Borrowing the running service would put a test device in the operator's window and
overwrite the bus's authoritative state — the numbers on somebody's screen would move.

---

## 1. Editor → Unity

`EditorPoseStream_MovesTheSimulatedWalker` sends the frame `editor/js/link.js` builds and
`ControlBus.streamPose` broadcasts, and the walker arrives at 9.4 m reporting `Simulated` origin and
`Precise` quality. This test was written to fail, failed, and now passes without being edited.

**The one that actually answers the artist's question** is
`AScrubbedWalkArmsAndCompletesTheRightBeat`. A pose landing at the right distance is necessary and
nowhere near sufficient; what is being asked is whether the walk rehearsed at the desk is the walk
performed at the creek. So it drives a real `JourneyProgression` from the followed pose, over the
real socket, along the real published centreline, walking at 1.2 m/s and streaming at the editor's
own twenty frames a second:

* `tree` (s = 3.97 m, 1.9 m enter radius, 1.2 s dwell, proximity) **arms, commits and completes**;
* every other beat stays `Idle` — they are metres away and nothing spuriously fires;
* the progression's own `S` matches the pose the operator scrubbed to within 5 cm, having arrived
  through the bus and the pose source.

It completes at about 3.5 m rather than at the marker, and that is correct rather than sloppy:
dwell accumulates from the moment the beat arms, so a visitor walking in at pace satisfies it
partway through the enter radius. The first version of this test asserted arrival at the marker and
failed — the test was wrong, not the code.

`JourneyProgression.Tick` takes a position and never learns where it came from, which is the whole
point of the `IPoseSource` seam. The trigger machine cannot tell it is being driven from a laptop.

`TheRealEditorsOwnFrameMovesTheWalker` closes the last gap: a frame the real editor page put on the
wire, replayed byte for byte, moves the walker to its `s`. Everything else in the fixture sends
frames the fixture composed, and a fixture can only be as right as whoever wrote it.

---

## 2. Controller → Unity

`AFiredBeatArrivesAtItsMomentAndIsAcked` presses `fireBeat heron` from an operator socket and
checks, against the bus's own `commandIssued` frame:

* the beat is applied within 60 ms *before* to 250 ms *after* its `fireAtMs`, measured in server
  time through the client's own clock estimate — it does not act on arrival;
* the ack comes back with `applied: true` and the right `commandId`.

`EveryOperatorActionReachesTheEffectsInOrder` presses `replayCurrent`, `advance`, `silence` and
`resume` 150 ms apart and gets all four, in order, each acked.

`ACommandThatSurfacesTooLateIsDroppedNotFired` produces expiry honestly: the client is simply not
pumped for eleven seconds, which is what a wifi stall does. The frame sits in the transport queue
the whole time; when it surfaces it is past its TTL, is dropped rather than fired, and is acked
`applied: false` so the operator is not left listening for a sound that is never coming.

**Two things could not be produced against a real bus and remain EditMode-only.** A stale
`sessionId` cannot arise — a reconnect adopts the new session from the welcome, so client and
command always agree unless the service restarts inside a window narrower than a test can open.
And `expiresAtMs` cannot be set by hand: the bus computes it, and `leadMs` is not configurable by
environment.

---

## 3. Unity → controller

`UnityStatusReachesTheOperatorsView` gives the client a status source reporting site, revision,
localization, tracking confidence, current beat, high-water mark, completed beats and shoal count,
and drives a pose so the report has an `s` to carry. All of it arrives in the bus's authoritative
state and reaches the operator, with `s` filled in from whichever pose source is live rather than
from the journey — so an operator watching a simulated walk sees the number the beats are actually
being fired against.

`ControlProtocol.Status` emits nothing outside the bus's allow-list. From the other end, the
Playwright test confirms every key Unity sent survived the merge, and that `/control` displays
distance, confidence and shoal count from Unity's own recorded frame.

---

## What the stale published revision cost

`data/journeys/ubc-nitobe-garden-creek` holds a draft that is **16 points, 18.8 m, ending
falls/spawn**. The published `r000001` is the superseded seeded layout — **3 points, 34.0 m, ending
barrier/headwater**. The app reads published revisions. A phone launched against the artist's
service today would walk the old creek, and none of the beats in the walk test above exist on it.

Nothing here published anything. Every fixture copies `data/journeys` to a temp directory and
publishes the draft *there*, so the app under test loads the current artwork and the artist's
revisions stay immutable. `ReportWhetherThePublishedRevisionMatchesTheDraft` logs the comparison on
every run and warns when they differ; it asserts nothing, because the day the artist publishes is
not a day a test should fail.

`TheAppLoadsTheCurrentArtworkFromALiveService` proves the real launch path works against that
scratch service: a real `UnityWebRequest` through `ServiceJourneyReader`, resolving r2, 16 points,
18.8 m, the current beats.

**`editorFrame.calibrated` is false**, and
`AnUncalibratedJourneyIsRefusedOnDeviceAndAllowedInSimulation` confirms the app behaves as designed:
refused with verdict `Uncalibrated` on device, allowed in simulation. That is correct, and it is
also the ceiling on all of this: **none of it is evidence about a phone.** It is evidence about the
desk — which is what the editor-follows-the-walk requirement is about — but nothing here has
localized against VPS, run on iOS, or made a sound through a speaker.

---

## Two smaller findings, neither fixed

Both are in runtime code outside this task's scope, and both are reported rather than touched.

**A stall that ruins the only clock sample lets a stale command fire.**
`AStallThatWrecksTheOnlyClockSampleLetsAStaleCommandFire` — the heartbeat goes out as the link comes
Online, its pong is still unread when an eleven-second stall begins, and the round trip finally
measured against it is the length of the stall. One sample, eleven seconds wide, uncertainty far
past the trust threshold. An untrusted estimate falls back to relative timing, which measures the
TTL from *arrival* — and nothing can be too late for a deadline that starts when it lands, so the
command fires eleven seconds after the button was pressed.

The window is narrow: a stall in the first seconds of a connection, before a second good sample lets
the minimum-round-trip filter throw the ruined one away. The fallback is deliberate and documented
in `docs/unity-control.md` §3. What is not documented is that expiry goes with it — that section
says an untrusted estimate "still expires", and it does not.

**Commands issued in the same millisecond can fire out of order.**
`CommandScheduler.cs:104` keeps the queue in fire order with `List.Sort`, which is introsort and
therefore unstable. The bus stamps `fireAtMs = now + leadMs`, so commands issued inside one
millisecond tie. A burst of four in the run that found this produced `advance` before
`replayCurrent`; the recorded `fireAtMs` values were `…570, …580, …580, …580`.

`CommandsIssuedInTheSameMillisecondTieInTheQueue` asserts the tie rather than the reordering, which
would be flaky. Narrow — an operator's fingers cannot tie, and `POST /api/control/command` is the
only thing that can be driven that fast — but `docs/unity-control.md` §4 promises the opposite:
"each one is a thing that happened, so they queue and fire in order". A stable insert or a sequence
tiebreaker would settle it.

---

## What is still untested

* **Anything on a phone.** No iOS build, no VPS localization, no AR session. The journey is
  uncalibrated and the app is designed to refuse it on device, which is correct and is also a wall.
* **Audio.** `IControlEffects` is driven through `RecordingEffects`, which records and refuses
  nothing. That a beat command reaches the effects interface is proven; that a sound comes out of
  the desk fallback is not. The walk test proves the trigger machine completes a beat — the thing
  downstream of that which actually makes noise has not been run.
* **The full reach.** The walk test completes the first beat. `minimumHoldSeconds` is 25, so
  walking all six beats is a two-and-a-half minute test; the ordering gates between them are
  unexercised end to end.
* **Gestures.** Every beat but `tree` needs an interaction (crouch, catch, give, lift) that arrives
  from `GestureDetector`, not from the bus. Nothing here supplies one.
* **Stale sessions and hand-set expiry**, for the reasons in §2 — EditMode-only, reasonably so.
* **Two devices at once,** and therefore the multi-device agreement the clock offset exists to buy.
* **Real network conditions.** Everything ran on loopback, where the round trip is under a
  millisecond and the clock estimate is trusted almost immediately. Park wifi is the case the
  robustness in `docs/unity-control.md` §5 was written for, and none of it was exercised.
