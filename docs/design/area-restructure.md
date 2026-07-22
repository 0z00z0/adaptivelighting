# Area restructure — settings that live where you look for them

*Design document. No code in this change; implementation is broken into work packages in §7.*

The owner's verdict on the current UI is the whole brief: **"Everything seems a bit random."** Settings
have accreted into `Global`, `Defaults`, `Advanced` and per-zone overrides, and the sections are named
after *mechanisms* (where the value is stored, how it inherits) rather than after *things a person can
point at*. This document restructures the product around one principle, folds in the nine requested
changes, and ends with an ordered implementation plan.

---

## 1. What's wrong now

Concrete, from the actual screens:

- **`Defaults` and `Advanced` are the same kind of section twice.** Both are "settings that apply to
  every room", split by an invisible criterion: `Defaults` holds the seventeen values a zone can
  override, `Advanced` holds everything else in `GlobalConfig`. A person looking for "which lights get
  managed" cannot know that the exclude label is under *Advanced → Discovery conventions* while the
  outdoor lux sensor is under *Defaults → Darkness* — the outdoor sensor is not even a default, it is
  `Global.OutdoorLuxSensor` rendered inside the Defaults section (`ConfigEditor.razor` line ~400).
  The seam between the two sections already leaks.
- **"Zone" is wrong vocabulary.** In Home Assistant a *zone* is a GPS region ("Home", "Work") and an
  *area* is a room. The product's zones are exactly HA areas — `ZoneConfig.AreaId` is the only field a
  zone normally carries — so the one word we chose is the one word HA users already use for something
  else. Every label ("Zones", "Add a zone") actively misleads.
- **The dashboard is a flat list.** Seventeen `ZoneCard`s in registry order, no floors, no distinction
  between "the engine chose not to light this" and "the owner never wanted this room managed".
- **Enable/disable is buried.** `ZoneConfig.Enabled` exists but lives as row 1 of 17 inside a
  collapsed "Room-specific settings" fold inside a collapsed zone fold. Turning a room off is the
  single most common decision after auto-setup, and it is four clicks deep behind an
  inherit-or-override widget.
- **Free-text labels.** `Global.ExcludeLabel` and `Global.MotionLabel` are text inputs with a
  `datalist` hint. A typo silently disables the feature: the engine looks for a label nobody has.
- **Auto-setup runs once and can never run again.** `Global.ZonesAutoDiscovered` is a one-way flag by
  design (a good design — deliberately removed rooms must not grow back), but there is no *deliberate*
  way to re-run discovery after the house changes.
- **Auto-setup enables everything.** A fresh install starts commanding lights in every discovered
  room. The conservative light+motion test limits the damage, but "software I installed ten minutes
  ago is turning my bedroom lights on" is the wrong first experience. The owner wants the opposite:
  discovered rooms start off, the owner turns on the ones they trust it with.

---

## 2. The new information architecture

### The principle

> **A setting lives under the noun it changes.** One room → inside that room's editor. Every room →
> the "All rooms" group of the Areas section. The day's rhythm → Schedule. The house's modes → House
> modes. The installation itself (its name, its people, its master switch, its internals) → House.
> Within a section, rarely-touched knobs go behind a single *Advanced* fold **at the bottom of that
> section** — never into a separate top-level dumping ground.

The last clause is what kills `Advanced` as a page. "Advanced" answered *"how confident do I need to
be?"*; it never answered *"where do I look?"*. A person who wants the motion device classes is
thinking about *how rooms find their sensors*, so that setting belongs in Areas — merely folded,
because it is rarely touched.

### The settings sections

The `ConfigEditor` rail goes from **Zones · Periods · House modes · Defaults · Advanced** (five
sections, two of them grab-bags) to **four**:

| Section | Contents |
|---|---|
| **Areas** | The floor-grouped list of rooms (each with enable toggle, edge colour, editor); the *Set up rooms again* action; the **All rooms** group (today's `Defaults`, plus `Global.OutdoorLuxSensor`); the **Finding lights & sensors** group (labels include/exclude/motion; device classes behind its Advanced fold). |
| **Schedule** | Today's Periods (daylight chart + period editor), plus `SmoothTransitions`/`BlendMinutes` — blending across period boundaries is a property of the schedule and has no business under "Timing & override detection". |
| **House modes** | Unchanged. This section already moved out of Global and is the model to copy, not a problem to fix. |
| **House** | Name, people watched, away debounce, master switch; then one *Fine tuning* fold: NetDaemon user id, circadian tick, self-echo window, other-automations-count-as-manual, command tolerances. |

**Verdict on the owner's "General section within Areas" (requirement 4):** right instinct, one
refinement. Don't add a *General* group *next to* Defaults — **replace Defaults with it**. The
seventeen defaults are precisely "settings for every room that a room can override", so they *are* the
area-general settings; keeping a separate Defaults section alongside an Areas-General group would
recreate today's two-grab-bags problem with new names. The group is called **"All rooms"** (not
"General" — "General" says nothing; "All rooms" says exactly which noun it changes and telegraphs the
override relationship: a room's own settings beat *All rooms*). Settings that are *not* per-room
overridables but are still about rooms (the labels, the device classes, the outdoor lux sensor) join
the Areas section in their own named groups, so the answer to "where are room things?" is always
"Areas".

### The dashboard

- Master switch and house strip unchanged at the top.
- Rooms **grouped by floor** (see §4 for the degradation rules), **enabled rooms only** (requirement 3).
- A one-line footer under the grid when anything is hidden: *"4 rooms are switched off — turn them on
  in Settings → Areas."* Disabled rooms must not silently vanish; a person who disables the kitchen by
  accident needs a thread to pull.
- A dedicated first-run state when rooms exist but none are enabled (§4.1) — the empty page is
  designed, not accidental.

### The vocabulary

"Area" is the formal noun (types, YAML, section title, matching HA's own UI). "Room" is the word used
in prose and help text, as the copy already mostly does ("No rooms yet", "Remove this room"). This is
deliberate: HA's settings say *Areas*, humans say *rooms*, and the page can honour both — title-case
nouns for structure, plain words for sentences.

---

## 3. The settings mapping table

Every setting that exists today. "Fold" means inside that section's collapsed Advanced/Fine-tuning
fold. Labels are the user-facing text; where the current label is already good it is kept — churn in
copy users have learned is a cost, not a cleanup.

### 3.1 `GlobalConfig` (today: scattered across Advanced, Defaults, House modes)

| Setting (today) | New home | New label | Help text (where it earns its place) |
|---|---|---|---|
| `ConfigName` | House › Identity | **House name** | "A label for logs and notifications, so two houses can be told apart." |
| `Persons` | House › People | **Who lives here** | "Whose presence decides Home and Away. Empty means everyone Home Assistant knows." |
| `AwayDebounceMinutes` | House › People | **Count the house as empty after** | "How long everyone must be gone before rooms react to an empty house." |
| `KillSwitchEntity` | House › Master switch | **Master switch** | unchanged — current copy is good |
| `KillSwitchActiveWhenOff` | House › Master switch (shown only with a custom entity, as today) | **How to read the switch** | unchanged |
| `NetDaemonUserId` | House › fold | **NetDaemon user id** | unchanged (incl. the write-only rendering) |
| `CircadianTickSeconds` | House › fold | **Re-check the rooms every** | "How often each room re-checks the time of day and the light outside. Once a minute is plenty." |
| `SelfEchoWindowSeconds` | House › fold | **Recognise own changes for** | "How long the app's own commands are recognised as its own rather than as a person at a switch." |
| `TreatAutomationsAsManual` | House › fold | **Other automations count as manual** | unchanged |
| `BrightnessTolerancePct` | House › fold | **Close enough — brightness** | "A light already this close to its target is left alone." |
| `ColorTempToleranceKelvin` | House › fold | **Close enough — colour** | (shares the row and help above, as today) |
| `SmoothTransitions` | Schedule | **Blend between periods** | "Lights drift to the next period's level instead of stepping at the boundary." |
| `BlendMinutes` | Schedule | **Blend over** | (same row) |
| `HouseMode` | House modes | — | already moved; unchanged |
| `OutdoorLuxSensor` | Areas › All rooms › Darkness | **Outdoor light sensor** | current copy is good |
| `ExcludeLabel` | Areas › Finding lights & sensors | **Never touch (label)** | "Anything in Home Assistant carrying this label is invisible to this app." Becomes a label dropdown — §5. |
| **new** `IncludeLabel` | Areas › Finding lights & sensors | **Only manage lights with (label)** | "When set, only lights carrying this label are managed. Leave empty to manage every light that's found." §3.4. |
| `MotionLabel` | Areas › Finding lights & sensors | **Counts as motion (label)** | "A sensor with this label is treated as a motion sensor whatever its type." Label dropdown. |
| `MotionDeviceClasses` | Areas › Finding lights & sensors › fold | **What counts as a motion sensor** | unchanged (incl. the replaces-not-adds warning) |
| `IlluminanceDeviceClass` | Areas › Finding lights & sensors › fold | **What counts as a light-level sensor** | unchanged |
| `ZonesAutoDiscovered` → `AreasAutoDiscovered` | *(hidden, as today)* | — | never rendered |
| `KillSwitchIsDefaulted`, `EffectiveKillSwitchEntity`, `DefaultKillSwitchEntity`, `EffectiveMotionDeviceClasses` | *(computed, `[YamlIgnore]`)* | — | unchanged |

### 3.2 `ZoneSettings` → `AreaSettings` (today: the Defaults section)

All land in **Areas › All rooms**, in three groups mirroring today's folds. Per-room overrides mirror
the same labels, so the two places a setting appears use identical words.

| Setting | Group | New label | Help |
|---|---|---|---|
| `VacancyTimeoutSeconds` | Movement & timing | **Lights stay on for** | "After the last movement, how long the lights stay on before the warning dim. Longer for rooms where people sit still." |
| `PreOffSeconds` | Movement & timing | **Warning dim lasts** | "Before going out, lights dim for this long. Any movement brings them straight back." |
| `PreOffBrightnessFactor` | Movement & timing | **Warning dim level** | "How deep the warning dim is. 0.5 means half the current brightness." |
| `OverrideDurationMinutes` | Movement & timing | **Hand changes hold for** | "When someone adjusts a light by hand, their choice is left alone for this long." |
| `VacancyResetMinutes` | Movement & timing | **After a manual off, wait** | "After someone turns the lights off by hand, movement won't turn them back on until the room has been empty this long." |
| `Darkness` | Darkness | **How a room decides it's dark** | current dropdown copy is good |
| `LuxThreshold` | Darkness | **Dark below** | "At or below this many lux the room counts as dark." |
| `LuxHysteresis` | Darkness | *(shares the row, as today)* | unchanged |
| `SunElevationThreshold` | Darkness | **Dark when the sun is below** | unchanged help |
| `SunEntity` | Darkness › fold | **Sun entity** | unchanged ("normally exactly one — no reason to change this" is why it folds) |
| `DayTransitionSeconds` / `NightTransitionSeconds` | Darkness | **Fades** | unchanged |
| `RespectSleepMode` | Room behaviour | **Respects sleep mode** | unchanged |
| `SleepBlocksAutoOn` | Room behaviour | **Sleep blocks auto-on** | unchanged |
| `SkipAwaySweep` | Room behaviour | **Stays on when everyone leaves** | "Porch and security lights are wanted precisely when nobody's home." |
| `WelcomeHome` | Room behaviour | **Welcome home** | unchanged |
| `Enabled` | **removed from the All rooms UI** | — | The default stays in the model (old documents may rely on inheritance) but the UI stops offering it: enablement is now a per-room decision made on the room's header toggle, and a "default enabledness" knob next to seventeen toggles is a footgun — flipping it would silently flip every room that never wrote an explicit value. |

### 3.3 `ZoneConfig` → `AreaConfig` (per-room)

Unchanged in substance: `Name`, `AreaId`, explicit `Lights` / `MotionSensors` / `LuxSensor`,
`IgnoreWhenOn` ("Blocked while on"), and the nullable overrides — minus `Enabled`, which is promoted
from override-row-1-of-17 to the header toggle (§4.3). The override fold's count becomes "n of 16".

### 3.4 Include-label semantics (requirement 6)

- **Empty (`null`) means "manage every light discovery finds"** — the status quo, and what every
  existing document means by saying nothing. The filter is strictly opt-in.
- **Set, it filters `DiscoverLights` only.** Motion and lux sensors are inputs, not things the engine
  commands; "only manage lights I've blessed" is about actuation. Filtering sensors too would make a
  half-labelled house silently deaf.
- **Exclude always wins.** The include filter selects candidates; the exclude label then removes.
  Both labels on one light → not managed. This needs no precedence UI: "never touch" losing an
  argument to any other setting would betray its own name.
- **Explicit `Lights` lists bypass both labels**, exactly as they bypass discovery today
  (`ZoneEntityResolver.TryResolve` applies no label filter to explicit lists). An explicit pick is the
  owner overruling the rules; the rules don't get a veto.
- **A room whose lights are all filtered out fails to resolve with a message naming the label**:
  *"No lights in 'stue' carry the label 'adaptive'. Remove the include-label filter or label the
  lights in Home Assistant."* — same skipped-room mechanism as today, never a document error.
- **The no-labels house** (the common case) must not see a dead dropdown: see §5.

---

## 4. Screen-by-screen design

### 4.1 Dashboard

```
┌────────────────────────────────────────────────────────┐
│  Adaptive lighting: On                       [  On  ]  │   ← master switch, unchanged
├────────────────────────────────────────────────────────┤
│  House mode: Hjemme ▸ …        Who's home: E ✓  A ✓    │   ← house strip, unchanged
├────────────────────────────────────────────────────────┤
│  GROUND FLOOR                                          │
│  ┌──────────────┐ ┌──────────────┐ ┌──────────────┐    │
│  │▌ Stue        │ │▌ Kjøkken     │ │▌ Gang        │    │   ← ZoneCard → AreaCard,
│  │  auto · on   │ │  standing by │ │  set by hand │    │     unchanged internally
│  └──────────────┘ └──────────────┘ └──────────────┘    │
│  FIRST FLOOR                                           │
│  ┌──────────────┐ ┌──────────────┐                     │
│  │▌ Soverom     │ │▌ Bad         │                     │
│  └──────────────┘ └──────────────┘                     │
│                                                        │
│  4 rooms are switched off — turn them on in Settings.  │   ← link to /config (Areas)
└────────────────────────────────────────────────────────┘
```

- **Grouping:** floor headers ordered by `Floor.Level` (nulls last), then name; areas without a floor
  collect under a trailing **"Other rooms"** header. **When no area in the house has a floor, no
  headers render at all** — the grid is exactly today's flat grid. A one-floor-of-many house gets
  headers; a no-floors house never learns the feature exists. Never show "Other rooms" as the only
  group.
- **Enabled only:** a card renders only for areas whose effective `Enabled` is true. The engine still
  builds and publishes disabled areas (`ZoneState.Disabled` snapshots keep flowing — that behaviour is
  right and unchanged); the dashboard just declines to give them cards. The footer line carries the
  count and the link. When zero rooms are hidden, no footer.
- **First-run / all-disabled state** (the tension in the brief, designed explicitly):

```
┌────────────────────────────────────────────────────────┐
│  Set-up found 17 rooms. None are switched on yet,      │
│  so no lights will change.                             │
│                                                        │
│  Stue (3 lights) · Kjøkken (2) · Gang (1) · …          │   ← grey chips, counts from discovery
│                                                        │
│  [ Choose which rooms to switch on ]                   │   → /config (Areas section)
└────────────────────────────────────────────────────────┘
```

  Shown when the config has areas but none is enabled. It is not an error state and must not be
  styled as one — it says the system did its half of the job and is waiting for the owner's half.
  The existing "Connecting…" / "not running" empty states stay for their own conditions and take
  precedence (a broken connection must not be dressed up as onboarding).

- **The first ten minutes, end to end:** install → engine discovers 17 areas after the 30 s settle,
  writes them **disabled**, adopts the house-mode select, seeds the people list → the owner opens the
  dashboard, reads the banner, taps through to Areas → sees floors, taps *Switch on this floor* on the
  ground floor, toggles two bedrooms off the idea entirely, saves → dashboard now shows a live grid of
  exactly the rooms they chose. No lights changed until the owner said so; nothing needed typing.

### 4.2 Settings › Areas

```
Areas
  Rooms not listed here are never touched. Lights and sensors
  are found automatically from Home Assistant's areas.

  [ Add a room ]                        [ Set up rooms again ]

  GROUND FLOOR                     [ Switch on this floor ]
  ┌───────────────────────────────────────────────────────┐
  │ ▌ [●on] Stue        stue      all automatic        ▸  │   ← ▌ = edge colour
  │ ▌ [●on] Kjøkken     kjokken   2 of 16 changed      ▸  │
  │ ▌ [○off] Gang       gang      all automatic        ▸  │   ← edge grey when off
  └───────────────────────────────────────────────────────┘
  FIRST FLOOR                      [ Switch on this floor ]
  │ ▌ [○off] Soverom    soverom   respects sleep       ▸  │
  OTHER ROOMS
  │ ▌ [●on] Uteplass    uteplass  stays on when away   ▸  │

  ▸ All rooms — settings every room starts with
      (Movement & timing · Darkness · Room behaviour)
  ▸ Finding lights & sensors
      (Only manage lights with · Never touch · Counts as motion
       · Advanced: device classes)
```

- **Header row per area** (requirement 1): the enable **toggle** (a switch, not a checkbox — it is
  the room's power state, not a form field), the name, the area slug, the existing stray-from-default
  summary, the fold chevron. The **left edge colour** reuses the dashboard's family colour for that
  area's *live* state — blue (`--machine`) when the engine is acting, amber (`--human`) when a person
  overrode it, `--idle` grey otherwise, and **flat grey with the toggle off**. Settings and dashboard
  thereby speak one colour language; a person who learned "amber means someone touched it" on the
  dashboard reads the same fact here. Live state comes from `AreaSnapshotCache` (already a singleton
  the page can inject), matched by area id (§6.5); an area with no snapshot yet renders the idle edge.
- **The toggle writes an explicit value** (`Enabled = true` / `false`), never `null`. Inheritance
  stays for old documents; new decisions are always explicit. Toggling is an edit like any other — it
  arms the save bar, it does not save by itself. The save bar is already sticky-visible; no new
  mechanism.
- **Floor grouping** degrades exactly as the dashboard's (§4.1). *Switch on this floor* sets
  `Enabled = true` on every area in the group — an edit, not a save. With every area in a group
  already on, the link reads *Switch off this floor* (symmetric, and the only bulk operation offered;
  a matrix of bulk actions is not needed for ≤ 20 rooms).
- **Inside the fold**: unchanged from today's `ZoneEditor` — name, area picker, discovery preview,
  hand-picked entities, overrides — minus the Enabled override row.
- **Set up rooms again** (requirement 8): a button opening a **preview panel**, §4.4.
- **All rooms / Finding lights & sensors**: `GroupFold`s after the room list. After, not before —
  the room list is what people come for; the shared settings are reference material. Each fold's
  one-line description carries the override rule: *"Every room starts with these. A room's own
  settings win."*

### 4.3 Settings › area editor changes

Only deltas from today's `ZoneEditor`:

- Enabled leaves the override list; the header toggle owns it. Override count "n of 16".
- The collapsed summary keeps "all automatic / n of 16 changed" and drops the `disabled` badge —
  the grey edge and the off-toggle already say it twice.
- A per-room **"Set this room up again"** action joins "Remove this room" at the bottom, and runs the
  same preview flow (§4.4) scoped to one area.

### 4.4 Set up rooms again — a clear warning, then a clean rebuild

Owner decision: **warn clearly rather than preserve cleverly.** There is no merge logic and no
attempt to guess which edits were deliberate. Two paths, deliberately different:

**The clean-slate path (developer / deploy).** Clear the config (delete the file, or *Start from
scratch* on a broken one) and restart: the ordinary first-run discovery rebuilds everything from
nothing. Nothing is preserved because nothing exists to preserve — no new mechanism, no merge, and
this document adds no code for it beyond what §4.5 already changes about first-run behaviour.

**The UI button.** From the Areas section header (*Set up rooms again*) or a single room's editor
(*Set this room up again*):

1. **Pick rooms.** A checkbox list of the document's rooms (all pre-ticked from the section header,
   just the one from a room's editor). Newly qualifying areas — rooms in HA with a light and a motion
   sensor that the document doesn't have — are listed separately and are always included: adding a
   switched-off room can't hurt anything.
2. **The warning.** One dialog, concrete about consequences per room, before anything changes:

   > **Set up 3 rooms again?**
   >
   > Each room is rebuilt from what Home Assistant knows right now, as if it were newly found.
   > Its automatically-guessed behaviour is re-guessed; everything you changed by hand is lost:
   >
   > - **Stue** — loses 2 hand-picked lights and 3 changed settings
   > - **Kjøkken** — loses its custom name ("Kitchen & pantry") and 1 changed setting
   > - **Gang** — nothing to lose; will be re-guessed as an entrance
   >
   > 2 new rooms will be added, switched off: **Loftstue**, **Vaskerom**.
   >
   > Rooms stay switched on or off as they are now. Rooms you didn't tick are untouched.
   > Nothing is written until you press *Save and apply* — *Discard changes* undoes this.
   >
   > [ Set up again ]  [ Cancel ]

   The per-room lines are computed, not generic: hand-picked entity counts, changed-settings counts,
   and a custom name are the three things a rebuild destroys, so those are the three things the
   dialog counts. A room with nothing to lose says so — "are you sure?" without stakes is noise.
3. **The rebuild.** A ticked room is replaced by a fresh discovery proposal for its `AreaId`: role
   flags re-guessed from the area name, explicit entity lists gone, overrides gone, custom name gone.
   Exactly two things survive, and both are excluded because they are not discovery's output:
   `AreaId` (the room's identity) and `Enabled` (the owner's power switch — re-tagging lights must
   not silently switch a room off, or on). Areas that no longer qualify are reported in the dialog
   (*"Bod no longer has a motion sensor"*) but never removed — the room will surface as a skipped-room
   validation message, and removal stays the owner's explicit act.
4. **Commit.** The rebuild mutates the in-memory document and arms the ordinary save bar — it does
   not write. The dialog already said so; the save path validates and writes as it does for every
   other edit, and *Discard changes* remains the undo. No new write path — a design property
   `ConfigEditor` already documents as load-bearing.

The one-time-flag semantics change from "discovery may never run again" to "discovery never runs
*unbidden* again": the automatic run still happens once, on the flag, exactly as today; every later
run is a user action through this dialog.

### 4.5 First-run auto-setup changes (requirements 2 and 9)

In `RunZoneDiscovery` (moving to `AreaSetupService`, §6.4):

- Every proposed area gets `Enabled = false` — an explicit value, not a flipped default. Flipping
  `Defaults.Enabled` instead would retroactively disable every area in the two live houses whose
  documents never wrote an explicit `Enabled`. The default stays `true`; only *newly proposed* areas
  start off.
- `Global.Persons` is seeded with every `person.*` id **when the list is empty at first setup**.
  Trade-off, stated honestly: today's empty list means "everyone, forever, including people added
  next year"; an explicit list freezes membership. The requirement stands because visible beats
  magic — a non-technical owner should *see* who drives Home/Away and be able to remove the car
  tracker — but the People field keeps its escape hatch copy: *"Empty means everyone Home Assistant
  knows, including people added later."* Seeding happens only at first setup, never on re-runs
  (a deliberately emptied list must stay empty — same principle as the discovery flag).

---

## 5. Copy guide

**Voice** (codifying what the best current copy already does):

- *Room* in sentences, *Area* / *Areas* for structure and anything that must match HA's UI.
- Say what happens, not what the field is: "Lights stay on for", not "Vacancy timeout".
- Units in words where a unit would be jargon: "lux" survives (the sensor says lux), "hysteresis"
  does not — it is already glossed as "extra light to count as bright again"; keep the gloss, demote
  the word to the help text.
- Every destructive or surprising action states its consequence in the control itself, before the
  click: the re-setup warning dialog, the "replaces the whole set" device-class warning, the
  "off pauses this page too" master-switch note. That pattern is house style; keep it.
- No exclamation marks, no "simply", no "just" in help text.

**Specific rewrites** (the jargon-heavy stragglers; everything else in §3's tables):

| Today | Becomes | Where |
|---|---|---|
| "Zones" (rail, headers) | **Areas** | everywhere |
| "Add a zone" | **Add a room** | Areas |
| "Vacancy timeout" | **Lights stay on for** | All rooms + per-room |
| "Pre-off warning" / "Pre-off brightness" | **Warning dim lasts** / **Warning dim level** | 〃 |
| "Override holds for" | **Hand changes hold for** | 〃 |
| "Vacancy reset" | **After a manual off, wait** | 〃 |
| "Darkness source" | **How a room decides it's dark** | 〃 |
| "Lux threshold" | **Dark below** | 〃 |
| "Sun elevation" | **Dark when the sun is below** | 〃 |
| "Circadian tick" | **Re-check the rooms every** | House fold |
| "Self-echo window" | **Recognise own changes for** | House fold |
| "Skips the away sweep" | **Stays on when everyone leaves** | room behaviour |
| "Periods" | **Schedule** | rail; "period" survives inside the section — the schedule is made of periods |
| "Defaults" | **All rooms** | Areas group |
| "Advanced settings" | *(section removed; contents redistributed)* | — |
| "Discovery conventions" | **Finding lights & sensors** | Areas group |

**The empty label dropdown** (requirements 6–7, no-labels house): the three label fields become a
shared `LabelPicker` (§6.6). When `IHaRegistry.Labels` is empty the picker renders not as a bare
dropdown but as its none-state with instructions: *"You haven't created any labels in Home Assistant
yet. Labels are made under Settings → Areas, labels & zones → Labels — create one there, then pick it
here."* A configured value that matches no live label renders selected with a gentle warning row
(*"no entity carries this label yet"*), never dropped: a stored value must survive HA being briefly
unreadable, the same rule the entity pickers already follow. `ExcludeLabel`'s shipped default
`adaptive-exclude` will trip this warning in most houses; the warning copy for the exclude field
therefore reads as information, not alarm: *"Nothing carries this label, so nothing is excluded —
that's fine."*

---

## 6. Code design

### 6.1 The rename (requirement 5)

**Scope.** Types and members, engine and web and tests, plus UI copy and docs. The namespace stays
`AdaptiveLighting.*`; the YAML root key stays `AdaptiveLighting.Configuration.AdaptiveLightingConfig`
(the class keeps its name, so the key is untouched — no migration needed there).

| Today | Becomes |
|---|---|
| `ZoneConfig` | `AreaConfig` |
| `ZoneSettings` | `AreaSettings` |
| `AdaptiveLightingConfig.Zones` | `.Areas` |
| `GlobalConfig.ZonesAutoDiscovered` | `.AreasAutoDiscovered` |
| `ZoneEntityResolver`, `ResolvedZone`, `ZonePreview` | `AreaEntityResolver`, `ResolvedArea`, `AreaPreview` |
| `ZoneAutoDiscovery` | `AreaAutoDiscovery` |
| `ZoneController`, `ZoneState` | `AreaController`, `AreaState` |
| `ZoneError` (ValidationResult) | `AreaError` |
| `ZoneSnapshot`, `ZoneSnapshotCache`, `ZoneSnapshotEvent` | `AreaSnapshot`, `AreaSnapshotCache`, `AreaSnapshotEvent` |
| `ZoneCard.razor`, `ZoneEditor.razor` | `AreaCard.razor`, `AreaEditor.razor` |
| CSS `.zone*` classes | `.area*` |

`AreaDiscovery`, `AreaOption`, `AreaEntities`, `IAreaRegistry` already use the right word and are
untouched — the rename *converges* on them rather than colliding: after it, "Area" means one thing
everywhere, which is the point.

**Config-file migration — silent, automatic, confined to the deserialiser.** Owner decision: an old
document migrates to the new schema on first start, silently — no prompt, no shim types, no old
names in the API. Two live houses have documents saying `Zones:` (and `ZonesAutoDiscovered:`), and
`LightingConfigDocument.Deserialize` uses `IgnoreUnmatchedProperties`, so a naive rename would
*silently* load zero areas — the worst possible failure. The design:

- **Reading.** `Deserialize` gains a key-translation pre-pass: the YAML is first read into a generic
  node tree, the legacy keys are renamed in place (`Zones` → `Areas`,
  `ZonesAutoDiscovered` → `AreasAutoDiscovered`, matched case-insensitively like everything else the
  binder inherited), and the result binds to the clean model. The model itself carries **no** legacy
  member — the old names survive in exactly one place, a
  `private static readonly Dictionary<string, string> LegacyKeys` inside `LightingConfigDocument`,
  with a comment saying why it must never be removed: deleting it would make pre-2.0 files load as
  zero areas without an error. If a hand-edited document somehow carries both an old and a new key,
  the **new key wins and the old is dropped**, and the load logs a warning naming both — the file
  said two things, the reader must pick one, and the one the current schema names is the one a
  current editor produced.
- **Migrating write.** `Deserialize` reports whether the pre-pass fired (a
  `DocumentReadResult(AdaptiveLightingConfig Config, bool UsedLegacyKeys)` return — explicit, not an
  out-param). On `Reload`, when the loaded document used legacy keys and the store is writable, the
  host immediately saves it back: the file on disk is rewritten in the new schema on the first start
  after the upgrade, before anything else happens, with one log line saying so. **A backup is
  written** — this is a commitment, not an option: the migrating save goes through
  `LightingConfigStore.Save`, which already keeps the previous file at `BackupPath`, so the last
  pre-migration document survives at the path the Configuration page already shows. No new backup
  mechanism; the existing one is the guarantee.
- **After migration.** A hand edit that reintroduces `Zones:` keeps loading forever (the pre-pass is
  unconditional) and is silently re-migrated on the next start or save. A "half-migrated" file —
  both keys, or old keys under a new root — is just a file the pre-pass normalises; there is no
  state the reader refuses. The strictness lives entirely on the write side: `Serialize` emits only
  the new schema, always.

**Wire contract.** The HA event `laget_lighting_zone` and its `zone` field follow the same owner
decision: clean break, no dual publishing. The event becomes `adaptive_lighting_area` with an `area`
field (and the new `area_id`, §6.5) in 2.0.0, and the release notes name it as the one change that
can break things outside this repo — an HA automation or dashboard listening for the old event name
must be updated by hand. Engine and web UI ship together, so the bundled dashboard never sees a
mismatch.

**Package staging.** The rename is a compile-breaking change to the published NuGet package's public
API → **major version (2.0.0)**, shipped clean: no `[Obsolete]` twins, no aliases, no old member
names anywhere in the API. Code consumers recompile against the new names; data files migrate
themselves per the above. The monorepo's diverged copy adopts the same rename on its own schedule —
the deserialiser pre-pass makes the order irrelevant.

**One change or several?** The rename ships as **one work package, alone** — nothing functional rides
along. A mechanical rename with 387 green tests before and after is easy to trust and easy to review;
a rename braided into feature work is neither. Everything else in this document builds on the new
names (§7 ordering).

### 6.2 Floors through the seam

`IAreaRegistry` exists because HassModel's `Area` (and equally `Floor`) cannot be constructed in
tests; floor support goes through it, not around it:

```csharp
	/// <summary>A floor as the engine and UI need it: identity, display name, and stacking order.</summary>
	/// <param name="Id">The registry floor id.</param>
	/// <param name="Name">The display name ("Ground floor", "Loftet").</param>
	/// <param name="Level">HA's level number, used only for ordering. Null when the house never set one.</param>
	public sealed record AreaFloor(string Id, string Name, int? Level);

	public interface IAreaRegistry
	{
		// ... existing four members unchanged ...

		/// <summary>The floor <paramref name="areaId"/> sits on, or null — floors are optional in HA.</summary>
		AreaFloor? FloorOf(string areaId);
	}
```

`HaAreaRegistry` implements it via `IHaRegistry.GetArea(areaId)?.Floor`, mapped through a new
`RegistryExtensions.FloorOf` (one expression, same pattern as the existing four). `FakeAreaRegistry`
gains a `Dictionary<string, AreaFloor>` — which is precisely why the record exists: tests can now
build floored houses.

Grouping itself is a small pure helper the two screens share, so they can never disagree:

```csharp
	/// <summary>One floor's rooms, ordered for display. Shared by the dashboard and the Areas section.</summary>
	public sealed record FloorGroup<T>(AreaFloor? Floor, IReadOnlyList<T> Items);
```

with a static `FloorGrouping.Group<T>(items, areaIdOf, registry)` in `AdaptiveLighting.Web.Services`:
order by `Level ?? int.MaxValue` then name; floorless items last under `Floor: null`; **if every item
is floorless, return a single unnamed group** — the renderers show headers only when `Count > 1 ||
Floor is not null`, which encodes the degradation rule (§4.1) once.

### 6.3 Include label

`GlobalConfig` gains:

```csharp
	/// <summary>
	///     Registry label a light must carry to be managed. Null — the default and the meaning of every
	///     pre-existing document — manages every light discovery finds. Applied to light discovery only:
	///     sensors are inputs, not things the engine commands, and filtering them too would make a
	///     half-labelled house silently deaf. The exclude label always wins over this one.
	/// </summary>
	public string? IncludeLabel { get; set; }
```

`AreaEntityResolver.DiscoverLights` adds one filter line after `IsExcluded`;
`HaCatalog.SignatureOf` adds the field (the discovery cache must invalidate when it changes);
`ConfigValidator` adds a *warning* when the label is set but no entity in HA carries it (fails open —
a typo'd include label stops rooms resolving, and each room already says why; the warning names the
root cause at document level).

### 6.4 `AreaSetupService`

First-run discovery currently lives inside `LightingEngineHost.RunZoneDiscovery`. It moves to a new
engine-side class so first run and re-run are the same code observed twice:

```csharp
	/// <summary>What one setup run will do, itemised so the warning dialog can be concrete about losses.</summary>
	public sealed record SetupPlan(
		IReadOnlyList<AreaConfig> NewAreas,
		IReadOnlyList<AreaRebuildPlan> Rebuilds,
		IReadOnlyList<string> NoLongerQualifying);

	/// <summary>
	///     One existing area's rebuild. The three counts are the three things a rebuild destroys —
	///     hand-picked entities, changed settings, a custom name — which is exactly what the dialog lists.
	/// </summary>
	public sealed record AreaRebuildPlan(string AreaId, int PinnedEntityCount, int OverrideCount, bool HasCustomName);
```

`AreaSetupService.Plan(config, registry, resolver, scope)` is pure (registry in, plan out — fully
testable with the fakes); `Apply(config, plan)` mutates the document per §4.4: ticked areas replaced
by fresh proposals keeping only `AreaId` and `Enabled`, new areas appended switched off. There is
deliberately no merge parameter and no preserve-list — the owner chose a warning over cleverness, and
a service with one behaviour is a service whose warning is always true.
`LightingEngineHost.RunZoneDiscovery` shrinks to: load → `Plan` → `Apply` → seed persons → set flag →
save; the web UI calls `Plan`, renders the §4.4 dialog from it, calls `Apply` on confirm, and hands
the mutated document to the ordinary save bar. New areas carry `Enabled = false` from
`AreaAutoDiscovery.Propose` itself (one line), so *every* path that proposes areas proposes them off.

### 6.5 Snapshots carry the area id

`AreaSnapshot` and the published event gain `AreaId` (`area_id` on the wire — additive, old
consumers unaffected; absent on events from old builds, deserialising to `null`). The dashboard and
the settings edge-colour both key on it, falling back to display-name match when null. This also
retires the name-keyed cache lookup as the only join between config and live state — names are
editable mid-session; ids are not.

### 6.6 `LabelPicker` component

One new component, used three times (include, exclude, motion): options from a new
`HaCatalog.LabelOptions()` (`record LabelOption(string Id, string Name)` over `IHaRegistry.Labels`),
a none-option whose text the caller supplies, the §5 empty-state and unknown-value behaviours.
**Stores the label name**, not the id: `LabelsOf` matches both, names are what the YAML-reading human
recognises, and the shipped default `adaptive-exclude` is already a name.

### 6.7 What deliberately does not change

- `HouseModeConfig` / `HouseModeAutoDetect` / the House modes section — already coherent.
- The engine's behavioural core (`AreaController` née `ZoneController`, orchestrator, monitors) —
  this is an information-architecture change; the state machine is not on the table.
- The save pipeline (`LightingEngineHost.Save` → validate → write → re-read → rebuild) — every new
  feature routes through it precisely so it can stay the only write path.
- Disabled areas keep being observed and published; only their *rendering* changes.

---

## 7. Implementation plan

Ordered; each package builds and tests green on its own. "Needs" states hard dependencies for
parallel agents. The suite is 387 tests today; each package states what it adds.

**WP1 — The rename and the silent migration** *(needs: nothing; blocks: all others)*
Engine, web, tests, CSS, UI copy: the §6.1 table, clean — no legacy names in the API. The
deserialiser key pre-pass and `DocumentReadResult`; the migrate-on-first-load save in
`LightingEngineHost.Reload` (through the store, so the existing backup covers it). Event renamed to
`adaptive_lighting_area` / `area` field, hard cut; `AreaSnapshotCache` subscribes to the new name.
*Tests:* old-key YAML loads with areas populated and flag honoured, and reports `UsedLegacyKeys`;
serialise emits `Areas:` and never `Zones:`; both-keys document → new key wins, warning logged;
full old-document → load → save → load round-trip lands on the new schema; a legacy-key load through
the host rewrites the file once and leaves the previous bytes at `BackupPath`; a new-schema load
rewrites nothing. Everything else is the existing 387 passing under new names.

**WP2 — Floors and labels through the seam** *(needs: WP1)*
`AreaFloor`, `IAreaRegistry.FloorOf`, `RegistryExtensions.FloorOf`, fakes; `FloorGrouping` +
`FloorGroup<T>`; `HaCatalog.LabelOptions()`.
*Tests:* `FloorOf` null for floorless areas; grouping orders by level-then-name, floorless last;
all-floorless collapses to one unnamed group; `LabelOptions` empty when HA has no labels.

**WP3 — Include label** *(needs: WP1)*
`GlobalConfig.IncludeLabel`; resolver filter; `SignatureOf`; validator warning.
*Tests:* null manages all (existing docs unchanged — assert on a doc with no key); set filters
discovery; include+exclude on one light → excluded; explicit `Lights` bypasses; all-filtered room
skips with the label named in the error; unknown label → document warning, not error.

**WP4 — `AreaSetupService`** *(needs: WP1; WP3 only for its label-aware plans)*
Extract discovery from the host; `SetupPlan`/`Apply`; `Enabled = false` in `Propose`; person seeding.
*Tests:* proposed areas disabled; first run seeds persons only when empty; a rebuild replaces a
ticked area with a fresh proposal keeping exactly `AreaId` and `Enabled` — name, pinned entities and
overrides gone, roles re-guessed; the plan's counts match what the rebuild then destroys (the dialog
must never under-warn); un-ticked areas byte-identical; deliberately emptied person list not
re-seeded; no-longer-qualifying reported, not removed; host's automatic run behaves exactly as
before minus enablement (existing `AutoConfigureTests` updated, not deleted).

**WP5 — Snapshot area id** *(needs: WP1)*
`AreaId` on snapshot + event; cache keyed by id with name fallback.
*Tests:* round-trip through `AreaSnapshotEvent`; old event without `area_id` still yields a snapshot.

**WP6 — Settings restructure** *(needs: WP1–WP4; WP5 for edge colours)*
`ConfigEditor` four-section rail (§2); floor-grouped `AreaEditor` list with header toggle, edge
colour, per-floor bulk enable; **All rooms** and **Finding lights & sensors** groups; `LabelPicker`
×3; Schedule gains blending; House section + Fine-tuning fold; Enabled override row removed ("n of
16"); the §4.4 re-setup warning dialog wired to `AreaSetupService.Plan`/`Apply` and the save bar.
*Tests:* component-level where the repo tests components (`PeriodStartTextTests`-style pure helpers:
bulk-enable mutation, edge-colour class selection, override count = 16); the rest is the §4 spec
against manual review — this repo has no Razor render-test harness and this design does not introduce
one.

**WP7 — Dashboard** *(needs: WP1, WP2, WP5)*
Floor groups via `FloorGrouping`; enabled-only cards; switched-off footer; §4.1 onboarding state.
*Tests:* pure helpers — visible-card filtering by effective `Enabled`, hidden count, onboarding-state
predicate (areas exist ∧ none enabled ∧ engine attached).

**WP8 — Copy and docs sweep** *(needs: WP6, WP7)*
§5 rewrites; `docs/adaptive-lighting/*.md`, `example-config.md`, README, the Astro site under
`website/`; release notes for 2.0.0 naming the YAML self-migration, the event rename window, and the
new-install "rooms start off" behaviour.
*Tests:* none; review against the §3 tables.

---

## 8. Decisions taken, and what remains open

Decided by the owner and folded in (not open):

- **Clean rename, breaking change accepted** — no API shims, no old names in code (§6.1).
- **Silent config migration on first start** — old schema loads via the deserialiser's key pre-pass,
  is rewritten in the new schema immediately, previous file kept at `BackupPath` (§6.1).
- **Re-setup warns, never merges** — the deploy path is a clean slate by construction; the UI button
  shows a concrete per-room warning and then rebuilds cleanly (§4.4).

Still open — one item:

1. **`laget_lighting_*` prefix**: the state publisher's other names carry the same legacy prefix as
   the renamed event. Renaming them is the same wire-contract question at larger scope; deliberately
   left out of this design, flagged for whenever the publisher is next touched. (Person seeding, §4.5,
   is treated as decided by requirement 9; the freeze trade-off is documented there, not reopened.)
