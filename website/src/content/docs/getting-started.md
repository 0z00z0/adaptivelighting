---
title: "Get started"
description: "From no NetDaemon host at all to a running install, for a first-time reader."
---

For someone who already has a NetDaemon host running, see [How to use it](/user-guide/#1-install-it).
This page is one level below that: starting from nothing.

## What is needed first

- A **Home Assistant** instance, reachable from wherever the host will run, with a
  [long-lived access token](https://www.home-assistant.io/docs/authentication/#your-account-profile).
- **.NET 10** ([download](https://dotnet.microsoft.com/download/dotnet/10.0)).
- A **GitHub account** with a [personal access token](https://github.com/settings/tokens) carrying
  the **`read:packages`** scope. The packages are hosted on GitHub, not nuget.org, and GitHub asks
  for a sign-in on every read — even for a public, MIT-licensed repository.

:::caution[The packages are currently private]
As of this writing the four packages restore only for accounts the `0z00z0` organisation has
explicitly granted access to — a `read:packages` token is necessary but not sufficient. This is a
known limitation, tracked as
[issue #33](https://github.com/0z00z0/adaptivelighting/issues/33): the organisation blocks public
package creation, and nothing short of an owner changing that setting fixes it. Everything below is
still worth following to have the project ready to go the moment that changes.
:::

## 1. Get a project

Two ways to reach a running host. Either is fine; the second gives an editable copy in place of a
package reference for the one piece you're likely to touch.

**Clone the sample** — the smallest working host, three files, already wired up:

```bash
git clone https://github.com/0z00z0/adaptivelighting
cd adaptivelighting/samples/MinimalHost
```

**Or start from the official NetDaemon template**, if a host with more than one automation is the
eventual goal:

```bash
dotnet new --install NetDaemon.Templates.Project
dotnet new nd-project -o my-house
cd my-house
dotnet add package AdaptiveLighting.NetDaemon
```

## 2. Add the GitHub Packages feed

```xml
<packageSources>
  <add key="0z00z0" value="https://nuget.pkg.github.com/0z00z0/index.json" />
</packageSources>
```

Put that in a `nuget.config` beside the project, then store the token once, outside anything committed:

```bash
dotnet nuget update source 0z00z0 --username YOUR_GITHUB_USERNAME --password YOUR_TOKEN --store-password-in-clear-text
```

Full detail on this step, including why it's necessary at all, is in the
[README](https://github.com/0z00z0/adaptivelighting#install).

## 3. Wire it in

Two calls are all a host needs — this is the whole of `samples/MinimalHost/Program.cs`:

```csharp
builder.AddAdaptiveLighting();      // engine, UI, static assets, key ring, and the port

WebApplication app = builder.Build();
app.UseAdaptiveLighting();          // assets, antiforgery, the Blazor endpoint
```

Everything around those two lines is the ordinary NetDaemon host boilerplate — reading Home
Assistant's own `Host`/`Port`/`Token` from `appsettings.json`, registering the app model, running
the host. The sample carries a working copy in full; a project built from the official template
already has it.

One more class hands the engine its Home Assistant connection — the whole of
`samples/MinimalHost/AdaptiveLightingApp.cs`:

```csharp
[NetDaemonApp(Id = "adaptive_lighting")]
internal sealed class AdaptiveLightingApp : IAsyncDisposable
{
	private readonly LightingEngineHost _engine;

	public AdaptiveLightingApp(LightingEngineHost engine, IHaContext ha, IHaRegistry registry, IScheduler scheduler)
	{
		_engine = engine;
		_engine.Attach(ha, registry, scheduler, NetDaemonAppSwitch.EntityIdFor(GetType()));
		_engine.Reload();
	}

	public ValueTask DisposeAsync() { _engine.Detach(); return ValueTask.CompletedTask; }
}
```

## 4. Point the config file outside the project

```json
{
  "HomeAssistant": {
    "Host": "homeassistant.local",
    "Port": 8123,
    "Ssl": false,
    "Token": "YOUR_LONG_LIVED_ACCESS_TOKEN"
  },
  "AdaptiveLighting": {
    "ConfigPath": "/path/outside/this/project/lighting.yaml"
  }
}
```

`ConfigPath` has to sit **outside the project's own folder** — a rebuild or a redeploy overwrites
everything inside it, taking every edit made later in the browser with it.

## 5. Restore and run

```bash
dotnet restore
dotnet run
```

The UI answers at `http://localhost:10000`. Half a minute after the Home Assistant connection
settles, set-up runs once: it reads every area that has both a light and a motion sensor, and writes
all of them down **switched off**. Nothing changes until the browser is opened and rooms are chosen.

📷 [screenshot: the first-run board — "Setting up found N rooms", the room chips, and the
"Choose which rooms to switch on" button]

That screenshot isn't captured yet — showing it correctly needs the demo UI host
(`tools/uihost`) to actually run the engine, which it doesn't yet
([issue #39](https://github.com/0z00z0/adaptivelighting/issues/39)). The description above is what
a running install shows; [How to use it](/user-guide/#2-start-it-and-wait-half-a-minute) picks up
from exactly this point, with the rest of the screens.

## Next

- [How to use it](/user-guide/) — the four screens, once it's running.
- [Settings reference](/configuration/) and [Example configuration](/example-config/) — everything
  past the minimal path.
