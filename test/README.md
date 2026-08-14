# Tests

Node's built-in runner, no framework. Everything runs from the repository root:

```sh
npm test           # every suite in test/
npm run typecheck  # the TypeScript suites, checked rather than stripped
npm run verify     # build, typecheck, then the whole suite
```

A single suite:

```sh
node --test test/journey-store.test.mjs
```

Suites are a mix of `.ts` and `.mjs`; Node runs both directly, stripping types without checking
them, which is why `npm run typecheck` is a separate guarantee rather than part of the run.

The end-to-end suites start `service/src/server.mjs` themselves with `PORT=0` (the OS picks a
free port, so a dev server you already have open is never in the way) and `JOURNEY_DIR` pointed
at a throwaway directory. No suite writes to `data/journeys/`. If a run is interrupted, the only
thing left behind is a `shoaling-test-*` directory in the system temp dir.

## The suites

| file | what it defends |
| --- | --- |
| `geometry.test.ts` | Projection onto the walking path, run against **both** implementations — the service's and the editor's. If they drift, the scrubber arms a beat at a position the phone will never trigger it at. |
| `journey-schema.test.mjs` | The manifest contract the editor, the service and the app all agree on, and in particular which problems are errors (the runtime cannot act on them) versus warnings (authoring judgements the validator is not entitled to overrule). |
| `journey-store.test.mjs` | Where authored work can actually be lost: drafts, append-only published revisions, and the separate patch paths that keep a background write of the trim box from clobbering an in-progress beat edit. |
| `audible-field.test.mjs` | The audible half-life the editor draws as a ring around every beat — a claim about how far a beat carries, not a rendering detail. |
| `audio-catalogue.test.mjs` | The packaged audio against the real `data/audio/` tree, because the failure that happens is a recording that never made it into the repo. |
| `api.e2e.test.mjs` | The HTTP contract the iOS app depends on, over fetch against a real server: draft, validate, publish, and the published revision the device reads. |
| `control-bus.e2e.test.mjs` | The operator's control bus over real WebSockets, including the property that matters in the field — commands are *scheduled*, not fired. |
| `browser.e2e.test.mjs` | The editor and the controller in a real browser. Skipped unless Playwright is installed — see below. |
| `helpers.ts` | Shared fixtures, not a suite. Every test builds its own journey rather than reading the artist's draft. |

## The browser suite

`browser.e2e.test.mjs` is the only suite that can see what the other seven cannot: the editor is
ES modules, an import map, three.js and spark.js against real WebGL, and it can be completely
broken while everything else passes. Both of the worst regressions so far — a
`Cannot set properties of null` at boot, and a call to a renamed private field that only failed
when the line was reached — left a blank stage and a green test run.

It boots the editor against a fixture journey that names a real capture in `data/splats/`, and
checks that there is no uncaught page error, no console error and no error toast; that the scan
loads and its splat count is reported; that the layer toggles, the panels, undo/redo and save all
work through real clicks and real keystrokes; that the fps readout is live; and that `/control`
loads and connects its WebSocket.

Playwright is **not** a dependency of this repository, and the suite is written so it never has
to be. When it is missing the whole suite skips and the run still exits zero:

```
ok 1 - the editor in a real browser # SKIP playwright is not available — run …
```

To turn it on:

```sh
npm i -D playwright          # or: npm i -g playwright
npx playwright install chromium
node --test test/browser.e2e.test.mjs
```

The same skip covers a Playwright that is installed without its browser binary, so a half-done
install reports what to run rather than failing the suite.

Notes if it misbehaves:

- **Slow first run.** It loads the full ~5M-splat capture. The suite waits on the editor's own
  loading state rather than on a timer, but the boot budget is 90 s for a machine falling back to
  software WebGL.
- **No captures on this machine.** The splat test skips on its own if `data/splats/` has nothing
  for the scan; the rest of the suite still runs.
- **Watching it work.** Launch headed by editing the `chromium.launch(...)` call to
  `{ headless: false }`.
