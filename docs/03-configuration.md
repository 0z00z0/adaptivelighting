# 03 — Configuration design

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
`AdaptiveLightingConfig` document per host containing a list of zones**; the single orchestrator
fans out to per-zone controllers internally. This keeps per-zone isolation (a bad zone is skipped,
see §5) while sharing presence/mode/registry state.

## 3. New schema (namespace `AdaptiveLighting.Configuration`)

Defaults-plus-overrides: `Defaults` holds every per-zone knob; each `ZoneConfig` overrides only
what differs (nullable properties; `ZoneConfig.Effective(defaults)` merges — a pure, unit-tested
function).

```csharp
public class AdaptiveLightingConfig
{
	public string? ConfigName { get; set; }
	public GlobalConfig Global { get; set; } = new();
	public ZoneSettings Defaults { get; set; } = new();
	public List<TimePeriodConfig> Periods { get; set; } = [];   // house-wide circadian table
	public List<ZoneConfig> Zones { get; set; } = [];
}

public class GlobalConfig
{
	public List<string> Persons { get; set; } = [];             // person.* / device_tracker.*; empty => discover all person.*
	public string? KillSwitchEntity { get; set; }               // input_boolean/switch; null => feature off
	public string? SleepModeEntity { get; set; }
	public string? GuestModeEntity { get; set; }                // reserved (v2 behavior), parsed now
	public string? NetDaemonUserId { get; set; }                // optional: HA user id of the ND token, for override detection
	public int AwayDebounceMinutes { get; set; } = 5;
	public int CircadianTickSeconds { get; set; } = 60;
	public int SelfEchoWindowSeconds { get; set; } = 8;
	public bool TreatAutomationsAsManual { get; set; } = true;
	public bool SmoothTransitions { get; set; } = true;
	public int BlendMinutes { get; set; } = 30;
}

public class ZoneSettings                                       // every knob a zone can override
{
	public int VacancyTimeoutSeconds { get; set; } = 600;
	public int PreOffSeconds { get; set; } = 30;
	public double PreOffBrightnessFactor { get; set; } = 0.5;
	public int OverrideDurationMinutes { get; set; } = 120;
	public int VacancyResetMinutes { get; set; } = 10;          // lifts SuppressedOff
	public DarknessSource Darkness { get; set; } = DarknessSource.Either; // Lux|Sun|Either|Always
	public double LuxThreshold { get; set; } = 40;
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

public class ZoneConfig                                          // ZoneSettings overrides: all nullable
{
	public string? Name { get; set; }                            // display; defaults to AreaId
	public string? AreaId { get; set; }                          // HA area id => registry discovery
	public List<string>? Lights { get; set; }                    // explicit override; wins over discovery
	public List<string>? MotionSensors { get; set; }             // explicit override; wins over discovery
	public string? LuxSensor { get; set; }                       // explicit override
	public List<string>? IgnoreWhenOn { get; set; }              // e.g. binary_sensor.projektor_er_pa: block auto-on while on
	// nullable twins of every ZoneSettings property:
	public int? VacancyTimeoutSeconds { get; set; }
	public int? PreOffSeconds { get; set; }
	/* … one nullable property per ZoneSettings knob … */
	public bool? Enabled { get; set; }
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

Binding stays on **`IAppConfig<AdaptiveLightingConfig>`** (established repo pattern; free typed
binding; zero new packages) for v1. The Blazor read/write story (04) introduces a store that
*also* reads this same file — see 04 §4 for how the two coexist and the v2 migration path.

## 4. Registry vs hand-written YAML — evaluation and recommendation

`IHaRegistry` surface (verified from source): `Areas`/`Floors`/`Labels`/`Devices`/`Entities`,
`Area.Entities` (direct + via device), `Area.Floor`, `Label.Entities`,
`EntityRegistration.Area/Device/Labels/Platform/Options`. Device class (motion vs door, lux vs
temperature) is **not** in the registry — it lives in state attributes
(`GetState(id).Attributes["device_class"]`), which `IHaContext` provides.

**Recommendation: hybrid, discovery-first — this matches the user's own flagged direction.**

- A zone declares `AreaId` and nothing else in the common case. `ZoneEntityResolver` then:
  - lights = `area.Entities` where domain `light`, minus group members (attribute `entity_id`
    present ⇒ group; drop its members), minus entities labelled `adaptive-exclude`;
  - motion = `binary_sensor` in area with `device_class ∈ {motion, occupancy, presence}`,
    plus any entity labelled `adaptive-motion` (covers mmWave sensors with odd device classes);
  - lux = single `sensor` in area with `device_class == illuminance` (two candidates ⇒
    validation error naming both — explicit `LuxSensor` required to disambiguate).
- Explicit YAML lists (`Lights`, `MotionSensors`, `LuxSensor`) fully replace discovery for that
  slot when present — the escape hatch when HA's area assignments are wrong.
- **Zones are opt-in** (only areas listed under `Zones:` are managed). Auto-managing every HA
  area is rejected for v1: surprise coverage of `Garasje` at 02:00 is how trust in the system
  dies. (Flip to opt-out later by generating the zone list from `registry.Areas` — the resolver
  already supports it; see 05 #2.)
- Labels used: `adaptive-exclude` (never touch this entity), `adaptive-motion` (treat as motion
  source). Labels are read at startup (registry snapshot); a restart or config reload picks up
  changes.

Why not pure-registry (no YAML at all): thresholds, timeouts, periods and mode entities have no
home in the registry; and area membership in HA is one shared taxonomy that other automations
also depend on — bending it to encode lighting policy (e.g. splitting areas to get two zones)
would be the tail wagging the dog. Why not pure-YAML: hand-listing every light re-creates today's
drift problem (`IngoreEntity`…) and breaks silently when entities are renamed — discovery keeps
the config document small enough for humans to keep truthful.

## 5. Validation — fail loudly, degrade per zone

Verified behavior: an app-constructor throw is caught by `Application.InstanceApplication`,
logged, app marked `Error`; the host and all other apps keep running. Policy:

- **Document-level errors ⇒ throw** (app visibly dead, HA persistent notification posted first,
  the migration notes (not published)'s bootstrap shape): empty `Zones`, empty `Periods`, unparseable `Start`,
  overlapping/duplicate period names, duplicate zone names, negative timeouts, thresholds out of
  range, `PreOffSeconds >= VacancyTimeoutSeconds`, unknown darkness source.
- **Zone-level referential errors ⇒ degrade**: `AreaId` not in registry, explicit entity id not
  in `GetAllEntities()`, no lights resolved, no motion sensors resolved, ambiguous lux sensor —
  skip that zone, log Error, aggregate all skipped zones into ONE persistent notification
  ("Adaptive lighting: 2 of 9 zones disabled — …"). Rationale: an entity renamed in HA must not
  black out the whole house's automation.
- `ConfigValidator.Validate(config)` returns `ValidationResult { Errors[], ZoneErrors[] }`; it is
  pure (registry/entity checks take an `IReadOnlyCollection<string> knownEntityIds` + area-id
  list, so tests need no fakes).

## 6. Complete example YAML — `House/apps/AdaptiveLighting/AdaptiveLighting.yaml`

```yaml
AdaptiveLighting.Configuration.AdaptiveLightingConfig:
  ConfigName: "Adaptive lighting [House]"

  Global:
    Persons:
      - person.espen
    KillSwitchEntity: input_boolean.adaptive_lighting_enabled   # note: engine treats OFF as kill
    SleepModeEntity: input_boolean.sover
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
    LuxThreshold: 40
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

  Zones:
    - Name: Stue
      AreaId: stue
      RespectSleepMode: true
    - Name: Kjeller multimedia
      AreaId: kjeller_multimedia
      IgnoreWhenOn:
        - binary_sensor.projektor_er_pa
      VacancyTimeoutSeconds: 1800
      Darkness: Always            # basement: no daylight gate
    - Name: Gang
      AreaId: gang
      WelcomeHome: true
      VacancyTimeoutSeconds: 120
    - Name: Soverom
      AreaId: soverom
      RespectSleepMode: true
      SleepBlocksAutoOn: true
      MotionSensors:              # explicit override example (mmWave not in area)
        - binary_sensor.soverom_mmwave_presence
    - Name: Ute
      AreaId: ute
      SkipAwaySweep: true
      Darkness: Sun
```

Notes for implementers: `AreaId` is the HA registry **area id** (slug), not the display name —
today's code matched on `Entity.Area` (friendly name, e.g. `"Kjeller - multimedia"`); the new
resolver uses `IHaRegistry.GetArea(id)` and must say so in the validation message when an id
misses ("did you mean area 'Stue' (id 'stue')?" — cheap Levenshtein-free hint: list all ids).
Entity ids above are illustrative; the real ones must be filled in by the user (05 #5) —
implementers must NOT invent entity ids, ship the file with placeholders clearly marked and the
zone list empty-but-commented if the real ids are unknown, so validation fails loudly rather than
silently controlling wrong devices.
Cabin gets its own file, same shape (`person.*` and areas differ; cabin probably wants
`GuestModeEntity` earliest).
