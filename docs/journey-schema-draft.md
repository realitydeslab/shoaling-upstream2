# Journey manifest — draft contract

**Status: draft for review. Not implemented.** This is the one document the three components
(web editor, sync service, iOS app) must agree on. Everything else is free to change
independently, which is exactly why this needs to be settled before any of them is written.

## Why this shape

Three forces set the design:

1. **Waypoints live in the VPS anchor's local frame.** NSDK gives us
   `ARVps2Manager.TryTrackAnchor(payload) → ARVps2Anchor`, and the anchor is a `Transform`.
   Content parented to it is correct by construction. So a waypoint position is
   `(x, y, z)` in metres relative to a named anchor — never latitude/longitude, which would
   silently reintroduce GPS error we have already paid to eliminate.
2. **The journey is linear but the participant is not.** People stop, backtrack, and stand
   still. So the manifest stores an *order*, and the runtime decides what is armed; the
   manifest never encodes "what happens next" as a state machine the editor would have to
   simulate.
3. **The editor authors against a splat; the app runs against an anchor.** Those are two
   coordinate frames. The transform between them is site metadata, versioned separately,
   and must never be silently assumed to be identity.

## Sketch

```jsonc
{
  "schemaVersion": "2.0",
  "journeyId": "shoaling-upstream",
  "revision": 7,                      // monotonic; published revisions are immutable
  "title": "Shoaling Upstream",
  "siteRef": {
    "slug": "ucb-strawberry-creek-south",
    "nianticOrgId": "f35a6672-fc09-4c74-ad33-488c721850fb",
    "nianticSiteId": "187bbd62-4d21-480e-8f8d-e484754aa22b",
    "vpsAssetId": "…",                // the asset whose anchor payload this was authored against
    "anchorPayload": "…base64…",      // may be fetched at runtime instead; see note below
    "upstreamAxis": { "kind": "altitude", "risesToward": "east" }
  },
  "editorFrame": {                    // splat space -> anchor space. NOT identity.
    "splatFile": "ucb-strawberry-creek-south.proxy.spz",
    "calibrated": false,              // hard gate: false means coordinates are provisional
    "rotation": [0,0,0,1], "translation": [0,0,0], "scale": 1
  },
  "waypoints": [
    {
      "id": "w-headwater-spawn",
      "order": 0,
      "title": "Headwater — the gravel bed",
      "position": { "x": 2.4, "y": -0.3, "z": 41.8 },   // metres, anchor-local
      "interaction": "crouch",
      "trigger": {
        "enterRadiusM": 4.0,
        "exitRadiusM": 6.0,           // > enter: hysteresis, prevents trigger thrash
        "dwellSeconds": 1.5,
        "requiresPreviousComplete": true
      },
      "audio": {
        "approach":   { "clipId": "gravel-bed-bed", "loop": true,  "gainDb": -6 },
        "completion": { "clipId": "spawn",          "loop": false, "gainDb": -3 }
      },
      "phase": {                      // Apple PHASE spatialisation, per source
        "model": "geometricSpreading",
        "nearM": 1.0, "farM": 20.0,
        "directivity": "omni"
      }
    }
  ],
  "shoal": { "startingCount": 40, "minimumCount": 6 },
  "safety": { "short": "…", "full": "…" }
}
```

## Decisions embedded above, each of which is arguable

**`exitRadiusM` separate from `enterRadiusM`.** A single radius makes a waypoint flicker on
and off when the participant stands near the boundary — and VPS pose jitter guarantees they
will. Hysteresis is one field and removes a whole class of field bug.

**`requiresPreviousComplete` rather than a state machine.** Keeps the manifest declarative.
The runtime owns progression; the editor only has to render an ordered list.

**`editorFrame.calibrated` as an explicit boolean.** The single most dangerous failure mode
in this system is authoring coordinates in splat space and shipping them as anchor space. A
boolean that starts `false` and must be deliberately set makes that failure loud instead of
silent. The app should refuse to run an uncalibrated journey on device while happily running
it in simulation.

**`anchorPayload` may be omitted.** NSDK's runtime Sites API can fetch it
(`AssetInfo.VpsData.AnchorPayload`, and `RequestSiteAssetsByLocationAsync` can even pick the
site by where the participant is standing). Baking it in is faster and works offline; fetching
it survives asset re-promotion. Probably: bake it, record `vpsAssetId` beside it, and have the
app warn loudly if the site's current production asset ID no longer matches.

**`upstreamAxis`.** Because the mapped reach rises ~2.3 m west→east, "progress upstream" can
be derived from anchor-relative altitude rather than from a path polyline. Fewer authored
artefacts, and it degrades gracefully.

## The six interactions

The brief asks for six. Mapping them to trigger primitives:

| # | Beat | Interaction kind | Primitive |
|---|---|---|---|
| 1 | Approach a tree, shelter | `proximity` | enter radius + dwell |
| 2 | Crouch at the gravel bed, spawn | `crouch` | proximity + downward phone displacement |
| 3 | Tap to eat mosquitoes midstream | `catch` | proximity + N taps within a window |
| 4 | Donate part of the shoal to the bear | `give` | proximity + explicit confirm, mutates `shoal.count` |
| 5 | Jump the barrier to continue | `jump` | proximity + upward displacement + airtime |
| 6 | Ambient sonification of site elements | `ambient` | continuous distance field, never "completes" |

Only #3 (`catch`, a repeated-tap mini-mechanic) and #6 (`ambient`, a source that is always
audible and has no completion) are genuinely new primitives; the rest are variations on
proximity plus one gesture. #4 is the only one that mutates persistent state, which is what
makes the shoal shrinking audible later.

## Open questions for review

1. **Is `catch` worth its own primitive**, or is tapping mosquitoes better expressed as
   `proximity` plus a count threshold on a generic "gesture" field? More primitives means a
   clearer editor UI; fewer means less code.
2. **Does `ambient` belong in `waypoints` at all**, or is it a separate `soundscape[]` array?
   It has no order, no completion, and no gating — putting it in the same list as the six
   beats may confuse the editor UI more than it saves.
3. **Should `revision` be a monotonic integer or a content hash?** Integer is legible in a
   field notebook; a hash makes "did this device run exactly this content" checkable.
4. **How much of PHASE's per-source configuration belongs in the manifest** versus being a
   property of the clip in an audio catalogue? Putting it per-waypoint lets a designer tune
   one tree without touching another; putting it per-clip keeps the manifest small.
