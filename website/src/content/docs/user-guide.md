---
title: "User guide"
description: "Living with the system day to day: the dashboard, house modes, and why a light did not turn on."
---

A short, practical guide to living with the system: what each screen does, and how to do the
things you'll actually want to do. For the design and internals, see [Overview](/overview/),
[Configuration](/configuration/) and [Architecture](/architecture/); this one is for using it.

> **Screenshots:** the slots below are marked `📷 [screenshot: …]`. Drop images into
> `website/public/screenshots/` and replace each slot with `![caption](/screenshots/name.png)`.

The web UI runs on the LAN at **http://<host>:10000**. No login — it's LAN‑only. There are two pages:
the **Dashboard**, and **Configuration**, which holds four sections — **Areas**, **Schedule**,
**House modes** and **House**.

---

## What it does, in one paragraph

Lights come on when you walk into a room — at a brightness and warmth that suit the time of day —
**but only if it's actually dark**. They dim as a warning before switching off, so they never drop on
someone sitting still. Touch a switch and the automation backs off and leaves your setting alone for a
while. When the house empties the lights sweep off; the first person home is met by the entry lights.
One master switch stops everything while still letting you see what it *would* have done.

---

## The first ten minutes

Nothing needs typing, and nothing happens to your lights until you say so.

1. **Start it and wait.** About half a minute after it connects, set‑up reads Home Assistant's areas
   and writes down every room that has both a light and a motion sensor. It guesses each room's role
   from its name — a bedroom respects sleep mode, a hallway lights on arrival, an outdoor area stays on
   when the house empties — adopts a house‑mode dropdown if it finds an obvious one, and fills in the
   list of people from Home Assistant.
2. **Every room it found is switched off.** The dashboard says so: *"Set‑up found 17 rooms. None are
   switched on yet, so no lights will change."*
3. **Choose the rooms.** Follow the button through to **Configuration → Areas**, where the rooms are
   listed by floor with a switch on each. Turn on the ones you want the system to run — a floor at a
   time with *Switch on this floor*, if that is easier — and press **Save and apply**.
4. The dashboard now shows a card per room you chose, and the lights start behaving.

You can come back and switch a room off at any time. A switched‑off room is still watched and still
reported — the one thing it never gets is a command.

---

## The dashboard

📷 [screenshot: the full dashboard — master switch, house strip, room cards]

Top to bottom, it answers the questions you ask in order:

- **Adaptive lighting: On / Off** — the master switch. Tap it to pause or resume *all* automatic
  lighting. When it's off nothing changes until you turn it back on.
- **House mode** — which mode the house is in (Normal, and any you've set up like *Borte*/*Sover*),
  with buttons to switch it, and one plain line on what it means right now.
- **Who's home** — each person and whether they're home.
- **Rooms** — one card per room you have switched on, told as a short story: what the lights are doing,
  what happened last, and what happens next. If Home Assistant knows which floor a room is on, the
  cards are grouped under floor headings; a house with no floors set sees one plain grid, as before.

Rooms you have switched off get no card, and a line under the grid says how many are hidden and where
to turn them back on. A room you disabled by accident never silently disappears.

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

## Areas (the rooms)

📷 [screenshot: Configuration → Areas, the floor‑grouped room list]

Every room the system knows lives here, grouped by Home Assistant floor. Rooms not listed here are never
touched. Usually you pick the Home Assistant **area** and its lights, motion sensors and lux sensor are
found automatically — open a room and the preview shows what was found.

Each row carries the room's switch, its name, its area slug, and a one‑line summary of how far it has
strayed from the shared settings ("all automatic", "2 of 16 changed"). The **coloured left edge** is the
same language the dashboard speaks: blue while the engine is driving, amber while somebody's hand change
is holding, grey when idle — and flat grey when the room is switched off.

Open a room to change anything about it on its own: a custom name, hand‑picked lights or motion sensors,
something that blocks auto‑on while it's on, and an override of any shared setting.

### Switching rooms on and off

The switch on a room's row is the room's power. Off means the engine watches the room and reports on it
but never commands it. Where a floor has a heading, *Switch on this floor* does the whole floor at once.
Like every other edit, a switch is not live until you press **Save and apply** — *Discard changes* puts
it back.

### Setting a room up again

If a room has drifted — you moved the lights, renamed things, or hand‑edited yourself into a corner —
**Set up rooms again** (whole house) and **Set this room up again** (one room) rebuild it from what Home
Assistant knows right now, as if it were newly found.

This throws away what you changed by hand, so it warns first and is concrete about it, per room:
*"Stue — loses 2 hand‑picked lights and 3 changed settings"*. A room with nothing to lose says so. Two
things always survive a rebuild: which area the room is, and whether it is switched on. Rooms you didn't
tick are untouched, rooms Home Assistant has newly qualified are added switched off, and nothing is
written until you save.

Below the room list are the settings that apply to rooms in general, as two folds:

- **All rooms** — the baseline every room starts with, in three boxes: *Movement & timing* (how long
  lights stay on, the warning dim, how long hand changes hold), *Darkness* (how a room decides it's dark,
  the lux threshold, the sun height, the outdoor light sensor, the fades) and *Room behaviour* (respects
  sleep mode, sleep blocks auto‑on, stays on when everyone leaves, welcome home). A room's own settings
  win over these.
- **Finding lights & sensors** — the Home Assistant labels that steer discovery: *Only manage lights
  with* (leave empty to manage every light found), *Never touch*, and *Counts as motion*. The device
  classes that qualify a sensor sit behind an Advanced fold, because almost nobody needs them.

---

## Schedule (the day)

📷 [screenshot: Configuration → Schedule, with the daylight chart]

The schedule is the house‑wide circadian table, made of **periods**: **when** each slice of the day starts
and **how bright / how warm** the lights are then. Boundaries are clock times or sun events
(`sunset-01:00`) that move with the seasons — the chart shows the year at a glance, with today's daylight
band. The period in force now has the **orange edge and "active now" badge**. A period can optionally
**set a house mode** when it starts (e.g. Night → Sover).

**Blend between periods** lives here too: lights drift towards the next period's level over the blend
window instead of stepping at the boundary.

---

## House

📷 [screenshot: Configuration → House, the People and Master switch groups]

The installation itself: its **name** (a label for logs and notifications), **who lives here** and how
long everyone must be gone before rooms react to an empty house, and the **master switch**. Leave the
people list empty and it means everyone Home Assistant knows, including people added later.

One **Fine tuning** fold at the bottom holds the things you set once and forget: the NetDaemon user id,
how often rooms re‑check the world, how long the app recognises its own changes, whether other
automations count as a person at the switch, and the "close enough" tolerances.

---

## Troubleshooting

**A light didn't turn on when I walked in.** Check the room's card on the dashboard. The usual cause is
the **darkness gate**: the card will say *"Too bright to turn on right now (lux 86, dark below 40)."* In a
Norwegian summer evening it's genuinely bright until late, so raise **Dark below** for that room (or for
**All rooms**), or set **How a room decides it's dark** to Sun. The same reasoning is in the add‑on log:

```
Motion in Tilbygg but auto-on is blocked: not dark enough (lux 86, dark below 40)
```

**A room I expected has no card.** It is switched off. The line under the grid says how many rooms are —
turn it on in **Configuration → Areas**.

**Nothing is changing at all.** Check the **master switch** on the dashboard — if it's Off, everything is
paused on purpose. The room cards still show what each room *would* be doing.

**The mode won't switch / I don't see my modes.** Make sure the dropdown helper is picked in
**Configuration → House modes** and has options. The dashboard's House‑mode card links straight there.

**No lights are found in a room.** If **Only manage lights with** is set, only lights carrying that Home
Assistant label are managed — a room whose lights are all unlabelled resolves to nothing and is skipped,
and the message names the label. Either label the lights in Home Assistant or clear the field, which goes
back to managing every light found.

**My own Home Assistant automation stopped firing.** From 2.0 the published event is
`adaptive_lighting_area` with an `area` field, not `laget_lighting_zone` with a `zone` field. Nothing
publishes the old name any more; update the automation or dashboard card by hand.

**My edits disappeared after a deploy.** They shouldn't — the live config lives outside the deploy folder
and survives. If a room stops resolving, it's usually a renamed Home Assistant entity; the validator lists
the real ids.
