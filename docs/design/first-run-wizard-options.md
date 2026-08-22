# First-run wizard — a menu of options

Planning only. Discovery has already run: the wizard opens on **"17 rooms found, none on"**, extends the
approved set-up report (`ui-design-c.md` §5.4), and ends at the one commit button. It shows what was found,
takes corrections, switches rooms on, and sets the few things discovery cannot know — it collects no facts
the app can discover. **★ = would ship by default.** Shapes are either/or; steps and devices combine freely.

## Overall shape — what kind of thing is it?

- ★ **Commissioning checklist on the board.** The awaiting-room-choice state grows a short checklist above
  the report — *Name it · Who lives here · Impostors · Rooms* — each item a focused sheet, done in any order,
  done-state chips. Cheapest that still guides; reuses the report and the board's empty state; nothing modal.
- **Linear stepper.** Full-screen steps, progress dots, back/next/skip. The most "wizard"; best home for the
  narrative devices (findings strip → commit); costs a new shell and imposes an order the facts don't need.
- **One scrolling page.** The report annotated: sections in commit order, sticky progress rail, commit at the
  bottom. Nothing to navigate; long on a phone, and skipping is scrolling.
- **Proposal-mode board — no wizard at all.** Lanes render grey with switches; the global questions sit in
  the exception tray as chips until answered. Looks most like the product; needs the board built first, and
  the tray was never designed to take input.
- **Sentence conversation.** Setup as `SentenceView` sentences with the unguessable values as tokens —
  *"This house is called `___`. `Ada` and `…` live here; it counts as empty after `5 min`."* — confirm
  each. Nearly free (SentenceView ships), teaches the app's grammar on day one; weak for the 17-row room
  table, so it pairs with a shape above rather than standing alone.

## Steps — what it shows / what it asks

**E** essential · **S** skippable · **C** conditional (renders only when relevant).

- ★ **Findings strip** — glyph + big-number chips: rooms, lights, sensors, people, the adopted mode select.
  Asks nothing; orientation in five numbers. **S** (can be the report's header rather than a step)
- ★ **House identity** — one sentence: house-name token, person chips toggled in/out, empty-house delay
  token. The only facts discovery truly cannot know. **E**
- **Mode adoption confirm** — the adopted `input_select` and how its options were classified, as chips;
  confirm or detach. **C** (only when one was adopted)
- ★ **Room roll-call** — the report table (switch · room · lights · motion · lux · verdict), grouped by
  floor with *switch on this floor*. The wizard's centre. **E**
- ★ **Impostor hunt** — suspected non-lights (network-gear status LEDs) ranked by heuristics; one tap
  excludes. Verdict chips carry the evidence ("no brightness · 'LED' in the name"). **C** — the step that
  earns the wizard its place.
- **Lux calibration** — the dial (below) drawn against the house's own outdoor sensor; confirm or drag the
  dark-below default. **S** (defaults survive skipping)
- **Sensor pick** — rooms with two or three light-level sensors show live readings side by side; tap the one
  that means the room. **C**
- **Schedule glance** — the daylight band with the seeded periods drawn on it, read-only; "adjust under
  House". **S**
- **Defaults glance** — the four default sentences, bold and read-only; a glance, not an editor. **S**
- **Master-switch offer** — one yes/no: "create a pause switch in Home Assistant?" Wiring stays on
  Configuration. **S**
- ★ **Commit** — one button naming its counts ("Switch on 9 rooms — the other 8 stay listed under House"),
  repeating the promise: nothing changed until now. **E**
- **Day-after debrief** — tomorrow the board offers one card: what each new room did on its first night. The
  wizard's last step happens after the wizard. **S**, scope call

## Graphic devices — saying it without words

- ★ **Big-number chips** — a glyph, a count, a noun; no paragraph. (§6.2's refusal of icons on report *rows*
  stands: glyphs mark sections, counts stay text.)
- ★ **Verdict chips in state colours** — "no motion sensor — would never auto-on" as a `--warn`-family chip
  on the row, not prose under it.
- ★ **The lux dial** — a band 0 → 4 000 lx with the house's own night (1–3 lx) and day (≈3 700 lx) shaded
  from real readings, a live now-marker, the threshold as a draggable token. Teaches lx wordlessly. Wants a
  few hours of readings; falls back to the live value alone.
- **Blink to identify** — tap a room or a suspect light: it pulses once. "Which room is 'gang'" and "is this
  a light or a router LED" answered without a word. New engine verb, and it touches lights before consent —
  see open questions.
- **Live sensor race** — multi-sensor rooms show the candidates' readings updating side by side; cover one
  with a hand and watch. Cheap; snapshots already flow.
- ★ **State-glyph legend strip** — the four state marks with one word each, shown once at commissioning: the
  product's vocabulary taught at the door. (Reuses the board legend.)
- **Before/after sentence** — a bedroom's sentence with its role-guess flag off, then on: the fourth
  sentence appearing *is* the explanation of what a role means.
- **Person chips with live presence dots** — the tracker working is shown, not claimed.
- **Kelvin swatches** — warmth rendered as the actual colour, never a number.
- **Progress = the switch count** — the commit button counts "9 of 17 on"; no progress bar.
- **Empty-lane epilogue** — after commit, the chosen rooms' lanes appear with empty tracks: the quiet start
  shown, not promised.

## Where the globals land

**Wizard material**

- **House name** — one token; personalises every sentence after it.
- **Who lives here + empty-house delay** — the facts discovery cannot know; chips plus one token.
- **Rooms on/off** — the wizard's whole point.
- **House-mode adoption** — confirm the guess once; remapping stays on Configuration.
- **Exclude label — the *act*** — the impostor step applies it; the label's name never appears.
- **Lux dark-below default** — only via the dial; skippable, the default stands.
- **Master switch** — a yes/no offer at most; entity wiring stays on Configuration.

**Stays on Configuration**

- **Label names & device classes** (include / exclude / motion, illuminance) — plumbing; the wizard uses
  their effects, never shows the strings.
- **Room defaults** (stay-on, warning dim, hand-hold, how a room decides it's dark) — sane out of the box;
  at most the read-only glance.
- **Schedule editing** — the wizard shows the band; the token table lives under House.
- **Mode hours & options** — seasonal tuning, not commissioning.
- **Fine tuning** (user id, tick, echo window, tolerances, automations-as-manual, blend) — never wizard
  material.

## Open questions

- May the wizard **blink real lights** to identify rooms and impostors before any room is on? It bends
  "nothing changes until you choose".
- Does **Set up again** replay the wizard, or just the report?
- May the impostor step **write labels into Home Assistant** as it goes, or only at commit?
- Is the **day-after debrief** in scope?
- May commissioning **assume a desk**? The table-heavy steps are honest on desktop, cramped on a phone.
