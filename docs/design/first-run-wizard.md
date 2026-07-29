# First run — the commissioning board

The recommended design, worked up in full from the starred options in
[`first-run-wizard-options.md`](first-run-wizard-options.md). It extends the approved set-up report
(`ui-design-c.md` §5.4) rather than replacing it, and it is buildable as written: every step below names
what it shows, what it asks, what skipping it means, what it looks like on a 390 px phone, and what it
costs. The mock-up is [`mockups/first-run.html`](mockups/first-run.html); the questions only the owner can
answer are in [`first-run-wizard-questions.md`](first-run-wizard-questions.md).

One sentence of premise, because everything follows from it: **by the time anyone sees this surface,
discovery has already done its half.** The engine's first start proposed every qualifying room switched
off (`AreaAutoDiscovery`), seeded the people from Home Assistant's own `person.*` entities
(`AreaSetupService.SeedPersons`), and adopted the house-mode dropdown when exactly one obviously qualified
(`HouseModeAutoDetect`). The wizard therefore never collects a fact the app can discover. It shows what
was found, takes the few corrections only a human can make, and switches rooms on — behind one commit
button.

---

## 1. The shape: a commissioning checklist on the board

The board's *awaiting room choice* state — the calm `.onboarding` panel a fresh install already lands on —
grows into the whole wizard: a findings strip as its header, a short stack of question sheets, the room
roll-call as its centre, and the one commit button at the end. Not a route, not a mode: **a state**. It
renders exactly while no room is switched on, and the moment the commit lands the same page becomes the
live board, with the chosen rooms' lanes empty — the quiet start shown rather than promised.

Each question sheet is a card that opens in place, one at a time, in any order. A sheet that has no work
does not render at all — a house with one lux sensor per room gets no sensor sheet, a house where nothing
looks like an impostor gets no impostor sheet. That is the dark-cockpit rule applied to the wizard itself:
steps only where something needs a human. On the smallest honest install the "wizard" degrades to one
identity card above the table, which is exactly the weight that install deserves.

Why this shape and not the other four:

**Against the linear stepper.** A stepper earns its keep when the answers depend on each other, and these
do not: the house's name, the car tracker, the router LED and the bedroom switch are four independent
facts. Imposing an order the facts don't need costs a full-screen shell, a progress model, and a back
button — and it hides the room table, the one thing the owner actually came to operate, behind N "next"
taps. The stepper's genuine strength, the narrative arc from findings to commit, survives in this design
as the page's top-to-bottom reading order: strip, questions, roll-call, commit. You get the narrative by
scrolling and the freedom by tapping, and nothing is modal on a page whose whole promise is "nothing
happens until you say so".

**Against the one scrolling page.** Closest cousin, and the phone kills it. Seventeen rows plus every
question's full UI inline is a very long page at 390 px, and on it "skip" degrades into "scroll past",
which reads as homework not yet done. The checklist keeps the questions folded until wanted, so the page's
resting length is the strip, four one-line items, the table and a button — and a sticky progress rail
(the scrolling page's navigation crutch) never needs to exist. What survives from this shape is commit
order as reading order.

**Against the proposal-mode board.** Rendering the grey lanes with switches looks most like the product,
but it needs the board built first (C4, deliberately last in the build plan), and it puts the global
questions in the exception tray — a surface designed to *report* exceptions, never to take input. A tray
chip that opens a form teaches, on day one, that tray chips open forms; every later visit unlearns it.
The checklist keeps the board's vocabulary out of the wizard until there is behaviour to show, and then
shows real behaviour: the epilogue's empty lanes are the board introducing itself honestly.

**Against the sentence conversation.** Right observation, wrong container. Sentences are how this product
writes behaviour, and the wizard should speak them from the first minute — but a 17-room decision is a
table, and no amount of grammar makes *"Stue and Kjøkken and fifteen more rooms are `off`"* operable. So
the conversation is hired where it is strong and nowhere else: the identity sheet **is** one sentence with
tokens, the mode sheet renders `HouseSentences.Modes` verbatim, and tapping any roll-call row unfolds that
room's own four sentences read-only. The grammar is taught at the door; the table does the arithmetic.

---

## 2. The steps

**E** essential · **C** conditional (absent, not greyed, when it has no work). Skipping is never punished:
every sheet's answer has a sane default already in the document, and the commit button works whether or
not a single sheet was opened.

### 2.0 The findings strip — the header, not a step

Five big-number chips — **17** rooms · **31** lights · **19** motion · **5** light-level · **3** people —
and, when one was adopted, a sixth chip carrying the `i-modes` glyph: *Husmodus · adopted*. Asks nothing;
orientation in five numbers. The counts are the resolver's (`HaCatalog.AreaOption`), after the exclude
label, the group preference and the ghost filter — the same numbers the engine will actually run, which is
what makes the strip a report rather than an advertisement.

The number chips are deliberately glyphless — big number above, small noun below. §6.2's split stands:
glyphs mark *sections*, counts stay text; the one glyph in the strip marks the one chip that is a fact
rather than a count. (A bulb or sensor glyph would be a commission for the icon author, not a bend of an
existing mark, and the strip does not need it.)

**Phone (390 px):** the chips wrap to a 3 + 2 grid, mode chip full-width beneath. Nothing scrolls.

### 2.1 Name it & who lives here — E

One sentence, in the product's own grammar:

> This house is called `B1`. `Espen` `Nora` `Bilen` live here; it counts as empty `5 min` after the last
> person leaves.

- **House name** — a free-text token editing `ConfigName`. It exists so two houses can be told apart in
  logs and notifications, and this owner has two houses, so it is not decoration.
- **Person chips** — the seeded `Global.Persons`, each with the live presence dot the board's house bar
  already uses (`.person-chip` / `.person-dot`). Tap toggles a person out (struck, muted) or back in. The
  live dots are the demonstration: the car showing *home* while parked outside is precisely why it must be
  evicted, and the chip shows the evidence rather than asking for trust.
- **Empty-house delay** — `HouseSentences.AwayDebounce`, verbatim, token and shortlist included.

**Skipped:** the house is called "Adaptive lighting", the seeded list stands (including the car), 5 min
stands. All three remain one tap away on House, forever.

**Phone:** the sentence wraps at its own 1.9 line height; person chips wrap to a second row; every token
opens as the bottom-sheet popover `.tok-pop` already becomes at ≤560 px. Nothing new to design.

### 2.2 House mode — C (renders only when a select was adopted)

The adopted `input_select` named in one line — *Husmodus, from Home Assistant* — and its options as the
read-only mode sentences the House tab will show (`HouseSentences.Modes`, `SentenceView` with
`Editable="false"`): *Hjemme is everyday automatic lighting…*, *Borte sweeps the lights off…*. Under
them, two buttons: **Keep it** (primary, and the default if never visited) and **Detach** (the house has
no mode; `Global.HouseMode = null` staged).

This sheet exists to be read more than to be answered. Adoption is safe by construction — an adopted mode
carries kinds only, no scene, no reset trigger, so the engine only ever *reads* the dropdown — but a
wrongly adopted select silently reclassifies the whole house's idea of Away, and the one moment to catch
that is now, in the owner's own words rendered back at them. Remapping options stays on Configuration; the
wizard only confirms or declines the guess.

**Skipped:** adoption stands. **Phone:** four sentences and two buttons; nothing to adapt.

### 2.3 Impostors — C (renders only when `LightAudit` flags something)

The step that earns the wizard its place, and it is a projection of code that already ships: every flagged
light from `LightAudit.Review` across the proposed rooms, one card per suspect, grouped by room:

> **Stue AP status** `light.stue_ap_status_led` — *named as a status light, which reports on a device
> rather than lighting a room* — [keep] **[exclude]**

The reason is `LightAudit`'s own sentence, verbatim — evidence beside the name, never a rule id — and the
choice is one tap. Excluding stages the id into that room's `ExcludeEntities`; nothing anywhere is
written. A *blink* button on each card, if the owner approves it (question 1), pulses the entity once so
"is this a light or a router LED" is answered by the ceiling rather than by the naming convention.

All suspects start **kept**. `LightAudit`'s own doctrine — *advice, never a filter; the household knows
its own house and this does not* — decides the default posture: the wizard points, the human excludes.

**Skipped:** every suspect stays commanded, and the shipped safety net still catches it — the switch-on
note (`SwitchOnWarning`) appears under each room's switch after commit, naming the same suspects with the
same reasons. The wizard is the convenient moment, not the only one. The commit smallprint counts what was
left unreviewed (*"2 lights still look wrong — they will be flagged again on the room"*), one muted line.

**Phone:** cards stack full-width; keep/exclude is a two-segment control sized for a thumb.

### 2.4 Light sensors — C (renders only when a room has several, or the dial has sensors to draw)

Two halves, both graphic:

**The race** — each room with two or more illuminance sensors shows its candidates side by side as live
tiles, readings updating each second: *Kontor — vindu `45 lx` · pult `12 lx`*. Cover one with a hand and
watch it fall. The default is already right by engine rule — the room reads the average, and the tiles say
so — but one tap on a tile pins it as the room's sensor (`LuxSensor` staged), which is exactly the escape
hatch the resolver's own log message offers.

**The dial** — one horizontal band, logarithmic from 1 to 10 000 lx (linear would spend half the band on
daylight nobody tunes), decade-ticked, annotated with reference points (*moonlight 1 · lit room 50 ·
overcast sky 1 000 · daylight 10 000*). On it: every lux-carrying room's **live reading as a labelled
dot**, and the house's *dark below* default as a draggable brass token. Rooms left of the token count as
dark right now — the lx unit taught wordlessly, with the house's own numbers, no history required. (The
options doc wanted the house's own night/day ranges shaded from hours of readings; at first run there are
no hours. Live dots are the fallback *and* they are better teaching, because they move when the owner
moves a curtain. Shading the observed range on later visits is a noted enhancement, not part of this
build.) Dragging stages `Defaults.LuxThreshold`; tapping the token offers the curated shortlist, which is
also the accessible path on touch.

**Skipped:** averages stand; the 1 000 lx default stands. **Phone:** tiles pair up two across; the band
keeps its size and scrolls sideways — shrunk to 390 px its lettering falls below legibility, so it takes
the same accepted compromise the board makes — and the token's popover is the bottom sheet.

### 2.5 The room roll-call — E, and never folded

The report table, always visible — it is the page's centre, not a sheet. Grouped by the six floors through
`FloorGrouping` with the shipped `.floor-head` (and its one bulk action, *switch on this floor*), one row
per proposed room:

| switch | room + area id | lights | motion | light level | notes |

- **Light level** is the room's live reading (`12 lx`), not a sensor count — the same number the dial
  plots, so the two surfaces corroborate each other. A room without a sensor shows a muted —.
- **Notes** are verdict chips in state colours, and only where something needs saying: *"3 of 5 lights
  look like something else"* (`--warn`, tap opens the impostor sheet scrolled to this room), *"no
  light-level sensor — darkness judged by the sun"* (info), *"reads the average of 2 sensors"* (info),
  and the role guesses (*"bedroom manners"*, *"welcomes you home"*, *"stays on when everyone leaves"*) as
  quiet chips. **A row with nothing to say says "Ready", muted** — one word, not a green celebration
  seventeen times; the approved report's ok-coloured verdict column survives only where the verdict works.
- **Tapping a row** (not its switch) unfolds the room in place: its four behaviour sentences read-only
  (`AreaSentences.ForArea` over the proposed config — the role guess *is* the fourth sentence, so
  "bedroom manners" explains itself), its gear as chips with entity ids, and its suspects if any. The
  sentence conversation, hired at the row level.
- **Under the table, the near-miss line:** *"Bod and Teknisk rom have lights but nothing that senses
  movement, so they sit this out — give them a motion sensor in Home Assistant and press Set up rooms
  again."* Discovery's strict rule (a light **and** a motion sensor) made these rooms invisible; one muted
  sentence makes the invisibility inspectable instead of mysterious.

**The 17-row hard case, solved rather than asserted:** six floor groups turn 17 undifferentiated rows into
runs of at most five under sticky floor headers, so the table is navigated by floor, the way the owner
thinks about the house. On the phone each row folds to two lines — switch · name · live lx on the first;
counts and note chips on the second, "Ready" suppressed entirely (silence is the phone's verdict) — and
the commit button detaches into a sticky bottom bar the moment the first room is picked, so the thumb
never scrolls seventeen rows to find it. The mock-up renders all seventeen at both widths; this paragraph
is checkable against it.

**Skipped** (no room picked): commit stays disabled, the house stays deliberately dark, the state persists
— skipping the wizard's centre is allowed but cannot be mistaken for finishing it. If sheets were answered
but no room picked, a quiet text link offers *save these answers without switching anything on*.

### 2.6 Commit — E

One primary button, counting as it goes: **Switch on 9 rooms** — with the sub-line *"the other 8 stay
listed under House, each with its own switch."* Above it, the promise, stated once and meant literally:
*"Nothing in the house changes until this button."* Under it, the legend strip: the four state marks
(`i-auto` · `i-manual` · `i-dimming` · `i-off`) with one word each — the board's vocabulary taught at the
door, on the last surface before the board starts speaking it.

What the button does, in order:

1. **Applies the draft** to a working copy of the document — `ConfigName`, `Persons`,
   `AwayDebounceMinutes`, `HouseMode` (kept or nulled), per-room `ExcludeEntities`, per-room `LuxSensor`
   pins, `Defaults.LuxThreshold` if dragged, and `Enabled = true` on each picked room. Until this moment
   every answer lives in a `CommissioningDraft` in the circuit's memory and nowhere else.
2. **One `LightingEngineHost.Save`** — the only write path, unchanged: validate, and on refusal nothing
   is written, the message is shown, the wizard stands exactly as it was; on success write the YAML,
   re-read it, rebuild every area controller. The picked rooms go live inside this one call.
3. **The board takes over.** The awaiting state's condition is now false; the same route renders the live
   board. The chosen rooms' lanes appear with empty tracks — the epilogue — and a toast names the count.
   If someone is standing in a dark picked room, its light comes on within the tick: that is not a wizard
   step, that is the product starting work.

There is no partial commit and no second transaction. Rooms left off are switched on later from House ›
Switched-off rooms — which gains the same verdict chips this table uses, so the late rooms keep their
evidence (small addition, costed below).

---

## 3. The graphic devices, each tied to its step

| Device | Serves | Note |
|---|---|---|
| Big-number chips | findings strip | number + noun, glyphless; the one glyph marks the adopted-mode chip |
| Sentence with tokens | identity sheet | `SentenceView` verbatim; the grammar taught on day one |
| Person chips with live presence dots | identity sheet | the tracker shown working, not claimed; the parked car convicts itself |
| Mode sentences, read-only | mode sheet | `HouseSentences.Modes` — the owner's own dropdown read back in English |
| Evidence cards | impostor sheet | `LightAudit`'s reason sentences verbatim, beside the name they accuse |
| Blink to identify | impostor sheet, roll-call rows | pending question 1; the only device that touches the house |
| Live sensor race | sensor sheet | snapshots already flow; cover a sensor and watch it fall |
| The lux band | sensor sheet | log scale, reference points, live room dots, one draggable token — lx taught wordlessly |
| Verdict chips in state colours | roll-call | evidence on the row, not prose under it; "Ready" muted, silence on the phone |
| Row-unfold sentences | roll-call | the role guess explains itself as the fourth sentence |
| Progress = the switch count | commit | the button counts; there is no progress bar anywhere |
| State-glyph legend strip | commit | the four marks, one word each, once |
| Empty-lane epilogue | after commit | the live board itself; free |

The mandate — *graphic and informative without too many words* — is enforced by a rule the mock-up obeys:
outside the identity and mode sentences (which are the product's own grammar), no step contains a
paragraph. The longest prose anywhere is the two-line commit promise.

---

## 4. What the wizard writes, and when

**Nothing, until commit.** The draft is a value in the circuit's memory; the document on disk is the one
discovery wrote at first start (all rooms off), and it is not touched again until the commit button's one
`Save`. There is no per-sheet write, no autosave, no shadow copy in the browser. Two consequences, both
deliberate and one of them a question for the owner:

- A browser refresh or a closed tab loses unsaved answers. Blazor Server's reconnect covers the phone
  locking or the tab backgrounding mid-wizard; a genuine close costs at most two minutes of re-answering
  four short sheets. The alternative — persisting a draft — is a second place configuration lives and a
  second thing that can disagree with the document, which is the exact class of bug the single write path
  exists to prevent. (Question 3 offers the owner the localStorage variant with its cost stated.)
- The promise *"nothing changes until the commit button"* is true of the house **and** of the document.
  The one candidate exception is the blink verb, which is question 1 precisely because it bends this.

## 5. Deliberately not in the wizard

- **Schedule and defaults glances.** Both are read-only furniture; a step that asks nothing and changes
  nothing is a brochure page, and the House tab teaches both in place, editable, one tap away. The wizard
  keeps only surfaces that collect something.
- **The master-switch offer.** Resolved by reading the code, not by asking: the engine host already
  creates the built-in pause switch on every start (`GlobalConfig.DefaultKillSwitchEntity`) — offering to
  create one would offer what exists. Wiring a *custom* entity stays on House › Master switch.
- **Label names and device classes.** The impostor sheet applies exclusions without ever naming the
  exclude label; the strings live under Finding lights & sensors.
- **Per-room tuning.** Row-unfold is read-only (pending question 2). The room page is the tuner's home;
  the wizard is the commissioner's.
- **Mode remapping.** Confirm or detach only; option-by-option editing is the House tab's mode card.
- **The day-after debrief.** Resolved out on measured grounds: the debrief needs last night's history the
  morning after, and history reaches back to engine start only (`ui-design-c.md` §7 — a restart wipes the
  morning; persistence is explicitly out of scope). A feature gated on infrastructure this design does not
  build is not a step, it is a wish. The empty-lane epilogue covers the first hour honestly; the first
  *night* is the board's job already.

## 6. Re-entry, and a half-finished wizard

- **The wizard is a state, so "does Set up again replay it?" dissolves.** The commissioning board renders
  exactly while no room is on. Switch every room off and it returns; commit one room and it yields to the
  live board. *Set up rooms again* — the scoped rebuild in Areas with `SetupWarning`'s counted losses —
  stays what it is and never becomes the wizard: the rebuild has hand-picked entities and overrides to
  warn about, the wizard's premise is that nothing has been chosen yet. Two flows, two honest tones.
- **A replayed wizard is not amnesiac.** Sheets read their current answers from the document — the house
  name, the trimmed person list, the kept mode — and show them as their checklist status lines, so
  re-entry reads as review, not interrogation.
- **Interruption:** reconnect keeps the draft; close loses it (§4, question 3). The page never blocks
  navigation — the tabs stay live, and coming back to the board mid-commissioning finds the state exactly
  as the document has it: rooms off, wizard waiting.

## 7. Cost, per step

Sized against what ships. The engine changes **at most once** (blink, if approved); everything else is
Web-project work over existing projections, and the save path is untouched.

| Step | Reuses (exists today) | New | Size |
|---|---|---|---|
| Findings strip | `HaCatalog.AreaOption` counts, chip styling | `FindingsStrip.razor` | small — hours |
| Checklist frame | `.onboarding` state, fold styling | `CommissioningChecklist.razor` (items + status lines from the draft) | small — hours |
| Identity sheet | `SentenceView`, `SentenceBuilder`, `HouseSentences.AwayDebounce`, `.person-chip`/`.person-dot`, `ModeService.GetPeople` | `IdentitySentence.cs` (pure, tested); a free-text token kind for the name (small `SentenceTokenView` addition); chip toggling | medium-small — a day |
| Mode sheet | `HouseSentences.Modes`, `SentenceView Editable=false` | keep/detach buttons, staging | small — hours |
| Impostor sheet | `LightAudit.Review`, `SwitchOnWarning` wording precedent | `ImpostorSheet.razor`; suspects-across-rooms projection (pure, tested) | medium-small — a day |
| Sensor sheet | live reads via `IHaContext`, 1 s tick precedent, `TokenChoices` | `LuxBandScale.cs` (pure log-scale maths, tested like `CurvePath`), `LuxBand.razor`, race tiles | medium — 1–2 days |
| Roll-call | `FloorGrouping`, `.floor-head`/`.floor-bulk`, `.switch`, `AreaSentences.ForArea` + `SentenceView` for unfold, gear-chip styling, `HaCatalog` | `CommissioningReport.razor`; `CommissioningVerdicts.cs` (pure verdict projection, tested — the near-miss line included) | **the largest** — days |
| Commit | `LightingEngineHost.Save`, toast | `CommissioningDraft.cs` (pure staging record: apply-to-document, tested); sticky-bar CSS | medium-small — a day |
| Epilogue | the live board (C4) | nothing | free |
| Switched-off rooms verdicts | House section, `CommissioningVerdicts` | chip rendering there | small — hours |
| Blink (question 1) | `ILightActuator` | one engine verb (pulse + restore), a host entry point, a guard against firing on enabled rooms | medium, **the only engine change** |

Everything pure is testable without a render harness, which is the repo's rule: the verdicts, the draft,
the identity sentence, the band scale each get the same treatment `AreaSentences` and `CurvePath` already
have.

## 8. Decisions taken here that the options doc left open

- **Impostor exclusions are document-side, at commit.** Measured, not chosen: `IAreaRegistry` is
  read-only — the app has no path that writes labels into Home Assistant — and `AreaConfig.ExcludeEntities`
  exists for exactly this. The exclusion is visible and reversible in the app's own document, survives
  nothing it shouldn't (a per-room rebuild drops it, and the rebuild warning already counts it), and the
  label's name never appears. The open question's premise ("write labels as it goes") would have required
  a new HA write capability for a worse home for the fact.
- **Master-switch offer dropped** — the built-in switch already exists (§5).
- **Day-after debrief resolved out** — gated on history persistence that is explicitly out of scope (§5).
- **"Set up again" dissolves into the state model** (§6).
- **Commissioning does not assume a desk** — the phone layout is designed, not apologised for (§2.5), and
  the mock-up shows every step at 390 px.
- **"Ready" is quiet.** Muted word on desktop, silence on the phone. Seventeen green verdicts would be the
  reassurance dashboard sneaking back in through the one door the design guards.
