# Matched-field Luna development comparison

Compare the current local credit parser and Luna on the same complete individual fields from the [100-field development bundle](../credits-development-v1/README.md). The comparison includes apparent parser successes and failures. It measures extraction of named tapers and transferrers, not global person identity. The [operator guide](../../../../../tools/RelistenNormalization/luna-credit-fields.md) defines the command, fixed $5 job cap, offline scoring and recovery.

## First measured comparison

Prompt `luna-recording-credits-v1` produced valid results for 94 fields, five invalid-response outcomes and one deterministic empty-field outcome. Three repeated fields reused cached responses: 96 admitted requests produced 91 successes and five failures. All 96 attempts have measured usage. The configured ledger accounts for $0.0061393 from 31,353 input tokens and 6,008 output tokens. This is usage accounting at the retained rates, not invoice reconciliation.

The [comparison receipt](comparison-v1.json) preserves metrics, executable/output hashes, failed-call coverage and the ignored artifact paths. A complete key-free replay reused all 94 valid field results with no additional attempts or accounted spend. The five uncached failures stayed explicit. Original raw fields, request bodies and full prediction files remain outside Git.

The fully resolved draft denominator contains 85 fields and 70 taper/transferrer assertions:

- The current local parser recovers 60 assertions, adds seven extra predictions and misses ten. It exactly matches 75 fields.
- Luna has valid output on 83 resolved fields containing 66 assertions. On that same subset, local parsing recovers 58 assertions with six extra predictions and eight misses; Luna recovers 48 with one extra prediction and 18 misses. Luna corrects five fields but introduces errors in 15 previously correct fields.
- Current fallback routing, with explicitly labeled local output retained on API failure, recovers 61 of all 70 assertions with seven extra predictions and nine misses. It exactly matches 76 fields. This is a complete pipeline result, not a Luna-only score.

Luna's main regression is omission of supported names from structured taper fields. It also shortens one name by dropping an attached parenthetical alias. The v2 development prompt clarifies field-supplied roles and complete literal names; its improvement still needs a measured rerun. Keep the v1 evidence unchanged. Neither this small draft set nor agreement between extractors establishes a deployable fallback policy or global identity accuracy.

The [local-parser error review](local-credit-grammar-followup.md) also identifies one disputed taper label: a sentence naming a transfer actor does not unequivocally identify who recorded the source. The supported transfer credit remains separate from that disputed role. The frozen v1 metrics still include the original draft judgment; resolve or mask the disputed role in a documented label revision or sensitivity report before using these comparisons for acceptance decisions. Do not add a grammar rule merely to fit it.

The [sensitivity report](label-sensitivity-v1.json) conservatively excludes that entire field, including its supported transfer credit, without changing labels or predictions. This leaves 84 resolved fields and 68 assertions. On the 82 fields where both methods have output, Luna recovers 47 of 64 assertions while local parsing recovers 57. The conclusion about v1's omissions remains the same. The report retains the original run, disputed-field and scorer hashes; it uses the scorer's explicit exclusion input and makes no API requests. This check does not adjudicate the disputed role.

## Independent source review

[source-review.jsonl](source-review.jsonl) records a second Codex reading of 12 original fields before inspecting their draft answers or model results for this review. The reviewer read the whole field, retained exact Unicode spans and recorded uncertain actors separately. These fields include structured taper/transferrer names, equipment-only lineage, HTML description credits, two roles on one name and an uncertain organization or affiliation.

Selection used a fixed SHA-256 rank within five field types and excluded the 20 fields in the earlier blind review. The [review receipt](source-review-receipt.json) records the original field-file hash, selection hash, reviewer-output hash and selection rule. The exact selected input copy remains in the ignored data directory named by that receipt's source inputs. The original field bundle retains every source field and its identifier.

The [draft comparison](source-review-comparison.jsonl) was written after freezing the independent answers. All 12 name/role and unknown-span sets agree. The review contains 12 credit-role assertions, including ten taper/transferrer assertions, and two fields with uncertain actors. An agreement between Codex readings is not independent human adjudication or a release accuracy estimate. It does not resolve whether a handle refers to the same person across artists.

Keep the original draft labels, earlier review, this review and future model predictions separate. Do not rewrite labels to make them agree with an extractor. Record disagreements and adjudication before using them to authorize acceptance policy. The 15 uncertain fields in the full bundle must not supply scored negative labels.
