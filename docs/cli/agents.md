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

### For now: always pass `--thoughts file`

**Current standing instruction from the maintainer (2026-10-15):** every paid command (`run`,
`extract run`, `refine run`, `pdf compile`, `finish`, `batch`) gets `--thoughts file`, so each
stage leaves an `<output>-thoughts.md` beside its output. Not every stage has thought summaries
on in its config (step 2, speech, does not), and without the file a continue or an odd answer
can only be explained from token counts afterwards, not from the model's reasoning. It does
not change the request beyond asking for the summaries, and it is not saved to config.

```bash
lecx finish --folder "<lecture folder>" --thoughts file
lecx thoughts show --folder "<lecture folder>" --stage speech
```

This holds until the maintainer says otherwise.

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

## Finishing what is started

`lecx status` names each lecture's next stage; `lecx finish` runs it. Use `finish`, not
`run`, for any lecture that already has output — it picks the right step file and command
per folder (see [commands.md](commands.md#lecx-finish--paid)):

```bash
lecx status --folder "D:/lecture-videos/analysis2/extracted_output"
lecx finish --folder "D:/lecture-videos/analysis2/extracted_output" --dry-run   # free: table + exact commands
lecx finish --folder "D:/lecture-videos/analysis2/extracted_output" --limit 2
```

---

## Things that will surprise you

**A raw video and its `-speed-N-compressed` sibling are one lecture, and the compressed
one wins.** A folder often holds both `lecture.mp4` and `lecture-speed-1-compressed.mp4`.
The compression suffix is stripped when deriving the output folder, so both map to the
same folder and part filenames. You do **not** have to pick a variant, because the program
already does:

- `--folder` / `--from` / `--limit` (and so `plan`, `run`, `batch`) keep only the
  compressed file of each pair (`ExtractionHelpers.DeduplicatePreferCompressed`).
- `--video <raw.mp4>` is swapped for the compressed sibling in the same directory, with an
  info line `Verwende vorhandene komprimierte Datei`.
- FFmpeg then cuts the segments from that compressed file (`VideoSegmentProducer`).

So a pair can only collide if you hand both paths to a script yourself. `plan` still
reports `warnings` for that case. To see which file a run will really use, read the
`videos[].fileName` of `lecx plan --json` — not the filename you passed.

**A 1 FPS video is split as-is, whatever its name.** Before splitting, the producer
compresses a video unless it is already compressed (`ExtractionHelpers.IsAlreadyCompressedAsync`).
A `-compressed` / `-speed-N-compressed` name settles that without opening the file. Any other
name is probed with ffprobe, and an average frame rate of at most 2 FPS
(`MaxCompressedFrameRate`) counts as compressed: raw ETH recordings run at 25 FPS, every
compression keeps 1, whichever preset, CRF or tool made it. Only the name counts for
choosing between a raw file and its sibling above, so a 1 FPS file without the marker is
not preferred over a raw file of the same lecture; it just isn't compressed again. The log
says `bereits komprimiert (niedrige Framerate)` when the frame rate decided it.

**Run `lecx` from the repository root — otherwise `run` loses its PDF and corrupts step 3.**
Two files in the repository root are looked up relative to the *current directory*:

- The preambles (`PdfCompilation.PreamblePath*`, relative): `pdf-preamble-de.tex` for a German
  lecture, `pdf-preamble-en.tex` for an English one, `pdf-preamble.tex` when the language is
  unknown. The transcript's `% Primary Language:` line decides (else its words); the log names the
  choice as `Preamble: … (Sprache: …)`. The two language files are generated from the Grundstrukturen
  and Algebra 1 book preambles: after either changes, run `py tools/build-language-preambles.py`.
  From anywhere else the log says
  `Preamble-Datei (pdf-preamble.tex) nicht gefunden`. The probe compile before step 3 then
  fails, and step 3 is handed that failure as if it were a LaTeX syntax error to fix. Step 4
  skips the PDF, and the video exits `6` although all text steps finished. Redo step 3 and
  the PDF from the repository root with `refine run --step 3 --through-end`.
- `dummy-part0.tex` (also searched next to the `.exe`). Missing, it only logs `dummy-part0.tex
  nicht gefunden`: it is a Lorem-Ipsum placeholder for the implicit prefix cache, so the
  transcription is unaffected and only a cache hit becomes less likely.

A script that drives `lecx` must `cd` into the repository root first, and pass `--video` and
`--out` as absolute paths.

**"High demand" (HTTP 503) gives up after 8 attempts and loses only that part.** The video then
ends with exit `6`. The parts finished before it are on disk, so running the same video again
within `--resume-window` requests only the missing part and the refinement. Whether the 503
attempts count against the free-tier daily quota is **not settled**. In the maintainer's
experience they usually do not. But on 2026-10-15 one key was refused with
`GenerateRequestsPerDay…` on `gemini-3.8-flash` after about 9 requests that were not 503
(4 warm-ups, 1 complete answer, 2 stream breaks, 1 continue) and about 20 that were. So either part of
the 503s counted (perhaps those that failed after Google had started generating), or the key
was used outside this pipeline that day. The per-project usage page in AI Studio or the Cloud
console is the place to check. On 2026-10-19 that page showed 7 requests for a run that sent 6 (2 warm-ups, 4 parts):
the 7th was part 4's first attempt, which ended in a 503. The log shows no streamed text before
it (`Die bisherige Teilantwort wird verworfen` is printed on every retry, it is no evidence of
output). So at least some 503s count against the daily quota; which ones is still open.

**A lost internet connection does not use up attempts.** When the Google host cannot be resolved
or has no route (`Der angegebene Host ist unbekannt`, `[Netzwerk-Fehler]` in the log), the app
probes it with a bare TCP connect to port 443 every `NetworkPollIntervalSeconds` (15) for up to
`NetworkMaxWaitMinutes` (30) and resends at once when it answers. The probe sends no request to
Google (no TLS, no HTTP, no quota); a DNS lookup alone would not do, since it answers from the cache
while the route is down. The failed request never reached Google, so it counts neither against the
retry limit nor in the request table. If the connection stays down for the full wait, the part or
step fails as after any exhausted retry. Other connection errors (reset, timeout) still use the
counted retry with its 300 s wait.

**A request over the per-minute token limit passes only with a cache hit.** The free tier caps
input tokens per minute per model and key: 250,000 on `gemini-3.8-flash`, `gemini-3.7-flash` and
`gemini-3.5-flash-lite` (`GenerateContentInputTokensPerModelPerMinute-FreeTier`, seen 2026-10-15/16).
A part's request here is ~340k (system instruction + history ≈ 270k, plus the video) and grows by
~12k per earlier part.

**Cached tokens do not count against that limit.** Shown by Google's accept/refuse, not by the
"Gecacht" figure: on 2026-10-16 `gemini-3.5-flash-lite` (limit 250,000) accepted warm-up handshakes
of 274,357 and 309,995 tokens a minute apart, with 129k and 38k fresh. So the warm-up
(`EnableImplicitPrefixCacheWarmup`) is what lets an oversized request through. It is no guarantee:
the cache is best-effort ([Documentation.md §4.4](../../Documentation.md)), and the 390k part-4
request right after those handshakes, with the same prefix plus the video, was refused twice. That
the 2026-10-13 parts (340k–352k, "Gecacht: 0") went through suggests they were cached and the
figure was wrong.

After the second refusal the part fails with `Die Anfrage hat N Eingabe-Tokens … zweimal
abgelehnt` and the video exits `6`. Ways out: warm-up on, less history (`HistoryPreloadPaths`), no
earlier parts (`DebugSendReferenceFile: false`), or a paid key.

**The warm-up is a config switch, with no CLI flag.** `EnableImplicitPrefixCacheWarmup` in
`AiStudioAutoExtractionConfig.json`; when off, the log says `Lade History-Dateien direkt in den
Session-Zustand (Warmup deaktiviert)`. When on, a session sends one handshake per history batch
before its first part (`Schritt 0`, `Schritt 1/1`, …). A session that starts at part 2 or later
(`finish` / `run --resume` with earlier parts on disk, inlined) sends one more, `[Resume Part N]`:
system instruction + history + the inlined earlier parts, everything but the video
(`PrimePrefixCacheForResumedPartAsync`). If it fails, the part is sent anyway.

**Part 4 is where the limit bites (2026-10-18).** Information Security, three keys: every part 1–3
went through (352k–387k, warm-up off), every part 4 (382k–396k) was refused twice, in the first run
and on resume. With warm-up on, `Schritt 1/1` (284k) was accepted, but the `[Resume Part 4]`
handshake (309k) and part 4 (382k) were refused. Editing the prompt files between runs changes the
prefix, so a resumed session starts with a prefix Google has never cached.

The earlier parts are the one input you can shrink without losing the lecture: `ReferenceSlimming` in
`AiStudioAutoExtractionConfig.json` says per kind of part what goes (`OlderParts`, `PrecedingPart`;
details in [Documentation.md §4.4](../../Documentation.md)). Since 2026-10-18 the older parts lose
speech, didactic boxes, `aiNote`s and TikZ (~2–3k tokens each instead of ~5k); the part directly before
stays complete. That is margin, not a fix: part 4 still needs the cache. First test, 02-23 part 4 on
a fresh key, warm-up on, no dummy part: context 19.5k, `[Resume Part 4]` (299k) and the request
(359k) both accepted, one answer to `Video complete`. The same evening 02-29 and 03-01 part 4
(364k each) passed too. Google reported 284k, 133k and 285k cached; that figure proves nothing
either way (see above), only the accept does.

**A request that sends nothing for 10 minutes is sent again.** The heartbeat prints
`Noch keine Daten vom Server (N min …)` every two silent minutes; at `FirstChunkTimeoutMinutes`
(appsettings.json, default 10, `0` = off) the attempt is abandoned and retried after 30 s. On
2026-10-15 one request stalled for 20 minutes and its retry then answered normally.

**A stream that stops mid-answer is continued after the same 10 minutes.** The limit is on silence,
not length, so a long answer is never cut. Once data has arrived the heartbeat says `Stream liefert
seit N min nichts mehr`; at the limit the text received so far is kept and a continue request
picks up from it. Before 2026-10-18 only the first chunk was timed: three refinement streams on three
keys went silent mid-answer around midnight and waited over an hour, and the heartbeat kept saying
"Noch keine Daten" although the answer had begun. A process stuck like that (built before the fix)
never ends on its own: stop it and continue with `lecx finish`.

**`lecx config profiles` can show room on a key Google has already refused.** On 2026-10-17 it
showed profile 2 at `2/20` for `gemini-3.8-flash` while Google answered `limit: 20` exceeded. The
counter is the app's own bookkeeping, not Google's; trust the refusal.

**After a refinement failure, continue with `finish`, not `run`.** `run` reuses the finished
parts, but it combines them again and starts the refinement at step 1. Because the first
combined file is still there, the second one is written as `step1-<name>-all-offset-copy-1.tex`,
and every later file carries that stem (`step2-<name>-all-offset-copy-1-offset-merged.tex`, …).
The merge is paid for twice and the names no longer match the table in
[pipeline.md](pipeline.md). `lecx finish --folder <lecture>` continues from the last finished
step; by hand that is `refine run --tex <that step's input> --step N --through-end`. `plan`
flags videos that are already in refinement.

**Output to a file or pipe is not wrapped.** Log lines and model text go out as single lines;
tables and rules are laid out for 160 columns. In a console window nothing changed.

**The output folder and the `.tex` stem differ.** The folder is
`<target>/<name-without-compression-suffix>/`, but the parts inside are
`step1-<name>-partN.tex`. Predicting `.tex` paths from the folder name alone finds
nothing.

**`pdf compile` is not free by default.** Its repair path sends the failed document and
the compile log to the model. `--fix-loop 0` makes it purely local.

**A PDF that will not compile is usually a known error class.** Before any AI repair, the
highly experimental auto-fixer (`PdfCompilation.ExperimentalLatexAutoFix`) applies our own
rule-based fixes, and the AI repair gets structural findings with line numbers. To repair
by hand: read the first `ERROR` in `step4-compile-log.txt`, fix every occurrence of that
pattern (not just the reported line), and compile with `--fix-loop 0`. The playbook and the
known error classes are in [../latex-auto-fix.md](../latex-auto-fix.md).

**Parallelism means processes, not threads.** The pipeline keeps its rate-limit pacing in
one process-wide clock, so two runs inside one process would corrupt each other's timing.
Use `lecx batch`, which spawns one child process per worker. See
[parallel.md](parallel.md).

**An answer that breaks off is never kept as a result.** When a request fails for good
mid-answer (say a continue that hits eight `503`s), is cancelled, or keeps coming back blank,
the text received so far is a fragment. The loop reports that (`GenerationEnd` /
`GenerationLoopResult.IsUsable`), and every stage treats it as a failure instead of passing it on:

- Stage 1: the part is not written, so the next run (or `finish`) requests it again instead
  of resuming from a half part.
- Refinement step: error `Antwort unvollständig`, the fragment goes to the step's
  truncated-response file for inspection, and the pipeline stops before the next step.
  Before 2026-10-16 such a fragment (43 of 68 speech blocks) passed the 60 % integrity check
  and step 3 polished it.
- PDF fix: that fix attempt is discarded.

A run that stops on the request budget (`MaxContinueRequests`) is *not* treated this way:
the text is complete up to the budget and is kept as before.

**A menu can never be auto-answered.** If a flow reaches an interactive choice, the run
fails with exit `3` naming the question rather than picking the first entry. That is
deliberate: guessing a menu entry is how an unattended run buys the wrong model. Supply
the value as a flag instead. `--yes` accepts defaults only for questions that *have* a
considered default — it does not answer menus.

---

## Reading the model's reasoning

Run with `--thoughts file` (or set `ThoughtSummaries.Enabled` per stage) and each stage
writes `<output>-thoughts.md` beside its output. Then:

```bash
lecx thoughts list --folder "<output-folder>" --json        # stage, requests, thinking tokens
lecx thoughts show --folder "<lecture>" --stage speech --json # the summaries themselves
```

The files are plain Markdown with one `<!-- lecx-thoughts... {json} -->` line per file and
per request, so `cat` works too. Thinking tokens share `MaxOutputTokens` with the answer;
a request ending in `MAX_TOKENS` with a large thinking count is where a continue came from.

---

## Not implemented

`lecx ask` is registered but refuses with exit `2`. Everything else in `--help` works.
