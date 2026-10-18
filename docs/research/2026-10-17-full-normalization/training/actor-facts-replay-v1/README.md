# Actor-fact replay: first whole-field baseline

The original v3 public C# extractor processed all **543 frozen development fields with zero command errors**. The run retained complete original outcomes and validated their raw Unicode citations. This establishes a reproducible baseline; extraction coverage still needs work before automatic matching can rely on these facts.

`baseline.json` pins the original corpus, input export, actual results, loaded assemblies, comparison, source files and verification. Raw fields, contacts and full results remain under ignored `local-dev/normalization-data/`. The [tool guide](../../../../../tools/actor-field-replay/README.md) explains the command and comparison contracts.

The comparison covers 43 independently reviewed fields and 500 unreviewed drafts. Thirty-three fields have explicit review disagreements. Partial coverage, disputed expectations and unknown kinds remain visible; no labels were changed in response to parser output.

The baseline found 356 exact unique reference-anchor correspondences and 160 unaligned actual anchor spans. Those numbers are diagnostics, not accuracy: references include aliases, and a nested alias can be extracted without a separately emitted reference anchor. Actor-container spans also need source interpretation. The report does not repair alignment from equal names.

For attached contacts, 124 of 240 draft claims matched on an exact aligned actor. Twenty-six were absent on aligned actors; another 90 were blocked by anchor alignment. These categories remain split by partial and disputed source coverage. Twelve repeated alias claims matched. Relationship diagnostics retain 109 expected and 137 actual claims, with 35 exact fully anchored correspondences; 13 actual endpoint mappings remain unscored.

The extractor emitted **zero profile facts**. None of the 44 raw draft profile records uses a currently qualified provider/attachment combination: 23 refer to personal or project sites, 12 to YouTube, five to Facebook, two to Twitter and two to MySpace. Those records reduce to 40 distinct actor/literal/relationship expectations. This is a coverage gap, rather than evidence that an existing qualified-provider rule failed. The separate source audit must distinguish explicit profile attribution from unsigned promotion and show/resource links before any provider or attachment extension. A URL does not become an authoritative person identifier.

Verification passed: 12 exporter tests, 22 comparison tests, and 13 C# public-boundary tests, plus the tool build with zero C# warnings. Root ran C# verification and the actual extraction; the implementation agent ran Python verification and the actual comparison. A fresh source review found no remaining correctness or readability issues after fixes to output ownership, malformed metadata handling, source snapshot publication and uncertainty reporting.

The source corpus, independent first answers, raw runtime output and baseline comparison are frozen. Further extraction work should use separate runs and additive evidence, while preserving this baseline and the source-review disagreements.

## Grammar v4: suffix and angle-email handling

The [additive v4 receipt](grammar-v4.json) records a separate actual run of all 543
fields with zero command errors. Exactly 29 field outputs changed: 28 now keep `IV`
with the credited name before its parenthesized contact, and one retains a complete
angle-email literal previously removed as HTML. The other 514 output rows are
byte-identical. These changes cover 15 source records and nine artists within one
discovery family; repeated wording does not establish independent accuracy.

Exact reference-anchor correspondences increased from 356 to 384. Contact draft
comparisons now contain 153 matches, 25 absences on aligned actors and 62 claims
blocked by alignment, against the same 240 expectations. Twenty-eight of the new
matches come from corrected actor segmentation; the raw extractor emits only one
additional contact. Profiles remain zero and alias matches remain 12. Partial and
disputed comparisons retain separate buckets. Only one of the 29 changed fields
was in the original independent-review subset; adjudication is still pending.

The tool build and 97 focused raw/retained extraction, recording-processor and replay
tests pass. Fresh source review found no remaining correctness, readability or
version-compatibility issues. The raw grammar is now
`recording-credits-v4/recording-actor-evidence-v1`; the actor-fact format and ID
algorithm remain v1. The receipt pins the new source, loaded assemblies, outputs,
comparison and verification logs. The original baseline, corpus labels and first
review answers remain unchanged. These development diagnostics do not authorize
identity acceptance or policy activation.
