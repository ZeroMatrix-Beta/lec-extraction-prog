# Deep dive — `InlinePrecedingLecTexParts` as an upload switch

Companion to [implementation-plan.md](../implementation-plan.md) — Phase 12 (.tex upload switch).

## Correction to the main plan

The main plan says to *add* `".tex" => "text/plain"` to the mime switch in
`AttachmentUploader`. **That is wrong** — reading the file more closely:

```csharp
// src/GoogleAi/AttachmentUploader.cs:25
private static readonly HashSet<string> s_textExtensions =
    [".md", ".txt", ".cs", ".json", ".xml", ".html", ".py", ".js", ".ts", ".css", ".tex"];
```

`.tex` is **already** in the text-extension set, and that set short-circuits at
`UploadAndAttachFileAsync` line ~155, well before the mime switch at line 169.
Text files are read and inlined as
`<attached_file name="…">…</attached_file>` text and never uploaded at all.

So the real change is not "teach it about `.tex`" but "give the text branch an
opt-in bypass". The mime switch does still need a `.tex` entry, because once the
bypass is taken, execution reaches it.

## What the prompt looks like today

`AiStudioAutoExtractionSession.BuildGenerationRequestAsync` (line 1031) builds
the user turn as, in order:

1. **One text Part**:
   `ReferenceContextPreamble` + `<reference_context file="part0.tex">…dummy anchor…</reference_context>`
   + one `<reference_context file="partN.tex">…</reference_context>` per preceding
   part + `GetStaticPromptBeginning(partNumber)`
2. the video `attachmentParts`
3. `parsedPrompt` (segment-specific parameters)

Two things the main plan got imprecise:

* **The whole block is gated on `DebugSendReferenceFile`, not on
  `InlinePrecedingLecTexParts`** (line 1067). AI Studio genuinely never reads
  `InlinePrecedingLecTexParts` — confirmed again here.
* **The dummy anchor, the preceding `.tex` blocks and the static beginning are
  all in the _same_ Part.** That matters for what follows.

Vertex (`VertexAutoExtractionSession.cs:1439` / `:1455`) reads the flag and
treats `false` as "append the same text at the **end** of the prompt instead" —
still text either way.

## Prefix-cache consequences, precisely

Implicit prefix caching matches on a byte-identical *leading* run of the
request. Today, per part:

```
Part 1: preamble | dummy |                        | static(1) | video | params
Part 2: preamble | dummy | tex1 |                 | static(2) | video | params
Part 3: preamble | dummy | tex1 | tex2 |          | static(3) | video | params
```

So part 3 shares `preamble|dummy|tex1` with part 2 before diverging. That
incremental growth is exactly what the config comment claims ("Part 3 reuses the
prefix from Part 2") and it is real.

Under upload-as-file, the text Part becomes constant across all parts except for
`static(N)`:

```
Part N: preamble | dummy | static(N) | [FileData tex1..texN-1] | video | params
```

The shared prefix collapses back to `preamble|dummy` — the anchor only. So the
main plan's claim that `false` trades away prefix-cache continuity is **correct**,
and this is the concrete size of the trade: you lose the incremental `tex1…texN`
run, keeping only the ~4500-token dummy anchor.

**Placement decision:** put the `FileData` parts **after** the text Part, not
between the anchor and the static beginning. Splitting the text Part in two
around the attachments would also break the `preamble|dummy` match, costing the
anchor benefit as well for zero gain.

## What it does and does not buy

Be honest about this in the commit message, because the intuition is wrong:

* **It does not reduce token cost.** The `.tex` content is tokenised either way.
  Roughly the same input tokens per request.
* **It does reduce payload size on the wire**, and the `.tex` text stops being
  re-sent on every "Continue" continuation — up to 6 per part. That is the real
  saving, and it grows with part count.
* **It costs one upload request per preceding `.tex` file per part**, which
  spends RPM quota — the app's binding constraint. Part 3 uploads 2 files. Over a
  3-part video that is 3 extra requests, against the ~9 that gating
  `LogTokenCountsAsync` (Phase 10) already gives back.
* AI Studio Files API objects expire after 48 h — far beyond one run, not a
  concern.

Net: it is a genuine trade, not a free win. Worth having as a switch; not
obviously worth defaulting to — see the settled default below.

## Implementation

**1. `AttachmentUploader` — add the bypass** (do this after the Phase 11 split of
`UploadAndAttachFileAsync`, so it lands in a small method rather than a 222-line one):

```csharp
public async Task<bool> UploadAndAttachFileAsync(
    string filePath, List<Part> parts, bool asSystemInstruction = false,
    string? baseDirectory = null, bool uploadTextAsFile = false,   // new
    CancellationToken cancellationToken = default)
```

* Guard the text short-circuit with `&& !uploadTextAsFile`.
* Add `".tex" => "text/plain"` to the mime switch (and only `.tex` — do not open
  the bypass to `.cs`/`.json`/etc., which have no caller and no need).
* `asSystemInstruction` must keep winning over `uploadTextAsFile`: system
  instructions have to be inline text to participate in the prefix at all.

**2. AI Studio — wire the flag it currently ignores.**
In `BuildGenerationRequestAsync`, inside the existing `if (_config.DebugSendReferenceFile)`:

* `InlinePrecedingLecTexParts == true` → unchanged, byte-for-byte. This is the
  important half: the default path must produce an identical request.
* `false` → build the text Part as `preamble + dummy + static(N)` only, then
  upload each `previousTexFile` with `uploadTextAsFile: true` and append the
  returned parts **after** that text Part, before the video.

**3. Vertex — same semantics**, replacing the append-at-the-end branch at
`VertexAutoExtractionSession.cs:1455`. Its `BuildPreviousTexReferenceBlockAsync`
helper stays for the `true` path.

### Settled: the read-only instruction is kept (decision, 2026-07-28)

The `<reference_context file="…">` wrapper cannot survive on a `FileData` part,
so the instruction it carries has to be restated in the text Part.

**Decision: keep `ReferenceContextPreamble` in the text Part and add a line
naming the attached files as read-only reference**, e.g.

```
Die folgenden angehängten .tex-Dateien sind Referenzkontext (read-only): part1.tex, part2.tex
```

Rationale: dropping an explicit read-only instruction from an otherwise
unchanged prompt is precisely the kind of regression that only shows up as
degraded transcription quality, which is slow and expensive to attribute. The
line costs a handful of tokens.

Rejected: relying on attachment order alone (no signal if it goes wrong); and
writing the wrapper into temp copies of each `.tex` before upload (means never
uploading the real files, for no benefit over a one-line statement).

### Settled: default stays `true`, both paths ship (decision, 2026-07-28)

Both code paths are implemented behind the flag. `InlinePrecedingLecTexParts`
ships defaulting to **`true` on both configs** — today's behaviour — so nothing
changes on the next run and upload mode is opt-in.

The default is then set **from measurement, not from the reasoning above**: run
one short video with `false`, one with `true`, compare the token reports, the
wall-clock, and the output quality. The analysis in this document predicts the
trade but has not been tested against the real API; treat it as a hypothesis to
check, not a conclusion. Record the outcome here when it exists.

### Interaction with the warm-up handshake

`PrimePrefixCacheAsync` sends `preamble + dummy + static(1)`. Under
`false`, real Part-1 requests become `preamble + dummy + static(1)` too — which
is *more* exactly aligned with the warm-up than the current `true` path
(where Part 2+ inserts `tex` blocks between dummy and static). So the handshake
keeps working and arguably matches better. No change needed there, but say so in
the commit message so it isn't re-investigated later.

## Verification

Automated coverage cannot reach this (paid API). So:

1. Build 0/0, tests green.
2. **Diff the assembled request with the flag `true` against the pre-change
   build** — dump `history` to JSON in both and confirm byte-identical. This is
   the check that matters: it proves the default path is untouched.
3. One short video end-to-end with `false`, then `true`; compare the `.tex`
   output quality and the token report.
4. Land last, alone, after Phase 10 has quietened the console enough to actually
   read what happened.
