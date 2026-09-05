# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/). Versions are
calendar-based, `YYYY.M.patch` (e.g. `2026.8.0`), unpadded to match the leading-zero stripping NuGet
already applies on publish — see *Numbers that were chosen, not derived* in `docs/mechanisms.md`.
The packages —
`AdaptiveLighting`, `AdaptiveLighting.Web`, `AdaptiveLighting.Extensions` and
`AdaptiveLighting.NetDaemon` — ship as a matched set under one version, because they are compiled
against each other.

## [Unreleased]

### Added

- **A feedback link in the top bar, on every page.** It opens a new GitHub issue on this repository,
  in a new tab, prefilled with the running version — nothing is submitted from inside the app
  itself. Below 560px it shows as its glyph alone, so the bar still fits a phone in two rows.

### Fixed

- **A room's per-period level row is two lines on a phone instead of eight, and the house-default pocket can be dragged into again.** The period name, its start time and the curve question share one line and each rail carries its number beside it; ticking the curve drops the rail entirely rather than replacing it with a sentence, and the Test button stays. The readout printed the value twice ("the schedule's 100 % 100 %") because a class rule setting `display` beat the `hidden` attribute, and the unlit rail measured 1.32:1 against the card, so only the thumb was visible — it is 4.49:1 in dark, 3.11 in light and 4.43 in 0z0 now. A press-and-hold anywhere on a brightness rail armed the fine-adjust handle after 450 ms and handed it pointer capture, after which the coarse rail could not move for the rest of that gesture and its nudges clamp at 0 % and can never mean *borrow*; the hold now requires the press to land on the thumb, and is withheld entirely while the rail sits in the pocket.

## [2026.9.5] - 2026-09-05

### Fixed

- **A snapshot rebuilt from a live `adaptive_lighting_area` event carried away-mode and forced-mode as
  `null`, even when the engine had just published them.** `HaStatePublisher` put `is_anyone_home` and
  the five `mode_forced_*` fields on the wire, but `AreaSnapshotEvent` had no properties for them, so
  `ToSnapshot()` silently dropped `AreaSnapshot.IsAnyoneHome` and `AreaSnapshot.Forced` on every
  rebuild — the away-mode and forced-mode UI reading live snapshots never saw either.
- **`FakeHaContext`'s entity-state recursion budget is on by default**, at 10000, rather than opt-in per
  test. A future test that builds a self-referencing group without setting `StateReadBudget` itself is
  now caught in flight instead of hanging the suite. The default is measured against the widest
  legitimate use in the current suite — a virtual-time simulation polling state on every scheduled tick,
  2821 reads at its busiest — with over 3x headroom.
- **A hand-set colour on a colour-channel fixture survives a period test.** `LightCommand` now
  carries an optional colour-channel vector alongside brightness and colour temperature, and a
  period test reads a fixture's current `rgb_color`/`rgbw_color`/`rgbww_color` back into it before
  running, the same way it already reads brightness and colour temperature. Previously the vector
  had nowhere to go, so a room with no colour temperature to fall back on came back from a test at
  neutral white.
- **A room's Test countdown survives leaving the page.** It used to be drawn from the page's own
  component state alone, which Blazor drops the instant the component is destroyed — navigating away
  and back within the ten seconds showed plain Test buttons while the engine's own return was still
  pending. The engine now publishes which period is under test and when it ends, so a fresh page load
  or a navigate-back redraws the same countdown a page that stayed open would show.

### Removed

- **`NullableNumber.razor` and `PresetSelect.razor` are gone from the web project.** Neither component
  was reachable from any page; `NullableNumber` was the only remaining consumer of `PresetSelect`, and
  removing the pair takes their six dedicated tests with them.
- **`ModePreview.PreviewBrightness` and `PreviewKelvin` are gone.** No page rendered either field, and
  a house mode no longer has a single brightness/colour-temperature answer to show now that the
  daylight curve is a per-room choice. `ActivePeriodName` and `IsOffPreview` stay: both are still
  house-level, single-valued facts, and the tests exercising them cover the mode preview's actual
  period-resolution logic rather than a display field nobody reads.

### Internal

- **`CircadianCalculator.PeriodsAcross` has direct tests.** It was exercised only through the web
  schedule and board views. Eight tests now cover the schedule-order case, a stretch crossing
  midnight, sun-anchored boundaries resolved against one day's sun times, an empty and a
  single-period table, the override, and the per-day hold rule it shares with `NextBoundary`.

## [2026.9.4] - 2026-09-05

### Added

- **The brightness rail carries a fine-adjust satellite handle.** Press and hold the primary
  thumb, and a small handle appears beside it that moves the value in single 8-bit
  raw-brightness steps (Home Assistant's own 0-255 scale), rather than jumping between the
  sixteen named presets. Releasing it, or pressing elsewhere, dismisses it. Colour temperature
  has no equivalent raw unit and does not get the handle.

### Changed

- **The house-default position on an inheritable preset rail now sits in its own recessed pocket
  before the 0 % mark, instead of on the leftmost stop of the same 0-100 % rail.** It reads as
  genuinely outside the real range rather than as the dimmest setting, while staying one
  draggable control.
- **The number beside a preset rail now tracks the thumb while it is being dragged**, rather than
  sitting stale until the drag is released.

## [2026.9.3] - 2026-09-03

### Changed

- **The daylight curve moves from a house-wide, per-period choice to a per-room, per-period one.** A
  room now follows the curve for one of its own periods through that period's own row on the room's
  page (`RoomLevelOverride.FollowDaylightCurve`), never through anything on the period. A period keeps
  exactly one house-wide `BrightnessPct` again, as it did before the curve existed, and two rooms can
  now run the same period two different ways — one on the curve, one on its own number.
  `TimePeriodConfig.UseDaylightCurve` is retired; a document still carrying it loads unchanged, with
  the key reported and dropped on the next save.
- **A room's own per-period brightness now reaches the lamp when that room has not put the period on
  the curve.** Previously it was discarded whenever the period itself was on the curve, whatever a
  room's own row asked for, because the choice lived on the period rather than the room.
- **The "nothing to read" warning now names the rooms that actually follow the curve**, rather than
  every room in the house whenever any period was on it.

## [2026.9.2] - 2026-09-03

### Added

- **Set-up offers a room that has lights and no movement sensor**, where before it proposed only rooms with
  both. Each such room says on its own row that it never lights itself, and one line under the table explains
  what it does instead: the wall switch lights it, and the hold is what ends it.

### Changed

- **A boundary between a period on the daylight curve and one stating a percentage eases instead of
  stepping**, and it eases from where the curve actually left the light rather than from the stored number the
  curve period keeps but does not use.

- **A sleep-clamp period that follows the daylight curve takes its night ceiling from the curve**, not from
  that stored number. With the boundary above, a curve period's stored percentage no longer reaches a lamp by
  any path, so the interface's promise that it is kept and does nothing is now true without qualification.

- **A period waiting for movement eases from the moment somebody arrived**, rather than from a boundary an
  empty house went by. The blend keeps its full length and so finishes later than a clock-started one would,
  instead of arriving part-way through.

- **A settings row is its name and its control**, with the explanation behind the ⓘ rather than printed under
  every setting. An entity list takes the card's full width instead of running off the edge on a phone, and
  two help lines that showed their HTML escape now read as the punctuation they meant.

- **The durable log rolls daily and keeps a fortnight**, replacing one 10 MiB file plus a single rotated
  generation that held between four and eight days. File names carry their date, so the old undated pair is
  removed at the first start on this version. The cost is disk: a 20 MiB ceiling becomes 60 MiB, about 37 MB
  at the rate a house actually logs.
  - `CircularLogSink` and `CircularLogWriter` are replaced by `DurableLogFormatter`, `DurableLogFile` and
    `LogFailureReport`. It is a change to the package's public surface, and no house names any of them.

### Fixed

- **Every filter button on the Activity page removes what it counts.** A report belongs to one category rather
  than to several, so the number beside a button is how many lines switching it off takes away, and the eight
  add up to what the page holds. The dashboard summary no longer repeats in words a turned-down movement the
  lanes beside it already mark.

- **A period that names no rooms for its movement-led start no longer writes an empty `StartsOnMotionAreas`
  into the document.** An existing document carrying the key still loads and still means what it meant; the
  next save drops it.

- **The three self-referencing-group tests in the entity resolver's suite are bounded by the number of entity
  states the walk reads**, not by a wall clock, so a loaded runner cannot turn them red.

## [2026.9.1] - 2026-09-02

### Added

- **A room that has lights but no movement sensor is set up and runs**, where before it was refused and
  disappeared from the interface. Nothing there ever lights itself; switching its lights on by hand puts the
  room on the ordinary manual hold, and the lights go off when that hold runs out. A room with no lights at
  all is still reported as a room that could not be set up.

- **A Test button on every period row of a room page.** It puts that period's brightness and warmth on the
  room's real lights for ten seconds and then hands the room back to the engine, so a setting can be judged in
  the room rather than read off a slider. Nothing about the room's state changes: no manual hold is started,
  none is cleared, and no countdown is disturbed.

- **A period test runs while somebody's own levels or a house scene are holding the room.** The room's levels
  are read before the test and put back after it, and the manual hold expires exactly when it would have.

### Changed

- **The room's brightness and warmth are stepped sliders, laid out as a table**, with *Period*, *Brightness*
  and *Warmth* said once at the top instead of on every row.
  - The leftmost stop means *follow the schedule*. It is drawn so it cannot be read as the dimmest setting —
    hatched rail, hollow thumb, a lit cap at the end of the rail, and the borrowed number in words rather than
    as a set value. **The separate revert button is gone**, the stop being the way back.
  - The schedule editor takes the same sliders, without that stop: the house's own schedule has nothing above
    it to borrow from.
  - **2500 K and 5000 K are dropped from the warmth stops.** Neither carries a name, and on a rail an unnamed
    step is a position the thumb has to cross to reach one that means something.

## [2026.9.0] - 2026-09-02

### Added

- **A manual change can hold a room until the movement clears, instead of for a fixed time.** *Manual changes
  hold* offers two answers: *until the room empties*, which waits for the same quiet as *Lights stay on for*,
  and *for a set time*, which is the clock it has always been. Offered per room and for the house, on the room
  page, the House tab and in the plain-English sentence.
  - Under *until the room empties* the level a person set stands while anyone is still in the room — every
    movement restarts the wait — and is handed back as soon as they leave. The room then does what it does for
    any empty room.
  - The room page **hides the length while the hold waits for movement** and keeps it in the document, so
    switching back restores what was typed.
  - **This is the new default**, so a house that has never named the setting follows movement after upgrading.
    A house that prefers the clock sets *for a set time*, per room or for the whole house.

- **A setting the schema no longer has is shown on the Configuration page**, not only written to the log. The
  page names the key, says what it stopped doing, and says that saving once drops it. Every retired setting is
  covered, and document-wide warnings now reach the page in general, where before they reached the log alone.

### Fixed

- **The next boundary is resolved against the day it falls on.** A period waiting for movement was matched
  against the previous day's instance, so a start whose own day had not begun was still woken for, and one
  held back the day before dropped out of the schedule.

- **A room that is switched on but cannot be set up — no lights assigned to it in Home Assistant, or no motion
  sensor — is reported once**, rather than at every engine start.
  - The rooms already reported are remembered in a note beside the configuration document. A room whose
    problem changes is reported again, and a room that resolves is forgotten, so a later regression is
    reported afresh.
  - The card is titled **rooms that could not be set up**, because the rooms it names are switched-on rooms
    that failed setup, not rooms the owner switched off.

## [2026.08.0] - 2026-08-27

### Added

- **The Configuration page shows the running package version.** A deployed instance can be identified without
  inspecting the DLL.

## [2.0.0-preview.6] - 2026-08-26

### Changed

- **The daylight curve is now a per-period mode, and replaces the per-room lift.** Every period carries one
  choice: *specify brightness*, which is its own percentage and the default, or *use daylight curve*, where
  the light outside decides the level instead. Several periods may claim the curve, and one curve spans them
  all.
  - The curve **replaces** the level rather than adding to it, so both of its ends are free across the whole
    0–100 %: `LuxBrightnessMinPct` at `LuxBrightnessStartLux` and below, `LuxBrightnessMaxPct` at
    `LuxBrightnessFullLux` and above, shaped by `LuxBrightnessGamma`. There is no ceiling any more, and the
    plan's brightest period no longer bounds it. A bright end set under the dark end makes the curve fall.
  - A period on the curve **hides its own percentage and keeps it in the document**, so switching back
    restores what was typed.
  - **The curve's dark end is seeded from the period that claims it** — half that period's own percentage,
    clamped to 0–100 % and rounded to one decimal, so a 15 % night starts the curve at 7.5 % and a 90 % day
    at 45 %. It fires only as the curve goes from unused to used: a second period joining leaves the value
    alone, since it may have been dragged into place by then, and turning the curve off everywhere and on
    again seeds afresh. Only the house default is written, so a room stating its own dark end keeps it. This
    is an editing action, so a hand-written document runs untouched and the schema default stands as the
    reserve for one where the curve was never claimed through the editor.
  - The curve diagram is shown on a room's page only while at least one period claims the curve.
  - **The reading comes from the house's outdoor sensor.** An indoor sensor measures the room's own lamps, so
    the curve would chase itself. A room may override it with `DaylightSensor`, chosen from any light-level
    sensor in the house. A document where a period claims the curve and no sensor is named is warned about,
    naming the rooms with nothing to read.
  - The captions, setting names and help texts around the curve are rewritten in plainer, shorter wording.

- **A room's night behaviour is one three-step control**, a rising ladder — *Normal*, *Dims*, *Dims and stays
  off* — in place of two checkboxes where the second only meant anything with the first set. The impossible
  combination, refusing to come on without holding the night limits, can no longer be picked.
  - **The document is unchanged and no house behaves differently.** Each step writes the pair the checkboxes
    always wrote: `RespectSleepMode` and `SleepBlocksAutoOn`, false/false, true/false, true/true. A file
    already holding the block without the clamp loads and runs exactly as before, and the control shows it as
    the top step.
  - The wording in the settings row, the room sentence, the roll-call verdict, the sleep-mode help and the
    docs site is plainer.

### Removed

- **`LuxBrightnessEnabled` is gone.** The period decides alone; one mechanism replaces two. A document still
  carrying the key loads unharmed — it is an unmatched key, which is silence — and the next save drops it,
  with one warning logged on load saying what to set instead.
  - **No migration is attempted, deliberately.** The old switch was per room and the new mode is per period,
    which is house-wide, so there is no faithful translation. A house that had the lift on in some rooms has
    it nowhere until a period is put on the curve. The curve's anchors, exponent and per-room overrides all
    survive the upgrade untouched.

### Fixed

- **The dashboard's schedule ribbon draws the periods the engine is running.** The band laid the day out of
  its own copy of the boundary rules, so a period still waiting for movement was drawn from its configured
  *Starts*, and under Home Assistant's period authority the dropdown's period never reached the band at all —
  the ribbon named one period while the engine ran another. `CircadianCalculator` now answers with the periods
  in force across a stretch, out of the same table every other answer comes from, and the band only turns
  those into percentages.

- **Six defects in the daylight-curve editor.**
  - The chart's footnote sent the reader to *All settings*, which holds no *Daylight sensor* row. It names the
    fold that does.
  - A period holding 62.5 % read as two different numbers on two surfaces beside each other. Both round
    through one helper, away from zero, so it reads 63 % in each, and the save pass stores the whole number.
  - The shaping phrase described the exponent alone, so a curve dragged to fall was still said to rise. It
    carries the direction between the two anchors, in the caption and in what a handle reports to a screen
    reader.
  - A handle standing at either end of the axis was drawn across its own label, behind a rectangular focus
    ring. It is held inside the plot, and the ring is round.
  - The gesture surface stopped where the plot did, leaving a handle on the boundary with nothing to aim at.
    It reaches a margin past the plot on every side, and a pointer on that margin reads as the plot's edge.
  - The write confirmation floated over the caption it was meant to sit beside. It sits in the card head,
    beside the room page's other save states, and nothing is pinned to the viewport.

### Internal

- **Debug data ships inside the assemblies, so a stack trace from a deployed house resolves to a line
  number.** `DebugType=embedded` replaces the four `.snupkg` symbol packages, which GitHub Packages has no
  symbol server to serve and the release push discarded on every run. Source Link is unchanged and is what
  maps the embedded data back to the repository. The cost is a larger assembly in every deploy: the four
  packaged DLLs go from 1 099 264 to 1 462 272 bytes in total, the largest single rise being
  `AdaptiveLighting.Web.dll` at 692 224 → 973 824.

## [2.0.0-preview.5] - 2026-08-22

### Added

- **`AdaptiveLighting.NetDaemon`: a fourth package binding the engine into a host in one line.**
  `builder.AddAdaptiveLighting()` registers the engine, the UI, static web assets and a DataProtection key
  ring; `app.UseAdaptiveLighting()` maps the assets, antiforgery and the Blazor endpoint. Removes 59 identical
  lines from a host's `program.cs`.
  - The key ring lands beside the lighting document, under `AdaptiveLighting:ConfigPath`. It tolerates the
    directory not existing yet as long as the parent does, matching `LightingConfigPath.Resolve`.
  - The port is bound by the package from `AdaptiveLighting:Port`, defaulting to 10000; `0` opts out for a host
    that binds Kestrel itself. The unauthenticated-listener warning is logged at every start, naming the port.
  - `UseIsoTimestampLogging()` is a separate call, chained after the host's own logging. It replaces that
    logger rather than adjusting it; placed first it loses every Debug line.

- **File logging survives a restart.** `UseIsoTimestampLogging()` also writes every line to
  `/config/adaptive-lighting/log/<stem>.log`, beside the configuration document and outside the deploy folder.
  A host with no `AdaptiveLighting:ConfigPath` keeps the console alone.
  - Two files and a hard 20 MiB ceiling: one active file capped at 10 MiB and one rolled generation the next
    rotation overwrites. No numbered series, no date in a name, no retention count. At a measured 170 bytes a
    line that is 12 000 lines guaranteed and about 24 000 at best.
  - The sink renders each event from its message template and named properties and never calls
    `RenderMessage`; the writer takes no free text. Every value crosses a filter dropping credential-shaped
    names whole and credential-shaped values in place — a JWT, a `password=` pair, a URL's user info, a long
    opaque mixed-case run. Entity ids, paths and Norwegian area names are unaffected.
  - Every line carries the full ISO date, `2026-08-05 00:03:12.000+02:00`, one physical line per event with
    newlines flattened.

- **The exclude label works on a Home Assistant area, not only on entities.** An area whose own registration
  carries `Global.ExcludeLabel` (default `adaptive-exclude`) is treated as not there: auto-discovery never
  proposes it, resolution refuses it before discovery runs, and the orchestrator counts it like a switched-off
  area rather than a fault. Intended for whole-house helper areas whose groups hold other rooms' sensors.

- **A room that refuses to switch off says what is holding it.** `KeepLitWhenOn` cancels the off and clears
  the countdown, which previously left the room page's next-line silent. It now reads *"Won't switch off while
  the television is holding the lights on."*, and the evidence table gains a **Held on by** row with the
  holder's friendly name.

- **An orphaned level row can be moved to a real period, not only deleted.** A row naming a period the
  schedule no longer has offers *move it to…* beside *Remove it*, listing only periods that room states nothing
  for, and carries brightness and warmth across.

- **A period can wait for movement instead of starting on the clock**, from the schedule editor.
  Previously `StartsOnMotion` could only be set by hand-editing the document.
  - *Wait for movement before starting* sits beside the period's *Starts*, which it overrides.
  - *Movement in* names the rooms whose movement may start it, offered from this document's own rooms and
    stored by area id. Empty means any room the engine watches. An unknown id is marked.
  - The collapsed card reads *06:30 · waits for movement in Kitchen or Hall*.
  - Dormant under Home Assistant's period authority, and the toggle says so.

- **A room can run a scene instead of switching on, and another instead of switching off.** Two optional
  per-room dropdowns under *Movement*: *Run a scene instead, on movement* and *Run a scene instead, when
  empty*. Both off by default.
  - Each replaces one transition and nothing else. A movement scene replaces the commanded brightness and
    warmth, and neither the circadian tick nor a house-mode change re-aims the room while its scene stands. An
    empty scene replaces the switch-off at the vacancy timeout, and the preceding warning dim does not run.
  - They are independent: only an empty scene means normal lighting settling to atmosphere; only a movement
    scene means the scene on entry with the usual dim and off.
  - Every gate that could refuse to light the room still refuses — master switch, room switch, empty house,
    sleep, the darkness gate, `IgnoreWhenOn`.
  - An empty house still switches the room off; `SkipAwaySweep` keeps its meaning.
  - `KeepLitWhenOn` blocks the empty scene exactly as it blocks a switch-off; the deferred transition that
    lands when the hold releases is the room's current off-transition.
  - Switching a scened room off by hand is obeyed. The scene's own light changes are declared to the override
    detector first.
  - The engine publishes `scene_applied` on `adaptive_lighting_area` while a room sits on a scene, reporting no
    brightness or colour temperature.
  - A scene that is not a `scene.*` entity, or that Home Assistant does not know, is a warning; the room still
    lights by its own levels.

- **A record of when each entity was last heard from**, in a new `AdaptiveLighting.LastSeen` module.
  Home Assistant resets `last_updated` and `last_changed` on every restart: measured 2.3 hours after a restart,
  the oldest timestamp among 51 motion sensors was 2.30 hours, so a sensor dead for a week was
  indistinguishable from one that reported five minutes before the restart.
  - Restarts are recognised from the shape of the whole entity population — a running house spreads timestamps
    over hours or days, and a restart collapses that spread. `homeassistant_start` is honoured when it arrives.
  - For five minutes after a restart nothing advances; every entity keeps the record it had.
  - "The value did not change" is never read as "it did not report": a light-level sensor at a constant 3 lx
    all night counts as alive.
  - Persisted as JSON in `last-seen/` beneath the configuration document's directory, split into
    `<document>.last-seen.<bucket>.json` rather than one file past 250 motion entries. Lights, motion sources
    and light-level sensors have buckets by rule; everything else is filed by `device_class`
    (`temperature`, `battery`, `door`, `power`) or, with no class, by domain (`person`, `sun`, `automation`,
    `script`); anything with neither lands in `other`. Written through an atomic move, no backups, batched to
    one flush every five minutes plus one on shutdown, ~45 KB for a 300-entity house.
  - The bucket name reaches the file system, so it is sanitised against an allow-list and fingerprinted when
    anything is dropped or truncated; two device classes cannot collide onto one file. An emptied bucket takes
    its file with it, which is also how a cache from the earlier four-file layout upgrades.
  - A missing or corrupt file costs only the history in it: every answer degrades to "we do not know", and
    `HasBeenSilentFor` returns `false` for anything unknown.
  - Nothing consumes it yet; it is registered by `AddLightingWeb` so history accumulates before the first
    consumer.

- **A theme picker**, at the right-hand end of the top bar, with a third palette beside light and dark.
  - *Follow the system* is the default: no `data-theme` attribute, `prefers-color-scheme` answers.
  - **0z0 tech** is the new palette — blue-black surfaces, a teal accent, monospace type throughout. State
    colours remain the app's own.
  - The choice is kept in the browser, not in the configuration document.
  - No flash on reload: a blocking script in the document head sets `data-theme` on `<html>` before the body is
    parsed, which a Blazor Server app needs.

- **An activity page**, at *Activity* in the top bar: recent engine decisions as a timeline, newest first,
  grouped by day, with room and time down the left. Each row says what happened and, where the engine declined,
  why — *Too bright to switch the lights on · lux 86, dark below 40*.
  - Fed by the same `adaptive_lighting_area` events the dashboard receives. No new subscription, no log file
    read, engine unchanged.
  - Bounded to the most recent 500 reports; the page says when it is holding the cap.
  - A room filter, and an empty state explaining that a newly set-up house has every room switched off.
  - New reports are counted as they arrive but inserted only on a button press.

- **Brightness that follows the daylight.** A room can brighten as it gets lighter outside. Off by default.
  - Five per-room settings, inherited from *Defaults*: `LuxBrightnessEnabled`, `LuxBrightnessStartLux` (at or
    below it, the schedule is used unchanged), `LuxBrightnessFullLux` (at or above it, fully applied),
    `LuxBrightnessMaxPct` (the brightness it is raised toward) and `LuxBrightnessGamma` (the curve's shape).
  - The interpolation is on `log10(lux)`, not lux, because perceived brightness is roughly logarithmic. With
    anchors at 100 and 10 000 lx, 1 000 lx is exactly halfway up the curve.
  - It raises, never lowers. The period's `MinBrightnessPct`/`MaxBrightnessPct` still bind — a night period
    capped at 30 % stays capped at 30 % — and sleep mode's clamp still wins.
  - The reading comes from the room's own lux sensor, otherwise from `Global.OutdoorLuxSensor`.

- **A note when a room is switched on**, on the House tab's room row and the room page, saying how many lights
  the room will command and naming every one.
  - Lights that look like something other than room lighting are marked with a reason: status LEDs and
    indicators, a trailing `_led` on an id, a colour channel of a lamp the room already commands whole, and a
    light inside an appliance. Measured on one house: 13 of the living room's 19 lights — three access-point
    LEDs, four board indicators, five WiZ channels and the fridge.
  - Advisory, never a filter. `entity_category` is not exposed by HassModel, so every rule is a name
    heuristic. The rules are asymmetric: accusing needs a whole word, excusing needs only a substring.
  - It recommends a *Room light* label in Home Assistant, named under *House › Finding lights & sensors › Only
    manage lights with*, and states that the setting reaches every room. A house that already names an include
    label is not told again.
  - Dismissible, and a room already read is not raised again. Switching a whole floor on raises nothing.

### Changed — action required

- **Periods and house-mode options carry an `Id`, so renaming is free.** A period's name used to be the key
  every reference used, so renaming one broke a room's levels, a mode's dim-while-asleep period, a reset
  trigger or a dropdown mapping. Ids are minted once, never shown, and every reference points at them. Names
  are display only and two periods may share one.
  - **The file is migrated on the first start after upgrading, once.** Ids are filled in, references repointed
    at what they already resolved to, and the previous file kept as the usual `.bak`. Later starts change
    nothing. A reference that matched nothing is left as it was and reports what it always did.
  - **The old key names still parse**: `Period` → `PeriodId`, `SetsMode` → `SetsModeId`, `ClampPeriod` →
    `ClampPeriodId`, `ResetOnPeriodStart` → `ResetOnPeriodStartId`. Ids read as `night-3c9f`, not as a GUID,
    so hand-written YAML can use them.
  - **A house-mode option's `Value` is unchanged** and still matches Home Assistant, so renaming the option in
    Home Assistant still needs re-pointing here.

- **A room counts as dark below 1000 lx, not 40.** The gating reading is usually not a reading of that room:
  most houses have one outdoor lux sensor and many rooms with none. One outdoor sensor measured over 30 hours
  sits at 1–3 lx at night and 1000–3706 through the day, and it is shaded — an unobstructed one would read
  10 000–50 000. Against 40 lx every room read *not dark* from first light until dusk while sitting genuinely
  dark. A room whose sensor really does measure the room wants a low number, set on that room.

- **The house-wide outdoor lux sensor is no longer a silent fallback.** `Global.OutdoorLuxSensor` used to be
  handed to every room that resolved no lux sensor of its own. A room now opts in with **`FollowOutdoorLux:
  true`**.
  - A room with no lux sensor is simply dark: the lux half of its gate stops holding it back. Under
    `Darkness: Either` such a room behaves as `Always`; under `Lux` a sensor that exists and will not read
    still falls back to the sun.
  - Existing documents are not rewritten. A document naming an outdoor sensor no room follows gets a
    validation warning saying what changed and how to restore it.
  - The daylight brightness curve reads whatever the darkness gate reads, so a room following the outdoor
    sensor for its level opts in the same way.

- **A room with several light-level sensors averages them instead of using none.** An area with more than one
  candidate previously used neither, which disabled the lux gate in 8 of 17 rooms on one house. The average is
  **geometric**: 170 lx and 3000 lx average to 714 rather than 1585, and those fall on opposite sides of a
  1000 lx threshold. Non-positive readings are dropped before the average; a room whose every reading is zero
  is 0.
  - Dead sensors are dropped first: no state, `unavailable`, `unknown`, or silent for longer than
    `Global.LuxSensorStaleAfterMinutes` (default two hours). Silence is judged on `last_updated`, not
    `last_changed`. Nothing is called dead until the engine has been watching for at least that window.
  - **Illuminance only.** Measured on one house, 30 of 51 motion sensors had not reported in over two hours and
    every one was healthy, because a motion sensor reports on change. Motion's only test for death stays no
    state / `unavailable` / `unknown`.

### Changed

- **The room page carries less standing prose, and its sections are in reading order.** *How this room
  behaves* leads the settings, with *All settings*, *In this room* and *What happened here* together at the
  foot of the page. The lines that reported nothing having happened yet are gone; *"Movement in the dark turns
  the lights on."* is now *"Awaiting movement."*; the levels lede is a count rather than a sentence; a period's
  boundary sits bracketed beside its name, *"Morgen [06:45]"*. A write is confirmed by a brief toast at the
  foot of the screen — the Configuration page's confirmation, floating alone — so the card heads carry only a
  pending or refused save. No engine behaviour, no commands and no configuration schema change.

- **A period arrives at its boundary, not on the next tick.** The engine schedules a wake-up at the next
  boundary and re-arms for the one after; the house mode a period sets, the period helper and every lit room
  all move at the boundary. Previously a period arrived up to a whole `CircadianTickSeconds` late — measured on
  a 300 s tick, six boundaries landed between zero and four minutes late. `CircadianTickSeconds` stays as the
  safety net, picking up a table a save rebuilt and a clock corrected after an outage. Start-up still resolves
  the running period from the clock, and a boundary crossed while the engine was down is still caught there.
  Across DST: a boundary in the spring-forward gap arrives when the gap ends, and one in the ambiguous autumn
  hour resolves as standard time.

- **A sun-anchored boundary moves when the sun does, without waiting for a tick.** The engine watches the sun
  entity's `next_rising` and `next_setting` and re-evaluates the moment either changes. Each area watches its
  own `SunEntity` and the mode brain the house's. Elevation and azimuth are ignored: no boundary is anchored to
  either, and watching them would wake the house 2 880 times a day instead of two to four. It re-evaluates
  rather than merely re-arming, because a sun time can move backwards past now. A house naming no sun entity
  keeps the tick; a sun entity Home Assistant does not have yet is adopted when it appears.

- **The dark theme separates its surfaces and lifts its secondary text.** A card sat 1.08:1 against its page,
  and `--muted` sat at 8.69:1 on a panel. The page goes darker, panels lift, and `--muted` moves with them,
  because lifting panels alone takes muted text down to 7.96:1. Measured over 120 rendered text nodes: median
  8.69 → 10.12, nothing below AA, panel-against-page 1.08 → 1.27. Light and 0z0 are untouched.

- **Picking a house-mode helper no longer rewrites the option list**, and the *Use the helper's options* button
  is gone. It invented mappings and dropped the settings of every option the new helper did not also offer. The
  cards render a row per live option and mark stranded ones. The drift notice stays as a description.

- **Chart labels keep their size on a phone.** Labels in the daylight and lux charts were sized in SVG user
  units: measured at 390 px, the daylight chart's 9-unit type rendered at 3.8 real pixels. They are now derived
  from the chart's own rendered width and keyed to the chart rather than the window. Desktop rendering is
  unchanged, measured at 1280 px. Browsers without container queries keep the old behaviour.

- **A period with `StartsOnMotion` no longer begins at its `Start`.** Previously only the house-mode brain read
  the flag, so the clock still entered the period on time.
  - The previous period keeps running until somebody moves in one of `StartsOnMotionAreas`, and then the
    period begins whole: brightness, warmth and `SetsModeId` together, for every room. An empty
    `StartsOnMotionAreas` still means any room the engine watches.
  - It falls through on its own: the next period's `Start` overtakes a period that never began.
  - Still once per local day, nothing at all under `PeriodAuthority.HomeAssistant`, and never before the
    period's own `Start`.
  - A document where every period sets `StartsOnMotion` now warns.
  - **Restarting inside such a period no longer re-fires it.** The day latch is seeded from the note of which
    period the last run ended in, and movement writes that note at once rather than at the next tick.

- **Groups are preferred for motion and light-level sensors, as they already were for lights.** The same code
  for all three domains: transitive membership, a cycle guard, a clip on any group reaching into another area,
  and widest-coverage selection between overlapping groups. Fixes a case where a `motion` group of two sensors
  plus both members meant one wave of a hand fired the area three times.

- **Several entities on one Home Assistant device are one piece of hardware, and only one is used.** Measured
  on one house: five light entities in the office — an RGBW fixture's combined entity beside its own colour
  channels — are one device, and all five were commanded. A group claims the devices of everything beneath it,
  so loose entities on the same fixture drop. Where no group covers a device, the entity its siblings extend
  with an underscore wins, then the shortest id. Motion is exempt: a device there is a controller, and a
  multi-zone presence sensor exposes genuinely different zones.

- **Movement into a room the engine will not light leaves a row on the activity page**, naming what stopped it:
  the master switch, the room's own switch, an empty house, a guest scene, a sleeping house, a named entity
  that is on, or the room being bright enough. Bounded on the reason, not the reading: one report per area per
  change of the refusing gate, so forty walks under one unchanged block produce one row and a lux value
  drifting from 900 to 980 produces none. The bound resets once the room lights.

- **Rooms are called what Home Assistant calls them.** An auto-discovered room writes only its area id, so the
  room page heading, the board's lanes and the activity log's room column read the slug. The name is now
  resolved in one place: a `Name` in the document wins, then the registry's name, then the area id. Resolved on
  read and never written to the document, so renaming an area in Home Assistant renames the room here.

- **Picking lights and sensors by hand offers the room's own entities**, with a tick-box widening to the whole
  house. On one house that is 3 candidates instead of 164. Anything already picked stays listed and removable
  even outside the scope. *Blocked while on* stays house-wide.

- **The all-rooms defaults and *Finding lights & sensors* moved from Areas to House**, which now holds the
  settings there is exactly one of. `?section=defaults` follows them, the Areas lede says where they went, and
  the room page's settings reveal links to the baseline.

- **The text is larger, and the quiet text is easier to read.** Every size in the type scale moves up a step —
  the uppercase section label 11 → 12.5 px, help lines and timestamps 12.5 → 14, dense settings rows
  13.5 → 15 — and the muted colour lifts in all three themes. Muted text on the page background measures
  9.41:1 on Dark (was 6.51:1), 6.05:1 on Light and 8.70:1 on 0z0. The commissioning column heads take the
  tokens instead of hardcoding a size.

- **Dropdowns that offered *Type as text* no longer do.** Brightness gained **0% (off)** in its place. A value
  already in a document that the list does not offer still shows and is still kept.

- **A period's *Starts* no longer repeats itself.** The line under the picker is shown for starts that need
  explaining and suppressed for a plain clock time.

- **The last-seen cache moved into `<config>/last-seen/` and stopped keeping backups.** One configuration
  folder held about 160 files, half of them `.bak` copies. The cache writes one file per bucket into its own
  subfolder through an atomic move, with no `.bak` beside it.
  - Files an earlier build wrote beside the document are moved in on the next start, with their history, and
    that build's backups are dropped.
  - An emptied bucket takes its file away, and an older build's leftover `.bak` with it.

### Removed

- **A period's *Starts* no longer offers *as text, for anything else*.** The third mode swapped the structured
  controls for a plain text box writing straight to `Start`. The grammar `PeriodStart.TryParse` accepts is a
  clock time or a sun event with an offset, which is exactly what *at a clock time* and *relative to the sun*
  compose; the box could only additionally store a string the engine then refused.
  - `Start` is stored exactly as before, one string, and the parser is untouched. No document needs migrating
    and none is rewritten on load.
  - A `Start` neither mode describes is kept, not corrected: the fold header shows the string as it stands, the
    picker rests on *at a clock time* without adopting it, and a note quotes it back.

- **`RoomFacts.KelvinCss` is gone from `AdaptiveLighting.Web`.** It duplicated `KelvinColour.Css`, which is
  public in the same package. Callers use `KelvinColour.Css(kelvin)`, same argument, same string back.

### Fixed

- **A room whose lights have no colour is no longer told a colour temperature.** *Detect from lights* read a
  fixture that offers only brightness as silence, and silence resolved to kelvin, so the room was commanded
  `color_temp_kelvin` no lamp in it could take and the levels table offered a warmth column to match.
  Detection now separates *nothing answered* from *everything answered, and none of it has colour*: the first
  keeps kelvin, because a house still starting up must not lock a room out of colour, and the second commands
  neither kelvin nor equal channels. `ColorControl` is unchanged — its ordinals are pinned — and a stated
  *Colour temperature* or *No colour temperature* still overrules the fixtures either way.

- **A room page open across another writer's save no longer reverts it.** A debounced autosave wrote the whole
  document back from the picture the page read on open. Two of the other writers are the engine itself, both
  landing during that visit: area discovery 30 s after start, and the schema-migrating rewrite inside `Reload`.
  The room page now re-reads the document at save time and applies only the room it is editing.
  - **A version stamp scoped to the room, not the file.** A whole-file token would refuse a save on exactly the
    post-deploy visit that caused this. The stamp is taken from the file after each save, so a page that stays
    open can go on saving.
  - **A refused save names the room and says what to press**, offering *Reload this room* rather than a *Try
    again* that could only refuse again.
  - **The settings page keeps its whole-document save and gains the same guard**, per file rather than per
    room. A stale overwrite is refused instead of quietly winning.
  - **"Set up rooms again" on a room page is now about that room.** Adopting rooms stays with
    Configuration → Areas and the first-run board.

- **A mode card no longer claims a period the house has not started.** The mode cards built their preview from
  a calculator with no hold predicate, so from a held period's boundary onward they showed the morning's levels
  while every room ran the night's. The preview now resolves through the running engine's own
  `MotionPeriodLatch`, forwarded from the orchestrator through the host. It uses the engine's latch, never a
  fresh one, since a new latch has recorded nothing and would pin the card to last night's levels; with no
  engine running there is no latch and the clock stands.

- **The room page's now badge and the schedule editor's no longer claim one either.** Both resolved the period
  from their own copy of the boundary maths, so from a held period's `Start` onward the room page badged the
  wrong levels row, drew the daylight curve from the wrong base level, and the editor marked the wrong card
  "active now". The three surfaces now ask `Schedule.InForceNow` over one calculator from
  `Schedule.CalculatorFor`. `Schedule.InForceAt` is gone. `RoomFacts` is untouched.
  - The answer carries the rule that decided it, so the editor's hover still says whether Home Assistant's
    dropdown named this period, the clock placed it, or the next is waiting for movement.
  - The badge still follows an unsaved draft: `PeriodsEditor` renders the document `ConfigEditor` is editing.
    Only the movement rule comes from the engine.
  - Web layer only — the engine binary is unchanged. With no engine attached the predicate is `null` and every
    period is placed by its `Start`.

- **The daylight chart's bottom-right corner no longer holds two label families, and close boundaries no longer
  land on one baseline.** The month row moves into a gutter below the plot. The label spread gained the missing
  pass: pushing down alone met the floor and clamped there, so a house with three boundaries inside half an
  hour drew all three on one line, at every width including desktop. Measured against that document: four
  collisions before, none after; the label cap rises from 13 user units to 15 (clean at 16, colliding at 17).
  A phone renders chart labels at 6.3 px where it rendered 5.5; the desktop is unchanged at 10.1.
  - The cap and everything paired with it live in `DaylightLabels`, out of the component, so the spreading is
    testable. `MinGap`, the month gutter and the drawing's height are arithmetic on the cap and two measured
    type metrics, so the stylesheet and the C# cannot drift.

- **A room sitting on its own scene says which one.** `AreaSnapshot.SceneApplied` reached the browser and no
  page read it. The evidence table gains a **Scene** row with the friendly name. Two related misreadings go
  with it: a scene nulls both levels, so the *Lights* row read **off** beside the scene naming it, and the
  headline read *"Lit at — level unknown."* A hand at the switch still wins the headline.

- **A house mode no longer promises a reset that Home Assistant's authority has stood down.** Under
  `HouseModeAuthority.HomeAssistant` the engine stops firing reset triggers, but the mode card went on
  describing them. It now says the rules are paused.

- **The mode preview follows the time-of-day dropdown again**, on any house through the stable-key migration.
  The preview handed its calculator the period's display name where the calculator matches on its key, so under
  Home Assistant's period authority the cards fell back to the clock. Every test fixture named its periods
  without ids, where the two spellings coincide, so nothing caught it.

- **The time-of-day helper is written when the engine starts, not one tick later.** `SchedulePeriodic`'s first
  callback is a whole `CircadianTickSeconds` away, so a restart inside a period left the `input_select` naming
  the previous period — measured at a 300 s tick, five minutes of every dashboard and automation reading the
  wrong time of day. It is written again when the master switch releases. Both are no-ops under Home
  Assistant's authority and when the select already reads the right option.

- **The schedule runs on the household's clock, not UTC.** `IScheduler.Now` is a `DateTimeOffset` at `+00:00`,
  and the engine compared its `TimeOfDay` directly against each period's wall-clock `Start`. One house ran two
  hours behind all summer: `Natt` at 22:30 began at 00:32, so walking through at midnight got the evening
  period's 70 % instead of the night's 5 %. The engine's idea of *today* rolled over at 02:00 local with it,
  reaching the motion latch and the once-a-day period rules.
  - The offset follows daylight saving, so it could not be dialled out of the document.
  - `CircadianCalculator` and `ModeMonitor` now take the household's `TimeZoneInfo`, defaulting to the
    machine's, and must be given the same one.
  - The pages were never affected: the activity list and the board already converted.
  - Sixteen tests were asserting UTC behaviour without saying so. They now name `TimeZoneInfo.Utc`, and the
    conversion is asserted against a fixed `+02:00` zone.

- **A restart no longer reads as somebody changing the house mode.** Every rebuild seeded the house stream with
  a fabricated state before publishing the observed one, so a settings save put one *Mode changed to X* row in
  the record and a restart put one per adopted room. The opening publication now carries `Startup`, which the
  existing empty-start-up rule drops.

- **A restart and a settings save are rows of their own**, in Background. `LightingEngineHost` raises one
  `EngineNotice` per rebuild; an entry carries either an area report or a notice, so no state, period or
  darkness verdict is invented for a row no room reported.

- **A click inside an (i) panel no longer ticks the checkbox behind it.** The panel sat inside the `<label>`,
  and a click on any descendant of a label is forwarded to the first labelable element in it. The label now
  wraps the box and its own words, and the (i) is its sibling.

- **A lamp holding a colour temperature is moved to equal channels.** Home Assistant publishes `rgbw_color` /
  `rgbww_color` only while the fixture is in that colour mode, so an equal-channels room whose lamp sat in
  colour-temp mode was read as already-correct and never commanded. A fixture that offers a colour channel and
  reports none is not using it; one that offers no colour at all is still left alone.

- **The warmth buttons on the House tab write something.** Every choice on that tab was parsed as a darkness
  rule, so picking *How warmth reaches these lights* silently did nothing. Each choice is now parsed against
  its own setting's type. Per-room warmth was never affected.

### Internal

- **`Test1.cs` is gone.** The `dotnet new mstest` template's empty `TestMethod1` never asserted anything. The
  suite is one test smaller and no less covered.

- **The two `Norwegian()` test helpers have names that say which fixture they are.** One returned four option
  rows and the other three, under one name in two suites. `ModeMonitorTests` gets `PeriodsInNorwegian()`,
  exactly the three periods its `Periods()` defines; `PeriodSelectReaderTests` gets `NorwegianDropdown()`, a
  whole day's options answering to no schedule.

- **One `SameName` matcher, in `AdaptiveLighting.Extensions`.** Helper options, mode values and period keys all
  arrive as hand-entered text, and every comparison spelled out `Trim()` and `OrdinalIgnoreCase` again — three
  of them as identical private `Eq` helpers on three screens. `left.SameName(right)` is now the single answer
  and a public method of the package. Behaviour is unchanged.

- **`periods.ByKey(id)` and `periods.ByName(name)` replace a dozen inline period lookups.** Three classes
  carried a private `PeriodWithKey` and the pages repeated the same `FirstOrDefault`; `ConfigValidator`,
  `CircadianCalculator` and `ModeMonitor` now share one, as do the schedule editor, the levels editor, the mode
  sentences and the period-select panel. They live in `AdaptiveLighting.Configuration`, not beside `SameName`,
  because `AdaptiveLighting.Extensions` is host-agnostic. `ByName` is kept separate and rare: only the
  sleep-clamp chain's last link and the note left by a pre-stable-key build ask a period by display name.

## [2.0.0]

**Zone became area.** Home Assistant calls a room an *area*; a *zone* is a GPS region like "Home" or "Work".
Types, YAML keys and every label now say *area*, and the word in prose is *room*.

Read the first item before upgrading. It is the only change that can break something outside this repository,
and it needs a manual step.

### Changed — action required

- **The published Home Assistant event is renamed, with no dual publishing.** `laget_lighting_zone` is now
  **`adaptive_lighting_area`**, and its `zone` field is now **`area`**. Nothing publishes the old name: it is a
  clean break, not a deprecation. **Any HA automation, script, template or dashboard card listening for
  `laget_lighting_zone` or reading its `zone` field stops working until it is updated by hand.** The engine and
  the bundled web UI ship together, so the dashboard itself never sees a mismatch.

  ```yaml
  # before
  trigger:
    - platform: event
      event_type: laget_lighting_zone
  condition: "{{ trigger.event.data.zone == 'Stue' }}"

  # after
  trigger:
    - platform: event
      event_type: adaptive_lighting_area
  condition: "{{ trigger.event.data.area == 'Stue' }}"
  ```

  The event gained an additive **`area_id`** field, which is the better thing to match on: display names are
  editable mid-session, registry ids are not.

### Changed — no action required

- **Configuration files migrate themselves, silently.** A document written before 2.0 says `Zones:` and
  `ZonesAutoDiscovered:`. It still loads: the deserialiser renames those keys before binding, and the engine
  writes the file back in the new schema on the first start after the upgrade, keeping the previous file at the
  store's backup path. There is no prompt and no migration command. A file hand-edited back to the old names
  keeps working and is re-migrated on the next start. The serialiser always emits `Areas:`.

  If a document carries both an old and a new key, the new key wins, the old is dropped, and the load logs a
  warning naming both.

- **The C# API renamed with the schema**, which is what makes this a major version. `ZoneConfig` →
  `AreaConfig`, `ZoneSettings` → `AreaSettings`, `AdaptiveLightingConfig.Zones` → `.Areas`,
  `GlobalConfig.ZonesAutoDiscovered` → `.AreasAutoDiscovered`, `ZoneController` → `AreaController`,
  `ZoneState` → `AreaState`, `ZoneEntityResolver` → `AreaEntityResolver`, `ResolvedZone` → `ResolvedArea`,
  `ZoneAutoDiscovery` → `AreaAutoDiscovery`, `ZoneSnapshot` / `ZoneSnapshotCache` → `AreaSnapshot` /
  `AreaSnapshotCache`, `ZoneError` → `AreaError`. No `[Obsolete]` twins, no aliases, no old names in the public
  API. The namespace root stays `AdaptiveLighting.*`, and the YAML document's top-level key is unchanged.

### Changed — new installations only

- **Discovered rooms start switched off.** On a fresh install, set-up runs about thirty seconds after the
  connection settles: it finds every Home Assistant area holding both a light and a motion sensor, guesses each
  room's role from its name, adopts an obvious house-mode `input_select` if it finds one, and seeds the list of
  people. It switches nothing on. Previously a fresh install began commanding lights in every discovered room.

  Existing installations are unaffected: the flip is on newly proposed rooms, which carry an explicit
  `Enabled: false`. The default in `Defaults` stays `true`.

- **The list of people is seeded from Home Assistant at first set-up only, and only when it is empty.** An
  explicit list freezes membership; an empty field still means everyone Home Assistant knows, including people
  added later. A deliberately emptied list is never re-seeded.

### Changed — the settings pages

- **Five sections became four: Areas · Schedule · House modes · House.** `Defaults` became the **All rooms**
  group inside Areas. `Advanced` is gone as a section; its contents moved to the section whose noun they
  change, behind that section's own fold. `Periods` is now **Schedule** and carries the blend-between-periods
  settings.
- **A room is switched on or off from a switch on its header row**, rather than from row 1 of 17 inside a
  collapsed override fold. The switch writes an explicit value; the override list is now "n of 16".
- **Copy rewritten away from jargon**: "Vacancy timeout" is **Lights stay on for**, "Pre-off warning" is
  **Warning dim lasts**, "Override holds for" is **Hand changes hold for**, "Darkness source" is **How a room
  decides it's dark**, "Lux threshold" is **Dark below**, "Circadian tick" is **Re-check the rooms every**,
  "Discovery conventions" is **Finding lights & sensors**.

### Added

- **Rooms group by floor** on both the dashboard and the Areas list, using Home Assistant's floor registry,
  with a per-floor *Switch on this floor*. A house with no floors set sees no floor headers.
- **An include label.** *Only manage lights with (label)* limits management to lights carrying a chosen Home
  Assistant label. Empty — the default — manages every light discovery finds. It filters lights only: motion
  and lux sensors are inputs. The exclude label wins over it, and an explicit `Lights` list bypasses both. A
  room whose lights are all filtered out is skipped with a message naming the label.
- **Set up rooms again**, from the Areas section header (any set of rooms) or a single room's editor. It
  rebuilds the chosen rooms from what Home Assistant knows now, warning first with a dialog that counts per
  room what will be lost — hand-picked entities, changed settings, a custom name. Exactly two things survive a
  rebuild: which area the room is, and whether it is switched on. Nothing is written until you save.
- **The three label fields are dropdowns** fed by the Home Assistant label registry instead of free text. A
  house with no labels gets an explanation of where to create one; a stored value matching no live label is
  kept and flagged, never dropped.
- **The dashboard shows only the rooms switched on**, with a line under the grid naming how many are hidden and
  where to turn them on, and a designed first-run state for "rooms found, none enabled yet". Disabled rooms are
  still observed and still published.
- `AreaSnapshot` and the published event carry **`AreaId`**, so config and live state join on an id rather than
  on an editable display name.

[Unreleased]: https://github.com/0z00z0/adaptivelighting/compare/v2.0.0...HEAD
[2.0.0]: https://github.com/0z00z0/adaptivelighting/releases/tag/v2.0.0
