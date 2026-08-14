# The Unity control client, and following a simulated walk

Two ways the app can know where the visitor is, and nothing downstream able to tell them apart.

```
VPS anchor pose ─┐
                 ├─→ IPoseSource ─→ JourneyProgression ─→ PHASE audio
browser scrubber ┘
```

That seam is the whole design. Everything else in `Runtime/Control/` exists to feed the second
branch safely.

---

## 1. Why there are two

The piece is performed by walking a creek with a phone that is localized by VPS. It is *made*
at a desk, with the browser editor's walk simulation scrubbing along the same centreline. The
artist's requirement is that these are the same run:

> "When I simulate on the web page, in the Unity editor you also need to see the same path
> simulated, and you can hear the sound effect from Unity."

If simulation went down its own code path, the thing rehearsed at the desk would not be the
thing performed at the creek, and the only place that difference would surface is standing in
the water. So the two are implementations of one interface, and the trigger machine, the audio
and the telemetry all take the interface.

| | on the creek | at the desk |
|---|---|---|
| class | `VpsPoseSource` | `SimulatedPoseSource` |
| fed by | whoever owns the AR session, via `Submit()` | `simulatePose` commands off the bus |
| arrives as | a point in the anchor's local frame | a distance `s` along the centreline |
| quality | whatever tracking reports | always `Precise` |
| freshness | 1.5 s | 2 s |

### Both halves of every sample

`PoseSample` carries the anchor-local point **and** `s`, even though either can be derived from
the other. That redundancy is the point: the two sources arrive with different halves. VPS gives
a point and knows nothing about `s`; the editor gives `s` and may not bother with a point. Each
source completes its own sample against the shared `CentrelineFrame`, so downstream always gets
a whole one.

`JourneyProgression.Tick` takes a position, `status` reports `s`. Neither has to ask where the
sample came from.

### Which one wins

`PoseSourceSwitch` in `Auto` mode: **simulation while it is live, VPS otherwise.**

At a desk there is no VPS pose, so simulation wins by default. On the creek nobody is scrubbing,
so VPS wins by default. The one case that needs an actual rule — a phone in the creek while
someone at the laptop drags the scrubber — resolves as *the operator wins while they are
dragging, and the device takes itself back about two seconds after they stop*. That is what an
operator expects from a control surface, and it needs no mode switch to get wrong under
pressure. `VpsOnly` and `SimulatedOnly` exist for builds that should never do the other thing.

Handover is abrupt, not blended. Cross-fading two positions on a line produces a walker moving
at a speed neither source reported, and dwell and hysteresis are calibrated against real walking
speed.

When the socket drops, the simulated pose is **cleared immediately** rather than left to expire:
an operator who has vanished is not still scrubbing.

---

## 2. The socket

```
ws://<laptop>:8710/ws?role=device
```

The role matters. An operator connection receives `commandIssued` where a device receives
`command`, and would never fire anything.

| direction | message |
|---|---|
| ← | `welcome` — carries the `sessionId` that scopes command ids |
| → | `hello {device, os, build}` |
| → | `heartbeat` → ← `pong {serverNowMs}` |
| → | `status` — **flat**, allow-listed keys only |
| ← | `command {id, action, beatId, value, issuedAtMs, fireAtMs, expiresAtMs, sessionId}` |
| → | `ack {commandId, applied, note, sessionId}` |

`status` must be flat. The server merges an allow-list of top-level keys and silently drops
everything else, so a nested payload arrives as no report at all — a test asserts we emit
nothing outside that list.

**Online means welcomed, not connected.** The client stays in `Connecting` until the `welcome`
arrives, because the session id it carries scopes every ack we are about to send, and a socket
that opens to something which never greets us is a socket to the wrong service.

### JSON

Hand-written, in `Json.cs`. `JsonUtility` cannot do this job: a command's `value` is polymorphic
— an object for `simulatePose`, a number or string or null for everything else — and
`JsonUtility` needs the shape declared up front. Parsing also has to fail by returning null
rather than throwing into a socket pump, so every entry point is a `Try`. Numbers are written
with invariant culture: a phone set to French would otherwise emit `9,4` and the service's
`JSON.parse` would reject the frame outright.

---

## 3. Clock offset

Commands carry absolute server timestamps and the bus schedules them 400 ms ahead. Two clocks on
a park's wifi, one of them a phone that has been asleep, are routinely further apart than that.
Guessing wrong one way plays everything late; the other way drops everything as expired. Neither
announces itself.

**The estimate.** Every heartbeat/pong pair is one sample. Sent at local `t0`, answered at local
`t1` carrying the server's `serverNowMs`:

```
rtt    = t1 - t0
offset = serverNowMs + rtt/2 - t1          (server clock minus local clock)
```

**The filter.** Keep the last eight samples and use the one with the **lowest round trip** —
never the mean. Park wifi is heavily right-tailed: most trips are quick and a few are wrecked by
a retransmit, and a long trip is also the one most likely to be asymmetric, so averaging imports
exactly the error the `rtt/2` assumption cannot handle. `UncertaintyMs` is half that best round
trip: the residual measurement cannot remove.

**A burst at connect.** The first five heartbeats go out 400 ms apart rather than 3 s apart, so
the offset is trusted within about two seconds of connecting instead of fifteen.

### When it is large

| situation | what happens |
|---|---|
| a jump larger than 500 ms | treated as a **step**, adopted whole, window discarded. Phones re-sync their clocks mid-session; smoothing across that leaves the device wrong in both directions for the length of the window. |
| uncertainty above 200 ms | the offset is **not trusted**. Half the bus's own lead — beyond that the timestamp is no longer saying anything the lead does not. |
| not trusted, or no samples yet | schedules fall back to **relative timing** (below) |
| a sample implying negative rtt | discarded |
| reconnect | `Reset()`. A new socket may be a different machine. |

**The relative fallback.** Absolute timestamps are unusable without an offset, but the
*durations* in the command are not: a lead of 400 ms and a TTL of 10 s mean the same thing on
any clock. So an untrusted estimate treats arrival as issue time. This loses the property that
two devices fire together, and keeps the one that matters more — a command still fires once,
near its intended moment, and still expires.

**`LocalClock`.** Unix milliseconds, so the protocol's timestamps mean something, but advanced
by a `Stopwatch` from a single epoch sample so an OS time step does not expire everything already
queued. iOS steps the wall clock when it picks up network time, often minutes after launch —
which on this walk is roughly when the phone reaches the park's wifi.

---

## 4. Scheduling and expiry

`CommandScheduler` holds commands until their moment and throws away the ones whose moment has
passed.

- **Fire at `fireAtMs`, not on arrival.** Acting on arrival converts the server's fixed lead back
  into variable network latency, which is the thing the lead exists to remove: steady latency is
  inaudible, jittery latency is not.
- **Drop past `expiresAtMs`,** both on arrival and while queued. A sound belongs to a place on the
  creek; by the time a delayed command surfaces the visitor has walked out of that place.
- **Expiry is acked** with `applied: false`. The operator pressed a button and needs to know it
  did not play, rather than listening for something that is never coming.
- **A command from another session is dropped and *not* acked.** The server discards such an ack
  anyway, and sending one would put a phantom success in the operator's log.

### Poses supersede; beats do not

A newer `simulatePose` removes every queued older one. A simulated pose stream is a stream of
positions, not a stream of events — after a wifi stall, a queue of them would replay the walk at
ten times speed and fire every beat along the way, when what the operator wants is where the
walker is now. Beats are the opposite: each one is a thing that happened, so they queue and fire
in order.

Poses are **not acked**. At scrub rate that would spend the socket on bookkeeping nobody reads.

Poses still honour `fireAtMs`, which costs the follow view the 400 ms lead. That is deliberate:
if poses applied immediately, a beat the device fires by crossing a trigger would land 400 ms
before an operator's `fireBeat` aimed at the same moment. One time base is worth the lag.

---

## 5. Robustness

This runs in a park on wifi that drops.

- **Nothing blocks.** `ControlClient.Pump()` drains a queue and returns. A dead service, a
  dropped socket and a laptop that was never switched on are the same to it: the piece keeps
  running on VPS, silently, which is what it does at the creek anyway.
- **Threads are contained to one file.** `WebSocketControlTransport` is the only thing that knows
  about `Task`. Received frames go into a `ConcurrentQueue` and the frame loop takes them out.
  Nothing touches a Unity API off the main thread — the standard way this component fails is a
  callback reaching an `AudioSource` from a pool thread and taking the app down in a park.
- **A socket that opens and never greets us is given up on** after 5 s. That is the wrong service
  on the right port, or a far end that died between the handshake and the welcome; waiting
  forever in `Connecting` is worse than starting again.
- **Three unanswered heartbeats replaces the link.** A park's wifi fails by staying associated and
  carrying nothing — the socket reports itself open the whole time, so only the missing pongs
  give it away. A device that *looks* connected while acting on nothing is the one failure the
  operator cannot diagnose from their end.
- **Backoff** is 500 ms doubling to a 10 s ceiling, with ±25 % jitter. The ceiling is low because
  the visitor walks back into range and the operator should not then wait a minute for their
  buttons. The jitter is because two phones on the same walk drop together when the access point
  does, and identical backoff would have them retry in lockstep forever.
- **A reconnect clears the queue and the clock.** The operator no longer knows what the device
  was about to do, and offsets measured against the old service describe a relationship that may
  no longer hold.
- **Backgrounding drops the socket** and reconnects on resume, so the return is a plain
  resync against a fresh snapshot rather than a minute of dead schedule replaying.
- **Close is `Abort()`, not a close handshake.** A handshake needs the far end to answer, and the
  case being handled is precisely the one where it will not.

---

## 6. Wiring it up

`ControlLink` is the MonoBehaviour, and it is thin on purpose — everything worth arguing about
lives in plain classes an EditMode test can drive.

```csharp
var link = gameObject.AddComponent<ControlLink>();
link.Host = "192.168.1.20";            // the service prints this on startup
link.SetCentreline(journey.site.centreline);
link.SetEffects(phaseAudioEngine);      // IControlEffects
link.SetStatusSource(journeyRuntime);   // IControlStatusSource

// on the creek, from whoever owns the AR session:
link.Vps.Submit(cameraInAnchorSpace, headingRad, quality, link.NowMs);

// everything downstream:
if (link.Poses.TryGetPose(link.NowMs, out var pose))
    progression.Tick(pose.AnchorLocalPosition, pose.Quality, Time.deltaTime);
```

### The two interfaces to implement

`IControlEffects` — "play this beat", and nothing more. Narrow in a particular direction: the
controller is a safety net for a trigger that did not fire, not a second authoring surface.
Moving a beat or swapping a clip means editing the draft and publishing. Every method returns
whether it actually did anything, because that answer is what goes back in the ack — an operator
seeing a button confirmed when nothing happened is worse than seeing it refused.

```csharp
bool FireBeat(string beatId);   bool ReplayCurrent();   bool Advance();
bool Silence();                 bool Resume();
```

`IControlStatusSource` — one `ControlStatus Snapshot()`. Fields left null are filled in by the
client from whichever pose source is live, so an operator watching a simulated walk sees the same
numbers the beats are being fired against.

### The `simulatePose` message

Assumed shape, as an ordinary operator command:

```json
{ "type": "command", "action": "simulatePose",
  "value": { "s": 27.5, "x": 0, "y": 0, "z": 27.5, "headingRad": 1.2 } }
```

Only one of `s` or the full `x`/`y`/`z` triple is required; whichever is missing is reconstructed
against the centreline. `headingRad` is optional and defaults to 0. An empty or wrong-shaped
`value` is refused rather than read as zero — reading a missing `s` as 0 would teleport the
visitor to the downstream end of the creek and fire the first beat.

Riding the command envelope rather than inventing a message type means poses get the bus's
scheduling, expiry and session scoping for free, and a pose that arrives late is exactly as wrong
as a beat that arrives late.

---

## 7. Tests

`app/Assets/ShoalingUpstream/Tests/EditMode/Control/` — no real sockets. The transport seam
exists so these can exist: frames go in by hand, come out into a list, and every timing question
is answered by a number the test chooses.

```
ControlProtocolTests    parse/serialise round trips, the status allow-list, malformed frames,
                        a device in a French locale
ServerClockTests        offset recovery, the minimum-rtt filter, steps vs drift, when to
                        distrust the estimate
CommandSchedulerTests   fireAtMs scheduling, expiry on arrival and while queued, stale
                        sessions, the relative fallback, pose superseding
ReconnectPolicyTests    growth, the ceiling, jitter bounds, reset on success
PoseSourceTests         each source completing its missing half, the switch handing over and
                        back, and the same walk driven both ways completing identical beats
ControlClientTests      hello after welcome, the heartbeat burst, a beat firing at its moment,
                        acks and their session, a dropped socket, a socket that opens and stays
                        silent, one that routes nowhere, rubbish on the wire
```

```bash
/Applications/Unity/6000.3.21f1/Unity.app/Contents/MacOS/Unity -batchmode -nographics -runTests \
  -projectPath app -testPlatform EditMode -testResults logs/control-tests.xml \
  -logFile logs/unity-control.log
```
