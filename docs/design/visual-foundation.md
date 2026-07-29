# The visual foundation

The shared vocabulary every page of this UI speaks: the design tokens, the icon set, the state chip,
and `SentenceView`. It implements §6 of [`ui-design-c.md`](ui-design-c.md) ("The Workshop") against the
mock-up at [`mockups/the-workshop.html`](mockups/the-workshop.html).

**If you are building a page, this file is your contract.** Reach for something here before inventing it;
four pages each inventing a chip is how one product becomes four.

---

## 1. The four rules

Everything below follows from these. They are also written at the top of `app.css`, where the tokens are.

1. **Flat.** Surfaces are a 1 px line on a flat ground — the same outline-on-nothing the icons are drawn
   with. Elevation is information, so only genuinely floating layers get `--shadow-float`: the token
   popover, the info popover, the undo toast. A **glow** reporting a light's actual warmth is data, not
   elevation, and stays.
2. **Geometric.** Radii come from the icons' own `rx ≈ 1.5–1.7` on 24 units: panels 8, controls 7, chips 6.
   **The pill is not a default.** It survives only where the shape *is* the meaning — a switch.
3. **One accent.** `--accent` (brass) marks **editability and primary action** and nothing else: sentence
   tokens, steppers, the active tab, the commit button. **State is never accent.** `--machine`, `--human`,
   `--warn`, `--idle` and `--now` are semantic and reach icons through `currentColor`.
4. **One stroke grammar.** Icon strokes (1.7, or 2 below ~16 px), control borders (1 / 1.5 px) and the 2 px
   now-line are one family of lines, not three separate decisions.

---

## 2. The tokens

Every token is defined in **every** theme — there are three. `prefers-color-scheme` is the default;
`:root[data-theme="light"]`, `:root[data-theme="dark"]` and `:root[data-theme="0z0"]` force one and outrank it.
**A new theme defines the whole table below, not the tokens that seem to matter**: anything left out falls
through to the bare `:root` block, which is the dark palette, and the omission shows up on one page, in one
state, on somebody else's screen.

The theme is chosen in the top bar and kept in this browser (`localStorage`, key `adaptive-lighting-theme`).
The list, and what an unrecognised stored id falls back to, are `AppThemes` in C# — see §8.

### Surfaces

| Token | For |
|---|---|
| `--bg` | the page behind everything |
| `--panel` | a card, a board, a fold — the surface content sits on |
| `--panel-2` | a recessed strip inside a panel: a bar, a group head |
| `--chip` | inset fill: chips, sentence tokens, badges, stepper wells |
| `--lane` | a track a timeline is drawn on |
| `--glow-track` | the unlit part of a ring, bar or lamp |

### Lines — two weights, and the difference is load-bearing

| Token | For |
|---|---|
| `--line` | the edge **of** a surface: panel border, control border |
| `--grid` | a hairline **inside** one: row separators, chart gridlines |

Shorthands: `--rule` (`1px solid var(--line)`) and `--rule-inner` (`1px solid var(--grid)`).

### Text

`--text` · `--muted` · `--accent-ink` (text **on** `--accent`, never text near it).

### The one accent

`--accent` — brass. Editability and primary action only. See rule 3.

### Semantic state — never the accent, never each other's hue

| Token | Means | Soft twin |
|---|---|---|
| `--machine` | the engine acting (a lit room's own Kelvin overrides it) | `--machine-bg` |
| `--human` | a person acted — **violet** | `--human-bg` |
| `--warn` | the warning dim, and anything asking to be noticed now | `--warn-bg` |
| `--ok` / `--bad` | healthy / broken | `--ok-bg` / `--bad-bg` |
| `--idle` | watching without commanding | `--idle-bg` |
| `--now` | the live edge: the now-line, the RUNNING badge | — |
| `--info` | a neutral remark. **Not a state.** | — |

> **`--human` is violet, not amber.** The shipped UI used amber for "a human did this"; the approved design
> moves it to violet so that "somebody touched it" and "look at this now" can never be read as the same
> colour. This is done now rather than when the board ships, so the language changes once.

### Icons

`--icon-accent` is set per theme to `var(--accent)`. State chips override it to `currentColor` at the point
of use, so a glyph's outline and its accent are one semantic colour. Never hard-code a colour into a glyph.

`--kelvin-weight` / `--kelvin-shade` keep a Kelvin tint legible in both themes — see §4.

### Radius, stroke, spacing, type

```
--radius-panel  8px    cards, panels, the board, folds
--radius-control 7px   buttons, inputs, steppers, segmented controls
--radius-chip   6px    chips, sentence tokens, badges
--radius-switch 999px  TRUE SWITCHES ONLY
--radius               the pre-token name, now pointing at --radius-panel

--stroke-hair    1px   a hairline inside a surface
--stroke-control 1.5px a border on something you press
--stroke-mark    2px   a mark on a chart: the now-line, a future tick

--space-1..6     4 6 8 12 16 24
--text-xs        11px    the uppercase section label
--text-sm        12.5px  help lines, meta, timestamps
--text-md        13.5px  dense body: settings rows, log lines
--text-base      15px    body
--text-lg        19px    page and card headings
--text-xl        24px    the one number a page is about
--label-track    0.14em  letter-spacing on uppercase labels
```

`--font-sans` and `--mono` are the two families. `--shadow-float` is the only shadow.

### Names that differ from the mock-up

CSS copied from `the-workshop.html` needs three renames: `--ink` → `--text`, `--panel2` → `--panel-2`,
`--*-soft` → `--*-bg`. Everything else carries the same name.

---

## 3. Icons

**15 hand-authored glyphs live in [`icons/`](icons/); 11 ship.** They are drawn in the `0z0-design` house
language — 24×24, flat geometric, a `currentColor` outline plus **one** flat accent bound to
`var(--icon-accent, currentColor)`, stroke 1.6–2, round caps. The rules are in
[`icons/README.md`](icons/README.md).

### Using one

```razor
<Icon Name="@Glyph.Schedule" Class="ic-head" />
```

- `Name` — always a `Glyph` constant. A mistyped id renders an **empty box in silence**, with nothing in the
  console; the constants turn that into a compile error.
- `Class` — `ic-sm` (15 px), default (18 px), `ic-lg` (22 px), `ic-head` (17 px, muted, for a card heading).
- `Label` — set it only when the glyph is the *only* thing saying what it means. Icons are decorative and
  `aria-hidden` by default, because an icon beside its own label is repetition to a screen reader.

Colour and size are the caller's context, not the glyph's: it takes whatever colour it lands in through
`currentColor`, and its accent from `--icon-accent`.

### Adding one

1. Draw it into `docs/design/icons/<name>.svg` following the README: 24×24, one accent, stroke 1.7 (2 if it
   will be used below ~16 px), content inside roughly the 3.5–20.5 live area.
2. Paste the body into `Components/IconSprite.razor` as a `<symbol id="i-<name>">`, keeping the source file's
   presentation attributes verbatim so a copy in either direction stays a copy.
3. Add a constant to `Services/Glyph.cs` with one line on what it means.

**Never `<use href="sprite.svg#id">`.** The page is served under a strict content policy, and a
cross-document reference is refused silently — every icon would vanish on the deployed machine while looking
perfect in development. `IconSprite` is inlined once by `MainLayout`; **a page must not host its own copy**,
or two elements share every id.

### What ships, and what was refused

`i-app` · `i-areas` · `i-schedule` · `i-modes` · `i-house` · `i-residents` · `i-lanes` · `i-auto` ·
`i-manual` · `i-dimming` · `i-off`.

`i-lanes` was **commissioned by §6.2**, which refused both drawn dashboard glyphs: tiles and gauge are the
wrong metaphor for a home surface that is a timeline, and a wrong metaphor is worse than none. It is three
tracks of unequal length cut by an accent now-line — the board in miniature.

`i-dimming` is the fourth state glyph §6.2 held in reserve. See §4.

§6.2 also refused glyphs on log rows, quick-action buttons, sentence tokens, person chips, room-page card
heads, the first-run table and the discovery card. Twelve repeated marks are texture, not information.
**Adding an icon is a design decision, not plumbing.**

---

## 4. The state chip

Room state used to be conveyed **by colour alone** — nothing to a colourblind reader, nothing in a greyscale
screenshot, nothing on a phone in sunlight. It is now shape *plus* colour, in one shared component so all
four pages say it identically.

```razor
<StateChip State="@snapshot.State" Kelvin="@snapshot.ColorTempKelvin" />
<StateChip State="@snapshot.State" Bare="true" />     @* the mark alone, for a dense row *@
<StateChip State="@snapshot.State" ShowWord="false" />
```

| Parameter | Default | |
|---|---|---|
| `State` | required | the room's last published `AreaState` |
| `Kelvin` | `null` | the commanded colour temperature; used only where it is a fact |
| `ShowWord` | `true` | say the state in words as well as in shape |
| `Bare` | `false` | the mark alone: no well, no word, no padding |
| `Title` | the word | a fuller hover explanation |

The mapping is a pure function — `StateGlyph.For(AreaState)` → `StateMark(Icon, Family, Word, Blinks)` — so
it is tested rather than screenshotted, and so no surface can name a state differently from another.

| State | Shape | Colour | Word |
|---|---|---|---|
| `AutoActive` | loop | `--machine`, or the room's own Kelvin | lit · auto |
| `PreOff` | **draining ring** | `--warn`, blinking | warning dim |
| `OverriddenOn` | person | `--human` | set manually |
| `SuppressedOff` | person | `--human` | off manually |
| `SceneHold` | person | `--human` | held by a scene |
| `Disabled` | struck circle | `--idle` | switched off |
| `AutoVacant` | **no shape — a dot** | `--idle` | watching |
| `Away` | **no shape — a dot** | `--idle` | house empty |

**The quiet states get no glyph.** That is the dark-cockpit rule carried into iconography: fourteen rooms out
of seventeen are watching and nothing else, and fourteen repeated marks are what the eye has to look past.

### The fourth glyph — a deviation, on the record

§6.2 shipped the warning dim as the **auto loop tinted amber, blinking**, and named the shared shape a
compromise, flagging a fourth glyph as the fix "if it proves too subtle". **It was drawn.** A shared shape
means that in greyscale, and to a colourblind reader, "lit and settled" and "about to go dark" are the same
picture — the exact failure the state glyphs exist to fix. `i-dimming` is a light with a quarter of its time
left: the same draining ring the room header already draws as a countdown. Nothing existing was bent into a
new meaning, and the amber and the blink still apply. A test asserts the two shapes differ.

The two hand states **do** share a shape, deliberately: they are one fact — somebody decided — and the word
carries which. A test asserts their words differ.

### Kelvin tint

A lit room's **glyph** takes the warmth it was actually commanded to; the **word** keeps `--machine`. Kelvin
colours are `rgb(255, g, b)` — at 6000 K that is nearly white, which is correct as light and invisible as
text on a pale panel. `--kelvin-weight` / `--kelvin-shade` pull the tint toward ink in the light theme until
it clears 3:1 against its own background, while the hue survives, so a 2200 K night dim and a 4500 K midday
white stay visibly different rooms.

---

## 5. `SentenceView`

A room's behaviour as readable prose, with the values inline and editable. **Reading the sentence and
changing it are the same act.**

> Lights when someone moves and it's darker than `40 lx` — or the sun is below `3°`. After `10 min` without
> movement, dim to `50 %` for `30 s`, then off.
>
> Manual changes hold for `2 h`; after a manual off, movement is ignored until the room is empty `10 min`.

### Using it

```razor
<SentenceView Sentences="@AreaSentences.ForArea(_room, _document.Defaults)"
              Edited="@OnEdited"
              Reverted="@OnReverted"
              Note="Applies when you save." />
```

| Parameter | Default | |
|---|---|---|
| `Sentences` | required | `IReadOnlyList<Sentence>` — build with `AreaSentences` or `SentenceBuilder` |
| `Editable` | `true` | `false` renders every value in bold, no controls |
| `Edited` | — | `EventCallback<SentenceEdit>` — a value was picked |
| `Reverted` | — | `EventCallback<string>` — this key should follow the house again |
| `Note` | `null` | one line in each popover about when a pick takes effect |
| `ShowLegend` | `true` | explain the amber dot underneath, when a dot exists |
| `LegendText` | "= this room's own setting…" | |

**`Note` is yours to word, and there is no default, on purpose.** Only the page knows: a room editor behind
the save bar should say "Applies when you save"; a surface that applies at once should promise the undo. A
promise about saving is the worst kind of thing to be wrong about.

### Applying an edit

```csharp
private async Task OnEdited(SentenceEdit edit)
{
    switch (edit.Key)
    {
        case nameof(AreaSettings.VacancyTimeoutSeconds):
            _room.VacancyTimeoutSeconds = edit.Seconds;
            break;
        case nameof(AreaSettings.OverrideDurationMinutes):
            _room.OverrideDurationMinutes = edit.Minutes;
            break;
        case nameof(AreaSettings.PreOffBrightnessFactor):
            _room.PreOffBrightnessFactor = edit.Fraction;   // the schema's 0-1 factor
            break;
        case nameof(AreaSettings.LuxThreshold):
            _room.LuxThreshold = edit.Number;
            break;
        case nameof(AreaSettings.Darkness):
            if (edit.TryEnum(out DarknessSource source))
                _room.Darkness = source;
            break;
    }

    await MarkDirty();     // your page's existing save path. Nothing here writes anything.
}

// Reverting is NOT "set it to the house's current number" — it clears the property, so the room keeps
// following the house the next time the house changes. Two callbacks, because they are two edits.
private async Task OnReverted(string key)
{
    if (key == nameof(AreaSettings.VacancyTimeoutSeconds))
        _room.VacancyTimeoutSeconds = null;
    // …
    await MarkDirty();
}
```

**Do not build a new write path.** `SentenceView` never touches the document and never saves. Every edit
leaves through a callback; the page mutates its own copy and puts it through the existing
validate-then-write pipeline, which stays the only way configuration reaches disk.

### `SentenceEdit`

`Key` (the `AreaSettings` property name) · `Kind` · `Value` (invariant string), plus readers that turn the
value back into what the schema wants: `Seconds` · `Minutes` · `Span` · `Percent` · `Fraction` · `Number` ·
`Integer` · `Flag` · `TryEnum<T>()`.

### Provenance — the amber dot

Every token carries a `TokenOrigin`:

- **`Own`** — the room states its own value. Renders the amber dot, and the popover offers
  *"Use house setting (10 min)"*.
- **`Inherited`** — follows the house. No mark, no road back to offer.
- **`None`** — provenance does not apply. The house's own defaults inherit from nothing.

Provenance is read off the schema's `null`, **never guessed by comparing values**: a room that deliberately
pins 10 min while the house also says 10 min has made a decision — one taken precisely so a later change to
the house leaves this room alone. The dot is how somebody sees at a glance what they have changed.

> **The dot is amber, the token's brass is hover/focus.** Brass says *you can change this*; amber says
> *this one is changed*. Two signals, two jobs — do not merge them.

### Building your own sentences

`AreaSentences.ForArea(area, defaults)` and `.ForDefaults(defaults)` cover the room and the House tab's
defaults. For anything else — mode sentences, the blend sentence, the away-debounce line — use the builder.
It is fluent so the call site reads as the English it produces:

```csharp
Sentence sentence = SentenceBuilder.Start("Count the house as empty ")
    .Duration(nameof(GlobalConfig.AwayDebounceMinutes), "Away debounce", 300,
              TokenChoices.DurationsInMinutes(1, 5, 10, 15))
    .Text(" after the last person leaves.")
    .Build();
```

Methods: `Text` · `Duration` · `Percent` · `Number` · `Choice` · `Toggle` · `Entity` · `Figure` · `When` ·
`Token` · `Build`. Shortlists come from `TokenChoices.Durations` / `.DurationsInMinutes` / `.Percentages` /
`.Numbers(unit, …)` / `.Of((text, value), …)`.

Each typed method formats its own value, so **the words and the carried value can never disagree** — a token
saying "10 min" while handing back `10` would set a ten-*second* timeout with nothing on screen looking
wrong.

Shortlists are **curated, not complete**: the handful of values a sane house uses. Everything between them
belongs in the All-settings row behind *show more*. Always include the value your setting actually ships
with, or the popover opens with nothing ticked (there is a test for this on the area sentences).

### Booleans, gated settings and figures

A yes/no is a `Toggle` token: written as **what it means here**, and flipped in place rather than opening a
popover for two options.

```csharp
SentenceBuilder.Start("This room ")
    .Toggle(key, "Brighten with daylight", on, "brightens with daylight", "follows its schedule",
            origin, houseValue: false)
    .When(on, clause => clause
        .Text(" — from ")
        .Number(floorKey, "Daylight level where brightening starts", 100, "lx",
                TokenChoices.Numbers("lx", 50, 100, 200, 500))
        .Text(" outside up to ")
        .Number(ceilKey, "Daylight level for full brightness", 10000, "lx",
                TokenChoices.Numbers("lx", 2000, 5000, 10000, 20000))
        .Text(", spread ")
        .Number(curveKey, "Curve shape", 1.6, "", TokenChoices.Numbers("", 0.5, 1, 1.6, 2.5))
        .Figure(CurvePath.Describe(1.6), FigureContent))
    .Text(".")
    .Build();
```

**`When` is how this model says "that setting only matters while this one is on": the clause is not built.**
A setting that cannot take effect should not be on the page — greying it out still spends the reader's
attention, still invites the tap, and still has to explain itself. The sentence gets shorter, and grows back
on the same tap that turns the gate on.

**Wide-range values want a shortlist, not a stepper.** 100 → 10 000 lx is four taps of curated values or a
very long drag.

**`Figure` is for a value that is a shape rather than a quantity.** `<CurveGlyph Exponent="1.6" />` draws a
power curve in the page's own stroke grammar (a 2 px accent line on hairline axes); `CurvePath.Power` and
`CurvePath.Describe` are the pure functions behind it, both tested. Give every figure `AltText` — it is what
`Sentence.PlainText`, a screen reader and a test all read in the drawing's place.

---

## 6. Two traps this project has already fallen into

**A string parameter takes a literal without `@`.** `Label="_text"` passes the *text* `_text`. Write
`Label="@_text"`. Check every string parameter you write.

**A container hosting a popover needs `position: relative`.** Anchored to a container that is not a
positioning context, a popover renders in the page's bottom-left corner. `.tok-host` and `.field` already
carry it; anything new that hosts one must too.

---

## 7. Testing

There is **no Razor render harness, and one must not be introduced.** Anything worth asserting is extracted
as a pure function and tested there — that is why the sentences are a projection, why the state mapping is a
lookup, and why the curve is a path builder. The pattern to follow is `AreaView` / `ActivityView`.

Current cover: `TokenFormatTests` (how values are written and carried, including a culture test —
`nb-NO` writes decimals with a comma, and a half carried as "0,5" would parse back as **five**),
`AreaSentencesTests` (the §3 table, provenance, the flags sentence), `SentenceBuilderTests`,
`StateGlyphTests` (that no two states are drawn the same way), `CurvePathTests`, `AppThemeTests`.

---

## 8. The themes, and the picker

`Services/AppTheme.cs` holds the list — `system` · `light` · `dark` · `0z0` — and `AppThemes.Resolve` turns a
stored id back into one of them. **An id is a storage key, not a word: renaming one drops every browser that
had chosen it.** An id this build no longer ships resolves to `system`, because `data-theme="solarized"` would
match no block at all and hand a light desk a dark page.

**The picker is on the top bar, not the House tab.** It is a per-browser preference that applies on the tap and
never reaches the YAML; the House tab's controls are document edits behind a save bar, and mixing the two is a
promise about saving that would be wrong.

**The first paint is the server's.** `wwwroot/theme.js` is loaded from `<head>` with no `defer`, so `data-theme`
is on `<html>` before the body is parsed; a preference read in `OnAfterRenderAsync` arrives one repaint late and
the page visibly changes colour. Its allow-list arrives on the script tag as `data-themes`, rendered from
`AppThemes.DataThemeIds`, so the ids are defined once.

### The 0z0 theme, and what in it was derived

Its source is `0z0-design/design-language.md`. That document keeps its palette for "anything that reads as
ZeroZero Software" and lets an app "introduce its own accent colours for **product**-specific meaning", which is
the split this theme follows: **chrome** is the studio palette verbatim (`bg` `bg2` `bg3` `border` `text`, and
teal `#27e0c8`, which the palette names the primary accent); **state** is this app's own.

Taken from the language: teal as the accent · blue as `--machine` · purple as `--human` (the violet rule 3
requires) · amber as `--warn` (the palette gives amber "warnings" outright) · monospace as `--font-sans` (called
"a deliberate, load-bearing choice" covering headings, navigation and body copy).

Derived, and on the record as derivations:

| Token | Why it is not in the palette |
|---|---|
| `--chip` `--lane` `--glow-track` `--grid` | the studio names three surfaces and one border; this UI needs six and two |
| `--muted` | the stated `#64788f` is 3.96:1 on `--panel`, below AA for 11–12.5 px text. Lifted one step, same hue; the stated value becomes `--idle`, whose shipped band is 3.4–4.1:1 |
| `--ok` `--bad` `--now` `--info` | no green, red or magenta in a five-hue palette. With the four studio hues they complete a terminal palette, which is that language's own voice |
| `--now` | magenta specifically: every other bright hue is spoken for, and the live edge must never read as the accent |
| `--daylight-*` | the chart's bands are times of day; cut from the studio's indigo and amber, darkened to this ground |

The `[Ø]` studio mark is **not** used: it is studio identity only and never an app's own icon. The brand typeface
is listed first in a font stack and never fetched, which is that language's zero-dependency rule for the web.
