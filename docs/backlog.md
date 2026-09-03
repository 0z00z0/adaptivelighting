# Backlog

Open work only. Each item carries the context needed to act on it — file names, type names, measured values —
without the conversation that produced it.

Finished work belongs in `CHANGELOG.md`, how the system behaves in `docs/mechanisms.md`. Nothing here
duplicates those.

Three sections: **queued**, **parked**, and **open questions**. An item moves between them; it is not
rewritten to suit one.

---

## Next up

- **The room page scrolls sideways at 390 px.** With a settings group open, `scrollWidth` measures 531 against
  an `innerWidth` of 390. The warmth `.seg` control is 474 px wide and `.srow-control` is `flex: 0 0 auto`
  (`app.css:4638-4644`), so the row never wraps; the narrow-viewport relaxation at `app.css:4855` covers
  `.steps` only. Pre-existing and independent of the stepped rows — hiding those leaves the number unchanged.

- **Every info text moves behind the ⓘ button, leaving the label alone.** The settings pages are too long to
  read, and worst on a phone where everything stacks into one column. A row becomes label + control, and all
  prose — description and help alike — lives behind the button. Needs a level inventory first: heading,
  description, help text, a control's label, and sub-control text (whether that last level exists is part of
  the question). One shared stylesheet for the levels, not per-page styling.

- **`Components/NullableNumber.razor` is referenced by nothing, and is now the only live consumer of
  `Components/PresetSelect.razor`.** Both were left in place, with their six tests, because removing them
  drops the test count — a decision to take deliberately rather than in passing.

- **Automatic room discovery still proposes only rooms that have both lights and a movement sensor.** A room
  with lights and no sensor now resolves and runs, but discovery does not offer it, so it has to be added by
  hand. Whether discovery should propose such rooms is a separate decision.

- **Three group-recursion guards in the entity resolver fail on their own wall clock under load.**
  `A_Light_Group_That_Contains_Itself…`, `A_Motion_Group_That_Contains_Itself…` and
  `Illuminance_Groups_Nest_Overlap…` carry `[Timeout(10000)]` and go red on that timeout, never on an
  assertion, during a loaded full run; alone they finish in under a second, and a quiet full run passes in
  21 s against a loaded run's 39 s. Observed three times in one day on untouched code. A wall-clock timeout is
  the one kind of test that can go red with nothing wrong, and CI runs on a shared runner, so this can turn
  `main` red for no reason. The guard is worth keeping — it exists to stop an infinite loop hanging the
  suite — but it wants a bound that is not the wall clock, or a much larger one.

- **Not every filter button in the Activity log works.** Unchecking one leaves its events in the list.

- **A room page's Test countdown is drawn locally, so it does not survive leaving the page.** Navigating away
  and back within the ten seconds shows plain Test buttons while the engine's return is still pending. The
  return happens correctly — it is scheduled on the engine's scheduler, not the page's — and only the drawing
  is lost. `AreaController.IsTestingLevels` already exists if it is ever worth surfacing through a snapshot.

- **A `LightCommand` carries brightness and colour temperature but no colour channels.** In a room commanded
  at equal channels — RGB-only fixtures with no colour temperature — a hand-set colour comes back as neutral
  white after a period test, because `AreaController.CaptureLights` can read back nothing that says what the
  colour was. Such a room is whitened by any ordinary engine command anyway, so the test makes nothing worse,
  and the person pressing Test owns the colour. Closing it means giving `LightCommand` an optional channel
  vector and teaching `HaLightActuator` to send and compare it.

- **The "Don't switch on while" help text renders `&amp;quot;` literally** instead of the quotation marks it
  stands for, so a reader sees the HTML escape. The neighbouring "Don't switch off while" row carries the same
  text. Pre-existing and unrelated to the settings rows around it.

- **The blend starts when the period actually begins.** A period whose `Start` was 06:30 but which movement
  began at 06:45 arrives already part-way through its blend, because the window trails the boundary and the
  boundary is still 06:30 (`mechanisms.md`, *A period that waits for movement*). It should ease from the moment
  somebody walked in. **The blend keeps its full configured length and therefore finishes later than a
  clock-started one would**, so the transition feels the same whenever you arrive. Needs the calculator to know
  *when* a period began, not just whether.

- **`PeriodsAcross` has no direct test.** `CircadianCalculator.PeriodsAcross` is exercised only through the
  web schedule and board views, never on its own. It is now the reference rule for two paths — the per-day
  table behind `NextBoundary` was brought onto it — so a change to it can break boundary resolution with
  nothing failing that names it.

- **The UI host seeds no activity, so the Activity page cannot be looked at.** Driving it means hand-editing
  `tools/uihost/Program.cs` to seed reports and reverting afterwards. A dozen seeded reports spread across the
  categories would make the page drivable as shipped.

- **`tools/uihost` hard-codes port 5199, so two worktrees cannot run it at once.** Parallel efforts collide on
  it; reading a port from the command line would let each look at its own.

- **Adopt GitHub Issues as this repo's tracker.** `0z00z0/adaptivelighting` currently has zero open
  issues — this file is the only tracker in use. Per the *Tracker* section of
  `Nextcloud\Projects\CLAUDE.md`, once a project has an issue tracker it is the source of truth and
  this file becomes a mirror in its existing taxonomy and line format, not a second authority.
  Read-only by default: creating, closing, editing or commenting on an issue needs explicit
  approval, as does any push, unless standing approval is recorded here.

## Parked

- **The daylight chart is only 101 px tall on a phone, which caps its labels.** The corner and the label spread
  were fixed and took the cap from 13 user units to 15, or 6.3 real pixels. The 10.1 the formula asks for needs
  a 24-unit cap and a `MinGap` near 34, which puts five gaps into that 101 px: the labels would cover the chart
  they annotate, and the desktop would carry the same spread for type a third of the size. Reaching it means a
  taller chart on a narrow container, or no period labels on the drawing at all — a design question, not a
  defect.

- **The user guide has no screenshots.** Every `📷 [screenshot: …]` slot is still a placeholder.

- **The first-run wizard is undocumented.** The user guide covers every other screen; the wizard ships without a section.

- **The four packages are private, and only an organisation owner can change that.** The organisation blocks
  public package creation, so every publish lands private. No token or script reaches it: the package API
  offers `GET`, `DELETE` and `restore`, and nothing that sets visibility. The fix is *Organization settings →
  Packages → Package Creation → enable Public*, then each package set public individually. Houses are
  unaffected — they authenticate and restore as now. What is blocked is an outside consumer of an MIT-licensed
  project.

## Open questions

- **A boundary into or out of a curve period is a step, not a blend.** The blend interpolates the two
  periods' stored levels and the daylight curve then replaces the result, so a boundary with the curve on
  one side and a stated percentage on the other changes level in a single move. Two adjacent periods both
  on the curve have no step, and neither do two that both state a percentage. Removing the step means
  moving the curve inside `CircadianCalculator`, which breaks the composition order
  `AreaController.ResolveTarget` holds — period, then curve, then sleep clamp (`docs/mechanisms.md`,
  *Order of composition*). The step is the current behaviour.

- **The durable log's retention is a byte budget, not a time budget.** One active 10 MiB generation plus one
  rotated copy, so the directory never exceeds 20 MiB. At the 111 kB/h measured on a live house a generation
  fills in about 94 hours and the pair holds between four and eight days, which clears the 24 hours intended
  with room to spare. The window still moves with room count and event traffic rather than with the clock, so
  a much chattier house retains proportionally less. A time-bounded budget would make it predictable; the
  larger cap makes it comfortable.

- **`StartsOnMotionAreas` is written into every period on save** as `[]`, because it is a non-nullable
  `List<string>`. `ConfigNormalizer.cs:76` clears it for a period that does not wait for movement and the
  serialiser writes it out regardless, so every period in every document carries the key — including in a house
  that never adopted the feature. The cost is noise in a file a person reads, nothing behavioural. Making it
  nullable would keep the key out; the load path already repairs null to an empty list
  (`LightingConfigDocument.cs:285`), so nothing downstream would notice.

- **`CommissioningVerdicts.NearMiss` writes an unbounded room list** where `HouseView.NameList` caps at three
  and falls back to "Stue, Kjøkken and 15 others". The line names the rooms that have lights but nothing that
  senses movement, so in a house where many rooms lack a sensor it becomes one sentence holding every name.
  The tension runs both ways: adopting the cap shortens the line but hides exactly the list somebody needs in
  order to go and fix those rooms.
