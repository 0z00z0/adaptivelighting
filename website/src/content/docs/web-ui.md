---
title: "The web UI"
description: "How the Blazor dashboard and configuration editor are built."
---

## 1. Verified facts

- **Tutorial** (`netdaemon.xyz/docs/user/tutorials/webhost/`, read today): change the project SDK
  to `Microsoft.NET.Sdk.Web`, switch `program.cs` to `WebApplication.CreateBuilder(args)`, keep
  all NetDaemon setup on `builder.Host.…`, add `AddRazorPages()` + `AddServerSideBlazor()`,
  Kestrel on port 10000. NetDaemon services (`IHaContext`, entities, `IScheduler`) are injectable
  straight into Razor components. Caveats the tutorial itself states: deployment must publish as
  a *website* (wwwroot artifacts), the port must be enabled in the add-on config, and it is not
  reachable via Nabu Casa.
- **Prior art in this repo**: branch `origin/add-razor-web-page` (commit `30bd81e`, 2025-09-02)
  did exactly this to `House` — `Sdk="Microsoft.NET.Sdk.Web"`, `WebApplication.CreateBuilder`,
  `builder.Host.UseNetDaemon…` chain preserved, controllers + Razor Pages + Blazor Server hub,
  `ListenAnyIP(10000)`, `app.MapBlazorHub()` / `MapFallbackToPage("/_Host")`. It predates the
  25.36.0 alignment (pins 25.18.1) and carries a huge stale `HomeAssistantGenerated.cs` diff —
  **do not merge it; treat it as a reference implementation of `program.cs` only.**
- **Add-on port support**: the NetDaemon V6 add-on declares TCP ports 10000–10004 (verified
  `netdaemon_6/config.json`), so the webhost pattern is first-class in a v6 add-on deployment —
  the user maps port 10000 in the add-on's Network panel.
- ASP.NET Core 10 still supports the Razor Pages + `MapBlazorHub` Blazor Server model used by the
  tutorial. **UNVERIFIED against MS docs** (high confidence; the newer
  `AddRazorComponents().AddInteractiveServerComponents()` model is the alternative if the
  implementers hit an obsoletion warning — either works; pick ONE and stay with it).

## 2. Where it lives

- **Hosting: per project, v1 = House only.** The web server is the process host, so the csproj SDK
  swap + `program.cs` rewrite are inherently per-project. Cabin follows later by copying
  the same ~40-line `program.cs` shape (05 #8).
- **UI content: `AdaptiveLighting.Web`, a new Razor Class Library** (`Microsoft.NET.Sdk.Razor`, net10.0)
  so both hosts get the same UI. It references `AdaptiveLighting` (for the config schema +
  `ILightingConfigStore` + `ZoneSnapshot`) and **never** a generated type. New project → new
  csproj + solution entry (allowed: it's a *new* file; the only *edits* to existing csproj files
  are House's SDK attribute + the migration pins).
- House additions: `House/Pages/_Host.cshtml`, `House/App.razor`, `House/wwwroot/*` (minimal), or the RCL
  carries everything routable and House keeps only `_Host`. Implementers follow the tutorial
  layout; keep House-side files to the bare minimum so Cabin's later adoption is a copy.

## 3. program.cs shape (House) — the only edit to an existing engine-relevant file

Follow the razor-branch/tutorial shape exactly (it is proven), modernised only where required:

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Host
	.UseNetDaemonAppSettings()
	.UseNetDaemonDefaultLogging()
	.UseNetDaemonRuntime()
	.UseNetDaemonTextToSpeech()
	.UseNetDaemonMqttEntityManagement()
	.ConfigureServices((_, services) => services
		.AddAppsFromAssembly(Assembly.GetExecutingAssembly())
		.AddNetDaemonStateManager()
		.AddNetDaemonScheduler()
		.AddHomeAssistantGenerated()
		.AddSingleton<ILightingConfigStore>(sp => /* file-backed store, path from config */));

builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();
builder.WebHost.ConfigureKestrel(o => o.ListenAnyIP(10000));

var app = builder.Build();
app.UseStaticFiles();
app.UseRouting();
app.MapBlazorHub();
app.MapFallbackToPage("/_Host");
await app.RunAsync();
```

Keep the old `Host.CreateDefaultBuilder` block commented below it (repo idiom: disable, don't
delete). The try/catch wrapper stays.

## 4. Config read / persist / hot-reload — the honest hard part

`IAppConfig<T>` is read-once-at-app-init and write-never. A UI that edits config therefore needs
its own store. Design (all in `AdaptiveLighting`):

```
ILightingConfigStore
	AdaptiveLightingConfig Current { get; }
	IObservable<AdaptiveLightingConfig> Changes { get; }
	Task SaveAsync(AdaptiveLightingConfig updated)     // validate -> atomic write -> push Changes
```

- **v1 (tonight/this week): read-mostly UI.** The store *reads* the same
  `apps/AdaptiveLighting/AdaptiveLighting.yaml` the app model binds (YamlDotNet — already in the
  dependency graph via NetDaemon.AppModel, add an explicit `PackageReference` to `AdaptiveLighting`;
  deserialize the single top-level document key). UI pages: **Dashboard** (live `ZoneSnapshot`
  per zone: state, why, current target, override remaining — fed by subscribing to
  `IStatePublisher` through a shared in-memory `ZoneSnapshotCache`), **Config viewer** (rendered
  read-only), **Modes** (kill/sleep/guest toggles — these are HA entities, so the page just calls
  `IHaContext.CallService("input_boolean", "turn_on"/"turn_off", …)`; state changes flow back via
  the normal engine path, no persistence problem at all).
- **v2: writes.** `SaveAsync` validates via `ConfigValidator` (reject on document-level errors),
  writes atomically (temp file + `File.Replace`), pushes `Changes`; `AdaptiveLightingApp`
  subscribes to `Changes` and does **dispose-orchestrator → build-new-orchestrator** (this is why
  `LightingOrchestrator` is `IDisposable` with airtight subscription cleanup — the design carries
  the hot-reload seam from day one). At that point the engine's *initial* load also moves from
  `IAppConfig<T>` to the store (one loader, one truth); `IAppConfig` remains only as the
  mechanism that told us where the file is — or is dropped entirely in favour of a fixed path.
- **Round-trip losses, stated plainly:** YamlDotNet serialization drops comments and reorders
  nothing but formats freshly — an edited file loses hand-written comments. Acceptable for v2;
  if not, the alternative is a JSON sidecar store (`adaptive-lighting.json`) that *overrides* the
  YAML — more moving parts, deferred decision (05 #4).
- **Deployment overwrite trap:** on an add-on deployment, publish copies the YAML into the deploy
  folder — a later redeploy would clobber UI edits. v2 must therefore move the *editable* file
  outside the publish tree (e.g. `/config/adaptive-lighting/<host>.yaml`, path in
  `appsettings.json`), falling back to the bundled YAML on first run. Decision needed (05 #4).
- **HA `input_*` helpers as the store — evaluated, rejected** for structured config (a zones
  tree does not fit flat helpers; one instance's helpers are UI/.storage-based, unversioned) but
  **adopted** for the live mode toggles, where HA entities are exactly right.

## 5. Auth / exposure

- LAN-only. Bind 10000, do **not** add the port to any router/NAT/Nabu Casa; on the add-on it is
  exposed only if the user maps it. v1 ships **no auth** — acceptable on this LAN for a UI that
  can toggle lights and modes, matching the ESPHome Device Builder precedent (`:6052`, no auth).
- **Nothing secret may be rendered or logged by the UI.** The web host process holds the HA token
  (from `appsettings.Development.json` / add-on env) — no page, API response, or diagnostic dump
  may echo configuration objects that contain it. Concretely: never bind/return
  `IConfiguration`/`HomeAssistantSettings` in a component; the UI touches only
  `AdaptiveLightingConfig`, `ZoneSnapshot`, and `IHaContext` calls. No cookies, no login form
  (creating credentials would be worse than none here).
- If exposure beyond LAN is ever wanted: put it behind HA ingress or a reverse proxy with auth —
  out of scope, explicitly deferred.

## 6. Difficulty and phasing — honest verdict

Coexistence of web host + NetDaemon is **proven easy** (tutorial + repo branch). The genuinely
hard parts are (a) config write round-trip + live orchestrator rebuild, and (b) not letting UI
work destabilise the engine. Hence:

- **v1 (in scope for this build):** SDK/program.cs swap on House, `AdaptiveLighting.Web` RCL, Dashboard
  (read-only live zone states), Config viewer (read-only), Modes page (toggles via HA entities).
  Effort: modest; risk: low; the engine does not change at all for it. If the overnight window
  gets tight, **v1 can be cut entirely without touching Parts 1–3** — the engine has no
  dependency on the web host. Cut line in priority order: Modes page → Config viewer → Dashboard
  → whole web host.
- **v2 (explicitly out of scope tonight):** `SaveAsync` + hot-reload rebuild, zone editor with
  entity pickers fed by `IHaRegistry`, editable-file relocation, Cabin rollout.

## 7. What v2 actually shipped

v2 is built. Where it diverged from the plan above, this is what is true now:

- **The store is `LightingConfigStore` + `LightingEngineHost` (both in `AdaptiveLighting/Hosting`),
  not the `ILightingConfigStore` sketched in §4.** No `IObservable<Changes>`: an observable implies
  several subscribers reacting to a config change, and there is exactly one — the thing that owns
  the orchestrator. So the engine host owns both the file and the orchestrator's lifetime, and
  `Save` is validate → write → dispose → rebuild, in that order, under one lock.
- **The document moved out of the publish tree** (05 #4), so `apps/AdaptiveLighting/*.yaml` is now
  the shipped example that seeds it, not the live config.
- **`IAppConfig<AdaptiveLightingConfig>` is gone from the engine's path.** It bound from `./apps`,
  which is no longer where the document lives, and two loaders for one file is a bug waiting for a
  bad night. `LightingConfigDocument` (YamlDotNet) is the only loader; the per-host bootstrap no
  longer reads config at all, it hands over `IHaContext`/`IHaRegistry`/`IScheduler` and asks for a
  load. The YAML's top-level key stays the FQN anyway, so pointing `IAppConfig` back at it would
  still work.
- **The bootstrap no longer throws on an invalid document**, reversing 02 §7. A throw sets
  `ApplicationState.Error` *and disposes the app's DI scope*, taking the `IHaContext` with it — so
  the browser could save a corrected file and still not start anything, which is the one thing the
  UI exists to do. The persistent notification and the error logs are unchanged; only the throw
  went. This is the deliberate cost of making the engine fixable from a browser.
- **Discovery preview** in the zone editor runs the real `ZoneEntityResolver`, not a lookalike, so
  what the page shows for an area is what the engine will do with it.
- **Auth is still none, and the risk changed.** The UI could previously toggle lights and modes; it
  can now rewrite the lighting configuration and restart the engine. Same LAN-only position, larger
  blast radius — see the exposure comment in each `program.cs`.
