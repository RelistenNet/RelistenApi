# Source-attached people pair comparison

`people-source-context-baseline` exports actual `ComparePeople` results after attaching facts from the original recording fields. It is a development experiment. It supplies no canonical person identities, publication policy, threshold or creation authority. Keep the existing name-only baseline and its immutable runtime for comparison.

```sh
RelistenNormalization people-source-context-baseline <repository-root> <bundle-directory> <new-output-directory> <frozen-manifest-sha256>
```

The input is the existing frozen development people bundle. The command authenticates its manifest, reservations, eligible selection, source bytes, exact Unicode citations and complete declared candidate pools before extracting text. Nonselected mixed-source records remain opaque bytes. Label answers, rationales, proposed features and actor-kind adjudications never become matcher inputs.

For each cited field, `RecordingCreditExtractor` reads the complete original value once. A mention receives context only when its source ID, field, JSON pointer, raw quote and Unicode span match the extracted actor anchor or the extractor's exact alias citation. Alias attachment also requires the explicit source-local `ExplicitAlias` relationship. A name repeated elsewhere, another actor in the same field, or a nearby contact does not establish that join. Joint recording roles can share one source-local reference; distinct references remain distinct.

`PeopleActorProjection` uses the same semantic projection for field-local and retained-provider credit facts. Source aliases become ordinary comparison names. Attached emails and supported profiles become nonunique hints; they never become qualified canonical IDs or prove account ownership. Actor kind remains unknown unless the source explicitly asserts it. The experiment does not apply pending actor-kind labels.

A missing or ambiguous context join preserves the original name comparison. Unsupported compound/reference expressions remain visible and retain their existing `projectionSupported=false` status. Shared/unattributed hints and ambiguous alias boundaries stay in the diagnostics. `queryComplete` concerns supported query terms and limits; source coverage remains nonexhaustive, and an empty hint list is not negative evidence.

## Outputs and recovery

`baseline.jsonl` retains the existing pair-row schema and five-feature order. It includes every declared rival, including unknown and unsupported expressions. `context-diagnostics.jsonl` has one row per authenticated mention, including mentions used only by actor-kind tasks. It records the original field hash, source pointer, raw token span, join route, source-local reference, facts and unresolved issues. These diagnostics explain context availability; they contain no label targets. The exact exchange fields are listed in `people-source-context-contract.json`.

The new manifest pins both outputs, all original inputs, executing tool/API assemblies and extractor/projection versions. Only `manifest.json` marks completion. A failure or cancellation can leave partial files; choose a new directory to rerun. Existing output is never replaced. Cancellation is checked between field extractions and pair exports; parser regex timeouts remain failures, never empty complete results.

## Observed development limitations

The six previously missed alias pairs all have explicit alias wording in the original selected development fields. Four occur in a taper field or an explicit recording-credit clause. Two occur in musician rosters. The current recording-credit grammar does not turn arbitrary roster lines into taper/transferrer claims, so those two may remain name-only. No parser was executed during source authoring; actual coverage must be measured from the new export.

The extractor may retain surrounding quote delimiters in a parenthetical alias citation. An annotation that cites only the inner token then fails the exact join and stays name-only. The experiment does not guess a smaller source span. Ordinary parenthetical nicknames, co-mentions and unmarked paired names remain distinct from explicit alias assertions. Contact/profile-reference selection for the separate newer review cohort requires its own authenticated adapter; this command accepts the original named-mention bundle.

## Verification

Root owns compilation, tests and actual exports. Run the new `TestPeopleSourceContextProjection` fixture plus `TestPeoplePairBaselineBatch`, existing `TestPeopleActorProjection` and retained actor-evidence tests. The new batch cases compare both modes on the same source, retain the unknown rival, reject forbidden/malformed sources before extraction, and show that changing labels cannot change comparisons. The projector cases cover exact aliases, co-mentions, Unicode/HTML, contact attachment, repeated names, uncertainty and unsupported expressions.

For the actual development run, retain new executing assemblies before export, use a new output directory, and compare unchanged task/mention/edge inventory with the old baseline. Retrain rules/logistic/boosted-tree diagnostics only after the trainer authenticates the new manifest and complete context artifact. Keep the same grouped folds and unknown pools. Any score improvement remains development evidence: there are no cross-recording different-person edges, and the existing true cross-recording positives cover one correlated identity component.
