---
title: "Example configuration"
description: "A fully worked configuration, annotated."
---

The configuration UI owns the live document: it reads that file and writes it back. YamlDotNet has no
way to preserve comments through a round trip, so the first time somebody presses **Save and apply**,
every comment in the file is gone.

The comments were the documentation, so they are kept here instead. This page is the reference; the
live file is data.

Read the [configuration reference](/configuration/) for the schema itself. This page is the annotated
example — what a real document looks like and why each knob is set the way it is.

## You probably do not need this

Nothing below has to be typed. A fresh installation starts from a document that names **no entities at
all** — no people, no rooms, no kill switch, only a sensible day/night curve — and then goes looking.
Half a minute after it connects, set-up reads Home Assistant's area registry, writes down every room
with both a light and a motion sensor, guesses each room's role from its name, and seeds the list of
people. Every room it proposes is **switched off**, so no light changes until the owner opens the UI
and chooses.

A shipped example full of `REPLACE_ME` placeholders reads as helpful and behaves as sabotage: every
placeholder is an id Home Assistant does not know, so a brand-new installation refuses to run and the
owner's first experience is a list of errors about rooms that were never theirs. Worse, a placeholder
*overrides* the discovery that would otherwise fill the same field: an empty `Persons` list finds every
person by itself, while `person.REPLACE_ME` finds nothing and blocks the engine.

So read this page for what each setting means and which shapes are worth knowing. Hand-editing the file
is the escape hatch, not the route in.

## The annotated document

```yaml
# ============================================================================
#  Adaptive lighting — a worked example
# ============================================================================
#
#  The top-level key MUST stay the fully qualified config class name — that is
#  how IAppConfig<T> binds. The filename is irrelevant to binding.
#
#  A document written before 2.0 says "Zones:" instead of "Areas:". It still
#  loads: the deserialiser renames the key, and the engine rewrites the file in
#  the new schema on the first start after the upgrade, keeping the previous
#  file at the store's backup path. Nothing to do by hand.
# ============================================================================

AdaptiveLighting.Configuration.AdaptiveLightingConfig:

  # Label for logs and notifications only. It is what tells two houses apart.
  ConfigName: "Adaptive lighting [House]"

  # --------------------------------------------------------------------------
  #  Global — house-wide. Every entity named here must exist in HA.
  # --------------------------------------------------------------------------
  Global:

    # person.* / device_tracker.* watched for presence ("home" = home).
    # An EMPTY list means "everyone Home Assistant knows, including people added
    # later" — which is a valid and often better choice than listing them.
    # First-run set-up fills this in once, so the owner can see and edit who
    # drives Home and Away rather than having it happen invisibly.
    Persons:
      - person.espen

    # Gates the whole engine. Omit the setting entirely and the app's own
    # NetDaemon enable switch is used instead — there is always a master switch.
    # KillSwitchActiveWhenOff: true (the default) reads the entity as an ENABLED
    # flag: state "off" muzzles the engine. Set it to false for an entity named
    # as a true kill switch, where "on" kills.
    KillSwitchEntity: input_boolean.adaptive_lighting_enabled
    KillSwitchActiveWhenOff: true

    # HA user id owning this host's long-lived token. Optional: override
    # detection works without it (command-expectation correlation is primary).
    # Filling it in sharpens "was that change us, or a human?".
    # Find it in HA: Settings -> People -> the user the token belongs to.
    # It is not a credential — an opaque id.
    # NetDaemonUserId: ""

    # How long everyone must be gone before the house counts as Away.
    AwayDebounceMinutes: 5

    # How often each room re-checks the time of day and the light outside.
    # Once a minute is plenty.
    CircadianTickSeconds: 60

    # After a command, how long a state change on that light is still read as
    # our own echo rather than as a person at a switch.
    SelfEchoWindowSeconds: 8

    # Whether a change carrying a parent context (another automation) counts as
    # a manual override. true = other automations win over this engine.
    TreatAutomationsAsManual: true

    # Blend circadian targets across period boundaries instead of stepping.
    # This is the Schedule section's "Blend between periods".
    SmoothTransitions: true
    BlendMinutes: 30

    # A house-wide outdoor lux sensor: the darkness reading for any room that
    # resolves no lux sensor of its own. One outdoor sensor can drive "is it
    # dark" across the whole house instead of every room needing one. Omit it
    # and such rooms fall back to sun elevation.
    # OutdoorLuxSensor: sensor.outdoor_illuminance

    # The house mode select and its option kinds. Set up from the House modes
    # section rather than by hand — first-run set-up adopts an obvious
    # input_select if it finds one.
    # HouseMode:
    #   Entity: input_select.husmodus
    #   Options:
    #     - Value: Hjemme
    #       Kind: Normal
    #     - Value: Borte
    #       Kind: Away
    #       Scene: scene.borte_belysning
    #       ResetOnPresence: true

    # Registry labels — the "Finding lights & sensors" group. Label an entity in
    # HA with these to:
    #   ExcludeLabel — never let the engine touch it (a lamp on a smart plug
    #                  that must stay put, a light in a room you half-manage).
    #   IncludeLabel — manage ONLY lights carrying this label. Empty (the
    #                  default, and what every pre-existing document means by
    #                  saying nothing) manages every light discovery finds.
    #                  It filters lights only: motion and lux sensors are
    #                  inputs, not things the engine commands, and filtering
    #                  them too would make a half-labelled house deaf.
    #                  Exclude always wins — a light carrying both is not
    #                  managed — and an explicit Lights list bypasses both.
    #   MotionLabel  — treat it as a motion source whatever its device class
    #                  (mmWave sensors often report something odd).
    ExcludeLabel: adaptive-exclude
    # IncludeLabel: adaptive
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
  #  Defaults — the baseline every room starts with. A room overrides only what
  #  differs, and a room's own setting always wins. The settings page calls this
  #  group "All rooms". Every knob here has a nullable twin on a room.
  # --------------------------------------------------------------------------
  Defaults:

    # "Lights stay on for": motion-free time before an active room dims to its
    # pre-off warning. Longer for rooms where people sit still.
    VacancyTimeoutSeconds: 600

    # The warning dim: lights drop to PreOffBrightnessFactor of target for
    # PreOffSeconds, and any movement in that window brings them straight back.
    # This is the "speak now" grace that stops the lights going out on a still
    # reader. PreOffSeconds must be shorter than VacancyTimeoutSeconds.
    PreOffSeconds: 30
    PreOffBrightnessFactor: 0.5

    # "Hand changes hold for": when someone adjusts a light by hand, their
    # choice is left alone this long before automatic control resumes.
    OverrideDurationMinutes: 120

    # "After a manual off, wait": motion-free time after someone turns the
    # lights off by hand before movement can turn them back on. Until then, the
    # room respects "the human wanted it dark".
    VacancyResetMinutes: 10

    # How a room decides it's dark:
    #   Lux    — the lux sensor only (falls back to sun if none resolves)
    #   Sun    — sun elevation only
    #   Either — dark when either says so
    #   Always — no daylight gate (rooms without windows)
    Darkness: Either
    LuxThreshold: 40           # "Dark below": at or below this many lux
    LuxHysteresis: 10          # extra light needed to count as bright again
    SunElevationThreshold: 3.0 # "Dark when the sun is below", in degrees
    SunEntity: sun.sun

    # Fades. Long at night because eyes are dark-adapted; snappy by day.
    DayTransitionSeconds: 1
    NightTransitionSeconds: 15

    # Room behaviour — off by default, opted into per room.
    RespectSleepMode: false    # held to the night caps while a Sleep mode is on
    SleepBlocksAutoOn: false   # refuses to auto-on at all while asleep
    SkipAwaySweep: false       # "Stays on when everyone leaves"
    WelcomeHome: false         # lights on first arrival, when it's dark

    # A switched-off room is still observed and published, never commanded.
    # The UI does not offer this as a default: enablement is a per-room decision
    # made on the room's own switch, and flipping it here would silently flip
    # every room that never wrote an explicit value.
    Enabled: true

  # --------------------------------------------------------------------------
  #  Periods — the house-wide circadian table, the Schedule section.
  #  A period runs from its Start until the next period begins. Start is either
  #  a clock time ("06:00") or a sun event with an optional offset ("sunrise",
  #  "sunset-01:00", "sunrise+00:45"). Quote clock times — bare 06:00 is a
  #  YAML sexagesimal, not a string.
  #
  #  Sun-anchored boundaries are read off SunEntity's next_rising/next_setting.
  #  Far north those swing hard across the year, and around midsummer or
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
      # and it also applies to the warning dim.
      MaxBrightnessPct: 30
      MinBrightnessPct: 5

  # --------------------------------------------------------------------------
  #  Areas — OPT-IN. An HA area that is not listed here is never touched, and a
  #  listed room with Enabled: false is watched but never commanded.
  #
  #  In the common case a room declares an AreaId and nothing else: lights,
  #  motion sensors and the lux sensor are discovered from the area registry.
  #  Discovery drops group members (the group wins) and anything labelled
  #  adaptive-exclude. The explicit lists below are the escape hatch for when
  #  HA's area assignments are wrong; each one fully replaces discovery for
  #  that slot alone, and bypasses the include and exclude labels.
  #
  #  AreaId is the registry area *id* (the slug, e.g. "stue"), NOT the display
  #  name ("Stue"). Start the host once and read the notification: it lists
  #  every known area id.
  #
  #  These five are worked examples of the shapes worth knowing.
  # --------------------------------------------------------------------------
  Areas:

    # 1. The common case: discovery does everything. Enabled is written
    #    explicitly because the room's switch in the UI always writes a value —
    #    inheritance stays for older documents, new decisions are never implied.
    - Name: Living room
      AreaId: stue
      Enabled: true
      RespectSleepMode: true

    # 2. A room with no daylight, a long timeout, and something that must block
    #    auto-on while it is on (a projector, a "do not disturb" flag).
    - Name: Media room
      AreaId: kjeller_multimedia
      Enabled: true
      Darkness: Always
      VacancyTimeoutSeconds: 1800
      IgnoreWhenOn:
        - binary_sensor.projektor_er_pa

    # 3. An entrance: short timeout, lights up on first arrival when dark.
    - Name: Hallway
      AreaId: gang
      Enabled: true
      WelcomeHome: true
      VacancyTimeoutSeconds: 120

    # 4. A bedroom, and an explicit-override example: the mmWave sensor is not
    #    in the area, so MotionSensors replaces motion discovery here. Lights
    #    and lux are still discovered — only the listed slot is replaced.
    - Name: Bedroom
      AreaId: soverom
      Enabled: true
      RespectSleepMode: true
      SleepBlocksAutoOn: true
      MotionSensors:
        - binary_sensor.soverom_mmwave_presence

    # 5. Outdoors: opts out of the leaving sweep (security lights are wanted
    #    precisely when nobody's home), gated on the sun, and fully explicit —
    #    no discovery is used when Lights is given. Found by set-up but not
    #    switched on yet, so nothing out here is commanded.
    - Name: Outdoor
      AreaId: ute
      Enabled: false
      SkipAwaySweep: true
      Darkness: Sun
      SunElevationThreshold: 1.0
      Lights:
        - light.outdoor_front
      LuxSensor: sensor.outdoor_illuminance
```

## Shapes worth knowing

A few of the decisions above are worth stating outright, because they come up in every house.

**A whole-house catch-all is not a room.** An area holding one "all lights" group and one "indoor
motion" sensor covers the kitchen and the living room at once, so a room there would light half the
house on any movement and fight the real rooms below it. Leave it out. Set-up will not propose it if it
has no motion sensor of its own; if it does, switch it off and leave it off.

**An area with motion but no lights has nothing to offer**, and one with lights but no way to sense
people cannot participate in motion-driven lighting. Set-up skips both. Add them when they get the
missing half.

**Explicit lists are for when the registry lies.** A room whose area also holds three unavailable test
rigs is a good reason to list the real lights by hand; a room with two illuminance sensors needs an
explicit `LuxSensor`, because the resolver treats two candidates as ambiguous and would rather refuse
the room than guess. Everything else is better left to discovery, which stays true across a rename.
