# AdaptiveLighting

Motion- and daylight-driven lighting for Home Assistant, as a [NetDaemon](https://netdaemon.xyz) library.

Lights come on when you walk into a room — at a brightness and warmth that suit the time of day, **but
only if it's actually dark**. They dim as a warning before switching off, so they never drop on someone
sitting still. Touch a switch and the automation backs off and leaves your setting alone for a while.
When the house empties the lights sweep off; the first person home is met by the entry lights.

There is a Blazor dashboard and configuration UI, so the whole thing is set up from a browser rather
than by hand-editing YAML.

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

---

## ⚠️ Read this before exposing the UI

**The web UI has no authentication of any kind.** Anyone who can reach its port can rewrite your
lighting configuration and rebuild the running engine — switch off every room, point it at other lights,
or remove the night-time brightness cap.

It is built for a **LAN-only** deployment, and that is the only context in which shipping it without a
login is defensible. **Do not port-forward it, and do not put it on an untrusted network.** If you need
it from outside, put it behind Home Assistant ingress or an authenticating reverse proxy — do not add a
hand-rolled login to it instead.

The engine itself (`AdaptiveLighting`) has no network surface. If you don't want the UI, don't
reference `AdaptiveLighting.Web`; configure the YAML by hand and the engine runs exactly the same.

---

## Status

**Preview.** The engine runs a real house and a real cabin, and is covered by 466 tests. The API and
the configuration schema may still move. Requires **.NET 10** and the **NetDaemon V6** add-on.

**2.0 renames zones to areas.** The types, the YAML and the UI all say *area* now, and the published
Home Assistant event changed name with them. A configuration file written before 2.0 migrates itself
on the first start; an HA automation listening for the old event does not. See
[CHANGELOG.md](CHANGELOG.md) before upgrading.

## Install

```bash
dotnet add package AdaptiveLighting
dotnet add package AdaptiveLighting.Web   # optional: the dashboard + config editor
```

## Quick start

Three things: point the engine at a config file, hand it Home Assistant, and (optionally) serve the UI.

**1. `appsettings.json`** — the config file must live *outside* your deploy folder, or a redeploy
will wipe every edit made in the UI:

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

Start it once and leave it alone. Half a minute in, the engine reads Home Assistant's area registry and
writes down every room that has both a light and a motion sensor — **all of them switched off**. No light
changes until you open the UI at `http://<host>:10000` and choose which rooms to switch on.

## The mental model

Four ideas carry the whole system:

- **An area** is a room the engine manages — one Home Assistant area. Rooms are **opt-in**: an area
  you don't list, or have switched off, is never touched. Each runs its own state machine.
- **A period** is a slice of the day with a target brightness and colour temperature. Periods are
  house-wide. Boundaries are clock times or sun events (`sunset-01:00`) and blend rather than step.
- **The house** has shared state: who's home, which **house mode** is active (Normal / Sleep / Away /
  Guest, each optionally applying a scene), and whether the master switch is on.
- **Origin** — every change to a light is classified as *ours* or *a human's*. That distinction is what
  makes override handling work, and it's the subtlest part of the system.

## Configuration

Nothing is hard-coded: no entity ids, thresholds, times or room names exist in the C#. It all lives in
one YAML file, in four layers, each narrowing the last:

| Layer | What it sets |
|---|---|
| `Global` | House-wide: people, master switch, house modes, outdoor lux sensor, the discovery labels |
| `Defaults` | The baseline every room starts with — the **All rooms** group in the UI |
| `Periods` | The circadian table: when each period starts, its brightness/colour, its caps |
| `Areas` | Per room — overrides *only* what differs from `Defaults` |

Most rooms are three lines, because of **discovery**: give an area an `AreaId` and its lights, motion
sensors and lux sensor are found from the Home Assistant area registry.

Full documentation — the configuration reference, a worked example, the architecture and a user
guide — is at **[adaptivelighting.netlify.app](https://adaptivelighting.netlify.app)** (source in
[`website/`](website/)).

## Packages

| Package | What it is |
|---|---|
| [`AdaptiveLighting`](https://www.nuget.org/packages/AdaptiveLighting) | The engine. No network surface, no UI. |
| [`AdaptiveLighting.Web`](https://www.nuget.org/packages/AdaptiveLighting.Web) | Blazor dashboard + config editor (Razor Class Library). |
| [`AdaptiveLighting.Extensions`](https://www.nuget.org/packages/AdaptiveLighting.Extensions) | Host-agnostic HassModel helpers. Useful in any NetDaemon app. |

Ship the three as a matched set — they are compiled against each other.

## Licence

MIT — see [LICENSE](LICENSE).
