# Comparing normalization assertions

Every current case specifies selected claims about its listed inputs. The labels do not describe a complete parse of each field. This document defines comparison semantics for an evaluator built against the [corpus format](format.md) and [domain contracts](../../../design/2026-10-17-normalization-contracts.md). It does not implement a scorer. All current labels remain `development_labeled`, including cases assigned to the evaluation split.

## Compare the requested decision

An evaluator first adapts its result into the case's domain, task, status and assertion fields. Record the adapter version with results. Compare `expected.status` exactly: `supported`, `different` and `unresolved` are distinct. `unresolved` requires preserving uncertainty about the requested identity, order or relationship. It does not reject independently supported extraction from that input. A failure to process the input is an error, not a correct unresolved decision.

Compare every explicitly expected assertion. An omitted expected field is unscored. Additional output does not fail merely because the label omits it; retain that output separately for adjudication. Additional assertions that contradict an expected claim do fail. For example, an asserted resolved person contradicts `person_identity: unresolved`, even if the credited name matches. Correct-looking extractions are not automatically certified, and unscored outputs cannot enter a precision denominator as correct predictions.

Match expected reason codes exactly when they are identifiers. Some venue cases use explanatory prose as `reason`; require the decision and cited locality distinction, rather than exact prose reproduction. `rationale` explains the label and is never a generated-text target.

## Fields and comparison units

- **People and work credits:** `credits` is an unordered collection of selected name/role tuples. Match each expected tuple once; list order has no meaning. Roles are exact tokens. A person can have several roles. `name` and `alias` retain extracted wording; `person_identity`, `relationship` and `composer_lyricist_split` are separate claims. A `writer` assertion must not become a composer/lyricist split without evidence. A supplied credit reports what the input says, not independent proof of the person's actions.
- **Title contents:** `segments` is ordered when `sequence: title_order` is expected. Otherwise compare selected segment kind/title pairs without asserting sequence. `ordered_mentions` preserves order and repeated mentions. `title`, `removed_prefix`, `earlier_medley_contains`, `coverage` and `basis` are separate assertions. `continuation_pair` contains the two one-based source entry numbers, not track UUIDs or time positions. `sequence: unresolved` forbids claiming established passage order from that evidence.
- **Lineage:** `ordered_chain`, `transfer_chain` and `format_conversion` preserve expected order. Compare `processing` and array-valued `capture_inputs` as unordered selected terms unless a separate expected order claim exists. Scalar `capture_inputs` and `recorder` preserve the named assertion. `asserted_parent_resource` identifies a provider-reported predecessor; it does not resolve a canonical parent. `mix_assertion`, `unknown_steps`, `byte_identity`, `capture_chain`, `actor_identities` and `cross_recording_identity` each retain their stated meaning.
- **Venue and identity decisions:** compare the requested `relationship` or unresolved/different status. Equal names alone do not substitute for a supported identity decision. Positive venue labels remain developer hypotheses requiring independent verification before they authorize an automatic policy.

For ordered selected terms, require an order-preserving subsequence, retaining repeated terms. Extra intermediate terms are unscored, and this comparison establishes no adjacency or timing beyond explicit expected claims. Unordered matching also consumes each matching output once; duplicate output cannot satisfy two expected occurrences.

Compare extracted names, titles and equipment text as Unicode NFC with surrounding whitespace removed. Preserve case, internal punctuation, spelling and word order. Canonical aliases, spelling correction, abbreviation expansion and fuzzy identity matching are separate decisions; an evaluator must retain the extracted wording to compare it. Basis citations remain exact against original evidence, without this comparison normalization.

`segue_marker` means the title contains segue syntax. It does not establish the runtime `SongOccurrence.transition_after=segue`; canonical transition remains unknown until supported. `partial_marker` likewise preserves an ellipsis marker without certifying the missing audio. Null `start_ms`/`end_ms` mean no supported boundary in milliseconds. These inputs supply no measured timestamps. Null, unknown, false and absent are distinct; none proves a negative fact about uncaptured upstream data.

## Report results honestly

Report status agreement and selected-claim recovery with case counts by domain, provider and split. Record processing errors, contradictions and unscored extras separately. This challenge set does not establish exhaustive extraction recall, accepted-match precision, candidate recall or catalog-wide coverage. Those measurements require independently reviewed complete outputs or candidate/identity labels, explicit denominators and representative held-out data. Integrity validation verifies references and receipts; it does not measure normalization accuracy.
