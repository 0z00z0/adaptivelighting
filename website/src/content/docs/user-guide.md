---
title: "User guide"
description: "Living with the system day to day: the dashboard, house modes, and why a light did not turn on."
---

A short, practical guide to living with the system: what each screen does, and how to do the
things you'll actually want to do. For the design and internals, see the numbered documents in this
folder; this one is for using it.

> **Screenshots:** the slots below are marked `📷 [screenshot: …]`. Drop images into
> `docs/adaptive-lighting/screenshots/` and replace each slot with `![caption](screenshots/name.png)`.

The web UI runs on the LAN at **http://<host>:10000** (the cabin is ha‑p). No login — it's LAN‑only.
There are three tabs: **Dashboard**, **Zones/Periods/House modes** (under Configuration), and
**Configuration** last.

---

## What it does, in one paragraph

Lights come on when you walk into a room — at a brightness and warmth that suit the time of day —
**but only if it's actually dark**. They dim as a warning before switching off, so they never drop on
someone sitting still. Touch a switch and the automation backs off and leaves your setting alone for a
while. When the house empties the lights sweep off; the first person home is met by the entry lights.
One master switch stops everything while still letting you see what it *would* have done.

---

## The dashboard

📷 [screenshot: the full dashboard — master switch, house strip, room cards]

Top to bottom, it answers the questions you ask in order:

- **Adaptive lighting: On / Off** — the master switch. Tap it to pause or resume *all* automatic
  lighting. When it's off nothing changes until you turn it back on.
- **House mode** — which mode the house is in (Normal, and any you've set up like *Borte*/*Sover*),
  with buttons to switch it, and one plain line on what it means right now.
- **Who's home** — each person and whether they're home.
- **Rooms** — one card per managed room, told as a short story: what the lights are doing, what
  happened last, and what happens next.

### Reading a room card

📷 [screenshot: one room card, ideally the "too bright" state]

Each card shows the room's state (auto · on, standing by, dimming, set by hand, …), a countdown to the
next change, and chips for **darkness**, **period** and **house mode**. The important one to know:

> **"Too bright to turn on right now (lux 86, dark below 40). Movement will light it once it's dark."**

That's the darkness gate talking. The room saw movement but didn't light because it isn't dark enough —
and it now tells you the exact reading and the threshold. This is the single most common "why didn't my
light come on?" answer (see [Troubleshooting](#troubleshooting)).

---

## House modes

📷 [screenshot: Configuration → House modes, the option cards]

The house mode is a Home Assistant **dropdown helper** (`input_select`). Each option you add is tagged
with one **kind**:

| Kind | What it does |
|---|---|
| **Normal** | Everyday lighting. This is the one the house resets *back* to. Mark exactly one option Normal. |
| **Sleep** | Sleep‑respecting rooms are held to a dim "night" level instead of the clock's. |
| **Away** | Runs a scene (or sweeps the lights off) and pauses automatic lighting until a reset fires. |
| **Guest** | Runs a scene and holds the rooms; otherwise like Normal. |

Switch modes from the **dashboard**. The card with the **orange edge and "active now" badge** is the one
you're in right now.

### Setting up an Away mode with a scene

1. Create a dropdown helper in Home Assistant (**Settings → Devices & services → Helpers → Dropdown**),
   add your options (e.g. `Hjemme`, `Borte`, `Sover`), and save.
2. In **Configuration → House modes**, pick the helper, then classify each option.
3. On the Away option: set the kind to **Away** and choose **"Activate this scene when entering mode"**
   (e.g. `scene.borte_belysning`). Leave the scene empty and Away simply sweeps the lights off.
4. Add a **reset trigger** so it comes back on its own — most commonly **Reset on presence** (someone
   moving switches it back to Normal after a short grace).

### Auto‑away: switch to a mode when nothing moves

📷 [screenshot: the "Activate when no movement for" control]

On any non‑Normal mode you can tick **"Activate when no movement for"** and set an hour/minute window
(default **6 h**). After the whole house has been that long with no motion, it switches to that mode by
itself. Pair it with a presence reset for the natural loop:

> **empty for 6 h → Away, someone moves → Normal.**

---

## Zones (rooms)

📷 [screenshot: Configuration → Zones, a couple of zone cards]

A **zone** is a room the engine manages. Rooms you don't list are never touched. Usually you just pick
the Home Assistant **area** and its lights, motion sensors and lux sensor are found automatically — the
preview shows what was found. Each zone can override any default (timeouts, darkness source, and so on).

---

## Periods (the daily schedule)

📷 [screenshot: Configuration → Periods, with the daylight chart]

Periods are the house‑wide circadian table: **when** each slice of the day starts and **how bright /
how warm** the lights are then. Boundaries are clock times or sun events (`sunset-01:00`) that move with
the seasons — the chart shows the year at a glance, with today's daylight band. The period in force now
has the **orange edge and "active now" badge**. A period can optionally **set a house mode** when it
starts (e.g. Night → Sover).

---

## Defaults & Advanced

📷 [screenshot: Configuration → Defaults, the Sleep and Away boxes]

- **Defaults** — the baseline every room starts with. The behaviour toggles are grouped into a **Sleep**
  box (respect sleep, block auto‑on while asleep) and an **Away** box (skip the away sweep, welcome home).
- **Advanced settings** — house‑wide things you set once: who counts as present, the kill switch, override
  tuning, discovery labels.

Both open as collapsed groups — open the one you came for.

---

## Troubleshooting

**A light didn't turn on when I walked in.** Check the room's card on the dashboard. The usual cause is
the **darkness gate**: the card will say *"too bright to turn on right now (lux 86, dark below 40)."* In a
Norwegian summer evening it's genuinely bright until late, so raise the room's (or the default) **lux
threshold**, or switch its **darkness source** to Sun. The same reasoning is in the add‑on log:

```
Motion in Tilbygg but auto-on is blocked: not dark enough (lux 86, dark below 40)
```

**Nothing is changing at all.** Check the **master switch** on the dashboard — if it's Off, everything is
paused on purpose. The room cards still show what each room *would* be doing.

**The mode won't switch / I don't see my modes.** Make sure the dropdown helper is picked in
**Configuration → House modes** and has options. The dashboard's House‑mode card links straight there.

**My edits disappeared after a deploy.** They shouldn't — the live config lives outside the deploy folder
and survives. If a room stops resolving, it's usually a renamed Home Assistant entity; the validator lists
the real ids.
