# Mechanisms

How the system works, and the handful of numbers that were chosen rather than derived.

This is not a changelog and not a record of decisions. It is here because the source comments were cut back
hard on 2026-08-03, and some of what they held could not be re-derived by reading the code. If you are about
to change something in this file's territory, read the section first.

Traps that a single edit can trip are still noted in the source, next to the code, in one or two lines.

---

## Configuration

### Two readers bind the config type, and only one of them can be fixed

`AdaptiveLightingConfig` is deserialised by two entirely separate readers:

- the engine's own `LightingConfigDocument.Deserialize`, which has a legacy-key pre-pass, and
- NetDaemon's `ConfigurationBinder`, against each house's app YAML, which cannot have one.

Consequences that follow from having two:

- An unknown **key** is silence in both. `IgnoreUnmatchedProperties` is set, so a misspelled `Levels` reports
  nothing at all: no exception, no warning, no log line. A pre-2.0 file saying `Zones:` against an
  Areas-only model binds to **zero areas**, silently. The legacy-key pre-pass exists to stop exactly that,
  and it translates only within this document's own section, because `Zones:` is a legitimate key for a
  GPS-zone application.
- An unknown enum **value** is a `FormatException` that kills the app at startup. It is the one thing a
  document can carry that stops the application rather than being ignored.

Enum members are therefore pinned to explicit ordinals and are never renamed or removed. Ordinals are
compile-time constants inlined into consuming assemblies, and `Enum.Parse` accepts the bare numeral, so
renumbering changes what already-built binaries and already-written files mean. `DarknessSource.Either` is
retired and must stay parseable forever; there is no version at which removing it becomes safe, because
there is no way to prove no file still says the word.

Only the two legacy names are matched case-insensitively. Every other key is case-sensitive, which is why a
lower-case `areas:` can never win over `Zones:`.

### Nothing in the document points at a name a person typed

A period and a house-mode option each carry an `Id`, minted once when they are created and never derived from
the current name. Every reference from inside the document is to that id: `RoomLevelOverride.PeriodId`,
`HouseModeOptionConfig.ClampPeriodId` and `ResetOnPeriodStartId`, `PeriodSelectOptionConfig.PeriodId`, and
`TimePeriodConfig.SetsModeId`. Renaming is therefore free, and two periods may share a display name.

`HouseModeOptionConfig.Value` is the exception and stays the match key. That string belongs to Home
Assistant: the engine has to write what the `input_select` will accept and classify what it reports. Renaming
the option **in Home Assistant** still needs re-pointing here; renaming anything on this side does not.

The shape is a slug of the creation-time name plus four random base-36 characters — `night-3c9f`. A raw GUID
would be correct and unreadable, and a hand-edited file is a supported thing to have. The slug is frozen at
creation, so it is a hint about where the row came from and never a fact about what it is called now.

Two accessors carry the fallback the ids cannot cover: `TimePeriodConfig.Key` and `HouseModeOptionConfig.Key`
answer the id when there is one and the name (or value) when there is not. That is the second binder's path,
the one with no pre-pass, so a house's app YAML bound by NetDaemon's `ConfigurationBinder` still resolves by
name as it always did.

### The migration off names runs once, on load

`StableKeyMigration.Apply` mints the missing ids and rewrites every reference that resolves by name onto the
id it resolved to. It runs from `LightingConfigDocument.Deserialize`, which is the load path and is otherwise
forbidden from rewriting a hand-edited file. The precedent, and the reason, is the `Zones:` pre-pass: the
alternative is silent data loss, because a reference that is no longer read by name is a reference that reads
as nothing at all.

Two things make it terminate. It repoints a reference **only** when the reference is not already some
period's id, so a migrated document changes nothing; and it reports whether it wrote anything, through
`DocumentReadResult.MintedStableKeys`, so `LightingEngineHost.Reload` takes the migrating write exactly once.
That single write matters because `LightingConfigStore` keeps one backup slot: a second rewrite would push the
only pre-migration copy of the file out of it.

A name that resolves to nothing is left exactly as it was, so the validator's existing severities still land
on it: a dangling levels row warns and survives, a dangling select mapping errors. Adding a period by hand and
reloading mints an id for it and leaves every other reference alone.

The old key names — `Period`, `SetsMode`, `ClampPeriod`, `ResetOnPeriodStart` — go through `LegacyKeys`, not a
property rename. Both binders ignore an unknown key, so simply renaming the property would have been silent
data loss for everyone on an older file.

`LastPeriodStore` writes a period id. A note written before ids holds a name, and `ModeMonitor` resolves that
by name on read; without it the first start after the upgrade compares a name against an id and acts on a
boundary crossing that never happened.

### Normalisation runs on save, never on load

`ConfigNormalizer` cleans a document on the way out, not on the way in. The load path must not rewrite a
hand-edited file just because the application booted.

This asymmetry is load-bearing: any surface reading a freshly loaded document is reading an **un-normalised**
one, so it must not assume a normaliser rule has already been applied.

`Either` is the exception, and it is worth stating exactly because it is easy to get wrong in both directions.
`LightingConfigDocument.LegacyValues` rewrites the YAML scalar `Either` to `Lux` on the **load** path, not
only on save. Every web surface reads through `LightingEngineHost.Store.Load()`, which is
`LightingConfigDocument.Deserialize`, so **no page can ever see `DarknessSource.Either`** however the file on
disk is spelled.

What keeps the member alive is the other binder: `Either` is still a defined enum value, and NetDaemon's
`ConfigurationBinder` against a house's app YAML has no legacy pre-pass, so `Darkness: Either` parses straight
through there. Predicates over `Darkness` name `Either` alongside `Lux` to match `IlluminanceGate`, which
answers them identically in all three arms. Treat that as defensive agreement with the gate rather than as a
path a page can reach.

### `LightingEngineHost.Save` is the only write path

Normalise, validate, write, re-read, rebuild every area controller. Nothing else writes the document.

The rebuild is total, which has a consequence worth knowing: **every settings save re-asserts the house mode
and re-runs every area's startup path.** A mode being forced by an entity is therefore re-applied on every
edit, which is how a cabin swept itself dark repeatedly while its owner was editing settings.

### A retired key parses but no longer behaves

Both binders ignore an unknown key, so removing a feature leaves every existing document loading cleanly and
behaving differently, with nothing in the file to point at. `MinBrightnessPct` / `MaxBrightnessPct` are the
worked example: a night row written `{ BrightnessPct: 15, MaxBrightnessPct: 30 }` — the shape the shipped
default had — clamped to 30 % in sleep mode and now clamps to 15 %. Half the light, silently.

`LightingConfigDocument.RetiredKeys` exists only to say so in the log on load, because the log is the one
place left where it can be said. The key stays in the file; only the next save drops it.

### A seed document names no entities

`AdaptiveLightingConfig.CreateDefault` fills in the circadian table and nothing else. A seed full of
`REPLACE_ME` ids looks helpful and is not: every placeholder is an id Home Assistant does not know, so a new
installation starts with a document-level error and refuses to run.

Worse, a placeholder **overrides the discovery that would have filled the same field**. An empty
`Global.Persons` finds every person by itself; `person.REPLACE_ME` finds nothing and blocks the engine.

### One parser, and the cost of it

The app model binds this document with the .NET configuration binder, which reads and never writes. Once the
UI can save, something has to serialise, and letting the engine load through the binder while the UI loads
through YamlDotNet would be two parsers disagreeing about one file. `LightingConfigDocument` is therefore the
only loader.

The cost is real and worth knowing before you promise anyone otherwise: YamlDotNet emits a fresh document, so
**every hand-written comment in the file is lost the first time the UI saves.** `Header` is re-emitted on
every write to say where the worked examples went.

Serialisation uses `OmitNull`, not `OmitDefaults`, because an explicit `Enabled: false` has to survive a
round trip.

### Null means inherit

A null on an area means "take the house default". A serialiser that wrote nulls back as resolved values would
freeze every area at whatever the defaults happened to be that day, and a later change to the house would
stop reaching the rooms. The same applies to an empty `MotionDeviceClasses`, which means "the built-in set":
writing the resolved fallback into the file pins it on first save.

The .NET configuration binder **appends** to a non-empty default rather than replacing it, so any list the
binder can reach has to default empty and resolve its fallback on read.

A bare `Levels:` with nothing under it assigns null straight over a property initialiser, and the room's
controller then fails at build time. YamlDotNet does this for any section left empty.

---

## The engine

### Darkness

`IlluminanceGate` answers `Lux` and `Either` identically, in all three of its arms. A gate with nothing to
read is not a gate: **a room with no usable light-level sensor counts as dark**, so movement lights it.

This is a product decision, and the rule behind it is *better to light too early than never*:

- A broken sensor and an absent sensor reach the same verdict. Only a warning log line separates them, and
  they are worth separating because one is a supported arrangement and the other is somebody's flat battery.
- The default threshold is **1000 lx**, not the 40 lx it once was. The sensor a gate reads is usually a
  shaded outdoor one; measured on a live house it ran 1000–3706 lx through the day and 1–3 lx at night, so
  against 40 lx every room read "not dark" from first light to dusk while actually sitting dark.
- Hysteresis is applied about the threshold: it takes `LuxThreshold` to become dark but
  `LuxThreshold + LuxHysteresis` to stop being dark, or a sensor resting on the threshold makes the area
  strobe.

An area with several candidate sensors **averages them geometrically**, because brightness is perceived
logarithmically. It matters at the threshold: 170 lx and 3000 lx mean **714**, not 1585, and those fall on
opposite sides of 1000. Non-positive readings are dropped before the mean (one 0 lx would drag a geometric
mean to zero, and a negative has no logarithm), but a room where *every* reading is non-positive answers 0,
which is genuinely pitch dark. It used to refuse to decide at all, which was defensible while the alternative was picking
one arbitrarily — one real house offered the probe inside its fridge as a candidate — but it left a
better-instrumented room strictly worse off than a bare one, and it is how eight of seventeen rooms once
stopped working.

A room does **not** follow the house's outdoor sensor unless it asks to (`FollowOutdoorLux`). Every sensorless
room used to do so automatically, and one shaded outdoor sensor reading several hundred lux while the rooms
behind it sat dark made a whole house refuse to light itself.

Staleness is measured against `LastUpdated`, not `LastChanged`, or a sensor steadily reporting 3 lx is
condemned for being consistent. Home Assistant resets entity timestamps on restart, so immediately after one
a week-dead sensor and a healthy one look identical; the grace period is measured from gate construction for
that reason.

### Periods and the circadian table

One calculator is built **per area**, because the period table is house-wide but the sun entity and the room's
own level overrides are not. `LevelsOf` is the single place a room's effective level is decided.

That is forced by the blend: `GetTarget` interpolates between the two periods either side of a boundary, and
a room replacing one side and not the other has to arrive from *its* level, not the house's. Replacing an
already-blended value afterwards turns a smooth arrival into a step.

Period **ids** are the join between periods and everything referring to them — house modes, room level
overrides, select mappings. `TimePeriodConfig.Name` is display only, two periods may share one, and renaming
one costs nothing. Every lookup goes through `TimePeriodConfig.Key`, case-insensitively, with both sides
trimmed.

Sun-anchored boundaries are laid out for the day before, the day of and the day after, so a period past
midnight still places, and so a DST boundary resolves against its own offset rather than the window's.

### A period that waits for movement

`StartsOnMotion` means the period **does not begin at its `Start`**. The previous period keeps running — the
house stays at night levels — until somebody moves in one of `StartsOnMotionAreas`, and then the period begins
whole: brightness, warmth and `SetsModeId` together, for every room.

It is implemented by **leaving the boundary out of the table**, not by a rule anywhere downstream.
`CircadianCalculator.ResolveBoundaries` skips a held period, so the wrap keeps the previous period in force and
the next period's own `Start` overtakes it without anything having to notice. Every question the engine asks —
`ActivePeriodId`, `GetTarget`, the blend — comes out of that one table, which is why the name and the levels
cannot disagree about a period that has not begun.

This was half-built for a long time. The flag existed and only `ModeMonitor` read it, so the clock still entered
the period on time and the lights brightened at 06:30 regardless; the flag moved the *house mode* and only
inside one `CircadianTickSeconds`, or after a restart mid-period.

Four things it is bounded by, and each of them is a house that would otherwise misbehave:

- **Its own `Start` must have come round.** Otherwise a 02:00 trip to the kitchen is the morning. This is also
  what stops the wrapped case — night, still running at 02:00 — from restarting itself on the far side of
  midnight.
- **The next period overtakes it.** An empty house is never stranded on last night's levels, and the day never
  ends holding a period that never started.
- **Once per local day**, so walking back in at lunch does not re-fire `SetsModeId` over a mode somebody chose.
- **Nothing at all under `PeriodAuthority.HomeAssistant`.** The dropdown is the boundary there.

The latch itself is `MotionPeriodLatch`: one object per engine, written by `ModeMonitor` and read by every
area's calculator through an injected predicate. A predicate and not the object, because the calculator is pure
— the instant is an argument, sun times arrive through a delegate, nothing there reads a clock, Home Assistant
or a motion sensor. It has to be shared because a calculator is built per area and a latch inside one would let
eighteen rooms hold eighteen opinions.

It is keyed by the **local day the running instance began on**, not by today. A boundary still ahead of now
belongs to yesterday's instance, which is the one the wrap has in force; asking about today would ask about a
period that has not come round yet.

Two places write it, and no more. `ModeMonitor.Start` seeds it from the note on disk, and movement claims it.
The seed is what stops a restart from re-firing a period's `SetsModeId` and its period-start reset over a mode a
person chose — three separate reviews found that the day latch was never seeded. A held period is seeded only
when the note names it: without the note there is no evidence it ever began, and the house waits for movement
as it would have without the restart. Movement writes the note immediately rather than leaving it to the tick,
because a config save rebuilds the whole engine and the latch is in memory.

The blend is unchanged, which means a period started at 06:45 whose `Start` was 06:30 arrives already part-way
through its blend. The window trails the boundary, and the boundary is still 06:30.

`GetPeriodTarget(id)` sits outside all of it. The sleep clamp asks for a period by id and gets the one it
named, held or not.

A document where **every** period sets `StartsOnMotion` places nothing at all from midnight until somebody
moves, so the validator warns. It is a warning and not an error: it is a house that waits, not a document that
cannot run.

### Who owns the time of day

`PeriodSelectConfig.Authority` decides, and `PeriodSelectReader` is the one object between the dropdown and
the engine. It holds two delegates and assigns **exactly one**, in its constructor, from the single authority
value. Nothing downstream re-asks.

The failure that construction rules out is the worst one available here: the engine writing the select while
also following it, chasing its own tail through Home Assistant with the household unable to move it.

Under Home Assistant's authority the day/night blend has no boundary time to interpolate from and becomes a
step. That is intended: the period began when somebody moved the dropdown, so there is no boundary to ease
away from, and inventing one would be a smoother lie rather than a truer answer.

A forced mode **never moves the select**. Printing the select's value during a force names the one thing that
did not happen, which is why `ForcedMode.Describe()` is the only thing allowed to word it — it is also the
only thing that knows which entity caused the force.

### Modes and presence

A house can read `home` on every `person.*` entity while sitting in forced Away. Presence and a forced mode
are separate causes and a surface that conflates them sends somebody hunting a presence fault that does not
exist.

Mode entry is **edge-triggered**, so a boundary crossed while the engine was down was never seen. On restart
the schedule's `SetsModeId` is applied over a non-Normal option deliberately: a crossed boundary is a real
event the schedule is entitled to act on. What protects a person's choice is the boundary test, not the
standing mode.

A missing, deleted or corrupt previous-run note are one answer: do nothing. Guessing the other way costs a
mode overwritten on no evidence, on a path a corrupt file could trigger at every start.

After a Home Assistant restart an `input_select` reads `unavailable` for a while. Anything reading one has to
survive that without acting on it.

### Order of composition: period, then daylight curve, then sleep clamp

`AreaController.ResolveTarget` composes in that fixed order.

The curve lives in `ResolveTarget` and not in `ApplyTarget` because `OnTick` compares `ResolveTarget()`
against the standing target and retargets on a difference — **that is the only thing that ever notices the sun
coming out.** In `ApplyTarget` alone it would raise the level on the next motion event and never before,
which in a hallway is effectively never.

The clamp goes last so a bright reading during an afternoon nap cannot lift a sleep-respecting room past the
night rules.

### A restart across a boundary is not a period entry

`ModeMonitor` keeps two paths on purpose. `OnPeriodEntered` is edge-triggered on the tick that first sees a
new period name. `ApplyPeriodModeOnStart` handles the boundary the engine was not running for, detecting the
crossing by comparing the period on disk against the period now current.

They are separate because `OnPeriodEntered` also fires the period-start reset: routing a restart through it
would cancel a retained Away or Guest mode as a side effect of a deploy. Not knowing which period the last run
ended in is "do nothing", never "a boundary was crossed".

What is stored is the period **id**, not a timestamp. A sun-anchored `Start` is re-resolved daily, so
yesterday's 20:14 is not today's; a sun-anchored period the sun entity cannot place is dropped from the table
entirely; and a box returning from an outage may carry an uncorrected clock. Comparing two ids touches none
of that.

### An unreadable period select is not a period change

Under Home Assistant authority, while the helper is unavailable `ActivePeriodId` falls through to the clock,
and that answer is indistinguishable from a real one. A household holding "day" at 23:30 would have night's
`SetsModeId` latch the house asleep — and nothing puts it back when the helper returns, because levels revert on
their own but a latched mode does not. `ModeMonitor.OnTick` computes `overrideIsBlind` for exactly this.

The mirror write works by **comparison, never by memory**: it writes only when the select is not already
showing the wanted option. That makes it self-healing for a select moved by hand or one that came back from a
restart on the wrong option. A remembered "already asked" latch would make both permanent.

### Manual-override detection, and why a flat bulb looks like a person

`IHaContext.CallService` is fire-and-forget and returns no context id, so nothing can compare context ids.
`OverrideDetector` combines two imperfect heuristics: an expectation declared *before* every command, and
`EntityState.Context`. Both fail in the safe direction.

- The window is the echo window **plus the command's own transition length**, or the tail of a 15-second fade
  reads as a human at the switch.
- A **parent** context is checked before the user id, because a script a person started carries both, and it
  is the script that set the level.
- `IsHandAtTheSwitch` requires **both ends** of a state change to read on or off. Home Assistant writes
  `unavailable` with a context carrying neither user nor parent, which is exactly `PhysicalDevice`. Without
  that guard a Zigbee hiccup pins the area in `SuppressedOff` and the reconnect pins it in `OverriddenOn`.

### Auto-on gates

`AreaController.AutoOnBlockNow` is the single place the auto-on gates are written. A second copy will drift.

Darkness gates **auto-on**, not adoption: lights already on when the engine starts are adopted whatever the
light level, because the engine did not turn them on and switching them off is not its call.

`AdoptedAtStartup` carries no commanded levels, because there was no command. A row or snapshot must not
invent brightness or kelvin figures for it.

### A room's two scenes each replace one transition, and nothing else

`SceneOnMotion` and `SceneWhenEmpty` are per-room and independent. Each replaces one command the engine had
**already decided to make**. Neither is a reason to make one, and neither is a gate.

| Transition | Without a scene | With one |
|---|---|---|
| Movement lights the room (`AutoVacant` → `AutoActive`, and motion rescuing `PreOff`) | `ApplyTarget` | `SceneOnMotion`, then the room is not re-aimed |
| Vacancy timeout | warning dim, then off | `SceneWhenEmpty`, no dim, room stays lit |
| Override expires into an empty room | off | `SceneWhenEmpty` |
| Leaving sweep | off | **off** |

The leaving sweep is the deliberate exception: an empty house is not a room going empty, and an atmospheric
scene must not keep a room lit in one. `SkipAwaySweep` keeps its existing meaning for rooms that want that.

The warning dim is skipped only for `SceneWhenEmpty`. It exists to warn that the lights are about to go out,
and for such a room they are not. A room with only `SceneOnMotion` still dims and still goes off.

`AreaController.SettleEmpty` is the single answer to "what is this room's off-transition", so the off a
`KeepLitWhenOn` hold refused settles as a scene for a room that names one. The hold is consulted at the same
point for both: while it applies the room does neither, and stays lit as it was. The empty scene is not a way
around the hold. `SettleHeldBackOff`'s `Away` branch stays a `TurnOff`, because the sweep it refused was a
sweep.

`_standingScene` is what stops the room being re-aimed: it is set by `ApplyScene`, cleared by `Send`, and
guards the retarget in both `OnTick` and the house-mode branch of `OnHouseChanged`. A light command is the
engine aiming the room itself, which the scene no longer describes.

Two consequences that follow and were accepted:

- The scene's own light changes carry neither a user nor a parent, which is `OverrideDetector`'s definition
  of `PhysicalDevice`. Without an expectation declared for them a scened room overrides itself on the spot.
  `ExpectScene` declares one with **no polarity**, because the scene's contents cannot be read, so a scene
  that leaves a light off is still the engine's own work for the length of the echo window. After that a hand
  at the switch wins again, as it always did.
- A room lit by its empty scene raises its own illuminance, so the darkness gate may then refuse to auto-on
  for movement. That is already true of any `KeepLitWhenOn` room and is not special-cased.

`AreaSnapshot.SceneApplied` names the scene the room is sitting on. `BrightnessPct` and `ColorTempKelvin` are
null while it stands, because the engine commanded no levels and must not invent them. It is not the house's
Guest scene, which `AreaState.SceneHold` reports.

### Staleness culling is illuminance only, and generalising it would break the house

`LuxSensorStaleAfterMinutes` exists because a room averages its illuminance sensors, and one dead sensor stuck
on its last value drags that average for ever. An illuminance sensor reports a continuously varying number, so
two hours of silence is a fault.

The obvious generalisation to motion is **wrong**. A battery PIR reports on change only. Measured on a live
house on 2026-07-28: 30 of 51 motion sensors had not reported in over two hours and every one was healthy.
Motion's only death test is no state, `unavailable`, or `unknown`.

### Snapshot diffing compares meaning, and what that costs

`AreaSnapshot.HasSameMeaningAs` is deliberately not record equality. `==` would compare the "as of" fields,
which all move every tick, so nothing would ever be suppressed and periodic evaluation would degenerate into a
fixed-rate heartbeat.

The exclusions have honest costs, and both were accepted:

- `LastMotionAt` excluded: motion in a room too bright to light updates the engine's record, and no tick
  republishes for it alone.
- `AutoOnBlockedBy` excluded: otherwise every area a television blocks republishes when it goes on and again
  when it goes off. The one report that must carry it — movement into a blocked room — is published past the
  comparison by `AreaController.ReportDeclinedMotion`, bounded to one row per change of refusing gate.

`LevelsFromRoom`, `IsAnyoneHome` and `Forced` **are** compared: none drifts, and each carries a case `Mode`
cannot show.

### Discovery waits, and is armed rather than inline

`LightingEngineHost.DiscoverySettle` is 30 seconds. Discovery used to run inline in `Reload`, immediately
after `Attach`, when NetDaemon has connected but its state cache is still filling. The resolver drops any
entity with no state, so an early scan sees a partial house, proposes a partial set of rooms, and the one-way
`AreasAutoDiscovered` flag locks that in. Observed on a real installation: four rooms with obvious lights and
motion were missed because their entities had not arrived.

The timer callback swallows everything, because the registry throws until the first connection completes, and
an unobserved exception on a thread-pool scheduler ends the process. Discovery finding nothing is logged and
retried on the next start, with the flag left clear.

### The host reports faults rather than throwing

The original design let the `[NetDaemonApp]` bootstrap throw on a bad document, so the failure was loud. But
an app in `ApplicationState.Error` has been disposed along with its DI scope and its `IHaContext`, leaving the
host holding a dead connection and no way to rebuild. The browser could then save a corrected file and still
not start the engine, which is the one thing the feature exists for.

### Entity resolution

One selection pass settles lights, motion and illuminance, because a one-level comparison of a group against
the ids it lists is defeated three separate ways: **nesting** (a group of groups), **reach** (a group holding
entities another area owns) and **overlap** (two groups sharing members with neither containing the other).
Throughout, a bulb dropped from a room is treated as a worse fault than a bulb commanded twice.

Where no group covers a device, the winner is ordered: group beats plain entity, then breadth, then the entity
its siblings extend with an underscore (an RGBW fixture's combined entity beside `_r`, `_w`), then shortest id
ordinally. The last two exist so **the answer never depends on registry enumeration order**.

- Group membership is walked **transitively**, under a visited set. Without the visited set a self-containing
  group hangs the resolver.
- Promoted group members are re-filtered by domain. The actuator calls `light.turn_on` unconditionally, so a
  non-light that slipped through the promotion reaches a service call that cannot work.
- Several entities on one Home Assistant **device** are one fixture — typically a combined entity beside its
  own colour channels — so lights are device-deduplicated. Motion is exempt, because a multi-zone sensor is
  one device holding genuinely separate sensors. Illuminance *is* deduplicated, because the area averages.
- A group reaching into another area puts one room in charge of the other's bulbs, so the area uses what it
  owns instead. Two areas commanding one bulb set each other's brightness and switch each other off.
- Overlapping sibling groups: the widest wins, and the narrower is traded for whatever bulbs only it holds.
- A disabled entity is still a registry row with no state. Discovery once called a router LED room lighting.
- `IncludeLabel` null means "manage everything", and the label applies to **lights only**.

`IAreaRegistry.DeviceOf` is the only **exact** answer to "are these two entities the same hardware". One live
house's office held five light entities on one device: a four-channel RGBW fixture's channels plus the
fixture's own combined entity. The engine was commanding all five, which is one lamp told four contradictory
things and four times the service calls. Every duplicate has a device id and every group helper has none, so
the device is both the signal and its own guard. `LightAudit` infers channels from id suffixes instead, which
is a convention; a device is a fact the registry records.

---

## The web layer

### The engine decides; pages read snapshots

Rules live in the engine. A page that re-derives "would this room light?" is a second opinion nobody
reconciles. Where a page needs the answer it reads the snapshot, or calls the same predicate the engine calls.

There is **no Razor render-test harness** in this repository. The consequence is a deliberate shape: the
judgement lives in pure functions and the markup holds only their arrangement. Any behaviour question about a
page is answerable in the test project, not in the markup.

### The activity record renders per event, the engine publishes per area

One house-level change causes **every switched-on room** to publish its own snapshot. `ActivityView.Rows`
collapses that burst into one row belonging to no room; without it the record showed one identical row per
room, each attributed to a room that had not done it, degrading linearly with every room switched on.

The collapse rules:

- only a **consecutive** run collapses, never a scattered set;
- the whole sentence must match, so two different modes stay two rows;
- the run must fall inside `ActivityView.CollapseWindow`;
- a limited read finishes the run it is inside, so the last row's room count is a fact about the house rather
  than about the read budget.

"Speaks for the house" is narrower than the House chip. A scene hold and a room's own enablement switch are
per-room facts even when a house-wide action caused them.

The circadian tick republishes **every room every pass**, so a room dark from dusk to dawn reports hundreds of
times. Those repeats are never adjacent, because all rooms are re-checked in the same pass; a rule keyed on
adjacency would collapse nothing in any house with more than one room.

### The mode an area found when it started is not a mode change

The house stream is seeded with a fabricated `HouseState.Initial` and the observed state is published after every
area has started, so the first genuine publication always looks like a transition. Read as `HouseModeChanged` it
put one **"Mode changed to Sover"** row in the record per rebuild, from a select that had not moved since the
previous evening. `LightingEngineHost.Save` rebuilds every controller, so two saves two minutes apart produced
two of them, and a restart produced one per room whose lights it adopted.

The opening publication carries `TransitionReason.Startup` instead. The engine forgets nothing: the room is still
swept for an away-kind mode, an adopted room is still retargeted, and `ModeMonitor.AnnounceForcedMode` is
untouched. Only the label moves, and `IsWorthShowing` then drops the rows that have nothing under them.

Two consequences worth knowing:

- the orchestrator publishes the opening state **even when it matches the seed**, because each area is waiting on
  that one publication to know which mode it merely found;
- `Startup` in `AreaState.Away` is the one start-up state where the engine acted, and it gets its own headline;
  "took the room as it was" would deny the sweep.

An away-at-start-up row lands under Background alone, where it used to land under Mode. That is the cost of not
claiming a mode change: the record says the house was already away, not that it just became so.

### A restart and a save are rows of their own

The record's only other input is `AreaSnapshotCache.Record`, fed by the per-area `adaptive_lighting_area` event
round-tripping through Home Assistant. A save was therefore **invisible** in the timeline and a restart was a
scatter of start-up rows, most of which the sift drops. The one fact that explained three phantom mode rows in a
morning, that the engine had restarted three times, was the one thing the record could not say.

`LightingEngineHost` raises one `EngineNotice` per rebuild, in process, and `EngineNoticeRecorder` files it.
Raised where the engine actually came up, so a rebuild that ends faulted is in the log and the notification but
not in the record.

`ActivityEntry` carries **either** an `AreaSnapshot` or an `EngineNotice`, never a fabricated snapshot: a state, a
period and a darkness verdict would all have to be invented and the rest of the page reads them as facts about a
room. The consequences, each of which has a test:

- a notice belongs to no room, so it names none in the dropdown, and choosing a room filters it out;
- it is its own row and joins no run, in either direction, so it cannot be swallowed by a house-wide collapse or
  end one prematurely by joining it;
- it is `Background` alone, so the page opens without it;
- the board's lanes skip it, because a lane draws one room's history.

The notice stream is a `ReplaySubject` of one. The engine is started by the NetDaemon bootstrap and the web
host's recorder can subscribe after that; one is enough, because nothing but the engine's own start can precede a
browser being open.

### How refusal reports are thinned, and the one path that escapes it

On the ordinary path the engine publishes one "movement refused" report per **change of the refusing gate**,
not one per movement, so somebody pacing under an unchanged block is one report.

The suppressed-off path is the exception: it republishes on **every** movement, so a bedroom sensor re-firing
under a hand-set off produces dozens of reports minutes apart. That is why the board's mark de-duplication is
a **pixel-gap test** (about 3 px on a phone track) rather than a gate-keyed one.

### Auto-on gate order, and which refusals are worth hoisting

The engine refuses auto-on in this order: kill switch, disabled, away, sleep, entity.

`Sleep` and `EntityOn` are judged **before** the darkness gate, so a sleeping house reports `Sleep` at noon as
readily as at midnight — hence the `IsDark is true` guard on anything reading it. They are also the only two
refusals that arise where the room would otherwise have lit; the three earlier ones are house-wide and would
otherwise be repeated once per room.

### Home Assistant's registry row count is not a lighting count

One live instance's `stue` reported **517** registry entities where Home Assistant's own
`area_entities('stue')` returns **164**. The gap is disabled rows, and neither number says anything about
lighting. Pickers label areas with the resolver's post-discovery, post-ghost-filter counts instead.

Discovery is cached because it is not cheap: each candidate costs a registry read and a state read, the area
picker needs a discovery for **every** area rather than the chosen one, and Blazor re-renders whole pages. An
uncached eleven-area house runs eleven full discoveries per keystroke in the editor. Only **successful**
discoveries are cached — a throw must never become a standing answer of "nothing here".

### DST

Every boundary is resolved **through the time zone**, never at the window's own offset, and the first day is
taken through the zone rather than from `window.Start.Date`. The two ambiguous hours a year resolve as
standard time.

### Reachability

Every report the engine can publish must land in at least one `ActivityCategory`, or it is a row no
combination of filter chips can ever show. The mapping reads six inputs and is walked exhaustively.
`Background` is the only category that opens switched off.

### Reading a report from an older build

Absent fields deserialise as null and **null means "this report cannot say"**. It is never read as "nothing
was in the way" or "the room was bright". A row may not invent a refusal any more than it may invent a
promise.

### Sequence numbers count reports, not entries

`ActivityLog.Newest` counts every report ever recorded; the buffer forgets, the counter does not. That is what
lets a page compute "4 new reports" by subtraction and stay correct after eviction has started.

`Read()` returns the entries and the newest sequence **under one lock**. As two separately-locked calls, a
report landing in the gap was counted as shown while absent from the list shown, and stayed invisible until
some later report arrived, which in a quiet house is hours.

### Provenance is read off null, never inferred by comparison

A room's override is stored as a nullable twin of the house default. `null` means inherit; anything else means
the room decided. Provenance must be read off that nullness and **never** derived by comparing the room's
value against the house's — a room that explicitly sets 10 min while the house also says 10 min has still made
a decision, and comparing values erases exactly the overrides somebody set to pin a room against future house
edits. With no room at all (the House tab rendering its own defaults) the origin is `None`, not `Inherited`.

### Room level overrides: the read path and the write path ask different questions

`RoomLevels.Stated` skips rows that state nothing, because `CircadianCalculator.LevelsOf` skips them: a
hand-edited file with a cleared `Kveld` row above a real one at 8 % runs at 8 %.

`RoomLevels.Find` takes any row, empty included, so an edit reuses a cleared row instead of adding a second
row for the same period. `Edit` must try `Stated` **before** `Find`, or the edit lands on a row the page is
not showing, and that row then becomes the first non-empty one and silently takes over.

Empty rows are pruned after every edit rather than at save time, because anything reading `AreaConfig.Levels`
counts a leftover row as an override.

### Shutdown order: the snapshot cache stops before Kestrel

The host stops hosted services in reverse registration order, and `GenericWebHostService` is registered by
`WebApplication.CreateBuilder` before `AddLightingWeb`. So `AreaSnapshotCache` stops **first**, while Kestrel
is still serving pages.

Live pages subscribe to `AreaSnapshotCache.Changes` in `OnInitialized`, and `Subject<T>.Subscribe` on a
disposed subject throws `ObjectDisposedException`, which `SubscribeSafe` does not catch — it guards the
handler, not the subscription. The subject is therefore never disposed; dropping the subscription is what
actually stops snapshots arriving.

Separately: the cache holds one DI scope for the process lifetime, and NetDaemon's scoped `IHaContext` is
`IAsyncDisposable` only, so a synchronous scope dispose throws. That throw surfaced as "Failed to start host".
Teardown also runs twice in a normal shutdown, once when the host stops the service and once when the
container disposes the singleton.

### Numbers in markup must be invariant

Under `nb-NO` a `double` renders `62,5`, and an HTML attribute given that renders an **empty** field. A half
written as "0,5" parses back as five. Every number reaching an attribute goes through an invariant conversion
first, and without group separators.

### Blazor: an exception in the wrong place kills the circuit

An exception out of `OnAfterRenderAsync` or an event handler is an unhandled circuit exception, and Blazor
tears the circuit down: the page dies, not the operation. Two places make that catastrophic rather than
annoying — `ThemePicker` sits in the layout, so it would be every page, and `CommissioningBoard` is the one
surface a brand-new install lands on.

The three ordinary interop failures worth surviving are `JSDisconnectedException` (circuit gone),
`OperationCanceledException` (browser never answered) and `JSException` (script missing).
`InvalidOperationException` is what every Home Assistant read in this project treats as "not answering".

`Math.Clamp` throws when min exceeds max, which is reachable from a saveable document:
`{ StartLux: 120000, FullLux: 200000 }` passes the validator, and the derived floor then exceeds the axis
ceiling. On that curve the **ceiling yields and the floor never does** — lowering the floor instead would
silently rewrite `FullLux` below the start anchor and make every later save fail.

### Blazor: notify the parent outside the catch

`CommissioningBoard.Commit` sets a `written` flag inside the try and acts on it afterwards. Invoking the
parent callback inside the try meant a throw from the parent's own re-read was reported as "nothing was
written" over a file that had just been written correctly, inviting a second press.

### Blazor: a control cannot nest inside a control

A row containing a switch cannot be a `<button>`. It is a `div` with `role="button"`, a `tabindex` and an
Enter/Space handler. The explicit `aria-label` is required, because a `role=button` otherwise takes its
accessible name from the whole row's content.

Relatedly: a click on any descendant of a `<label>` is forwarded by the browser to the first labelable
element inside it. `InfoPopover`'s scrim needs `@onclick:preventDefault`, or the scrim closes the panel and
the forwarded click reopens it in the same gesture.

### Blazor: other traps that have each cost something

- A loop variable captured by a handler must be copied per iteration, or every row binds to the last value.
- Razor reserves `<text>` as a control-flow transition, so an SVG `<text>` element cannot be written inline;
  those charts compose markup strings instead.
- `step="any"` on a numeric input, or the browser marks a legal `62.5` invalid.
- Not `@bind` on a field that saves: every keystroke would schedule a write.
- An uncontrolled `<details>` (no `open` attribute) so Blazor's diff never fights a fold a human toggled.
- `OrderBy` rather than `List.Sort` where ranks tie: a stable sort stops an unrelated edit rebinding an
  in-progress edit to a different row.
- The icon sprite is hosted exactly once; `<use>` resolves to whichever matching id the browser saw first. It
  is inline symbols rather than a sprite file because a cross-document `<use href="sprite.svg#id">` is refused
  by the strict CSP with no console message, and an `<img src>` would isolate the glyph from the cascade so
  `currentColor` and `var(--icon-accent)` both stop working.

### Locale and CI

CI runs on Ubuntu in UTC; the development box is Europe/Oslo. Anything rendering a wall clock goes through
`ToLocalTime()`, so a test asserting a literal clock string passes locally and fails on the agent. Assert
shape, or compare against the same projection.

---

## Last-seen tracking

### Detecting a Home Assistant restart from the shape of the population

Home Assistant resets `last_updated` and `last_changed` on **every** restart: each entity is restored and
re-announced, so every timestamp in the house collapses to one instant. Measured on the live house on
2026-07-28, 2.3 hours after a restart, the oldest timestamp among 51 motion sensors was 2.3 hours.

The restart is therefore detected by the shape of the whole population rather than by asking: when all sample
timestamps fit inside `CollapseWindow` and the population is at least `MinimumPopulation`, the oldest
timestamp is taken as the restart moment. That needs no cooperation from Home Assistant and survives the
socket being down while it happened — which it always is. `homeassistant_start` is a second opinion, not the
mechanism.

The collapse must be read as a **transition, not a state**: a handful of chatty sensors satisfy it
permanently. For `StartupGrace` after a detected restart nothing advances, so every entity keeps the record it
had.

The tracker **samples rather than subscribes**, and that is the deliberate choice. Home Assistant retains
`last_updated` until it restarts, so a census a minute misses nothing. A subscription would be strictly worse:
the restore burst arrives *before* anything could work out that a restart happened, so an event-driven design
advances every record first and then needs a rollback. The census reaches its verdict from the same sample it
applies it to.

An empty house is a connection problem. Never conclude a restart or an eviction from it.

### Cache keys reach the file system, so the token must be injective

A bucket key can be a device class, which is open-ended data from Home Assistant. The file token is built from
an allow-list of `a-z`, `0-9`, `_`. Dropping alone would collide — `a/b` and `a\b` both give `ab` — so any key
altered by dropping, or truncated at 48 characters, gets an 8-hex SHA-256 fingerprint appended after a `-`.

`-` is excluded from the allow-list precisely so the mapping stays injective: a token containing `-` is always
fingerprinted, one without is always the key verbatim. The hash is SHA-256 and not `string.GetHashCode`,
because the runtime's string hash is randomised per process and this lands in a **file name**. Case folding is
load-bearing on a case-insensitive file system.

Curated bucket names are reserved. A `binary_sensor` may legitimately declare `device_class: light` — it
detects light, it is not a lamp — so it files as `binary_sensor_light` and the curated files keep holding only
what they held before.

### Where the cache lives

Cache files sit in the **configuration document's** directory and take their stem from it, so `b1.yaml` gives
`b1.last-seen.motion.json`. That directory is the only path on a Home Assistant box that survives a redeploy;
the deploy folder is wiped and re-copied every time. `AddEntityLastSeen` must therefore be registered after
`LightingConfigStore`.

### `last_reported` is invisible to this build

Since Home Assistant 2024.8, a report of a byte-identical value with byte-identical attributes moves only
`last_reported`. NetDaemon 26.21's `EntityState` does not expose that field, so such a report cannot be seen
at all. When NetDaemon surfaces it, reading it in preference to `last_updated` is the whole fix.

---

## Numbers that were chosen, not derived

| Value | Where | Why |
|---|---|---|
| 1000 lx | default darkness threshold | measured 1000–3706 lx by day on a shaded outdoor sensor; 40 lx left rooms dark all day |
| geometric mean | multi-sensor lux | brightness is perceived logarithmically |
| echo window + transition | manual-override detection | a 30 s fade otherwise reads as a person at the switch |
| 21 | per-room settings count | the re-setup warning counts against it; was 16 before the daylight-brightness settings |
| 3 | names before "and N others" | naming three beats spending a clause to avoid printing one word |
| 2 % | `BrightnessTolerancePct` | HA reports brightness as a 0–255 integer against our per cent, so a round trip lands ~1 % off; 2 % is wider than that and narrower than an eye |
| 50 K | `ColorTempToleranceKelvin` | under 2 % at the warm end, invisible anywhere in the range |
| 30 s | `DiscoverySettle` | how long Home Assistant's state cache needs before the registry reads whole |
| ~3× | lux ladder ratio | illuminance spans four orders of magnitude; a fixed step is unusable at one end or the other |
| 21 | overridable per-room settings | `RoomSettings.Keys` derives it by reflection over the nullable twins; `AreaView.OverridableSettingCount` hard-codes it and a test holds the two together. A hand-written list is how the editor came to say "n of 16" about a document with 21 |
| ~3 px | board mark de-duplication gap | a screen bound, not a clock bound, because the suppressed-off path republishes per movement |

---

## Things that are true and easy to assume otherwise

- Supervisor's `/info` returns 401 to a long-lived token, while `/logs`, `addon_stop` and `addon_start` all
  work. The asymmetry is Supervisor's.
- The add-on injects the Home Assistant host, token and websocket path as environment variables. Anything the
  deployed `appsettings.json` declares under `HomeAssistant` overrides part of that and inherits the rest,
  producing a connection that resolves nothing. The section is stripped on the way to the box.
- `.local` names resolve by mDNS and do not resolve inside the add-on container.
- An empty option list from Home Assistant is indistinguishable from an unreachable Home Assistant. It is
  never read as a difference to act on, because the alternative is offering to empty somebody's house modes
  because the connection blinked.
- Two themes sharing an id makes one unreachable, silently. `theme.js` runs before any Blazor circuit exists,
  so the allow-list rides on its script tag.
- Home Assistant reports brightness on 0–255 but **accepts** it as a percentage.
- A light told to fade to where it already is visibly restarts the fade.
- Three availability predicates exist and are deliberately not one: `IsAvailable` drops a null state and
  `unavailable` but keeps `unknown`; `AsUsableState` drops `unknown` too, so a house-mode select sitting on
  `unknown` classifies as no mode; `AreaEntityResolver.IsLive` also drops it, because an entity that has never
  reported is indistinguishable from absent when pre-populating rooms.
- `IsOff` is not `!IsOn`. Both are false for an unavailable entity.
- In `LightAudit`, accusing words match whole underscore-separated segments while excusing words match
  substrings — Norwegian buries them in compounds, and bare `oven` means "above".
- Every web asset carries a `?v=` build token, or a cached `app.css` survives a deploy. The token is the
  commit sha, not the version number.
