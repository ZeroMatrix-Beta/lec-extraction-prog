# Driving this pipeline as an agent

Written for an AI agent (or any unattended script) operating `lecx`. If you read one
file, read this one.

The binary is `bin/Debug/net10.0/lec-extraction-prog.exe`. `lecx.cmd` in the repository
root is a shim for the same thing. **Running it with no arguments starts an interactive
menu that will block forever** — always pass a command.

---

## The five rules

### 1. `plan` before you spend

`lecx plan` issues no API call and costs nothing. It tells you how many videos matched,
how many requests are still pending, which model and key profile would be used, and
whether that key actually resolves.

```bash
lecx plan --folder "D:/lecture-videos/analysis2" --json
```

If `pendingRequests` is not the number you expected, your `--folder` or `--from` is
wrong. Find that out here, not four videos into a paid batch.

### 2. Never pass `--save-config`

The app rewrites its own `*Config.json` during a normal run. In CLI mode that writeback
is **off by default**, which is what keeps an unattended run from changing the settings
the human comes back to. `--save-config` turns it back on. You almost never want it.

If you need different settings for one run, pass them as flags (`--model`, `--profile`,
`--parts`). They apply to that run only.

### 3. Read the exit code, especially `6`

| Code | Meaning | What to do |
|---|---|---|
| 0 | Everything requested completed | — |
| 1 | Unhandled exception | Read stderr; this is a bug |
| 2 | Bad arguments | Fix the command line |
| 3 | A prompt had no answer | You hit an interactive question; supply the value as a flag |
| 4 | Config or credentials | An API key env var is unset — `lecx config profiles` |
| 5 | API gave up after retries | Rate limit or outage; retry later |
| **6** | **Partial success** | **Some videos produced output and others did not.** Do not treat as success |

`6` is the one that matters. A batch that transcribed four videos and failed the fifth
exits `6`, and the JSON payload names which.

### 4. Resume is time-limited — this is where money leaks

A finished `.tex` part is reused instead of re-requested **only if it is younger than two
hours**. Retry a failed batch three hours later and you silently pay for every part
again.

```bash
lecx plan --folder ... --resume-window 24 --json   # what would be reused with a 24h window
lecx run  --folder ... --resume-window 24          # actually use it
```

`--force` does the opposite: ignore everything on disk and re-request. Only use it when
you know the existing output is wrong.

### 5. Under `--json`, stdout is the payload and stderr is the log

```bash
lecx plan --folder ... --json 2>/dev/null | jq .pendingRequests
```

Without `--json`, human-readable logging goes to stdout and there is nothing to parse.

---

## A safe working loop

```bash
# 1. Is the environment sane? (exits 4 if the active key is missing)
lecx config profiles --json

# 2. What would happen? (no cost)
lecx plan --folder "D:/lecture-videos/analysis2" --json > plan.json

# 3. Check the warnings before committing to it
jq -r '.warnings[]' plan.json

# 4. Rehearse the exact command (no cost)
lecx run --video "<one video>" --dry-run --json

# 5. Run it for real
lecx run --video "<one video>" --json
echo "exit=$?"
```

## Stopping between stages

Every stage is separately runnable, and each reads what the previous one wrote:

```bash
lecx media segment --input video.mp4 --out ./work --json   # local, free
lecx extract run   --video video.mp4 --out ./work --json   # .tex only, no refinement
lecx refine run    --tex ./work/<name>/step1-<name>.tex --json
lecx pdf compile   --tex <refined>.tex --fix-loop 0 --json # --fix-loop 0 keeps it free
```

`lecx run` does all of this in one go, because the pipeline already chains internally.
Use the separate commands when you want to inspect or intervene between stages.

---

## Things that will surprise you

**Two variants of the same lecture collide.** A folder often holds both `lecture.mp4`
and `lecture-speed-1-compressed.mp4`. The compression suffix is stripped when deriving
the output folder, so **both resolve to the same folder and the same part filenames** —
each reads the other's segments and `.tex` as its own cache. `plan` reports this in
`warnings`. Pick one variant with `--video`, or accept that the pair is one lecture.

**The output folder and the `.tex` stem differ.** The folder is
`<target>/<name-without-compression-suffix>/`, but the parts inside are
`step1-<name>-partN.tex`. Predicting `.tex` paths from the folder name alone finds
nothing.

**`pdf compile` is not free by default.** Its repair path sends the failed document and
the compile log to the model. `--fix-loop 0` makes it purely local.

**Parallelism means processes, not threads.** The pipeline keeps its rate-limit pacing in
one process-wide clock, so two runs inside one process would corrupt each other's timing.
Use `lecx batch`, which spawns one child process per worker. See
[parallel.md](parallel.md).

**A menu can never be auto-answered.** If a flow reaches an interactive choice, the run
fails with exit `3` naming the question rather than picking the first entry. That is
deliberate: guessing a menu entry is how an unattended run buys the wrong model. Supply
the value as a flag instead. `--yes` accepts defaults only for questions that *have* a
considered default — it does not answer menus.

---

## Not implemented

`lecx ask` is registered but refuses with exit `2`. Everything else in `--help` works.
