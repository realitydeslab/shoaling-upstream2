# Site geometry — what is actually localizable

Measured 2026-08-13/14 from the Niantic portal API and the splat files themselves.
This is the document that constrains how long the walked journey can be.

## Strawberry Creek South (UC Berkeley) — the primary site

Org `Trout AR` `f35a6672-fc09-4c74-ad33-488c721850fb`,
site `187bbd62-4d21-480e-8f8d-e484754aa22b`, origin `37.870617, -122.266053`.

### The VPS-mapped footprint is ~55 × 45 m

The production mesh asset `4767ffbb-0ba9-420a-967b-bcd677ab90a5` has **26 nodes on a
regular ~8 m grid**. Converted to a local ENU frame about the site origin:

| | |
|---|---|
| East extent | −23.4 m … +31.1 m — **54.5 m** |
| North extent | −11.5 m … +33.9 m — **45.4 m** |
| Altitude | 57.91 m … 60.18 m — **2.27 m of rise** |
| Node heading | 227.4° (uniform across all nodes) |

The splat asset agrees: its 98% extent is 29.97 m × 13.32 m × 61.41 m, long axis in Z.

**This is the hard constraint on the piece.** Localized AR only works inside that
footprint. Six beats inside ~55 m means roughly 8–12 m between beats, which happens to
sit comfortably in the range walking-scale AR practice recommends — but it does mean the
journey is a short, dense reach, not a long hike.

### East is upstream

Altitude rises west → east across the mesh grid (57.9 → 60.2 m). Strawberry Creek flows
westward, so **the upstream direction is east, and it climbs about 2.3 m** over the mapped
reach. This gives a physically grounded axis for "progress upstream" that does not depend
on the participant's heading, and it is measurable from the anchor pose alone.

### The scans imply a longer route than the map covers

Eight scans exist in this org. In the local ENU frame:

| Scan | East (m) | North (m) | Distance from origin |
|---|---:|---:|---:|
| S2 | −4.8 | 21.4 | 22.0 |
| S1 | −5.7 | 37.6 | 38.1 |
| S3 | −20.3 | 122.0 | 123.7 |
| S4 | +71.8 | 124.4 | 143.6 |
| scan_…637171 | +55.8 | 127.7 | 139.4 |
| scan_…619352 | +590.0 | −64.4 | 593.5 |
| scan_…618889 | +655.7 | 112.0 | 665.2 |
| scan_…619139 | +696.9 | 104.8 | 704.8 |

The named sequence **S2 → S1 → S3 → S4 traces 194 m** of creek (16 m, then 86 m, then 92 m).
Three further scans sit **590–705 m east** — a separate reach entirely.

So there are three different lengths in play, and they are easy to confuse:

- **~55 m** — what is VPS-localizable today.
- **~194 m** — what the S1–S4 scan sequence walks.
- **~700 m** — the extent of all captured material along the creek.

The journey as designed has to live in the first number. Extending to the second or third
requires either new scans promoted to production, or several sites chained with a handover
between them. That is a scanning decision, not a software one, and it should be made
deliberately rather than discovered in the field.

## Garden Creek, UBC (Nitobe area) — the best-looking creek

Org `Reality Design Lab`, site `1a1df855-d2fc-4337-ae6f-c0c2277584b2`,
splat asset `ce4dd8c5-4b83-4081-8efb-b5d38b0a5f50`, centre `49.266644, -123.25991`.

Visually the strongest material of the three: mossy stones, running water over gravel,
dense green understory — it reads immediately as the creek the piece is about.

Two caveats:
1. The site record has **no `mapPoint`** set in the portal; the centre above comes from the
   splat asset's node GPS.
2. The splat asset is **not "Set to production"**. Runtime `AssetInfo.Deployment` would come
   back as `Unspecified` rather than `Production`, and the official sample filters on exactly
   that. Promote it before expecting device localization.

Its splat asset carries a starting pose, which the editor will need in order to place the
splat in the anchor frame:

```
rotation  w 0.63428313  x 0.06111304  y 0.26931247  z −0.72209483
translation  x −3.70003057  y 0.72191614  z −2.35591984
```

## UBC Trees — the rehearsal ground

Org `Elan's organization`, site `ed383e4c-34c7-48cd-b358-3e65377229ba`,
centre `49.264440, -123.256835`, mesh asset `03155c69-bfd6-43f4-84d8-95704c8f7c0a`
(root node `4016F5F600EC471897252C0AF186D4E9`).

Open lawn with mature broadleaf trees, no creek. Useful precisely because it is dull and
close to hand: it is the site for proving multi-site switching and for rehearsing the
tree-proximity beat without needing creek access.

## Consequences for the design

1. **Design for ~55 m, not 200 m.** Six beats at 8–12 m spacing.
2. **Use altitude as the upstream axis.** It is anchor-relative, robust to heading error,
   and physically honest: upstream really is uphill.
3. **Promote the UBC garden splat to production** before it can be used on device.
4. **Decide the multi-site story deliberately**: one 55 m reach done well, or a chain of
   reaches with an explicit handover. The former is achievable now; the latter needs scanning.
