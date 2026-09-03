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

- #19 **`Components/NullableNumber.razor` is referenced by nothing, and is now the only live consumer of
  `Components/PresetSelect.razor`.** Both were left in place, with their six tests, because removing them
  drops the test count — a decision to take deliberately rather than in passing. The wider sweep this
  belongs to is #38.

- #23 **A room page's Test countdown is drawn locally, so it does not survive leaving the page.** Navigating
  away and back within the ten seconds shows plain Test buttons while the engine's return is still pending.
  The return happens correctly — it is scheduled on the engine's scheduler, not the page's — and only the
  drawing is lost. `AreaController.IsTestingLevels` already exists if it is ever worth surfacing through a
  snapshot.

- #24 **A `LightCommand` carries brightness and colour temperature but no colour channels.** In a room
  commanded at equal channels — RGB-only fixtures with no colour temperature — a hand-set colour comes back
  as neutral white after a period test, because `AreaController.CaptureLights` can read back nothing that
  says what the colour was. Such a room is whitened by any ordinary engine command anyway, so the test makes
  nothing worse, and the person pressing Test owns the colour. Closing it means giving `LightCommand` an
  optional channel vector and teaching `HaLightActuator` to send and compare it.

- #27 **`PeriodsAcross` has no direct test.** `CircadianCalculator.PeriodsAcross` is exercised only through the
  web schedule and board views, never on its own. It is now the reference rule for two paths — the per-day
  table behind `NextBoundary` was brought onto it — so a change to it can break boundary resolution with
  nothing failing that names it.

- #28 **The UI host seeds no activity, so the Activity page cannot be looked at.** Driving it means hand-editing
  `tools/uihost/Program.cs` to seed reports and reverting afterwards. A dozen seeded reports spread across the
  categories would make the page drivable as shipped.

- #29 **`tools/uihost` hard-codes port 5199, so two worktrees cannot run it at once.** Parallel efforts collide on
  it; reading a port from the command line would let each look at its own.

- #39 **The UI host never attaches the engine, so the commissioning board cannot be looked at
  as shipped.** `tools/uihost` raises area events but starts no engine, and the board reads what the engine
  publishes. Driving it means hand-patching `tools/uihost/Program.cs` to call `Attach` and reverting
  afterwards. Same shape as #28, and the two are probably one job.

- #40 **`ModePreview.PreviewBrightness` and `PreviewKelvin` are dead.** Both are populated from
  an unresolved target and asserted in eight tests, and no page renders either. The house-mode preview has no
  room, so there is no single daylight-curve answer to show even if a page wanted one — deleting them is the
  likely answer, and the eight tests go with them. Belongs to the #38 sweep.

- #41 **The group-recursion budget is opt-in per test.** `FakeHaContext.StateReadBudget` is
  unset by default, so a future test that builds a self-referencing group hangs the suite exactly as before
  unless it sets the budget itself. Making it default-on means every test paying for the counter and some
  legitimate walk tripping it, which is the trade to weigh.

- #42 **`comm-nearmiss` is a misnomer.** The commissioning board's two paragraphs still carry
  that class after the near-miss line was replaced (#37). Renaming touches `app.css` and the component
  together, and neither may move alone.

- #38 **The web project carries dead code.** `Components/NullableNumber.razor` and the `Components/PresetSelect.razor`
  it alone consumes are the first named instance (#19); the task is the sweep, not that one pair. Find what else
  is unreferenced, decide the whole set deliberately, and record the test count before and after — a falling
  count is only legitimate with a sentence saying why.

## Parked

- #30 **The daylight chart is only 101 px tall on a phone, which caps its labels.** The corner and the label spread
  were fixed and took the cap from 13 user units to 15, or 6.3 real pixels. The 10.1 the formula asks for needs
  a 24-unit cap and a `MinGap` near 34, which puts five gaps into that 101 px: the labels would cover the chart
  they annotate, and the desktop would carry the same spread for type a third of the size. Reaching it means a
  taller chart on a narrow container, or no period labels on the drawing at all — a design question, not a
  defect.

- #31 **The user guide has no screenshots.** Every `📷 [screenshot: …]` slot is still a placeholder.

- #32 **The first-run wizard is undocumented.** The user guide covers every other screen; the wizard ships without a section.

- #33 **The four packages are private, and only an organisation owner can change that.** The organisation blocks
  public package creation, so every publish lands private. No token or script reaches it: the package API
  offers `GET`, `DELETE` and `restore`, and nothing that sets visibility. The fix is *Organization settings →
  Packages → Package Creation → enable Public*, then each package set public individually. Houses are
  unaffected — they authenticate and restore as now. What is blocked is an outside consumer of an MIT-licensed
  project.

## Open questions

None open.
