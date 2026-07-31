# Command reference

Every command accepts the [global options](#global-options) below. Anything marked
**paid** issues billable API requests; everything else is local and free.

---

## `lecx run` — paid

The whole pipeline: preprocess → segment → transcribe → refine → PDF. This is the
one-liner. It works because the pipeline already chains internally — a successful
extraction constructs the refinement session itself.

```bash
lecx run --video "D:/lecture-videos/analysis2/02-19-2026-thursday-week1-....mp4"
lecx run --folder "D:/lecture-videos/analysis2" --from 03-30-2026
```

| Option | Meaning |
|---|---|
| `--video <file>` | A single video file to work on |
| `--folder <dir>` | Every `.mp4` in this folder. Defaults to the configured source folder |
| `--from <text>` | Skip videos before the first whose filename contains this, in chronological order |
| `--stop-after extract` | Transcribe only; skip refinement and PDF |
| `--resume-window <hours>` | How long a finished `.tex` part stays reusable. Default **2** |
| `--force` | Ignore existing `.tex` parts and re-request every segment |
| `--out <dir>` | Target folder. Defaults to `<source>/extracted_output` |
| `--parts <n>` · `--overlap <s>` · `--speed <x>` · `--preset <p>` | Segment geometry and FFmpeg preset |
| `--model <id>` · `--profile <n>` | Model and API-key profile for this run only |

Exits `0` on full success, `6` if some videos succeeded and others failed, `4` if the
key does not resolve (checked **before** any request).

## `lecx extract run` — paid

Identical to `run` minus `--stop-after`, and it never chains into refinement. Produces
the per-part `.tex` files and stops.

## `lecx plan` — free

Reports what a run would do and calls nothing. Same selection options as `run`.

```bash
lecx plan --folder "D:/lecture-videos/analysis2" --json
```

Payload includes `videoCount`, `pendingRequests`, `resumableSegments`,
`apiKeyResolves`, `resumeWindowHours`, a per-video breakdown, and `warnings`.
Exits `4` when the active profile's key is unset.

## `lecx media` — free

| Command | Does |
|---|---|
| `media probe --input <mp4>` | Duration, size, parsed lecture date, resulting segment geometry |
| `media segment --input <mp4>` | Compresses and slices into overlapping segments |
| `media audio --input <mp4>` | Extracts the mono AAC track used for timestamp correction |

`media segment --json` emits a `PreparedVideo` — the same record the pipeline passes
internally from its FFmpeg producer to its Gemini consumer:

```json
{
  "sourceVideoPath": "...", "outputFolder": "...", "tempFolder": "...",
  "segments": [ { "filePath": "...", "startTimeSeconds": 0 } ],
  "cameFromCache": true, "sourceDurationSeconds": 5036
}
```

`probe` exits `1` if ffprobe cannot read the file, rather than reporting zeroes.

## `lecx refine run` — paid

```bash
lecx refine run --tex out/step1-lecture.tex            # all three steps
lecx refine run --tex out/step1-lecture.tex --step 2   # only step 2
lecx refine run --tex out/step1-lecture.tex --step 1 --through-end
```

| Option | Meaning |
|---|---|
| `--tex <file>` | **Required.** The file to work on |
| `--step 1\|2\|3` | One step only: merge/timestamps, speech, final polish. Omit for all three |
| `--through-end` | With `--step`, continue through the remaining steps and compile the PDF |
| `--audio <file>` | Audio for timestamp correction. Defaults to `*_audio.aac` beside the `.tex` |

## `lecx pdf compile` — paid unless `--fix-loop 0`

Runs step 4 alone. **Not free by default**: the repair path sends the failed document and
its compile log to the model.

```bash
lecx pdf compile --tex final.tex --fix-loop 0   # local only
```

## `lecx batch` — paid

One child process per worker, each with its own API-key profile and therefore its own
rate-limit budget. See [parallel.md](parallel.md) for why this is processes and not
threads.

```bash
lecx batch --folder "D:/lecture-videos/analysis2" \
           --workers "profile=1:model=gemini-3.5-flash,profile=2"
```

| Option | Meaning |
|---|---|
| `--workers <spec>` | **Required.** Comma-separated `profile=N:model=X` entries |
| `--log-folder <dir>` | Per-worker logs. Defaults to `<target>/batch-logs` |

Plus the selection and geometry options from `run`. Two workers on the same profile are
**refused** — they would share one rate-limit budget and finish slower than a single
worker.

## `lecx config` — free

| Command | Does |
|---|---|
| `config list [--section <name>]` | The effective configuration |
| `config get <Section.Path>` | One value. `AiStudioAutoExtractionConfig.CurrentModel` or `...Paths.SourceFolder` |
| `config set <Section.Path> <value>` | Writes one scalar value |
| `config models` | Selectable models per backend |
| `config profiles` | Key profiles and whether each resolves. **Exits 4** if the active one does not |
| `config folders` | Configured source and target folders |

`config get`/`set` accept both the nested path and its flat alias — `Paths.SourceFolder`
and `SourceFolder` reach the same value. `set` handles scalars only; arrays have no
unambiguous command-line spelling.

## `lecx ask`

**Not implemented.** Registered so the intended shape is visible; refuses with exit `2`.

---

## Global options

| Option | Meaning |
|---|---|
| `--json` | Payload to **stdout**, all logging to **stderr** |
| `--dry-run` | Resolve everything and report; issue no paid request |
| `--yes` / `-y` | Accept defaults for questions that have one. Does **not** answer menus |
| `--save-config` | Allow the run to persist config changes. **Off by default** |
| `--config-dir <dir>` | Where the `*Config.json` live. Also suppresses the working-copy mirror write |
| `--quiet` / `-q` | Errors only |

## Exit codes

| Code | Meaning |
|---|---|
| 0 | Success |
| 1 | Unhandled exception |
| 2 | Usage / bad arguments |
| 3 | An unattended prompt had no answer |
| 4 | Configuration or credentials |
| 5 | API exhausted its retries |
| 6 | Partial success — some items succeeded, others failed |
