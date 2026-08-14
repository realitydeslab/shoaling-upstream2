# How the app gets its journey and its sound

`docs/configuration.md` covers where the editor writes and what publishing means. This is the
other half: what the iOS app does with that at launch, and how the build gets packaged.

---

## 1. The three situations

They pull in different directions, and the design is the resolution of that.

| | Journey | Audio | Service |
|---|---|---|---|
| **Standalone** — Berkeley, TestFlight, no laptop | in the build | in the build | none |
| **Field test** — the artist's laptop on the same wifi | freshly published | in the build | reachable |
| **Desk** — no VPS, no creek | in the build | in the build | usually local |

So: **bundled configuration and audio are the baseline, a reachable service is an override, and
the last good fetch is cached.** Nothing about the standalone case is a degraded mode; it is the
normal one, and the other two are additions to it.

---

## 2. Precedence

`JourneyProvider` gathers up to three candidates at launch — the bundled journey, the cached
previous fetch, and `GET {host}/api/sites/{slug}/published` — and `JourneyResolver` decides:

> Among the candidates that parse, are for this site, and are allowed to run here, run the one
> with the **highest revision number**. Ties go to the bundled copy.

Revisions are immutable and monotonic per site — the service only appends — which is what makes
them comparable across sources at all. Two consequences are the point of it:

**A stale laptop cannot undo a build.** A laptop restored from an old copy of `data/journeys`
serves an older revision than the one in TestFlight. "Newest wins" keeps the build. "Network
wins" would walk the visitor through a layout that was superseded weeks ago, and nobody would
notice until the beats were in the wrong places.

**The cache cannot pin a session to the past.** It is another numbered candidate, so a fresher
build or a reachable service overtakes it with no invalidation step anywhere.

Ties go to bundled because the bundled journey is the only one whose audio is guaranteed to be
in the build beside it.

### Refusals

Two conditions disqualify a candidate outright rather than degrading it.

**Uncalibrated is refused on device.** `editorFrame.calibrated == false` means the coordinates
were authored in splat space and have never been matched to three physical points, so against a
real VPS anchor every beat is in the wrong place — the failure this system is most able to hide.
It runs in simulation, which is where provisional coordinates belong. *Both sites in the repo
are uncalibrated today, so on a phone, today, nothing runs.* That is the intended behaviour and
it is the loudest available way of saying that calibration has not happened yet.

**An unsupported `schemaVersion` is refused whole.** Not partly read. `JsonUtility` ignores
fields it does not know and default-constructs the ones it cannot find, so a document from
another schema arrives looking plausible with whatever moved silently replaced by zeroes.

A journey for a **different site** is refused too: revision numbers are per site, so Berkeley's
r7 is not newer than UBC's r3, it is not comparable at all.

### The timeout

The service gets **2.5 seconds** and then the launch goes on with what is already on the device.
A network that is merely absent fails immediately; a network that is present and cannot route to
the laptop — a phone on cellular, municipal wifi, the wrong SSID — hangs until the OS gives up,
tens of seconds later. The deadline is raced against the read rather than delegated to it, so a
source that swallows cancellation cannot defeat it.

### The cache

Written only after a fetch has parsed, and written to a temporary file that is then moved into
place. Both halves matter, and both are about the same launch: the one in a park with no wifi,
where a cache file containing half a document is the only thing left to read. A cached document
that fails to parse is deleted rather than left to fail again next time.

A fetch that parses is cached even when this device then refuses to run it — an uncalibrated
journey is still the right thing to have on hand for the next simulation. A document for another
site is never cached.

---

## 3. What is in the build

```
app/Assets/StreamingAssets/ShoalingUpstream/
  manifest.json               which sites are packaged, and which one this build launches into
  journeys/<slug>.json        the published revision, verbatim
  audio/index.json            clip ids with their byte lengths
  audio/<clipId>.mp3          one file per clip id the journey names
```

`BundleLayout` names this layout on the C# side and `tools/export-to-unity.mjs` writes it. They
have to agree exactly; a disagreement is invisible until a beat is silent.

Nothing written carries a timestamp, so re-running the exporter with nothing changed upstream
leaves the build byte-identical and "the assets changed" always means something changed.

---

## 4. Packaging a build

```bash
node tools/export-to-unity.mjs --site ubc-nitobe-garden-creek --allow-uncalibrated
node tools/export-to-unity.mjs --site ubc-nitobe-garden-creek --check
node tools/export-to-unity.mjs --site ucb-strawberry-creek-south --from-service --default
```

It reads the newest revision under `data/journeys/<slug>/revisions/` — no service needed —
or `GET /published` with `--from-service`. Then it validates the document with the service's own
`validateJourney`, copies every clip the journey plays, and writes the index and the manifest.

| Situation | What happens |
|---|---|
| A referenced clip has no packaged file | **Stops. Writes nothing.** Lists the clips. |
| A clip file is zero bytes | Stops. |
| The journey is uncalibrated | Stops unless `--allow-uncalibrated`. |
| The journey fails validation | Stops, printing the errors. |
| `schemaVersion` is not the one the app reads | Stops. |
| `site.slug` is not the slug asked for | Stops. |
| Nothing has changed | Writes nothing, says so. |
| Clips no bundled journey references | Reported; deleted with `--prune`. |

Refusing on a missing clip is the rule worth the most: a journey shipped without its audio fires
its beats correctly and plays silence, which on site reads as a bug in the trigger machine — the
hardest thing here to debug and the least likely to be believed.

The manifest accumulates, so packaging UBC does not unpackage Berkeley. `--default` sets which
site the build launches into.

### Without a terminal

**Tools ▸ Shoaling Upstream ▸ Package Journey and Audio…** runs the same script and shows its
output verbatim. It shells out rather than reimplementing the packaging rules, because a second
implementation of "refuse to ship a journey whose clips are missing" is a second set of rules the
moment either changes. Node is found through a login shell (`bash -lc`), since Unity launched
from Finder inherits a PATH that does not have it.

---

## 5. Using it from code

```csharp
var provider = JourneyProvider.ForLaunch(new JourneyProviderSettings
{
    Slug = "ubc-nitobe-garden-creek",           // omit to take the manifest's defaultSlug
    ServiceHost = "http://192.168.1.24:8710",   // empty for the standalone walk
});

var resolution = await provider.ResolveAsync();
JourneyProvider.Log(resolution);

if (!resolution.HasJourney) { /* say so on screen — do not start silently */ }
```

`resolution.Outcomes` holds one entry per candidate with a verdict — `Accepted`, `Superseded`,
`Unreadable`, `WrongSite`, `Uncalibrated` — and `Notes` holds the sources that produced no
candidate at all. When a field test turns out to have been running the wrong revision, that log
is the only record of why.

Audio:

```csharp
var catalogue = AudioCatalogue.FromStreamingAssets();

foreach (var clipId in catalogue.MissingClipIds(resolution.Document))
    Debug.LogWarning($"{clipId} is not in this build — that beat will be silent");

var request = catalogue.Load("heron--mid");
yield return request;                  // or: await catalogue.LoadAsync("heron--mid");
if (request.Clip != null) source.clip = request.Clip;
```

Clip ids are the ones the editor authors: `heron--far`, `heron--mid`, `heron--intimate`, plus
one-shot completions such as `lay-egg`, `jump`, `eat-strider`. Asking twice returns the same
request and the same clip, so three distance layers crossfading against each other decode one
file each. Clips are held compressed in memory: decoded, the ambient beds are about 11 MB a
minute and several are resident at once.

`MissingClipIds` is empty by construction for a bundled journey — the exporter guarantees it. It
can be non-empty for a fetched one, when a revision published after the last packaging run names
a clip that was never copied to the device. Check it when a fetched revision wins, and say so.

---

## 6. Tests

`app/Assets/ShoalingUpstream/Tests/EditMode/Config/` — precedence including the stale laptop, the
calibration refusal on device and its absence in simulation, schema-version refusal, malformed
and truncated JSON, a truncated cache being discarded, a service that never answers, and what the
catalogue knows before anything is played.

```
/Applications/Unity/6000.3.21f1/Unity.app/Contents/MacOS/Unity -batchmode -nographics -runTests \
  -projectPath app -testPlatform EditMode -testResults logs/config-tests.xml -logFile logs/unity-config.log
```
