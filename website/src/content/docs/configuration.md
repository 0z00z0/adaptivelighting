---
title: "Settings reference"
description: "Every setting, in the words the UI uses, with its default and its name in the file."
---

Every setting, in the words the UI uses. The **In the file** column is the key in the YAML document,
for the rare occasions you edit it by hand — see the
[example configuration](/example-config/). Almost nothing here has to be typed: set-up fills the
document in, and the Configuration page edits it.

The document has four layers, each narrowing the last:

| Layer | What it holds | Where you edit it |
|---|---|---|
| `Global` | The house: people, the master switch, the modes, the labels | **Configuration → House**, **House modes** |
| `Defaults` | The baseline every room starts with | **House → Every room starts with these** |
| `Periods` | The day's brightness and warmth | **Configuration → Schedule** |
| `Areas` | One entry per room; overrides only what differs from `Defaults` | **Configuration → Areas**, then the room's page |

A room's own setting always wins over the baseline. A room's page says how many of its settings are
its own rather than the house's.

---

## Per room, and for every room

These are the settings a room can state for itself. The same list, with the same labels, appears twice:
under **House → Every room starts with these** as the baseline, and on each room's page as that room's
own. There are 22 of them.

### Movement & timing

*How long the lights stay on, and what stops or overrules them.*

| Setting | What it does | Default | In the file |
|---|---|---|---|
| **Lights stay on for** | After the last movement, how long the lights stay on before the warning dim. Longer for rooms where people sit still. | 10 min | `VacancyTimeoutSeconds` |
| **Warning dim level** | How deep the warning dim is. 50 % is half the brightness the room was holding. | 50 % | `PreOffBrightnessFactor` |
| **Warning dim lasts** | Before going out, the lights dim for this long. Any movement brings them straight back. | 30 s | `PreOffSeconds` |
| **Manual changes hold for** | When someone adjusts a light by hand, their choice is left alone for this long. | 2 h | `OverrideDurationMinutes` |
| **After switching off by hand, wait** | After someone turns the lights off by hand, movement won't turn them back on until the room has been empty this long. | 10 min | `VacancyResetMinutes` |

The warning dim must be shorter than the time the lights stay on.

Four more controls close the section, though none is one of the 22: two lists that decide whether the
room switches itself on and off, and two scenes it can run instead. They belong to one room and have
no house-wide baseline, so they are described under [a room's own facts](#a-rooms-own-facts).

### Darkness

*What has to be true outside before movement lights the room.*

| Setting | What it does | Default | In the file |
|---|---|---|---|
| **How the room decides it's dark** | Which signal decides the room is dark enough to light: **Sensor**, **Sun** or **Always dark**. | Sensor | `Darkness` |
| **Dark below** | At or below this many lux the room counts as dark. Readings run from a few lux at night to tens of thousands at midday, so pick the decade before the number. | 1000 lx | `LuxThreshold` |
| **Bright again above** | The extra light needed to count as bright again, so a sensor sitting on the threshold cannot flap. Scale it with the threshold. | 10 lx | `LuxHysteresis` |
| **Dark when the sun is below** | Sun elevation below which the room counts as dark. In degrees above the horizon: 0° is sunset, −6° is dusk. | 3° | `SunElevationThreshold` |

*Dark below* and *Bright again above* are shown under **Sensor**, the one that reads a sensor;
*Dark when the sun is below* under **Sun**, the one that reads the sun.

**Sensor**, the default, reads the room's own light-level sensor and nothing else. A room with
nothing to read — one that has no sensor, or one whose sensors have all stopped reporting — counts
as **dark**, so movement lights it: a gate with nothing to read holds nothing back, and a flat
battery is no reason to leave a room unlit through a bright evening. A room whose sensors have gone
quiet also says so once in the log, which is the only place you are told the hardware has failed.

**Sun** reads sun elevation alone, so a room with no sensor is unaffected by having none.
**Always dark** is for rooms with no daylight.

There was once a fourth, *Either*, which counted a room dark when the sensor said so **or** when the
sun was low enough. It was retired: two answers, and the one that won was the one you were not
looking at — a bright afternoon reading overruled by a low winter sun. A room still set to it reads
as **Sensor**, and the next save writes that.

### Brightness from daylight

*Lifting the room above the schedule when it is bright outside.* Off until you switch it on, and it
only ever adds light — a period already brighter than *Brightest it goes* is left alone.

| Setting | What it does | Default | In the file |
|---|---|---|---|
| **Brighten with daylight** | On a bright day the room is lifted above the schedule's brightness, so it doesn't look gloomy against a bright window. | off | `LuxBrightnessEnabled` |
| **Daylight level where brightening starts** | At or below this reading outside, the schedule's brightness is used unchanged. | 100 lx | `LuxBrightnessStartLux` |
| **Daylight level for full brightness** | At or above this reading the room holds the brightest it goes. 10 000 lx is a bright overcast day. | 10 000 lx | `LuxBrightnessFullLux` |
| **Brightest it goes** | The brightness the room is raised toward. | 100 % | `LuxBrightnessMaxPct` |
| **Curve shape** | 1 rises steadily. Above 1 holds back until it is properly bright out; below 1 lifts the room as soon as the light outside starts climbing. | 1 | `LuxBrightnessGamma` |

The last four appear only once *Brighten with daylight* is on. The reading comes from the room's own
sensor, or from the house's outdoor sensor when the room follows it.

### Room behaviour

*What this room does when the house sleeps, empties or fills again.*

| Setting | What it does | Default | In the file |
|---|---|---|---|
| **How warmth reaches these lights** | Most lights take a colour temperature in kelvin. Plain dimmers and colour strips do not, and those are driven with every channel at one value, which is neutral white. Left to **Detect from the lights** it reads the room's own fixtures and needs no answer. | Detect from the lights | `ColorControl` |
| **Gentle while the house sleeps** | Held to the night period's limits, so a 03:00 glass of water gets a dim light. | off | `RespectSleepMode` |
| **Never comes on by itself while the house sleeps** | For the bedroom itself. The wall switch still works. | off | `SleepBlocksAutoOn` |
| **Stays on when everyone leaves** | Porch and security lights are wanted precisely when nobody's home. | off | `SkipAwaySweep` |
| **Lights up when the first person comes home** | Comes on to meet them if the house is dark, instead of waiting for a motion sensor. | off | `WelcomeHome` |

### Rarely needed

| Setting | What it does | Default | In the file |
|---|---|---|---|
| **Fade when it's light out** | How long the lights take to reach a new level while the room is not dark. | 1 s | `DayTransitionSeconds` |
| **Fade when it's dark out** | Gentler, because eyes are dark-adapted. | 15 s | `NightTransitionSeconds` |
| **Sun entity** | A house normally has exactly one, so there is usually no reason to change this. | `sun.sun` | `SunEntity` |

---

## A room's own facts

These belong to one room and have no house-wide baseline. They live on the room's page.

| On the page | What it does | In the file |
|---|---|---|
| The room's name | Left alone, the room is called whatever Home Assistant calls its area, so a rename over there arrives here. | `Name` |
| Home Assistant area | Which area the room is. Everything else is found from it. | `AreaId` |
| **Not right? Pick by hand → Lights / Motion sensors / Light-level sensor** | Each list you fill in replaces the automatic choice for that list alone, and ignores the labels. Leave empty to use whatever is found. | `Lights`, `MotionSensors`, `LuxSensor` |
| The **×** on a found chip | Leaves one entity out of this room — a fridge's own light sensor, a hallway lamp filed under the wrong room. Listed afterwards so you can put it back. | `ExcludeEntities` |
| **Don't switch on while** | While any of these is on, the lights won't come on by themselves. A projector, a do-not-disturb switch. Movement is still noticed, and lights already on are left alone. Offered from the whole house, since a blocker often belongs to no room. | `IgnoreWhenOn` |
| **Don't switch off while** | While any of these is on, the room won't turn its own lights off: the countdown, the warning dim and the leaving sweep all leave it alone. A meeting light, a guest switch. It never turns anything *on*, and switching off by hand still works. | `KeepLitWhenOn` |
| **Run a scene instead, on movement** | Movement runs this scene rather than setting the room's own brightness and warmth, and the room is left alone afterwards. | `SceneOnMotion` |
| **Run a scene instead, when empty** | When the room has been empty long enough to go off it runs this scene and stays there — atmospheric lighting rather than darkness. | `SceneWhenEmpty` |
| The room's switch, in its header | Whether the engine commands this room at all. A switched-off room is still watched and still reported. | `Enabled` |

All four close the *Movement & timing* section under **All settings**, beside the timings they work with.

**Both lists can be turned round.** Tick *while these are off instead* under either one and the rule
holds while the listed entities are **off** — for a "lighting allowed" switch you turn off rather than a
"do not disturb" switch you turn on. In the file that is `IgnoreWhenOnInverted` and
`KeepLitWhenOnInverted`, and the box only appears once the list has something in it.

**A scene replaces one transition and nothing else.** Everything that could refuse to light the room
still refuses — the master switch, the room's own switch, an empty house, sleep, too much daylight,
*Don't switch on while*. A scene is what happens *instead of* a command the engine had already decided
to make, never a reason to make one. Leaving the house still switches the room off, because an empty
house is not a room going empty; a room that should stay lit then wants *Stays on when everyone
leaves*. While a room sits on one of its scenes the engine leaves it there: neither the circadian drift
nor a house-mode change re-aims it. A hand at the switch still wins.

*Don't switch off while* blocks the empty scene exactly as it blocks a plain switch-off. When the hold
releases, what lands is whatever that room's off-transition now is — the scene, for a room that names
one.

**File only:** `FollowOutdoorLux: true` makes a room read the house's outdoor light sensor when it has
none of its own. There is no control for it in the UI. A room's own sensor always wins over the
outdoor one.

---

## The day — Configuration → Schedule

A period runs from its start until the next period begins.

| Setting | What it does | In the file |
|---|---|---|
| **Period name** | `morning`, `day`, `evening`, `night` — free-form, and what the board and the logs call it. Rename it whenever you like; nothing points at the name. | `Name` |
| *(not on screen)* | The period's id, minted once when it is created. Everything that refers to a period refers to this. | `Id` |
| **Starts** | A clock time (`22:30`) or a sun event with an optional offset (`sunrise`, `sunset-01:00`, `sunrise+00:45`). | `Start` |
| **Brightness** | The target brightness while this period runs. | `BrightnessPct` |
| **Colour temperature** | The target warmth, in kelvin. | `ColorTempKelvin` |
| **Also switches house mode to** | When this period starts, switch the house to this mode option. | `SetsModeId` |
| **Blend between periods** / **Blend over** | Lights drift to the next period's level instead of stepping at the boundary. | `SmoothTransitions`, `BlendMinutes` (default on, 30 min) |
| **Wait for movement before starting** | The period does not begin at its *Starts*. The one before it keeps running until somebody moves. | `StartsOnMotion` |
| **Movement in** | Which rooms' movement may start it. Empty means any room the engine watches. | `StartsOnMotionAreas` |

Quote clock times in the file: bare `06:00` is not a string in YAML. Keep at least one clock-time
boundary — far north a sun-anchored boundary can be unresolvable around midsummer and midwinter, and a
period that cannot be placed is skipped.

### A period that waits for movement

Set on a morning period, this is the difference between the house brightening at 06:30 whether or not
anybody is up and the house brightening when somebody gets up. The night period keeps running until
then, and the morning then arrives whole: brightness, warmth and *Also switches house mode to*
together, for every room.

- **Never before its own *Starts*.** A 02:00 trip to the kitchen is not the morning.
- **Once a day**, and the day is your local day.
- **It falls through on its own.** The next period's own start overtakes one that never began, so an
  empty house is never stranded on last night's levels.
- **Nothing at all when Home Assistant owns the periods.** The toggle says so where it is dormant.
- A schedule where *every* period waits gives you a warning: nothing would be in force from midnight
  until somebody moved.
- The collapsed card says which it is — *06:30 · waits for movement in Kitchen or Hall* — so a
  schedule that waits cannot be misread as one that runs on the clock.

One thing it does not yet do: the blend arrives already part-run. A period whose *Starts* is 06:30 and
which begins at 06:45 is 15 minutes into its 30-minute blend rather than easing from the moment
somebody walked in.

---

## House modes — Configuration → House modes

The house mode is one Home Assistant dropdown helper (`input_select`). You create the helper; each
option is then tagged here.

| Setting | What it does | In the file |
|---|---|---|
| **House mode** | The `input_select` whose value is the house mode. | `Global.HouseMode.Entity` |
| **Kind** | What the option means: **Normal**, **Sleep**, **Away** or **Guest**. Mark exactly one option Normal — it is what every reset returns to. | `Kind` |
| **Activate this scene when entering mode** | A `scene.*` applied on entry. Away with no scene sweeps the lights off instead. | `Scene` |
| **Dim level while asleep** | Sleep only: the period whose dimness sleep-respecting rooms are held to. Falls back to a period that sets this mode, then to one named `night`. | `ClampPeriodId` |
| **Turn this mode on while …** | While any listed entity is on, this option is the active mode whatever the dropdown says — a bedside "sleep" switch. | `ActivateWhileOn` |
| **Activate when no movement for** | Switch to this option once the whole house has had no movement for this long. Non-Normal options only. | `ActivateAfterNoMotionMinutes` |
| **Reset when a period starts** | Back to Normal when the named period begins. | `ResetOnPeriodStartId` |
| **Reset on presence**, and its **grace** | Back to Normal when somebody moves. An empty sensor list means every motion sensor in the house. The grace ignores presence for that long after the mode is set, so walking out of the door does not cancel the mode you set on your way. | `ResetOnPresence`, `ResetPresenceSensors`, `ResetPresenceGraceMinutes` (default 15 min) |

Reset triggers combine: any of them can be set, and the first to happen wins. Leave them all unset to
switch back by hand.

Pairing *Activate when no movement for* with *Reset on presence* gives the natural loop: empty for six
hours → Away, someone moves → Normal.

---

## The house — Configuration → House

### Finding lights & sensors

| Setting | What it does | Default | In the file |
|---|---|---|---|
| **Only manage lights with (label)** | When set, only lights carrying this Home Assistant label are managed. Leave empty to manage every light that's found. Lights only — filtering sensors would make a half-labelled house deaf. | none | `IncludeLabel` |
| **Never touch (label)** | Anything carrying this label is invisible to the app. Always wins over the include label. | `adaptive-exclude` | `ExcludeLabel` |
| **Counts as motion (label)** | A sensor with this label is treated as a motion sensor whatever its type. | `adaptive-motion` | `MotionLabel` |
| **Outdoor light sensor** | The house's outdoor sensor, read by the rooms that ask for it. | none | `OutdoorLuxSensor` |
| **What counts as a motion sensor** | Device classes that qualify a `binary_sensor`. Listing any **replaces** the built-in set rather than adding to it. | motion, occupancy, presence | `MotionDeviceClasses` |
| **What counts as a light-level sensor** | The device class that qualifies a `sensor`. | `illuminance` | `IlluminanceDeviceClass` |

An explicit list of lights or sensors on a room bypasses both labels.

**File only:** `LuxSensorStaleAfterMinutes` is how long a light-level sensor may go without reporting
before it stops counting toward a room's average. Default 120; zero or less switches the rule off.
Illuminance only — a motion sensor that has said nothing for hours is a room nobody walked through.

### People, the master switch and the name

| Setting | What it does | Default | In the file |
|---|---|---|---|
| **Who lives here** | Whose presence decides Home and Away. Empty means everyone Home Assistant knows, including people added later. | empty | `Persons` |
| **Count the house as empty after** | How long everyone must be gone before rooms react to an empty house. | 5 min | `AwayDebounceMinutes` |
| **Master switch** | The entity that pauses everything. Left at the default, the app's own enable switch in Home Assistant is used — turning that one off pauses the app, this page included. | the app's own switch | `KillSwitchEntity` |
| Which way round it reads | Offered only once you pick your own entity: *read as an enabled flag — off kills the engine*, or *read as a kill switch — on kills the engine*. | enabled flag | `KillSwitchActiveWhenOff` |
| **House name** | A label for logs and notifications, so two houses can be told apart. | "Adaptive lighting" | `ConfigName` |

### Fine tuning

| Setting | What it does | Default | In the file |
|---|---|---|---|
| **NetDaemon user id** | The Home Assistant user id owning this host's token. Optional; it sharpens "was that change us, or a person?". | none | `NetDaemonUserId` |
| **Re-check the rooms every** | How often each room re-checks the time of day and the light outside. Once a minute is plenty. | 60 s | `CircadianTickSeconds` |
| **Recognise own changes for** | How long the app's own commands are recognised as its own rather than as a person at a switch. | 8 s | `SelfEchoWindowSeconds` |
| **Other automations count as manual changes** | Whether a change made by another automation counts as a manual change. On means your other automations win. | on | `TreatAutomationsAsManual` |

---

## A file written before 2.0

A document that says `Zones:` still loads. The old keys are translated as it is read, and the file is
written back in the new schema on the first start after the upgrade, with the previous version kept
beside it. Nothing needs doing by hand.

The same happens to a file whose periods and house-mode options have no `Id`. Each gets one on the
first start, and everything that pointed at a period or a mode **by name** is repointed at its id:
`SetsMode` becomes `SetsModeId`, `ClampPeriod` becomes `ClampPeriodId`, `ResetOnPeriodStart` becomes
`ResetOnPeriodStartId`, and a room's own levels follow. That is what makes renaming a period safe —
before it, renaming *morning* silently broke every reference to it. A reference naming something that
does not exist is left exactly as written, so a typo still shows up as the warning it always was.

Two things do not become ids. `Value` on a house-mode option stays the text your `input_select`
reports, because that is how the two are matched. And `Global.HouseMode.Entity` is still an entity id.

What does not migrate is your own Home Assistant automations: the published event is
`adaptive_lighting_area` with an `area` field, and nothing publishes the old `laget_lighting_zone`
any more.
