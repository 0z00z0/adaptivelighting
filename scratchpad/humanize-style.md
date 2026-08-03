# Comment style: the target

Read this fully before editing. Every agent on this pass follows it, so the result reads as one hand.

## What is wrong today

Measured across `src/`: **36.7% of all lines are comments**, comment:code = **0.73**. Worst file is 3.74:1.
1698 em dashes, "rather than" 669 times, "deliberately" 117, `<b>` inside XML docs 259 times.

The problem is not only volume. It is that **coverage is uniform**. A one-line expression-bodied property
carries a four-line `<remarks>`. Real code has uneven attention: the gnarly part gets three paragraphs and the
obvious part gets nothing. Flat coverage is the tell.

The second problem is **register**. The docs argue: they justify the choice, name the alternative that was
rejected, and close with a moral. That is an essay about the code, not a note in it.

## The target

Roughly **0.25–0.35** comment:code overall. This is a direction, not a quota — do not pad a bare file to hit
it, and do not strip a genuinely tricky one to hit it either. **Most members should end up with no comment at
all.**

## DELETE outright

- `<remarks>` that justifies a decision, defends it, or names an alternative that was considered and rejected.
- Any reference to a design document: `§2.5`, `ui-design-c`, `first-run-wizard.md`, `(09 §3.5)`.
- War stories whose risk **no longer exists**. You must check the current code before deciding this — see below.
- `<param>` / `<returns>` that restate the parameter name or the return type.
- `<b>`, `<i>`, `<para>` inside doc comments. Plain sentences only.
- Any comment that restates the line under it.
- Commentary about how the UI should read, what a user will feel, or what the design refuses.
- `<exception>` for a plain `ArgumentNullException.ThrowIfNull` guard. Keep it only for a thrown condition a
  caller could not guess.

## KEEP — but rewrite short and keyword-ish

Keep a note only where a **future edit could plausibly break something, and the trap is still reachable in
today's code**. Two lines maximum. Prefer a `//` line next to the code over a `<remarks>` block.

Categories that qualify:

- **Invariants that are not visible locally.** Enum members pinned to ordinals. A single write path. A field
  that must only be touched under a named lock. Two delegates of which exactly one is ever assigned.
- **Ordering that matters.** Something that must be read before something else is mutated.
- **Platform and locale traps.** Invariant formatting in HTML attributes, UTC vs local time in tests, mDNS
  names that do not resolve in a container, `&` in a password passed through `cmd`.
- **Asymmetries a reader will assume away.** Normaliser runs on save but not on load. Supervisor `/info` 401s
  while `/logs` works.
- **Concurrency.** What a lock actually protects, in a clause.

Good shape:

```csharp
// Ordinals are pinned: an unknown enum value is a FormatException at startup, not a silent default.
// Normalised on save only. The load path must not rewrite a hand-edited file.
// Under _gate: decide and claim must be atomic.
```

Bad shape (this is the current style — do not produce it):

```csharp
/// <remarks>
///     <b>This is not tidiness.</b> Two readers bind this type, and an unknown enum value is a
///     <see cref="FormatException"/> that kills the app on start — deleting one member took a live house's
///     dashboard down, and there is no version at which it becomes safe to do again, because there is no way
///     to prove no file still says the word.
/// </remarks>
```

## `<summary>`

Keep a one-line `<summary>` where the member's name does not already say it, on types and on public members
that are read from another file. Delete it where the name says it. Never more than one sentence.

`CS1591` is suppressed project-wide, so a missing summary does not warn.

## Prose rules

- **No em dashes.** Comma, semicolon or full stop.
- Avoid: "rather than", "exactly", "deliberately", "precisely", "the whole", "not X but Y", "for the same
  reason", "which is".
- Plain declarative. Present tense. No rhetorical questions, no italics for emphasis, no bold.
- Vary length. Some notes are three words.

## Hard rules

1. **Do not change code.** Comments and doc comments only. The one exception is deleting a `using` that
   becomes unused — and only if the build still passes.
2. **Tabs** for indentation, matching the file.
3. Do not rename anything. Do not reorder members.
4. Do not touch any file outside your assigned list.
5. Run `dotnet build AdaptiveLighting.slnx` before you finish. It must be **0 warnings, 0 errors**.

## Before you delete a war story, check it

Many notes record a real incident. Some of those risks are now **structurally impossible** — the code moved
on. Read the code and decide:

- Risk still reachable → keep it, as one keyword-ish line.
- Risk now prevented by a type, a guard, a test, or a restructure → delete the note. Say so in your report.
- Cannot tell → keep it, short, and flag it in your report as unverified.

## Report back

Your final message must contain three sections:

1. **`MECHANISMS`** — knowledge worth preserving that is about *how the system works* rather than about a past
   decision, which you removed from the source. Write each as a short paragraph with a heading, ready to paste
   into a shared document. This is the material the owner asked to keep. Do not invent; only extract what was
   really there. Include the type/method it came from.
2. **`RISKS KEPT`** — every note you kept, one line each, as `file:line — note`.
3. **`DROPPED AS STALE`** — war stories you removed because the risk is gone, one line each, with why.

Also report your file's before/after comment:code if you can.
