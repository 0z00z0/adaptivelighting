# Backlog

**GitHub Issues is the source of truth for open work in this repository** —
https://github.com/0z00z0/adaptivelighting/issues. This file is a mirror of it, kept so the backlog can
be read beside the code; each item names the issue that holds it. Where the two disagree, the tracker
wins, and a new item is opened there first.

Each item carries the context needed to act on it — file names, type names, measured values — without
the conversation that produced it. The issue body carries the same.

Finished work belongs in `CHANGELOG.md`, how the system behaves in `docs/mechanisms.md`. Nothing here
duplicates those.

Three sections: **queued**, **parked**, and **open questions**. An item moves between them; it is not
rewritten to suit one. On the tracker the same three are the `parked` label, the `question` label, and
no label at all for what is queued.

A **struck** item is done and waiting to be closed on the tracker. Its body is replaced by the release
that carried it, because the detail under a finished item describes behaviour that no longer exists.
An item with no number is one this file records before the tracker has minted one.

---

## Next up

- **Naming who changed a light is unmeasured on a real house.** `ChangeOriginNames` names people from the
  `person` entities' `user_id` attribute and automations from `automation_triggered` events. Neither was observed
  on the wire: whether the add-on's Supervisor token receives `automation_triggered`, reads `user_id` on a person
  entity, and sees a light's report keep its parent context rests on Home Assistant's source and documentation.
  The check: in a room nobody is using, run an automation that sets a light and read the activity row, then tap
  the same light from a dashboard and read it again. A light driven through Z-Wave JS is expected to read "At the
  device or wall switch" for the automation, for the reason in `docs/mechanisms.md`.

- **A room set to ignore automations keeps a light an automation switched on while the room was empty.** An
  ignored change in `AutoVacant` arms no countdown, so the light burns until movement or a hand ends it. A lead-in
  replaces the automation that causes this for a hall; a house giving a room *No* under *Other automations count
  as manual changes* should switch off any automation that only lights that room.

- #28 **The UI host seeds no activity, so the Activity page cannot be looked at.** Driving it means hand-editing
  `tools/uihost/Program.cs` to seed reports and reverting afterwards. A dozen seeded reports spread across the
  categories would make the page drivable as shipped.

- #39 **The UI host never attaches the engine, so the commissioning board cannot be looked at
  as shipped.** `tools/uihost` raises area events but starts no engine, and the board reads what the engine
  publishes. Driving it means hand-patching `tools/uihost/Program.cs` to call `Attach` and reverting
  afterwards. Same shape as #28, and the two are probably one job.

- #75 **The activity log's "newest first" heading is ambiguous.** "First" names no position; the heading
  should read "newest at top".

- #76 **Motion went unrecorded at Petterhaugen for a twenty-minute window.** Movement in Tilbygg between
  07:16 and 07:36 did not register, against both a person's own account and the Home Assistant history log
  for the same window.

- #77 **An away-mode reason names the wrong cause.** House mode was forced to `Borte` (away) because the
  house went quiet, and the shown reason says so — but nobody had left, so the text describes a
  quiet-house heuristic as if it were a departure.

- #78 **Area settings render with text pre-selected in blue.** The screen reads as broken rather than as a
  settings page.

- #79 **Room settings mislabel what the toggle controls, throughout the application.** The control reads
  "Enable Room" where it is adaptive lighting being enabled *for* the room, not the room itself; the on/off
  label beside it is also misaligned and reads as more technical than the setting needs.

- #80 **NetDaemon 26.36.0 introduces `ICurrentApp`; evaluate whether it is useful here.** No adoption is
  proposed yet — the item is to read the release and decide whether it replaces anything in the current
  app model.

- #81 **Some rooms disappear from the board with no explanation.** A room that is not shown should either
  show, or carry a stated reason it is unavailable, rather than vanishing silently.

- **Three controls still require the document, the area or the periods, which is why `RoomPageModel` exposes
  them.** `LevelsEditor` requires the periods, the area and the defaults, `SetupAgainPanel` requires the whole
  document, and `AreaSentences.ForArea` takes the area and the defaults. Every mutation already goes through a
  named model method, so no markup assigns to the document; closing the read side is the remaining half of the
  rule that a control takes what it draws.

- **A red-first check can rest on a fixture that pins nothing.** The refactor plan's verification for package
  4.1 named four expected events pinning the word "Home" in `HaStatePublisherTests`. They are input fixtures
  for the read-back path, so mapping the everyday kind to another word left all twelve tests green; nothing
  covered the outgoing word until that package added a test for it. Worth checking whether other packages'
  red-first checks rest on the same assumption.

- **`StateStoreRegistry` is not wired into `LightingEngineHost` or `LightingOrchestrator`.** Three of the four
  engine-owned state files each open their own write path instead of sharing one registry on the engine's own
  scheduler: `AreaSetupMemoryStore` and `LastPeriodStore` accept an optional registry but the host still builds
  them without one, and `RoomHistoryStore` and `ActivityJournalStore` each construct a private
  `StateStoreRegistry` instance of their own. On a house this means the notes and the cache get the write
  hardening (flush before rename, backup read, future-dated refusal) but not the shared flusher's retry or one
  start-up report naming every store. Wiring it in needs one registry built with the engine's scheduler, passed
  to `AreaSetupMemoryStore`, `LastPeriodStore` and `LastSeenStore`, a start-up report call after the first load,
  and disposal on shutdown.

- **The activity journal holds its own registry instance rather than the shared one.** `ActivityJournalStore`
  lives in the web layer, a separate assembly the registry's internal types cannot cross into, so it cannot take
  the engine host's registry even once one exists. Only `IActivityJournalStore` crosses the assembly boundary.

- **The level row on the room page is still reset by a settings save.** The save carry-over hands on history and
  a manual hold; a running level test, pre-off, lead-in and the level row all start afresh, by design of package
  5.6, but a person mid-test loses it on every save.

- **A battery added to a device in Home Assistant is seen only after the next save or restart.** The battery
  lookup runs once when a room's controller is built, on the shared state stream from then on; it does not
  notice a battery entity that appears on a device later.

- **The engine's own-user timestamp match is not measured against a real house.** `OwnUser` matches the learned
  user id from the first `adaptive_lighting_area` event whose timestamp round-trips through the same
  `System.Text.Json` serialisation the test uses; whether NetDaemon's own event serialisation and Home
  Assistant's echo keep that timestamp comparable over the real path has not been checked on a house.

- **A room removed from the document keeps its entry in the room-history note indefinitely.** Nothing prunes
  it. Harmless — nothing reads a key that is not also in the current document — but grows the file slowly on a
  house that renames or deletes rooms often.

## Parked

- #30 **The daylight chart is only 101 px tall on a phone, which caps its labels.** The corner and the label spread
  were fixed and took the cap from 13 user units to 15, or 6.3 real pixels. The 10.1 the formula asks for needs
  a 24-unit cap and a `MinGap` near 34, which puts five gaps into that 101 px: the labels would cover the chart
  they annotate, and the desktop would carry the same spread for type a third of the size. Reaching it means a
  taller chart on a narrow container, or no period labels on the drawing at all — a design question, not a
  defect.

- #31 **The user guide has no screenshots.** Every `📷 [screenshot: …]` slot is still a placeholder.

- #33 **The four packages are private, and only an organisation owner can change that.** The organisation blocks
  public package creation, so every publish lands private. No token or script reaches it: the package API
  offers `GET`, `DELETE` and `restore`, and nothing that sets visibility. The fix is *Organization settings →
  Packages → Package Creation → enable Public*, then each package set public individually. Houses are
  unaffected — they authenticate and restore as now. What is blocked is an outside consumer of an MIT-licensed
  project.

## Open questions

- #67 **Presence today is one boolean, trusted completely: whatever `person`/`device_tracker`
  entities last reported.** `PresenceMonitor` folds every watched entity into a single
  `IsAnyoneHome`, and `HouseState` turns that straight into `HouseMode.Away` with nothing else
  consulted. A stale tracker, a location update Home Assistant never receives, or a phone left
  behind all read as "nobody home," and today that verdict refuses a room's own manual **Light
  on** button as well as motion-triggered auto-on, with no way out from inside the building.
  Separate work makes movement count as presence and removes that specific lock-out; what remains
  is which signals should decide presence at all — how reliable phone tracking really is, whether
  Bermuda BLE room-level presence belongs in the mix, how sources should combine when they
  disagree, and whether any one of them should be able to declare the house empty alone. No
  implementation is proposed; the question needs answering first.
