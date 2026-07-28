---
title: "Configuration"
description: "The configuration document, layer by layer: Global, Defaults, Periods and Areas."
---

## 1. What exists today (read and understood)

- `House/apps/Configuration/ConfigurationReader.cs` — `[NetDaemonApp]` that injects
  `IAppConfig<LightConfig>` and posts a persistent notification. Proof the YAML binder works.
- `House/apps/Configuration/Model/` — `LightConfig` (`ConfigName`, `Areas`, `Lights`),
  `AreaMapping`/`LightMapping : MappingList`, `Mapping` (`MappingKey`, `Timeout`, `IgnoreEntity`,
  `UseIlluminance`, `Illuminance`), `IlluminanceMapping` (`Illuminancelevel` — note it is a
  *string* and singular while the YAML supplies a *list* with `IluminanceLevel` [sic]),
  `LightValue` (RGB/WW/CW levels). The YAML (`LightConfiguratoin.yaml`, misspelled, still loads
  because binding is by top-level class-name key, not filename) and the model have drifted:
  `IngoreEntity` [sic] in YAML vs `IgnoreEntity` in C#, list-vs-scalar illuminance — silent
  binding losses today. Cabin has an older inline copy of the same idea in its
  `ConfigurationReader.cs`.
- Binding rules (verified in docs + AppModel source): top-level YAML key = fully qualified config
  class name; `snake_case`/`PascalCase` automap; entity-typed properties bind from an id string.

The old files stay untouched (their reader apps get disabled, 02 §9). The new schema is a clean
break — different class names, different YAML file.

## 2. Multi-instance: the verified answer

**The current AppModel instantiates exactly one instance per `[NetDaemonApp]` class** (verified,
`AppModelContext.cs`; the instancing docs page shows only `IAppConfig` injection). "One app
instance per area" via YAML blocks is **not a supported mechanism** — the prompt's assumption and
the `netdaemon` skill are both wrong on this point. Design consequence: **one
`AdaptiveLightingConfig` document per host containing a list of areas**; the single orchestrator
fans out to per-area controllers internally. This keeps per-room isolation (a bad room is skipped,
see §5) while sharing presence/mode/registry state.

## 3. The schema (namespace `AdaptiveLighting.Configuration`)

Defaults-plus-overrides: `Defaults` holds every per-room knob; each `AreaConfig` overrides only
what differs (nullable properties; `AreaConfig.Effective(defaults)` merges — a pure, unit-tested
function). The settings page calls the `Defaults` group **All rooms**, which is what it is.

```csharp
public class AdaptiveLightingConfig
{
	public string? ConfigName { get; set; }
	public GlobalConfig Global { get; set; } = new();
	public AreaSettings Defaults { get; set; } = new();
	public List<TimePeriodConfig> Periods { get; set; } = [];   // house-wide circadian table
	public List<AreaConfig> Areas { get; set; } = [];
}

public class GlobalConfig
{
	public List<string> Persons { get; set; } = [];             // person.* / device_tracker.*; empty => discover all person.*
	public string? KillSwitchEntity { get; set; }               // input_boolean/switch; null => this app's own enable switch
	public bool KillSwitchActiveWhenOff { get; set; } = true;   // true: state "off" muzzles the engine
	public HouseModeConfig? HouseMode { get; set; }             // the mode select and its option kinds; null => no modes
	public string? OutdoorLuxSensor { get; set; }               // read by the rooms that set FollowOutdoorLux; never automatic
	public bool AreasAutoDiscovered { get; set; }               // set once by first-run set-up; never reset
	public string? NetDaemonUserId { get; set; }                // optional: HA user id of the ND token, for override detection
	public int AwayDebounceMinutes { get; set; } = 5;
	public int CircadianTickSeconds { get; set; } = 60;
	public int SelfEchoWindowSeconds { get; set; } = 8;
	public bool TreatAutomationsAsManual { get; set; } = true;
	public bool SmoothTransitions { get; set; } = true;
	public int BlendMinutes { get; set; } = 30;
	public string ExcludeLabel { get; set; } = "adaptive-exclude";
	public string? IncludeLabel { get; set; }                   // null => manage every light discovery finds
	public string MotionLabel { get; set; } = "adaptive-motion";
	public List<string> MotionDeviceClasses { get; set; } = []; // empty => motion, occupancy, presence
	public string IlluminanceDeviceClass { get; set; } = "illuminance";
	public double BrightnessTolerancePct { get; set; } = 2;
	public int ColorTempToleranceKelvin { get; set; } = 50;
}

public class AreaSettings                                       // every knob a room can override
{
	public int VacancyTimeoutSeconds { get; set; } = 600;
	public int PreOffSeconds { get; set; } = 30;
	public double PreOffBrightnessFactor { get; set; } = 0.5;
	public int OverrideDurationMinutes { get; set; } = 120;
	public int VacancyResetMinutes { get; set; } = 10;          // lifts SuppressedOff
	public DarknessSource Darkness { get; set; } = DarknessSource.Either; // Lux|Sun|Either|Always
	public double LuxThreshold { get; set; } = 1000;            // a daylight number: the reading is usually an outdoor sensor
	public double LuxHysteresis { get; set; } = 10;
	public double SunElevationThreshold { get; set; } = 3.0;    // degrees
	public string SunEntity { get; set; } = "sun.sun";
	public double DayTransitionSeconds { get; set; } = 1;
	public double NightTransitionSeconds { get; set; } = 15;
	public bool RespectSleepMode { get; set; } = false;
	public bool SleepBlocksAutoOn { get; set; } = false;
	public bool SkipAwaySweep { get; set; } = false;
	public bool WelcomeHome { get; set; } = false;
	public bool Enabled { get; set; } = true;
}

public class AreaConfig                                          // AreaSettings overrides: all nullable
{
	public string? Name { get; set; }                            // display; defaults to AreaId
	public string? AreaId { get; set; }                          // HA area id => registry discovery
	public List<string>? Lights { get; set; }                    // explicit override; wins over discovery
	public List<string>? MotionSensors { get; set; }             // explicit override; wins over discovery
	public string? LuxSensor { get; set; }                       // explicit override; one sensor, no average
	public bool? FollowOutdoorLux { get; set; }                  // read Global.OutdoorLuxSensor when the room finds none of its own
	public List<string>? IgnoreWhenOn { get; set; }              // e.g. binary_sensor.projektor_er_pa: block auto-on while on
	// nullable twins of every AreaSettings property:
	public int? VacancyTimeoutSeconds { get; set; }
	public int? PreOffSeconds { get; set; }
	/* … one nullable property per AreaSettings knob … */
	public bool? Enabled { get; set; }                           // the room's power switch, written explicitly
}

public class TimePeriodConfig
{
	public string Name { get; set; } = "";                       // "morning" | "day" | "evening" | "night" — free-form
	public string Start { get; set; } = "";                      // "06:30" | "sunrise" | "sunset" | "sunrise+00:45" | "sunset-01:00"
	public double BrightnessPct { get; set; } = 80;
	public int ColorTempKelvin { get; set; } = 3500;
	public double? MaxBrightnessPct { get; set; }                // night-light ceiling (the 03:00 rule)
	public double? MinBrightnessPct { get; set; }
}
```

Binding was originally to be **`IAppConfig<AdaptiveLightingConfig>`** (established repo pattern; free
typed binding; zero new packages). It is not, any more: `LightingConfigDocument` reads and writes the
document and is the only loader, because the UI has to serialise and two parsers disagreeing about one
file is the bug you find at 03:00. See [the web UI](/web-ui/) §7. The top-level key stays the fully
qualified class name regardless.

### Reading a pre-2.0 document

Files written before 2.0 say `Zones:` and `ZonesAutoDiscovered:`. They still load: the deserialiser
renames those two keys before binding, and the engine writes the file back in the new schema on the
first start after the upgrade, keeping the previous file at the store's backup path. Nothing needs
doing by hand, and a file that is hand-edited back to the old names keeps working. Writing is strict
in one direction only: the serialiser emits `Areas:`, always.

## 4. Registry vs hand-written YAML — evaluation and recommendation

`IHaRegistry` surface (verified from source): `Areas`/`Floors`/`Labels`/`Devices`/`Entities`,
`Area.Entities` (direct + via device), `Area.Floor`, `Label.Entities`,
`EntityRegistration.Area/Device/Labels/Platform/Options`. Device class (motion vs door, lux vs
temperature) is **not** in the registry — it lives in state attributes
(`GetState(id).Attributes["device_class"]`), which `IHaContext` provides.

**Recommendation: hybrid, discovery-first — this matches the user's own flagged direction.**

- An area declares `AreaId` and nothing else in the common case. `AreaEntityResolver` then:
  - lights = `area.Entities` where domain `light`, minus entities labelled `adaptive-exclude` — and,
    when `IncludeLabel` is set, minus every light that does *not* carry it;
  - motion = `binary_sensor` in area with `device_class ∈ {motion, occupancy, presence}`,
    plus any entity labelled `adaptive-motion` (covers mmWave sensors with odd device classes);
  - lux = every `sensor` in area with `device_class == illuminance`. Several are averaged
    geometrically at read time, dead and stale ones dropped; an explicit `LuxSensor` pins one.
- Two de-duplication passes then run over **all three** lists, in this order:
  - **Groups win over their members.** Membership is followed transitively (`entity_id`), a group
    reaching into another HA area is clipped, overlapping groups are settled by widest coverage, and
    a group that contains itself terminates rather than hanging.
  - **One entity per Home Assistant device** (lights and illuminance only). Several entities on one
    device are one fixture — an RGBW lamp's combined entity beside its own colour channels — so only
    one is used, and a group, which has no device, claims the devices of everything beneath it.
    Motion is deliberately exempt: a device there is a *controller*, and a multi-zone presence sensor
    exposes genuinely different zones, so collapsing them would make the room blind.
- Explicit YAML lists (`Lights`, `MotionSensors`, `LuxSensor`) fully replace discovery for that
  slot when present — the escape hatch when HA's area assignments are wrong. An explicit list
  bypasses both labels: an explicit pick is the owner overruling the rules, and the rules do not
  get a veto.
- **Rooms are opt-in** (only areas listed under `Areas:` are managed, and only those switched on
  are commanded). Auto-managing every HA area is rejected: surprise coverage of `Garasje` at 02:00
  is how trust in the system dies. First-run set-up proposes rooms, switched off, and the owner
  chooses.
- Labels used: `adaptive-exclude` (never touch this entity), `adaptive-motion` (treat as motion
  source), and an optional include label (manage only lights carrying it — empty means every light
  found). Exclude always wins over include: a light carrying both is not managed. Labels are read
  at startup (registry snapshot); a restart or config reload picks up changes.

Why not pure-registry (no YAML at all): thresholds, timeouts, periods and mode entities have no
home in the registry; and area membership in HA is one shared taxonomy that other automations
also depend on — bending it to encode lighting policy (e.g. splitting areas to get two rooms)
would be the tail wagging the dog. Why not pure-YAML: hand-listing every light re-creates today's
drift problem (`IngoreEntity`…) and breaks silently when entities are renamed — discovery keeps
the config document small enough for humans to keep truthful.

## 5. Validation — fail loudly, degrade per room

Policy:

- **Document-level errors**: empty `Periods`, unparseable `Start`, overlapping/duplicate period
  names, duplicate room names, negative timeouts, thresholds out of range,
  `PreOffSeconds >= VacancyTimeoutSeconds`, unknown darkness source. An HA persistent notification
  is posted listing every problem, and the engine does not start commanding. It deliberately does
  *not* throw: a throw would dispose the app's DI scope and take the web UI's connection with it, and
  the UI is the one thing that can fix the file. See [the web UI](/web-ui/) §7.
- **Room-level referential errors ⇒ degrade**: `AreaId` not in registry, explicit entity id not
  in `GetAllEntities()`, no lights resolved, no motion sensors resolved, ambiguous lux sensor —
  skip that room, log Error, aggregate all skipped rooms into ONE persistent notification
  ("Adaptive lighting: 2 of 9 rooms disabled — …"). Rationale: an entity renamed in HA must not
  black out the whole house's automation.
- **Warnings** sit between the two: an include label no entity carries, for instance, is reported at
  document level and fails open, because the rooms it affects already say why they resolved nothing.
- `ConfigValidator.Validate(config)` returns `ValidationResult { Errors[], AreaErrors[] }`; it is
  pure (registry/entity checks take an `IReadOnlyCollection<string> knownEntityIds` + area-id
  list, so tests need no fakes).

## 6. Complete example YAML — `apps/AdaptiveLighting/AdaptiveLighting.yaml`

```yaml
AdaptiveLighting.Configuration.AdaptiveLightingConfig:
  ConfigName: "Adaptive lighting [House]"

  Global:
    Persons:
      - person.espen
    KillSwitchEntity: input_boolean.adaptive_lighting_enabled   # note: engine treats OFF as kill
    NetDaemonUserId: ""            # optional; fill with the ND token's HA user id if known
    AwayDebounceMinutes: 5
    TreatAutomationsAsManual: true

  Defaults:
    VacancyTimeoutSeconds: 600
    PreOffSeconds: 30
    PreOffBrightnessFactor: 0.5
    OverrideDurationMinutes: 120
    VacancyResetMinutes: 10
    Darkness: Either
    LuxThreshold: 1000
    LuxHysteresis: 10
    SunElevationThreshold: 3.0
    DayTransitionSeconds: 1
    NightTransitionSeconds: 15

  Periods:
    - Name: morning
      Start: "06:00"
      BrightnessPct: 60
      ColorTempKelvin: 3000
    - Name: day
      Start: sunrise+00:45
      BrightnessPct: 90
      ColorTempKelvin: 4500
    - Name: evening
      Start: sunset-01:00
      BrightnessPct: 70
      ColorTempKelvin: 2700
    - Name: night
      Start: "22:30"
      BrightnessPct: 15
      ColorTempKelvin: 2200
      MaxBrightnessPct: 30        # the 03:00 rule

  Areas:
    - Name: Stue
      AreaId: stue
      Enabled: true               # the room's power switch, written explicitly by the UI
      RespectSleepMode: true
    - Name: Kjeller multimedia
      AreaId: kjeller_multimedia
      Enabled: true
      IgnoreWhenOn:
        - binary_sensor.projektor_er_pa
      VacancyTimeoutSeconds: 1800
      Darkness: Always            # basement: no daylight gate
    - Name: Gang
      AreaId: gang
      Enabled: true
      WelcomeHome: true
      VacancyTimeoutSeconds: 120
    - Name: Soverom
      AreaId: soverom
      Enabled: true
      RespectSleepMode: true
      SleepBlocksAutoOn: true
      MotionSensors:              # explicit override example (mmWave not in area)
        - binary_sensor.soverom_mmwave_presence
    - Name: Ute
      AreaId: ute
      Enabled: false              # found by set-up, not switched on yet
      SkipAwaySweep: true
      Darkness: Sun
```

Notes: `AreaId` is the HA registry **area id** (slug), not the display name; the resolver uses
`IHaRegistry.GetArea(id)` and says so in the validation message when an id misses, listing every
known area id.

The entity ids above are illustrative. Nobody has to type them: a fresh installation writes no ids at
all and lets first-run set-up find the rooms, and the configuration UI's pickers offer the real
entities by name. Hand-editing this file is the escape hatch, not the route in.
