# AdaptiveLighting

**Lights that come on when you walk in — at the right brightness and warmth for the time of day, and only when it's actually dark.** Motion- and daylight-driven lighting for [Home Assistant](https://www.home-assistant.io), packaged as a [NetDaemon](https://netdaemon.xyz) library.

[![CI](https://github.com/0z00z0/adaptivelighting/actions/workflows/ci.yml/badge.svg)](https://github.com/0z00z0/adaptivelighting/actions/workflows/ci.yml) &nbsp;·&nbsp; .NET 10 &nbsp;·&nbsp; MIT licence &nbsp;·&nbsp; Preview

<!-- screenshot: a hero shot of the board — room lanes on a shared time axis. Drop it here once captured. -->

Lights come on when you enter a room, dim as a warning before switching off so they never drop on someone sitting still, and back off the moment you touch a switch. When the house empties they sweep off; the first person home is met by the entry lights. A browser UI sets the whole thing up — no hand-edited YAML required.

**Who it's for:** Home Assistant users who run the NetDaemon add-on and want circadian, presence-aware lighting they configure from a screen rather than from automations.

## What it does

- **Motion, gated by daylight** — a room lights on movement, but only if it's dark, judged by its own light-level sensors or the sun's height.
- **Dims before it gives up** — a warning fade precedes switch-off; any movement brings the lights straight back.
- **Backs off when you intervene** — touch a switch or dimmer and the automation leaves your setting alone for a while. Every change is classified as the engine's or a person's.
- **A house-wide circadian schedule** — periods (morning, day, evening, night) set brightness and colour temperature and blend across boundaries; a period can wait for movement instead of the clock.
- **House modes** — Normal, Sleep, Away and Guest, each able to run a Home Assistant scene.
- **A room can run a scene** instead of switching on, and another instead of switching off — so it settles into something soft when it empties rather than going dark.
- **Zero hard-coding** — no entity ids, thresholds or room names in the C#; it all lives in one YAML file, with rooms discovered from the Home Assistant area registry.
- **A Blazor web UI** (optional) — a board of room lanes on a shared time axis, a page per room, an activity log and a settings editor.

```
┌ AdaptiveLighting.Extensions ┐   host-agnostic HassModel helpers
└──────────────┬──────────────┘
┌──────────────▼──────────────┐
│      AdaptiveLighting       │   the engine: areas, schedule, modes
└──────────────┬──────────────┘
┌──────────────▼──────────────┐
│    AdaptiveLighting.Web     │   Blazor dashboard + config editor (optional)
└─────────────────────────────┘
```

> [!WARNING]
> **The web UI has no authentication of any kind.** Anyone who can reach its port can rewrite your lighting configuration and rebuild the running engine. It is built for a **LAN-only** deployment, which is the only context in which shipping it without a login is defensible — **do not port-forward it, and do not put it on an untrusted network.** If you need it from outside, put it behind Home Assistant ingress or an authenticating reverse proxy rather than adding a hand-rolled login.
>
> The engine itself has no network surface: skip `AdaptiveLighting.Web` and configure the YAML by hand, and it runs exactly the same.

## Status

**Preview.** The engine runs a real house and a real cabin and is covered by 927 tests, but the API and the configuration schema may still move. Requires **.NET 10** and the **NetDaemon V6** add-on.

**2.0 renames zones to areas** — the types, the YAML, the UI and the published Home Assistant event all say *area* now. A pre-2.0 configuration migrates itself on first start; an HA automation listening for the old event does not. Read [CHANGELOG.md](CHANGELOG.md) before upgrading.

## Install

> **Preview — not yet on NuGet.** The packages below are wired to publish on the first tagged release; until that lands, clone the repo and reference the three projects (or `dotnet pack` them locally). Track the first published build in the [releases](https://github.com/0z00z0/adaptivelighting/releases).

```bash
dotnet add package AdaptiveLighting
dotnet add package AdaptiveLighting.Web   # optional: the dashboard + config editor
```

## Quick start

Three things: point the engine at a config file, hand it Home Assistant, and (optionally) serve the UI.

**1. `appsettings.json`** — keep the config file *outside* your deploy folder, or a redeploy wipes every edit made in the UI:

```json
{
  "AdaptiveLighting": { "ConfigPath": "/config/adaptive-lighting/lighting.yaml" }
}
```

**2. A NetDaemon app** that hands the engine its Home Assistant connection:

```csharp
[NetDaemonApp(Id = "adaptive_lighting")]
internal sealed class AdaptiveLightingApp : IAsyncDisposable
{
    private readonly LightingEngineHost _engine;

    public AdaptiveLightingApp(LightingEngineHost engine, IHaContext ha,
                               IHaRegistry registry, IScheduler scheduler)
    {
        _engine = engine;
        _engine.Attach(ha, registry, scheduler, NetDaemonAppSwitch.EntityIdFor(GetType()));
        _engine.Reload();
    }

    public ValueTask DisposeAsync() { _engine.Detach(); return ValueTask.CompletedTask; }
}
```

**3. `program.cs`** — standard NetDaemon host, plus the UI if you want it:

```csharp
builder.Services.AddLightingWeb();                                  // engine + UI services
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.WebHost.ConfigureKestrel(o => o.ListenAnyIP(10000));        // LAN only — see the warning above
builder.WebHost.UseStaticWebAssets();

var app = builder.Build();
app.MapStaticAssets();                    // serves the library's CSS via the asset manifest
app.UseAntiforgery();
app.MapRazorComponents<AdaptiveLighting.Web.App>().AddInteractiveServerRenderMode();
```

Start it once and leave it alone. Half a minute in, the engine reads Home Assistant's area registry and writes down every room that has both a light and a motion sensor — **all of them switched off**. No light changes until you open the UI at `http://<host>:10000` and choose which rooms to switch on.

## The mental model

Four ideas carry the whole system:

- **An area** is a room the engine manages — one Home Assistant area. Rooms are **opt-in**: an area you don't list, or have switched off, is never touched. Each runs its own state machine.
- **A period** is a slice of the day with a target brightness and colour temperature. Periods are house-wide; boundaries are clock times or sun events (`sunset-01:00`) and blend rather than step.
- **The house** holds shared state: who's home, which **house mode** is active (Normal / Sleep / Away / Guest), and whether the master switch is on.
- **Origin** — every change to a light is classified as *ours* or *a human's*. That distinction is what makes override handling work, and it's the subtlest part of the system.

## Configuration

Nothing is hard-coded: it all lives in one YAML file, in four layers, each narrowing the last.

| Layer | What it sets |
|---|---|
| `Global` | House-wide: people, master switch, house modes, outdoor lux sensor, the discovery labels |
| `Defaults` | The baseline every room starts with |
| `Periods` | The circadian table: when each period starts, its brightness/colour, its caps |
| `Areas` | Per room — overrides *only* what differs from `Defaults` |

Most rooms are three lines, because of **discovery**: give an area an `AreaId` and its lights, motion sensors and lux sensor are found from the Home Assistant area registry.

Full documentation — how it works, how to use it, the settings reference and a worked example — is at **[adaptivelighting.netlify.app](https://adaptivelighting.netlify.app)** (source in [`website/`](website/)). Working on the engine itself? [`docs/mechanisms.md`](docs/mechanisms.md) explains why each chosen number is that number; [`docs/backlog.md`](docs/backlog.md) is what's still open.

## Packages

Ship the three as a matched set — they are compiled against each other.

| Package | What it is |
|---|---|
| `AdaptiveLighting` | The engine. No network surface, no UI. |
| `AdaptiveLighting.Web` | Blazor dashboard + config editor (Razor Class Library). |
| `AdaptiveLighting.Extensions` | Host-agnostic HassModel helpers. Useful in any NetDaemon app. |

## Licence

MIT — see [LICENSE](LICENSE).

---

Part of **[ZeroZero Software](https://0z0.xyz)** — small tools, zero bloat. · [github.com/0z00z0](https://github.com/0z00z0)
