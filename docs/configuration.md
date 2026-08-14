# Where configuration lives, and how the phone gets it

Three questions, answered in order: where the editor writes, how the app reads, and how to
change something while a visitor is walking.

---

## 1. Where it is saved

One file per site, on the machine running the service:

```
data/journeys/<slug>/draft.json                 the working document — what the editor edits
data/journeys/<slug>/revisions/r000001.json     published snapshots, append-only
data/journeys/<slug>/draft.<timestamp>.bak.json backups written before destructive tooling
```

Everything the editor authors is in `draft.json`:

| What | Where in the file |
|---|---|
| Walking path (the camera track, at 1.40 m chest height) | `site.centreline` |
| Points of interest | `beats[]` |
| Trigger geometry per beat | `beats[].trigger` |
| Audio, three distance layers plus a completion clip | `beats[].audio` |
| Trim box (display only — hides floater splats) | `editorFrame.trim` |
| Measured scan extent | `editorFrame.bounds` |
| Scan-to-journey transform, and whether it is trusted | `editorFrame.calibrated` |

The editor saves continuously as you drag, through **three separate endpoints** rather than one:

```
PUT /api/sites/:slug/editor-frame     the trim box alone
PUT /api/sites/:slug/site             the walking path alone (recomputes every beat's s)
PUT /api/sites/:slug/draft            the whole document
```

They are separate so a background save of one edit cannot push out a half-finished edit
somewhere else in the document. **Known limitation:** two editor tabs open on the same site
will both auto-save and overwrite each other.

`beats[].s` — distance along the path — is **derived**, never authored. Moving the path
recomputes every beat's `s` and moves no beat. Path and place are separate things.

---

## 2. How the app reads it

Saving does **not** change what the phone sees. Publishing does.

```
POST /api/sites/:slug/publish     freeze the current draft as the next numbered revision
GET  /api/sites/:slug/published   what the device fetches — always the newest revision
GET  /api/sites/:slug/revisions/:n   a specific earlier one
```

Revisions are immutable and monotonic. Once published, you can keep editing the draft all
afternoon and the phone keeps running the revision until someone presses Publish again — which
is the point: a visitor's walk is never altered by an editor window open on a laptop.

So the loop is:

```
edit in the browser  →  saved to draft.json continuously
press Publish        →  revision N written
phone fetches /published on launch  →  runs revision N
```

The phone reads over HTTP from the laptop on the same wifi. The service prints the LAN address
to connect to when it starts.

**A journey with `editorFrame.calibrated === false` is refused on device** and will only run in
simulation. That is deliberate: uncalibrated coordinates mean the beats are provisional, and
provisional coordinates in a real creek put a visitor in the wrong place.

---

## 3. Changing something in real time

Publishing is the wrong tool mid-walk — the app reads it at launch. For live changes there is a
**WebSocket control bus**, which is what the controller page (`/control`) drives.

```
ws://<laptop>:8710/ws?role=device       the phone
ws://<laptop>:8710/ws?role=operator     the controller page
```

**The phone reports upward** with a flat `status` message. The server merges an allow-list of
fields into authoritative state and fans it out to operators:

```json
{ "type": "status", "localization": "precise", "trackingConfidence": 0.92,
  "s": 9.4, "currentBeat": "strider", "shoalCount": 33 }
```

**The operator sends commands downward**, either over its socket or over HTTP:

```
POST /api/control/command    { "action": "fireBeat", "beatId": "falls" }
```

Every command is **scheduled, not fired**:

```json
{ "type": "command", "id": "<session>-7", "action": "fireBeat", "beatId": "falls",
  "issuedAtMs": …, "fireAtMs": …, "expiresAtMs": …, "sessionId": "…" }
```

`fireAtMs` is a moment in the near future that every device acts on, so two phones stay
together. A command that arrives after `expiresAtMs` is dropped rather than played late — a
sound arriving at the wrong place is worse than one that never arrives. `sessionId` scopes the
ids: acknowledgements from a previous service session are discarded rather than reported as a
success that never happened.

The operator gets `commandIssued` immediately so its interface can respond without waiting for
the phone, and `commandAck` when the phone reports it acted.

### What this is for

The piece runs automatically. The controller is the safety net for when an automatic trigger
does not fire — which is why it leads with *why*: localization state, distance along the reach,
what has already played. Two independent projects, Notre-Dame's *Whispers* and Hidden Florence,
arrived at the same feature after meeting GPS reality.

### What it cannot do

The control bus fires and adjusts; it does not re-author. Moving a beat, changing a trigger
radius or swapping a clip means editing the draft and publishing, and the phone picks that up on
its next load. If a beat is in the wrong place during a walk, the operator fires it manually and
it gets moved afterwards.

---

## Quick reference

```bash
node service/src/server.mjs          # editor at :8710/, controller at :8710/control
npm test                             # the whole suite
JOURNEY_DIR=/tmp/x PORT=0 node service/src/server.mjs   # throwaway instance
```
