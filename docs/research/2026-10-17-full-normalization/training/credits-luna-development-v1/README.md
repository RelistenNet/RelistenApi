# Matched-field Luna development comparison

Compare the current local credit parser and Luna on the same complete individual fields from the [100-field development bundle](../credits-development-v1/README.md). The comparison includes apparent parser successes and failures. It measures extraction of named tapers and transferrers, not global person identity. The [operator guide](../../../../../tools/RelistenNormalization/luna-credit-fields.md) defines the command, fixed $5 job cap, offline scoring and recovery.

## First measured comparison

Prompt `luna-recording-credits-v1` produced valid results for 94 fields, five invalid-response outcomes and one deterministic empty-field outcome. Three repeated fields reused cached responses: 96 admitted requests produced 91 successes and five failures. All 96 attempts have measured usage. The configured ledger accounts for $0.0061393 from 31,353 input tokens and 6,008 output tokens. This is usage accounting at the retained rates, not invoice reconciliation.

The [comparison receipt](comparison-v1.json) preserves metrics, executable/output hashes, failed-call coverage and the ignored artifact paths. A complete key-free replay reused all 94 valid field results with no additional attempts or accounted spend. The five uncached failures stayed explicit. Original raw fields, request bodies and full prediction files remain outside Git.

The fully resolved draft denominator contains 85 fields and 70 taper/transferrer assertions:

- The current local parser recovers 60 assertions, adds seven extra predictions and misses ten. It exactly matches 75 fields.
- Luna has valid output on 83 resolved fields containing 66 assertions. On that same subset, local parsing recovers 58 assertions with six extra predictions and eight misses; Luna recovers 48 with one extra prediction and 18 misses. Luna corrects five fields but introduces errors in 15 previously correct fields.
- Current fallback routing, with explicitly labeled local output retained on API failure, recovers 61 of all 70 assertions with seven extra predictions and nine misses. It exactly matches 76 fields. This is a complete pipeline result, not a Luna-only score.

Luna's main regression is omission of supported names from structured taper fields. It also shortens one name by dropping an attached parenthetical alias. The v2 development prompt clarifies field-supplied roles and complete literal names. The measured rerun below preserves this v1 evidence. Neither this small draft set nor agreement between extractors establishes a deployable fallback policy or global identity accuracy.

The [local-parser error review](local-credit-grammar-followup.md) also identifies one disputed taper label: a sentence naming a transfer actor does not unequivocally identify who recorded the source. The supported transfer credit remains separate from that disputed role. The frozen v1 metrics still include the original draft judgment; resolve or mask the disputed role in a documented label revision or sensitivity report before using these comparisons for acceptance decisions. Do not add a grammar rule merely to fit it.

The [sensitivity report](label-sensitivity-v1.json) conservatively excludes that entire field, including its supported transfer credit, without changing labels or predictions. This leaves 84 resolved fields and 68 assertions. On the 82 fields where both methods have output, Luna recovers 47 of 64 assertions while local parsing recovers 57. The conclusion about v1's omissions remains the same. The report retains the original run, disputed-field and scorer hashes; it uses the scorer's explicit exclusion input and makes no API requests. This check does not adjudicate the disputed role.

## Clarified-prompt comparison

Prompt `luna-recording-credits-v2` uses the same 100 original fields and the same compiled local v1 parser. All 100 local predictions and the entire saved selection match the first run. The [v2 receipt](comparison-v2.json) records the loaded executable hashes and the code receipt captured before subsequent local-parser edits.

The rerun has 91 valid field results, eight invalid-response outcomes and one deterministic empty-field outcome. Its 96 admitted attempts add $0.0076179 of accounted usage. Across both prompt versions, the dedicated job has 192 attempts and $0.0137572 of accounted usage under its $5 cap. A key-free replay reuses all 91 valid field results without additional attempts or spend. Failed responses remain unavailable; their embedded local fallback is not a model prediction. All eight failures have parsed usage, but unsuccessful response bodies were not retained. The shared `invalid_response` reason cannot distinguish envelope/schema failures from citation or name validation, so their exact causes cannot be reconstructed from this run.

Against the unchanged draft labels:

- On the 79 resolved fields with available Luna output, including the deterministic empty field, local parsing recovers 57 of 64 priority-role assertions with six extra predictions and seven misses. Luna recovers 60 with three extra predictions and four misses. It corrects three fields and introduces no newly incorrect field on this subset.
- On all 85 resolved fields, using explicitly labeled local output when Luna fails, the combined result recovers 63 of 70 assertions with four extra predictions and seven misses. It exactly matches 78 fields; local parsing alone matches 75.
- Current selective routing with local output on failure recovers 62 of 70 assertions with seven extra predictions and eight misses. This differs from sending every nonempty field to Luna; the experiment does not authorize that routing change.

The two prompt versions share available output on 77 resolved fields with 60 assertions. On that common subset, v1 recovers 43 assertions with one extra prediction and 17 misses; v2 recovers 56 with three extra predictions and four misses. V2 corrects 16 fields but adds three parenthetical name-boundary disagreements against the drafts. This comparison is separate from v2 versus local parsing above.

The prompt revision reduces the measured omissions, but validation failures rise from five to eight fields. The [v2 sensitivity report](label-sensitivity-v2.json) excludes the same disputed Josh Myers field as the v1 report. Neither prompt comparison resolves that label, proves global person identity or supplies independent release accuracy. Broader labels, failure analysis and measured fallback routing remain required.

## Independent source review

[source-review.jsonl](source-review.jsonl) records a second Codex reading of 12 original fields before inspecting their draft answers or model results for this review. The reviewer read the whole field, retained exact Unicode spans and recorded uncertain actors separately. These fields include structured taper/transferrer names, equipment-only lineage, HTML description credits, two roles on one name and an uncertain organization or affiliation.

Selection used a fixed SHA-256 rank within five field types and excluded the 20 fields in the earlier blind review. The [review receipt](source-review-receipt.json) records the original field-file hash, selection hash, reviewer-output hash and selection rule. The exact selected input copy remains in the ignored data directory named by that receipt's source inputs. The original field bundle retains every source field and its identifier.

The [draft comparison](source-review-comparison.jsonl) was written after freezing the independent answers. All 12 name/role and unknown-span sets agree. The review contains 12 credit-role assertions, including ten taper/transferrer assertions, and two fields with uncertain actors. An agreement between Codex readings is not independent human adjudication or a release accuracy estimate. It does not resolve whether a handle refers to the same person across artists.

Keep the original draft labels, earlier review, this review and future model predictions separate. Do not rewrite labels to make them agree with an extractor. Record disagreements and adjudication before using them to authorize acceptance policy. The 15 uncertain fields in the full bundle must not supply scored negative labels.
