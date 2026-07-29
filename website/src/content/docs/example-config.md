---
title: "Example configuration"
description: "What the configuration file looks like, annotated."
---

The Configuration page owns this file: it reads it and writes it back. Comments do not survive that
round trip, so they live here instead. Hand-editing works, and the next save from the browser
rewrites the file from scratch.

For what each setting does, see the [settings reference](/configuration/).

## You probably do not need this

A fresh installation starts from a document naming **no entities at all** — no people, no rooms, no
master switch, only a day/night curve — and then goes looking. Set-up finds your rooms, and every one
it finds is switched off until you choose. Nothing below has to be typed.

The two things you cannot do from the browser are `FollowOutdoorLux` on a room and
`LuxSensorStaleAfterMinutes` on the house. Everything else is easier in the UI.

## A worked document

```yaml
# The top-level key must stay the fully qualified class name. The filename does not matter.
AdaptiveLighting.Configuration.AdaptiveLightingConfig:

  # A label for logs and notifications, so two houses can be told apart.
  ConfigName: "Adaptive lighting [House]"

  # --------------------------------------------------------------------------
  #  Global — the house. Every entity named here must exist in Home Assistant.
  # --------------------------------------------------------------------------
  Global:

    # Whose presence decides Home and Away. An EMPTY list means everyone Home
    # Assistant knows, including people added later — usually the better choice.
    # Set-up seeds this once so you can see who drives it.
    Persons:
      - person.espen

    # The master switch. Omit it entirely and this app's own enable switch is
    # used, so there is always one. Left true, the entity reads as an ENABLED
    # flag: state "off" pauses the engine. Set it false for an entity named as a
    # true kill switch, where "on" pauses it.
    KillSwitchEntity: input_boolean.adaptive_lighting_enabled
    KillSwitchActiveWhenOff: true

    # How long everyone must be gone before rooms react to an empty house.
    AwayDebounceMinutes: 5

    # How often each room re-checks the time of day and the light outside.
    CircadianTickSeconds: 60

    # After a command, how long a change on that light still counts as our own
    # echo rather than as a person at a switch.
    SelfEchoWindowSeconds: 8

    # Whether a change made by another automation counts as a manual change.
    # true means your other automations win.
    TreatAutomationsAsManual: true

    # Lights drift to the next period's level instead of stepping at the
    # boundary. This is the Schedule section's "Blend between periods".
    SmoothTransitions: true
    BlendMinutes: 30

    # The house's outdoor light sensor. Rooms read it only if they ask to, with
    # FollowOutdoorLux — one shaded outdoor sensor reads hundreds of lux while
    # the rooms behind it are dark. A room with no reading at all counts as dark
    # and lights on movement.
    # OutdoorLuxSensor: sensor.outdoor_illuminance

    # How long a LIGHT-LEVEL sensor may go without reporting before it stops
    # counting toward a room's average. Zero or less switches the rule off.
    # Illuminance only: a motion sensor that has said nothing for hours is a room
    # nobody walked through, not a fault.
    # LuxSensorStaleAfterMinutes: 120

    # The house-mode select and what each option means. Set this up from the
    # House modes section rather than by hand — set-up adopts an obvious
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
    #       ResetPresenceGraceMinutes: 15
    #       ActivateAfterNoMotionMinutes: 360
    #     - Value: Sover
    #       Kind: Sleep
    #       ClampPeriod: night

    # Labels — the "Finding lights & sensors" group.
    #   ExcludeLabel — never let the app touch it.
    #   IncludeLabel — manage ONLY lights carrying this label. Empty, the
    #                  default, manages every light that is found. Lights only:
    #                  filtering sensors too would make a half-labelled house
    #                  deaf. Exclude always wins, and an explicit Lights list
    #                  bypasses both.
    #   MotionLabel  — treat it as a motion source whatever its device class.
    ExcludeLabel: adaptive-exclude
    # IncludeLabel: room-light
    MotionLabel: adaptive-motion

    # Which device classes qualify during discovery. MotionDeviceClasses is
    # empty by default, and empty means [motion, occupancy, presence]. Listing
    # classes here REPLACES that set rather than adding to it, so spell out
    # every class you want.
    # MotionDeviceClasses:
    #   - motion
    #   - occupancy
    #   - presence
    #   - vibration
    IlluminanceDeviceClass: illuminance

  # --------------------------------------------------------------------------
  #  Defaults — what every room starts with. A room overrides only what differs,
  #  and a room's own setting always wins.
  # --------------------------------------------------------------------------
  Defaults:

    # "Lights stay on for": motion-free time before an active room dims to its
    # warning level. Longer for rooms where people sit still.
    VacancyTimeoutSeconds: 600

    # The warning dim: lights drop to this fraction of target for this long, and
    # any movement brings them straight back. PreOffSeconds must be shorter than
    # VacancyTimeoutSeconds.
    PreOffSeconds: 30
    PreOffBrightnessFactor: 0.5

    # "Manual changes hold for": how long somebody's own setting is left alone.
    OverrideDurationMinutes: 120

    # "After switching off by hand, wait": how long a room must be still after
    # somebody switches the lights off before movement can turn them back on.
    VacancyResetMinutes: 10

    # How a room decides it's dark. The UI calls Lux "Sensor".
    #   Lux    — the room's light-level sensor and nothing else, and the
    #            default. A room with no sensor, or whose sensors have all
    #            stopped reporting, counts as dark and lights on movement: a
    #            gate with nothing to read holds nothing back, and a flat
    #            battery is no reason to leave a house unlit. The sun is never
    #            consulted here.
    #   Sun    — sun elevation only. No sensor is read, so a room with none is
    #            unaffected by having none.
    #   Always — no daylight gate, for rooms with no windows.
    Darkness: Lux
    LuxThreshold: 1000          # "Dark below". A daylight number: the reading is
                                # usually an outdoor sensor.
    LuxHysteresis: 10           # "Bright again above": extra light needed to
                                # count as bright again.
    SunElevationThreshold: 3.0  # "Dark when the sun is below", in degrees
    SunEntity: sun.sun

    # Brightening with daylight. Off by default; it only ever adds light, and
    # the active period's own cap still binds.
    LuxBrightnessEnabled: false
    LuxBrightnessStartLux: 100      # at or below this, the schedule is used unchanged
    LuxBrightnessFullLux: 10000     # at or above this, the room holds its ceiling
    LuxBrightnessMaxPct: 100        # the brightness it is raised toward
    LuxBrightnessGamma: 1.0         # 1 rises steadily; above 1 holds back

    # Fades. Long at night because eyes are dark-adapted, snappy by day.
    DayTransitionSeconds: 1
    NightTransitionSeconds: 15

    # Room behaviour — off by default, opted into per room.
    RespectSleepMode: false    # "Gentle while the house sleeps"
    SleepBlocksAutoOn: false   # "Never comes on by itself while the house sleeps"
    SkipAwaySweep: false       # "Stays on when everyone leaves"
    WelcomeHome: false         # lights on first arrival, when it's dark

    # A switched-off room is still watched and published, never commanded. The
    # UI does not offer this as a default: switching a room on or off is a
    # decision made on that room, and flipping it here would silently flip every
    # room that never wrote a value of its own.
    Enabled: true

  # --------------------------------------------------------------------------
  #  Periods — the day, house-wide. A period runs from its Start until the next
  #  one begins. Start is a clock time ("06:30") or a sun event with an optional
  #  offset ("sunrise", "sunset-01:00", "sunrise+00:45"). Quote clock times:
  #  bare 06:30 is not a string in YAML.
  #
  #  Sun-anchored boundaries move with the seasons. Far north they swing hard,
  #  and around midsummer or midwinter one can be unresolvable — a period that
  #  cannot be placed is skipped, so keep at least one clock boundary.
  # --------------------------------------------------------------------------
  Periods:

    - Name: morning
      Start: "06:30"
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
      # BrightnessPct above is the whole answer for this period — 15 % is what
      # a room runs at night, and nobody gets 100 % in the face at 03:00
      # because the night row says 15, not because a second number forbids it.
      # SetsMode switches the house to this mode option when the period starts.
      # SetsMode: Sover

  # --------------------------------------------------------------------------
  #  Areas — opt-in. An area that is not listed here is never touched, and a
  #  listed room with Enabled: false is watched but never commanded.
  #
  #  In the common case a room declares an AreaId and nothing else: its lights,
  #  motion sensors and light-level sensors are found from the area registry.
  #  Discovery prefers groups over their members, treats several entities on one
  #  device as one fixture, and drops anything carrying the exclude label. The
  #  explicit lists below are for when Home Assistant's area assignments are
  #  wrong; each replaces discovery for that slot alone and bypasses the labels.
  #
  #  AreaId is the registry area *id* (the slug, "stue"), not the display name.
  #  Leave Name out and the room is called whatever Home Assistant calls the
  #  area, so a rename over there arrives here.
  # --------------------------------------------------------------------------
  Areas:

    # 1. The common case: discovery does everything.
    - AreaId: stue
      Enabled: true
      RespectSleepMode: true

    # 2. No daylight, a long timeout, and something that blocks auto-on while it
    #    is on — a projector, a do-not-disturb flag.
    - AreaId: kjeller_multimedia
      Enabled: true
      Darkness: Always
      VacancyTimeoutSeconds: 1800
      IgnoreWhenOn:
        - binary_sensor.projektor_er_pa

    # 3. An entrance: short timeout, lights on first arrival when it's dark.
    - AreaId: gang
      Enabled: true
      WelcomeHome: true
      VacancyTimeoutSeconds: 120

    # 4. A bedroom, with an explicit override: the mmWave sensor is not in the
    #    area, so MotionSensors replaces motion discovery here. Lights and
    #    light-level sensors are still discovered — only the listed slot is
    #    replaced.
    - Name: Bedroom          # a name of your own, when you want one
      AreaId: soverom
      Enabled: true
      RespectSleepMode: true
      SleepBlocksAutoOn: true
      MotionSensors:
        - binary_sensor.soverom_mmwave_presence

    # 5. A hallway with no sensor of its own that follows the weather: it gates
    #    on the sun, and reads the house's outdoor sensor to lift its brightness
    #    on a bright day. Naming Sun is the point of the entry — left at the
    #    default a room with no sensor is simply always dark. FollowOutdoorLux
    #    is file-only.
    - AreaId: kjellergang
      Enabled: true
      Darkness: Sun
      FollowOutdoorLux: true
      LuxBrightnessEnabled: true

    # 6. Outdoors: opts out of the leaving sweep, gated on the sun, and fully
    #    explicit — no discovery is used for a slot you fill in. Found by set-up
    #    but not switched on, so nothing out here is commanded. ExcludeEntities
    #    keeps one entity out of discovery without listing every other by hand.
    - AreaId: ute
      Enabled: false
      SkipAwaySweep: true
      Darkness: Sun
      SunElevationThreshold: 1.0
      Lights:
        - light.outdoor_front
      ExcludeEntities:
        - sensor.vaerstasjon_illuminance
```

## Shapes worth knowing

**A whole-house catch-all is not a room.** An area holding one "all lights" group and one "indoor
motion" sensor covers the kitchen and the living room at once, so a room there would light half the
house on any movement and fight the real rooms. Leave it out. Set-up will not propose it unless it has
a motion sensor of its own; if it does, switch it off and leave it off.

**A room needs both halves.** An area with motion but no lights has nothing to offer, and one with
lights but no way to sense people cannot do motion-driven lighting. Set-up skips both. Add them when
they gain the missing half.

**Explicit lists are for when the registry is wrong.** A room whose area also holds three unavailable
test rigs is a good reason to list the real lights by hand. Everything else is better left to
discovery, which stays true across a rename. Fixing the area assignment in Home Assistant is usually
better than either.

## What refuses to save

Some problems stop the engine commanding anything and are listed on the Configuration page: no
periods, a `Start` that cannot be parsed, two periods with the same name or the same start, duplicate
room names, negative timeouts, a warning dim longer than the time the lights stay on, a value out of
range.

Others only cost you one room: an `AreaId` that is not in the registry, an entity id Home Assistant
does not know, a room that resolves no lights or no motion sensors. That room is skipped, the rest
keep running, and the message names the real ids.
