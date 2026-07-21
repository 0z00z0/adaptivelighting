---
title: "Example configuration"
description: "A fully worked configuration, annotated."
---

The lighting web UI now owns `apps/AdaptiveLighting/AdaptiveLighting.yaml` on each host: it reads that
file and writes it back. YamlDotNet has no way to preserve comments through a round trip, so the first
time somebody presses **Save** in the browser, every comment in that file is gone.

The comments were the documentation, so they are preserved here instead, verbatim as they stood before
the UI gained a write path. This page is the reference; the live file is now data.

Read [03-configuration.md](/configuration/) for the schema itself. This page is the annotated
example — what a real document looks like and why each knob is set the way it is.

The two differ usefully. **House's still has `REPLACE_ME` placeholders**: that is the shape a host ships
in, and what the validator's error messages are written against. **Cabin's has real ids**, read
from one instance's live registry — so it is the better read for what a filled-in document actually looks
like.

These are copies. The live files under `apps/AdaptiveLighting/` are now only the example that seeds
each host's real, external document on first run (see
the repository's issues #4); the document the engine reads lives at
`AdaptiveLighting:ConfigPath`, outside the publish tree. Editing either file by hand configures
nothing on a host that has already seeded.

## House — `House/apps/AdaptiveLighting/AdaptiveLighting.yaml`

```yaml
# ============================================================================
#  Adaptive lighting — House (Bjørmoen 1B, the house host)
# ============================================================================
#
#  THIS FILE DOES NOT WORK YET, ON PURPOSE.
#
#  Every id below that reads REPLACE_ME is a placeholder. They were left as
#  placeholders deliberately: the person who wrote this file could not see
#  the house host's entity registry, and an entity id that merely *looks* plausible
#  would quietly command the wrong device at 03:00. A placeholder cannot.
#
#  As shipped, the app fails at startup:
#    - Global.Persons / KillSwitchEntity / SleepModeEntity name entities HA
#      does not know  ->  document-level errors  ->  the app throws, goes to
#      ApplicationState.Error, and posts an HA persistent notification titled
#      "Adaptive lighting: invalid configuration" listing every problem.
#      The host and every other app keep running.
#    - The zone AreaIds are not registry area ids  ->  zone-level errors. Those
#      only skip the zone, and the notification lists ALL known area ids for
#      this HA instance — which is the fastest way to fill this file in.
#
#  To adopt: start the host once, read the notification, replace every
#  REPLACE_ME with a real id (or delete the setting where the feature is not
#  wanted), restart. Delete the zones you do not have.
#
#  The top-level key MUST stay the fully qualified config class name — that is
#  how IAppConfig<T> binds. The filename is irrelevant to binding.
# ============================================================================

AdaptiveLighting.Configuration.AdaptiveLightingConfig:

  # Label for logs and notifications only.
  ConfigName: "Adaptive lighting [House]"

  # --------------------------------------------------------------------------
  #  Global — house-wide. Every entity named here must exist, or the app throws.
  # --------------------------------------------------------------------------
  Global:

    # person.* / device_tracker.* watched for presence ("home" = home).
    # An EMPTY list means "discover every person.* entity" — which is a valid
    # and often better choice than listing them.
    Persons:
      - person.REPLACE_ME

    # Gates the whole engine. Omit the setting entirely to disable the feature
    # (engine then always enabled). KillSwitchActiveWhenOff: true (the default)
    # reads this as an ENABLED flag — state "off" kills the engine. Set it to
    # false if the entity is named as a true kill switch, where "on" kills.
    KillSwitchEntity: input_boolean.REPLACE_ME_adaptive_lighting_enabled
    KillSwitchActiveWhenOff: true

    # Sleep mode. Omit to make sleep permanently inactive.
    # Zones opt in per zone via RespectSleepMode / SleepBlocksAutoOn.
    SleepModeEntity: input_boolean.REPLACE_ME_sleep_mode

    # Guest mode is parsed and published now; behaviour is reserved for v2.
    # Omit unless the entity exists — a placeholder here throws like any other.
    # GuestModeEntity: input_boolean.REPLACE_ME_guest_mode

    # HA user id owning this host's long-lived token. Optional: override
    # detection works without it (command-expectation correlation is primary).
    # Filling it in sharpens "was that change us, or a human?".
    # Find it in HA: Settings -> People -> the user the token belongs to.
    # NetDaemonUserId: ""

    # How long everyone must be gone before the house counts as Away.
    AwayDebounceMinutes: 5

    # How often an active zone re-evaluates its circadian target.
    CircadianTickSeconds: 60

    # After a command, how long a state change on that light is still read as
    # our own echo rather than as a human touching a switch.
    SelfEchoWindowSeconds: 8

    # Whether a change carrying a parent context (another automation) counts as
    # a manual override. true = other automations win over this engine.
    TreatAutomationsAsManual: true

    # Blend circadian targets across period boundaries instead of stepping.
    SmoothTransitions: true
    BlendMinutes: 30

    # The period whose caps a sleep-respecting zone is held to while asleep,
    # whatever the clock says. Must match a name in Periods below.
    SleepPeriodName: night

    # Registry labels. Label an entity in HA with these to:
    #   ExcludeLabel — never let the engine touch it (a lamp on a smart plug
    #                  that must stay put, a light in an area you half-manage).
    #   MotionLabel  — treat it as a motion source whatever its device class
    #                  (mmWave sensors often report something odd).
    ExcludeLabel: adaptive-exclude
    MotionLabel: adaptive-motion

    # Discovery filters: which device classes qualify during area discovery.
    #
    # MotionDeviceClasses is empty by default, and empty means the built-in set
    # [motion, occupancy, presence]. Listing classes here REPLACES that set
    # rather than adding to it, so spell out every class you want — including
    # the built-in ones you still need. (The default is empty rather than the
    # three real values because the .NET configuration binder appends to a
    # non-empty default instead of replacing it; an empty default is what makes
    # this list behave the way the YAML reads.)
    # MotionDeviceClasses:
    #   - motion
    #   - occupancy
    #   - presence
    #   - vibration
    IlluminanceDeviceClass: illuminance

    # A light within these tolerances of the target is left alone, rather than
    # being told to fade to where it already is on every tick.
    BrightnessTolerancePct: 2
    ColorTempToleranceKelvin: 50

  # --------------------------------------------------------------------------
  #  Defaults — the baseline for every zone. A zone overrides only what differs.
  #  Every knob here has a nullable twin on a zone.
  # --------------------------------------------------------------------------
  Defaults:

    # Motion-free time before an active zone dims to its pre-off warning.
    VacancyTimeoutSeconds: 600

    # The pre-off warning: lights drop to PreOffBrightnessFactor of target for
    # PreOffSeconds, and motion in that window cancels darkness. This is the
    # "speak now" grace that stops the lights going out on a still reader.
    # PreOffSeconds must be shorter than VacancyTimeoutSeconds.
    PreOffSeconds: 30
    PreOffBrightnessFactor: 0.5

    # How long a manual change holds a zone before automatic control resumes.
    OverrideDurationMinutes: 120

    # Motion-free time after a manual turn-off before suppression is lifted.
    # Until then, motion is respected as "the human wanted it dark".
    VacancyResetMinutes: 10

    # Which signal decides the zone is dark enough to light:
    #   Lux    — the lux sensor only (falls back to sun if none resolves)
    #   Sun    — sun elevation only
    #   Either — dark when either says so
    #   Always — no daylight gate (rooms without windows)
    Darkness: Either
    LuxThreshold: 40
    LuxHysteresis: 10          # extra lux needed to leave dark: stops flapping
    SunElevationThreshold: 3.0 # degrees
    SunEntity: sun.sun

    # Fade lengths. Long at night because eyes are dark-adapted; snappy by day.
    DayTransitionSeconds: 1
    NightTransitionSeconds: 15

    # Sleep/away/welcome behaviour — off by default, opted into per zone.
    RespectSleepMode: false
    SleepBlocksAutoOn: false
    SkipAwaySweep: false
    WelcomeHome: false

    # A disabled zone is still observed and published, just never commanded.
    Enabled: true

  # --------------------------------------------------------------------------
  #  Periods — the house-wide circadian table.
  #  A period runs from its Start until the next period begins. Start is either
  #  a clock time ("06:00") or a sun event with an optional offset ("sunrise",
  #  "sunset-01:00", "sunrise+00:45"). Quote clock times — bare 06:00 is a
  #  YAML sexagesimal, not a string.
  # --------------------------------------------------------------------------
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
      # The night-light floor. MaxBrightnessPct caps EVERY command while this
      # period is active — nobody gets 100% in the face at 03:00, whatever a
      # welcome-home or a motion event asks for. MinBrightnessPct is the floor,
      # and it also applies to the pre-off dim.
      MaxBrightnessPct: 30
      MinBrightnessPct: 5

  # --------------------------------------------------------------------------
  #  Zones — OPT-IN. An HA area that is not listed here is never touched.
  #
  #  In the common case a zone declares an AreaId and nothing else: lights,
  #  motion sensors and the lux sensor are discovered from the area registry.
  #  Discovery drops group members (the group wins) and anything labelled
  #  adaptive-exclude. The explicit lists below are the escape hatch for when
  #  HA's area assignments are wrong; each one fully replaces discovery for
  #  that slot alone.
  #
  #  AreaId is the registry area *id* (the slug, e.g. "stue"), NOT the display
  #  name ("Stue"). Start the host once and read the notification: it lists
  #  every known area id.
  #
  #  These five are worked examples of the shapes worth knowing. Replace the
  #  AreaIds with real ones and delete the zones you do not have.
  # --------------------------------------------------------------------------
  Zones:

    # 1. The common case: discovery does everything.
    - Name: Example living room
      AreaId: REPLACE_ME_living_room_area_id
      RespectSleepMode: true

    # 2. A room with no daylight, a long timeout, and something that must block
    #    auto-on while it is on (a projector, a "do not disturb" flag).
    - Name: Example media room
      AreaId: REPLACE_ME_media_room_area_id
      Darkness: Always
      VacancyTimeoutSeconds: 1800
      IgnoreWhenOn:
        - binary_sensor.REPLACE_ME_projector_is_on

    # 3. An entry zone: short timeout, lights up on first arrival when dark.
    - Name: Example hallway
      AreaId: REPLACE_ME_hallway_area_id
      WelcomeHome: true
      VacancyTimeoutSeconds: 120

    # 4. A bedroom, and an explicit-override example: the mmWave sensor is not
    #    in the area, so MotionSensors replaces motion discovery here. Lights
    #    and lux are still discovered — only the listed slot is replaced.
    - Name: Example bedroom
      AreaId: REPLACE_ME_bedroom_area_id
      RespectSleepMode: true
      SleepBlocksAutoOn: true
      MotionSensors:
        - binary_sensor.REPLACE_ME_bedroom_mmwave_presence

    # 5. Outdoors: opts out of the leaving sweep (security lights stay on when
    #    the house empties), gated on the sun, and fully explicit — no area
    #    needed when Lights is given.
    - Name: Example outdoor
      AreaId: REPLACE_ME_outdoor_area_id
      SkipAwaySweep: true
      Darkness: Sun
      SunElevationThreshold: 1.0
      Lights:
        - light.REPLACE_ME_outdoor_front
      LuxSensor: sensor.REPLACE_ME_outdoor_illuminance
```

## Cabin — `Cabin/apps/AdaptiveLighting/AdaptiveLighting.yaml`

```yaml
# ============================================================================
#  Adaptive lighting — Cabin (the cabin, the cabin host)
# ============================================================================
#
#  Filled in from one instance's live registry on 2026-07-17. Every id below was read
#  from the running instance, not guessed — the placeholders this file shipped
#  with did their job: the app started, validated, and named the real area ids
#  back in a persistent notification.
#
#  Three zones, not four. The 'petterhaugen' area is deliberately NOT a zone:
#  its only light is light.lys_alt ("Lys [alle]"), a group spanning the kitchen
#  and the living room, and its motion sensor is binary_sensor.inne_bevegelse
#  ("indoor motion"). It is a whole-house catch-all, not a room — a zone there
#  would light the house on any indoor movement and fight Stue and Kjokken.
#  Areas kjeller, sov1, terrasse and toalett have motion but no lights.
#
#  The top-level key MUST stay the fully qualified config class name — that is
#  how IAppConfig<T> binds. The filename is irrelevant to binding.
#
#  Note: this file is the SEED. Once the Blazor config UI lands, the live config
#  moves outside the publish tree (the deploy wipes and re-copies this folder),
#  and the UI owns it from then on.
# ============================================================================

AdaptiveLighting.Configuration.AdaptiveLightingConfig:

  # Label for logs and notifications only.
  ConfigName: "Adaptive lighting [Cabin]"

  # --------------------------------------------------------------------------
  #  Global — house-wide. Every entity named here must exist, or the app throws.
  # --------------------------------------------------------------------------
  Global:

    # person.* / device_tracker.* watched for presence ("home" = home).
    # Listed explicitly ON PURPOSE. An empty list means "discover every person.*",
    # and the cabin host also knows person.b1_espen, person.b1_samuel, person.b1_leon and
    # person.b1_faith — the Bjørmoen household. Discovery would make the cabin
    # believe someone is home whenever anyone is home at the *other house*, and
    # Away would never fire.
    Persons:
      - person.espen
      - person.leon
      - person.samuel
      - person.faith

    # Kill switch and sleep mode are OMITTED, not forgotten: the cabin host has no such
    # helpers (its only input_booleans are 'occupancy' and NetDaemon's own app
    # switch), and naming an entity HA does not know is a document-level error
    # that stops the app dead. Omitting a setting disables that feature cleanly.
    #
    # There is already a working kill switch for free: NetDaemon auto-creates
    # input_boolean.netdaemon_laget_net_daemon_petterhaugen_adaptive_lighting_app,
    # which disables the whole app. Purpose-built helpers are to be created by
    # the engine itself via IMqttEntityManager (decided 2026-07-17, not built
    # yet); wire KillSwitchEntity/SleepModeEntity to them when they exist.
    # KillSwitchEntity: input_boolean.adaptive_lighting_enabled
    # KillSwitchActiveWhenOff: true
    # SleepModeEntity: input_boolean.sover

    # Guest mode is parsed and published now; the behaviour it will gate
    # (longer timeouts, less aggressive sweeps) is reserved for v2. A cabin is
    # the obvious first customer, so the setting is shown — but it is commented
    # out, because a placeholder here throws like any other id.
    # GuestModeEntity: input_boolean.REPLACE_ME_guest_mode

    # HA user id owning this host's long-lived token. Optional: override
    # detection works without it (command-expectation correlation is primary).
    # Filling it in sharpens "was that change us, or a human?".
    # Find it in HA: Settings -> People -> the user the token belongs to.
    # This is one instance's own user id — it is NOT the same value as House's.
    # Read from the cabin host on 2026-07-17: the context.user_id HA recorded when
    # NetDaemon created its own app switch, i.e. the token owner. Not a
    # credential — an opaque id. It gives override detection a second,
    # independent signal beyond command-expectation correlation.
    NetDaemonUserId: "818195a0b0384602b3b4d100b2b5b337"

    # How long everyone must be gone before the house counts as Away.
    AwayDebounceMinutes: 5

    # How often an active zone re-evaluates its circadian target.
    CircadianTickSeconds: 60

    # After a command, how long a state change on that light is still read as
    # our own echo rather than as a human touching a switch.
    SelfEchoWindowSeconds: 8

    # Whether a change carrying a parent context (another automation) counts as
    # a manual override. true = other automations win over this engine.
    TreatAutomationsAsManual: true

    # Blend circadian targets across period boundaries instead of stepping.
    SmoothTransitions: true
    BlendMinutes: 30

    # The period whose caps a sleep-respecting zone is held to while asleep,
    # whatever the clock says. Must match a name in Periods below.
    SleepPeriodName: night

    # Registry labels. Label an entity in HA with these to:
    #   ExcludeLabel — never let the engine touch it (a lamp on a smart plug
    #                  that must stay put, a light in an area you half-manage).
    #   MotionLabel  — treat it as a motion source whatever its device class
    #                  (mmWave sensors often report something odd).
    ExcludeLabel: adaptive-exclude
    MotionLabel: adaptive-motion

    # Discovery filters: which device classes qualify during area discovery.
    #
    # MotionDeviceClasses is empty by default, and empty means the built-in set
    # [motion, occupancy, presence]. Listing classes here REPLACES that set
    # rather than adding to it, so spell out every class you want — including
    # the built-in ones you still need. (The default is empty rather than the
    # three real values because the .NET configuration binder appends to a
    # non-empty default instead of replacing it; an empty default is what makes
    # this list behave the way the YAML reads.)
    # MotionDeviceClasses:
    #   - motion
    #   - occupancy
    #   - presence
    #   - vibration
    IlluminanceDeviceClass: illuminance

    # A light within these tolerances of the target is left alone, rather than
    # being told to fade to where it already is on every tick.
    BrightnessTolerancePct: 2
    ColorTempToleranceKelvin: 50

  # --------------------------------------------------------------------------
  #  Defaults — the baseline for every zone. A zone overrides only what differs.
  #  Every knob here has a nullable twin on a zone.
  # --------------------------------------------------------------------------
  Defaults:

    # Motion-free time before an active zone dims to its pre-off warning.
    VacancyTimeoutSeconds: 600

    # The pre-off warning: lights drop to PreOffBrightnessFactor of target for
    # PreOffSeconds, and motion in that window cancels darkness. This is the
    # "speak now" grace that stops the lights going out on a still reader.
    # PreOffSeconds must be shorter than VacancyTimeoutSeconds.
    PreOffSeconds: 30
    PreOffBrightnessFactor: 0.5

    # How long a manual change holds a zone before automatic control resumes.
    OverrideDurationMinutes: 120

    # Motion-free time after a manual turn-off before suppression is lifted.
    # Until then, motion is respected as "the human wanted it dark".
    VacancyResetMinutes: 10

    # Which signal decides the zone is dark enough to light:
    #   Lux    — the lux sensor only (falls back to sun if none resolves)
    #   Sun    — sun elevation only
    #   Either — dark when either says so
    #   Always — no daylight gate (rooms without windows)
    Darkness: Either
    LuxThreshold: 40
    LuxHysteresis: 10          # extra lux needed to leave dark: stops flapping
    SunElevationThreshold: 3.0 # degrees
    SunEntity: sun.sun

    # Fade lengths. Long at night because eyes are dark-adapted; snappy by day.
    DayTransitionSeconds: 1
    NightTransitionSeconds: 15

    # Sleep/away/welcome behaviour — off by default, opted into per zone.
    RespectSleepMode: false
    SleepBlocksAutoOn: false
    SkipAwaySweep: false
    WelcomeHome: false

    # A disabled zone is still observed and published, just never commanded.
    Enabled: true

  # --------------------------------------------------------------------------
  #  Periods — the house-wide circadian table.
  #  A period runs from its Start until the next period begins. Start is either
  #  a clock time ("06:00") or a sun event with an optional offset ("sunrise",
  #  "sunset-01:00", "sunrise+00:45"). Quote clock times — bare 06:00 is a
  #  YAML sexagesimal, not a string.
  #
  #  Sun-anchored boundaries are read off SunEntity's next_rising/next_setting.
  #  This far north those swing hard across the year, and around midsummer or
  #  midwinter a sun boundary can be unresolvable — a period that cannot be
  #  placed is skipped, so keep at least one fixed clock boundary (night, here)
  #  as the backstop.
  # --------------------------------------------------------------------------
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
      # The night-light floor. MaxBrightnessPct caps EVERY command while this
      # period is active — nobody gets 100% in the face at 03:00, whatever a
      # welcome-home or a motion event asks for. MinBrightnessPct is the floor,
      # and it also applies to the pre-off dim.
      MaxBrightnessPct: 30
      MinBrightnessPct: 5

  # --------------------------------------------------------------------------
  #  Zones — OPT-IN. An HA area that is not listed here is never touched.
  #
  #  In the common case a zone declares an AreaId and nothing else: lights,
  #  motion sensors and the lux sensor are discovered from the area registry.
  #  Discovery drops group members (the group wins) and anything labelled
  #  adaptive-exclude. The explicit lists below are the escape hatch for when
  #  HA's area assignments are wrong; each one fully replaces discovery for
  #  that slot alone.
  #
  #  AreaId is the registry area *id* (the slug), NOT the display name. Start
  #  the host once and read the notification: it lists every known area id.
  #
  #  These four are worked examples of the shapes worth knowing. Replace the
  #  AreaIds with real ones and delete the zones you do not have.
  # --------------------------------------------------------------------------
  Zones:

    # Stue. Lights are listed explicitly rather than discovered: the area also
    # holds three esp_lightcontrol_test / esp_test_netdaemon lights, all
    # currently unavailable, which are test rigs and not room lighting.
    # light.livingroom_ceiling ("Stue - Taklys") is a group of the three real
    # ceiling lights, so commanding it covers all of them — the engine keeps
    # groups and drops their members, which is exactly the behaviour wanted here.
    # MotionSensors is likewise explicit: the area exposes ten motion-class
    # binary_sensors, but nine are ESP test devices; only the multisensor is real.
    - Name: Stue
      AreaId: stue
      Lights:
        - light.livingroom_ceiling
      MotionSensors:
        - binary_sensor.stue_multisensor_motion_detection
      LuxSensor: sensor.stue_multisensor_illuminance

    # Tilbygg (annex). Lights and motion come from discovery — both lights are
    # real, and all three motion sensors are genuine. LuxSensor must be explicit:
    # the area has two illuminance sensors, which the resolver treats as
    # ambiguous and would refuse the zone over. annex_illuminance reports a
    # value; tilbygg_bevegelse_shelly_m2_luminosity is unavailable.
    - Name: Tilbygg
      AreaId: tilbygg
      LuxSensor: sensor.annex_illuminance

    # Kjokken. Discovery finds light.kjokken_taklys and
    # binary_sensor.kjokken_taklys_occupancy. No illuminance sensor in this area,
    # so the daylight gate is sun elevation rather than the default Either —
    # stated outright instead of relying on the lux-to-sun fallback.
    - Name: Kjokken
      AreaId: kjokken
      Darkness: Sun

    # NOT a zone: 'petterhaugen'. Its only light is light.lys_alt ("Lys [alle]"),
    # a group of kjokken_taklys + livingroom_ceiling + wiz_rgbww_tunable_734b00 +
    # stue_spisebord, and its motion sensor is binary_sensor.inne_bevegelse. It
    # is a whole-house catch-all, so a zone here would command the kitchen and
    # living room from one indoor motion sensor — and fight the Stue and Kjokken
    # zones below it. Excluded deliberately.
    #
    # NOT zones: kjeller, sov1, terrasse, toalett. All have motion sensors but no
    # lights, so there is nothing to control. Add them when they get lights.
```
