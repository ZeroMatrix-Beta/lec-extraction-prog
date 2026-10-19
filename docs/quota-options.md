# Getting past the free tier's 250k tokens/minute: options

Written 2026-10-19 after the 03-14 Information Security lecture failed on part 3 (383,858 input
tokens, refused twice on the free tier). Nothing here has been tried yet: no billing account was
linked and no paid run was made. Prices and rules are from Google's pages as read on 2026-10-19;
re-check them before acting.

## What Gemini (the chatbot) claimed, checked against Google's pages

| Claim | Verdict | Source |
|---|---|---|
| Linking a Cloud billing account moves the project to Tier 1 | **Confirmed, with a catch**: the billing page also requires a **prepay of at least $5** (new users default to Prepay; at $0 balance every key fails with HTTP 402). Upgrades "typically take effect instantly". | [rate-limits](https://ai.google.dev/gemini-api/docs/rate-limits), [billing](https://ai.google.dev/gemini-api/docs/billing) |
| Tier 1 gives "2M–4M+ TPM" | **Unverifiable.** Google publishes no per-model TPM; it is shown only in AI Studio (aistudio.google.com/rate-limit, needs login) and "not guaranteed". Tier 1 has a $250/month cap and a $10 per 10 minutes spend-rate limit. | [rate-limits](https://ai.google.dev/gemini-api/docs/rate-limits) |
| Tier 1 unlocks explicit context caching | **Confirmed in substance**: caching is "a paid feature"; the Paid plan lists "Access to Context caching". (The 3.8 Flash free-tier caching cell says "Free of charge", which likely means implicit caching only.) | [explicit caching (legacy page)](https://ai.google.dev/gemini-api/docs/generate-content/caching), [pricing](https://ai.google.dev/gemini-api/docs/pricing) |
| Paid tier opts your data out of training | **Confirmed**: "Used to improve our products": free Yes, paid No. | [pricing](https://ai.google.dev/gemini-api/docs/pricing) |
| Google AI Pro includes $10/month of Google Cloud credit usable for the Gemini API | **Confirmed** (credits expire a year after grant, redeemed on developers.google.com/program/my-benefits onto a billing account). **But** users report AI Studio refusing them until a prepay top-up, and paid calls failing once the credit ran out; no official fix. | [Google blog](https://blog.google/innovation-and-ai/technology/developers-tools/gdp-premium-ai-pro-ultra/), [GDP benefits FAQ](https://developers.google.com/profile/help/benefits), [forum thread](https://discuss.ai.google.dev/t/10-monthly-developer-program-credits-visible-in-gcp-billing-but-ai-studio-forces-prepay-top-up-no-available-credits/143815) |
| The $300 Cloud free trial gives Tier 1 | **Wrong**: "the Google Cloud Welcome credit or free trial credit can't be used towards the Gemini API or AI Studio" (since March 2026). | [billing](https://ai.google.dev/gemini-api/docs/billing) |
| `gemini-2.0-flash` still has 1M TPM on the free tier | **Wrong / obsolete**: deprecated, shutdown date June 1, 2026, replaced by `gemini-3.6-flash`. | [deprecations](https://ai.google.dev/gemini-api/docs/deprecations) |
| Cached tokens don't count toward TPM | **Contradicted by the docs**: "token limits include cached tokens". (The app's own observation, [agents.md](cli/agents.md), is that oversized requests pass only on a cache hit; the docs and that observation disagree, so don't plan on either.) | [explicit caching (legacy page)](https://ai.google.dev/gemini-api/docs/generate-content/caching) |
| Gemini CLI was replaced by the Antigravity CLI (`agy`) for personal accounts | **Confirmed**: "Gemini CLI was replaced by Antigravity CLI on June 18th, 2026" for unpaid and Google One users. | [geminicli.com tos-privacy](https://geminicli.com/docs/resources/tos-privacy/) |
| `agy` installs as `@google/agy` | **Wrong**: it installs via script (`irm https://antigravity.google/cli/install.ps1 \| iex` on Windows); no npm package. | [Antigravity getting started](https://antigravity.google/docs/getting-started?tab=cli) |
| AI Pro in Antigravity: 5-hour quota plus weekly limit, AI-credit overages | **Confirmed** qualitatively ("refreshed every five hours until the weekly limit is reached"). | [Antigravity plans](https://antigravity.google/docs/plans) |
| "~25M–40M tokens per 5-hour window", full 1M context, 60 RPM | **Unverifiable**: Google gives no numbers ("correlate with the amount of work done by the agent"). Old Gemini CLI quotas were per day (AI Pro 1,500 requests/day); no 60 RPM figure found. | [Antigravity plans](https://antigravity.google/docs/plans), [geminicli quota](https://geminicli.com/docs/resources/quota-and-pricing/) |
| Custom apps may not call the Code Assist / OAuth endpoint | **Confirmed, explicitly**: "Using third party software, tools, or services to access the Service (e.g. using OpenClaw with Antigravity OAuth) is a breach of this Agreement", grounds for suspending the Antigravity and Gemini CLI accounts. Same wording for Gemini CLI OAuth. | [Antigravity terms §6](https://antigravity.google/terms), [geminicli tos-privacy](https://geminicli.com/docs/resources/tos-privacy/) |
| "Gemini 4 Argon" | **Exists** (a real model on the pricing page; no free tier, implicit caching only). | [pricing](https://ai.google.dev/gemini-api/docs/pricing) |

## Prices used (gemini-3.8-flash, paid Standard tier, per 1M tokens)

From the [pricing page](https://ai.google.dev/gemini-api/docs/pricing). Introductory until
2026-12-31; **every figure doubles on 2027-01-01**.

| | until 2026-12-31 | from 2027-01-01 |
|---|---|---|
| Input (one price for text/image/video/audio, no >200k tier) | $0.75 | $1.50 |
| Output incl. thinking | $3.75 | $7.50 |
| Cached input | $0.075 | $0.15 |
| Cache storage per hour | $0.50 | $1.00 |
| Batch input / output | $0.375 / $1.875 | $0.75 / $3.75 |

## Cost per lecture

Assumed lecture: 4 video parts at ~380k input tokens + 3 refinement steps at ~380k = **~2.66M input
tokens**; output assumed ~30k per request including HIGH thinking = ~0.21M (an estimate: check
real output counts in a run's logs).

| Scenario | 2026 prices | 2027 prices |
|---|---|---|
| No caching (worst case) | 2.66M × 0.75 + 0.21M × 3.75 ≈ **$2.80** | ≈ $5.60 |
| Explicit cache of the ~300k Stage 1 prefix for the 4 parts (~1 h TTL), refinement uncached | 1.2M × 0.075 + 0.32M × 0.75 + 0.15 storage + 1.14M × 0.75 + 0.79 ≈ **$2.10** | ≈ $4.20 |
| Implicit cache hits on paid tier | somewhere between the two; automatic, but "no cost saving guarantee" | |

The $10/month AI Pro credit would cover roughly 3–5 lectures a month, if AI Studio accepts it
(see the forum thread). Output is about a third of the bill and caching does not touch it.

## Options

### A. Paid Tier 1 on the existing AI Studio project (recommended)

- **Changes:** billing: link a Cloud billing account to the AI Studio project, prepay $5 minimum,
  redeem the AI Pro $10/month credit onto that billing account, set a project spend cap in AI
  Studio. Account: the same Google account and project. **Code: none** for the basic switch; the
  same keys keep working, now on Tier 1.
- **Limits after:** TPM not published (look it up in AI Studio after the upgrade); $250/month tier
  cap; $10 per 10 minutes spend rate (a lecture is ~$3, fine); RPD not published. Rate limits are
  **per project, not per key**, so several keys in one project no longer multiply the quota;
  `docs/cli/parallel.md` assumptions change.
- **Caching:** explicit caching available. The app already has it, but only for Vertex:
  `src/Extraction/ExplicitContextCache.cs` (`Caches.CreateAsync`, TTL extend, state file) takes a
  `VertexAutoExtractionConfig`, and `AiStudioExtractionBackend.ContextCache` returns `null`
  (`src/Extraction/AiStudioExtractionBackend.cs:25`). `LatexRefinementSession.EnsureContextCacheAsync`
  is gated on `_config.UseVertex`. To use it on AI Studio: let `ExplicitContextCache` take the shared
  `IAutoExtractionConfig`/`BackendParameters` (it needs `UseContextCaching` and the TTL settings
  that AI Studio's config lacks), return an instance from `AiStudioExtractionBackend`, drop the
  `UseVertex` gate in refinement, and turn off `EnableImplicitPrefixCacheWarmup` (the warm-up
  requests then only cost money). `BuildGenerationRequestAsync` already sends `CachedContent`
  instead of the system instruction when a cache name is set. Saves ~$0.70 per lecture now, ~$1.40
  from 2027; worth doing but not needed to unblock.
- **Risk:** none on terms. Cost risk: the spend cap is "experimental" with ~10 minutes of billing
  lag. Free-tier data use for training stops.

### B. Vertex AI with the same credits

- **Changes:** a Cloud project with Vertex AI enabled, billing account with the AI Pro credit;
  the app's Vertex mode (`VertexAutoExtractionConfig.json`, `UseContextCaching: true`) already does
  explicit caching. Code: none, config and credentials only.
- **Limits:** Vertex quotas (not researched here); no prepay system, which is what a Cloud Billing
  support reply in the forum thread suggested as the workaround for credits AI Studio rejects.
- **Cost:** same per-token prices assumed (not verified for Vertex), with caching ≈ $2.10/lecture.
- **Risk:** none on terms. Use it as the fallback if option A's credits don't apply.

### C. Stay on the free tier

- **Changes:** none; keep relying on the implicit prefix cache, or shrink the Stage 1 prompt below
  ~250k minus the video share.
- **Limits:** 250k TPM per project and model, 20 requests per day; requests above 250k pass only on
  an unguaranteed cache hit. `gemini-2.0-flash` is no escape (shut down).
- **Cost:** $0, but parts fail unpredictably, and prompts are used for training.

### D. OAuth via Antigravity CLI / Code Assist endpoint (assessed only, not built or tested)

- **Changes:** would need the app to impersonate Google's CLI against an undocumented endpoint
  with user OAuth, or to drive `agy` as a subprocess.
- **Limits:** unknown; no published numbers, 5-hour and weekly pools shared with interactive use.
- **Cost:** included in AI Pro.
- **Risk: high and explicit.** Antigravity's terms call third-party access via its OAuth "a breach
  of this Agreement", with suspension of the Antigravity and Gemini CLI accounts. Scripting `agy`
  itself as a batch backend is not named in the terms, but is a grey zone, and the undocumented
  quota makes it unusable for planning. **Rejected.**

## Recommendation: option A

Next steps (the user does these; nothing below has been done):

1. In AI Studio, open the project the keys belong to → Billing → link a Cloud billing account,
   prepay $5.
2. At developers.google.com/program/my-benefits, apply the AI Pro $10/month credit to that same
   billing account (tick "Always use this billing account"). Check in Cloud Billing → Credits that
   it shows up; expect the forum's problem that AI Studio may draw only the prepay balance.
3. In AI Studio → Spend, set a project spend cap (e.g. $15/month).
4. Open aistudio.google.com/rate-limit and note the Tier 1 TPM and RPD for `gemini-3.8-flash`;
   add them to `docs/cli/agents.md` and fix the per-key assumptions in `docs/cli/parallel.md`.
5. Rerun the failed 03-14 part 3 alone with `--resume`, then compare Google's billed amount for it
   with the estimate above.
6. Then, as a separate change: port `ExplicitContextCache` to AI Studio as described under option A.
7. Before 2027-01-01, re-read the pricing page: costs double.
