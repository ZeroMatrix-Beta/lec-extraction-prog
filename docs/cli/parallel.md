# Running several videos at once

**Supersedes `Documentation.md` §4.6.** That section tells you to copy
`bin/Debug/net10.0/` into `instance1/`, `instance2/`, … so each instance gets its own
config file. You no longer need to: `--config-dir` and the per-run flags give the same
isolation without copying anything.

---

## Why processes and not threads

This is not a preference. Four pieces of process-global mutable state make in-process
parallelism actively wrong:

| State | What breaks |
|---|---|
| `InteractiveDelay.LastGenerationCompletionTimeUtc` | **The decisive one.** A single process-wide rate-limit clock, written after every generation and read to decide how long to wait next. Two sessions in one process would each read the *other's* timestamp — destroying exactly the independent pacing that separate API keys exist to provide |
| `AttachmentUploader.HasJustUploaded` | A single flag written and read across chat, extraction and refinement; concurrent uploads flip each other's |
| `SessionCostLedger` | One set of counters per process, so per-run cost attribution becomes meaningless |
| `Ui` / `AnsiConsole` | One console. Interleaved output, and live status regions corrupt each other |

Separate processes get separate clocks, separate ledgers and separate rate-limit budgets
for free. Making the pipeline thread-safe would be a large refactor of code that already
works correctly for one run at a time.

## `lecx batch`

```bash
lecx batch --folder "D:/lecture-videos/analysis2" \
           --workers "profile=1:model=gemini-3.5-flash,profile=2:model=gemini-3.6-flash"
```

The supervisor contains **no pipeline logic**. It:

1. resolves the video list exactly as `plan` and `run` do,
2. deals videos round-robin across workers — whole videos only, so no two workers ever
   touch the same output folder or `tmp` directory,
3. spawns one `lecx run` child per video, with that worker's `--profile` and `--model`,
4. tees each worker's output to `<log-folder>/worker-N.log`,
5. aggregates exit codes: `0` if all succeeded, `6` if some did, `1` if none did.

Add `--dry-run` to see the assignment without running anything:

```
── 2 Worker ───────────────────────────────────────────────
  Worker 0: Profil 1, Modell gemini-3.5-flash — 7 Video(s)
  Worker 1: Profil 2, Modell (konfiguriert) — 7 Video(s)
  Logs: D:\...\extracted_output\batch-logs
```

## One profile per worker

Two workers sharing a profile share a rate-limit budget, so they finish **slower** than a
single worker would — the opposite of the point. The spec parser refuses it:

```
[batch] [FEHLER] Profile 1 used by more than one worker; they would share one
rate-limit budget. Give each worker its own profile.
```

Parsing is strict for the same reason: a typo that silently fell back to the default
profile would double the load on one key.

`lecx config profiles` lists the profiles and shows which resolve in this environment.

## Free-tier quota: 20 requests per key, per model, per day

The keys are free tier. Google's limit is
`GenerateRequestsPerDayPerProjectPerModel-FreeTier (limit: 20)`: 20 generate requests per
day, **per key (project) and per model name**. It resets at 00:00 UTC (02:00 summer time).
One lecture with 4 parts and refinement needs well over 7 requests (parts, continues,
refinement steps), so one key gets through about two lectures per model per day.

- **The "20/20 Bereit" in `lecx quota` / `config profiles` is the app's own count**, not
  Google's. It does not see requests made outside this program. On 2026-10-13 it showed
  20/20 for every key while Google refused profiles 2 and 3 after a few requests. Do not
  plan a batch on it.
- **An exhausted key fails fast and the run moves on.** The part request is retried once,
  then the video is abandoned (`allSucceeded: false`). Every further video on that key fails
  the same way a few minutes later (after cutting and upload). A queue loop must therefore
  **stop the worker** on the first daily-quota error. Otherwise one exhausted key drains the
  whole queue, marking every lecture it touches as failed.
- **Match `PerDay`, not `FreeTier`.** The per-minute limit
  `GenerateContentInputTokensPerModelPerMinute-FreeTier (limit: 250000)` carries the same
  `FreeTier (limit` text. The app simply waits it out (`Please retry in 41s`) and the run
  succeeds, so a script that greps for `FreeTier` reports a false "quota exhausted" for a
  lecture that finished. Only `GenerateRequestsPerDayPerProjectPerModel` means the day is over.
- **`gemini-3.7-flash` is mapped to `gemini-3.8-flash`, with its own quota.** Requests to
  `gemini-3.7-flash` are answered by `gemini-3.8-flash` (user's observation; Google documents
  the same kind of routing for `gemini-3.5-flash` → `gemini-3.6-flash` on its "What's new in
  Gemini 3.5" page). The quota is still counted **per requested model name**: on 2026-10-13
  profiles 2 and 3 were refused for 3.8 after their daily 20 and served `gemini-3.7-flash`
  right away. The `.tex` header records the requested name, 3.7.

## Eight workers on four keys

Because the two names have separate daily budgets (and, going by the limit's name `PerModelPerMinute`, separate per-minute ones too), each key can run **two**
workers at once: one on `--model gemini-3.8-flash`, one on `--model gemini-3.7-flash`. Four
keys give eight independent budgets (8 × 20 requests a day). `lecx batch` cannot express this,
since it refuses two workers on one profile; run two queue loops instead, one per model name,
sharing one job list. This was first run on 2026-10-15: `run-queue3.sh` (four 3.8 workers;
`run-queue4.sh` is its copy with the `PerDay` fix) plus `run-queue-37.sh`, which adds four
3.7 workers to the same queue, all in
`D:\lecture-videos\analysis2\extracted_output\rerun-logs\`.

Things to watch with eight: the upload of eight ~130 MB segments at once needs a stable
connection, and "high demand" (HTTP 503) hits both names alike, because both are served by
the same 3.8 backend.

## Doing it by hand

`batch` is a convenience. Two shells work just as well, because the isolation comes from
the flags, not the supervisor:

```bash
# shell 1
lecx run --folder D:/lecture-videos/analysis2 --from 02-16 --profile 1

# shell 2
lecx run --folder D:/lecture-videos/analysis2 --from 03-30 --profile 2
```

Neither writes configuration (writeback is off unless `--save-config`), so they cannot
fight over a `*Config.json`. If you *do* want each to persist its own settings, give each
its own directory instead of copying the build output:

```bash
lecx run --config-dir ./cfg-worker1 --profile 1 --save-config ...
```

## What `--profile` switches

One `--profile N` per worker covers the **whole** pipeline, not just extraction: `run`
also points the refinement session (merge, speech, polish, PDF repair) at index `N` of the
refinement config's key list (`RunCommand.BuildSession`). So four workers on profiles 0-3
use four independent keys end to end. Profile 4 (`API_KEY-latex-refinement`) is only used if
you pass `--profile 4` yourself.

## Logs are written per video, at the end

`batch` collects each child's output with `ReadToEnd` and writes it to `worker-N.log` only
once that video's process exits, so the log does not grow while a lecture is running (that
takes roughly an hour). Live evidence of progress is the output folder (new
`step1-…-partN.tex` files) and `lecx status`, and the per-process session folders under
`C:\Users\miche\gemini-logs\folder-*` (`chat_log.md`).

## Running a hand-picked set of videos

`batch` can only select with `--folder`, `--from` and `--limit`, so it cannot run an
arbitrary, non-contiguous list (for example "exactly the lectures that already have an
output folder"). For that, run `lecx run --video <file> --profile N --parts 4 --out <dir>`
yourself, one loop per key, with a shared work queue so a free key takes the next video
instead of a fixed round-robin. Each `lecx run` is one video, so it keeps the same
guarantees as `batch` (separate process, clock and key).

## Watch for output-folder collisions

Sharding guarantees two workers never get the same *file*. A raw video and its
`-speed-N-compressed` sibling are collapsed to the compressed one before sharding, so they
land on one worker. Only paths you assemble by hand can still share an output folder; `plan`
and `batch` warn about it.
