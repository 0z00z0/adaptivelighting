# Backlog

Open work, one line each with enough context to act on without the conversation that produced it.

Reconstructed on 2026-08-04 from the changelog, the docs, memory and a session's history, after a 50-item
list that lived only in a session was lost. **That is why this file exists**: a backlog that is not in the
repository is not a backlog. Items marked *(thin)* survived only as a title; their detail is gone.

Finished work belongs in `CHANGELOG.md`, how the system behaves in `docs/mechanisms.md`, live facts about
what is deployed in `NetDaemon/CLAUDE.md`. Nothing here duplicates those.

---

## Next up

- **Schedule to the next boundary instead of polling every `CircadianTickSeconds`.** B1 runs a 300 s tick, so a
  period arrives 0–5 minutes late; measured 2026-08-05, the six boundaries landed +0 to +4 min. Home Assistant
  has no wall-clock event worth subscribing to — its time triggers live in HA automations, which would move the
  schedule out of the engine — so the fix is `IScheduler.Schedule(nextBoundary)` and a reschedule whenever the
  sun times move or the document is saved. **A timer does not survive a restart and does not need to**: start-up
  already resolves the active period from the clock, `LastPeriodStore` seeds what has already fired, and
  `ModeMonitor.BoundaryWentByWhileDown` covers a boundary crossed while the engine was down. Keep the tick as a
  slow safety net rather than deleting it.
- **A 24-hour circular log beside the config, surviving restarts.** `/config/adaptive-lighting/` rather than the
  deploy folder, which is wiped on every deploy. The need is proven: the midnight incident on 2026-08-05 could
  not be read from the add-on log at all — Supervisor's buffer had been overwritten by two restarts, and the
  diagnosis had to come from Home Assistant's recorder instead. Wants a size cap and a rotation that cannot
  itself fill `/config`, and it must never write a token or a Samba credential.

## Known defects, found by review and not yet fixed

- **The daylight chart's bottom-right corner holds two label families.** The December month label
  (`.daylight-axis`, y=236) and the last period label (`.daylight-period-label`, right-anchored at x=726)
  overlap once either grows. Measured 2026-08-06 at 390 px: clean to 13 user units, colliding at 14, which is
  what caps the responsive label sizing at 5.5 real pixels instead of the 10.1 the formula asks for. Fixing
  the corner — shifting the month row, or dropping the last month label when a period label shares its band —
  is what unlocks the rest. Separately, two period boundaries closer than about 11 user units overlap at
  today's size too, and always have.
- **`ModeService.ComputePreview` builds a calculator with no hold predicate**, so the settings-page preview
  shows the clock's period for a held-back period while the house is still on the previous one. Harder than it
  looks and deliberately left alone on 2026-08-05: the preview is `static` and has no access to the engine's
  `MotionPeriodLatch`, and a fresh latch would answer "not begun" for every held period, which is wrong in the
  other direction. It needs the host to hand its latch over, or the preview to ask the engine for the period it
  already resolved rather than recomputing one.
- **`AreaSnapshot.SceneApplied` reaches Home Assistant but no page reads it.** Verified 2026-08-06: it is set
  by the engine, compared in the snapshot's own equality, written out as `scene_applied`, and carried through
  `AreaSnapshotEvent` — and no `.razor` file mentions it. A room sitting on its own `SceneOnMotion` or
  `SceneWhenEmpty` scene therefore never names which scene. Same shape as the `IsHeldLit` gap closed the same
  day, and the same fix: one more row in `RoomFacts.For`.

## Decisions taken on 2026-08-06, recorded so they are not relitigated

- **A mode option's fold stays below the helper panel, not inside its `Body`.** The two helper screens are
  therefore not visually identical, and that is the answer, not a gap. A period row is one dropdown whose whole
  meaning is "this option maps to this period", so it belongs beside the picker; a mode fold is a full editor
  (kind, scene, clamp, activation, resets) and is the page's main content. Nesting six of them under the picker
  demotes what a person actually comes to read.
- **Adoption is gone entirely — the button and the silent adopt on picking a helper.** The 2026-08-04 decision
  covered only the button; the pick path was still rewriting the list, which was the one edit that could destroy
  an option's settings. The cards render a row per live option regardless, so nothing became unreachable.
  `HouseModeSync` now reports drift and never proposes.
- **The chart labels are sized in pure CSS**, `tan(atan2())` against a container query, not a `ResizeObserver`.
  No interop, no script to fail to load, and `@supports` leaves an old browser on exactly today's behaviour.

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
- **Section icons in the 0z0 design language are not wired into the UI.** *(thin)*

## House-specific

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
