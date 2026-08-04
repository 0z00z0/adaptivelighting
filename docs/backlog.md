# Backlog

Open work, one line each with enough context to act on without the conversation that produced it.

Reconstructed on 2026-08-04 from the changelog, the docs, memory and a session's history, after a 50-item
list that lived only in a session was lost. **That is why this file exists**: a backlog that is not in the
repository is not a backlog. Items marked *(thin)* survived only as a title; their detail is gone.

Finished work belongs in `CHANGELOG.md`, how the system behaves in `docs/mechanisms.md`, live facts about
what is deployed in `NetDaemon/CLAUDE.md`. Nothing here duplicates those.

---

## Next up

- **House mode page gets the Schedule's mapping table.** One row per live helper option with the
  rename/orphan handling. Unblocked: the ids landed and are deployed. **Decided 2026-08-04: the "take its
  list" adopt button goes.** The table shows a row per live option regardless, so no option becomes
  unreachable; you tag each option's `Kind` rather than adopting a list and editing it. Same reasoning the
  Schedule's own picker already uses — adopting invents mappings nobody chose.

## Known defects, found by review and not yet fixed

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

- **Per-period ceilings stay cut. Decided 2026-08-04.** A night period asking 15 % in a room reading 1000 lx
  now lands near 58 %, and only sleep mode clamps it. To bite, a room needs its sensor reading ~1000 lx while
  it is dark, which means the sensor is reading that room's own lamps — a placement problem a brightness cap
  would hide rather than fix. `LuxBrightnessMaxPct` is one number and would also cap the bright-afternoon
  lift the setting exists for.
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
- **The user guide has no screenshots.** Every `📷 [screenshot: …]` slot is still a placeholder.
- **`SceneHold` is missing from the architecture state diagram.** *(thin)*
- **Section icons in the 0z0 design language are not wired into the UI.** *(thin)*

## House-specific

- **Four dangling level rows on B1**, measured 2026-08-04 after the migration: `lab` names periods `morning`,
  `evening` and `night`, and `lab_t` names `night`. This house's periods are `Tidlig morgen`, `Morgen`, `Dag`,
  `Ettermiddag`, `Kveld`, `Natt` — those four names have never existed here, so the rows have always done
  nothing. `lab` is disabled, so only `lab_t`'s `night: 30 %` is live. Decide whether it meant `Natt`, then
  fix it on the room page; the rest can go.
- **Eight B1 rooms have a lux sensor that is not reporting** — `bad`, `soverom-gang`, `kjeller_multimedia`,
  `kjokken` (both of its two), `kontor`, `vaskerom`, `trening`, `inngang`. Each warns on every start and each
  counts as dark meanwhile, so movement lights it. Dead batteries or a Zigbee dropout, not a config fault.
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
