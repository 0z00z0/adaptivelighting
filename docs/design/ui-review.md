# UI review — a clean-sheet look at the web interface

Two interactive mock-ups accompany this document:

- **In the Room** — https://claude.ai/code/artifact/d05ca071-acb7-45e3-aa0a-7b861e5a2216
- **The Timetable** — https://claude.ai/code/artifact/136ed95b-3c2a-470e-97c4-e0046b626054

Both are self-contained multi-page mock-ups: main view, room view, settings surface and
first-run state, navigable, phone-width first, light and dark.

---

## 1. What the current UI gets wrong

The current pages are carefully written — the copy is honest, the empty states are ordered
correctly, the validation is real. The problems are structural, not cosmetic.

**The dashboard and the settings page are two descriptions of the same house, and the
person is always on the wrong one.** The scenario the product itself names — standing in
the room, on a phone, wanting the hallway light to stay on longer — takes this path today:
Dashboard → find the card → realise cards are read-only → Configuration → Areas → find the
room in a second, differently-shaped list → open the fold → find *Lights stay on for* →
change it → scroll to the save bar → *Save and apply*. That is seven steps and two mental
models for a one-number change. The dashboard's `AreaCard` displays state a household
member cannot act on; the settings page's `AreaEditor` edits state it cannot show live.
Every room therefore exists twice, and the code works hard (shared `AreaView.Family`,
shared `FloorGrouping`) to stop the two copies drifting — effort that exists only because
the split exists.

**The area card is a debugging instrument wearing a reassurance costume.** One card
carries: a badge, a headline sentence, an event line with relative time, a next-change
line, a countdown bar, a three-cell `dl` of stats (motion / command / next), and five
metadata chips including `reported 21:36:48`. That is the right density for the owner
diagnosing a sensor at his desk, and roughly 4× the right density for anyone else. With 17
rooms on a phone, the dashboard is a very long scroll of prose in which "everything is
fine" and "something needs you" look nearly identical. The card answers *"what exactly did
the engine see?"*; the household question is *"is anything odd, and when will this light
go out?"* — one line, not eleven.

**Whole-document save is a transaction imposed on people who wanted a knob.** The save
bar, `Discard changes`, "3 problems to fix before saving", and the rule that an error in a
section you are not looking at blocks the save — all of this is correct for editing a YAML
document, and the page *is* editing a YAML document. That is the problem: the document
leaked into the interface. A household member changing a timeout should not be exposed to
the concept of an invalid sibling section.

**Inheritance is the product's best idea and the UI whispers it.** Every per-room setting
is a nullable twin over `Defaults` — a genuinely good model. But the UI expresses it as an
"All rooms" `GroupFold` *below* the room list (settings for everything, placed after the
things they set), plus a per-room "strays from the all-rooms settings" hint. Nothing at
the point of editing says *"this value is the house's, that one is this room's own, and
here is how you give it back."*

**First-run points away from itself.** The onboarding state renders room chips and then
sends the user to the settings page ("Choose which rooms to switch on") — into the most
complex screen the product has, on day one. The chips themselves are inert; the one thing
the user must do (turn rooms on) is not doable where the invitation is issued.

**Smaller strains, named:** the master switch's kill-switch polarity dropdown ("read as an
enabled flag — off kills the engine") surfaces an internal double negative nobody should
meet; `HouseModeOptions` asks the user to classify select options with kinds, clamp
periods, grace minutes and reset triggers as a form, when every one of those is a sentence
the engine could say; the Schedule section draws a `DaylightChart` but edits through bare
numeric inputs beside it — the picture and the controls never touch.

---

## 2. The two designs

### Design A — "In the Room"

**Thesis: the room you are standing in is the whole interface — every setting lives on
the thing it affects, and changes apply the moment you make them.**

One phone-first surface. The home screen is a compact room list — glow dot in the light's
actual warmth, name, one status phrase ("On 70 % · dims in 6 min", "Set by hand · auto at
22:20") — with the house mode and master pause above it. Tapping a room opens the room:
a plain-language state sentence with a countdown ring, quick actions ("Resume auto now",
"Pause this room 1 h"), then that room's settings as rows you edit in place. Inherited
values render muted with a "house default" tag; touching one makes it the room's own,
marked with a dot and a one-tap "Use house default (10 min)" way back. Every change
applies immediately with an undo snackbar — no save bar, no document, no transaction.
House-wide things (rhythm, modes, people) live on a second, much smaller screen; engine
internals are one folded row at the bottom of it. First-run is the home screen itself in
its empty state: "Found 8 rooms", each with a switch, nothing styled as an error.

**The bet:** almost every visit to this UI is about exactly one room, made standing in
it. Merge state and control onto the room and both pages of the current app collapse into
one object.

**What it gives up, honestly:** the at-a-glance whole-house view is thinner — you cannot
see every room's full story on one screen, and the event-log/diagnostic depth (transition
reasons, timestamps, darkness details) is gone from the surface entirely. It would need a
per-room "history" drawer for the owner's debugging sessions. Instant apply also means
misedits happen live; undo has to be taken seriously. Batch edits ("set all basement
timeouts to 3 min") get slower.

**Who it suits:** the household. This is the design a guest can use.

### Design B — "The Timetable"

**Thesis: the primary object is not the room but time — one board that shows every room
on a shared time axis, what happens next, and the rules, written as sentences, that will
make it happen.**

The main view is a board: a clock-and-facts header, an exception tray, and room swim-lanes
under a schedule band. Each lane shows the recent past as blocks (lit in the actual
commanded warmth, violet when a hand intervened, amber for warning dims, hatched when
paused) and the future as dotted marks ("22:20 auto resumes", "22:30 → night 15 %"), with
a now-line through everything. The dark-cockpit rule governs colour: a room doing what the
timetable says is a quiet grey row; only exceptions light up, and they are also collected
into the tray on top — the board is scannable in one second *because* normal is invisible.
Clicking a lane opens the room's dossier: a facts table, the event log (the
`TransitionReason` stream, finally given a home), and the room's behaviour written as
sentences with editable tokens: *"After **10 min** without movement, dim to **50 %** for
**30 s**, then off."* Tokens that differ from the house rule are amber. Edits collect in a
pending-changes drawer — reviewed as a diff, applied in one deliberate step — keeping the
current architecture's validate-then-write model but making the transaction visible
instead of ambient. First-run is a commissioning report: a table of discovered rooms,
their gear, and a verdict column ("Ready", "No motion sensor — would never auto-on"),
ticked and committed like an acceptance test.

**The bet:** the system's real substance is temporal — periods, timeouts, holds, resets —
and the current UI hides that substance in numeric fields scattered across sections. Put
time on the axis and rules in sentences, and both *what happened* and *what will happen*
become legible for the first time.

**What it gives up, honestly:** it is a denser, more literate interface — a guest will
not wander into the rules page and feel at home. On a phone the board works (the lane area
scrolls sideways) but it is a desktop/wall-panel design at heart. And the review-then-apply
drawer, while safer, keeps a two-step model that Design A proves unnecessary for
single-knob changes.

**Who it suits:** the owner, a wall-mounted tablet, and anyone asking "why didn't the
light come on last night?"

---

## 3. What was borrowed, and why it applies

- **Nest / Tado (thermostats):** one hero state per room and settings *behind the thing
  itself*, not in a parallel settings app. Lighting has the same shape as heating: a
  target, a schedule, an override. → Design A's room screen.
- **Philips Hue:** the room list whose indicator glows in the light's actual colour —
  state you can verify by looking up from the phone. The engine already publishes Kelvin
  and brightness, so the glow is data, not decoration. → both designs' dots/blocks.
- **HomeKit / Google Home:** instant apply with undo, instead of edit-then-save. A home is
  manipulated, not configured. → Design A's snackbar model (undo pattern from Gmail).
- **Xcode / IntelliJ preference "modified" markers:** the muted-inherited vs.
  marked-overridden rendering, with an explicit "reset to default" affordance — the
  cleanest known UI for a nullable-twin settings model. → Design A's setting rows,
  Design B's amber tokens.
- **Apple Shortcuts / IFTTT sentence builders:** rules as editable prose. The config
  model's `VacancyTimeoutSeconds` + `PreOffSeconds` + `PreOffBrightnessFactor` is one
  sentence in disguise; showing the sentence removes the need for three help popovers.
  → Design B's rules, and the mode reset-trigger sentences.
- **Flight departure boards / Home Assistant's history view / Gantt lanes:** rooms as
  swim-lanes on a shared time axis, future events as marks. → Design B's board.
- **Industrial annunciator panels ("dark cockpit" principle):** nothing lights up when
  everything is nominal, so anything lit *means something*. The current dashboard's
  uniformly rich cards are the opposite of this. → Design B's row colouring and exception
  tray.
- **Ecobee's schedule band:** the day as a coloured band you read left-to-right, with the
  editing controls attached to the band rather than beside it. → both designs' rhythm
  views; Design B makes the band's values the editable tokens.

---

## 4. Recommendation

**Build "In the Room", and steal two organs from "The Timetable" while doing it.**

The deciding facts are in the brief the product sets for itself: two houses, one with 17
rooms; used on phones as often as desktops; must be operable by someone who was never
taught it, typically while standing in the room being configured. Every one of those facts
points at A. The two-page split is the current UI's root defect, and A is the design that
actually removes it — B reorganises brilliantly but still separates the board from the
rules. A guest can use A; that was a stated requirement and only A meets it.

What A must take from B, because A's honest costs are real and B has already paid them:

1. **The event log.** B's dossier log (timestamped `TransitionReason`s in plain language)
   goes into A's room screen behind a "What happened here" row. This is the answer to A's
   biggest sacrifice — debuggability — at almost no surface cost.
2. **The exception tray.** A's room list should hoist its exceptions (hand-set, warning
   dim, overdue) to the top the way B's tray does, so the phone answers "anything odd?"
   without scrolling 17 rows.

The sentence-shaped settings from B are worth adopting inside A opportunistically — the
pre-off trio and the mode reset triggers first, since those are the settings whose numeric
form is least self-explaining.

**What would change my mind:** evidence about who actually opens this UI. If, six months
in, the visit log shows it is overwhelmingly Espen at a desk asking *why* something
happened — and household members interact only through wall switches and the HA app — then
the guest-usability requirement is theoretical, A is optimising for a user who never
arrives, and B's board-plus-dossier is the better home for the person who does. A
wall-mounted tablet in either house would tilt the same way: B is the design that earns a
permanent screen.
