# LaTeX auto-fix and diagnostics (highly experimental)

What happens when a lecture's PDF does not compile, how our own rule-based repairs work, and what
to do by hand when they do not help. Written for humans and for AI agents working on this repo.

> **Status: highly experimental.** The auto-fixer rewrites the final `.tex` in place. The original
> is always kept as `…-final-orig.tex`, every change is printed with an `[EXPERIMENTELL]` tag, and
> the whole thing is opt-in (`PdfCompilation.ExperimentalLatexAutoFix`, default `false`). Treat a
> fixed document as a proposal that happened to compile, not as verified LaTeX.

---

## 1. Where it runs

PDF stage (refinement step 4, or `lecx pdf compile`), in `LatexRefinementSession.Pdf.cs`:

1. Compile the final `.tex` with the preamble for its language (`pdf-preamble-de.tex` /
   `pdf-preamble-en.tex`, chosen by `% Primary Language:`; `pdf-preamble.tex` when unknown).
2. On failure: back up the input as `-orig.tex` (and the partial PDF).
3. **Auto-fix** (only if `ExperimentalLatexAutoFix` is on): run `LatexAutoFixer`. If anything changed,
   compile again. Free and instant.
4. Still failing: **AI repair**, either the Antigravity agent (`UseAntiGravityAgent`) or the Gemini
   fix loop (`MaxFixRounds`). Both get the compile log **plus our structural findings**
   (`LatexDiagnostics`) with line numbers. Those findings are read-only and run whether or not
   the auto-fix is on.

`lecx pdf compile --tex <file> --fix-loop 0` runs steps 1-3 only: no API call, no cost.

## 2. Layout

```
src/Latex/
├── LatexAutoFixer.cs          ← runner: lines, comments, CRLF, report, fix order
├── LatexScan.cs               ← shared scanning rules ("parsing tricks", section 4)
├── LatexDiagnostics.cs        ← read-only findings with line numbers for console + AI repair prompt
└── AutoFix/
    ├── ILatexLineFix.cs                    ← interface for line-local fixes
    ├── ILatexDocumentFix.cs                ← interface for whole-document fixes
    ├── BraceNestedOptionalArgumentFix.cs   ← [Units in $R[x]$] → [{Units in $R[x]$}]
    ├── BareMathCommandInTitleFix.cs        ← [Sequential \implies Topological] → [Sequential $\implies$ Topological]
    ├── TexorpdfstringBracketFix.cs         ← {R[[x]}}} → {R[[x]]}}
    ├── UndefinedMathCommandFix.cs          ← \mapsfrom → \mathrel{\reflectbox{$\mapsto$}}, \sub → \subset, \Lightning → \ensuremath{\lightning}
    ├── UnclosedInlineMathBeforeProseFix.cs ← $x and negatives → $x$ and negatives
    ├── HighlightOptionalColorFix.cs        ← \begin{highlight}[T] → \begin{highlight}{BrickRed}[T]
    ├── HighlightColorTintFix.cs            ← \begin{highlight}{BrickRed!15} → \begin{highlight}{BrickRed}
    ├── LeadingAngleBracketCommandFix.cs    ← <coordinate (A) at … → \coordinate (A) at …
    ├── MisnamedEnvironmentEndFix.cs        ← \end{student} closing a speech → \end{speech}
    ├── FloatInBoxFix.cs                    ← document fix: \begin{table}[h!] inside a box → \begin{center}, \captionof
    └── SurplusBraceAfterMetaNoteFix.cs     ← document fix: \inlinemetanote{...}} → \inlinemetanote{...}
```

## 3. Error classes we fix (all seen in real lectures)

| Fix | pdflatex says | Cause | Seen in |
|---|---|---|---|
| `BraceNestedOptionalArgumentFix` | `Extra }, or forgotten $` at `\begin{theorem}[… $R[x]` | A classic optional argument ends at the first `]`, so `R[x]` in a title cuts it off. | 09-30 (theorem), 10-06 (lemma) |
| `BareMathCommandInTitleFix` | `Missing $ inserted` at `\begin{proofWrapper}[… \implies …]` | A math-only command (`\implies`, `\to`, `\subseteq`, `\iff`, …) written as prose in a box title, which is set in text mode. Wraps each such command outside `$...$` in its own `$...$`; titles with `\texorpdfstring` are left alone. | Analysis II 02-23, rerun of 2026-10-25 (step 4, a `proofWrapper` and a `content` title) |
| `TexorpdfstringBracketFix` | `Argument of \environment content has an extra }` | The plain-text half of `\texorpdfstring` lost a `]` that became a `}`: `{R[[x]}}}`. | 09-30 (5 titles) |
| `MisnamedEnvironmentEndFix` | `\begin{speech} on input line 1011 ended by \end{student}` | The model closed `speech` with the wrong name. | 09-22 |
| `UndefinedMathCommandFix` | `Undefined control sequence` at `\mapsfrom` (or `\sub`, `\Lightning`) | Commands the preamble does not define: `\mapsfrom` → `\mathrel{\reflectbox{$\mapsto$}}`, `\longmapsfrom` likewise, `\sub` → `\subset`, and marvosym's `\Lightning` → `\ensuremath{\lightning}` (stmaryrd's contradiction mark, which the preamble loads and transcription.md names; `\ensuremath` makes it work inside `\text{...}` too). | 10-07 (step 4; `\sub` from AI repair round 1); Analysis II 02-23, 3.8 rerun of 2026-10-25 (step 4, `\text{\Large\textbf{\Lightning}}`) |
| `UnclosedInlineMathBeforeProseFix` | `Missing $ inserted` after a formula that runs into prose | A closing `$` was dropped before ordinary words: `$x and negatives` → `$x$ and negatives`. Only on a line with an odd number of `$` whose formula holds two plain words in a row; runs before `InlineMetaNoteMathFix`. | 10-07 (AI repair round 1, 2 lines), 09-29 (an older run) |
| `InlineMetaNoteMathFix` | `Missing $ inserted`, `Extra }, or forgotten $` | Math inside a `\inlinemetanote{writes $...$}` went wrong: a `$` left open, an unclosed `cases`/`aligned`/`matrix` (closed before the last `$`), or a stray `$` right after the note, `{schreibt $F x$}$`. That last one is removed only on a line with an odd number of `$`. | Grundstrukturen FS25: weeks 1 and 2 (stray `$`, 12 notes), week 9 (`cases`) |
| `HighlightOptionalColorFix` | `Undefined color '['` (xcolor) | `highlight` is the one box whose color comes first (`{ m o }`); the model wrote `\begin{highlight}[Title]` like every other box. Adds `{BrickRed}`. | Grundstrukturen FS25: weeks 2 and 13 |
| `HighlightColorTintFix` | ``Undefined color `30'`` (xcolor) | The model gave `highlight` a tint, `{BrickRed!15}`; the box mixes its own tints from the argument (`#1!30`), so it became `BrickRed!15!30`. Keeps the bare name. | Information Security 02-22-2024 (16 boxes, steps 3 and 4) |
| `LeadingAngleBracketCommandFix` | `Missing $ inserted` at `<coordinate` | A rewriting stage replaced a command's `\` by `<`. Only at the start of a line, before a known command name (`coordinate`, `draw`, `node`, `path`, `fill`, `begin`, `end`, `item`, …). | Information Security 02-29-2024 (step 3, and on another line step 4) |
| `FloatInBoxFix` | `LaTeX Error: Not in outer par mode` | A `table` or `figure` float inside a box (`content`, `highlight`, `steps`, …); floats cannot stand in a tcolorbox. A document fix: the float becomes a `center` block, its `\caption` a `\captionof{table}`; a float outside every box stays. | Information Security 02-29-2024 rerun (step 4, `\begin{table}[h!]` in `content`) |
| `SurplusBraceAfterMetaNoteFix` | `Too many }'s` | One `}` too many after a note whose formula has set braces `\{...\}`. A document fix: it counts braces over the whole file and removes only a `}` that closes nothing, so a `}` closing a group opened lines earlier (a `\footnote{`) stays. | Grundstrukturen FS25: week 11 |

**Not fixed automatically (yet):** missing, run-over and stray `\end{...}`. By decision, a fix is
written for an error class only once it has actually occurred. Until then `LatexDiagnostics` reports
them with line numbers and the AI repair (or you) handles them.

`content`, `math-stroke` and `highlight` are defined with `\NewTColorBox` and an xparse argument
spec, which counts nested brackets. `\begin{content}[… $\mathbb{Z}[i]$]` compiles (09-16), so the
bracket fix skips them. Everything defined with `\newtheorem`, `\newtcolorbox{…}[1][…]` or
`\newenvironment` (theorem family, `speech`, `aiNote`, `student`, `proof`, `proofWrapper`, …) needs it.

## 4. Parsing rules (`LatexScan`)

Every tool here must follow these. A tool that disagrees with `LatexScan` is the bug.

1. **Escapes:** a character is escaped when an **odd** number of backslashes precedes it.
   `\%` is a percent sign; `\\%` is a line break followed by a comment. Never test only the one
   character before.
2. **Comments:** an unescaped `%` starts a comment to the end of the line. Look for structure only
   in the code before it, and re-attach the comment unchanged when rewriting a line.
3. **Optional arguments:** `[…]` ends at the first `]` outside braces; brackets inside `{…}` are
   invisible to LaTeX. Nesting must be **counted**, so a regex may locate where something
   starts but must never decide where it ends.
4. **Environments:** `\begin`/`\end` pair like brackets. LaTeX reports a missing `\end` at the
   **next** `\end`, not where it is missing, so pair the whole document (`MatchEnvironments`).
5. **Verbatim:** inside `verbatim`, `Verbatim`, `lstlisting`, `minted` and `comment` nothing is
   structure until the literal `\end{name}`. Iterate `CodeSegments`, never the raw text.

Out of scope: catcode changes, macro expansion, `\verb|…|`.

## 5. Adding a fix

Only for an error class that actually broke a lecture:

1. Put the failing line(s) from the real `…-final-orig.tex` into a new test class first.
2. Write one class in `src/Latex/AutoFix/` implementing `ILatexLineFix` (line-local) or
   `ILatexDocumentFix` (needs the whole document). Use `LatexScan` helpers, not your own loops.
3. Add it to `LatexAutoFixer.DefaultLineFixes` or `DefaultDocumentFixes`. Order matters if one
   error can hide another; say why in a comment.
4. **Test both directions:** the broken form gets fixed, and correct LaTeX (including the
   already-fixed form) stays untouched. A second run must change nothing.
5. **Check against real documents:** run the fixer over every `step4-*-final*.tex` in the lecture
   folders. Compiled documents must get **0 changes and 0 findings**.
6. Contract: rewrite only what cannot compile, stay idempotent, report every change in German with
   the line number. Static switches are global state; tests that flip them go in the
   `AutoFixSettings` collection and restore the defaults.

## 6. Repairing a PDF by hand (playbook)

1. Read `<lecture>/step4-compile-log.txt`. The first `ERROR` line names the line (`l.497 …`)
   and usually the error class.
2. Look at that line in `step4-…-final.tex` and match it against section 3.
3. **Search the whole file for the same pattern.** The model repeats its mistakes (09-30 had the
   same broken `\texorpdfstring` five times). Fixing only the reported line costs one compile per
   occurrence.
4. Fix, then compile locally at no cost:
   `lecx pdf compile --tex "<lecture>/step4-…-final.tex" --fix-loop 0`
5. Repeat until the log says `SUCCESS`. If an error class shows up twice across lectures, turn it
   into a fix (section 5).

Files you will meet in a lecture folder:

| File | Meaning |
|---|---|
| `…-final-orig.tex` | Input as it was before any repair. Compare against it. |
| `…-final-tryN.tex` / `compile-log-…-tryN.txt` | AI repair round N and its compile log. |
| `…-tryN-rejected.tex` | An AI answer rejected as unsafe (prose instead of LaTeX, too short, …). |
| `step3-precheck-compile-log.txt` | Trial compile before step 3; its errors go into step 3's prompt. |
