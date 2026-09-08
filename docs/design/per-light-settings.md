# Per-light levels inside a room

Status: stage 1 built, 2026-09-07. Stages 2 and 3 are open and sit in `docs/backlog.md`.

The rules and the reasoning below are the record of what the design decided; how the built code holds them is
in `docs/mechanisms.md` under *A level belongs to one light, never to a group*. Two points where building it
settled the document differently:

- **A light row that speaks to brightness at all owns the curve flag**, where the prose below says only a row
  stating a brightness does. Either half counts, because the page writes the curve flag without seeding a
  brightness beside it, and the narrower reading would make ticking the curve box on a light do nothing. The
  YAML example below already assumes the wider rule.
- **The "this room does not command that light" warning is the resolver's, not the validator's.**
  `ConfigValidator` is given known entity ids and no group membership, so it cannot answer the question.

A room commands every light it holds to one brightness and one warmth per period. This adds a second
layer beneath it: any single light in the room may state its own brightness, warmth or curve rule for a
period, and the room keeps commanding everything else as before. A room that states nothing per light
behaves exactly as today, down to the service calls it makes.

## The rule that settles groups

**A level belongs to one light. A group is only a way of reaching lights.** Nothing is ever stored
against a group, so a light in two groups has one value and shows it in both, and a group of groups
is the lights at the bottom of it.

Applied by hand, for one light in one period:

1. The light's own row for the period, if it states the value.
2. Otherwise the room's row for the period.
3. Otherwise the schedule.

Brightness and the curve flag travel together in step 1; warmth travels alone. A light row that states
a brightness decides the curve flag for itself (off unless the row says on). A light row that states no
brightness inherits both the room's brightness and the room's curve flag. Without this a lamp pinned to
30 % under a room that follows the daylight curve would be silently overruled by the curve.

Groups never enter the lookup. Where the page shows a group, what it shows is derived from the lights
under it, and setting a value on a group (stage 2) means writing that value to every light under it.

## What a person sees and does

**Default, nothing changed.** The room page's "Brightness & warmth" card is as it is now: the warmth
select, one row per period, the daylight curve chart where the room follows it. One line is added at
the foot of the table: a checkbox, *Adjust lights individually*. It appears only when the room
resolves to two or more lights once groups are followed to the bottom; a one-lamp room never sees it.

**Room level, checkbox ticked.** A list opens beneath the table, one line per entry the engine
commands. The entries are the room's settled light list, the same list the "In this room" chips show:
a stand-alone light is one line, a group is one line, and a light that is inside a group is not listed
at room level. Each line carries the friendly name, a summary, and a chevron.

- A light's summary is *follows the room* or *own levels for 2 periods*.
- A group's summary is *a group of 5, all follow the room* or *a group of 5, 2 set differently*.

Unticking the checkbox with rows present asks once, in the control's own label, before dropping them:
*Untick to forget the 3 lights' own levels*. With no rows present it simply closes the list. Nothing
is stored for the checkbox itself: it is ticked on page load when any light row exists.

**Light level.** Opening a light shows the period table for that light: the same columns as the
room's table (period, brightness, warmth, curve checkbox), with the room's effective value as the
inherited answer where the slider sits at its leftmost stop, labelled *the room's*. The mark that
today says *this room's own setting* says *this light's own setting*. No warmth select, no daylight
chart and no Test column inside a light: warmth capability is a fact about the room's fixtures, the
chart belongs to the room, and the room's Test button already puts every light's own level on the
real lights.

**Group level.** Opening a group lists every light under it, followed all the way down, each as a
light line described above. A group inside a group is not a fold inside the fold: its lights appear in
the flat list, once each. Stage 1 offers no controls on the group line itself.

**Orphans.** A light row whose light the room no longer commands (moved to another room, removed from
its group, renamed) is listed under the light list with what it still pins and a *Remove it* button,
mirroring the orphan period rows above.

**On a phone.** Closed, the feature costs one line. Open, it costs one line per entry. An opened light
shows the period table, which already stacks below the three-column width.

## Where the values live

One new key on an area, additive, omitted from the file when null:

```yaml
- AreaId: stue
  Levels:
    - PeriodId: evening
      Brightness: 153
  LightLevels:
    - EntityId: light.stue_leselampe
      Levels:
        - PeriodId: evening
          Brightness: 77
          ColorTempKelvin: 2400
        - PeriodId: night
          Brightness: 0
    - EntityId: light.stue_taklys_2
      Levels:
        - PeriodId: evening
          FollowDaylightCurve: true
```

- `AreaConfig.LightLevels`: `List<LightLevelOverride>?`, default `null`. `null` and empty mean the
  same thing; only `null` is omitted by the serialiser, so the normaliser writes `null` when the list
  is empty, as it does for `StartsOnMotionAreas`.
- `LightLevelOverride`: `EntityId` (the leaf light's id) and `Levels`, a `List<RoomLevelOverride>`.
  The row class is reused unchanged: `PeriodId`, `Brightness` as the 0–255 byte, `BrightnessPct` bound
  on load only, `ColorTempKelvin`, `FollowDaylightCurve`. A null field inherits per the rule above.
- `Brightness: 0` on a light row means this light is off in that period. The engine sends it as an
  off command and declares an off expectation. It is not sent as a turn-on at 0 %, which Home
  Assistant carries out as a turn-off and the override detector would then read as a hand at the
  switch.
- Neither `LightLevels` nor `EntityId` appears in the document's retired-key or legacy-key tables, so
  the raw-text pre-pass leaves them alone.

**Normaliser.** Drops empty rows, drops a light with no rows left, drops the list when empty. Rows are
pruned on every edit in the page as the room's rows are, so a cleared row never lingers.

**Validator.** Range errors for brightness and kelvin as for room rows. A row naming no period, a
duplicate period on one light, and a period no schedule has: warnings, first row wins, the row is
kept. An entity id Home Assistant does not know: the same area error other unknown ids get. A light
Home Assistant knows but this room does not command: a warning, the rows are kept, and the page shows
the orphan. Two `LightLevels` entries for the same light: warning, first wins.

**Older build reading the new key.** The key is unknown to it and is silence: the room commands every
light at the room's level, which is today's behaviour, so a downgrade is safe for the lights. A save
from the older build drops the key, so a downgrade followed by a save loses the per-light rows; the
`.bak` slot beside the document holds the previous file. Not a one-way door for the engine, a one-way
door for the data once an old build saves.

**Area rebuild.** `AreaSetupService.Apply` carries `LightLevels` through a rebuild beside `Enabled` and
`Levels`, and the reflection test that pins the count of surviving properties moves with it.

## What the engine does differently

Sized against the code as it stands: the room controller changes at eight points, the calculator not
at all, the detector not at all, the subscriptions not at all.

**Resolve time** (`AreaEntityResolver.TryResolve`). `ResolvedArea` gains two read-only maps: every
entry in `Lights` to the leaf set beneath it, from the existing `LeavesOf`; and every leaf that has
`LightLevels` rows to its rows. A `LightLevels` entry whose id is under no entry is left out of the
resolved area with one warning naming it; the document keeps it. Membership is read here and not per
command, consistent with how colour capability is read.

**Build time** (`LightingOrchestrator`). One `CircadianCalculator` per room as today, plus one per
leaf that has rows, built on that leaf's merged rows: for each period, the light's row merged onto the
room's row by the rule above, into a plain `RoomLevelOverride`. The merge is a pure function in the
configuration namespace with its own tests. The calculator itself is untouched; its blend already
interpolates both ends through the rows it is given, so a light blends between its own two levels.

**Each evaluation** (`AreaController`). `ResolveTarget` becomes `ResolveTargets`: the room target as
now, plus one target per leaf calculator, each passed through the daylight curve and the sleep clamp
exactly as the room's is. `_lastTarget` becomes the set; the tick re-applies when any one of them has
moved past the tolerance. Cost per tick: one boundary resolution per leaf with rows, a sort of the
period table each. For a house of ten such lights that is ten extra resolutions a minute.

**Each command.** The fan-out lives in one place, the successor of `SendUnrecorded`, and follows this
rule for every entry in `Lights`:

- No leaf under the entry has its own target: declare the expectation on the entry and apply the
  room's command to the entry. This is today's path, unchanged, one service call per entry.
- Some leaf under the entry has its own target: declare the room's expectation on the entry anyway,
  then command each leaf under it individually, its own command where it has one and the room's
  command where it has not, declaring each leaf's expectation before its command. The entry itself is
  not commanded, so no lamp receives two commands in one send.
- A leaf is commanded at most once per send. An explicit `Lights` list can hold a group and one of its
  members side by side; the member is skipped the second time it is reached.

The expectation on the entry is load-bearing. The room subscribes to its entries, not to leaves, and a
group entity re-publishes a member's change under the group's id; without an expectation there, the
echo falls through to the context heuristic, and without `NetDaemonUserId` set it classifies as a
person at a switch. The detector matches polarity only, so the room's command is the right thing to
declare on the entry: it is on when the leaves are on.

Off commands are unchanged: one off to every entry. Scenes are unchanged. A light row at brightness 0
produces an off command for that leaf inside an otherwise-on send, and its expectation is off.

**Level test.** `TestPeriod` resolves the named period per leaf too, so the ten-second test shows the
room as it will really be lit. The capture-and-return path already works per entry and is untouched.

**Snapshot, activity log, detector, subscriptions.** Unchanged in stage 1. The room's standing
brightness in the snapshot is the room's common level; a page wanting per-light readouts reads the
document, not the snapshot.

**Colour.** Whether kelvin reaches the room is a room decision, read once from the settled entries,
and a leaf commanded individually takes the room's answer. This matches what a group fan-out already
delivers to its members today, so no new case is created.

**Actuator.** Unchanged. Its per-entity "already matches" check suppresses re-sends to a leaf whose
level has not moved, so exploding a group into its leaves costs service calls only when a level
actually changes.

## Deliberately not solved

- **A bulb newly added to a Home Assistant group takes the room's level, not its siblings'.** The
  group line shows *4 of 5 set differently*, and stage 2's group action fixes it in one press. Storing
  a level against the group would avoid this and would bring back the two-groups-one-light conflict
  the whole design exists to remove. What would reopen this: a house whose groups gain and lose bulbs
  often.
- **No per-light warmth capability.** A brightness-only lamp in a kelvin room gets a kelvin it ignores,
  as it does today through its group.
- **No per-light live readout.** The board and the room's state line keep describing the room.
- **No fold inside a fold.** Nested groups are flattened in the page. The engine already flattens
  them, and a fold per nesting level on a phone is a page nobody can read.
- **A group that contains itself.** The resolver's visited set already stops the walk; such a group
  stands for itself and everything it reaches, and this design adds no handling for it.
- **The room-level brightness 0 row.** The validator accepts 0 on a room row today, and it is sent as
  a turn-on at 0 %, which Home Assistant carries out as a turn-off against an on-expectation. This
  design closes the trap for light rows and leaves the room row as it is; it belongs in the backlog on
  its own.

## Stages

**Stage 1, shippable on its own.** Everything above except the group action. Engine, configuration,
normaliser, validator, area rebuild, the room page checkbox and list, orphans, the docs site page
("Tune one room") and `docs/mechanisms.md`.

The safety property stage 1 must prove before it ships: **a document with no `LightLevels` produces
the identical sequence of actuator calls to the build before it.** One test records the actuator calls
of a room through motion, tick, pre-off and off on the current build and asserts the new build's are
equal, entry for entry. That is what lets it deploy to a live house with people in the rooms: nothing
moves until somebody ticks the box in one room.

Second property: with one leaf under a group given its own level, the group entity receives an
expectation and no command, every leaf receives one command, and a hand-off on a sibling leaf after the
echo window is still read as manual. The mutation that must turn this red is removing the entry's
expectation.

**Stage 2.** The group action: sliders on the group line whose value shows when every light under the
group agrees and reads *mixed* otherwise, and whose change writes to every light under the group. A
per-light *Test* that puts one light's period level on that light alone.

**Stage 3, if wanted.** A per-light standing level in the snapshot for the board, and the activity
log naming a light when only that light moved.

## Decisions taken this round

Each in one or two sentences; the reasoning behind them is in the working notes, not here.

- Levels are stored per light and never per group. It removes the conflict rather than ruling on it.
- Per-light calculators on merged rows, not a light dimension inside the calculator. Zero change to
  the class that decides a room's level; the merge is a pure function.
- A light row that states a brightness owns its curve flag. Otherwise a pinned light under a curve room
  is overruled silently.
- No stored "adjust individually" flag. The rows are the fact; a flag could disagree with them.
- The entry list on the page is the engine's settled list, so the page and the engine agree on what a
  group is and what is inside one.
- Nested groups are flattened in the page. Finely balanced against one fold per level; flat wins on a
  phone, and a house with three-deep groups would change it.
- Brightness 0 on a light row is an off, sent and expected as one.
- The snapshot is unchanged. A per-light readout is stage 3, because it touches the board and the log
  and buys nothing for the first user of the feature.

## Verification for the implementation round

- Build with 0 warnings, 0 errors; run the suite and write the count beside the documented floor.
- The two safety properties above, each with its mutation shown to go red.
- Merge-rule tests: brightness with curve, brightness without curve, warmth alone, nothing stated;
  against a room row present and absent.
- Normaliser: empty row, empty light, empty list all disappear; a file with none of the key
  serialises byte for byte as before.
- Validator: each warning and error sentence exercised once.
- Older-build read: the current build's deserialiser given the new key loads the room with the key
  ignored; assert the room's rows survive and the light rows are absent, both directions.
- The room page at 1280×900 and 390×844, both themes, in three states: checkbox absent (one-lamp
  room), ticked with a group opened, and an orphan present. Screenshots through the UI host.
