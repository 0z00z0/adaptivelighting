# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html). The three packages —
`AdaptiveLighting`, `AdaptiveLighting.Web` and `AdaptiveLighting.Extensions` — ship as a matched set
under one version, because they are compiled against each other.

## [Unreleased]

### Added

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

### Changed

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
