# Gemini free tier: what the 250k limit counts

2026-10-23 · exported from the live doc https://claude.ai/artifact/UZhrqUSiUzXtnJeFuYeJXn (the same file sits in
`prompt-engineering/tools/` and `lec-extraction-prog/docs/`). Measurements: `C:\Users\miche\programming\gemini-cache-probe`.

## Summary

The free tier's limit of 250,000 input tokens per minute is checked against the request's **inline text characters ÷ 4**, not against its tokens. Video, images and text uploaded as files are not counted at that check, so one request can fill most of the 1,048,576-token context window.

- The edge is ~1M characters of inline text: ~380k tokens of the probe's nonsense, ~300k tokens of the real transcription prompt (3.30 characters per token).
- After a request is accepted, its real token count (media included) is charged to the minute, so a second large request within ~60 s is refused.
- Google's implicit cache does not raise the limit; it only speeds up the first reply.
- The system instruction rejects File-API links (HTTP 500); uploaded files work only in the user turn.
- Records in one accepted request: 997,287 tokens (900 inline images), 868,602 (370k text + 200 min video), 701,342 (uploaded text).
- lecx's part 4 failed because its inline text (prompt + inlined earlier parts) crossed ~1M characters. With `InlinePrecedingLecTexParts: false` the same part went through on the first attempt at 87 %.

## Why we started

lecx (lec-extraction-prog) sends ~280–400k-token requests on the free tier, and some passed while others failed for no visible reason.

- **Part 4 kept failing.** On 2026-10-18 (Information Security, three keys) every part 1–3 passed at 352k–387k tokens, and every part 4 (382k–396k) was refused twice, also on resume.
- **The warm-up did not explain it.** `Schritt 1/1` (284k) was accepted, then `[Resume Part 4]` (309k) refused.
- **Google's "cached" figure contradicted the acceptances.** Requests over 250k were accepted with "0 cached", so the figure could not be what let them through.
- **The usage page did not add up.** It showed 294.21K for 3.6 Flash after parts of ~359k, and 220.7K / 235.06K for 3.8 Flash, matching no request.

Real lecture runs change prompt, video, key and load at once, so they could not settle this. We built a probe that changes one thing at a time.

## How we measured

A new .NET 10 tool, gemini-cache-probe (private repo ZeroMatrix-Beta/gemini-cache-probe), sends controlled requests and logs every HTTP attempt.

- **Seeded nonsense text.** A fixed generator (SplitMix64) makes pseudo-words; the same seed gives the same bytes, a new seed text no one has sent. Every request opens with a warning that the text is nonsense and ends with "Reply with exactly: OK".
- **Identical prefixes, proven.** Each request carries SHA-256 hashes of its prefix and of the whole body.
- **Plain REST, no SDK.** `streamGenerateContent`, `countTokens`, `models.list` and the File API over HttpClient, so every error body (quota name, limit, retry delay) is visible.
- **What counts as a result.** Accept or refuse is the ground truth; time to first data is the second signal; Google's "cached" figure is only quoted.
- **Keys and models.** Four free-tier projects (profiles 1–4), each with its own limit; mostly gemini-3.5-flash-lite (500 requests/day), checks on gemini-3.1-flash-lite, gemini-3.6-flash and gemini-3.8-flash. Profile 0, the extraction key, stayed out.

| Id | Question |
| --- | --- |
| e1 | Which single cold request is refused? |
| e2 | How much fresh text can a warm prefix carry, and for how long? |
| e3 | Is an identical request accepted again (lecx's staging)? |
| e5 | Does video or image input count like text? |
| e6 / e9 | Do two requests in one minute add up, and where exactly? |
| e8 | Does a prefix that stays warm move the edge? (`--fixed-video`, `--sys-images`, `--sys-files` copy the pipeline) |
| e10 | Characters or JSON bytes? |
| e12 | Does text uploaded as a file count? |
| e13 / e14 | Does the system instruction take file links (text, images)? |
| e15 | Earlier parts inline vs uploaded, real lecture |
| e16 | Hundreds of inline images in the system instruction |

Also switches for lecx's own habits: `--max-output 65535 --thinking high`, `--count-first` (CountTokens right before the request).

## What the limit counts

Before a request, Google estimates it as **text characters ÷ 4** and refuses it when that, plus the real tokens already charged that minute, exceeds 250,000. After acceptance, the real token count is charged.

| Test | Highest accepted | Lowest refused | Estimate (÷ 4) at the edge |
| --- | --- | --- | --- |
| e1, single cold request, 10k steps (key 3) | 380,491 tokens | 390k | 248,620 / 254,988 |
| e8, 300k prefix + fresh text, keys 2, 3, 4 | 380,714 – 381,162 | 390k | same edge |
| e9, A 140k, then B 15 s later (key 1) | A 140,578 + B 160,448 | A 140,494 + B 170k | 140,578 + 104,900 = 245.5k / 140,494 + 111,133 = 251.6k |
| e10, 888k–933k chars, plain vs 15 % backslashes (3.1 Flash Lite) | all, up to 487,285 tokens | – | JSON bytes ÷ 4 up to 281k accepted |

- **Not tokens.** The same 933k characters passed both as 379k and as 487k real tokens (e10). Denser text gets more tokens through.
- **Not JSON bytes.** A backslash is two bytes in JSON; backslash-heavy text passed at 281k by bytes (e10). LaTeX costs nothing extra.
- **Real tokens afterwards.** In e9, request A counted at its real 140k, not its 92k estimate, against B.
- **Exactness.** The edge lies between 248.6k and 251.6k by the estimate; Google names the limit as exactly 250000 in every refusal.
- **Ruled out.** `maxOutputTokens` 65,535 with thinking `high` (340k–370k accepted), CountTokens right before the request (counted 371,013, then 371,012 accepted), and placement: system instruction and user turn hit the same edge.

## What does not count

Video, inline images and text uploaded through the File API pass the check for free; only the 1,048,576-token context window limits them.

| Request (single, cold unless noted) | Real tokens | Result |
| --- | --- | --- |
| 900 inline PNGs in the system instruction (e16) | 997,287 (980,100 image) | accepted |
| 370k text + 200 min synthetic video at 0.25 fps (e8) | 868,602 | accepted |
| 370k text + 240 min video | ~1.08M | 400: exceeds 1,048,576 tokens |
| 700k tokens of text as an uploaded text/plain file (e12) | 701,342 (1.73M chars) | accepted |
| 380k text + 40 min video, keys 1, 2, 3; 3.6 Flash (e8) | 481,486 / 481,161 / 481,082 / 480,529 | accepted; 390k text refused |
| 380k text + 12 real prompt images in the system instruction (e8) | 394,327 | accepted; 390k text refused |

- **File links go only in the user turn.** A text file linked from the system instruction: HTTP 500 on all attempts (3.5 Flash Lite 13, 3.6 Flash 10). 33 image files linked there: 500 on all 10. The same file in the user turn: accepted 3 of 3 (601,186 tokens).
- **Images cost context, not limit.** Each image is ~1,089 tokens in the context and on the usage page, whatever its size.
- **Larger requests answer later.** First data took 136 s at ~1M tokens of images, cold.

## The implicit cache and the warm-up

The implicit cache makes a repeated prefix answer faster but does not let a request through that would otherwise be refused, so the warm-up is not needed for the limit.

- **Same edge, warm or cold.** A 250k or 300k prefix kept warm for the whole run still met its edge at ~381k accepted / 390k refused, as a cold request does (e8).
- **Speed only.** e3 sent the same 279,758-token request three times: first data 7.0 s, then 1.4 s on the immediate repeat, 4.6 s after 5 minutes.
- **e2 could not show the cache's lifetime.** Every cell (150k warm + 50k or 120k fresh, after 65 s, 300 s, 1200 s) was under the edge anyway.
- **Google's "cached" figure is consistent.** Over all probe runs it reported a hit on 0 of 79 requests with a new prefix, 84 of 103 repeated within 10 minutes (typically 85 % of the prompt), and 0 of 2 repeated after 20 minutes. It only looked wrong while we read it as the reason requests were accepted: "0 cached" on an accepted request is normal, because the cache never decides acceptance.
- **The warm-up costs.** Two to three extra large requests per session (they count toward the daily quota), 2 × 64 s of waiting, and one more chance to be refused (on 2026-10-18 `[Resume Part 4]` was refused before the part was sent).

## The usage page in AI Studio

The page shows the peak of real tokens per minute, video included, several minutes late, so it can show more than its own 250K line.

| Reading | What it matches |
| --- | --- |
| Flash Lite, key 3: 150.06K, then 174.86K, then 300.35K / 250K | the e8 steps 150,062, 174,860 and 300,346, each accepted |
| Flash Lite: 380.49K / 250K | the e1 request of 380,491, accepted (estimate 248,620) |
| Flash Lite, key 1: 400.22K / 250K | the e5 request of 400,217, of which 99,600 video |
| 3.6 Flash: 294.21K / 250K | lecx's `[Resume Part 3]` warm-up (294,213); parts 3 and 4 (~359k with video) never showed |
| 3.8 Flash: 220.7K, 235.06K, 235.21K | no request of that size; unexplained |

The 3.6 and 3.8 readings stay open. Read the page as one more observation, not as the size the limit checks. It does show that Google counts the real usage: a workflow that routinely sits far above 250K per minute relies on the current check staying as it is.

## Why lecx's part 4 failed

Part 4 carried the most inline text: the prompt plus the inlined parts 1–3 crossed ~1M characters. Nothing in lecx's code was wrong.

- **The pipeline copied 1:1** (`--sys-files`): the AI Studio config's 55 prompt and history files in lecx's own wrapper, 863,427 characters (÷ 4 = 215,856), plus the 12 real images inline, then fresh text and a 25-minute video in the user turn. Predicted: accepted up to +50k fresh, refused at +60k. Measured: exactly that (386,741 accepted; text 311,479, video 62,250, images 13,012).
- **Real text runs at 3.30 characters per token** (863,427 / 261,312), so ~300k text tokens fit inline, against ~380k of the nonsense.
- **Part 4 of 2026-10-18 sat on that edge:** 382k–396k with ~77k video leaves ~305k–319k text tokens, ~1.0–1.05M characters.
- **Today's 03-14 parts 1–3 are 136k characters** (49.6k + 46.7k + 39.8k). Inline, they add ~34k to the prompt's ~216k: ~100 %.
- **e15, the real request both ways:** parts 1–3 inline were refused (429); the same three files uploaded were accepted at 376,757 tokens, and their ~40k tokens reached the model.
- **Why the trim helped:** the speech cut of 2026-10-23 (−17.7k tokens, ~58k characters) took the prompt back under the edge, which is why 03-14 parts 3 and 4 then passed inline.
- **lecx's 64 s waits** between warm-up and part already keep two large requests out of one minute; that was not the cause.

## Real lecture tests with lecx

With the earlier parts uploaded, 03-14 part 4 went through on the first attempt at 87 % of the limit, and the speech stayed verbatim.

**Test 1** — Information Security 03-14, part 4 only, gemini-3.8-flash, profile 1, warm-up on, `InlinePrecedingLecTexParts: false` in a copied config (`--config-dir`), parts 1–3 sent in full as text/plain uploads.

| Request | Estimate (log) | Real tokens | Result |
| --- | --- | --- | --- |
| `Schritt 0` | 542,207 chars ÷ 4 = 135,551 (54 %) | 151,621 | accepted |
| `Schritt 1/1` | 873,541 ÷ 4 = 218,385 (87 %) | 276,846 | accepted |
| part 4 | 875,899 ÷ 4 = 218,974 (87 %) | 381,583 | accepted, 1st attempt |

No `[Resume Part 4]` warm-up is sent in this mode. Compared with the inline part 4 from 13:13 (3.8 Flash, 13 attempts):

| | Uploaded (22:38) | Inline 3.8 (13:13) | Inline 3.6 |
| --- | --- | --- | --- |
| Opening section | Generic Composition for Authenticated Encryption | same | same, reworded |
| `hh:mm:ss` timestamps | 69 of 69 | 70 of 70 | 10 of 52 |
| Fillers per 1,000 words, by quarter | 54 / 56 / 88 / 64 | 45 / 25 / 27 / 25 | 56 / 28 / 25 / 26 |
| Size, sections, labels | 44k chars, 10, 5 | 40k, 8, 3 | 33k, 6, 0 |

The filler gain cannot be credited to the upload: four prompt commits for speech liveliness and stutters (18:47–19:22) landed between the two 3.8 runs. The filler count is crude (it counts "so" and "right" too), and it is one run.

**Runs A and B** — 03-14 again, gemini-3.8-flash, profile 2, warm-up off (one request per part), 1 fps instead of 0.25, earlier parts uploaded. Both needed several `503 high demand` retries (A 4 attempts, B 5); neither was refused by the limit.

| Run | Layout | Estimate (log) | Google's count | Video (1 fps) |
| --- | --- | --- | --- | --- |
| A, part 1 | history + 12 images inline in the system instruction | 874,289 chars ÷ 4 = 218,572 (87 %) | 417,691 | 159,239 |
| B, part 2 | history in the user turn, 12 images uploaded, part 1 uploaded | 875,759 ÷ 4 = 218,939 (87 %) | 433,130 | 159,239 |

- **Uploaded images cost the same tokens as inline ones.** B is 15,439 tokens more than A; part 1's uploaded `.tex` (49,572 characters, ~15,000 tokens) accounts for almost all of it.
- **1 fps doubles the video** from ~77k to ~159k tokens per part: context, not limit headroom.
- **Output at 1 fps** against the 0.25 fps versions of 2026-10-19 (older prompt): speech unchanged (36–37 uh/um per 1,000 words); part 2 has 10 sections instead of 6 and 17 slide notes instead of 4. A hint of better visual capture, not a proof.
- **CountTokens runs ~18.6k above Google's own count** in both runs (436,312 vs 417,691; 451,685 vs 433,130).

## Recommendations for lecx

Keep the rules inline in the system instruction, send everything bulky as uploads in the user turn, and drop the warm-up.

1. **Set `InlinePrecedingLecTexParts: false`.** It exists already and keeps every part at the prompt's own share (87 % today), whatever the part number.
2. **Set `EnableImplicitPrefixCacheWarmup: false`.** The cache does not raise the limit; the warm-up costs 2–3 large requests and ~2 minutes per session.
3. **Build `UploadHistoryAsFiles` (sketch).** The training history (30 files, 322,317 chars, ~80k at the limit) goes as one uploaded `training-history.md` at the start of the user turn; its images stay inline in the system instruction; the rules keep the file tree. Inline text drops to ~541k chars (~54 %).
4. **Watch the new log line.** Since `800f68c` every request logs `Minutenlimit-Schätzung: N Textzeichen ÷ 4 = M (P %)`. Over 100 %: too much inline text. Under 100 % and refused: another request in the same minute.
5. **Montages are optional.** Inline images never count at the limit; 33 single images instead of 12 montages cost ~23k context tokens and give full-size boards.
6. **Do not render text as images.** It bypasses the limit, but costs about as many tokens, risks misread LaTeX, and Google's terms forbid getting around usage limits.

## Open questions

- [ ] Does the upload itself keep the speech verbatim, or only the new prompt? A/B: today's prompt, part 4 inline vs uploaded, same model.
- [ ] Is an uploaded file cached? Google reported "597,979 cached" on repeats, but first data did not get faster.
- [ ] Do model turns count like user turns? Untested; matters for refinement's continue requests.
- [ ] Is there a daily token limit on the free tier? Only requests per day are known: 20 per key for gemini-3.8-flash, found when profile 1 ran out on 2026-10-23.
- [ ] Is the divisor exactly 4? The edge is pinned to 248.6k–251.6k; a 1k-step scan would narrow it.
- [ ] Why did the 3.6 Flash usage page stay at 294.21K after ~359k parts, and what do the 3.8 readings match?
- [ ] Does the model follow rules as well at ~1M tokens of context as at ~300k?

## How we got there

All on 2026-10-23, each step answering the question the one before it raised; times local, newest first.

| Time | Step | Result |
| --- | --- | --- |
| ~23:40 | lecx runs A (part 1, inline images) and B (part 2, uploaded images), 1 fps, warm-up off (profile 2) | both accepted after 503 retries, 87 %; image tokens equal |
| ~22:45 | lecx full lecture, images uploaded, 1 fps (profile 1) | stopped at once: 3.8 Flash quota of 20 requests/day used up |
| 22:38 | lecx Test 1: 03-14 part 4, earlier parts uploaded | accepted 1st try at 87 %; committed docs `d1d4640` |
| ~21:50 | e16: 100–900 inline images in the system instruction | all accepted, up to 997,287; `c8c14e1` |
| ~21:10 | e15: real 03-14 parts inline vs uploaded; e13 on 3.6 Flash | inline refused, uploaded 376,757; 3.6 rejects file links in the system instruction; `888bc4d` |
| ~20:25 | e13 / e14: file links in system instruction vs user turn | system 500, user turn accepted; `c0bd393` |
| ~20:13 | e12: text uploaded as a file | 701,342 accepted |
| ~20:10 | `--count-first`, `--max-output 65535 --thinking high` | both ruled out |
| ~20:08 | 1:1 pipeline copy (`--sys-files`) | refused exactly at the predicted ~1M characters |
| ~20:04 | e8 with the 12 real images in the system instruction | edge unchanged |
| ~19:55 | video-length scan, 370k text + 80–240 min video | 868,602 accepted; 240 min hit the context window |
| ~19:30 | e10: plain vs backslash text, same characters | characters count, not JSON bytes or tokens |
| ~19:20 | e8 + video on keys 1–3 and 3.6 Flash | text edge unchanged by video, key or model; `c3393d3` |
| ~19:00 | e5: 300k text + 40 min video / + text / + 20 images | video and images free, text refused |
| ~18:50 | e8 300k prefix on keys 2, 3, 4 | warm prefix does not move the 390k edge |
| ~14:15 | e9 pair-sum, e1 fine scan, e8 250k prefix | the bytes ÷ 4 rule found; `9bfd664`, `84b600f` |
| ~13:55 | e1, e2, e6, e3, e8 first runs | single requests over 250k accepted; pairs add up |
| ~13:30 | gemini-cache-probe built (`0774516`) | after the usage page and "cached" figure failed to explain lecx's runs |
| morning | lecx 03-14 parts 3–4 on 3.6 and 3.8 Flash; usage-page oddity logged in agents.md | part 4 on 3.8 needed 13 attempts |

Code: gemini-cache-probe (experiments e1–e16, 28 tests) and lec-extraction-prog `800f68c` (the log line), `f338354`, `23a7d22`, `d1d4640`, `a2690e6` (docs).
