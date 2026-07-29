# First-run wizard — the questions only you can answer

**Answered by the owner, 2026-07-29.** Each question keeps its options for the record; the choice is
marked **Chosen** beneath it, and the design doc reads as settled
([`first-run-wizard.md`](first-run-wizard.md) §9 collects the fold-in).

Four. Everything else the options doc left open is resolved in
[`first-run-wizard.md`](first-run-wizard.md) §8, from the code or by designing it: impostor exclusions go
into the document at commit because the app has no way to write labels into Home Assistant at all; the
master-switch offer is dropped because the engine already creates the built-in pause switch on every
start; the day-after debrief is out because it needs history that survives a restart, which nothing
persists; "Set up again" dissolves because the wizard is a state that returns exactly when no room is on;
and the phone layout is designed rather than asked about — commissioning does not assume a desk.

Each question below actually changes what gets built. Options are mutually exclusive; the recommendation
is marked.

---

## 1. May the wizard blink a real light before any room is on?

A *blink* button on each impostor card (and each room row) would pulse that one entity once, so "is this a
light or a router LED" is answered by looking up instead of by reading entity ids.

- **Blink on request** — one new engine verb (pulse, then restore), the only engine change in the whole
  wizard. A light flashes only in the second you press the button; the promise becomes "nothing changes
  *by itself* until commit".
- **No touching until commit** — the promise stays absolute and the engine stays untouched. Doubtful
  entities are identified by name and floor, or after commit by the room's switch-on note.
- **Blink impostors only** — same engine verb, offered only on the impostor sheet. Room rows stay
  look-don't-touch; identifying an unlabelled *room* still falls back to names.

**Recommended: Blink on request.** You press it, it flashes, it stops — that is a torch, not an
automation, and it turns the wizard's hardest identification job into a glance.

**Chosen: Blink on request** — on impostor cards *and* room rows. The verb is specified in
`first-run-wizard.md` §2.7: off/on behaviour, what restores, compare-before-restore, the server-side
sequence that survives a browser disconnect, and the self-echo registration that keeps a pulse from ever
being read as a hand at a switch.

## 2. May the wizard change how a room behaves, or only choose rooms?

Tapping a roll-call row unfolds the room's four behaviour sentences — including the guessed flags, like a
bedroom's "never auto-on while the house sleeps".

- **Read-only sentences** — the unfold shows behaviour but edits nothing; a wrong guess is fixed on the
  room's page after commit. Cheapest to build, and the draft stays four global answers plus switches.
- **Editable tokens in the unfold** — fix the bedroom flag right there, before it ever runs. The draft
  grows per-room overrides (more to build, more to lose if the browser closes), and the wizard becomes a
  second room editor to keep in step with the real one.
- **No unfold at all** — the table stays a table; roles appear only as note chips. Least to build, but the
  fourth sentence is what makes "bedroom manners" self-explanatory.

**Recommended: Read-only sentences.** The wizard collects what discovery cannot know; behaviour it merely
*guessed* is visible here and editable one tap later, where the room page already does it properly.

**Chosen: Read-only sentences — with a condition.** In the owner's words: *"Read-only, but keep editable
as a future option after I see it in action."* So the unfold renders through the real `SentenceView`
token machinery with `Editable="false"` — never flattened to prose — and turning editing on later is a
switch, not a rewrite. Deferred, not rejected; `first-run-wizard.md` §2.5 names what the editable
variant would need (per-room overrides on the draft) and the discipline that keeps the wizard from
becoming a second room editor.

## 3. Should a half-finished wizard survive closing the browser?

Answers live in memory until commit. A locked phone or a dropped connection reconnects and keeps them; a
genuinely closed tab loses them.

- **Lost on close** — nothing new to build, and configuration continues to live in exactly one place. The
  worst case is re-answering four short sheets, about two minutes.
- **Draft in this browser** — answers also mirror into the browser's local storage and are offered back on
  return. Survives a close, but creates a second copy of half-made configuration that can sit stale for
  weeks and resurface after the house has moved on.

**Recommended: Lost on close.** Two minutes of worst case is a better price than a shadow document, and
the commit-button promise stays simple enough to state in one line.

**Chosen: Lost on close.** As recommended. The one carve-out is §2.7's: a blink pulse in flight still
completes its restore whatever the browser does, because it is house state, not an answer.

## 4. How much light-sensor teaching should the wizard carry?

The sensor sheet has two halves: live side-by-side readings for rooms with several sensors (tap to pin
one, or keep the engine's average), and the lux band — every room's live reading plotted on a 1–10 000 lx
scale with the "dark below" default as a draggable marker.

- **Races and the band** — the full sheet, one to two days of build. Lux is taught wordlessly with the
  house's own live numbers, and the dark-below default gets its one graphic home.
- **Races only** — half a day less; multi-sensor rooms still get settled at the door. Dark-below stays a
  number in a row on House, learned the first time a room refuses to light on a bright afternoon.
- **Neither** — the sheet does not exist; averages and defaults stand silently. Cheapest, and the wizard's
  most graphic device is gone.

**Recommended: Races and the band.** It is the one place the mandate — graphic and informative, few words
— gets to teach the unit the whole darkness model runs on, and it is skippable for anyone who already
knows it.

**Chosen: Races and the band.** As recommended — the full sensor sheet ships.
