# The Workshop — the converged design (C)

Interactive mock-up: **The Workshop** — https://claude.ai/code/artifact/5de52c86-d1f4-41f7-a694-01b590972399
(also saved at `docs/design/mockups/the-workshop.html`).

This is not a third alternative beside A and B. It is the design the owner's reaction to those two
points at: B's timeline promoted to the home surface, A's editing feel kept wherever a value is
touched, and the owner's own suggestion — sentence overview plus *show more* — as the disclosure
model that lets both be true at once. Everything in §3 of `area-restructure.md` stays reachable;
§4 below maps every row of that table to its place here.

---

## 1. Who this is for, and when they arrive

The previous round designed two philosophies and never named a visitor. The owner named the missing
premise himself: *if this system works as it should, no one would need to ever visit the dashboard
except when trying to understand or to change behaviour.* Taken seriously, that premise produces a
short, concrete visitor list — and one sharpening the premise itself needs.

**V1 — the detective.** The owner, arriving with a room and a time already in mind: *"why didn't
the Uteplass light come on last night around 23:00?"* Weekly in the first month, monthly once the
house is tuned — but never zero, because the world keeps changing under the system: a sensor's
battery dies, a bulb is replaced with a dumber one, Home Assistant renames an entity. The detective
needs evidence: what the engine saw (the lux reading beside the threshold it was compared against),
when, and what it decided. This visitor is the design's primary.

**V2 — the tuner.** The owner again, usually carrying somebody else's complaint: *"the shower light
keeps dying on me."* One room, one setting, resolved in one visit. Near-daily in the calibration
weeks after commissioning; a few times a year afterwards (seasons change, furniture moves, a child
starts sleeping in a different room).

**V3 — the commissioner.** Once per house, plus after renovations or new gear. First-run, and the
*set up again* flow.

**V4 — the owner six months later.** Not a fourth person but a constraint on the other three: the
rare visitor has forgotten the vocabulary. A form of seventeen numeric fields assumes a reader who
remembers what `VacancyTimeoutSeconds` meant; a sentence does not. This is the decisive argument for
B's sentences, and it is why they win the overview layer even though the owner rightly worried they
are "a lot to read" as the *whole* story.

**Named non-visitors**, because leaving them implicit is how A over-invested:

- **Household members.** Their interface is the wall switch, and the engine's entire design honours
  it — `ManualOn` and `ManualOff` are first-class states with first-class courtesy (the hold, the
  suppression). The UI serves the household *indirectly*, through V2 carrying their complaints. They
  do not open this app, and the design stops pretending they might.
- **Guests.** Never. A stated requirement of the previous round — "operable by someone who was never
  taught it" — optimised for a user who does not arrive. Dropped, deliberately, in the open. (V4
  inherits the good part of that requirement: the surfaces must reread themselves cold.)
- **The wall tablet.** Doesn't exist in either house. If one ever appears, the board is already the
  screen that earns a permanent place — but this design spends nothing on it today.

**Where the owner's premise needs sharpening rather than restating:** the two visits it names are
usually *the same visit*. The detective who finds the answer almost always proceeds to turn a knob
(the diagnosis's output is a tuning decision), and the tuner who arrives sure of the fix should
first see what the room actually did (half of the complaints are the system behaving as configured).
So the real requirement is stronger than either A or B met: **understanding and changing must live
on one screen** — not on two pages (the shipped UI's defect), and not on two tabs (B's residual
split between the board and the rules page). That is why C's room page holds state, history and
settings together: A's move, executed with B's organs.

One honest amendment: there *is* a period of ambient watching — the first weeks after
commissioning, before trust is earned, when the owner opens the board just to watch the engine
behave. That is not a third kind of visit needing a reassurance dashboard; it is a temporary
intensity of V1, and the board serves it as-is: quiet lanes doing what the schedule band says *is*
the reassurance.

**Frequency ladder**, stated so the design can be checked against it:

| Occasion | Frequency | Surface that answers it |
|---|---|---|
| "Why did/didn't that happen?" | weekly → monthly → rare, never zero | board + log, room page log & facts |
| "Change how this room behaves" | daily (weeks 1–6) → few times a year | room page sentences / all-settings |
| "Change the house's rhythm/modes" | seasonal, a few times a year | House tab |
| Commission / re-commission | once per house + renovations | first-run report, *set up again* |
| Ambient monitoring | weeks 1–6 only, then never | the board, incidentally |
| Guest use | never | — |

---

## 2. What changes from A and B, and why

**Kept from B — the diagnostic skeleton:**

- **The board as the home surface.** The premise says the visitor comes to understand or to change;
  both begin with *what happened*. "Now" is just the newest moment of the timeline, so a separate
  live dashboard of current state does not earn a page: the board's right edge is the dashboard.
  The `AreaCard` grid — eleven lines of prose per room, seventeen times — retires; its content
  survives intact on the room page (§3.2).
- **The exception tray and the dark-cockpit rule.** Nominal rooms are quiet grey lanes; only
  exceptions light up, and they are also collected on top. Seventeen rooms are scannable in one
  second *because* fourteen of them are invisible.
- **The dossier organs** — the facts table and the per-room event log — moved onto the room page.
- **The commissioning report** as first-run: a table of discovered rooms, gear, and a verdict column,
  switched on deliberately. (It matches what actually shipped: rooms start off.)
- **Sentences** — but demoted from "the settings UI" to "the overview layer of the settings UI" (§3).

**Kept from A — the editing feel:**

- **Instant apply with undo, everywhere.** No save bar, no pending-changes drawer, no document
  exposed. Why this is now safe: every control in C is *constrained* — steppers, segmented choices,
  toggles, curated option popovers — so no interaction can produce an invalid document. The shipped
  validate-then-write pipeline still runs on every change; it just runs per change instead of per
  session, and *undo* (write the previous value back) replaces *discard*. B's drawer kept the
  transaction visible; C makes the transaction so small it needs no ceremony.
- **The inheritance rendering.** Inherited values muted with a "house setting" tag; a room's own
  values marked with a dot and a one-tap *"Use house setting (10 min)"* road back. In the sentences,
  the same fact renders as B's amber token. One model, two densities.
- **Quick actions on the room** — *Resume auto now*, *Pause this room 1 h*, *Listen to movement
  again* — commands, not configuration, and instant by nature.
- **The glow in the light's actual warmth**, on the room page header and the board's blocks alike.

**Dropped, with reasons:**

- **B's pending-changes drawer.** Two editing models is one too many; single-knob changes dominate
  every visit; the one legitimate batch (commissioning) keeps its own deliberate commit button.
- **A's bottom-nav room list as the home surface.** The board's sticky name column *is* the room
  list — same glance ("dot + name"), plus six hours of context the list never had.
- **The guest requirement** (§1). And with it, A's insistence that everything fit a casual thumb:
  the board accepts a sideways scroll on phones because its reader is an investigator, not a passer-by.
- **The reassurance dashboard as a genre.** No grid of cards saying "everything is fine" seventeen
  ways. The tray's one line — *"The other 12 rooms are doing what the schedule says."* — is the
  entire reassurance budget.

**One deliberate break with the shipped colour language:** the shipped UI uses amber for "a human
did this"; B introduced violet-hand / amber-warning, and C keeps B's assignment. Warning and human
cannot share a hue family, and the warning dim — the one state that wants to be noticed *right now* —
has the stronger claim to amber. Migration note: the dashboard's `--human` amber becomes violet in
the same release that ships the board, so the language changes once, not twice.

---

## 3. The disclosure model

Four layers. Each answers one question, and each is one tap from the next.

**Layer 0 — the lane** (board). One line per room: status dot (kelvin glow / violet hand / blinking
amber warning / grey), the recent past as blocks, the future as dotted marks. Answers *"anything
odd, and what happens next?"*

**Layer 1 — the sentences** (room page, always visible). The room's behaviour as at most four
sentences, generated from exactly these settings:

| Sentence | Settings it renders |
|---|---|
| *"Lights when someone moves and it's darker than **40 lx** — or the sun is below **3°**."* (variants for sensor-only, sun-only, and "whatever the daylight") | `Darkness`, `LuxThreshold`, `SunElevationThreshold` |
| *"After **10 min** without movement, dim to **50 %** for **30 s**, then off."* | `VacancyTimeoutSeconds`, `PreOffBrightnessFactor`, `PreOffSeconds` |
| *"Hand changes hold for **2 h**; after a manual off, movement is ignored until the room is empty **10 min**."* | `OverrideDurationMinutes`, `VacancyResetMinutes` |
| *"This room is gentle while the house sleeps, and welcomes the first person home."* — rendered **only when a flag is on**; a room with no flags gets no fourth sentence | `RespectSleepMode`, `SleepBlocksAutoOn`, `SkipAwaySweep`, `WelcomeHome`, `IgnoreWhenOn` |

Rules of the layer:

- **Every value is a token.** Tapping it opens a popover of curated common values; picking one
  applies instantly (toast + undo). Values that are the room's own render amber with a dot; the
  popover's footer says so and the *All settings* row offers the way back.
- **Curated, not complete.** The popover offers the handful of values a sane house uses
  (10 min → the popover says 3/5/10/20/30). The row behind *show more* has the stepper for
  everything between — and clicking a stepper's value turns it into a plain numeric input, so no
  value expressible today becomes inexpressible here. Sentence for the common case, row for the
  precise one, keyboard for the exotic one.
- The layer never hides that more exists: the bar under the sentences reads
  *"All settings ▾ — 3 of 16 settings are this room's own"*.

**Layer 2 — All settings** (the *show more* reveal, same card). The complete per-room inventory as
rows in A's style — label, help line, control, inherit tag or override dot with reset. Grouped:

- **Movement & timing** — Lights stay on for · Warning dim level · Warning dim lasts · Hand changes
  hold for · After a manual off, wait
- **Darkness** — How the room decides it's dark · Dark below (+ extra light to count as bright
  again) · Dark when the sun is below
- **Behaviour** — Gentle while the house sleeps · Never auto-on while the house sleeps · Stays on
  when everyone leaves · Welcome home · Blocked while on
- **Rarely needed** — Fade up/down over · Sun entity
- **Room identity** — Name · Home Assistant area
- **In this room** (its own card, always visible below) — the discovered gear as chips, with *Pick
  by hand…* opening the explicit lights/motion/lux pickers, and the destructive pair: *Set this room
  up again* (the §4.4 warning flow from `area-restructure.md`) and *Remove this room*.

The reveal is per-room state, collapsed on arrival, and it does not navigate — the log and facts
stay on screen below it, because the tuner reading evidence and the tuner changing the knob are the
same person mid-thought.

**Layer 3 — the House tab.** The same pattern applied house-wide, so the model is learned once:

- **Every room starts with these** — the *same four sentences* rendering the defaults (bold, not
  tokens — editing defaults is rarer and the rows are one tap away), with its own *All settings*
  reveal of the same 16 rows, minus inherit tags (these *are* the house settings). A closing line
  names which rooms stray: *"14 rooms follow these exactly; Stue, Kontor and 6 others carry their
  own values."*
- **Schedule** — the daylight band, the period table with tokens (start · brightness · warmth ·
  ceiling), and the blend sentence.
- **House modes** — each mode as one sentence with its tokens (auto-away hours, arrival grace,
  night end), exactly B's rendering.
- **People** — chips plus the away-debounce sentence.
- **Master switch** — the one toggle; *how to read the switch* appears only when a custom entity is
  configured, as today.
- **Finding lights & sensors** — the three label pickers (include / never-touch / counts-as-motion)
  with the §5 empty-label behaviour from `area-restructure.md`, and the device-classes fold.
- **Switched-off rooms** — every disabled room with its own switch. This is the thread the board's
  footer pulls.
- **Fine tuning — the engine itself** (one fold) — house name, NetDaemon user id (set/not-set only),
  re-check interval, self-echo window, automations-count-as-manual, command tolerances.

**The reachability invariant:** every setting in §3 of `area-restructure.md` is reachable in at most
two taps from the page of the noun it changes — a room's setting: room page → *All settings*; a
house setting: House tab, at most one fold deep. No setting requires visiting any other page, and
no path passes through a save bar.

---

## 4. The §3 mapping — nothing quietly dropped

Every row of `area-restructure.md` §3, placed. ("Sentence + row" means it appears as a Layer-1 token
*and* a Layer-2 row; "row" means Layer-2 only.)

**Per-room / All-rooms (`AreaSettings`, §3.2):**

| Setting | In C |
|---|---|
| `VacancyTimeoutSeconds` | sentence + row (*Lights stay on for*) |
| `PreOffSeconds` | sentence + row (*Warning dim lasts*) |
| `PreOffBrightnessFactor` | sentence + row (*Warning dim level*) |
| `OverrideDurationMinutes` | sentence + row (*Hand changes hold for*) |
| `VacancyResetMinutes` | sentence + row (*After a manual off, wait*) |
| `Darkness` | sentence variant + row (*How the room decides it's dark*) |
| `LuxThreshold` / `LuxHysteresis` | sentence + row / row (*Dark below*, *extra light…*) |
| `SunElevationThreshold` | sentence + row (*Dark when the sun is below*) |
| `SunEntity` | row, *Rarely needed* |
| `DayTransitionSeconds` / `NightTransitionSeconds` | rows, *Rarely needed* (*Fade up/down over*) |
| `RespectSleepMode` / `SleepBlocksAutoOn` / `SkipAwaySweep` / `WelcomeHome` | flag sentence + rows |
| `IgnoreWhenOn` (per-room) | flag sentence + row (*Blocked while on*) |
| `Enabled` | first-run switches; House › *Switched-off rooms*; a switched-off room's own page opens with *Turn this room on* |
| explicit `Lights` / `MotionSensors` / `LuxSensor` | room page › *In this room* › *Pick by hand* |
| `Name` / `AreaId` | room page › *Room identity* |

**Global (`GlobalConfig`, §3.1):**

| Setting | In C |
|---|---|
| `ConfigName` | House › Fine tuning (*House name*) |
| `Persons` / `AwayDebounceMinutes` | House › People (chips / debounce sentence) |
| `KillSwitchEntity` / `KillSwitchActiveWhenOff` | House › Master switch |
| `NetDaemonUserId`, `CircadianTickSeconds`, `SelfEchoWindowSeconds`, `TreatAutomationsAsManual`, `BrightnessTolerancePct`, `ColorTempToleranceKelvin` | House › Fine tuning fold |
| `SmoothTransitions` / `BlendMinutes` | House › Schedule (blend sentence token) |
| `OutdoorLuxSensor` | House › Finding lights & sensors (it is an input the house discovers darkness with; grouping it with the other "how rooms find things" settings beats §3.1's *All rooms › Darkness* placement by one fewer home for sensor-ish settings — a deliberate small deviation, flagged) |
| `IncludeLabel` / `ExcludeLabel` / `MotionLabel` | House › Finding lights & sensors (label pickers) |
| `MotionDeviceClasses` / `IlluminanceDeviceClass` | same card › device-classes fold |
| Periods / `HouseMode` config | House › Schedule / House modes |
| `AreasAutoDiscovered`, computed members | hidden, as today |

---

## 5. Screen by screen

### 5.1 The board (home)

Top to bottom: the **header facts line** (clock · period and next boundary · mode · who's home ·
dusk estimate · RUNNING/PAUSED button — pausing commands nothing off, and says so); the **exception
tray** (each exception one bordered chip: violet edge for hand states, amber for warning dim,
plus engine faults and overdue reports; tapping opens the room; when empty, the tray is one line —
*"All 15 rooms are doing what the schedule says."*); the **lanes**, grouped under floor separators
(Hovedetasjen · Oppe · Kjelleren · Ute), each lane a sticky name cell and a track: schedule band
above, past blocks in commanded warmth / violet / amber / hatched-paused, dotted future marks
("22:30 → night 15 %"), one teal now-line through everything. Window: the last ~4 h and next ~2 h.
The **switched-off footer** names hidden rooms with the thread to House. Below, **the log** — the
shipped Activity page's content, house-wide, newest first, each line *time · room · what — why*,
with the why carrying the evidence ("36 lx crossed below 40 lx"). Room names link to room pages.

Phone: the name column stays sticky, the track scrolls sideways; the tray and the log — both fully
phone-native — carry most phone visits anyway.

Empty states, in precedence order (all inherited from the shipped dashboard, reworded not redesigned):
connecting → engine-not-running (with fault) → **awaiting room choice** (renders the first-run
report, §5.4) → quiet house (lanes render with empty tracks; an empty track *is* the information).

### 5.2 The room page

Header: glow in actual warmth with the countdown ring around it, name, state pill, "since" stamp.
Then the **state sentence** (the `AreaCard` headline + next-line, verbatim tone), **quick actions**
for the state, and the four cards: **How this room behaves** (sentences → *All settings*, §3),
**What happened here** (the log filtered to this room), **Right now — what the engine saw** (the
facts table: state, lights and levels, last motion, last command, darkness with the reading and
threshold, period), **In this room** (gear chips, pick-by-hand, set-up-again, remove).

A switched-off room's page is short: the sentence *"This room never changes by itself,"* one
primary action (*Turn this room on*), and the gear card — no settings shown for a room that obeys
none of them.

### 5.3 The House tab

As §3, Layer 3 — seven cards and a fold, in the order a visitor's questions get rarer: defaults,
schedule, modes, people, master switch, finding gear, switched-off rooms, fine tuning.

### 5.4 First run

The board route, detecting the awaiting-room-choice state, renders the **set-up report**: one
sentence (*"Found 17 rooms, their lights and sensors already matched up. Nothing changes until you
switch a room on — start with a hallway, it's the easiest room to trust."*), the table (switch ·
room + area id · lights · motion · light level · verdict), verdicts doing real work (*"No motion
sensor — would never auto-on"*, *"no window: set 'Always dark'?"*), and one commit button naming
its count. Not styled as an error anywhere. Rooms left off remain listed under House with their own
switches — the sentence under the button says so.

---

## 6. The icon pass — one visual voice

A second round folded in the hand-authored icon set (`docs/design/icons/`, drawn in the
`0z0-design` technique: 24×24, flat geometric, a `currentColor` outline plus one flat accent bound
to `var(--icon-accent)`, stroke 1.6–2 with round caps, bolder below ~16 px). The owner asked for it
to inform the UI overall, not merely to appear in it. Both happened, and the first mattered more.

### 6.1 What the icon language changed about the wider surface

- **Flat, not floated.** The cards, board, log panel, tray chips and buttons drop their box-shadows;
  surfaces are now a 1 px line on a flat ground, exactly like the glyphs' outline-on-nothing.
  Elevation survives only where it is information — the token popover and the undo toast, the two
  things that genuinely float above the page.
- **A geometric radius scale.** The icons' rx ≈ 1.5–1.7 on 24 units set the proportions: panels 8 px,
  controls 7 px, chips 6 px. The pill shape is no longer a default — it survives only on true
  switches (the toggle) where the shape *is* the meaning; quick actions and gear chips squared up.
- **The accent budget, written down.** One accent per icon generalised to the page: brass
  (`--accent`) marks *editability and primary action* — tokens, steppers, the active tab, the commit
  button — and nothing else. State is never accent: the kelvin ambers, `--human` violet and `--warn`
  amber are semantic and flow through `currentColor`, precisely the split the state glyphs already
  make between outline and accent.
- **One stroke grammar.** Icon strokes (1.7/2), control borders (1–1.5 px), the 2 px now-line and the
  2 px dotted future marks now read as one family of lines rather than three decisions.
- **The theme contract.** Each theme block sets `--icon-accent: var(--accent)`; state glyphs override
  it to `currentColor` at the point of use (the README's own usage sketch). No colour is hard-coded
  into an icon, and both themes were checked.

### 6.2 Where icons landed — and where they were refused

The strongest case is accessibility, not decoration: the board, tray and lanes previously said
lit-by-engine / set-by-hand / warning **by colour alone**. Now state is shape *plus* colour:

| Place | Glyph | Note |
|---|---|---|
| Header | `app-mark` | the product in one mark, ink outline, brass light point |
| Lane markers | `state-auto` (kelvin-tinted) · `state-manual` (violet, covers both hand states — the pill and hatch say which) · `state-auto` amber + blink for the warning dim | **nominal rooms keep a bare grey dot** — the dark-cockpit rule extended to iconography: shapes only where something is happening |
| Exception tray chips | same glyphs | the tray is now legible in greyscale |
| Board legend | glyph + swatch per key | teaches the shape language where colour is explained |
| Room state pill | the state's glyph beside its word | none for the nominal "watching" |
| House › Every room starts with these | `areas` | rooms as places |
| House › Schedule | `schedule` | it is literally the card's own chart |
| House › House modes | `house-modes` (rotary) | over the segments alt: one-position-selected matches the semantics, and the segments shape would collide with the app's own segmented controls |
| House › People | `house-alt-residents` | see below |
| House › Master switch | `house` | see below |
| House › Switched-off rooms | `state-off` (struck circle) | over the power alt: power reads "toggle me", and real toggles sit right beside it |

**Pair verdicts the icon author asked for.** `state-auto` loop over the circled-A (a letter in a UI
already dense with monospace text); `schedule` curve over the dial (the card draws that exact
curve); rotary over segments and struck-circle over power as above. The `house` /
`house-alt-residents` pair stops being an either/or here: it was drawn as alternatives for one
*House section*, but this design dissolved that section into cards — so the switch-in-a-house glyph
sits on the Master switch card and the resident-in-a-house on People, each exactly literal. Two
house outlines on one page is a deliberate rhyme, not a clash. And the `dashboard` pair —
tiles vs gauge — ships **neither**: the home surface is a timeline, not a tile grid or a single
reading, and a wrong metaphor is worse than none. The tabs stay text-only; if nav icons are ever
wanted, the right move is to commission a lanes glyph (three tracks, an accent now-line) in the
same technique, not to bend an existing one.

**Refused placements**, so restraint is on the record: log rows (twelve repeated glyphs are texture,
not information), quick-action buttons, sentence tokens, person chips (the avatar is already the
mark), the room-page card heads (the glowing room header is that page's mark), the first-run table,
and the Finding lights & sensors card (nothing in the set says "discovery", and it can wait).

**One accepted compromise, named:** the warning dim shares the auto loop's shape — truthfully, since
`PreOff` *is* the engine acting — and is distinguished by amber, blinking, and its presence in the
tray. A colourblind reader in a static screenshot tells it from auto-lit by the tray line naming it.
If that proves too subtle, the icon author should draw a fourth state glyph; none of the fifteen
should be bent into meaning "about to switch off".

### 6.3 Cost

Carried into the build plan: the sprite (one Razor component of `<symbol>`s), `--icon-accent` in
`app.css`'s theme blocks, and the flat pass over existing card/chip styles land with **C1**; the
lane and pill glyphs ride **C2/C4** where those surfaces are built. Days, not weeks, in total.

---

## 7. What this design gives up

- **The reassurance dashboard.** Nobody gets a grid of cards narrating seventeen fine rooms. If the
  owner's premise is wrong — if someone *does* open this daily just to feel the house working — the
  tray's single quiet line is all they get.
- **Guest usability as a requirement.** A guest handed this page would find the board legible but
  would not feel invited. Accepted, because that guest does not exist.
- **Transactional review.** No diff, no "apply 3 changes". A mis-tap is live in the house for the
  seconds until undo. Undo therefore must be engineering-grade (write-back of the previous value
  through the same pipeline), and bulk edits cost N taps — "set all basement timeouts to 3 min" has
  no one-shot gesture. (The one batch that matters, commissioning, keeps its commit button.)
- **History depth.** The board and log reach back to engine start, exactly like the shipped
  Activity page — a restart wipes the morning. Persisting history is out of scope here and honestly
  labelled on the page ("since the engine started, 18:00").
- **Raw numeric freedom at the surface.** Tokens offer curated values; steppers move in a field's
  natural grain; the keyboard path (click the stepper's value) is two layers down. Deliberate
  friction, but friction.
- **The board on phones is a compromise.** Sideways scroll is honest work, not delight. The design
  bets phone visits lean on the tray, the log and the room page — if that bet fails, the board
  needs a phone-specific collapsed rendering (per-lane sparklines), which is not designed here.

---

## 8. What it would take to build

Sized against the shipped pages (`Dashboard.razor`, `ConfigEditor.razor` ~1500 lines,
`Activity.razor` just landed) and assuming the `area-restructure.md` work packages WP1–WP5 (rename,
floors, setup service, snapshot area ids) are in. Ordered by value-per-effort; each ships alone.

**C1 — Tray + log on the shipped dashboard.** *Small; days.* Hoist exceptions above the grid
(`AreaView.Family` already classifies states), embed the Activity list (already built) below it.
No new data, no new write path. This ships the dark-cockpit answer to "anything odd?" immediately,
while the cards still exist.

**C2 — The room page.** *The big one; the largest single package.* New `/room/{areaId}` route.
Content is mostly relocation: `AreaCard`'s prose becomes the header and facts card; `AreaEditor`'s
override rows become the *All settings* rows; `ActivityView.InRoom` feeds the log. Genuinely new:
`SentenceView` (a pure projection of `AreaConfig` + defaults into the Layer-1 sentences — fully
unit-testable, and the tests are the §3 table), and **per-change apply**: one mutation →
`LightingEngineHost.Save` → toast, with a one-deep undo that writes the prior value back through
the same path. Saves serialize through a single-flight queue (last write wins); no new write
surface. The save bar does not appear on this page.

**C3 — The House tab.** *Medium.* Re-hangs `ConfigEditor`'s sections into the §5.3 cards: defaults
get the sentence overview + reveal (reusing `SentenceView` and the same row components as C2),
Schedule gains the token table beside the existing `DaylightChart`, modes become sentences over the
existing `HouseModeConfig`, label pickers per the restructure plan. `ConfigEditor` stays alive at
`/config` until this reaches parity, then redirects.

**C4 — The board.** *Medium-large, and last.* The only genuinely new rendering in the design. Lanes
are a pure projection of `ActivityLog` (state spans between consecutive transitions; block colour
from each snapshot's kelvin; hand/dim/paused from state), future marks from `NextChangeAt` plus the
schedule's next boundaries. Ships last because the log (C1) already answers the same questions in
text; the board makes them one-glance.

**Retirement schedule:** the `AreaCard` grid retires when C4 lands; the whole-document save bar
retires page-by-page as C2/C3 land. Nothing retires before its replacement is live.

---

## 9. Decisions most worth challenging

Named so the pushback lands somewhere specific:

1. **The board is the home page**, not a compact room list with the timeline behind a tab. If the
   phone-compromise (§7) weighs heavier than judged, home becomes A's room list and the board moves
   one tap away — the rest of the design survives that swap intact.
2. **Instant apply everywhere, no pending drawer anywhere** — including schedule and mode edits,
   where a mistake touches every room at once. The constrained-controls argument covers validity,
   not regret; if regret needs more than undo, the schedule card alone could get a confirm step.
3. **Guests are formally out.** This contradicts a stated requirement of the previous round, on the
   strength of the owner's premise. If a wall tablet ever ships, revisit.
4. **Curated tokens over free fields**, with the keyboard demoted two layers. If the owner tunes by
   exact numbers more than by "one step warmer", the balance flips.
5. **The card grid dies with nothing card-shaped replacing it.** The tray line plus quiet lanes is
   the whole "all is well" statement. That is the dark-cockpit bet, applied without a hedge.
