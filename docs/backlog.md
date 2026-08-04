# Backlog

Open work, one line each with enough context to act on without the conversation that produced it.

Reconstructed on 2026-08-03 from the changelog, the docs, memory and a session's history, after a 50-item
list that lived only in a session was lost. **That is why this file exists**: a backlog that is not in the
repository is not a backlog. Items marked *(thin)* survived only as a title; their detail is gone.

Finished work belongs in `CHANGELOG.md`, how the system behaves in `docs/mechanisms.md`, live facts about
what is deployed in `NetDaemon/CLAUDE.md`. Nothing here duplicates those.

---

## In flight

- **Stable ids for periods and modes** — `refactor/stable-keys`. A period's user-typed `Name` is the key every
  reference uses, so renaming silently breaks whatever pointed at it. Moves `RoomLevelOverride.Period`,
  `ClampPeriod`, `ResetOnPeriodStart`, `PeriodSelectOptionConfig.Period` and `LastPeriodStore` onto ids.
  `HouseModeOptionConfig.Value` must stay the match key against Home Assistant's `input_select`. The risky
  half is a load-path migration that must run exactly once: the store keeps one backup slot, so a second pass
  would overwrite the only pre-migration copy.
  - **Copy `b1.yaml` off the box by hand before the deploy that carries this.** The migration rewrites it on
    load, and one backup slot is not enough if the first migrated load is itself wrong.
- **House mode page gets the Schedule's mapping table** — blocked on the ids work, because an id changes what
  a mapping row points at. One row per live helper option with the rename/orphan handling. **The "take its
  list" adopt button goes away**; if that button is wanted, say so before the page is rebuilt.

## Known defects, found by review and not yet fixed

- **The House tab's warmth buttons do nothing.** `ConfigEditor.SetHouseChoice` ignores its `key` and always
  parses into `DarknessSource`, so picking a warmth on the House tab silently writes nothing. Per-room warmth
  works.
- **`HaLightActuator.AlreadyMatches` reads an absent colour attribute as a match.** Home Assistant only
  publishes `rgbw_color`/`rgbww_color` while the fixture is in that colour mode, so an equal-channels room
  sitting in colour-temp mode is judged already-correct and never commanded. Three reviews found it.
- **`ModeService.ComputePreview` builds a calculator with no hold predicate**, so the settings-page preview
  shows the clock's period for a held-back period while the house is still on the previous one.
- **`ModeService.GetHouseMode` still promises reset behaviour under Home Assistant mode authority.** The
  engine stands those rules down; the dashboard card still describes them as live.
- **Six chart labels are `9px`/`10px` in SVG user units, not CSS pixels.** `.daylight-axis`,
  `.daylight-period-label`, `.daylight-mark-label`, `.luxc-axis`, `.luxc-decade`, `.luxc-now-label`. The
  daylight chart's viewBox is 730 wide at `width: 100%`, so on a 390 px phone it scales by about 0.48 and 9px
  renders near 4 px. Raising the number is not the fix — the decade labels already ran together at 390 px
  once. Needs the size to come from the rendered width, and a look at both widths afterwards.

## Product decisions still open

- **B1's config still sets `MaxBrightnessPct`.** Per-period ceilings were cut, so the key does nothing: the
  sleep clamp holds the night period at its `BrightnessPct` of 15 where the ceiling used to hold it at 30, and
  the daylight curve lost its only night cap. Remove the key, or lower `LuxBrightnessMaxPct` on rooms that
  brighten with daylight.
- **`Lab/T` is a Home Assistant area with lights and a motion sensor and is not in the lighting config.**
  Creating it took the motion sensor out of `Lab`, which now has lights and nothing that senses movement.
  *Set up rooms again* proposes it; automatic discovery will not, because `AreasAutoDiscovered` is one-way.
- **The blend does not move with a motion-started period.** A period whose `Start` was 06:30, begun by
  movement at 06:45, arrives already halfway through its blend rather than easing from the moment somebody
  walked in. Fixing it needs the calculator to know *when* a period began, not just whether.
- **`StartsOnMotionAreas` is written into every period on save** as `[]`, because it is non-nullable. Making
  it nullable would keep the key out of a document that never adopted the feature.
- **`CommissioningVerdicts.NearMiss` writes an unbounded room list** where `HouseView.NameList` caps at three.
  Adopting the cap changes what a seventeen-room house reads.

## Shipping and packaging

- **`NUGET_API_KEY` is not set** as a repo secret, so tagging `v*` cannot publish. Espen must add it.
- **Ship as its own Home Assistant add-on with ingress.** The only route to HA auth: `IHaContext` has no
  `SetState`, and `last_updated` cannot be written back at all.
- **The user guide has no screenshots**, and the website documents neither the motion-started period nor the
  two room scenes.
- **`SceneHold` is missing from the architecture state diagram.** *(thin)*
- **Section icons in the 0z0 design language are not wired into the UI.** *(thin)*

## House-specific

- **Set B1's lux-sensor rooms to 1000 lx.** *(thin — the reasoning behind 1000 is in `mechanisms.md`.)*
- **Junk light entities in `stue` and `kjokken`** should be swept using the include label. `light.gang_vegglys_opp`
  is the worked example: it overlaps `light.gang_vegglys` on three bulbs and contributes none of its own, so
  the group is dropped and nothing replaces it.

## Housekeeping

- **Two `Norwegian()` test helpers disagree** — `PeriodSelectReaderTests` returns four option rows,
  `ModeMonitorTests` three. Same name, different fixture.
- **`tests/Test1.cs`** looks like the `dotnet new mstest` template leftover.
- **A shared `SameName` / `ByName` matcher.** Twenty hand-written `Trim()` + `OrdinalIgnoreCase` comparisons
  and fifteen period-name lookups, each spelled out. Deferred once as too broad for an unrelated change.
- **`AreaSnapshot.IsHeldLit` / `HeldLitBy` are published and compared but no page reads either**, so a room
  that refuses to switch off has nothing on screen naming what is holding it.
