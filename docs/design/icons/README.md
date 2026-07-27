# Section icons

Illustrative icons for the AdaptiveLighting UI — nav rail, section headers, and the room-state
glyphs — drawn in the ZeroZero Software icon technique (`0z0-design/icon-library.md`).

**These are now wired in.** `src/AdaptiveLighting.Web/Components/IconSprite.razor` carries the
shipping set as inline `<symbol>`s, and `docs/design/visual-foundation.md` §3 is the usage
contract — including why a `<use>` must never point at a sibling `.svg` file. The files here stay
the drawing source: edit one, then copy its body into the sprite.

Open [`contact-sheet.html`](contact-sheet.html) to see every glyph at real sizes on both the
dark and light app palettes, including the alternatives that were drawn but not recommended.

## Rules taken from the library (stated explicitly there)

- `viewBox="0 0 24 24"`, flat geometric glyphs.
- Two-tone: one outline colour for strokes, one or two flat accent colours for small filled
  details (dots, an interior fill, a highlight line).
- No gradients; stroke width ~1.6–2 px; round line caps/joins.
- An adopting app substitutes its **own** product palette — the technique is shared, the
  colours are not. ChargeKeeper's SteelBlue/Sage/Terracotta and its feature glyphs are not
  reused here.

## Rules inferred (demonstrated, not stated — correct me if wrong)

- **Accent strokes are allowed, not just accent fills** — `about.svg` and `smart-charge.svg`
  both use an accent-coloured line. Used here for the power glyph, dial pointers and needle.
- **One accent per icon is the norm** — most library icons use a single accent plus outline;
  two accents are the exception. I stayed at one throughout.
- **Content sits inside roughly a 3.5–20.5 unit live area** with circles at r ≈ 7.6–7.8 —
  matched from `about.svg`/`appearance.svg`.
- **Bolder strokes below ~16 px** — inferred from the logo's favicon cut
  (`logo/GUIDE.md`: bracket 15→22, ring 10→17). The three state glyphs, which live on 12–14 px
  chips, are drawn at stroke 2 instead of 1.7.
- **No editor metadata, single-line SVG, presentation attributes on the root** — matched from
  the existing files.

## Deliberate deviation: `currentColor` instead of hard-coded hexes

The library's icons hard-code their two-tone hexes (ChargeKeeper is a WinUI app with one
theme). This app is themed light **and** dark, so hard-coded colours cannot work:

- Outline: `stroke="currentColor"` — the icon takes the text colour of wherever it sits.
- Accent shapes: `fill|stroke="var(--icon-accent, currentColor)"` — the app sets
  `--icon-accent: var(--accent)` (or any semantic token) in CSS; with nothing set, the icon
  collapses to legible monochrome, because every accent is a *filled* shape against a
  *stroked* outline (or a clearly separate line).

The two-tone **technique** survives; only the colour *binding* moved from the SVG into CSS.
This is the same product-palette substitution the library already demands, done at runtime.

## The set and its metaphors

The brief asked for more than five bulb variations — the metaphors come from what the app
actually does: rooms lit by movement, held back by daylight, dimmed as a warning, backed off
for people. There is exactly one bulb-adjacent shape in the set (the app mark's light point),
and no bulbs.

| File | Metaphor | Why |
|---|---|---|
| `app-mark.svg` | A point of light answering motion waves — lamp left, sensor arcs right | The whole product in one glyph: light that responds to movement |
| `dashboard.svg` **(ships)** | Four room tiles, one lit | The dashboard literally is a grid of rooms, one or more currently on |
| `dashboard-alt-gauge.svg` | Gauge with needle | "Live reading" — honest but promises a single number, and the page is a grid |
| `areas.svg` | Floor plan: three rooms, a door gap, light on in one room | Rooms as *places* with lights and sensors in them, not devices |
| `schedule.svg` **(ships)** | The circadian brightness curve with the sun at solar noon | It is the exact chart the Schedule page draws |
| `schedule-alt-dial.svg` | 24-hour dial, hand at afternoon | Reads "time" faster but "clock" is weaker than "curve" for a brightness-over-day page |
| `house-modes.svg` **(ships)** | Rotary selector: four positions, pointer on the active one | One mode at a time, chosen deliberately — Home/Away/Sleeping/Guests |
| `house-modes-alt-segments.svg` | Segmented switch, first position engaged | Same idea, flatter; the dial has more character at header size |
| `house.svg` **(ships)** | House with the master switch inside | The installation itself, which can be switched on and off as a whole |
| `house-alt-residents.svg` | House with a resident inside | "Who lives here" — warmer, but the master switch is the section's headline control |
| `state-auto.svg` **(ships)** | Circular loop around the light point | The automation running by itself |
| `state-auto-alt-letter.svg` | Circled A | Phone auto-brightness convention; fits the monospace voice, but reads as a letter, not a state |
| `state-manual.svg` | Person over the light | A human set this — pairs with the app's `--human` token |
| `state-off.svg` **(ships)** | Circle struck through | "None/disengaged"; the slash angle deliberately echoes the `[Ø]` mark's amber slash (echo only — the studio mark itself is never used as app iconography, per `logo/GUIDE.md`) |
| `state-off-alt-power.svg` | Power symbol | Universal, but reads "toggle me" rather than "currently off", and would collide with the master-switch glyph inside `house.svg` |
| `lanes.svg` **(ships)** | Three tracks of unequal length, cut by an accent now-line | Commissioned by `ui-design-c.md` §6.2, which refused *both* dashboard glyphs: the home surface is a timeline, not a tile grid or a single reading, and a wrong metaphor is worse than none. The tracks are unequal and the now-line crosses two of them, so it cannot be misread as a text-alignment mark |
| `state-dimming.svg` **(ships)** | A light with a quarter of its time left | The fourth state glyph §6.2 held in reserve. The design shipped the warning dim as the auto loop in amber, blinking, and called the shared shape a compromise — but a shared shape means "lit and settled" and "about to go dark" are one picture in greyscale and to a colourblind reader, which is the failure these glyphs exist to fix. Drawn new rather than bending an existing one; the amber and the blink still apply. The draining ring rhymes with the countdown the room header already draws |

The state glyphs exist because automatic / set-by-hand / off are currently distinguished by
colour alone, which fails for colourblind users and in greyscale screenshots. Each state now
has a distinct *shape*; the chip's semantic colour (`--machine` / `--human` / `--idle`)
flows in through `currentColor` so shape and colour carry the same message.

## Usage sketch (for the wiring job, not done here)

```css
.nav-icon { color: var(--muted); --icon-accent: var(--accent); }
.chip.machine .ic { color: var(--machine); --icon-accent: currentColor; }
```

Inline the SVG (or `<use>` a symbol sprite) so `currentColor` and the variable can reach it —
`<img src>` isolates the SVG from page CSS and gets the monochrome-black fallback.

## Ambiguities found in the library

- The library never states a grid/live-area rule or an optical-size rule for icons; both are
  inferred above from examples and from the logo's favicon cut.
- `icon-library.md` names ChargeKeeper's `Assets/nav/` as the authoritative source and warns
  the `0z0-design` copy can go stale — these icons were matched against the `0z0-design`
  copies (2026-07-15), the newest available here.
- Whether an app with *semantic* state colours should use them as icon accents (as the state
  glyphs do via `currentColor`) is not covered by the product-colour rule; I treated semantic
  tokens as part of this app's product palette.
