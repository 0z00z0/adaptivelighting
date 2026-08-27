# Backlog

Open work only. Each item carries the context needed to act on it — file names, type names, measured values —
without the conversation that produced it.

Finished work belongs in `CHANGELOG.md`, how the system behaves in `docs/mechanisms.md`. Nothing here
duplicates those.

Three sections: **queued**, **parked**, and **open questions**. An item moves between them; it is not
rewritten to suit one.

---

## Next up

- **`ResolveBoundaries` places the hold against the instant, not against the day the boundary falls on.**
  `CircadianCalculator.cs:364` derives the instance day from `now` alone, so a boundary still ahead of the
  current time is tested against *yesterday's* hold. `NextBoundary` (`CircadianCalculator.cs:313`) reads that
  list, so a period whose today instance is still waiting for movement can be handed back as the next boundary,
  while one whose yesterday instance was held back drops out of it. `PeriodsAcross`
  (`CircadianCalculator.cs:276-283`) already gives each day its own instance and is the rule to bring the
  instant-shaped path onto. A latent inconsistency in the engine, not in the band that exposed it; it belongs
  in its own change.

- **The room page scrolls sideways at 390 px.** With a settings group open, `scrollWidth` measures 531 against
  an `innerWidth` of 390. The warmth `.seg` control is 474 px wide and `.srow-control` is `flex: 0 0 auto`
  (`app.css:4638-4644`), so the row never wraps; the narrow-viewport relaxation at `app.css:4855` covers
  `.steps` only. Pre-existing and independent of the stepped rows — hiding those leaves the number unchanged.

- **Every info text moves behind the ⓘ button, leaving the label alone.** The settings pages are too long to
  read, and worst on a phone where everything stacks into one column. A row becomes label + control, and all
  prose — description and help alike — lives behind the button. Needs a level inventory first: heading,
  description, help text, a control's label, and sub-control text (whether that last level exists is part of
  the question). One shared stylesheet for the levels, not per-page styling.

- **Brightness and warmth become stepped sliders, laid out as a table.** A slider with fixed steps is easier
  to set than a dropdown or a continuous control. Brightness shows a *house default* marker and the percentage
  only, with no per-step wording; warmth shows the same marker and keeps the wording each step already has in
  its dropdown. The two dropdown steps that carry no wording are granularity nobody uses — drop them. The
  words *Brightness* and *Warmth* are not repeated per period; the periods read as rows:

  | Period | Brightness | Warmth |
  | --- | --- | --- |

- **A retired configuration key reaches only the log, never the browser.** `RetiredKeys`
  (`LightingConfigDocument.cs:88`) already carries a sentence per key and logs it on every load
  (`LightingConfigDocument.cs:357`), but nobody watching the browser sees it. `ConfigValidator`
  already carries a document-wide warning list read by the UI, on the same pattern used for a
  daylight-curve period with no sensor to read (`ConfigValidator.cs:185`). Loading a document that
  still holds a retired key should add one of those warnings, using the sentence `RetiredKeys`
  already has — no new text, no new mechanism, just wiring what is already detected into what is
  already shown. Generic to every key in the table and every house, not the one presently in use.

- **The blend starts when the period actually begins.** A period whose `Start` was 06:30 but which movement
  began at 06:45 arrives already part-way through its blend, because the window trails the boundary and the
  boundary is still 06:30 (`mechanisms.md`, *A period that waits for movement*). It should ease from the moment
  somebody walked in. **The blend keeps its full configured length and therefore finishes later than a
  clock-started one would**, so the transition feels the same whenever you arrive. Needs the calculator to know
  *when* a period began, not just whether.

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
