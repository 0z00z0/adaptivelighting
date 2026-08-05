# uihost — a host for the UI, so a change can be looked at

`AdaptiveLighting.Web` is a Razor Class Library and has no host of its own, so a UI change could only ever be
reasoned about or checked against a hand-written HTML page. This runs the real components, against the real
stylesheet, with a fake Home Assistant.

```bash
dotnet run --project tools/uihost
# http://localhost:5199
```

`local.yaml` beside this file is the document it edits. It is gitignored: copy a real one in to reproduce a
house, or leave it absent and the engine writes a starting document. **Never commit a real one — this
repository is public.**

To reproduce a state, seed it in `Program.cs`. What is there now gives each helper one orphan and one free
option, which is what the *move it to…* control needs to appear.

## Three things that are not obvious, and each looked like broken UI

- **`builder.WebHost.UseStaticWebAssets()`.** Static web assets are wired up automatically only in
  Development. Without this the RCL's `_content/**` 404s and the page renders unstyled.
- **`app.MapStaticAssets()` as well as `app.UseStaticFiles()`.** `_framework/blazor.web.js` comes from the
  former. Without it every page server-renders once and no circuit ever opens, so nothing on the page
  responds — which reads as a dead UI rather than a missing script.
- **A `PackageReference` to `Microsoft.AspNetCore.App.Internal.Assets`.** That is where `blazor.web.js`
  actually lives. It is not in the shared framework and not in the SDK; a NetDaemon host gets it
  transitively, and this project has to ask for it by name.

Also: `app.UseAntiforgery()` is required, and a `wwwroot` folder must exist even if it is empty.

## Not in the solution

`AdaptiveLighting.slnx` does not include this project on purpose — CI should not build or package a developer
tool, and it references the test project for its fakes.
