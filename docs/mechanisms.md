# Mechanisms

How the system works, and the handful of numbers that were chosen rather than derived.

Traps a single edit can trip are noted in the source, beside the code, in one or two lines. What cannot be
re-derived by reading the code is here.

---

## Configuration

### Two readers bind the config type, and only one of them can be fixed

`AdaptiveLightingConfig` is deserialised by two entirely separate readers:

- the engine's own `LightingConfigDocument.Deserialize`, which has a legacy-key pre-pass, and
- NetDaemon's `ConfigurationBinder`, against each house's app YAML, which cannot have one.

Consequences of having two:

- An unknown **key** is silence in both. `IgnoreUnmatchedProperties` is set, so a misspelled `Levels` reports
  nothing at all: no exception, no warning, no log line. A pre-2.0 file saying `Zones:` against an Areas-only
  model binds to **zero areas**, silently. The legacy-key pre-pass stops exactly that, and it translates only
  within this document's own section, because `Zones:` is a legitimate key for a GPS-zone application.
- An unknown enum **value** is a `FormatException` that kills the app at startup. It is the one thing a
  document can carry that stops the application rather than being ignored.

Enum members are pinned to explicit ordinals and are never renamed or removed. Ordinals are compile-time
constants inlined into consuming assemblies, and `Enum.Parse` accepts the bare numeral, so renumbering changes
what already-built binaries and already-written files mean. `DarknessSource.Either` is retired and must stay
parseable, because there is no way to prove no file still says the word.

Only the two legacy names are matched case-insensitively. Every other key is case-sensitive, which is why a
lower-case `areas:` can never win over `Zones:`.

### Nothing in the document points at a name

A period and a house-mode option each carry an `Id`, minted once when they are created and never derived from
the current name. Every reference from inside the document is to that id: `RoomLevelOverride.PeriodId`,
`HouseModeOptionConfig.ClampPeriodId` and `ResetOnPeriodStartId`, `PeriodSelectOptionConfig.PeriodId`, and
`TimePeriodConfig.SetsModeId`. Renaming is therefore free, and two periods may share a display name.

`HouseModeOptionConfig.Value` is the exception and stays the match key. That string belongs to Home
Assistant: the engine has to write what the `input_select` will accept and classify what it reports. Renaming
the option **in Home Assistant** still needs re-pointing here; renaming anything on this side does not.

The id is a slug of the creation-time name plus four random base-36 characters — `night-3c9f` — so a
hand-edited file stays readable. The slug is frozen at creation, so it is a hint about where the row came from
and never a fact about what it is called now.

Two accessors carry the fallback the ids cannot cover: `TimePeriodConfig.Key` and `HouseModeOptionConfig.Key`
answer the id when there is one and the name (or value) when there is not. That is the second binder's path,
the one with no pre-pass, so a house's app YAML bound by NetDaemon's `ConfigurationBinder` still resolves by
name.

### The migration off names runs once, on load

`StableKeyMigration.Apply` mints the missing ids and rewrites every reference that resolves by name onto the
id it resolved to. It runs from `LightingConfigDocument.Deserialize`, which is the load path and is otherwise
forbidden from rewriting a hand-edited file. Without it, a reference no longer read by name reads as nothing at
all.

Two things make it terminate. It repoints a reference **only** when the reference is not already some
period's id, so a migrated document changes nothing; and it reports whether it wrote anything, through
`DocumentReadResult.MintedStableKeys`, so `LightingEngineHost.Reload` takes the migrating write exactly once.
That single write matters because `LightingConfigStore` keeps one backup slot: a second rewrite would push the
only pre-migration copy of the file out of it.

A name that resolves to nothing is left exactly as it was, so the validator's existing severities still land
on it: a dangling levels row warns and survives, a dangling select mapping errors. Adding a period by hand and
reloading mints an id for it and leaves every other reference alone.

The old key names — `Period`, `SetsMode`, `ClampPeriod`, `ResetOnPeriodStart` — go through `LegacyKeys`, not a
property rename. Both binders ignore an unknown key, so a plain property rename is silent data loss for
anyone on an older file.

`LastPeriodStore` writes a period id. A note written before ids holds a name, and `ModeMonitor` resolves that
by name on read; without it the first start after the upgrade compares a name against an id and acts on a
boundary crossing that never happened.

### Normalisation runs on save, never on load

`ConfigNormalizer` cleans a document on the way out, not on the way in. The load path must not rewrite a
hand-edited file just because the application booted.

This asymmetry is load-bearing: any surface reading a freshly loaded document is reading an **un-normalised**
one, so it must not assume a normaliser rule has already been applied.

#### A list is nullable purely so the serialiser omits it

`TimePeriodConfig.StartsOnMotionAreas` is `List<string>?` for one reason: the serialiser writes nothing for
null and writes an empty sequence for an empty list. Null and empty **mean the same thing** to every reader —
any room's movement starts the period — so the normaliser writes null and one shape only ever reaches a file.

Two readers would otherwise tell them apart, and both would be wrong to:

- the load path's repair walks the list and would report a clean document as damaged;
- the page's change token serialises the **un-normalised** document, so an empty list and an absent key hash
  differently and the page believes an untouched document has been edited.

An existing document carrying the empty key still loads and still means what it meant. The next ordinary save
drops it, which is the normaliser's usual contract and not a migration.

`Either` is the exception. `LightingConfigDocument.LegacyValues` rewrites the YAML scalar `Either` to `Lux` on
the **load** path, not only on save. Every web surface reads through `LightingEngineHost.Store.Load()`, which
is `LightingConfigDocument.Deserialize`, so **no page can ever see `DarknessSource.Either`** however the file
on disk is spelled.

What keeps the member alive is the other binder: `Either` is still a defined enum value, and NetDaemon's
`ConfigurationBinder` against a house's app YAML has no legacy pre-pass, so `Darkness: Either` parses straight
through there. Predicates over `Darkness` name `Either` alongside `Lux` to match `IlluminanceGate`, which
answers them identically in all three arms. That is defensive agreement with the gate, not a path a page can
reach.

### The store normalises and validates, so every writer gets both

`LightingConfigStore.Save` normalises the document, validates it, and only then writes. Three writers reach
it, and none of them can skip either step:

| Writer | When | What an unrunnable document costs |
| --- | --- | --- |
| `LightingEngineHost.Save` | somebody pressed Save | the save — nothing reaches the disk |
| `LightingEngineHost.RewriteInCurrentSchema` | `Reload`, on a document that loaded through a superseded schema | nothing — it is written and reported |
| `LightingEngineHost.RunAreaDiscoveryCore` | 30 s after `Reload`, in a house with no rooms yet | nothing — it is written and reported |

**The two internal writes proceed and report; only a person's save is refused.** Neither has anybody behind
it. A migration that refuses to write strands the house on a file nothing will read in the current schema,
with no action available that would change that, and discovery that quietly stops adding rooms is worse than
a document with an error somebody can see. So they write, log the errors, and raise their own
persistent-notification card — the same mechanism the host uses to say a document cannot run, under its own
title so it does not replace that one. A person's save is different in the one way that matters: they are
looking at the page, so refusing it tells them.

Only `ValidationResult.Errors` are consulted. Warnings and area errors never block or alter a write, in
either direction — a dangling levels row still warns and survives.

The store cannot ask Home Assistant anything, so the host hands it `Validate` through
`LightingConfigStore.ValidateWith` in its constructor. A store built without a host falls back to the pure
document rules, which is the same check without the referential ones.

The rebuild that follows a save is total, so **every settings save
re-asserts the house mode and re-runs every area's startup path.** A mode being forced by an entity is
therefore re-applied on every edit, so an away-kind force sweeps the house again on each save.

### A retired key parses but no longer behaves

Both binders ignore an unknown key, so removing a feature leaves every existing document loading cleanly and
behaving differently, with nothing in the file to point at. `MinBrightnessPct` / `MaxBrightnessPct` are the
worked example: a night row written `{ BrightnessPct: 15, MaxBrightnessPct: 30 }` — the default shape —
clamps to 15 % in sleep mode where the retired key asked for 30 %. Half the light, silently.

`LightingConfigDocument.RetiredKeys` exists only to say so in the log on load, because the log is the one
place left where it can be said. The key stays in the file; only the next save drops it.

### A retired key on the page

The reader is the only thing that can see a retired key, because both binders drop an unmatched key and the
document is bound after the translation pass. So the reader carries what it found on the configuration object
itself, in memory only, and the validator forwards it.

It is a warning and never an error: the document runs perfectly well with the key, and refusing the save would
block the very page that removes it. One sentence per key however many places carry it, since a setting retired
on four periods is one thing to fix, and the sentence is worded once and serves both the log and the page. It
clears itself: a save writes the document without the key.

### A seed document names no entities

`AdaptiveLightingConfig.CreateDefault` fills in the circadian table and nothing else. A seed full of
`REPLACE_ME` ids looks helpful and is not: every placeholder is an id Home Assistant does not know, so a new
installation starts with a document-level error and refuses to run.

A placeholder also **overrides the discovery that would have filled the same field**. An empty
`Global.Persons` finds every person by itself; `person.REPLACE_ME` finds nothing and blocks the engine.

### One parser, and the cost of it

The app model binds this document with the .NET configuration binder, which reads and never writes. Something
has to serialise once the UI can save, and two parsers reading one file would disagree about it, so
`LightingConfigDocument` is the only loader.

The cost: YamlDotNet emits a fresh document, so **every hand-written comment in the file is lost the first
time the UI saves.** `Header` is re-emitted on every write to say where the worked examples went.

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
read is not a gate: **a room with no usable light-level sensor counts as dark**, so movement lights it. The
rule behind it is *better to light too early than never*:

- A broken sensor and an absent sensor reach the same verdict. Only a warning log line separates them, and
  they are worth separating because one is a supported arrangement and the other is a flat battery.
- The default threshold is **1000 lx**. The sensor a gate reads is usually a shaded outdoor one; on a live
  house it runs 1000–3706 lx through the day and 1–3 lx at night, so against a 40 lx threshold every room
  reads "not dark" from first light to dusk while actually sitting dark.
- Hysteresis is applied about the threshold: it takes `LuxThreshold` to become dark but
  `LuxThreshold + LuxHysteresis` to stop being dark, or a sensor resting on the threshold makes the area
  strobe.

An area with several candidate sensors **averages them geometrically**, because brightness is perceived
logarithmically. It matters at the threshold: 170 lx and 3000 lx mean **714**, not 1585, and those fall on
opposite sides of 1000. Non-positive readings are dropped before the mean (one 0 lx would drag a geometric
mean to zero, and a negative has no logarithm), but a room where *every* reading is non-positive answers 0,
which is genuinely pitch dark. Refusing to decide instead would leave a better-instrumented room strictly
worse off than a bare one.

A room does **not** follow the house's outdoor sensor unless it asks to (`FollowOutdoorLux`). One shaded
outdoor sensor reading several hundred lux while the rooms behind it sit dark otherwise makes a whole house
refuse to light itself. This is darkness only: the daylight curve reads the outdoor sensor by default, in
any room that follows the curve for at least one period, without needing `FollowOutdoorLux` — for the
opposite reason, an indoor sensor is measuring the lamps it would be setting.

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

### The boundary is its own wake-up, and the tick is the safety net

`CircadianCalculator.NextBoundary` names the instant the table's next boundary falls on, and `BoundaryTimer`
arms a one-shot a second past it. `ModeMonitor` and every `AreaController` carry one, so a period's
`SetsModeId`, the period helper and a lit room's levels all move at the boundary. Left to the tick alone, a
house on a 300 s `CircadianTickSeconds` places a boundary up to five minutes late.

`CircadianTickSeconds` still runs, and every tick re-asks. A table a save rebuilt and a clock a box corrected
after an outage each re-arm within one tick and need no subscription of their own. A timer that never fires
costs lateness, never correctness.

**A sun time that moves is the exception, and it has a subscription.** A sun-anchored boundary is placed by
`sun.sun`'s `next_rising` and `next_setting`, which Home Assistant rewrites the moment either is crossed;
until then the armed one-shot stands on yesterday's instant. `LightingOrchestrator.SunMoved` watches the sun
entity, projects those two anchors off the change's own payload, and emits through `DistinctUntilChanged`, so
the elevation and azimuth the same entity republishes every half minute announce nothing: no boundary is
anchored to either. What is left is two to four events a day. Each area is handed the observable for its own
`SunEntity` and `ModeMonitor` the house's, matching the calculator each was built with.

It runs `OnTick()`, not a bare re-arm. A sun time can move *backwards* past now as easily as forwards, and
`NextBoundary` answers with the first `Start` strictly after now, so re-arming alone would step straight over a
boundary that had just been taken into the past and lose it in silence. Evaluating and then arming is what the
tick itself does.

The projection reads the change's payload instead of calling `ReadSunTimes`, because `SubscribeSafe` guards the
handler and not the pipeline: an `OnError` out of the `Select` would take the subscription down for the rest of
the run. `AttrDateTimeOffset` is TryParse-based and total on a null state, so a sun reporting nonsense costs
nothing and a sun that stops resolving only takes its own boundaries out of the table.

A house that names no sun entity gets no subscription and keeps the tick alone. A house that names one Home
Assistant does not have gets the subscription anyway — the id is watched, not the entity — so the sun is adopted
the moment it appears, which is what a Home Assistant restart looks like from here.

The second of lead puts the instant the callback reads on the new period's side of the boundary, and
`NextBoundary` answers with the first `Start` strictly after now, so a wake can never arm for the moment it has
just handled.

One timer per area rather than one per house, because the table is house-wide but the sun entity is an area
setting, so two rooms can hold different instants for the same sun-anchored boundary. A period still waiting
for movement is out of the table here as it is everywhere else, so nothing wakes for a boundary that will not
be crossed.

None of it survives a restart and none of it needs to: start-up resolves the running period from the clock,
`LastPeriodStore` says which period the last run ended in, and `ApplyPeriodModeOnStart` acts on a boundary
crossed while the engine was down.

### A period that waits for movement

`StartsOnMotion` means the period **does not begin at its `Start`**. The previous period keeps running — the
house stays at night levels — until somebody moves in one of `StartsOnMotionAreas`, and then the period begins
whole: brightness, warmth and `SetsModeId` together, for every room.

It is implemented by **leaving the boundary out of the table**, not by a rule anywhere downstream.
`CircadianCalculator.ResolveBoundaries` skips a held period, so the wrap keeps the previous period in force and
the next period's own `Start` overtakes it without anything having to notice. Every question the engine asks —
`ActivePeriodId`, `GetTarget`, the blend — comes out of that one table, and so does the dashboard's schedule
band through `PeriodsAcross`, which is why the name, the levels and the drawing cannot disagree about a period
that has not begun.

Four things bound it, and each of them is a house that would otherwise misbehave:

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
every room hold its own opinion.

It is keyed by the **local day the running instance began on**, not by today. A boundary still ahead of now
belongs to yesterday's instance, which is the one the wrap has in force; asking about today would ask about a
period that has not come round yet.

Two places write it, and no more. `ModeMonitor.Start` seeds it from the note on disk, and movement claims it.
The seed is what stops a restart from re-firing a period's `SetsModeId` and its period-start reset over a mode a
person chose. A held period is seeded only when the note names it: without the note there is no evidence it ever
began, and the house waits for movement as it would have without the restart. Movement writes the note
immediately rather than leaving it to the tick, because a config save rebuilds the whole engine and the latch is
in memory.

#### The blend eases from the arrival, not from the boundary

A period started at 06:45 whose `Start` was 06:30 blends from 06:45. It keeps its configured length and
therefore finishes later than the same period started by the clock, rather than arriving part-way through a
window that ran while nobody was there.

That needs the latch to carry **when** a period began, not merely whether. The instant travels with the hold
as one value, so no surface can read the hold without also reading the arrival — the pair cannot be assembled
wrongly by a caller, because there is no second lookup to get wrong.

Three cases carry no instant and fall back to the boundary:

- no latch at all, which is every pure calculator and the mode cards with no engine running;
- a period nothing started, where there is no arrival to blend from;
- a start seeded from the note on disk. That run is gone and its arrival with it, and a restart inside a
  period must not restart the blend — a deploy would otherwise re-run every held period's fade.

A blend still running when the next start comes round is cut off by it. The next period is in force from its
own boundary, and an unfinished fade out of the previous one has nothing left to ease towards.

`GetPeriodTarget(id)` sits outside all of it. The sleep clamp asks for a period by id and gets the one it
named, held or not.

The mode cards read the same latch, through `LightingOrchestrator.MotionPeriods` and
`LightingEngineHost.MotionPeriods` to `ModeService`. Forwarded and never rebuilt: a fresh latch has recorded
nothing, so it would call every held period not begun and hold the card on last night's levels for the whole of
the next day. `null` while nothing is running puts every period on its clock start, which is the only answer
available with no engine to ask and the right answer for a document that holds nothing back. It is read per
call: a save rebuilds the orchestrator, so a cached reference points at a latch nobody writes to any more.

A document where **every** period sets `StartsOnMotion` places nothing at all from midnight until somebody
moves, so the validator warns. It is a warning and not an error: it is a house that waits, not a document that
cannot run.

### Resolving the next boundary

The motion hold is a question about one named day's instance, so a boundary and the hold governing it must come
from the same day. Two tables answer it, and they answer different questions.

The **instant** table stands behind `GetTarget` and `ActivePeriodId`: a start still ahead can only be in force
through the wrap, and the instance the wrap puts in force began yesterday. The **per-day** table stands behind
`NextBoundary` and `PeriodsAcross`, where a start is held against the day it falls on.

Reading the instant table for a boundary yet to arrive asks about yesterday, and answers wrongly in both
directions: a start whose own day has not begun is still woken for, and one held back the day before drops out
of the schedule. A day with nothing placeable left falls through to the next day's earliest start rather than
reporting no boundary at all.

### Who owns the time of day

`PeriodSelectConfig.Authority` decides, and `PeriodSelectReader` is the one object between the dropdown and
the engine. It holds two delegates and assigns **exactly one**, in its constructor, from the single authority
value. Nothing downstream re-asks.

The failure that construction rules out is the worst one available here: the engine writing the select while
also following it, chasing its own tail through Home Assistant with the household unable to move it.

Under Home Assistant's authority the day/night blend has no boundary time to interpolate from and becomes a
step. That is intended: the period began when somebody moved the dropdown, so there is no boundary to ease
away from.

A forced mode **never moves the select**. Printing the select's value during a force names the one thing that
did not happen, which is why `ForcedMode.Describe()` is the only thing allowed to word it — it is also the
only thing that knows which entity caused the force.

### Modes and presence

A house can read `home` on every `person.*` entity while sitting in forced Away. Presence and a forced mode
are separate causes, and a surface that conflates them sends somebody hunting a presence fault that does not
exist.

Mode entry is **edge-triggered**, so a boundary crossed while the engine was down was never seen. On restart
the schedule's `SetsModeId` is applied over a non-Normal option: a crossed boundary is a real event the
schedule is entitled to act on. What protects a person's choice is the boundary test, not the standing mode.

A missing, deleted or corrupt previous-run note are one answer: do nothing. Guessing the other way costs a
mode overwritten on no evidence, on a path a corrupt file could trigger at every start.

After a Home Assistant restart an `input_select` reads `unavailable` for a while. Anything reading one has to
survive that without acting on it.

### The daylight curve is a per-room, per-period opt-in

A period itself carries no curve choice: it has one `BrightnessPct`, house-wide, exactly as it did before the
curve existed. A room opts a *period* into the curve through its own `Levels` row for that period —
`RoomLevelOverride.FollowDaylightCurve` — never through anything on the period. Two rooms can therefore run
the same evening period two different ways: one on the curve, one stating its own percentage.

`CircadianCalculator.LevelsOf` resolves this per room per period, the same place `FromRoom` is resolved, and
carries the answer as `PeriodLevels.UsesDaylightCurve`. `ToTarget` copies it onto `LightTarget.UsesDaylightCurve`
from **this room's own row for the active period**, never from the period. `LuxBrightnessCurve.Apply` needed
no change at all for this: it already read `LightTarget.UsesDaylightCurve` and `BlendEndpoints.LeavingUsesDaylightCurve`
without caring where they came from, so re-pointing the source at the room instead of the period was the whole
fix. A room with no row for a period is never on the curve for it, whatever another room's row says.

The curve **replaces** the level rather than adding to it. That is what frees both of its ends: it runs from
`LuxBrightnessMinPct` at `LuxBrightnessStartLux` and below to `LuxBrightnessMaxPct` at `LuxBrightnessFullLux`
and above, each draggable across the whole 0–100 %, and the schedule's brightest period bounds neither. The
span is **signed**, so a bright end under the dark end is a curve that falls, not a curve that is ignored.
Interpolation is on `log10(lux)`, so each decade gets an equal share, and `LuxBrightnessGamma` shapes it.

A room's row for a period it follows the curve for keeps its `BrightnessPct` in the document, hidden on
screen. Switching back restores what was typed, and nothing has to be re-entered to try the curve for an
evening.

#### The dark end is seeded by the period this room first claims the curve for

The curve's dark end is what a room gives when it is dark outside, so no single number suits every period a
room might claim: a room asking for 15 % at night and 90 % by day is served badly by one figure sitting
between them. `DaylightCurveMode.Set` therefore writes the room's own `AreaConfig.LuxBrightnessMinPct` to
**half whatever brightness that room's cell already showed for the claiming period**, clamped to 0–100 % and
rounded to one decimal. Night at 15 % seeds 7.5; day at 90 % seeds 45.

It fires only as the curve goes from **unused to used in this room**, and only while the room states no dark
end of its own yet. A second period in the same room joining seeds nothing, because by then the value may
have been dragged into place by hand and overwriting it would undo that; nor does it fire again once the room
has any dark end at all, seeded or hand-set — that value now behaves like any other setting the room states
for itself, and stands until changed by hand. The house's own `Defaults.LuxBrightnessMinPct` is never touched
by this: seeding always writes the *room's* value, and a room that says nothing simply inherits the house
default as it always did.

**This is an editing action, not engine logic.** The engine reads whatever the document holds, so a
hand-written file runs with nothing seeded, and `AreaSettings.LuxBrightnessMinPct`'s schema default is the
fallback for a document where no room ever claimed the curve through the editor. The seeding is the normal
path to that number; the schema default is the reserve.

**The reading is the house's outdoor sensor**, `Global.OutdoorLuxSensor`, unless the room names its own
`AreaConfig.DaylightSensor`. Never the darkness sensor: an indoor sensor measures the lamps the curve is
setting, so a closed loop oscillates. That makes it a different question from `FollowOutdoorLux`, which is
about darkness only, and `LuxReader` is the one implementation both questions read through. With no sensor
named at all the curve holds its dark end, which is a level nobody chose — `ConfigValidator` warns for exactly
that, naming the rooms that follow the curve for some period and have nothing to read.

Two adjacent periods a room follows the curve for both have no boundary to draw at all for that room, because
the curve is continuous across them.

#### The blend's endpoints are each side's own answer

The brightness interpolation resolves **after** the curve, inside the period stage, so the order in
*Order of composition* is untouched: period, then daylight curve, then sleep clamp.

Each side of a boundary contributes the level it actually resolves to — the curve's answer on a curve side,
the stored `BrightnessPct` on a stated side — and the blend interpolates between those two. Where both sides
are the same kind the arithmetic reproduces the number the old order gave, exactly: two stated periods
interpolate two stored levels as before, and two curve periods interpolate two readings of one continuous
curve. **Only a mixed boundary moves**, and it moves from a step to an ease that starts where the light
actually was.

Colour temperature still blends in the calculator. The curve never touches kelvin, so there is nothing for
the two stages to disagree about.

The accepted consequence: during a blend out of a curve period the leaving endpoint is a live reading, so a
passing cloud retargets the room mid-blend. That already happened for the whole of a curve period; the blend
window now begins it thirty minutes earlier.

`LuxBrightnessEnabled` is gone, and so is the period-level `UseDaylightCurve` that briefly replaced it: no
translation exists for either onto the room's own `FollowDaylightCurve`, so both keys parse as silence and
`LightingConfigDocument.RetiredKeys` logs each once on load.

### Order of composition: period, then daylight curve, then sleep clamp

`AreaController.ResolveTarget` composes in that fixed order.

The curve lives in `ResolveTarget` and not in `ApplyTarget` because `OnTick` compares `ResolveTarget()`
against the standing target and retargets on a difference — **that is the only thing that ever notices the sun
coming out.** In `ApplyTarget` alone it would set the level on the next motion event and never before,
which in a hallway is effectively never.

The clamp goes last so a bright reading during an afternoon nap cannot lift a sleep-respecting room past the
night rules. **That order is unchanged, and worth restating as unchanged**, because the clamp now asks a
question of the curve and the two can be read as having swapped places.

#### The sleep clamp asks the curve for its clamp period's level

The clamp names a period and takes that period's brightness, for this room, as the night's ceiling. Where this
room follows the daylight curve for the named period, the answer comes from the curve rather than from the
stored figure the room's row keeps and does not use — so a room that follows the light outside at night is
capped by the light outside, and a room that does not is capped by its own stated percentage as before.

The lux sensor is therefore read twice in a sleeping room: once for the target and once for the ceiling. A
reading that moves between the two shifts the cap by a hair, which is invisible, and the result stays monotone
under the minimum because both readings run through the same curve.

### A restart across a boundary is not a period entry

`ModeMonitor` keeps two paths. `OnPeriodEntered` is edge-triggered on the tick that first sees a new period
name. `ApplyPeriodModeOnStart` handles the boundary the engine was not running for, detecting the crossing by
comparing the period on disk against the period now current.

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

### What ends a manual hold

`AreaSettings.OverrideUntilVacant` picks between two clocks and nothing else changes: the manual level stands,
and `OnOverrideExpired` still asks where the room is when the countdown runs out.

Set, the countdown is `VacancyTimeoutSeconds` and every motion event restarts it, so the level a person chose
outlasts them while they are in the room and is handed back once they leave. `OverrideDurationMinutes` is
ignored, and stays in the document so switching back restores what was typed. Clear, the countdown is that
number of minutes and motion restarts nothing.

The firing of a movement-led countdown is itself the proof the room is vacant, which is why one branch serves
both: the countdown is exactly the vacancy timeout and motion restarts it, so `IsOccupied` is already false
when it fires and the expiry lands on the empty-room branch without knowing which clock ran. `SuppressedOff`
is the same shape one state over.

It is a boolean beside the number rather than an enum over both. An absent key leaves the initialiser
standing, where an unknown enum name is a `FormatException` at start-up and `LightingEngineHost.Reload` is
documented never to throw. The initialiser is `true`, so a document that has never named the setting follows
movement; turning it off is written out as `false`, because the writer omits nulls and not defaults.

### A movement-led hold in a room with no movement sensor

A movement-led hold is judged on the last movement seen. A room with no motion sensor sees none, so nothing
ever restarts the countdown: it runs once for the full `VacancyTimeoutSeconds` and then settles the room
empty. That is the whole mechanism, and it is why a room without a sensor needs no mode of its own.

The constraint that follows: anything re-arming the hold while the room reads as vacant would hold such a room
lit for ever, there being no second event to end it.

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

The leaving sweep is the exception: an empty house is not a room going empty, and an atmospheric scene must
not keep a room lit in one. `SkipAwaySweep` keeps its existing meaning for rooms that want that.

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

Two consequences that follow:

- The scene's own light changes carry neither a user nor a parent, which is `OverrideDetector`'s definition
  of `PhysicalDevice`. Without an expectation declared for them a scened room overrides itself on the spot.
  `ExpectScene` declares one with **no polarity**, because the scene's contents cannot be read, so a scene
  that leaves a light off is still the engine's own work for the length of the echo window. After that a hand
  at the switch wins again.
- A room lit by its empty scene raises its own illuminance, so the darkness gate may then refuse to auto-on
  for movement. That is already true of any `KeepLitWhenOn` room and is not special-cased.

`AreaSnapshot.SceneApplied` names the scene the room is sitting on. `BrightnessPct` and `ColorTempKelvin` are
null while it stands, because the engine commanded no levels and must not invent them. It is not the house's
Guest scene, which `AreaState.SceneHold` reports.

### Testing a period's levels holds the room for ten seconds and changes nothing else

`AreaController.TestPeriod` puts one period's levels on a room's real fixtures and hands the room back after
`LevelTestSeconds`, which is **ten seconds** — long enough to judge a level standing in the room, short enough
that nobody waits for it. Testing is a command surface, never a state of the machine: `_levelTesting` is a flag
beside it, and every timer, hold and gate carries on around a test untouched.

The test resolves the **period**, not a pair of numbers. It takes a period key, asks
`CircadianCalculator.GetPeriodTarget` for it and sends the answer through the same `TargetCommand` ordinary
running uses, so the room's own level overrides and the daylight curve reach the fixtures exactly as the engine
would send them. A caller passing brightness and kelvin instead would be a second reading of the settings, free
to drift from the engine's.

The return is scheduled on the engine's own scheduler, so it happens whether or not whoever pressed is still
watching: a closed page cannot strand a room on test levels. `ReassertLights` **re-resolves at the instant it
fires** and never replays an answer captured before the test, because ten seconds is long enough for movement,
a boundary or a hand at a switch to have moved it, and the room has to end where it would have been had nobody
pressed. `Dispose` runs the return immediately rather than leaving it pending: a save rebuilds every
controller, and the replacement's first tick is `CircadianTickSeconds` away.

Both directions go out through `SendUnrecorded`, which **declares the expectation to `OverrideDetector` for
every light before the command reaches Home Assistant**. Without it the room reads its own work as a hand at
the switch and falls into `OverriddenOn` — and the trap is that this is the very setting the page exists to
configure, so the failure lands precisely where it is least welcome. `ReassertLights` declares `ExpectScene`
for a room sitting on a standing scene, for the same reason and with the same ordering.

`RefuseLevelTest` is the single place a test's gates are written, so the reason a button carries and the
refusal a press would get are one answer; a second copy in the web project would drift, as `AutoOnBlockNow`'s
would. It refuses under the kill switch, a room switched off and a controller mid-rebuild.

Those three are the only refusals. The rule is to give the levels back to whoever owns them: where the engine
owns them, `ReassertLights` resolves its own answer afresh; where a person owns them, the fixtures are read
before the test and put back after it, with the expectation declared per light on the way back as on the way
in. A scene-held room takes the capture too rather than re-firing the house scene, which would reach every
other room that scene names — and `EnterSceneHold` clears the room's own standing scene, so re-resolving would
take a scene-held room dark. A hand at the switch during a test drops the return, because the person has just
said what those levels are.

### Staleness culling is illuminance only, and generalising it would break the house

`LuxSensorStaleAfterMinutes` exists because a room averages its illuminance sensors, and one dead sensor stuck
on its last value drags that average for ever. An illuminance sensor reports a continuously varying number, so
two hours of silence is a fault.

The obvious generalisation to motion is **wrong**. A battery PIR reports on change only. Measured on a live
house, 30 of 51 motion sensors had not reported in over two hours and every one was healthy. Motion's only
death test is no state, `unavailable`, or `unknown`.

### Snapshot diffing compares meaning, and what that costs

`AreaSnapshot.HasSameMeaningAs` is not record equality. `==` would compare the "as of" fields, which all move
every tick, so nothing would ever be suppressed and periodic evaluation would degenerate into a fixed-rate
heartbeat.

The exclusions have honest costs:

- `LastMotionAt` excluded: motion in a room too bright to light updates the engine's record, and no tick
  republishes for it alone.
- `AutoOnBlockedBy` excluded: otherwise every area a television blocks republishes when it goes on and again
  when it goes off. The one report that must carry it — movement into a blocked room — is published past the
  comparison by `AreaController.ReportDeclinedMotion`, bounded to one row per change of refusing gate.

`LevelsFromRoom`, `IsAnyoneHome` and `Forced` **are** compared: none drifts, and each carries a case `Mode`
cannot show.

### Discovery waits, and is armed rather than inline

`LightingEngineHost.DiscoverySettle` is 30 seconds. Run inline in `Reload`, immediately after `Attach`,
discovery would scan while NetDaemon has connected but its state cache is still filling. The resolver drops
any entity with no state, so an early scan sees a partial house, proposes a partial set of rooms, and the
one-way `AreasAutoDiscovered` flag locks that in — rooms with obvious lights and motion are missed because
their entities have not arrived.

The timer callback swallows everything, because the registry throws until the first connection completes, and
an unobserved exception on a thread-pool scheduler ends the process. Discovery finding nothing is logged and
retried on the next start, with the flag left clear.

#### Membership is lights alone

A room qualifies on having lights. A movement sensor is not part of the test, so a room with lights and no
sensor is proposed, and says on its own row that it lights at the wall and never by itself — the row carries
the consequence, because a room listed with no qualifier reads as one that will light on movement.

The no-light-level warning is narrowed to rooms that **have** a movement sensor. Nothing else consults the
darkness gate, so raising it against a room that can never reach that gate names a fault the room cannot have.

### The host reports faults rather than throwing

Letting the `[NetDaemonApp]` bootstrap throw on a bad document would make the failure loud, but an app in
`ApplicationState.Error` has been disposed along with its DI scope and its `IHaContext`, leaving the host
holding a dead connection and no way to rebuild. The browser could then save a corrected file and still not
start the engine, which is the one thing the feature exists for.

### Reporting a room that cannot be set up

Rooms switched off and rooms carrying the exclude label are left out, neither being a fault. The problems
already reported are held in `<stem>.setup-faults.json` beside the configuration document, one entry per room
holding the problem's own sentence. A problem is announced the first time and then stays quiet; a changed
problem reads as a new entry and is announced again.

Only the problems standing at that start are written back, so a room that resolves is forgotten and the file is
removed once every room resolves. Without that clearing, a problem could never be reported twice. The key is
the area id where the document gives one, else the display name.

Every failure degrades to announcing rather than to silence: an unreadable or wrong-shaped file, a failed write,
and a path with no writable directory beside it all leave the standing problems announced, so the cost is a
repeated card and never a silence.

The trap: the notification id is derived from the card's title, so changing the title leaves a card raised under
the old one standing until Home Assistant restarts.

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
  - The guard against that is tested by **counting nodes visited, not seconds**. A group that contains itself
    makes the walk unbounded rather than slow, and a clock cannot tell those two apart — a loaded runner
    reads as a hang and a fast one reads as a pass. `FakeHaContext.StateReadBudget` counts every state read,
    which over a walk across groups is the nodes visited, and the budget is **200** against a measured 27, 46
    and 28 on healthy code. It throws in flight, because an unbounded walk never returns to be asserted on.
    The budget is opt-in per test, so a test that builds a self-referencing group has to set it.
- Promoted group members are re-filtered by domain. The actuator calls `light.turn_on` unconditionally, so a
  non-light that slipped through the promotion reaches a service call that cannot work.
- Several entities on one Home Assistant **device** are one fixture — typically a combined entity beside its
  own colour channels — so lights are device-deduplicated. Motion is exempt, because a multi-zone sensor is
  one device holding genuinely separate sensors. Illuminance *is* deduplicated, because the area averages.
- A group reaching into another area puts one room in charge of the other's bulbs, so the area uses what it
  owns instead. Two areas commanding one bulb set each other's brightness and switch each other off.
- Overlapping sibling groups: the widest wins, and the narrower is traded for whatever bulbs only it holds.
- A disabled entity is still a registry row with no state, and discovery would otherwise call a router LED
  room lighting.
- `IncludeLabel` null means "manage everything", and the label applies to **lights only**.
- `ExcludeLabel` on the **HA area itself** reads as the area not existing: `TryResolve` refuses before any
  discovery runs, `DiscoverArea` yields nothing, and auto-discovery therefore never proposes it. The refusal
  has to come first, because a whole-house helper area can hold a motion group whose members are four rooms'
  own sensors, and a sharing warning raised before the disabled check would fire on every start. The
  orchestrator treats the label like `Enabled: false`: an owner's act, never a fault.

`IAreaRegistry.DeviceOf` is the only **exact** answer to "are these two entities the same hardware". A
four-channel RGBW fixture presents five light entities on one device: the four channels plus the fixture's own
combined entity. Commanding all five is one lamp told four contradictory things and four times the service
calls. Every duplicate has a device id and every group helper has none, so the device is both the signal and
its own guard. `LightAudit` infers channels from id suffixes instead, which is a convention; a device is a fact
the registry records.

### Colour control reads four answers, not two

`supported_color_modes` on the area's resolved lights is read once, when the area resolves, because the
alternative is a state read per light per tick. `ColorControl.Auto` then settles on what those fixtures said:

| What the fixtures say | What the room commands |
|---|---|
| any light offers `color_temp` | `color_temp_kelvin` |
| a colour channel, but no `color_temp` anywhere | every channel at one value |
| every light that answered offers neither | brightness alone, no colour field at all |
| no light answered | `color_temp_kelvin` |

The last two rows are the same silence to a single tri-state, which is why `ResolvedArea` carries
`LightsSupportAnyColour` beside `LightsSupportColorTemp`: only the pair separates *nothing answered* from
*nothing has colour*. A light with no readable `supported_color_modes` is not counted at all, so a house still
starting up falls in the fourth row and keeps its kelvin until a fixture says otherwise; resolving it to
anything else would strip colour from every room until the next rebuild.

Row three is not a member of `ColorControl`. That enum's ordinals are pinned and an unknown member name is a
`FormatException` at start-up in an older engine, so "no colour" is `ResolvedArea.CommandsColour` instead — a
property the controller and the levels table both read. A brightness-only dimmer beside a real lamp changes
nothing: one fixture with colour puts the room in row one or two.

Detection settles `Auto` only. A stated `Kelvin` or `EqualChannels` is an owner overruling the fixtures, which
is needed in both directions because Home Assistant sometimes advertises a capability a fixture lacks.

---

## The web layer

### The engine decides; pages read snapshots

Rules live in the engine. A page that re-derives "would this room light?" is a second opinion nobody
reconciles. Where a page needs the answer it reads the snapshot, or calls the same predicate the engine calls.

There is **no Razor render-test harness** in this repository. The judgement lives in pure functions and the markup holds only their arrangement. Any behaviour question about a
page is answerable in the test project, not in the markup.

### Which period is in force is one question, asked in one place

Five surfaces answer which period is in force. Three compute it for an instant, one reads it, and one draws a
stretch of them:

- `Room.razor`'s now badge and its levels table's highlighted row;
- `PeriodsEditor.razor`'s "active now" badge and its hover;
- `ModeService`'s mode cards;
- `RoomFacts`, which reads the snapshot's period name. That is the engine's own answer at report time, not a
  computation of its own;
- the dashboard's schedule band, which asks for the whole board window rather than a single instant.

The four that compute go through a calculator from `Schedule.CalculatorFor`, and that is the only place the web
layer builds one; the three answering for an instant go on through `Schedule.InForceNow`. A hand-rolled
`ResolveBoundaries` plus `ActiveIndex` does not know that a period waiting for movement is left out of the
table, and badges the morning from 06:30 in a house still running night levels. A band laying its own
boundaries out carries that fault twice: it draws a held period from its `Start`, and under
`PeriodAuthority.HomeAssistant` it draws the clock's schedule while the engine runs the dropdown's period.

**A shared factory rather than a value the engine publishes**, because `PeriodsEditor` renders `ConfigEditor`'s
unsaved draft. The badge has to follow the periods being edited, and a published answer could only ever
describe the saved ones. Only the movement rule comes from the engine, as a predicate, which means a draft that
renames a period carries a key the latch does not hold and that period is placed by the clock until the save.

**A null predicate is ordinary behaviour exactly**: every period on its own `Start`. That is what keeps each
call site free of an "engine unavailable" branch, and it is what `tools/uihost` runs on, which never attaches
an engine.

Provenance rides back in `PeriodInForce.Rule`, because the editor's hover has to say which rule decided.
`HeldBack` is the difference between `CircadianCalculator.ActivePeriodId`, which respects the hold, and
`ScheduledPeriodId`, which ignores it, so the web layer reads the movement rule off the engine's own two
answers instead of gaining a third.

### The schedule band is that same table, over a stretch

`CircadianCalculator.PeriodsAcross` answers with the periods in force between two instants, clipped to them.
Boundaries are placed per local day, from the day before the stretch through the day after, so a window opening
inside yesterday's last period and one reaching past midnight both come out whole; one day's sun times serve
them all. A period waiting for movement is absent, so the one before it holds the stretch its `Start` would have
taken. Under an override there are no boundaries at all: the named period holds the whole stretch, because that
is what the engine is running.

`BoardView.Band` turns those stretches into percentages of the board's width and decides nothing else.

### The activity record renders per event, the engine publishes per area

One house-level change causes **every switched-on room** to publish its own snapshot. `ActivityView.Rows`
collapses that burst into one row belonging to no room; without it the record shows one identical row per
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

### Every report lands in exactly one category

`ActivityView.Categorise` returns one member, never a set. One is the floor and one is the ceiling, and each
bound is a visible fault:

- a report in **no** category is a row no combination of chips can bring back, so the page holds a line it can
  never show;
- a report in **two** survives either of them being switched off, which is a button that does not remove what
  it counts — and it makes the chip counts sum past the number of reports the page holds.

So the number beside a chip is exactly how many rows switching it off takes away, and the eight sum to the
page's own total. Categories follow the words the row shows rather than the report behind it, because that is
what a reader is switching off.

The dashboard summary reads the same categorisation and drops a turned-down movement the board's lanes already
mark, rather than spending a row of its budget saying in words what is drawn immediately above it.

### The mode an area found when it started is not a mode change

The house stream is seeded with a fabricated `HouseState.Initial` and the observed state is published after every
area has started, so the first genuine publication always looks like a transition. Read as `HouseModeChanged` it
puts a **"Mode changed to Sover"** row in the record per rebuild, from a select that has not moved since the
previous evening — and `LightingEngineHost.Save` rebuilds every controller, so two saves two minutes apart
produce two of them, and a restart one per room whose lights it adopted.

The opening publication therefore carries `TransitionReason.Startup`. The engine forgets nothing: the room is
still swept for an away-kind mode, an adopted room is still retargeted, and `ModeMonitor.AnnounceForcedMode` is
untouched. Only the label differs, and `IsWorthShowing` then drops the rows that have nothing under them.

Two consequences:

- the orchestrator publishes the opening state **even when it matches the seed**, because each area is waiting on
  that one publication to know which mode it merely found;
- `Startup` in `AreaState.Away` is the one start-up state where the engine acted, and it gets its own headline;
  "took the room as it was" would deny the sweep.

An away-at-start-up row lands under Background alone rather than under Mode. That is the cost of not claiming a
mode change: the record says the house was already away, not that it just became so.

### A restart and a save are rows of their own

The record's other input is `AreaSnapshotCache.Record`, fed by the per-area `adaptive_lighting_area` event
round-tripping through Home Assistant. On that input alone a save is **invisible** in the timeline and a restart
is a scatter of start-up rows, most of which the sift drops — so the fact that explains a cluster of phantom mode
rows, that the engine restarted, is the one thing the record cannot say.

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

One live instance's `stue` reports **517** registry entities where Home Assistant's own
`area_entities('stue')` returns **164**. The gap is disabled rows, and neither number says anything about
lighting. Pickers label areas with the resolver's post-discovery, post-ghost-filter counts instead.

Discovery is cached because it is not cheap: each candidate costs a registry read and a state read, the area
picker needs a discovery for **every** area rather than the chosen one, and Blazor re-renders whole pages. An
uncached eleven-area house runs eleven full discoveries per keystroke in the editor. Only **successful**
discoveries are cached — a throw must never become a standing answer of "nothing here".

### DST

Every boundary is resolved **through the time zone**, never at the offset the instant asking happens to carry:
the day a boundary belongs to is taken through the zone, and its wall clock is placed on that day. The two
ambiguous hours a year resolve as standard time.

A boundary's *instant*, which is what the wake-up is armed at, resolves the same way: `ConvertTimeToUtc` for
the autumn hour that happens twice, and a walk to the first minute that exists for the spring-forward gap,
which swallows the wall clock a period may be written at. Both are asserted against a synthetic zone carrying
the European rule rather than a looked-up one, so they mean the same thing on a box with no tz database.

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
report landing in the gap is counted as shown while absent from the list shown, and stays invisible until
some later report arrives, which in a quiet house is hours.

### Provenance is read off null, never inferred by comparison

A room's override is stored as a nullable twin of the house default. `null` means inherit; anything else means
the room decided. Provenance must be read off that nullness and **never** derived by comparing the room's
value against the house's — a room that explicitly sets 10 min while the house also says 10 min has still made
a decision, and comparing values erases exactly the overrides set to pin a room against future house edits.
With no room at all (the House tab rendering its own defaults) the origin is `None`, not `Inherited`.

### A page writes the part of the document it owns, and nothing else

The file is one document, but it has more than one writer, and two of them are the engine's own: area
discovery fires 30 s after start, and `Reload` rewrites a pre-2.0 document in the current schema before the
engine is built. A page that reads the whole document once and later writes the whole document back sends
every other part of it as it stood at page load, so both of those writes are silently undone.

The room page therefore **re-reads the document at save time** and applies only its own area onto it
(`RoomWrite`), which is the shape `CommissioningBoard.Commit` uses. The settings editor keeps its
whole-document save: it edits a whole draft, and there is no smaller part of it to scope to.

A re-read alone is not enough. Between the read and the write somebody may have changed the very thing the
page is about, and applying the page's copy over that reverts it just as thoroughly. Each page therefore
takes a `ConfigStamp` when it loads and compares it against the file before writing.

**The room page's stamp covers one area, not the file.** A per-file stamp would be invalidated by every write
the engine makes to itself, and the first of those lands half a minute after a deploy — which is exactly when
somebody opens a room page to check the deploy went well. Scoped to the area, a conflict means *this room*
changed elsewhere, which is rare and genuinely unresolvable by the page. The settings editor's stamp is
per file, because its write is.

Two consequences:

- The room page does not reload after saving, so `RoomWrite` hands back a fresh token taken **from the file**,
  not from the object it was given. The store normalises on the way out, so a token stamped off the caller's
  object would be stale the moment it was taken and every edit after the first would be refused.
- Retrying a conflicted save cannot clear it: the page's copy is still the old one. So the refusal names the
  room, and the page offers a reload where it otherwise offers a retry.

The room page's "Set up rooms again" is scoped to that room for the same reason. `AreaSetupService.Plan`
proposes every unconfigured area it finds, whatever scope it is given, so the plan is stripped of `NewAreas`
before the panel sees it — the warning a person reads then matches what confirming does. Adopting rooms
elsewhere in the house belongs to Configuration → Areas and to the first-run board.

### Room level overrides: the read path and the write path ask different questions

`RoomLevels.Stated` skips rows that state nothing, because `CircadianCalculator.LevelsOf` skips them: a
hand-edited file with a cleared `Kveld` row above a real one at 8 % runs at 8 %.

`RoomLevels.Find` takes any row, empty included, so an edit reuses a cleared row instead of adding a second
row for the same period. `Edit` must try `Stated` **before** `Find`, or the edit lands on a row the page is
not showing, and that row then becomes the first non-empty one and silently takes over.

Empty rows are pruned after every edit rather than at save time, because anything reading `AreaConfig.Levels`
counts a leftover row as an override.

### The borrowing stop on a preset slider

The leftmost stop of `PresetSlider` states nothing and lets the surface above supply the number. Four signals
say so at once — a hatched rail, a hollow thumb, a lit cap at the rail's end, and the readout in `--idle`
naming the borrowed number in words — because any one of them alone reads as the dimmest setting rather than
as borrowing.

`--idle` is reused instead of a colour being added. It is already the token for watching without commanding,
which is what borrowing is, and it is defined in every theme block — which is what the
colour-lives-in-several-places trap exists to prevent.

The heading row is shown from **780 px** upward, and each rail's own label is hidden there because the heading
is saying it. Below that width the rows stack, and a heading row above stacked periods names nothing near the
control it heads, so every rail carries its label again.

A value the ladder does not carry is given a stop of its own, in sorted order, never the nearest one. That is
what stops opening a page rewriting a hand-edited document.

### The text ladder in a settings row

Five levels of words appear on a settings surface, and a row paints only two of them: the setting's **name**
and its **provenance** — where the value came from and the road back. The whole explanation lives behind the
ⓘ, so a column of rows reads as a list of settings rather than as prose with controls buried in it. Anything
a control says for itself — a checkbox's own label, an empty state, a placeholder — belongs to the control
and stays where it is. Both the room page and the House tab draw all five from one stylesheet; a page styling
its own rows teaches two models of one thing.

**The trap is that `.srow-control` must be allowed to shrink.** It is `flex: 0 0 auto` so an ordinary widget
keeps its natural width beside the label, and a control that wants a line of its own then breaks out through
the card's edge instead of wrapping. Measured at a 390 px viewport with two blockers listed, the page's
scroll width was **1239** — `.chips` inside an unconstrained parent lays out at `max-content` and never wraps.
An entity list is the case that has to go further and take the card's full width: it is a block, not a widget
beside a label, and squeezed into what is left it wraps to one chip per line.

### Shutdown order: the snapshot cache stops before Kestrel

The host stops hosted services in reverse registration order, and `GenericWebHostService` is registered by
`WebApplication.CreateBuilder` before `AddLightingWeb`. So `AreaSnapshotCache` stops **first**, while Kestrel
is still serving pages.

Live pages subscribe to `AreaSnapshotCache.Changes` in `OnInitialized`, and `Subject<T>.Subscribe` on a
disposed subject throws `ObjectDisposedException`, which `SubscribeSafe` does not catch — it guards the
handler, not the subscription. The subject is therefore never disposed; dropping the subscription is what
actually stops snapshots arriving.

Separately: the cache holds one DI scope for the process lifetime, and NetDaemon's scoped `IHaContext` is
`IAsyncDisposable` only, so a synchronous scope dispose throws, surfacing as "Failed to start host".
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
parent callback inside the try reports a throw from the parent's own re-read as "nothing was written" over a
file that has just been written correctly, inviting a second press.

### Blazor: a control cannot nest inside a control

A row containing a switch cannot be a `<button>`. It is a `div` with `role="button"`, a `tabindex` and an
Enter/Space handler. The explicit `aria-label` is required, because a `role=button` otherwise takes its
accessible name from the whole row's content.

Relatedly: a click on any descendant of a `<label>` is forwarded by the browser to the first labelable
element inside it. `InfoPopover`'s scrim needs `@onclick:preventDefault`, or the scrim closes the panel and
the forwarded click reopens it in the same gesture.

The scrim is not the whole of it. Where a `<label>` wraps both a checkbox and an `InfoPopover`, the
forwarding target is the checkbox, so reading the panel toggles the setting behind it. **A label wraps its
own control and its own words; an (i) is the label's sibling.** `.housemode-check` is that inner label, and
inherits size, weight and colour from the `.housemode-field-label` span it sits in.

### Blazor: other traps

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

### A Start is a wall clock; `IScheduler.Now` is not

`IScheduler.Now` is a `DateTimeOffset` at **+00:00**, so `now.TimeOfDay` is UTC and `now.Date` is the UTC day.
Every `Start` in the document is a household wall clock. Compared directly at a +02:00 offset, a `Natt`
period written 22:30 begins at 00:32 local, and a person walking through at midnight gets the evening period's
70 % instead of the night's 5 %. The offset follows DST, so it cannot be dialled out of the document.

`WallClock.TimeIn` / `DayIn` are the only two readings, and `CircadianCalculator` and `ModeMonitor` both take a
`TimeZoneInfo` defaulting to `TimeZoneInfo.Local` — the same shape `BoardView` uses. **The two must carry the
same zone**, or a period's mode switch is filed against a different day from the one the table placed it on.

The web layer converts: `ActivityView` and `BoardView` go through `ToLocalTime()`. That asymmetry is why the
pages can look right while the lights do not.

**Tests must name the zone.** A test whose instants are built at `+00:00` passes on CI whatever the engine
does unless it passes `TimeZoneInfo.Utc` explicitly. The conversion itself is asserted against a fixed
`+02:00` custom zone, so it means the same thing on a box with no tz database. A regression test here that does
not name a zone proves nothing.

---

## Last-seen tracking

### Detecting a Home Assistant restart from the shape of the population

Home Assistant resets `last_updated` and `last_changed` on **every** restart: each entity is restored and
re-announced, so every timestamp in the house collapses to one instant. Measured on the live house 2.3 hours
after a restart, the oldest timestamp among 51 motion sensors was 2.3 hours.

The restart is therefore detected by the shape of the whole population rather than by asking: when all sample
timestamps fit inside `CollapseWindow` and the population is at least `MinimumPopulation`, the oldest
timestamp is taken as the restart moment. That needs no cooperation from Home Assistant and survives the
socket being down while it happened — which it always is. `homeassistant_start` is a second opinion, not the
mechanism.

The collapse must be read as a **transition, not a state**: a handful of chatty sensors satisfy it
permanently. For `StartupGrace` after a detected restart nothing advances, so every entity keeps the record it
had.

The tracker **samples rather than subscribes**. Home Assistant retains `last_updated` until it restarts, so a
census a minute misses nothing. A subscription would be strictly worse: the restore burst arrives *before*
anything could work out that a restart happened, so an event-driven design advances every record first and
then needs a rollback. The census reaches its verdict from the same sample it applies it to.

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

## The durable log

The add-on log lives in Supervisor's ring buffer, and a restart overwrites part of it, so a midnight incident
can be gone before anyone reads it. `UseIsoTimestampLogging()` therefore writes a second copy of every line
into `log/` beside the configuration document, which is the only directory on a Home Assistant box that
survives a deploy. A host whose `AdaptiveLighting:ConfigPath` names nothing on this machine gets the console
alone, which is what a development box wants.

### The sink hangs off the ISO logging call, and cannot be its own

`UseSerilog` **replaces** the logger. A second call adds nothing and destroys the first, so the durable sink
attaches inside the one `UseSerilog` that `UseIsoTimestampLogging` already makes. That is the same ordering
constraint the console template has, for the same reason, and it is why neither is a flag on
`AddAdaptiveLighting`.

### Retention is bounded twice, and both bounds are stated

Files roll **daily** and carry their date, `b1-20260903.log`, so a reader asking what happened on a named
night opens that night's file. Two bounds hold the directory down and neither is derived from the other:
`DurableLogFile.RetainedFileTime` is **14 days**, which is what a reader gets in the ordinary case, and
`RetainedFileCount` is **15** files of `MaxFileBytes` **4 MiB**, which is the hard ceiling at **60 MiB**.

A single byte ceiling cannot promise a fortnight and a single time limit cannot promise a ceiling, which is
why both are set. At the measured **111 kB an hour** a fortnight is about **37 MB**, comfortably inside 60 MiB
and comfortably outside the 20 MiB the previous pair of files allowed — a 20 MiB ceiling and time-bounded
retention cannot both hold, and the fortnight is the one worth keeping. A day that logs unusually hard rolls
within itself at 4 MiB and spends more of the file count, which is the ceiling doing its job.

A line is capped at 4096 characters of its own, which keeps one event from approaching a file's cap.

The sink **holds the handle** rather than appending and closing per line. Closing per line was the safer-
looking choice and bought nothing measurable: 50 of 50 lines survive a hard kill with the handle held, so the
tail immediately before a crash — the part this exists to read — is there either way.

File names carry a date, and the previous build's did not. `b1.log` and `b1.1.log` therefore match no
retention rule and would sit in `/config` for ever, so they are removed at the first start on a build that
names files this way.

### A secret is kept out by construction, not by redaction at call sites

Three properties, together:

**The rendered message is never taken.** `LogEvent.RenderMessage` hands back the interpolated string with the
secret already in it, and redacting after that is a text search for a thing that is already present.
`DurableLogFormatter` walks `MessageTemplate.Tokens` instead, so the literal halves of the template and the
runtime values stay separate and every value can be filtered before it is joined to anything.

**There is no free-text way in.** The formatter is an `ITextFormatter` and takes a `LogEvent`, and the file
`DurableLogFile` opens is written through nothing else. No call site has an `Append(string)` to reach for, so
there is no call site that could forget.

Serilog's own failures go through `LogFailureReport`, which holds back a repeat of the same message rather
than writing one line per lost event: a sink that cannot write is a fault worth seeing once, and a fault
that reports itself per event is how a full disk becomes an unreadable console.

**One filter, two tests.** `LoggedValue` drops a property whose *name* reads as a credential — `token`,
`password`, `passwd`, `pwd`, `secret`, `credential`, `api_key`, `connectionstring` — whole, including anything
nested underneath it. It then replaces a *value* that reads as one in place: a JWT (a Home Assistant
long-lived token always starts `eyJ`), a `password=` or `password:` pair, a URL's user info
(`smb://user:pass@nas`), or a long opaque run. The template's own literal text goes through the same filter,
because `ILogger.Log(someString)` compiles and then the template is runtime data too, as does an exception's
`ToString()`.

The opaque-run test wants 32 characters, mixed case and a digit, over an alphabet that omits `/`, `\`, `:` and
`.`. That is what lets a path through: an absolute Windows path is long, mixed case and full of digits, and
redacting one would gut the log to guard against nothing. Home Assistant entity ids are lower case, so they
fail the mixed-case test and survive whole. The known cost is a classic-base64 secret containing a slash,
which splits into pieces that may each fall under the length; base64url, which is what tokens actually use,
does not.

Non-string scalars skip the filter and are written through `IFormattable` against the invariant culture, so a
decimal comma or a local date order never reaches the file either.

### Timestamps carry the date, and so do the tests

`2026-08-05 00:03:12.000+02:00`, invariant, the event's own offset, one physical line per event with control
characters flattened to spaces. A value carrying a newline therefore cannot forge a second entry. CI runs in
UTC and the development box does not, so the tests assert the *shape* of the timestamp, or compare against the
same projection of a fixed `DateTimeOffset` — never a wall clock.

---

## Dark theme contrast

Contrast tokens are not the thing to measure: text on background is 15.05:1 and muted 9.41:1, both far above
AA, while the theme still reads as flat. Two separate faults sit underneath.

**Surfaces must separate.** `--panel` against `--bg` at **1.08:1** makes a card and the page it sits on
effectively the same colour, so nothing on the screen has edges. Light and 0z0 do not have this; it is
specific to Dark.

**`--muted` carries most of the interface.** The nav, the section labels, the info icons and every secondary
sentence are `--muted`, not `--text`. At 8.69:1 on a panel it passes every guideline and still looks washed out
against a near-black page, because low-saturation olive-grey on olive-black is a small *hue* step however large
the luminance step is.

Walking the rendered DOM rather than the tokens is what finds it: 120 live text nodes, their effective colour
after inherited `opacity`, against the real painted background behind each one.

| | before | after |
|---|---|---|
| panel against page | 1.08 | **1.27** |
| `--muted` on a panel | 8.69 | **10.12** |
| `--muted` on the page | 9.41 | **12.82** |
| median of 120 live text nodes | 8.69 | **10.12** |
| nodes below AA | 0 | 0 |

**Lifting the panels costs text contrast, so `--muted` moves with them.** Panel `#1c1e16` → `#22261b`
alone drops muted-on-panel from 8.69 to 7.96 — the wrong direction, and most text sits on panels. The two
changes are one change.

**Two things that look like faults and are not.** The disabled ↑/↓ period-reorder buttons measure 3.76:1 at
`opacity: 0.45`; WCAG exempts disabled controls, and dimming them is what says they are disabled. The active
nav item measures 6.86:1 because it is the amber accent, which is above AA.

**The dark palette is written twice.** The stylesheet is dark-first: the bare `:root` *is* the dark theme and
is what "follow the system" resolves to on a dark device, which is the default for anyone who never opens the
theme picker. `:root[data-theme="dark"]` is only the explicit escape hatch. Changing the second alone ships a
build where picking *Dark* by hand looks new and the default still looks old. **Serving `app.css` off the box
and searching it for the tokens meant to be gone is the check**; the `@media (prefers-color-scheme: light)`
block is the third place a surface colour lives.

---

## Chart labels are sized against the chart, not the window

Both hand-built SVG charts are drawn at a fixed viewBox and stretched to `width: 100%` — the daylight chart
730 units wide, the lux curve 620. Everything inside scales with that, type included, so a `font-size` written
in the stylesheet is a *user unit* and not a pixel. The consequence is that the labels shrink exactly as the
chart does.

At a 390 px viewport:

| | viewBox | chart width | scale | label before | label after |
|---|---|---|---|---|---|
| daylight | 730 | 308 px | 0.42 | **3.8 px** | 5.5 px |
| lux curve | 620 | 303 px | 0.49 | 9.8 px | 9.8 px |

The lux chart carries a `@media (max-width: 560px)` block bumping its labels to fixed user-unit sizes, so a
phone is covered there; the daylight chart has no such block, and 3.8 px is the defect. That bump is also why
the lux figures do not move — the cap lands on the same number the hack did.

The fix cancels the viewBox scale in CSS: `tan(atan2(730px, 100cqw))` evaluates to `730 / rendered width`, so
multiplying a wanted pixel size by it yields the user-unit size that renders at that pixel size, at any width.
It keys on a container query rather than the viewport, so a narrow chart inside a wide window thins too, and
sits inside `@supports` so a browser lacking either feature keeps the old behaviour rather than losing labels.

**The targets are measured, not the numbers in the stylesheet.** At 1280 px the daylight chart is 819 px wide
(scale 1.12) and the lux plot 1013 px (scale 1.63) — so the lux chart's "10 px" labels already render at
16.3 px. Taking the stylesheet figure as the target would shrink that chart by a third.

**The cap is what limits the gain, and `DaylightLabels` owns everything paired with it.** Labels sit at
data-driven y positions that do not grow with the type, so the cap is a layout budget rather than a taste
setting. `MaxLabelUnits` is that budget in C#, and `MinGap`, `MonthBaseline` and `ChartHeight` are all
computed from it; the stylesheet's `min(…, 15px)` is the same number written where CSS can read it. **Raising
it in one place alone brings the collisions straight back**, on a document whose boundaries happen to be close
together — which the common four-period document never is, so it passes a casual look and fails on a real
schedule.

Two constants stand behind the arithmetic and were measured, not chosen: a label's box reaches **0.77 of its
type size above the baseline and 0.42 below**, read off rendered charts across caps 16 to 24 at a 390 px
viewport.

**The month row lives in a gutter below the plot**, not over the foot of the night band. Sharing the plot it
also shares the bottom-right corner with the last period's label, and one or the other has to give way as soon
as either grows — that corner is what holds the cap at 13. The plot is still 240 units tall; the drawing is
`ChartHeight`.

The forcing case is a six-period document with three boundaries inside 25 minutes, all late in the evening,
which forces both the spread and the corner. At the cap of 13 it produces **four collisions**, at 390 px *and*
at 1280 px — "Ettermiddag", "Kveld" and "Natt" on one baseline over "Dec". At 15 and at 16 it is clean; at 17
it collides. The phone renders 6.3 px, the desktop 10.1.

**The 10.1 px the formula asks for is not reachable on a phone, and the corner is not what stops it.** That
needs a 24-unit cap, which needs `MinGap` near 34, which puts five gaps into a plot 101 px tall at 390 px: the
labels would cover the chart they annotate, and the desktop would carry the same spread for type a third of
the size. The remaining limit is the chart's own height, which is a design question and not a defect.

## The lux curve's handles, its target and its whole per cent

Figures are in the chart's own user units, 620 across, unless a pixel is named.

**A handle standing at either end of the axis is drawn `HandleInset` 16 inside the plot**, not on its true
position. Every axis label sits outside the plot, so a mark that never crosses the plot's edge cannot reach
one. Held in only by its own desktop reach of 9, a focused mark at 0 % and 1 lx measures **1.5 px outside the
plot with 0.1 px of ink clear of "0 %"** at a 390 px viewport. At 16 it sits 2.0 px inside with a 4.9 px gap,
and at 1280 px 14.7 px inside with 6.5 px. Widening the gutter instead is not open: "100 %" is 55 of the 62
units there, and 390 px is where the stylesheet caps the axis type at its largest, so the type grows while the
gutter cannot.

**The drag surface reaches `GrabMargin` past the plot on every side**, and a pointer on that margin reads as
the plot's edge. Boundary and target on one coordinate leaves a handle standing on an end with nothing around
it to aim at; with the dark end driven to 1 lx the surface's left edge is **37.6 px left of the handle's
centre**, and a press 10 px further left than the centre moves it. The margin is `PlotTop`, 14, so the surface
can never spill out of the drawing.

**The lux readings are written `LuxLabelDrop` 22 below the plot.** A label hangs from its baseline, so what a
handle on the foot of the plot can cover is the drop less one line of type: 22 − `AxisTextHeight` 10 = 12,
against a `HandleReach` of 9. At 16 the clearance is 6 and the mark covers the label.

**A brightness per cent is rounded away from zero, never to the even neighbour**, by
`ConfigNormalizer.Whole`. The control, the collapsed summary and the file have to agree, and 62.5 reading 62
on one surface and 63 on the one beside it is the disagreement the single helper exists to close. Away from
zero is also what reads as correct: 62.5 % becomes 63 %. The save pass stores the whole number, so the
document settles on what both surfaces already show.

---

## Numbers that were chosen, not derived

| Value | Where | Why |
|---|---|---|
| 1000 lx | default darkness threshold | a shaded outdoor sensor measures 1000–3706 lx by day; 40 lx leaves rooms reading "not dark" all day |
| geometric mean | multi-sensor lux | brightness is perceived logarithmically |
| echo window + transition | manual-override detection | a 30 s fade otherwise reads as a person at the switch |
| 23 | per-room settings count | the re-setup warning counts against it |
| 3 | names before "and N others" | naming three beats spending a clause to avoid printing one word |
| 2 % | `BrightnessTolerancePct` | HA reports brightness as a 0–255 integer against the engine's per cent, so a round trip lands ~1 % off; 2 % is wider than that and narrower than an eye |
| 50 K | `ColorTempToleranceKelvin` | under 2 % at the warm end, invisible anywhere in the range |
| 30 s | `DiscoverySettle` | how long Home Assistant's state cache needs before the registry reads whole |
| 1 s | `BoundaryTimer.Lead` | the wake fires just past the boundary, so the instant the callback reads is on the new period's side of it; short enough that nobody can see it and long enough to cover a timer that fires a hair early |
| ~3× | lux ladder ratio | illuminance spans four orders of magnitude; a fixed step is unusable at one end or the other |
| half the claiming period's level | seeded `LuxBrightnessMinPct` | the dark end is what that period gives when it is dark outside, and it has to land under the period's own level without collapsing to nothing; one fixed figure suits neither a 15 % night nor a 90 % day |
| 40 % / 100 % | `LuxBrightnessMinPct` / `LuxBrightnessMaxPct` schema defaults | the reserve for a document where the curve was never claimed through the editor, and the bright end throughout; 100 lx to 10 000 lx spans a dull room to a bright overcast day |
| 23 | overridable per-room settings | `RoomSettings.Keys` derives it by reflection over the nullable twins; `AreaView.OverridableSettingCount` hard-codes it and a test holds the two together |
| ~3 px | board mark de-duplication gap | a screen bound, not a clock bound, because the suppressed-off path republishes per movement |
| 12.5 / 14 / 15 / 20 / 25 px | the type scale | one step up from 11 / 12.5 / 13.5 / 19 / 24, which read as small and hard to read at a muted colour that already passed AA |
| 12.82 / 6.05 / 8.70 : 1 | muted text on the page background, Dark / Light / 0z0 | contrast floor per theme |
| 1.27 : 1 | dark theme, panel against page | at 1.08 cards do not read as cards; Light and 0z0 are comfortable here |
| 15 user units | `DaylightLabels.MaxLabelUnits`, the chart label cap | clean at 15 and at 16 on the forcing document, colliding at 17. The stylesheet carries the same number and neither may move alone |
| 0.77 / 0.42 | `DaylightLabels.LabelAscent` / `LabelDescent` | how far a label's box reaches either side of its baseline, per unit of type size, measured off rendered charts at caps 16 to 24. `MinGap`, `MonthBaseline` and `ChartHeight` are arithmetic on these two and the cap |
| 10.1 / 7.9 px | daylight chart label targets | what a 1280 px desktop already renders, so the desktop chart does not move |
| 16.3 / 14.7 px | lux curve label targets | same rule, and this plot is 1013 px wide at 1280, so its "10 px" labels are already 16.3 |
| 16 | `LuxCurve.HandleInset` | at the handle's own desktop reach of 9 a focused mark at 0 % and 1 lx measures 1.5 px outside the plot, 0.1 px clear of "0 %", at 390 px; at 16 it is 2.0 px inside with a 4.9 px gap |
| 14 | `LuxCurve.GrabMargin` | `PlotTop`, the largest margin that cannot spill out of the drawing; it puts the drag surface's left edge 37.6 px clear of a handle sitting on 1 lx |
| 22 | `LuxCurve.LuxLabelDrop` | the drop less one line of type is what a handle can cover: 22 − 10 = 12, against a reach of 9. At 16 the clearance is 6 |
| away from zero | `ConfigNormalizer.Whole` | the control, the summary and the file must show one number, and 62.5 has to land somewhere; 63 is what reads as correct, and to-even would give 62 |
| 4 MiB | `DurableLogFile.MaxFileBytes` | a day's logging at the measured 111 kB/h is about 2.7 MB, so an ordinary day is one file and a hard-logging one rolls within itself rather than spilling the whole budget |
| 15 | `DurableLogFile.RetainedFileCount` | one file per day for a fortnight, plus a file's worth of slack for the days that roll twice |
| 14 days | `DurableLogFile.RetainedFileTime` | how far back a reader can look in the ordinary case, which a byte budget alone cannot promise |
| 60 MiB | durable log hard ceiling | 15 × 4 MiB. A fortnight at the measured rate is 37 MB, so the ceiling and the fortnight cannot both fit under the 20 MiB the previous scheme allowed |
| 200 | `AreaEntityResolverTests.LoopBudget` | states read on a healthy resolve measure 27, 46 and 28, so the budget is wide enough not to fire on ordinary work and narrow enough to stop an unbounded walk in flight |
| unpadded month | version format `YYYY.M.patch` | `2026.08.0` was the first calendar-versioned release, tagged with a zero-padded month for string sort order; the published NuGet packages came back as `2026.8.0` regardless, because NuGet strips a leading zero from each numeric segment on publish. From the next release on, the tag and the packages agree by not padding in the first place |

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
- Three availability predicates exist and are not one: `IsAvailable` drops a null state and
  `unavailable` but keeps `unknown`; `AsUsableState` drops `unknown` too, so a house-mode select sitting on
  `unknown` classifies as no mode; `AreaEntityResolver.IsLive` also drops it, because an entity that has never
  reported is indistinguishable from absent when pre-populating rooms.
- `IsOff` is not `!IsOn`. Both are false for an unavailable entity.
- In `LightAudit`, accusing words match whole underscore-separated segments while excusing words match
  substrings — Norwegian buries them in compounds, and bare `oven` means "above".
- Every web asset carries a `?v=` build token, or a cached `app.css` survives a deploy. The token is the
  commit sha, not the version number.

### One select, two authorities, one writer

The house mode and the time of day are two `input_select`s with the same mechanics and different rules.
`SelectMirror` holds the mechanics: whether the select is ours to write, whether it already shows the option,
and the call. The rules stay with their callers, and each passes its own log line as a callback that runs
**only if a write actually happens** — a message describing a write that did not occur is worse than none.

Ownership is structural rather than checked. `PeriodSelectReader` assigns `ReadPeriod` or `OptionForPeriod`,
never both, so "the engine owns the period select" is `OptionForPeriod is not null` and cannot drift from the authority
that decided it. The mode select's is `HouseModeConfig.HomeAssistantDecides`.

`SchedulePeriodic` does not fire immediately: its first callback is one full period away. Anything that must be
true from the first instant has to be done in `Start` as well, which is why the period select is written there
and again when the master switch releases. Neither crosses a boundary, and a boundary is all the tick watches.

### One reconciliation for both helpers

A house mode and a time-of-day period are both a Home Assistant dropdown set against rows in the document, and
the answer — which options are still offered, which the helper has stopped offering, which nothing maps yet —
is one answer. `HelperOptions.Reconcile` is it, and it carries the rule:

**An empty option list is a connection that has not answered, not a helper somebody emptied.** Read as an
answer it strikes every stored row through at once, the moment the socket blinks. So `IsLive` is true for
everything while nothing has been reported, and `Orphans` is empty.

Rendering and orphaning are separate questions. A stored row the helper no longer offers is **rendered**
whether or not Home Assistant answered — losing it off the screen would be worse than showing it — but is
**badged** as renamed only when there was an answer to judge it against. `Display` and `Orphans` are the two.

What is not shared is the shape: a mode option opens a whole editor, a period row carries one dropdown. The
picker, the direction switch and the unknown-entity notice belong to `SelectAuthorityPanel`.

### A dropped helper option is described once

A stored row whose dropdown no longer offers its value is described in one place: **a rename is a removal and
an addition**, so nothing on this side can tell them apart, and the shared wording says neither. It reads
`not in the helper`, with the explanation that it was renamed or removed and the two look the same from here.

`HelperOrphan` carries the words and `HelperOrphanBadge` the markup, so the screens cannot drift. What stays
with each caller is the **consequence** — losing a period mapping is not losing a mode's reset triggers, and
the household wants to be told which. `ConfigValidatorTests.A_Dropped_Option_Reads_The_Same_On_Both_Helpers`
holds both halves: the same diagnosis, different tails, and neither guessing at the cause.

### The UI can be run, and how

`AdaptiveLighting.Web` is a Razor Class Library with no host. `tools/uihost` is a host: the real components,
the real stylesheet, a fake Home Assistant, and a gitignored `local.yaml` that can hold a copy of a real
document. It is not in the solution, because CI should not build a developer tool.

Three things it needs that are each invisible until the page looks broken rather than errors:
`builder.WebHost.UseStaticWebAssets()`, because static web assets are automatic only in Development;
`app.MapStaticAssets()`, which serves both `_content/**` and `_framework/blazor.web.js`, without which the page
server-renders once and never opens a circuit; and a `PackageReference` to
**`Microsoft.AspNetCore.App.Internal.Assets`**, which is where `blazor.web.js` actually lives — not the shared
framework, not the SDK. A NetDaemon host gets it transitively.

`UseStaticFiles()` is **not** among them. Measured with it removed, both assets return full bodies and the
circuit opens; a 0-byte body comes from the missing package in a Production environment, not from its absence.

**Nothing starts the engine there**, so the dashboard, the activity record and every room page would sit on
*"hasn't reported yet"* — the whole read side of the UI. The host therefore raises one `adaptive_lighting_area`
event per area in the document once the cache has subscribed, shaped by what that area is configured to do: a
scene, a `KeepLitWhenOn` holder, or plain levels. It goes through `FakeHaContext.RaiseEvent` and not through
`SendEvent`, which is the fake's record of what the engine published and is not wired back round.

### Razor does not check component parameter names at compile time

Passing a parameter a component does not have compiles cleanly and throws **at first render**:
`Object of type 'X' does not have a property matching the name 'Y'`. In Blazor Server that exception is
unhandled, so it **tears the circuit down**: the page stops responding to every click, not just the one that
triggered it, and looks frozen rather than broken. A clean build and a green suite prove nothing about it,
because nothing that does not render a component can catch it.

**So: a component whose parameters change means grepping its call sites, and opening the pages in
`tools/uihost`.**

### The last-seen cache does not get backups, and does not sit on the document

**Its own subfolder.** `/config/adaptive-lighting/` holds exactly one file a person ever edits. Burying it
under some seventy-five machine-written buckets makes it unfindable, so the cache lives in `last-seen/`
beneath it. The stem stays in each file name even there, because two houses can share a `/config`.

**No `.bak`.** The write is a temp file and an atomic move, so a torn file is not reachable; a backup could
only buy back *history*, and losing history is this cache's documented, graceful failure — every answer
degrades to "we do not know", never to "everything is dead". A backup per bucket would be seventy-five files
guarding against the one outcome that is already acceptable.

`LastSeenStore`'s constructor moves anything an earlier build left beside the document into the subfolder and
deletes the backups that build kept. It runs on every start and is a no-op after the first, because there is
then nothing beside the document to find. Every failure in it is swallowed: this is a cache, and the worst
outcome of giving up is a bucket that starts again as unknown, which must never stop the engine from starting.

The constructor also creates the directory eagerly, so `PathFor` names somewhere that exists.
