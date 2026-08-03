# Humanize pass — test project brief

Read `scratchpad/humanize-style.md` in full first. It is the spec. This file adds the test-specific
overrides. Where they differ, this file wins.

Repo root: `C:\Users\EspenLaget\Nextcloud\ai\NetDaemon\adaptivelighting`
Branch `cleanup/humanize` is already checked out. Do NOT create branches, commit, push, or run
`dotnet build` / `dotnet test` (the coordinator builds at the end; other agents are editing `src/`
concurrently so a failure would not be yours).

## Scope

Only the files in your assigned list. Nothing under `src/`. Nothing outside your list.

## Test-specific rules

1. **Never rename a test method.** The long sentence-style names are the intended convention and they
   carry the assertion.
2. **Never change an assertion, a value, or any code.** Comments only. If a test looks wrong, report it,
   do not touch it.
3. `<summary>` on a test method: delete it when it only restates the method name (the common case, since
   the name is already a sentence). Keep a one-liner only where it says something the name cannot.
4. `<remarks>` on a test method: delete unless it records **why the test exists** in a way that would stop
   someone deleting the test as redundant — a regression test whose point is not obvious from its
   assertions. Keep those to one or two lines, keyword-ish.
   Keep, compressed: "Regression: the load path does not normalise, so a hand-edited Either reaches the board."
   Delete: three paragraphs on what a dashboard should not celebrate.
5. Delete `// ===================== section =====================` banners only where the file has fewer
   than three of them. Otherwise keep them; they are navigational. Never add new ones.
6. Assertion messages (the string argument to `Assert.*`) are NOT comments. Leave them alone.
7. A class-level `<summary>` may stay as one line if it says what the file covers. Class-level `<remarks>`
   essays go, unless they hold a mechanism nothing else records — and then one or two lines.
8. Helper methods and fakes: a one-line `<summary>` only where the name does not say it.

## Prose rules (from the spec, repeated because they are the point)

- No em dashes. Comma, semicolon or full stop.
- No `<b>`, `<i>`, `<para>` inside doc comments.
- Avoid "rather than", "exactly", "deliberately", "precisely", "the whole", "not X but Y", "which is".
- Plain declarative, present tense. Vary length; some notes are three words.
- **Tabs** for indentation, matching the file.

## Report back

Three sections, exactly:

1. `MECHANISMS` — things a test's prose documented about **how the system behaves** that are not written
   down anywhere else, and which you removed. Short paragraph each, with a heading, and the
   file + test it came from. Do not invent; only extract what was really there.
2. `RISKS KEPT` — every note you kept, one line each, as `file:line — note`.
3. `DROPPED AS STALE` — war stories removed because the risk is gone, one line each, with why. Check the
   current code before calling a risk gone; if you cannot tell, keep the note and flag it as unverified.

Also give before/after comment:code for your files.
