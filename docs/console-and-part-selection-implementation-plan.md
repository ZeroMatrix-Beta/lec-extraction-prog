# Implementation Plan: Console Output, Part Reuse & Cleanup

| Section | Status |
|---|---|
| 1. Console output, delay logging & stream safety | Done (`776c6b3`, `e1887d4`) |
| 2. Part reuse and redoing a single part | Planned |
| 3. Cleanup (`lecx clean`, `--clean-tmp`, `lecx files`) | Planned |
| 4. Dropped from the first draft, with reasons | - |
| 5. Order and verification | - |

---

## 1. Console Output, Delay Logging & Stream Safety

### 1.1 Problem Statement
1. **Countdown Ticker Flood**: In non-interactive or redirected streams (log files, background tasks, CI runners), `AnsiConsole.Status` cannot rewrite lines in place. A 64-second wait produces 64 distinct lines (`⏳ Warte 64s: ...`), causing severe log bloat.
2. **Stream Collisions**: When LLM output streams directly to stdout without a trailing newline, any subsequent warning or error banner (e.g., on connection loss) gets appended directly to the end of the partial LaTeX token.
3. **Garbled Glyphs in Logs**: Emojis like `⏳` and box-drawing glyphs `──` show up as `â ³`, `â”€â”€`. The program writes valid UTF-8 (the CLI sets `Console.OutputEncoding = UTF8`); the pattern is UTF-8 read back as Windows-1252 by whatever opens the log (e.g. PowerShell 5.1 `Get-Content` without `-Encoding UTF8`). The program cannot fix that reader, only avoid the glyphs.
4. **Mixed Language Phrasing**: Standard German banners were frequently paired with English fallback strings like `"Still waiting for the acknowledgment / processing..."`.

### 1.2 Implemented Design
- **`InteractiveDelay.cs`**:
  - Chooses the display by `Ui.CanRedrawInPlace` (Spectre's `Interactive && Ansi`), not by attended/unattended. Spectre treats the console as non-interactive as soon as *any* of stdin/stdout/stderr is redirected, and its live `Status` then prints every update as a new line - even when the display is a real window.
  - Can redraw: live countdown, updated only when the remaining second changes - also for an unattended `lecx run` in a terminal.
  - Cannot redraw: one line up front instead of a ticker:
    ```csharp
    Ui.Info($"Warte {seconds}s: {message}", "Delay");
    ```
  - One wait loop serves both displays; Enter-to-skip depends only on whether a person is at the keyboard (`Ui.PromptSource.IsInteractive`), not on the display.
  - Updates default message to `"Warte auf Server-Antwort / Verarbeitung..."`.
- **`Ui.cs` ASCII frames**:
  - `Step` (rule), `Header` (panel) and `Table` use ASCII borders when `Ui.IsTerminalOutput` is false (Spectre's output stream is a file or pipe), so a log stays readable in a Windows-1252 reader. Spectre's own `Unicode = false` does not help here: it only swaps to "safe" borders that are still box-drawing glyphs.
  - Umlauts, emojis and dashes inside message text are unaffected; a log containing them must be read as UTF-8.
- **`Ui.cs` Line Tracking**:
  - Tracks `_isLineOpen` in `Ui.Raw` and `Ui.RawLine`.
  - Automatically calls `EnsureNewLine()` before rendering any structured badge (`Info`, `Warn`, `Error`, `Success`, `Step`, `Detail`, `Header`), preventing banner collisions with unfinished streams.
- **Language Normalization**:
  - Normalized status strings across `ApiRetryPolicy.cs`, `ResponseStreamPrinter.cs`, and `AttachmentUploader.cs`.

---

## 2. Part Reuse and Redoing a Single Part

### 2.1 Where things stand

Already done (`776c6b3`): a failed part no longer deletes the parts finished before it, and an
exhausted per-day quota fails fast with a `--profile` hint instead of sleeping for hours. Re-running
with another profile inside the resume window reuses the finished parts; after the window,
`--resume-window 48` does. **Resuming after a failure therefore needs no new option.**

What is still wrong or missing:

1. **Reuse ignores how a part was cut.** The rule is "the `.tex` exists and is younger than the
   window", and it exists four times: `AiStudioAutoExtractionSession`, `VertexAutoExtractionSession`,
   `ExtractionPlanner.CountResumableSegments`, and the pre-upload in `AiStudioAutoExtractionSession`
   (which checks existence only, without the window). Change `NumberOfParts` from 4 to 2 within two
   hours and `part1.tex` - the first *quarter* - is reused as part 1 of 2, the first *half*: the
   combined document silently loses a quarter of the lecture. The part headers now record
   `part n of N` and `PART_START_SECONDS`, so this is detectable.
2. **A redone part is never used.** Every write goes through `ExtractionHelpers.ResolveNonClashingTexPath`:
   when `part3.tex` exists, the new text lands in `part3-copy-1.tex`, while resume and assembly keep
   reading `part3.tex`. The same holds for `-all.tex` / `-all-offset.tex`: after a re-run the new
   document is `...-copy-1.tex` and `refine run --tex step1-<name>-all-offset.tex` picks up the old
   one. This is also where the `-copy-N` sprawl comes from.
3. **One bad part cannot be redone** without `--force`, which re-bills every part of the video.

### 2.2 Design

#### A. One reuse rule, with a geometry check
- A new `PartReuse.Check` in `src/Extraction` replaces all four copies:
  ```csharp
  PartReuseDecision Check(string texPath, int partNumber, int totalParts,
                          double? expectedStartSeconds, TimeSpan window, bool ignoreAge)
  ```
  The decision is one of *reuse*, *missing*, *too old* or *cut differently*, with a reason for the
  log and the plan.
- Geometry: read the part's header layer (`TexDocumentWriter.SplitHeaderLayers`). `part n of N` must
  match `totalParts`; `PART_START_SECONDS` must match the segment's start within 1 s when both are
  known. On a mismatch the part is re-transcribed, with a warning naming both geometries.
- Headers written before these fields existed: age only, as today, plus a count proxy (a
  `part{N+1}.tex` next to it means the set was cut into more parts).
- Start times: the formula `(duration + (N - 1) * overlap) / N` currently lives inline in
  `media probe` and separately in the segment producer. Move it into one pure function in `src/Media`
  used by both and by the reuse check. To check first: whether `VideoSegment.StartTimeSeconds` is in
  original-video seconds or after `--speed`, so the header value is compared in the same unit.
- Planner: probes the duration (ffprobe) of every video that already has parts on disk, so it applies
  exactly the run's rule. A plan that disagrees with the run is worse than no plan. Videos without
  parts need no probe, so a fresh plan stays instant.

#### B. Replace, don't copy
- Replace `ResolveNonClashingTexPath` in both sessions with `ExtractionHelpers.ArchiveAndReplace(path)`:
  if the target exists, move it to `<lecture>/archive/<yyyyMMdd-HHmmss>/` (one timestamp per run),
  then write under the canonical name.
- Effects: the canonical names always hold the newest output, so resume, assembly and
  `refine run --tex ...` see what was just produced; nothing that was paid for is deleted; no new
  `-copy-N` files appear.
- Covers part files (`partN.tex`, `partN-offset.tex`) and combined files (`-all.tex`,
  `-all-offset.tex`). Check how the refinement names its step outputs and apply the same rule if it
  copy-suffixes too.
- Resume, planner and `media` read exact names and `tmp/`, so they never see `archive/`.

#### C. `--redo-part <n>` on `lecx run` and `lecx extract run`
Re-transcribes part `n` of one video and rebuilds the combined documents from the parts on disk.

- Requires `--video`. With `--folder`, with `--force`, or with `n` outside `1..N` (after `--parts`
  or `auto` resolution): exit `2` (Usage). `batch` does not forward it.
- Every other part must exist and pass the geometry check, **regardless of age** - the caller named
  the bad part, which vouches for the rest. A missing or differently cut part exits `2` before any
  API call and names the parts; a normal run (or `--force`) is the way to fill them.
- Part `n` is transcribed even if its file is fresh; the old file goes to `archive/` (B).
- Parts `1..n-1` are passed as preceding context exactly as in a normal run (`GeneratedTexFiles`),
  so continuity and prefix caching match the original run.
- `lecx run` then chains into refinement as usual, since the combined document changed;
  `lecx extract run --redo-part` stops after assembly.
- Implementation: a `RedoPart` init property on both sessions next to `ResumeWindow`. In
  `TranscribeSegmentsAsync`, part `RedoPart` skips the reuse check; every other part calls
  `PartReuse.Check` with `ignoreAge: true`. The pre-upload uses the same check, so it uploads only
  part `n`.
- FFmpeg: the producer still needs the segment. Its 48 h cache usually has it; otherwise it re-cuts
  the video - minutes of local work, no API cost.
- Name: `--redo-part`, not `--part`. `--parts 3` already exists and means "cut into 3 parts"; one
  letter between "cut into 3" and "redo part 3" is a typo waiting to happen.

#### D. `lecx plan`
- Accepts `--redo-part`: that video reports `pendingSegments = 1`; a missing or differently cut part
  becomes a warning (the run would exit `2`).
- Each planned video gains `parts: [{ number, decision, reason }]`, so a caller sees *why* a part
  will be re-billed (too old, cut differently) instead of only a count.

---

## 3. Cleanup

### 3.1 Problem Statement
1. `<lecture>/tmp/` holds the compressed video and every segment - gigabytes per lecture. It doubles
   as the FFmpeg cache (48 h) and is never deleted.
2. `-copy-N.tex` files from earlier re-runs. 2.2.B stops new ones; existing ones remain.
3. Uploads to the Gemini Files API stay until they expire after 48 h. A project holds up to 20 GB,
   which heavy `batch` use can reach.

### 3.2 Design

#### A. `lecx clean --video <mp4> | --folder <dir>`
| Flag | Does |
|---|---|
| `--tmp` | Deletes `<lecture>/tmp/`. Safe: FFmpeg recreates it when needed. |
| `--copies` | For each canonical name with `-copy-N` siblings, keeps the **newest** of them under the canonical name and moves the rest to `archive/<timestamp>/`. Never deletes. |
| `--dry-run` | Lists what would be moved or deleted, and the bytes freed. |

- At least one of `--tmp` / `--copies` is required.
- Skips a lecture whose `tmp/` or `.tex` files changed in the last 10 minutes (a run is probably
  using them); `--force` overrides. Exit `6` when lectures were skipped.
- "Newest wins" matters: a `-copy-1` written by a re-run is *newer* than the canonical file, so the
  first draft's "delete the copies" would often have deleted the latest paid transcription.

#### B. `--clean-tmp` on `lecx run` / `lecx extract run`
- Deletes a video's `tmp/` once it finished successfully (for `run`, including refinement).
- Off by default: keeping `tmp/` is what makes a later `--redo-part` cheap.

#### C. `lecx files list | purge [--profile <n>]`
- `list`: name, display name, size, created, expires and state of every upload in the project behind
  the profile's key.
- `purge --older-than <hours>` (default 6) `[--dry-run]`: deletes uploads older than that. The age
  floor protects a `batch` worker that is using its upload in the same project right now.
- Uses the SDK's Files list/delete calls; today only `UploadAsync` and `GetAsync` are used, so check
  the exact API surface of the Google.GenAI version in the project first.
- Possibly better later: delete a video's uploads once the video is done. Decide after `files list`
  shows how much actually accumulates.

---

## 4. Dropped From the First Draft

| Idea | Why dropped |
|---|---|
| `--from-part <n>` | Covered already: parts survive a failure and a re-run reuses them (`--resume-window` past 2 h). What it added - skipping missing earlier parts - produces an incomplete document. |
| `--part <n>` | Replaced by `--redo-part` (2.2.C): too close to `--parts`, and it did not say that the part is redone even when it exists. |
| Typed commands while running (`skip`, `retry`, `profile`, `model`, `clean`, `stop`) | `skip`/`retry` = Enter, `stop` = Ctrl+C now that finished parts survive. `profile`/`model` mid-video cannot just swap: uploads and caches belong to the old key's project and model, so everything would be uploaded again - failing fast and re-running with `--profile` does the same with no new machinery. `clean` would delete segments in use. Stdin is already shared by Enter-to-skip, prompts and key polling, and `batch` workers have no keyboard. |
| `--clean-before` | Archives the `.tex` parts - the resume cache section 2 depends on. That is `--force` plus a file move, and with 2.2.B `--force` archives on its own. |
| `--clean-copies`, `clean --copies` as deletion | Would often delete the newest output; replaced by newest-wins archiving (3.2.A). |
| `clean --archive` (default), `--all` | With 2.2.B outputs are archived when replaced; a separate whole-folder archive step adds nothing. |
| Two archive locations (`archive/` and `old-YYYYMMDD-HHmmss/`) | One convention: `<lecture>/archive/<yyyyMMdd-HHmmss>/`. |
| `lecx cloud purge` | The CLI drives AI Studio only; Vertex is disabled for cost reasons and its sessions purge their bucket themselves (`GcsWorkspace.PurgeAsync`). |

---

## 5. Order and Verification

Each step can ship on its own:

1. 2.2.A - one reuse rule with the geometry check. Fixes a silent bug that exists today.
2. 2.2.B - replace instead of copy.
3. 2.2.C / 2.2.D - `--redo-part` and its plan.
4. 3.2.A / 3.2.B - `lecx clean`, `--clean-tmp`.
5. 3.2.C - `lecx files`.

Tests (`tests/LectureExtraction.Tests/`):
- `PartReuseTests`: fresh / too old / missing; part count differs; start time differs; legacy header
  (age only, count proxy); `ignoreAge`.
- Planner: `--redo-part` gives one pending segment; a missing other part gives a warning; a
  differently cut part counts as pending with its reason; planner and run share `PartReuse.Check`.
- `ArchiveAndReplace`: an existing target moves into `archive/<ts>/`, the canonical file has the new
  content, no `-copy-` file appears, and two writes in one run share one timestamp folder.
- CLI validation, following `CliExitCodeTests`: `--redo-part` with `--folder`, with `--force`, out
  of range - each exits `2`.
- `clean`: `--copies` keeps the newest under the canonical name; `--tmp` touches nothing outside
  `tmp/`; `--dry-run` changes nothing; a recently modified lecture is skipped.
- `files purge`: the age filter as a pure function - no API calls in tests.

Docs: `docs/cli/commands.md` (new options and commands) and `docs/cli/pipeline.md` ("Caching and
resume": geometry check, archive folder).

Build: `dotnet build` with 0 warnings, 0 errors; `dotnet test` all green.
