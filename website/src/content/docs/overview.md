---
title: "Overview"
description: "How the system thinks: zones, periods, the house, and origin."
---

Start here. The other documents in this folder are the design record; this one is the map. For
day‑to‑day use (the dashboard, house modes, "why didn't my light turn on?"), see the
[user guide](/user-guide/).

## What it does

Lights turn on when you walk into a room, at a brightness and colour temperature that suit the
time of day, but only if it's actually dark. They dim as a warning before going out, so they never
drop on someone sitting still. If you touch a switch or a dimmer, the automation backs off and
leaves your setting alone for a while. When the house empties the lights sweep off; when the first
person comes back, the entry lights meet them. At 03:00 nothing gets brighter than a configured
ceiling.

If any of that misbehaves, one kill switch stops the engine commanding anything, while it keeps
watching so you can see what it *would* have done.

## The mental model

Four ideas carry the whole system.

**A zone** is a room the engine manages — usually one Home Assistant area. It owns its lights, its
motion sensors, and optionally a lux sensor. Zones are **opt-in**: an area you don't list is never
touched. Each zone runs its own independent state machine.

**A period** is a slice of the day (morning / day / evening / night) with a target brightness and
colour temperature. Periods are house-wide, not per zone. Boundaries are clock times or sun events
(`sunset-01:00`), and targets blend across a boundary rather than stepping.

**The house** has state that all zones share: who's home, which **house mode** is active, and whether
the kill switch is on. A zone consults it but decides for itself. The house mode is a single
`input_select` whose options each carry one *kind* — Normal, Sleep, Away or Guest — and a period,
a presence sensor or a clock can switch it automatically; every reset returns to Normal. Away and
Guest apply an HA scene and pause the engine until a reset fires (see `/configuration/`).

**Origin** is the interesting one. Every change to a light is classified as *ours* or *a human's*.
That distinction is what makes override work, and it's the hardest part of the system (see below).

## The whole thing on one page

Every arrow names the parameter that drives it — so if you want to change a behaviour, this tells
you which setting to reach for.

![Zone state machine, annotated with the configuration parameter governing each transition](/state-machine.svg)

## The zone state machine

Each zone is always in exactly one state:

| State | Meaning |
|---|---|
| `AutoVacant` | Nobody here, lights off, waiting for motion. |
| `AutoActive` | Occupied and lit; retargets as the period drifts. |
| `PreOff` | Vacancy timeout hit — dimmed as a warning. Motion here rescues it. |
| `OverriddenOn` | A human set a level. Their setting is sacred until the override expires. |
| `SuppressedOff` | A human turned it off. Motion is deliberately ignored — they wanted dark. |
| `Away` | House is empty. |
| `Disabled` | Kill switch or per-zone `Enabled: false`. Still observing, never commanding. |

The two that make it feel considered rather than robotic are `PreOff` (a grace period instead of
sudden darkness) and `SuppressedOff` (turning the lights off actually *means* something — the room
doesn't fight you by relighting on your way out).

## How override detection works, and its honest limit

`CallService` in NetDaemon is fire-and-forget: it never tells us the context id of the command it
just sent. So there's no exact way to ask "was that state change mine?". Two heuristics combine:

1. **Command expectation** (primary) — before commanding a light, the engine records what it
   expects. Changes matching that expectation, within a window, are its own echo.
2. **Context inspection** — `UserId == null && ParentId == null` means a physical switch or dimmer.
   A user id means the app or UI. A parent id means another automation (which counts as manual by
   default, so your other automations win).

The echo window spans the command's own fade. This matters more than it sounds: a 15-second night
fade emits state changes for 15 seconds, and a fixed 8-second window would make the engine read
the tail of its own fade as a human at the dimmer — overriding itself on every night retarget.

## Configurability

**Nothing is hard-coded.** No entity ids, thresholds, times, or room names exist anywhere in the
C#. It all lives in one YAML file per site — a commented example ships in the repo as the seed,
and on a running host the live copy sits outside the publish tree where the config editor owns it.

The schema is four layers, each narrowing the last:

| Layer | What it sets |
|---|---|
| `Global` | House-wide: persons, kill switch, house modes, override tuning, discovery labels. |
| `Defaults` | The baseline for every zone — every per-zone knob has a default here. |
| `Periods` | The circadian table: when each period starts, its brightness/colour, its caps. |
| `Zones` | Per room. Overrides *only* what differs from `Defaults`. |

Most zones are three lines, because of **discovery**. Give a zone an `AreaId` and the engine finds
its lights, motion sensors and lux sensor from the Home Assistant area registry — dropping group
members and anything labelled `adaptive-exclude`. Explicit lists (`Lights`, `MotionSensors`,
`LuxSensor`) are the escape hatch when HA's area assignments are wrong; each replaces discovery for
that slot only. This keeps the config small enough to stay truthful, instead of a hand-listed
inventory that silently rots when an entity is renamed.

The knobs worth knowing:

- **`Darkness`** — `Lux` / `Sun` / `Either` / `Always`. `Always` is for rooms with no daylight;
  `Sun` for outdoors. `LuxHysteresis` stops flapping at the threshold.
- **`VacancyTimeoutSeconds`** — 120 for a hallway, 1800 for a media room.
- **`IgnoreWhenOn`** — block auto-on while something is on (a projector, a do-not-disturb flag).
- **`RespectSleepMode` / `SleepBlocksAutoOn`** — bedroom-adjacent behaviour.
- **`SkipAwaySweep`** — outdoor and security lights stay on when the house empties.
- **`WelcomeHome`** — entry zones light on first arrival if it's dark.
- **`MaxBrightnessPct` on the night period** — the 03:00 rule; caps *every* command, including
  welcome-home.

## Failure behaviour

Deliberately split, because these deserve different answers:

- **Bad global config** (a person or kill switch HA doesn't know) → the app **throws**, lands in
  `ApplicationState.Error`, and posts a persistent notification listing every problem. The host and
  all your other apps keep running.
- **Bad zone config** (an area id that doesn't exist) → that **zone is skipped**, the rest run. One
  aggregated notification, and it lists every real area id on your instance — which is the fastest
  way to fix the file. A renamed entity must not black out the whole house.

## Where it lives

The engine is entirely in `AdaptiveLighting/` and never references a generated entity type. That's
deliberate and it's why `House` and `Cabin` are each a ~40-line bootstrap plus a YAML file —
generated types are per-project and can never move to `AdaptiveLighting`, so the engine is written against
`IHaContext` / `IHaRegistry` / `IScheduler` and three small interfaces of its own instead.

A **Blazor UI** served by each host (LAN-only, no auth) does three things. The **dashboard** is the
house-state hub: the master enable switch (clickable), who's home, which house mode is active, and
the live per-zone stories — what the lights are doing, what happened last, a countdown to the next
change — with "unknown" a first-class value distinct from "not connected". The **config editor**
actually configures the system from the browser: pick an area from a dropdown and watch its lights,
motion sensors and lux sensor resolve live; edit zones, periods, and house modes in collapsible
cards; save validates, writes atomically, and rebuilds the running engine in place — no YAML, no
restart. The config file lives *outside* the publish tree, so a redeploy can't wipe your edits.

## To make it actually work

1. **Fill in the ids.** A fresh YAML ships `REPLACE_ME` placeholders on purpose: nobody writing the
   file can see your registry, and an id that merely *looks* plausible would quietly command the
   wrong device. Once a host runs once, the config editor's pickers offer your real entities by name.
2. Start the host once. Bad global ids fail loudly (the app posts a notification listing every
   problem and lands in `Error`, host still up); bad zone ids just skip that zone, and the
   notification lists your real area ids.
3. Fill them in — from the UI once it's running, or by editing the YAML — and the engine rebuilds.
4. Deploying needs the **V6 add-on** (`netdaemon6`), and its port mapped in the add-on's Network
   panel to reach the UI.

## Status — what's proven, and what isn't

**It's live.** The engine runs on **a live instance** and controls real lights across three zones
(Stue, Tilbygg, Kjokken). The things this document once flagged as unproven have now happened in the
house: motion → darkness gate → circadian target → `light.turn_on` → vacancy timeout → dim → off,
the whole cycle, unprompted; the dashboard carries real snapshots; discovery resolves real entities;
and override detection positively identifies the engine's own commands by user id. House modes,
scenes and their reset triggers are configured from the dashboard and confirmed switching real modes.
**367 tests** cover the engine and its web services (plus 120 for the separate ClaudeDesk component on
the same branch).

Still open, and honest: **the house host is built and tested but not deployed** — only the
cabin is proven. Sleep is now a **mode kind**, not a helper the engine creates: a `Sleep` option on
the husmodus select clamps sleeping zones to a named period, so the old MQTT-helper plan is retired.
`MinBrightnessPct: 5` on the night period is an invented default worth reviewing. Concurrency is
asserted by inspection — `TestScheduler` is single-threaded.

Known gaps and design questions are tracked in the repository's issues.
