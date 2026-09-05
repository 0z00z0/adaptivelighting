# uihost — a host for the UI, so a change can be looked at

`AdaptiveLighting.Web` is a Razor Class Library and has no host of its own, so a UI change could only ever be
reasoned about or checked against a hand-written HTML page. This runs the real components, against the real
stylesheet, with a fake Home Assistant.

```bash
dotnet run --project tools/uihost
# http://localhost:5199
```

## Two at once

Two worktrees previewing at the same time need two ports, so the port is a command-line value. Without one it
is 5199, exactly as before.

```bash
dotnet run --project tools/uihost -- --port 5200   # a second worktree
dotnet run --project tools/uihost -- --port 0      # any free port, printed on startup
```

The bound address is printed as `uihost listening on …` once the host is up, which is the line to read after
`--port 0`.

**A port that is already taken fails the start; it does not quietly move to a free one.** A host that lands
somewhere unannounced is worse than one that refuses: the next verification run opens the port it expected and
looks at the *other* worktree's UI while believing it is looking at its own. `--port 0` is how to ask for a
free port on purpose.

Note that `--port 0` binds `127.0.0.1` alone, where a fixed port binds the `localhost` name and so answers on
both loopback families. Kestrel refuses a dynamic port on a host name.

`local.yaml` beside this file is the document it edits. It is gitignored: copy a real one in to reproduce a
house, or leave it absent and the engine writes a starting document. **Never commit a real one — this
repository is public.**

To reproduce a state, seed it in `Program.cs`. What is there now gives each helper one orphan and one free
option, which is what the *move it to…* control needs to appear.

## Three things that are not obvious, and each looked like broken UI

- **`builder.WebHost.UseStaticWebAssets()`.** Static web assets are wired up automatically only in
  Development. Without this the RCL's `_content/**` 404s and the page renders unstyled.
- **`app.MapStaticAssets()`.** It serves both `_content/**` and `_framework/blazor.web.js`. Without it every
  page server-renders once and no circuit ever opens, so nothing on the page responds — which reads as a dead
  UI rather than a missing script.
- **A `PackageReference` to `Microsoft.AspNetCore.App.Internal.Assets`.** That is where `blazor.web.js`
  actually lives. It is not in the shared framework and not in the SDK; a NetDaemon host gets it
  transitively, and this project has to ask for it by name.

Also: `app.UseAntiforgery()` is required, and a `wwwroot` folder must exist even if it is empty.

**`UseStaticFiles()` is not needed and is not here.** An earlier version of this file said to call it *as well*,
on the strength of a note claiming `MapStaticAssets` returns 0-byte bodies. Measured on 2026-08-05 with
`UseStaticFiles` removed entirely: `app.css` 200 / 182 663 bytes, `blazor.web.js` 200 / 200 538 bytes, circuit
open, Schedule tab rendering. The 0-byte symptom had a different cause — the missing `Internal.Assets` package
in a Production environment — and the extra call was cargo-cult. The ESPHomeAdmin session pushed back on it,
which is how it got measured.

## Not in the solution

`AdaptiveLighting.slnx` does not include this project on purpose — CI should not build or package a developer
tool, and it references the test project for its fakes.
