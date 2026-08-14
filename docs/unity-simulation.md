# Simulating the walk in Unity

Scrub the walk in the browser; watch and hear it happen in Unity. No phone, no creek.

**Scene: `app/Assets/ShoalingUpstream/Scenes/Simulation.unity`**

---

## For the artist: what to do

1. The service is running (`node service/src/server.mjs`) and the browser editor is open at
   `http://localhost:8710/`. If it is not, start it first — Unity asks it for the journey.
2. Open the `app` folder in **Unity 6000.3.21f1**.
3. Open **`Assets/ShoalingUpstream/Scenes/Simulation.unity`**. It is also first in File → Build
   Settings, so it is the scene that opens by default.
4. Press **Play**.
5. Go to the browser and **drag the scrubber**, or press the walk button.

Unity should follow within a frame or two.

### What you should see

* The **Game view** is the visitor's own view, at the height the centreline is authored at. It
  moves along the reach as you scrub, facing the way the path goes.
* An **inset map** in the bottom-right corner shows the whole reach from above: the blue line is
  the centreline, the white capsule is you, and the spheres are the beats.
* **Beat spheres change colour**: grey idle → amber armed → orange firing → green complete. A
  beat flashes white the instant it fires. Magenta means it is waiting for a gesture you cannot
  perform at a desk (see below).
* The **panel top-left** reads: link state, which journey loaded, the calibration gate, the audio
  renderer, your distance `s` along the creek, the current beat, and the last few events.
* You should **hear** it. The desk renderer plays through your laptop's normal output.

### If nothing moves

Read the top-left panel first — it is written to answer this.

* `link  Offline` — Unity cannot reach the service. Check it is running on port 8710.
* `no pose — scrub the browser` — Unity is connected but nothing is being scrubbed. Drag the
  scrubber; a single click may not move it.
* The `journey` line in **red** — see the next section. Unity is showing a reach you are not
  editing.

---

## Which journey it loads, and why that is a decision

**This scene loads your DRAFT, straight from the service.** The panel says so:
`draft r0 from the service — 16 points, 18.8 m`.

That is deliberate and it is not what the phone does. Three facts forced it:

* The app reads **published** revisions. The published revision for the garden creek is
  `r000001`, the old seeded layout: **3 points, 34 m, ending `barrier`/`headwater`**. Your draft
  is **16 points, 18.8 m, ending `falls`/`spawn`**.
* The packaged copy in `StreamingAssets` is that same `r1`. So is any cache. Every source a phone
  would consult has the old reach.
* Worse, `JourneyResolver` settles competing journeys by **taking the higher revision**, and a
  draft's revision is `0`. Offered alongside anything published, a draft is always refused as
  superseded. It is not a revision and cannot win that contest.

So the simulation scene asks for the draft and offers it to the resolver **alone**, skipping the
precedence rule on purpose. Everything else still applies: it has to parse, it has to be for this
site, and it has to pass the calibration gate.

**If the service is unreachable**, there is no draft to be had, and the scene falls back to what a
phone would load — the packaged `r1`. The panel turns that line red and says
`!! PUBLISHED r1 … this is NOT your draft`, because the difference is sixteen metres and four
beats and it would otherwise look like your work had silently changed.

Nothing here publishes anything. Publishing is your decision and revisions are immutable.

---

## What will not work at a desk, and is not broken

**Only the first beat completes on its own.** `tree` is a proximity beat: walk into it, wait out
the dwell, and it fires. Every other beat needs a gesture — `redd` and `spawn` want a crouch,
`strider` a catch, `heron` a give, `falls` a lift — and those come from the phone's accelerometer,
which a laptop does not have. Scrub past them and they will arm, commit, and then sit **magenta**
in `AwaitingAction` forever.

To carry on past them, open the controller at **`http://localhost:8710/control`** and press the
beat's button. That fires it in Unity and turns it green. The same page shows where Unity thinks
it is, live.

**A completed beat holds for 25 seconds** before the next one can arm (`minimumHoldSeconds`), so
the reach does not fire in quick succession. That is the artwork's pacing, not a stall.

**There is no scan in the Unity view.** The splat is not rendered here — the Game view is a line,
some spheres and a dark background. The scan lives in the browser editor; Unity is showing you
geometry, triggers and sound, not the creek.

**VPS does nothing.** The localization stack is in the scene and running with its surface set to
`Simulation`, but with no AR session it samples nothing and never reaches a fix. That is correct
desk behaviour, and it is why the pose channel from the browser exists at all.

**`Silence` and `Resume` on the controller are refused.** They come back as "did not apply"
rather than doing nothing quietly: the audio engine has no mute, so there is nothing honest for
those buttons to do yet.

**Every site is uncalibrated** (`editorFrame.calibrated: false`), so the calibration gate would
refuse this journey on a phone. Simulation is the only surface that runs it today. The panel shows
the gate's verdict so this is visible rather than assumed.

---

## For whoever maintains it

### The scene is built by a script, not by hand

`app/Assets/ShoalingUpstream/Editor/SimulationSceneBuilder.cs` constructs and saves the whole
scene. Re-running it **overwrites** `Simulation.unity`, so nothing should be hand-edited into the
saved asset — the next run would throw it away.

```bash
# rebuild the scene without opening the GUI
/Applications/Unity/6000.3.21f1/Unity.app/Contents/MacOS/Unity -batchmode -nographics -quit \
  -projectPath app \
  -executeMethod ShoalingUpstream.EditorTools.SimulationSceneBuilder.BuildFromCommandLine \
  -logFile "$PWD/logs/scene-build.log"
```

It is also on the menu bar: **Shoaling Upstream → Rebuild Simulation Scene**. The builder
registers the scene first in `EditorBuildSettings`.

A scene written by a script diffs as code and can be reviewed. One built by dragging in the GUI is
a YAML blob nobody reads, and cannot be produced at all by an agent with no GUI.

### What is in it

| object | components |
|---|---|
| `Main Camera` | `Camera`, `AudioListener` — the visitor's eye, and the listener the mix is judged from |
| `Directional Light` | so the markers are not silhouettes |
| `Shoaling Upstream` | `ControlLink`, `JourneyAudioRunner`, `VpsLocalizationRunner`, `SimulationDriver` |

`SimulationDriver` (`Scenes/SimulationDriver.cs`) is the glue: it loads the journey, opens the
gate, starts the audio, ticks `JourneyProgression` from whichever pose source is live, spawns the
markers, draws the panel, and implements `IControlEffects`/`IControlStatusSource` so the controller
page can drive it and watch it.

It lives outside `Runtime/` on purpose. Nothing in it should ship on a phone: the phone's entry
point localizes against VPS and has no business spawning a sphere at every beat.

`_surface` on `VpsLocalizationRunner` is private and serialized deliberately — it is the guard
against shipping provisional coordinates to somebody standing in a creek — so the builder sets it
through `SerializedObject`, which keeps it stored in the scene, visible in the Inspector, and
reviewable in the diff.

### The headless proof

`app/Assets/ShoalingUpstream/Tests/PlayMode/SimulationSceneTests.cs` loads **the scene** — not
hand-built objects — against a scratch service, streams poses from a real operator socket, and
asserts the walker moved and the beat completed. It exists because every other test in that folder
constructs its objects in code, which is precisely why they all passed for a week while the project
had no scene at all.

The fixture deliberately **never publishes**, so if the scene were reading published revisions it
would find none and fail.

```bash
/Applications/Unity/6000.3.21f1/Unity.app/Contents/MacOS/Unity -batchmode -nographics -runTests \
  -projectPath app -testPlatform PlayMode \
  -testResults "$PWD/logs/playmode-tests.xml" -logFile "$PWD/logs/unity-playmode.log"
```

See `docs/unity-integration.md` for the three connections these tests prove, and
`docs/unity-control.md` for why the pose channel is shaped the way it is.
