---
title: "How to use it"
description: "The first ten minutes, the four screens, and what to do when a light does not come on."
---

Install it, let it find your rooms, switch on the ones you trust it with, and tune from there. Nothing
happens to your lights until you say so.

For what the system does once it is running, see [How it works](/overview/).

:::note[Screenshots]
The slots below are marked `📷 [screenshot: …]`. Drop images into `website/public/screenshots/` and
replace each slot with `![caption](/screenshots/name.png)`.
:::

## 1. Install it

Adaptive lighting runs inside a [NetDaemon](https://netdaemon.xyz) host, on the **NetDaemon V6**
add-on. It needs .NET 10.

```bash
dotnet add package AdaptiveLighting
dotnet add package AdaptiveLighting.Web   # the board, the room pages and the settings
```

Three things then need doing in your host, and they are all in the
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

Nothing needs typing. About thirty seconds after the connection settles — long enough for Home
Assistant's entities to arrive — set-up runs once and:

- writes down **every Home Assistant area that has both a light and a motion sensor**;
- **guesses each room's role from its name**, so a bedroom is held to night levels and never lights
  itself, a bathroom gets the night levels but still lights, a hallway or stairwell lights on
  arrival, and a terrace or garage stays on when the house empties;
- **adopts a house-mode dropdown** if it finds an obvious one;
- **fills in the list of people** from Home Assistant.

Then it stops. **Every room it found is switched off**, and the board says so:

> Setting up found 17 rooms. None are switched on yet, so no lights will change.

Under that line are the rooms it found, each with how many lights it holds, and one button.

📷 [screenshot: the board's first-run state — the found-rooms line, the grey room chips, and the
"Choose which rooms to switch on" button]

Set-up runs once. A house that deliberately has no rooms does not find them grown back after a
restart. If it finds nothing at all it does *not* mark itself done — that usually means Home
Assistant was still waking up — so it looks again on the next restart.

<!-- TODO (docs): the first-run wizard section goes here, once it has shipped. Do not write it from
     the design — wait for the built version and describe what it actually asks, in what order, and
     what it leaves switched off. Screenshot marker 1 above photographs this same surface, so the two
     have to be updated together. -->

## 3. Choose which rooms to switch on

Follow the button through to **Configuration → Areas**. Rooms are listed by floor, each with a
switch.

📷 [screenshot: Configuration → Areas — the floor-grouped room list with its switches and the
per-floor "Switch on this floor" action]

- Turn on the rooms you want it to run. A hallway is the easiest room to trust first.
- *Switch on this floor* does a whole floor at once, where Home Assistant knows your floors.
- Press **Save and apply**. *Discard changes* puts everything back.

When you switch a room on, a note names **every light that room will now command**, and marks the ones
that look like something other than room lighting — status LEDs, indicator lights, an RGBW lamp's
colour channels, a light inside an appliance. If the list is full of things you did not mean, make a
label in Home Assistant, put it on your real room lights, and name it under **House → Finding lights
& sensors → Only manage lights with**. That setting reaches every room.

A room you leave switched off is still watched and still reported. The one thing it never gets is a
command.

## 4. Watch the board

The board is the home page. It answers "is anything odd, and what happens next?".

📷 [screenshot: the board on a normal evening — the house bar, the exception tray, and the lanes with
the now-line]

Top to bottom:

- **The house bar** — the house mode with buttons to change it, who is home, and **last of the
  three, the master switch that pauses everything**. It is the same small on/off switch a room page
  has, not a big button, and it comes last on purpose: it is touched perhaps twice a year, while the
  other two are read every day. Nothing says "all is well" — that would be a permanent line saying
  nothing happened. It speaks up the moment anything is wrong: a line under it and a colour on the
  panel's edge when it is paused or unreachable, and when it is paused, a notice across the page as
  well.
- **The exception tray** — one line per room that is doing something other than following the
  schedule. There are exactly four: a warning dim running, somebody's setting standing, somebody
  having switched the lights off, and a scene holding the room. When no room is doing any of them,
  the tray is one sentence saying the rest are doing what the schedule says.
- **The lanes** — one line per room on a shared time axis, grouped under floor headings, showing the
  **last four hours** behind the now-line and the **next two** after it. A room behaving normally has
  an empty track, and past six rooms the quiet ones drop to chips rather than claiming a row.
- **What's worth knowing** — the exceptions, newest first, each line naming the room, what it did and
  why. Room names link through to their pages. Deliberately short: the lanes above have already drawn
  every movement and every light that came on, so this list carries only what a picture cannot say —
  somebody overriding the engine by hand, the engine declining to light a room and why, a change of
  house mode, and the house emptying, filling or being switched off. The line under it counts the
  everyday reports waiting on the Activity page.

Rooms you have switched off get no lane. A line under the board says how many are hidden and where to
turn them back on.

## 5. Tune one room

Click a room name anywhere to open its page at `/room/<area id>`.

📷 [screenshot: a room page — the header with the state pill and the room's switch, the behaviour
sentences with their values as tappable tokens, and the facts panel]

The page has:

- **The header** — the room's name, what it is doing now, how long it has been doing it, and the
  room's own on/off switch.
- **Right now — what the engine saw** — the readings behind the claim, in the order you are likely to
  want them. *Dark enough?* first, answered yes or no with the actual reading underneath it, because
  that is what you came to find out. When something is stopping movement from lighting the room, an
  *If someone walks in* row follows it and says what. Then the lights and their levels, the last
  movement, the last change, and the time of day the room is following. There is no *State* row — the
  header an inch above already says what the room is doing. If the master switch is off, that gets a
  row of its own, above everything, because nothing else on the page matters while it is.
- **Brightness & warmth** — what this room runs in each period of the day, one row per period, with
  the one in force now marked *now*. Every row starts out following the schedule and is drawn quietly
  to say so. Change a brightness or a warmth here and only this room changes; an amber dot marks what
  you chose, and under it is the way back to the schedule's own value.

  Underneath it, **Brighten with daylight**, off until you switch it on. On, a bright day lifts this
  room above the schedule so it does not read as gloomy against a bright window — a chart shows the
  shape, with the room's current reading marked on it. It only ever adds light.
- **How this room behaves** — the room's settings as sentences with the values written into them:

  > Lights when someone moves and it's darker than **1000 lx**. After **10 min** without movement,
  > dim to **50 %** for **30 s**, then off.
  >
  > Manual changes hold for **2 h**; after somebody switches them off manually, movement is ignored
  > until the room has been empty **10 min**.

  Tap any value to pick a different one from a short list. **All settings** reveals the rest as five
  folded sections — each names what it holds, and opens when you tap it — plus one more for what the
  room *is*: its name, and which Home Assistant area it stands for. The line beside the button says
  how many of the settings are this room's own rather than the house's.
- **In this room** — the lights and sensors that were found, as chips. The **×** on a chip leaves that
  entity out of this room, and the exclusions are listed so you can put one back. *Not right? Pick by
  hand* replaces the automatic choice for one list at a time. At the bottom sit the two actions that
  throw work away: *Set this room up again* and *Remove this room*. Each says what it costs before you
  press it.
- **What happened here** — the log, filtered to this room.

Changes on a room page apply about a second after you make them; there is no save button. A
switched-off room's page is short, and offers to turn the room on.

## 6. Find out why a light did not come on

The **Activity** page is the whole house's decisions, newest first.

📷 [screenshot: Activity — the room filter, the category chips with their counts, and a
"Nothing happened" row carrying its lux reading]

Filter by room, and by category: *Movement*, *Light change*, *Darkness*, *Manual changes*, *Nothing
happened*, *Mode changes*, *House*, and *Background tasks* — which starts hidden because it is the
noisiest and says least. The other seven start showing.

*Nothing happened* is the one to reach for. It carries the reason the engine declined, with the
evidence:

> Too bright to switch on · lux 86, dark below 40

New entries are counted as they arrive but are not inserted under you; a button adds them when you
are ready.

## 7. The settings

**Configuration** holds four sections.

**Areas** — your rooms, by floor, each with a switch and a way into its page. *Add a room* offers the
Home Assistant areas that do not have one yet; it arrives switched off. *Set up rooms again* rebuilds
chosen rooms from what Home Assistant knows right now, and warns first, per room, about what each one
loses. Two things always survive a rebuild: which area the room is, and whether it is switched on.

**Schedule** — how bright and how warm the lights are through the day, as periods on a daylight chart
that shows the year at a glance.

📷 [screenshot: Configuration → Schedule — the daylight chart and the period table]

A period starts at a clock time (`22:30`) or a sun event with an offset (`sunset-01:00`), so it moves
with the seasons. Each period sets a brightness and a colour temperature, and can cap or floor how
bright any light gets while it runs — that cap is what stops a motion event putting 100 % in your face
at 03:00. A period can also set a house mode when it begins. *Blend between periods* lets the lights
drift to the next period's level instead of stepping at the boundary.

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
- **Finding lights & sensors** — the three labels (only-manage / never-touch / counts-as-motion), the
  house's outdoor light sensor, and the device classes behind a fold.
- **People** — who is watched for presence, and how long everyone must be gone before rooms react to
  an empty house. Leave the list empty and it means everyone Home Assistant knows, including people
  added later.
- **Master switch** — the one switch that pauses everything.
- **House name**, and a **Fine tuning** fold for the things you set once.
- **This installation** — whether the engine is running, whether Home Assistant is answering, and
  where your settings file is kept.

Every edit here waits for **Save and apply**, which validates, writes, and rebuilds the running engine
in place. No restart.

## 8. Themes

The picker at the right-hand end of the top bar offers **Follow the system** (the default), **Light**,
**Dark** and **0z0 tech**. The choice is kept in your browser, so two people can read the same house
in different colours and neither has anything to save.

## Troubleshooting

**A light did not come on when I walked in.** Open the room and read *Right now — what the engine
saw*, or filter the Activity page to that room and the *Nothing happened* category. The usual answer
is that the room did not count as dark, and the row names the reading and the threshold. Either raise
*Dark below* for that room until the reading falls under it, or set *How the room decides it's dark*
to **Sun** or **Always dark**.

**The room has no light-level sensor and still would not light.** On *Sensor*, the default, that is
not the darkness gate — a room with nothing to read counts as dark, whether it never had a sensor or
its sensors have all stopped reporting. Look for another reason in the same rows: the master switch,
the room's own switch, an empty house, a guest scene, a sleeping house, or an entity named under
*Blocked while on*.

**A room I expected has no lane.** It is switched off. The line under the board says how many are, and
links to where you turn them back on.

**Nothing is changing at all.** Check the master switch in the house bar. While it is off, everything
is paused on purpose, and the board still shows what each room would be doing.

**A room's name reads as a slug.** The room is named after its Home Assistant area, resolved every
time it is shown. If it reads `kjeller_bad`, Home Assistant is not answering — the display name comes
from the registry.

**No lights were found in a room.** If *Only manage lights with* is set, only lights carrying that
label are managed, and a room whose lights are all unlabelled resolves to nothing. The message names
the label. Either label the lights in Home Assistant, or clear the field, which goes back to managing
every light found.

**Some rooms are listed as skipped.** A room that cannot be resolved is skipped and the rest keep
running. The message names the real Home Assistant ids, which is the fastest way to fix it.

**My own Home Assistant automation stopped firing.** From 2.0 the published event is
`adaptive_lighting_area` with an `area` field, not `laget_lighting_zone` with a `zone` field. Nothing
publishes the old name any more. Update the automation or dashboard card by hand — and match on the
new `area_id` field rather than the display name, which is editable.

**My edits disappeared after a deploy.** The live configuration file must live outside your deploy
folder. **Configuration → House → This installation** shows where it is.
