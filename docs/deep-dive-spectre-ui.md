# Deep dive — Spectre.Console migration and the `Ui` API

Companion to [implementation-plan.md](../implementation-plan.md) — Phase 10 (Spectre.Console UI).

## The actual tag inventory

Measured from `docs/ui-strings.baseline.txt`, not estimated. Two different
things are currently mixed into one bracket syntax:

**Severity tags** — these want to become colour + one canonical spelling:

| Concept | Spellings found | Count |
|---|---|---|
| Info | `[INFO]` 89, `[Info]`, `[Debug]` 2, `[DEBUG]` | ~93 |
| Error | `[FEHLER]` 25, `[Fehler]` 5, `[FATAL ERROR]` 6, `[FAILED]` 2, `[ERROR]`, `[Error]`, `[ffprobe error]` 3, `[FFmpeg error]` 2 | ~45 |
| Warning | `[WARNUNG]` 24, `[Warning]` 2, `[GCS Warnung]`, `[AppConfig Warnung]` | ~28 |
| Success | `[OK]` 9, `[SUCCESS]` 3, `[Erfolg]` 3 | 15 |

**Subsystem scopes** — these are *not* severity and must survive as scopes:

`[AutoExtraction]` 15, `[LaTeX Refinement]` 14, `[Cache]` 9, `[FFmpegToolkit]` 8,
`[API]` 5, `[Timer]` 4, `[Kostenschutz]` 4, `[FFmpeg Producer]` 4,
`[Antigravity Agent API]` 4, `[YouTube Mode]` 3, `[LatexToolkit]` 3,
`[ContextCacheStateManager]` 3, `[GCS]` 2, `[Rate Limit]` 2, `[API Retry]` 2, …

**Data labels** — a third category that is neither: `[Request Tokens]` 3,
`[Session Total Tokens]` 3, `[Tokens]` 3, `[startIndex]`, `[ENTER]` 2.
These are not log lines and should not be routed through severity helpers at all.

The naive find/replace the old backlog item 8 proposed would have flattened all
three categories together. Don't. The `Ui` API below separates them explicitly.

## The `Ui` surface

```csharp
namespace LectureExtraction.ConsoleUi;

public static class Ui {
    // Severity — canonical German tag + colour, markup-escaped
    public static void Info(string msg, string? scope = null);
    public static void Warn(string msg, string? scope = null);
    public static void Error(string msg, string? scope = null);
    public static void Success(string msg, string? scope = null);

    // Structure
    public static void Step(string title);          // Spectre Rule
    public static void Detail(string msg);          // dim, two-space indent
    public static void Blank();

    // Verbatim — NO markup parsing, for model output and LaTeX
    public static void Raw(string text);            // Console.Write equivalent
    public static void RawLine(string text);

    // Data
    public static void Table(string title, IEnumerable<(string Key, string Value)> rows);
}
```

`scope` renders as a dim prefix: `Ui.Error("Upload fehlgeschlagen", "LaTeX Refinement")`
→ `[LaTeX Refinement] [FEHLER] Upload fehlgeschlagen`, preserving today's
`[LaTeX Refinement] [FEHLER]` shape exactly. That shape is deliberate and the
migration must keep it — it tells you *which* subsystem failed.

Canonical severity tags stay German, per the existing convention (identifiers
English, user-facing strings German): `[INFO]`, `[WARNUNG]`, `[FEHLER]`, `[OK]`.
These are already the majority spelling in every category, so most call sites
change colour only, not text — which keeps the UI-string diff readable.

### Settled: colour **and** tags, not colour alone (decision, 2026-07-28)

Severity is carried by both a colour and the canonical bracket tag.

The deciding argument is that **Spectre strips colour automatically when output
is not a TTY** — piping to a file or through `tee`, which is exactly what you do
when capturing a long run to inspect afterwards. Colour-only output loses its
entire severity signal precisely in that case. Tags cost eight characters and
stay greppable (`grep '\[FEHLER\]'`).

Rejected: coloured glyphs (`✗ ⚠ ✓ ·`) instead of tags — less horizontal noise and
consistent with the emoji already in the main menu, but it breaks grepping and
turns the migration into a 624-line diff where every line changed, which is
unreviewable. Emoji stay where they already are (menu entries, `Ui.Step`
headers); they do not become the severity mechanism.

**Refinement adopted with it:** `[INFO]` is 89 of ~180 severity strings, and info
is the default level — most of those lines are secondary chatter, not status
events. Route them to `Ui.Detail` (dim, indented, no tag) and reserve `[INFO]`
for genuine status. This is the single biggest reduction in visual noise
available, and unlike a glyph swap it is a judgement call per call site, so make
it *while* migrating each file rather than as a separate mechanical pass.

## The escaping boundary — the one thing that can break output

Spectre parses `[` … `]` as markup. This app prints LaTeX. Both facts are
non-negotiable, so the boundary must be explicit:

* **Everything routed through `Info`/`Warn`/`Error`/`Success`/`Step`/`Detail`
  gets `Markup.Escape` applied internally to the message argument.** Callers
  never escape by hand and never pass markup. This is why the API takes plain
  strings rather than exposing Spectre markup — a caller who could pass markup
  would eventually pass LaTeX.
* **Model output goes through `Ui.Raw` only**, which uses
  `AnsiConsole.Write(new Text(s))` — `Text` does no markup parsing at all.
* Colour for streamed output is not attempted. `Ui.Raw` is unstyled by design.

Test to pin it, added with the `Ui` class itself:

```csharp
[Theory]
[InlineData(@"\section[short]{long}")]
[InlineData(@"\begin{itemize}\item[a] x")]
[InlineData("[FEHLER] literal in payload")]
public void Raw_and_severity_helpers_never_mangle_latex(string s) { … }
```

Capture via `AnsiConsole.Record()` + `ExportText()`, assert the payload appears
byte-identical.

## Migration order

Do the `Ui` class and its tests first, alone, in one commit. Then migrate by
file, largest first, one commit each, reading the UI-string diff after every one:

| Order | File | `Console.Write*` calls |
|---|---|---|
| 1 | `src/Extraction/VertexAutoExtractionSession.cs` | 160 |
| 2 | `src/Refinement/LatexRefinementSession.cs` | 141 |
| 3 | `src/Extraction/AiStudioAutoExtractionSession.cs` | 88 |
| 4 | `src/Media/FfmpegInteractiveSession.cs` | 69 |
| 5 | `src/ConsoleUi/ConfigurationPrompts.cs` | 52 |
| 6 | `src/Extraction/RefinementUiHelper.cs` | 46 |
| 7 | `src/Extraction/AiStudioAutoExtractionSession.Repl.cs` | 45 |
| 8 | `src/Media/FfmpegToolkit.cs` | 40 |
| 9+ | the remaining ~14 files, ≤25 calls each | ~130 |

**Vertex first, deliberately.** It is the largest and the one that cannot be
smoke-tested, so it should be migrated while the pattern is freshest and while
the AI Studio twin still exists in its original form beside it as a reference.
If the pattern turns out wrong, discovering it on Vertex costs nothing at
runtime.

**Watch out:** `DirectAiChatSessionAiStudio.cs`, `DirectAiChatSessionVertex.cs`
and `AttachmentUploader.cs` all have `using static System.Console`, so they call
bare `Write` / `WriteLine`. A grep for `Console.Write` will miss every call in
those three files. Remove the `using static` first in each, let the compiler
list the call sites, then migrate.

## Streaming — the boundary that must not erode

These are the only places model tokens reach the console:

* `AiStudioAutoExtractionSession.cs:1171` — `Console.Write(txt)` in `StreamAndCollectAsync`
* `AiStudioAutoExtractionSession.Repl.cs:320` — the debug-chat stream
* the Vertex and chat-session equivalents
* `LatexRefinementSession.StreamAndCollectAsync` / `StreamFixResponseAsync`

All become `Ui.Raw(txt)` and nothing else. No live region, no status spinner, no
progress bar may be active while any of these runs — Spectre's `Live`/`Progress`
own the cursor and will interleave with raw writes into garbage.

Practical rule: `Progress`/`Status` scopes must **close before** the generate
call starts. That is why §9.4 limits live regions to FFmpeg splitting, uploads,
rate-limit delays and PDF compilation — all strictly before or after generation,
never during.

## Live-progress targets, concretely

| Where | Today | Becomes |
|---|---|---|
| `VideoSegmentProducer.RunAsync` | per-part `Console.WriteLine` | `Progress` task per video part |
| `AttachmentUploader` upload path | per-attempt lines + `[Timer]` | `Progress` task, bytes + elapsed |
| `ConsoleUi/InteractiveDelay` (97 lines of hand-rolled countdown) | redrawn countdown line | `Status` spinner with remaining seconds |
| `LatexRefinementSession.CompilePdfAsync` | per-round lines | `Status` per compile round |

`InteractiveDelay` is the highest-value one: `VideoPartDelaySeconds` is 130 and
`HistoryRateLimitDelaySeconds` is 65, so this is where most wall-clock time is
spent staring at the console. It also currently supports keypress-to-skip —
check that behaviour survives; Spectre's `Status` runs a callback on a
background thread, so the key-reading loop needs to stay on the calling thread.

## Menus

Replace with `SelectionPrompt<T>` over a menu-item record, so the numbering,
the dispatch and the display text come from one list:

```csharp
sealed record MenuItem(string Label, Func<Task> Run, bool Enabled = true);
```

This structurally removes: hand-numbering drift between printed text and
`switch`, the `__EXIT__` magic string from `ConfirmOrChangeModel`, the ~6
divergent `"exit"`/`"quit"` handlers, and the `"Invalid choice."` /
`"Ungültige Auswahl."` split (there is no invalid choice in a selection prompt).

**Settled (2026-07-28): disabled entries render dim and non-selectable**, never
hidden and never selectable-then-rejected. Vertex with `IsVertexAiEnabled = false`
stays visible in grey carrying its `[DEAKTIVIERT - Kostenschutz]` note, and the
cursor skips over it. You keep seeing that the capability exists and why it is
off, without the dead-end interaction of picking it and being refused.

`SelectionPrompt` has no built-in disabled state, so implement it as: filter
disabled items out of the prompt's choices, and print them as dim lines above or
below the prompt. Applies to every menu, not just this one.

`(j/n)` prompts become `ConfirmationPrompt` with an explicit default. Note the
current semantics are a genuine footgun worth calling out in the commit message:
`if (Console.ReadLine()?.Trim().ToLower() != "j") return true;` means Enter, `y`,
`yes` and any typo all mean "no" **and the method returns success**, so a paid
extraction proceeds with an empty system instruction.

## Verification

Per commit: build 0/0, tests green, then read `tools/dump-ui-strings.sh | diff`
output and confirm every line is intended. Regenerate the baseline in the same
commit.

`dump-ui-strings.sh` greps for `Console.Write*`. Once files are migrated it will
stop seeing them, so **update the script's pattern to also match `Ui.` calls in
the same commit as the `Ui` class** — otherwise the diff silently goes empty and
stops being a check at all.

Manual, once at the end: walk menu paths 1–7, and run one short extraction to
confirm streamed LaTeX is byte-clean and no progress region overlaps it.
