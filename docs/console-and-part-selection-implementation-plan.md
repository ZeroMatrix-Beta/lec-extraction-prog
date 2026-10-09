# Implementation Plan: Console Output Improvements & Selective Part Execution

This document specifies two major architectural enhancements for `lec-extraction-prog`:
1. **Console Output, Delay Logging, and Stream Safety**
2. **Selective Part Execution (`--from-part`, `--part`) & Resilient Part Caching**

---

## 1. Console Output, Delay Logging & Stream Safety

### 1.1 Problem Statement
1. **Countdown Ticker Flood**: In non-interactive or redirected streams (log files, background tasks, CI runners), `AnsiConsole.Status` cannot rewrite lines in place. A 64-second wait produces 64 distinct lines (`⏳ Warte 64s: ...`), causing severe log bloat.
2. **Stream Collisions**: When LLM output streams directly to stdout without a trailing newline, any subsequent warning or error banner (e.g., on connection loss) gets appended directly to the end of the partial LaTeX token.
3. **Unicode Mojibake on Windows**: Emojis like `⏳` and box-drawing glyphs `──` render as garbled characters (`â ³`, `â”€â”€`) on Windows console streams using OEM code pages.
4. **Mixed Language Phrasing**: Standard German banners were frequently paired with English fallback strings like `"Still waiting for the acknowledgment / processing..."`.

### 1.2 Implemented Design
- **`InteractiveDelay.cs`**:
  - Automatically inspects `bool isUnattended = !Ui.PromptSource.IsInteractive || Console.IsOutputRedirected || Console.IsErrorRedirected`.
  - When unattended or redirected, skips `AnsiConsole.Status` entirely and emits a single clean notice:
    ```csharp
    Ui.Info($"Warte {seconds}s: {message}", "Delay");
    ```
  - In interactive mode, throttles status updates so `ctx.Status(...)` is only updated once per second when the remaining countdown value changes.
  - Updates default message to `"Warte auf Server-Antwort / Verarbeitung..."`.
- **`Ui.cs` Line Tracking**:
  - Tracks `_isLineOpen` in `Ui.Raw` and `Ui.RawLine`.
  - Automatically calls `EnsureNewLine()` before rendering any structured badge (`Info`, `Warn`, `Error`, `Success`, `Step`, `Detail`, `Header`), preventing banner collisions with unfinished streams.
- **`CliBootstrapper.cs` Encoding**:
  - Configures both `Console.OutputEncoding = Encoding.UTF8` and `Console.InputEncoding = Encoding.UTF8`.
- **Language Normalization**:
  - Normalized status strings across `ApiRetryPolicy.cs`, `ResponseStreamPrinter.cs`, and `AttachmentUploader.cs`.

---

## 2. Selective Part Execution & Resilient Part Caching

### 2.1 Problem Statement
1. **Wasteful Failure Deletion**: Currently, if extraction fails on Part 3 or 4 (e.g., reaching daily API quota or prolonged rate limits), the failure branch in `AiStudioAutoExtractionSession` iterates over `state.GeneratedTexFiles` and deletes them:
   ```csharp
   foreach (var failedTexFile in state.GeneratedTexFiles) {
       System.IO.File.Delete(failedTexFile);
   }
   ```
   This deletes successfully transcribed parts (Parts 1 and 2), throwing away paid tokens and compute time.
2. **No Selective Part Selection**: Users currently cannot instruct the pipeline to transcribe only a specific part (e.g., `--part 3`) or resume starting from a given part (e.g., `--from-part 2`).

### 2.2 Proposed Solution

#### A. Preserve Succeeded Parts on Failure
- In `AiStudioAutoExtractionSession.cs` and `VertexAutoExtractionSession.cs`, remove the deletion of `GeneratedTexFiles`.
- Retain all valid, generated `.tex` files on disk so subsequent runs can immediately reuse them within the resume window.

#### B. Command-Line Options
Add two new options to `ExtractionOptions.cs` and `RunCommand.cs`:
1. `--from-part <n>`:
   - Starts transcription from segment `n` (1-indexed) up to the final segment.
   - For any parts `< n`, checks if the corresponding `.tex` part already exists on disk. If present, it loads the header and content into memory and includes them in the inlined prefix-caching reference context.
2. `--part <n>`:
   - Transcribes *only* segment `n`.
   - Leaves other segments untouched on disk.

#### C. Extraction Planner Integration (`lecx plan`)
- Update `ExtractionPlan.cs` and `ExtractionPlanner.cs` to accept `fromPart` and `singlePart`.
- Adjust `pendingSegments` and `pendingRequests`:
  - If `--part 3` is specified for a 4-part video, `pendingSegments` will report `1`.
  - If `--from-part 2` is specified and Part 1 exists on disk, `pendingSegments` will report `3` (or fewer if subsequent parts exist).

#### D. End-of-Run Assembly
- In `FinalizeVideoOutputAsync`:
  - Reads any missing parts from existing files on disk so that `step1-...-all-offset.tex` and `step1-...-all.tex` are always fully assembled from all parts `1..totalParts`.
  - If a part is missing and was not processed in this run, logs a warning and assembles whatever parts are available.

---

---

## 3. Dedicated Cleanup Commands (`lecx clean` & `lecx files`)

### 3.1 Problem Statement
1. **Disk Sprawl in Output Folders**:
   - Each lecture creates multi-GB sliced video chunks in `<lecture>/tmp/` (`part1.mp4`, `part2.mp4`, etc.) and `-speed-1-compressed.mp4`. Once a lecture is transcribed, these intermediate video chunks are rarely needed.
   - Re-running extractions without manual archiving creates `-copy-1.tex`, `-copy-2.tex` duplicate files, alongside prompt logs and compiler logs.
   - Users currently have to manually organize ad-hoc folders like `old/`, `old2/` using File Explorer or PowerShell.
2. **Cloud Storage Accumulation**:
   - Google AI Studio File API stores uploaded videos for up to 48 hours. When iterating or testing batches, uploaded remote files accumulate against project storage limits without an easy CLI mechanism to list or purge them.

### 3.2 Proposed Cleanup & Command-Line Controls

#### A. Pipeline Command-Line Flags on `lecx run` & `lecx extract run`
These flags allow automated or direct control over cleanup as part of a run:
| Flag | Behavior |
|---|---|
| `--clean-before` | Automatically archives existing `.tex`, `.pdf`, `.aac` outputs in the target lecture folder to `archive/` before running, ensuring completely clean output filenames without `-copy-1.tex` suffixes. |
| `--clean-tmp` | Deletes temporary FFmpeg video segments (`tmp/*.mp4`) upon successful completion to reclaim multi-GB disk space automatically. |
| `--clean-copies` | Removes any stray `-copy-*.tex` files from earlier aborted runs before processing. |

#### B. Standalone Output Cleanup: `lecx clean`
```bash
lecx clean --video "lecture.mp4" [flags]
lecx clean --folder "D:/lectures" [flags]
```
| Flag | Behavior |
|---|---|
| `--archive` *(default)* | Moves previous `.tex`, `.pdf`, and `.aac` outputs into a timestamped `old-YYYYMMDD-HHmmss/` directory so a fresh run has no collisions. |
| `--tmp` / `--cache` | Deletes intermediate FFmpeg video segments (`<lecture>/tmp/*.mp4`) to reclaim disk space after extraction completes. |
| `--copies` | Deletes stray `-copy-*.tex` duplicate files produced by previous clashing runs. |
| `--all` | Archives previous outputs and purges `tmp/` intermediate videos. |
| `--dry-run` | Shows what would be moved or deleted without modifying files. |

#### C. Interactive Runtime Console Commands (from Stdin while running)
While `lec-extraction-prog` is executing (during backoff delays, between segments, or during retries), the console listener accepts interactive text commands without having to kill the process:
| Command | Action |
|---|---|
| `[Enter]` / `skip` | Immediately skips the current delay/backoff countdown. |
| `retry` | Immediately cancels the current wait and triggers an API retry. |
| `profile <n>` (e.g. `profile 2`) | Dynamically switches the active API key profile to `<n>` on the fly (saving the run if a 429 quota error occurs mid-lecture!). |
| `model <id>` | Switches the model on the fly if the current model faces a prolonged high-demand outage. |
| `clean` | Cleans up intermediate temp files in `tmp/`. |
| `stop` / `abort` | Cleanly halts the pipeline, preserving all completed parts on disk without deleting them. |

#### D. Cloud Storage Management: `lecx files`
```bash
lecx files list [--profile <n>]      # List active remote video/audio files on Google File API
lecx files purge [--profile <n>]     # Delete remote files across projects to free quota
lecx cloud purge                     # Purge Vertex AI GCS bucket (wrapping GcsWorkspace.PurgeAsync)
```

---

## 4. Verification & Testing

1. **Unit Tests (`tests/LectureExtraction.Tests/`)**:
   - `ExtractionPlannerTests.cs`: verify that `--from-part` and `--part` correctly calculate pending requests and segment counts.
   - `ExtractionHelpersTests.cs`: verify part numbering and naming logic.
   - `CleanCommandTests.cs`: verify archiving and temp cleanup operations without data loss.
2. **Console Output Verification**:
   - Verify that redirected runs output a single clean delay notice rather than 60+ ticking lines.
3. **Mandatory Build Verification**:
   - Verify `dotnet build` passes with **0 Warnings, 0 Errors**.
   - Verify `dotnet test` passes with **all tests green**.

