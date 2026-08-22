---
title: "How to use it"
description: "The first ten minutes, the four screens, and what to do when a light does not come on."
---

Install it, let it find your rooms, switch on the ones you want it to run, and tune from there.
Nothing happens to your lights until you say so.

For what the system does once it is running, see [How it works](/overview/).

:::note[Screenshots]
The slots below are marked `📷 [screenshot: …]`. Drop images into `website/public/screenshots/` and
replace each slot with `![caption](/screenshots/name.png)`.
:::

## 1. Install it

Adaptive lighting runs inside a [NetDaemon](https://netdaemon.xyz) host, on the **NetDaemon V6**
add-on. It needs .NET 10.

```bash
dotnet add package AdaptiveLighting.NetDaemon   # engine, board, room pages and settings
```

:::note[One extra step: these packages are on GitHub, not nuget.org]
The command above will not find anything until you add the feed. GitHub asks for a sign-in on every
read, even for a public package, so you need a [token](https://github.com/settings/tokens) with the
**`read:packages`** scope.

Add the feed to your own `nuget.config`:

```xml wrap
<packageSources>
  <add key="0z00z0" value="https://nuget.pkg.github.com/0z00z0/index.json" />
</packageSources>
```

Then store the token once, so it never ends up in a file you commit:

```bash wrap
dotnet nuget update source 0z00z0 --username YOUR_GITHUB_USERNAME --password YOUR_TOKEN --store-password-in-clear-text
```

Latest preview: **2.0.0-preview.4**. See the
[releases](https://github.com/0z00z0/adaptivelighting/releases).
:::

Three things then need doing in your host, all covered in the
[README](https://github.com/0z00z0/adaptivelighting#quick-start):

1. **Point it at a configuration file** in `appsettings.json`. Put the file **outside your deploy
   folder**, or a redeploy wipes every edit you make in the browser.
2. **Add a small NetDaemon app** that hands the engine your Home Assistant connection.
3. **Serve the UI** on a port — the quick start uses 10000.

Map that port in the add-on's **Network** panel, and the UI answers at `http://<host>:10000`.

:::caution[There is no login]
Anyone who can reach that port can rewrite your lighting configuration. Keep it on your LAN. Do not
port-forward it. If you need it from outside, put it behind Home Assistant ingress or an
authenticating reverse proxy.
:::

## 2. Start it and wait half a minute

About thirty seconds after the connection settles, set-up runs once and:

- writes down **every Home Assistant area that has both a light and a motion sensor**;
- **guesses each room's role from its name** — a bedroom is held to night levels and never lights
  itself, a bathroom gets the night levels but still lights, a hallway or stairwell lights on
  arrival, and a terrace or garage stays on when the house empties;
- **adopts a house-mode dropdown** if it finds an obvious one;
- **fills in the list of people** from Home Assistant.

**Every room it found is switched off**, and the board says so:

> Setting up found 17 rooms. None are switched on yet, so no lights will change.

Under that line are the rooms it found, each with how many lights it holds, and one button.

📷 [screenshot: the board's first-run state — the found-rooms line, the grey room chips, and the
"Choose which rooms to switch on" button]

Set-up runs once, so deleted rooms do not come back after a restart. If it finds nothing at all it
does *not* mark itself done, and looks again on the next restart.

## 3. Choose which rooms to switch on

The button leads to **Configuration → Areas**. Rooms are listed by floor, each with a switch.

📷 [screenshot: Configuration → Areas — the floor-grouped room list with its switches and the
per-floor "Switch on this floor" action]

- Turn on the rooms you want it to run.
- *Switch on this floor* does a whole floor at once, where Home Assistant knows your floors.
- Press **Save and apply**. *Discard changes* puts everything back.

When you switch a room on, a note names **every light that room will now command**, and marks the
ones that look like something other than room lighting — status LEDs, indicator lights, an RGBW
lamp's colour channels, a light inside an appliance. If the list holds things you did not mean, make
a label in Home Assistant, put it on your real room lights, and name it under **House → Finding
lights & sensors → Only manage lights with**. That setting reaches every room.

A room you leave switched off is still watched and still reported, but never commanded.

## 4. Watch the board

The board is the home page: what is odd, and what happens next.

📷 [screenshot: the board on a normal evening — the house bar, the exception tray, and the lanes with
the now-line]

Top to bottom:

- **The house bar** — the house mode with buttons to change it, who is home, and the master switch
  that pauses everything. It speaks up when anything is wrong: a line under it and a colour on the
  panel's edge when the engine is paused or unreachable, plus a notice across the page while paused.
- **The exception tray** — one line per room doing something other than following the schedule.
  There are four: a warning dim running, somebody's setting standing, somebody having switched the
  lights off, and a scene holding the room. When no room is doing any of them, the tray is one
  sentence saying so.
- **The lanes** — one line per room on a shared time axis, grouped under floor headings, showing the
  **last four hours** behind the now-line and the **next two** after it. A room behaving normally
  has an empty track, and past six rooms the quiet ones drop to chips.
- **What's worth knowing** — the exceptions, newest first, each line naming the room, what it did
  and why. Room names link through to their pages. It carries only what the lanes cannot draw:
  somebody overriding the engine by hand, the engine declining to light a room and why, a change of
  house mode, and the house emptying, filling or being switched off. The line under it counts the
  everyday reports waiting on the Activity page.

Rooms you have switched off get no lane. A line under the board says how many are hidden and where
to turn them back on.

## 5. Tune one room

Click a room name anywhere to open its page at `/room/<area id>`.

📷 [screenshot: a room page — the header with the state pill and the room's switch, the behaviour
sentences with their values as tappable tokens, and the facts panel]

The page has:

- **The header** — the room's name, what it is doing now, how long it has been doing it, and the
  room's own on/off switch.
- **Right now — what the engine saw** — *Dark enough?* first, answered yes or no with the actual
  reading underneath. When something is stopping movement from lighting the room, an *If someone
  walks in* row follows and says what. Then the lights and their levels, the last movement, the last
  change, and the time of day the room is following. If the master switch is off, that gets a row
  above everything else.
- **Brightness & warmth** — what this room runs in each period, one row per period, with the one in
  force now marked *now*. Every row follows the schedule until you change it; an amber dot marks
  what you chose, and under it is the way back to the schedule's own value.

  Underneath it, **Brighten with daylight**, off until you switch it on. On, a bright day lifts this
  room above the schedule — a chart shows the shape, with the room's current reading marked on it.
  It only ever adds light.
- **How this room behaves** — the room's settings as sentences with the values written into them:

  > Lights when someone moves and it's darker than **1000 lx**. After **10 min** without movement,
  > dim to **50 %** for **30 s**, then off.
  >
  > Manual changes hold for **2 h**; after somebody switches them off manually, movement is ignored
  > until the room has been empty **10 min**.

  Tap any value to pick a different one from a short list. **All settings** reveals the rest as five
  folded sections, plus one more for the room's name and which Home Assistant area it stands for.
  The line beside the button says how many of the settings are this room's own rather than the
  house's.

  *Movement & timing* holds the timings. *Don't switch on while* names things that stop the room
  lighting up — a projector, a do-not-disturb switch. *Don't switch off while* is the opposite:
  while one of those is on, the room will not turn itself off. Either list can be turned round with
  *while these are off instead*, for a switch you turn **off** to mean the same thing.

  The two scene rows swap one moment for a scene of yours. *Run a scene instead, on movement*
  replaces switching on; *Run a scene instead, when empty* replaces switching off. Set one, both or
  neither. Everything that could refuse to light the room still refuses; a scene changes *what*
  happens, never whether it happens.
- **In this room** — the lights and sensors that were found, as chips. The **×** on a chip leaves
  that entity out of this room, and the exclusions are listed so you can put one back. *Not right?
  Pick by hand* replaces the automatic choice for one list at a time. At the bottom sit *Set this
  room up again* and *Remove this room*; each says what it costs before you press it.
- **What happened here** — the log, filtered to this room.

Changes on a room page apply about a second after you make them; there is no save button. A
switched-off room's page is short, and offers to turn the room on.

## 6. Find out why a light did not come on

The **Activity** page is the whole house's decisions, newest first.

📷 [screenshot: Activity — the room filter, the category chips with their counts, and a
"Nothing happened" row carrying its lux reading]

Filter by room, and by category: *Movement*, *Light change*, *Darkness*, *Manual changes*, *Nothing
happened*, *Mode changes*, *House*, and *Background tasks* — which starts hidden. The other seven
start showing.

*Nothing happened* carries the reason the engine declined, with the evidence:

> Too bright to switch on · lux 86, dark below 40

New entries are counted as they arrive but are not inserted under you; a button adds them when you
are ready.

## 7. The settings

**Configuration** holds four sections.

**Areas** — your rooms, by floor, each with a switch and a way into its page. *Add a room* offers
the Home Assistant areas that do not have one yet; it arrives switched off. *Set up rooms again*
rebuilds chosen rooms from what Home Assistant knows right now, and warns first, per room, about
what each one loses. Two things always survive a rebuild: which area the room is, and whether it is
switched on.

**Schedule** — how bright and how warm the lights are through the day, as periods on a daylight
chart that shows the year at a glance.

📷 [screenshot: Configuration → Schedule — the daylight chart and the period table]

A period starts at a clock time (`22:30`) or a sun event with an offset (`sunset-01:00`), so it
moves with the seasons. Each period sets a brightness and a colour temperature, and can set a house
mode when it begins. *Blend between periods* lets the lights drift to the next period's level
instead of stepping at the boundary.

What stops a motion event putting 100 % in your face at 03:00 is the night period's own brightness,
held by *Gentle while the house sleeps* on the rooms you want kept dim.

A period can instead **wait for movement**. Tick *Wait for movement before starting* and the period
before it keeps running — the house stays at night levels — until somebody moves, and then it begins
whole. Name the rooms under *Movement in*, or leave it empty for any room. It never fires before the
period's own start time, and it happens once a day.

Keep at least one clock-time boundary. Far north, a sun-anchored boundary can be unresolvable around
midsummer and midwinter, and a period that cannot be placed is skipped.

**House modes** — pick the Home Assistant dropdown helper that carries your modes, then tag each
option with what it means: Normal, Sleep, Away or Guest. An Away or Guest option can run a scene. A
non-Normal option can switch itself on when the house has had no movement for a set time, and can
reset to Normal on movement, at a time, or when a period starts. The mode you are in is marked
*active now*.

**House** — the settings there is exactly one of.

📷 [screenshot: Configuration → House — "Every room starts with these" above "Finding lights &
sensors"]

- **Every room starts with these** — the baseline every room inherits, written as the same sentences
  a room page uses, with the same *All settings* reveal underneath. A room's own settings win.
- **Finding lights & sensors** — the three labels (only-manage / never-touch / counts-as-motion),
  the house's outdoor light sensor, and the device classes behind a fold.
- **People** — who is watched for presence, and how long everyone must be gone before rooms react to
  an empty house. Leave the list empty and it means everyone Home Assistant knows, including people
  added later.
- **Master switch** — the one switch that pauses everything.
- **House name**, and a **Fine tuning** fold for the things you set once.
- **This installation** — whether the engine is running, whether Home Assistant is answering, and
  where your settings file is kept.

Every edit here waits for **Save and apply**, which validates, writes, and rebuilds the running
engine in place. No restart.

## 8. Themes

The picker at the right-hand end of the top bar offers **Follow the system** (the default),
**Light**, **Dark** and **0z0 tech**. The choice is kept in your browser.

## Troubleshooting

**A light did not come on when I walked in.** Open the room and read *Right now — what the engine
saw*, or filter the Activity page to that room and the *Nothing happened* category. The usual answer
is that the room did not count as dark, and the row names the reading and the threshold. Either
raise *Dark below* for that room until the reading falls under it, or set *How the room decides it's
dark* to **Sun** or **Always dark**.

**The room has no light-level sensor and still would not light.** On *Sensor*, the default, a room
with nothing to read counts as dark, so that is not the reason. Look for another in the same rows:
the master switch, the room's own switch, an empty house, a guest scene, a sleeping house, or an
entity named under *Don't switch on while*.

**A room will not switch itself off.** Something is named under *Don't switch off while* and is on —
or, if *while these are off instead* is ticked, is off. While that holds, the countdown, the warning
dim and the leaving sweep all leave the room alone. Switching it off by hand still works.

**A room dims to something soft instead of going dark.** That is *Run a scene instead, when empty*.
Clear it and the room goes off. The warning dim does not run first, because nothing is about to go
off.

**The morning never arrived.** If the morning period has *Wait for movement before starting* ticked,
nothing happens until somebody moves in one of the rooms named under *Movement in*. Check that list
holds a room people walk through first thing.

**A room I expected has no lane.** It is switched off. The line under the board says how many are,
and links to where you turn them back on.

**Nothing is changing at all.** Check the master switch in the house bar. While it is off,
everything is paused, and the board still shows what each room would be doing.

**A room's name reads as a slug.** The room is named after its Home Assistant area, resolved every
time it is shown. If it reads `kjeller_bad`, Home Assistant is not answering — the display name
comes from the registry.

**No lights were found in a room.** If *Only manage lights with* is set, only lights carrying that
label are managed, and a room whose lights are all unlabelled resolves to nothing. The message names
the label. Either label the lights in Home Assistant, or clear the field, which goes back to
managing every light found.

**Some rooms are listed as skipped.** A room that cannot be resolved is skipped and the rest keep
running. The message names the real Home Assistant ids.

**My own Home Assistant automation stopped firing.** From 2.0 the published event is
`adaptive_lighting_area` with an `area` field, not `laget_lighting_zone` with a `zone` field.
Nothing publishes the old name. Update the automation or dashboard card by hand, and match on the
`area_id` field rather than the display name, which is editable.

**My edits disappeared after a deploy.** The live configuration file must live outside your deploy
folder. **Configuration → House → This installation** shows where it is.
