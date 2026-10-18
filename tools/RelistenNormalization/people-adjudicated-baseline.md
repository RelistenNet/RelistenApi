# Source-only people comparisons from the prepared review package

`people-adjudicated-baseline` exports actual `ComparePeople` features from the prepared development package. It reads no target answers, reasons or fitted model. The package name describes its origin; the exporter supplies no publication policy, calibrated probability, canonical identity or release-evaluation authority.

```text
RelistenNormalization people-adjudicated-baseline <prepared-package-directory> <new-output-directory> <frozen-manifest-sha256>
```

The externally supplied manifest hash is checked first. The reader then opens only these fixed artifacts:

- `source/records.jsonl`
- `source/endpoints.jsonl`
- `source/questions.jsonl`
- `source/grouping.jsonl`

The manifest's separate targets and upstream input paths are metadata only. They are never opened, checked for availability or rechecked. A missing or changed target file cannot change source comparisons. The prepared manifest is the caller's trust anchor for prior reservation/eligibility checks; the reader also verifies development partitions, source/artist ownership, field hashes, exact Unicode spans, endpoint references and complete grouping coverage.

## Projection and ambiguity

Complete original fields are parsed once through `RecordingCreditExtractor`. Named selectors reuse `PeopleSourceContextProjection.Project`; contact and URL selectors reuse `ProjectReference`. No caller supplies the actor name for a reference selector. Every original selector and returned actor variant remains in the endpoint diagnostics. An exact `actor_name` selector is the source name assertion even when newer review endpoints omit `mention_kind`; original null metadata is retained in diagnostics. `source_expression` still requires its declared compound/reference kind, and contact/URL selectors still require null mention metadata.

An endpoint has a usable actor when all its selectors independently match the same `(evidenceId, field, ReferenceUuid)`. A single named selector whose context is unavailable can still supply its original name comparison, as in the previous context experiment. Missing reference selectors, unowned hints, different source-local actors or unsupported expressions do not choose a convenient first actor. Equal names, contact keys, URLs or naked local UUIDs never collapse variants.

An attached URL with an unknown namespace can still identify the expressed source actor; it supplies no invented profile key. A generic unattributed reference remains unowned even when there is only one nearby hypothesis. Comparison query bounds and all source uncertainties remain visible. These statements concern source attribution, not verified identity or account ownership.

Each declared identity edge appears in `baseline.jsonl`. Usable endpoints produce the actual runtime comparison and shared five-feature vector. Unsupported edges have `features=null`, `comparison=null`, `projectionSupported=false` and an explicit reason. They remain in the pool and must never become negative training examples merely because projection failed.

## Grouping and provenance

`groups.jsonl` preserves every original source grouping task, including actor-kind-only tasks. `source-bridges.jsonl` adds source-only leakage-control bridges for endpoints/tasks sharing the same fully scoped actor reference. Explicit alias endpoints share their original actor reference. Unowned source hypotheses can conservatively bridge their tasks; a bridge is not an identity label. The trainer must combine these groups before fitting or filtering tasks.

The final manifest pins the four output artifacts, only the five consumed input files (manifest plus four source artifacts), executing API/tool assemblies, and parser/projection versions. `endpoints.jsonl` records every selector, exact evidence, status and actor variant. The five-feature order is shared with the existing exporters. Existing modes and output contracts are unchanged.

Input files are limited to 64 MiB each and 128 MiB together, with 100,000 rows per artifact and 100 selectors per endpoint. Projection refuses more than 1,000 variants per endpoint or 100,000 variants overall. Each output artifact is streamed with a 128 MiB bound. Overflow and parser failure reject the complete export; no truncated or apparently complete artifact is published.

Only `manifest.json`, renamed last, marks completion. Output is never overwritten. An interruption may leave partial files; retain them and choose a new directory to retry. Rechecks authenticate only the original manifest/source bytes and do not substitute newer input. There is no network, database, scheduler or accepted-data write.

## Root verification

Run `TestPeopleAdjudicatedBaselineBatch` alongside the source-context/reference fixtures and old baseline contracts. The new fixture checks target independence, alias/scoped grouping, shared raw UUIDs across sources, complete task coverage, joint roles, multi-selector ambiguity, source authentication and recovery into a new directory.

For the actual prepared package, independently check 229 source tasks, 318 endpoints, 227 records and 219 declared identity edges against the pinned inputs. These are observed package counts, not hardcoded requirements. Preserve all 93 original groups and combine newly exported bridges before future fitting. Target joining, target-conflict handling and model evaluation belong to the separate trainer; this exporter cannot activate a policy.
