# Actor-fact replay: first whole-field baseline

The current public C# extractor processed all **543 frozen development fields with zero command errors**. The run retained complete original outcomes and validated their raw Unicode citations. This establishes a reproducible baseline; extraction coverage still needs work before automatic matching can rely on these facts.

`baseline.json` pins the original corpus, input export, actual results, loaded assemblies, comparison, source files and verification. Raw fields, contacts and full results remain under ignored `local-dev/normalization-data/`. The [tool guide](../../../../../tools/actor-field-replay/README.md) explains the command and comparison contracts.

The comparison covers 43 independently reviewed fields and 500 unreviewed drafts. Thirty-three fields have explicit review disagreements. Partial coverage, disputed expectations and unknown kinds remain visible; no labels were changed in response to parser output.

The baseline found 356 exact unique reference-anchor correspondences and 160 unaligned actual anchor spans. Those numbers are diagnostics, not accuracy: references include aliases, and a nested alias can be extracted without a separately emitted reference anchor. Actor-container spans also need source interpretation. The report does not repair alignment from equal names.

For attached contacts, 124 of 240 draft claims matched on an exact aligned actor. Twenty-six were absent on aligned actors; another 90 were blocked by anchor alignment. These categories remain split by partial and disputed source coverage. Twelve repeated alias claims matched. Relationship diagnostics retain 109 expected and 137 actual claims, with 35 exact fully anchored correspondences; 13 actual endpoint mappings remain unscored.

The extractor emitted **zero profile facts**. None of the 44 raw draft profile records uses a currently qualified provider/attachment combination: 23 refer to personal or project sites, 12 to YouTube, five to Facebook, two to Twitter and two to MySpace. Those records reduce to 40 distinct actor/literal/relationship expectations. This is a coverage gap, rather than evidence that an existing qualified-provider rule failed. The separate source audit must distinguish explicit profile attribution from unsigned promotion and show/resource links before any provider or attachment extension. A URL does not become an authoritative person identifier.

Verification passed: 12 exporter tests, 22 comparison tests, and 13 C# public-boundary tests, plus the tool build with zero C# warnings. Root ran C# verification and the actual extraction; the implementation agent ran Python verification and the actual comparison. A fresh source review found no remaining correctness or readability issues after fixes to output ownership, malformed metadata handling, source snapshot publication and uncertainty reporting.

The source corpus, independent first answers, raw runtime output and baseline comparison are frozen. Further extraction work should use separate runs and additive evidence, while preserving this baseline and the source-review disagreements.
