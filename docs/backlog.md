# Backlog

Open work only. Each item carries the context needed to act on it — file names, type names, measured values —
without the conversation that produced it.

Finished work belongs in `CHANGELOG.md`, how the system behaves in `docs/mechanisms.md`. Nothing here
duplicates those.

Three sections: **queued**, **parked**, and **open questions**. An item moves between them; it is not
rewritten to suit one.

---

## Next up

- **The two night switches become one three-step control.** A room carries *is gentle while the house sleeps*
  and *never comes on by itself while the house sleeps* as two checkboxes, and the second implies the first —
  `AreaSentences.cs:238` already writes them with `else if`. Replace with one stepped choice: **normal → dims
  while the house sleeps → dims and does not come on**. The document keeps both fields; no house changes
  behaviour.
  **Why these are not the night period's percentage:** the night period is driven by the clock and these by the
  house mode, so going to bed at 21:00 or sleeping until 10:00 is only ever handled by the mode; the sleep
  clamp is the
  only ceiling left since per-period ceilings were cut, without which a night period asking 15 % lands near
  58 % in a room reading 1000 lx; the period is house-wide where these are per room; and the auto-on block is
  not a level at all — 0 % still sends a turn-on.

- **Every info text moves behind the ⓘ button, leaving the label alone.** The settings pages are too long to
  read, and worst on a phone where everything stacks into one column. A row becomes label + control, and all
  prose — description and help alike — lives behind the button. Needs a level inventory first: heading,
  description, help text, a control's label, and sub-control text (whether that last level exists is part of
  the question). One shared stylesheet for the levels, not per-page styling.

- **Cut the room page's standing prose.** Named cases:
  - *"Watching for movement. The lights haven't been touched yet."* — a line that reports nothing happening
    earns no space. Drop it and the other nothing-yet lines.
  - *"Movement in the dark turns the lights on."* → *"Awaiting movement"* or similar.
  - *"6 of 6 periods are this room's own; the rest follow the schedule."* — shorten.
  - *"every change here applies and saves by itself"* — remove, and confirm a write with a brief toast at the
    foot of the screen instead.
  - *"Morgen every day at 06:45"* → *"Morgen [06:45]"*.

- **Brightness and warmth become stepped sliders, laid out as a table.** A slider with fixed steps is easier
  to set than a dropdown or a continuous control. Brightness shows a *house default* marker and the percentage
  only, with no per-step wording; warmth shows the same marker and keeps the wording each step already has in
  its dropdown. The two dropdown steps that carry no wording are granularity nobody uses — drop them. The
  words *Brightness* and *Warmth* are not repeated per period; the periods read as rows:

  | Period | Brightness | Warmth |
  | --- | --- | --- |

- **Move *How this room behaves* to the top of the room settings**, with *All settings*, *In this room* and
  *What happened here* together at the foot of the page.

- **Fix the dashboard's period ribbon, which draws a schedule the engine is not running.** `BoardView.Band`
  (`BoardView.cs:449-500`) lays the day's boundaries out itself from `PeriodStart.TryParse` and
  `PeriodStart.Resolve`: a third boundary layout beside `CircadianCalculator.ResolveBoundaries` and the room
  page's, blind to both things that move a period away from its `Start`. A period still waiting for movement is
  drawn as though it had begun, and under `PeriodAuthority.HomeAssistant` the dropdown's choice does not reach
  the ribbon at all, so the band names one period while the engine runs another. The fix is to finish the
  consolidation: the band takes the boundaries the calculator resolves, which means handing it the held-back
  predicate and the override reader the calculator already takes.

- **The blend starts when the period actually begins.** A period whose `Start` was 06:30 but which movement
  began at 06:45 arrives already part-way through its blend, because the window trails the boundary and the
  boundary is still 06:30 (`mechanisms.md`, *A period that waits for movement*). It should ease from the moment
  somebody walked in. **The blend keeps its full configured length and therefore finishes later than a
  clock-started one would**, so the transition feels the same whenever you arrive. Needs the calculator to know
  *when* a period began, not just whether.

- **The daylight lift climbs past the day level.** *Brighten with daylight* has been observed near 100 % in the
  toilet. It should not exceed the *Day* period's brightness, which gives the lift a ceiling without
  reintroducing a per-period cap.

- **Publishing is blocked: the packages are still linked to the archived repository, and are now private.**
  Measured 2026-08-22 — `users/0z00z0/packages/nuget/AdaptiveLighting` reports
  `repository: 0z00z0/adaptivelighting-archive`, `visibility: private`. The `v2.0.0-preview.5` tag built and
  tested green and then failed on push with `403 Forbidden`: a release job's per-run token can only write
  packages linked to its own repository. The packages also inherited the archive's visibility when it was made
  private, so an outside consumer of a public MIT project can no longer restore them at all. Each of the four
  packages needs its linked repository changed to `0z00z0/adaptivelighting` and its visibility set back to
  public, in the package settings. The alternative, a personal access token held as a repository secret, is
  what the project deliberately avoids. Until this is done no release can be published and no house can be
  updated past `2.0.0-preview.4`.

- **The daylight curve becomes a per-period mode, replacing the lift.** Each period carries a choice:
  *specify brightness*, which is today's behaviour and the default, or *use daylight curve*, where the curve
  decides the brightness instead. The curve diagram is shown only when at least one period claims it, and a
  claiming period hides its own percentage, which the curve makes irrelevant. Several periods may claim it,
  and the curve then spans them all. The curve is draggable across the full 0-100 % range, so no ceiling is
  needed and the plan's brightest level does not constrain it.
  - A claiming period keeps its stored percentage in the document, hidden, so the choice is reversible without
    losing what was typed.
  - **The reading comes from the house's outdoor sensor**, since an indoor sensor measures the room's own
    lamps. A configuration with no outdoor sensor defined is warned about. A room may override which sensor it
    reads, chosen from all light-level sensors, in its own settings.
  - `LuxBrightnessEnabled` is removed: the period decides alone, and one mechanism replaces two.
  - Texts throughout are reworked to plain, short wording.

## Parked

- **The daylight chart is only 101 px tall on a phone, which caps its labels.** The corner and the label spread
  were fixed and took the cap from 13 user units to 15, or 6.3 real pixels. The 10.1 the formula asks for needs
  a 24-unit cap and a `MinGap` near 34, which puts five gaps into that 101 px: the labels would cover the chart
  they annotate, and the desktop would carry the same spread for type a third of the size. Reaching it means a
  taller chart on a narrow container, or no period labels on the drawing at all — a design question, not a
  defect.

- **The user guide has no screenshots.** Every `📷 [screenshot: …]` slot is still a placeholder.

- **The first-run wizard is undocumented.** The user guide covers every other screen; the wizard ships without a section.

## Open questions

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

- **Symbols reach nobody.** `Directory.Build.props` sets `IncludeSymbols` + `SymbolPackageFormat=snupkg` and
  wires Source Link, so four symbol packages are built per release — and `release.yml` pushes with
  `--no-symbols` because GitHub Packages has no symbol server, so all four are dropped every time. A stack
  trace from a live house resolves to a method with no line number. `DebugType=embedded` puts the debug data
  inside the DLL, which needs no symbol server and would make the trace resolve, at the cost of a larger DLL in
  every deploy.
