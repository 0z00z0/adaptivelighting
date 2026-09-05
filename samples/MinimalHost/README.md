# MinimalHost

The smallest NetDaemon host that runs AdaptiveLighting: three files, two of AdaptiveLighting's own
calls, and nothing else. Full walkthrough, including how to get a GitHub Packages token:
[Get started](https://adaptivelighting.netlify.app/getting-started/).

Every value in `appsettings.json` is a placeholder — `homeassistant.local`, `8123`,
`YOUR_LONG_LIVED_ACCESS_TOKEN` — fill in a real Home Assistant host and a long-lived access token
before running it.

```bash
dotnet restore
dotnet run
```

`AdaptiveLighting:ConfigPath` in `appsettings.json` must point somewhere **outside this project
folder** before the engine can write to it — a `dotnet publish` of this project would otherwise
overwrite the lighting document on every deploy.
