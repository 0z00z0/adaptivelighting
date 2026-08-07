# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html). The three packages —
`AdaptiveLighting`, `AdaptiveLighting.Web` and `AdaptiveLighting.Extensions` — ship as a matched set
under one version, because they are compiled against each other.

## [Unreleased]

### Added

- **A room that refuses to switch off now says what is holding it.** `KeepLitWhenOn` cancels the off and
  clears the countdown with it, so the room page's next-line had nothing to draw and fell silent — the exact
  moment somebody wants an answer. It now reads *"Won't switch off while the television is holding the lights
  on."*, and the evidence table gains a **Held on by** row. The holder is resolved to its friendly name; an
  engine too old to name it still gets a sentence that parses.
- **An orphaned level row can be moved to a real period, not only deleted.** A row naming a period the
  schedule no longer has already showed a *Remove it*; it now also offers *move it to…*, listing only the
  periods that room states nothing for, and carries the brightness and warmth across. Matches what the
  house-mode and period-select mappings have always done with a vanished option.

- **A period can wait for movement instead of starting on the clock**, from the schedule editor. The
  engine has understood `StartsOnMotion` since it shipped, and until now the only way to set it was to
  hand-edit the document.
  - *Wait for movement before starting* sits beside the period's *Starts*, which it overrides. The
    period before it keeps running — the house stays at night levels — until somebody moves, and then
    it begins whole: brightness, warmth and its house mode together.
  - *Movement in* names the rooms whose movement may start it, offered from this document's own rooms
    and stored by area id. Empty means any room the engine watches, and the picker says so in those
    words. An id no room here answers to is marked, as the validator's warning already said.
  - The collapsed card says it as well: *06:30 · waits for movement in Kitchen or Hall*. A schedule
    that waits can no longer be read as one that runs on the clock.
  - Dormant under Home Assistant's period authority, and the toggle now says so where the start times
    above it already did.

- **A room can run a scene instead of switching on, and another instead of switching off.** Two
  optional per-room dropdowns under *Movement*: *Run a scene instead, on movement* and *Run a scene
  instead, when empty*. Both are off by default, and a room that sets neither behaves exactly as it
  did. The point of the pair is a room that drops to atmospheric lighting when it empties rather than
  going dark.
  - Each replaces **one** transition and nothing else. A movement scene replaces the brightness and
    warmth the room would have been commanded to, and the engine then leaves it alone: neither the
    circadian tick nor a house-mode change re-aims a room while its scene stands. An empty scene
    replaces the switch-off at the vacancy timeout, and the warning dim that precedes it does not run,
    because nothing is about to go off.
  - They are independent. Only the empty scene means the room lights normally and then settles to
    atmosphere; only the movement scene means the room runs the scene on entry and still dims and goes
    off as it always did.
  - **Every gate that could refuse to light the room still refuses.** The master switch, the room's own
    switch, an empty house, sleep, the darkness gate and `IgnoreWhenOn` are all consulted first. A
    scene is what happens instead of a command the engine had already decided to make, never a reason
    to make one.
  - **Leaving the house still switches the room off.** An empty house is not a room going empty, and an
    atmospheric scene must not keep a room lit in one. `SkipAwaySweep` keeps its meaning for rooms that
    want it.
  - **`KeepLitWhenOn` blocks the empty scene exactly as it blocks a normal switch-off.** While the hold
    applies the room does neither and stays lit as it was; when the hold releases, the deferred
    transition that lands is whatever that room's off-transition now is, so for a room with an empty
    scene it is the scene. A refused *leaving sweep* still settles as a switch-off.
  - A hand at the switch still wins: switching a scened room off by hand is obeyed, and nothing
    relights it. The scene's own light changes are declared to the override detector first, or the room
    would read them as somebody at the wall.
  - The engine publishes `scene_applied` on `adaptive_lighting_area` while a room is sitting on one of
    its scenes, and reports no brightness or colour temperature with it, because it commanded none.
  - A scene the room names that is not a `scene.*` entity, or that Home Assistant does not know, is a
    **warning**. The room still lights, by its own levels.

- **An honest record of when each entity was last heard from**, in a new `AdaptiveLighting.LastSeen`
  module, because Home Assistant cannot answer that question about itself. `last_updated` and
  `last_changed` are Home Assistant's own bookkeeping and it resets them on every restart: measured on a
  live house, 2.3 hours after a restart, the *oldest* timestamp among 51 motion sensors was 2.30 hours —
  a sensor dead for a week was indistinguishable from one that reported five minutes before the restart.
  - Restarts are recognised from the shape of the whole entity population rather than from anything Home
    Assistant has to tell us: a running house has timestamps spread over hours or days, and a restart
    collapses that spread to nothing. `homeassistant_start` is honoured too, when it arrives — usually it
    does not, because the socket is down while Home Assistant is restarting.
  - For five minutes after a restart nothing advances, so every entity keeps the record it already had.
    Refusing a genuine report costs minutes of apparent staleness; believing a restore costs the whole
    point of the feature.
  - Never confuses "the value did not change" with "it did not report" — a light-level sensor sitting at
    a constant 3 lx all night is healthy and quiet, and is counted alive.
  - The record survives a redeploy: it is JSON in `last-seen/` beneath the configuration document's own
    directory, split into `<document>.last-seen.<bucket>.json` so somebody diagnosing their lux sensors
    opens one small file rather than reading past 250 motion entries. Lights, motion sources and
    light-level sensors have their own buckets by rule; everything else is filed under its own
    `device_class` — `temperature`, `battery`, `door`, `power` — or, for an entity with no class, under
    its domain (`person`, `sun`, `automation`, `script`). Nothing is dropped: an entity with neither
    lands in `other`. Written through an atomic move and **kept without backups**, batched to one flush
    every five minutes plus one on shutdown, ~45 KB for a 300-entity house.
  - The bucket name reaches the file system and comes from an external system, so it is sanitised
    against an allow-list and fingerprinted whenever anything had to be dropped or truncated — two
    device classes can never collide onto one file. An emptied bucket takes its file with it, which is
    also how a cache written by the earlier four-file layout upgrades: the old `other` file is read,
    its records keep their history, the first census re-files each one by class, and the empty original
    removes itself.
  - A missing or corrupt file costs only the history in it: every answer degrades to "we do not know",
    never to "everything is dead", and `HasBeenSilentFor` returns `false` for anything unknown. Deleting
    the files by hand is safe, and each one says so in its first line.
  - Nothing consumes it yet; it is registered by `AddLightingWeb` so that it is accumulating history
    before the first consumer needs it.

- **A theme picker**, at the right-hand end of the top bar, with a third palette beside light and dark.
  - *Follow the system* is the default and is what every browser did before this shipped: no
    `data-theme` attribute, `prefers-color-scheme` answers, nothing changes for anyone who does not
    open the dropdown.
  - **0z0 tech** is the new palette — the ZeroZero Software design language: blue-black surfaces, a
    teal accent, and monospace type throughout, which that language calls a load-bearing choice rather
    than a code-block convention. State colours are the app's own, as that language's product-colour
    rule expects.
  - The choice is kept in this browser, not in the configuration document, so two people reading the
    same house can read it in different colours and neither has anything to save.
  - No flash on reload. A blocking script in the document head puts `data-theme` on `<html>` before the
    body is parsed, which a Blazor Server app needs: the server paints the first frame, so a preference
    read after render arrives one repaint too late.

- **An activity page**, at *Activity* in the top bar: the engine's recent decisions as a timeline,
  newest first, grouped by day, with the room and the time down the left. Each row says what happened
  and — where the engine declined to act — why, in the darkness gate's own words: *Too bright to switch
  the lights on · lux 86, dark below 40*. That is the measured reading beside the configured threshold,
  which is the answer to "why didn't that light come on".
  - Fed by the same `adaptive_lighting_area` events the dashboard already receives. No new subscription,
    no log file is read, and the engine is unchanged. The timeline therefore starts when adaptive
    lighting starts, and shows only what Home Assistant delivered.
  - Bounded to the most recent 500 reports, so a process that runs for months cannot grow without limit.
    The page says when it is holding the cap and that older reports have been dropped.
  - A room filter, and an honest empty state: a house that has just been set up has every room switched
    off by design and can legitimately sit quiet, so the page explains the quiet rather than showing a
    blank panel.
  - New reports are counted as they arrive but not inserted. A button adds them, so the timeline never
    moves under somebody who is reading it.

- **Brightness that follows the daylight.** A room can now be told to brighten as it gets lighter
  outside, so a hallway does not read as gloomy against a bright window at noon. Off by default: a
  house that does not switch it on behaves exactly as it did.
  - Five per-room settings, inherited from *Defaults* and overridable per room like every other:
    `LuxBrightnessEnabled`, `LuxBrightnessStartLux` (at or below it, the schedule is used unchanged),
    `LuxBrightnessFullLux` (at or above it, the adjustment is fully applied), `LuxBrightnessMaxPct`
    (the brightness it is raised *toward*) and `LuxBrightnessGamma` (the curve's shape).
  - The interpolation is on `log10(lux)`, not lux. Illuminance spans orders of magnitude while
    perceived brightness is roughly its logarithm, so a linear map would spend its whole range in the
    top decade. With anchors at 100 and 10 000 lx, 1 000 lx is exactly halfway up the curve.
  - It raises, it never lowers, and the period's own `MinBrightnessPct`/`MaxBrightnessPct` still bind:
    a night period capped at 30 % stays capped at 30 % whatever the sky is doing. Sleep mode's clamp
    also still wins.
  - The reading comes from the room's own lux sensor when it has one, and otherwise from
    `Global.OutdoorLuxSensor` — the same sensor the darkness gate reads, resolved once.

- **A note when a room is switched on**, on both the House tab's room row and the room page. It says
  how many lights the room will now command and **names every one of them**, because a count alone
  leaves somebody hunting through the room.
  - The lights that look like something other than room lighting are marked, each with a reason:
    status LEDs and indicators, a trailing `_led` on an id, a colour channel of a lamp the room
    already commands whole, and a light inside an appliance. On one live house that is 13 of the
    living room's 19 lights — three access-point LEDs, four board indicators, five WiZ channels and
    the fridge.
  - **Advisory, never a filter.** Home Assistant's `entity_category` is not exposed by HassModel, so
    every rule is a heuristic on a name, and a heuristic may point but must never quietly drop a
    light. The rules are asymmetric on purpose: accusing needs a whole word, excusing needs only a
    substring, so an LED strip and a *taklys* are not mistaken for indicators.
  - It recommends making a *Room light* label in Home Assistant and naming it under *House › Finding
    lights & sensors › Only manage lights with*, and says plainly that the setting reaches every room.
    A house that already names an include label is not told to do it again.
  - The switch works either way and the note is dismissible; a room already read is not raised again.
    Switching a whole floor on raises nothing — six notes at once is a wall rather than a warning.

### Changed — action required

- **Renaming a period is now free, and the settings file gains ids.** A period's name used to be the key
  every reference used, so renaming one silently broke whatever pointed at it — a room's levels, a mode's
  dim-while-asleep period, a reset trigger, a dropdown mapping. Periods and house-mode options now carry an
  `Id`, minted once and never shown, and every reference inside the file points at that instead. The name is
  display only, two periods may share one, and the warning that used to sit under the name field is gone.
  - **Your file is migrated on the first start after upgrading, once.** Ids are filled in and every
    reference is repointed at the period or option it already resolved to; the file is then written back with
    the previous version kept as the usual `.bak`. Later starts change nothing. A reference that matched
    nothing is left exactly as it was and still reports what it always reported — a room's levels row warns
    and survives, a dropdown mapping errors.
  - **The old key names still parse**, so an unmigrated file loses nothing: `Period` becomes `PeriodId`,
    `SetsMode` becomes `SetsModeId`, `ClampPeriod` becomes `ClampPeriodId` and `ResetOnPeriodStart` becomes
    `ResetOnPeriodStartId`. Hand-written YAML wanting the new names should use the ids the migration wrote;
    the ids read as `night-3c9f`, not as a GUID, for exactly that reason.
  - **A house-mode option's `Value` is unchanged and still matches Home Assistant.** That string is the
    dropdown's, not ours, so renaming the option *in Home Assistant* still needs re-pointing here. Only
    renames on this side became free.

- **A room counts as dark below 1000 lx, not 40.** The reading a room gates on is usually not a
  reading of that room at all: most houses have one outdoor lux sensor and many rooms with none. One
  live instance's outdoor sensor, measured over 30 hours, sits at 1–3 lx at night and 1000–3706
  through the day — and it is shaded, so an unobstructed one would read 10 000–50 000. Against 40 lx
  every room read *not dark* from first light until dusk while sitting genuinely dark. The owner's
  rule: better to light up too early than never. A room whose sensor really does measure the room
  wants a low number, which is one line on that room.
- **The house-wide outdoor lux sensor is no longer a silent fallback.** `Global.OutdoorLuxSensor`
  used to be handed to every room that resolved no lux sensor of its own, so a room's darkness could
  be decided by a reading taken outside it. A room now opts in with **`FollowOutdoorLux: true`**.
  - **A room with no lux sensor is simply dark** — the lux half of its gate stops holding it back and
    movement lights it. Under `Darkness: Either` that makes such a room behave as `Always` until it
    is given a reading; under `Lux` a sensor that *exists* and will not read still falls back to the
    sun, because a Zigbee dropout is not the same as having no sensor.
  - Existing documents are **not rewritten**. A document that names an outdoor sensor no room follows
    now gets a validation warning saying exactly what changed and how to put it back.
  - The daylight brightness curve reads whatever the darkness gate reads — one sensor per room, one
    answer — so a room following the outdoor sensor for its *level* opts in the same way.
- **A room with several light-level sensors averages them instead of using none.** An area with more
  than one candidate used to use neither, on the ground that the engine could not tell which was the
  room's. That left a better-instrumented room strictly worse off than a bare one, and once disabled
  8 of 17 rooms on one house. The average is **geometric**, at the owner's decision: brightness is
  perceived logarithmically, so 170 lx and 3000 lx average to 714 rather than 1585, and those fall on
  opposite sides of a 1000 lx threshold. Non-positive readings are dropped before the average, since
  a geometric mean multiplies; a room whose every reading is zero is 0, which is pitch dark.
  - Dead sensors are dropped first: no state, `unavailable`, `unknown`, or **silent for longer than
    `Global.LuxSensorStaleAfterMinutes`** (default two hours). Silence is judged on `last_updated`,
    not `last_changed`, so a sensor sitting at a steady 3 lx all night is not condemned for being
    consistent. Nothing is called dead until the engine has been watching for at least that window —
    Home Assistant resets every timestamp when it restarts.
  - **Illuminance only.** Measured on one live instance, 30 of 51 motion sensors had not reported in
    over two hours and every one was healthy: a motion sensor reports on change, so silence means
    nobody walked through that room. Motion's only test for death stays no state / `unavailable` /
    `unknown`.

### Changed

- **The dark theme separates its surfaces and lifts its secondary text.** A card sat 1.08:1 against the page
  it was on, so nothing had edges and the theme read as flat; and `--muted`, which carries the nav, the labels
  and every secondary sentence, sat at 8.69:1 on a panel. The page goes darker, the panels lift, and `--muted`
  moves with them — because lifting panels alone takes muted text *down* to 7.96:1, and most text is on a
  panel. Measured over 120 rendered text nodes: median 8.69 → 10.12, nothing below AA, panel-against-page
  1.08 → 1.27. Light and 0z0 are untouched; neither had the problem.

- **Picking a house-mode helper no longer rewrites the option list**, and the *Use the helper's options*
  button is gone with it. Adopting invented mappings nobody chose, and dropped the settings of every option
  the new helper did not also offer — the one edit in the page that could destroy a mode's configuration. The
  cards already render a row per live option and mark the stranded ones, so nothing became unreachable and a
  helper picked by mistake now costs nothing. The drift notice stays, as a sentence that describes rather than
  offers.
- **Chart labels keep their size on a phone.** Every label in the daylight and lux charts was sized in SVG
  user units, which scale with the drawing: measured at 390 px, the daylight chart's 9-unit type rendered at
  3.8 real pixels. They are now derived from the chart's own rendered width, so a label is the same physical
  size at any width, and keyed to the chart rather than the window — a narrow chart on a wide screen had the
  same problem. Desktop rendering is unchanged, measured at 1280 px. Browsers without container queries keep
  exactly the old behaviour.

- **A period with `StartsOnMotion` no longer begins at its `Start`.** It was half a feature: only the
  house-mode brain ever read the flag, so the clock still entered the period on time and the lights
  brightened at 06:30 whether or not anybody was up. The flag moved the house *mode*, and only inside
  one `CircadianTickSeconds` or after a restart mid-period.
  - Now the previous period keeps running — the house stays at night levels — until somebody moves in
    one of `StartsOnMotionAreas`, and then the period begins whole: brightness, warmth and `SetsModeId`
    together, for every room. An empty `StartsOnMotionAreas` still means any room the engine watches.
  - It falls through on its own. The next period's own `Start` overtakes a period that never began, so
    an empty house is never stranded on last night's levels and the day never ends holding a period
    nobody started.
  - Still once per local day, still nothing at all under `PeriodAuthority.HomeAssistant`, and still
    never before the period's own `Start` — a 02:00 trip to the kitchen is not the morning.
  - A document where *every* period sets `StartsOnMotion` now warns: nothing would be in force from
    midnight until somebody moved.
  - **Restarting inside such a period no longer re-fires it.** The day latch was never seeded at
    start-up, so the first movement after a deploy re-applied the period's `SetsMode` and its
    period-start reset over a mode a person had chosen. It is now seeded from the note the engine
    already keeps of which period the last run ended in, and movement writes that note at once rather
    than waiting for the next tick, so a configuration save cannot drop the house back to night.
- **Groups are preferred for motion and light-level sensors, exactly as they already were for
  lights.** The same code, reached from all three domains: transitive membership, a cycle guard, a
  clip on any group reaching into another Home Assistant area, and widest-coverage selection between
  overlapping groups.
  - The bug this fixes: on one live instance `binary_sensor.kontor_trening_bevegelse` is a `motion`
    group of two sensors and the office subscribed to all three, so one wave of a hand fired the area
    three times — re-arming its vacancy timer and publishing on each.
  - For light-level sensors it can settle a room outright: a group listed beside the two sensors
    inside it was three candidates and is now one reading.
- **Several entities on one Home Assistant device are one piece of hardware, and only one is used.**
  Measured on one live instance: five light entities in the office — an RGBW fixture's combined
  entity beside its own colour channels — are one device, and the engine commanded all five. Groups
  have no device and every duplicate does, so a group claims the devices of everything beneath it and
  the loose entities on the same fixture drop; the office resolves to `light.kontorlys_alle` alone.
  Where no group covers a device, the entity its siblings extend with an underscore wins, then the
  shortest id, so the answer never depends on registry order. Motion is deliberately exempt — a
  device there is a *controller*, and a multi-zone presence sensor exposes genuinely different zones.
- **Movement into a room the engine will not light now leaves a row on the activity page**, naming
  what stopped it: the master switch, the room's own switch, an empty house, a guest scene, a
  sleeping house, a named entity that is on, or simply that the room is bright enough already.
  - Bounded on the *reason*, not the reading: one report per area per change of the refusing gate, so
    forty walks under one unchanged block produce one row and a lux value drifting from 900 to 980
    produces none. The bound resets once the room actually lights, so a block that returns is news
    again. That is what makes this affordable — publishing per blocked movement was deferred once
    precisely because it risked an event every time anyone walked through a sunlit room.
- **Rooms are called what Home Assistant calls them.** An auto-discovered room writes only its area
  id, and nothing asked the registry for the area's name, so the room page's heading, the board's
  lanes and the activity log's room column all read the slug — `kjeller_bad` for a room Home
  Assistant knows as *Kjeller - Bad*. The name is now resolved wherever a room is named, in one
  place: a `Name` in the document still wins, then the registry's name, then the area id.
  - Resolved on read and **never written to the document**, so renaming an area in Home Assistant
    renames the room here. Adding a room, adopting an area and changing a room's area have all
    stopped copying the name in for the same reason.
- **Picking lights and sensors by hand offers the room's own entities**, with a tick-box that widens
  to the whole house — the scoping that was lost when the room page replaced the old area editor. On
  one house that is 3 candidates instead of 164. Anything already picked stays listed and removable
  even when it falls outside the scope. *Blocked while on* is deliberately still house-wide: a
  do-not-disturb flag belongs to no area at all.
- **The all-rooms defaults and *Finding lights & sensors* moved from Areas to House.** Below a
  seventeen-room list they were effectively invisible. House now has a rule worth stating: it holds
  the settings there is exactly one of. `?section=defaults` follows them, the Areas lede says where
  they went, and the room page's settings reveal links to the baseline it is measured against.
- **The text is larger, and the quiet text is easier to read.** Every size in the type scale moves up
  a step — the uppercase section label from 11 to 12.5 px, help lines and timestamps from 12.5 to 14,
  dense settings rows from 13.5 to 15 — and the muted colour lifts in all three themes. Muted text on
  the page background measures 9.41:1 on Dark, 6.05:1 on Light and 8.70:1 on 0z0, where Dark was
  6.51:1. The commissioning column heads stop hardcoding their own size and take the tokens.
- **Dropdowns that offered *Type as text* no longer do.** Every preset picker had a toggle into a free
  text field, on lists where anything worth choosing is already offered. Brightness gained **0% (off)**
  in its place, which is what people were typing. A value already in a document that the list does not
  offer still shows and is still kept.
- **A period's *Starts* no longer repeats itself.** The line under the picker read *every day at 06:45*
  beside a control that already said 06:45. It is shown for the starts that need explaining and
  suppressed for a plain clock time.

### Changed

- **The last-seen cache moved into `<config>/last-seen/` and stopped keeping backups.** One house's
  configuration folder held about 160 files — one document a person edits, and everything else machine-written,
  half of it `.bak` copies nobody reads. The cache now writes one file per bucket into a subfolder of its own,
  through an atomic move with no `.bak` beside it: the only thing a backup could buy back is history this cache
  is already documented as losing gracefully, where every answer degrades to *we do not know* and never to
  *everything is dead*.
  - Files an earlier build wrote beside the document are **moved in on the next start**, with their history, and
    the backups that build kept are dropped. Nothing is lost and nothing needs doing by hand.
  - An emptied bucket still takes its file away, and now takes an older build's leftover `.bak` with it.

### Fixed

- **A house mode no longer promises a reset that Home Assistant's authority has stood down.** Under
  `HouseModeAuthority.HomeAssistant` the engine stops firing reset triggers, but the mode card went on
  describing them: *"switches back to Normal when 'Morgen' starts"*. It now says the rules are paused, which
  is what the dormant-rules notice on the same page has always said.

- **The mode preview follows the time-of-day dropdown again**, on any house that has been through the
  stable-key migration. The preview handed its calculator the period's *display name* where the calculator
  matches on its *key*, which is the id once one exists — so under Home Assistant's period authority the cards
  silently fell back to the clock, which is the one thing that method exists to prevent. Every test fixture
  named its periods without ids, where the two spellings coincide, so nothing caught it.
- **The time-of-day helper is written when the engine starts, not one tick later.** `SchedulePeriodic`'s first
  callback is a whole `CircadianTickSeconds` away, so a restart inside a period left the `input_select` naming
  the period *before* it — measured on one house at the 300 s tick, five minutes of every dashboard and
  automation reading the wrong time of day. It is written again when the master switch releases, which crosses
  no boundary and so triggered nothing. Both are no-ops under Home Assistant's authority, and when the select
  already reads the right option.
- **The schedule runs on the household's clock, not UTC.** `IScheduler.Now` is a `DateTimeOffset` at `+00:00`,
  and the engine compared its `TimeOfDay` directly against each period's `Start`, which is a wall clock. One
  house ran **two hours behind** all summer: `Natt` at 22:30 began at 00:32, so walking through at midnight got
  the evening period's 70 % instead of the night's 5 %. The engine's idea of *today* rolled over at 02:00 local
  with it, which reached the motion latch and the once-a-day period rules.
  - The offset follows daylight saving, so it could not be dialled out of the document.
  - `CircadianCalculator` and `ModeMonitor` now take the household's `TimeZoneInfo`, defaulting to the machine's,
    and **must be given the same one** — a period's mode switch is otherwise filed against a different day from
    the one that placed it.
  - The pages were never affected: the activity list and the board already converted, which is why the UI looked
    correct while the lights did not.
  - Sixteen tests were asserting UTC behaviour without saying so and would have passed on a UTC CI box whatever
    the engine did. They now name `TimeZoneInfo.Utc`, and the conversion is asserted against a fixed `+02:00`
    zone so it means the same thing anywhere.
- **A restart no longer reads as somebody changing the house mode.** Every rebuild seeds the house
  stream with a fabricated state before publishing the observed one, so the first genuine publication
  looked like a transition: a settings save put one *Mode changed to X* row in the record, and a
  restart put one per adopted room, from a `select` nobody had touched. The opening publication now
  carries `Startup`, and the rule that already drops empty start-up rows drops these.
- **A restart and a settings save are now rows of their own**, in Background. The record was fed only
  by per-area events, so the one fact that explained three phantom mode rows — that the engine had
  restarted three times — was the one thing it could not say. `LightingEngineHost` raises one
  `EngineNotice` per rebuild; an entry carries either an area report or a notice, so no state, period
  or darkness verdict is invented for a row no room reported.
- **A click inside an (i) panel no longer ticks the checkbox behind it.** The panel sat inside the
  `<label>`, and a click on any descendant of a label is forwarded to the first labelable element in
  it. The label now wraps the box and its own words, and the (i) is its sibling.
- **A lamp holding a colour temperature is now moved to equal channels.** Home Assistant publishes
  `rgbw_color` / `rgbww_color` only while the fixture is *in* that colour mode, so an equal-channels
  room whose lamp sat in colour-temp mode was read as already-correct and never commanded. A fixture
  that offers a colour channel and reports none is not using it; one that offers no colour at all is
  still left alone, so a plain dimmer is not re-commanded every tick.
- **The warmth buttons on the House tab write something.** Every choice on that tab was parsed as a
  darkness rule, so picking *How warmth reaches these lights* there silently did nothing. Each choice
  is now parsed against its own setting's type. Per-room warmth was never affected.

## [2.0.0]

**Zone became area.** Home Assistant calls a room an *area*; a *zone* is a GPS region like "Home" or
"Work". The one word this project had chosen was the one word HA users already use for something else.
Types, YAML keys and every label now say *area*, and the word in prose is *room*.

Read the first item before upgrading. It is the only change that can break something outside this
repository, and it needs you to do something by hand.

### Changed — action required

- **The published Home Assistant event is renamed, with no dual publishing.** `laget_lighting_zone` is
  now **`adaptive_lighting_area`**, and its `zone` field is now **`area`**. Nothing publishes the old
  name any more: it is a clean break, not a deprecation. **Any HA automation, script, template or
  dashboard card listening for `laget_lighting_zone` or reading its `zone` field stops working until
  you update it by hand.** The engine and the bundled web UI ship together, so the dashboard itself
  never sees a mismatch — only your own automations do.

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

  The event gained an **`area_id`** field at the same time. It is additive, and it is the better thing
  to match on: display names are editable mid-session, registry ids are not.

### Changed — no action required

- **Configuration files migrate themselves, silently.** A document written before 2.0 says `Zones:`
  and `ZonesAutoDiscovered:`. It still loads: the deserialiser renames those keys before binding, and
  the engine writes the file back in the new schema on the first start after the upgrade, keeping the
  previous file at the store's backup path — the one the Configuration page already shows. There is no
  prompt, no migration command and nothing to run. A file hand-edited back to the old names keeps
  working and is re-migrated on the next start. Writing is strict in one direction only: the serialiser
  emits `Areas:`, always.

  If a hand-edited document somehow carries both an old and a new key, the new key wins, the old is
  dropped, and the load logs a warning naming both.

- **The C# API renamed with the schema**, which is what makes this a major version. `ZoneConfig` →
  `AreaConfig`, `ZoneSettings` → `AreaSettings`, `AdaptiveLightingConfig.Zones` → `.Areas`,
  `GlobalConfig.ZonesAutoDiscovered` → `.AreasAutoDiscovered`, `ZoneController` → `AreaController`,
  `ZoneState` → `AreaState`, `ZoneEntityResolver` → `AreaEntityResolver`, `ResolvedZone` →
  `ResolvedArea`, `ZoneAutoDiscovery` → `AreaAutoDiscovery`, `ZoneSnapshot` / `ZoneSnapshotCache` →
  `AreaSnapshot` / `AreaSnapshotCache`, `ZoneError` → `AreaError`. Shipped clean: no `[Obsolete]`
  twins, no aliases, no old names anywhere in the public API. Code consumers recompile against the new
  names; data files migrate themselves. The namespace root stays `AdaptiveLighting.*`, and the YAML
  document's top-level key is unchanged.

### Changed — new installations only

- **Discovered rooms now start switched off.** On a fresh install, set-up runs about thirty seconds
  after the connection settles: it finds every Home Assistant area holding both a light and a motion
  sensor, guesses each room's role from its name, adopts an obvious house-mode `input_select` if it
  finds one, and seeds the list of people. It switches **nothing** on. No light changes until the owner
  opens the UI and chooses which rooms to hand over. Previously a fresh install began commanding lights
  in every discovered room.

  Existing installations are unaffected: the flip is on *newly proposed* rooms, which carry an explicit
  `Enabled: false`. The default in `Defaults` stays `true`, so a document that never wrote an explicit
  value keeps every room it already had.

- The list of people is seeded from Home Assistant at first set-up only, and only when it is empty. An
  explicit list freezes membership, which is the trade: leaving the field empty still means everyone
  Home Assistant knows, including people added later. A deliberately emptied list is never re-seeded.

### Changed — the settings pages

- **Five sections became four: Areas · Schedule · House modes · House.** A setting now lives under the
  noun it changes. `Defaults` became the **All rooms** group inside Areas. `Advanced` is gone as a
  section; its contents moved to the section whose noun they change, behind that section's own fold
  where they are rarely touched. `Periods` is now **Schedule**, and carries the blend-between-periods
  settings, which are a property of the schedule rather than of override detection.
- **A room is switched on or off from a switch on its header row**, rather than from row 1 of 17 inside
  a collapsed override fold. The switch writes an explicit value; the override list is now "n of 16".
- **Copy rewritten away from jargon**: "Vacancy timeout" is **Lights stay on for**, "Pre-off warning"
  is **Warning dim lasts**, "Override holds for" is **Hand changes hold for**, "Darkness source" is
  **How a room decides it's dark**, "Lux threshold" is **Dark below**, "Circadian tick" is
  **Re-check the rooms every**, "Discovery conventions" is **Finding lights & sensors**.

### Added

- **Rooms group by floor** on both the dashboard and the Areas list, using Home Assistant's floor
  registry, with a per-floor *Switch on this floor*. A house that has set no floors sees no floor
  headers at all — exactly the flat grid it had before.
- **An include label.** *Only manage lights with (label)* limits management to lights carrying a chosen
  Home Assistant label. Empty — the default, and what every existing document means by saying nothing —
  manages every light discovery finds. It filters lights only: motion and lux sensors are inputs, not
  things the engine commands. The exclude label always wins over it, and an explicit `Lights` list
  bypasses both. A room whose lights are all filtered out is skipped with a message naming the label.
- **Set up rooms again**, from the Areas section header (any set of rooms) or a single room's editor.
  It rebuilds the chosen rooms from what Home Assistant knows right now, and warns first with a dialog
  that is concrete per room about what will be lost — hand-picked entities, changed settings, a custom
  name — counted, not generic. A room with nothing to lose says so. Exactly two things survive a
  rebuild: which area the room is, and whether it is switched on. Nothing is written until you save.
- **The three label fields are dropdowns** fed by the Home Assistant label registry, instead of free
  text where a typo silently disabled the feature. A house with no labels gets an explanation of where
  to create one rather than an empty dropdown; a stored value that matches no live label is kept and
  flagged, never dropped.
- **The dashboard shows only the rooms you switched on**, with a line under the grid naming how many
  are hidden and where to turn them on, and a designed first-run state for "rooms found, none enabled
  yet". Disabled rooms are still observed and still published — only their rendering changed.
- `AreaSnapshot` and the published event carry **`AreaId`**, so config and live state join on an id
  rather than on an editable display name.

[Unreleased]: https://github.com/0z00z0/adaptivelighting/compare/v2.0.0...HEAD
[2.0.0]: https://github.com/0z00z0/adaptivelighting/releases/tag/v2.0.0
