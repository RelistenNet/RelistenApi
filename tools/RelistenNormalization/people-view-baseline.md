# Independent people source views

`people-view-baseline` exports every independently attributed source view from an authenticated development package. It keeps the existing single-projection comparator beside the new outputs. It supplies observations for a development experiment; it does not resolve people, create identities, read adjudication targets, or activate a policy.

```text
RelistenNormalization people-view-baseline <prepared-package-directory> <new-output-directory> <frozen-manifest-sha256>
```

Pin the package manifest hash externally. The reader authenticates only its four source artifacts: records, endpoints, questions, and grouping. Each selected field and Unicode span must agree with its original source, including artist and development eligibility. Target paths and other upstream receipts described by the package manifest remain inert metadata. Missing or changed target files cannot change this export.

Source records retain their text authority. `resource.provider` may be
`relisten_catalog_snapshot` or `relisten_retained_provider_field`. A native record
requires `provider_origin` with provider `archive.org`, `local` or `phish.in`, kind
`retained_provider_input`, and lowercase 64-character SHA-256 values in
`evidence_sha256` and `resource_key_sha256`. Keep this object unchanged from the
authenticated preparation evidence. Native values may differ from prior catalog
text; selectors and hashes must cite the complete native field, without replacing
it with the catalog wording.

The operator must retain the native response, pointer or ordered composition and
resource evidence behind those hashes. The hashes alone do not authenticate an
upstream provider. This reader validates the declared provenance profile and
binds the exact source package to the export; it does not fetch provider data,
read targets or grant accepted-data authority.

## What one view means

The existing source-context and source-reference projectors parse each complete original field. A selector contributes a view when it selects exactly one independently attributed actor whose query projection is complete. An exact name selector can also contribute its existing name-only fallback when it has no ambiguous actor context. Missing context does not invent a credit role, contact owner, or actor UUID.

A source actor key includes its evidence ID, field, and extractor reference UUID. The same raw UUID in another source or field identifies another view. Multiple selectors for one same-field actor, including an explicit alias or joint taper/transferrer roles, share one view and retain every selector index. A name-only key instead includes the exact source selector span and original field hash, with a null reference UUID. All six key fields are always serialized; inapplicable fields remain null.

Different fields retain independent records. No record receives names, contacts, or profiles from another view. All selectors, variants, attachment statuses, facts, and exact evidence remain in `endpoint-views.jsonl`, including ambiguous or unattributed alternatives that cannot supply a view. A single nearby actor hypothesis with an unowned reference is still unattributed.

An exact name or alias selector can also select a role-free assertion from
`SourceActorAliasExtractor`. These assertions retain the original roster or
recording-byline context and establish only the source's explicit alias statement.
The exporter first preserves any unique recording-credit match. If no credit
matches, it can select one alias assertion by its exact primary-name or alias
citation. It does not attach an inner guessed span, borrow a repeated name, or
repair an ambiguous credit match with another assertion.

Version 2 views and variants retain this typed evidence in `sourceAssertion`;
their `facts` value stays null. Recording-credit views retain `facts` and a null
`sourceAssertion`. Name-only fallbacks retain neither. The alias projection adds
only the asserted names and the source artist's catalog context. It supplies no
recording role, attendance, actor kind, contact ownership or canonical identity.
The source-context version pins the alias extractor as well as this attachment
behavior. Historical version 1 exports keep their original four-field view shape;
the trainer supports both version profiles without rewriting old artifacts.

`coverage=complete` means every declared selector supplied exactly one eligible view. Partial coverage retains its clear views. Ambiguous and unattributed selectors retain their separate statuses. `matchRecordsAgree` compares entire matching records structurally; it establishes neither co-reference nor global identity. Completeness does not require the different records to agree.

## Files and completion

The output contains:

- `comparator/`: the unchanged adjudicated v1 comparator, its endpoint diagnostics, grouping, source-reference bridges, and manifest.
- `endpoint-views.jsonl`: every endpoint with full selector/variant diagnostics and independently supported views.
- `view-bags.jsonl`: one row for every declared identity edge, including edges with zero eligible views.
- `view-pairs.jsonl`: actual `ComparePeople` output and the existing five numeric features for every eligible left/right view combination.
- `manifest.json`: the completion marker, source and runtime pins, artifact hashes/byte lengths/row counts, versions, bounds, and distinct task/endpoint/edge/view/leaf/bag counts.

The top manifest is the only completion authority for this mode. A completed `comparator/manifest.json` alone acknowledges no completed view export. Writers use new files, retain partial output on interruption, and publish the top manifest last. Restart into a new directory with the same externally pinned package; existing output is never overwritten.

The comparator phase reuses the existing public batch and records its source projection version. The view projection reparses the same bounded, authenticated source package, and rechecks it before and after the comparator and output phases. This costs a second parse but keeps the comparator protocol in one place and avoids a second parser or a shared workflow abstraction. New projection versions can change the resulting observations; the export records the versions needed to interpret them.

The machine-readable exchange contract is [people-view-baseline-contract.json](people-view-baseline-contract.json). `edgeId` is a JSON-encoded two-string array containing task ID and candidate endpoint ID. Parse that JSON string; do not split it by punctuation. A leaf key combines edge ID and both full typed source-view keys. The nested comparator preserves every original grouping task and source-reference bridge, including unsupported endpoints and actor-kind questions.

## Bounds and recovery

Existing package limits remain in force: 64 MiB per source artifact, 128 MiB total input, 100,000 rows per source artifact, and 100 selectors per endpoint. Projection permits at most 1,000 variants per endpoint and 100,000 aggregate variants or views. Before expanding any Cartesian comparison, the batch checks the aggregate maximum of 1,000,000 leaf pairs. Each output artifact is streamed with a 128 MiB byte limit. No limit truncates an endpoint or turns a partial bag into a complete one.

Invalid source authentication fails before a completed export. Overflow, cancellation, source mutation, or output failure leaves no top completion manifest. A surviving nested comparator may be inspected under its own v1 contract, but the view trainer must refuse it as a completed view export. Retry chooses a new directory, reuses the original source bytes, and reruns local extraction and projection. It makes no provider request, retained capture, or database write.

## Training boundary

The separate trainer authenticates the top manifest, nested comparator and all new artifacts before joining targets. All tasks, source bridges, and full unordered view-pair overlaps form grouping units first. Sharing only one actor is insufficient to deduplicate two physical pairs.

Each final overlap unit has total weight one. Initial learned-model fitting admits only complete bags whose supported five-feature vectors are identical. Partial bags, differing vectors, and unsupported bags remain diagnostic with all their original edge and leaf rows. No identity target is assigned to an individual view pair. Every actual rule comparison remains available; disagreement in rule results cannot be replaced by a convenient representative view. These constraints belong to the separate trainer and must be verified there; the exporter performs no fitting.

## Verification

The focused public batch fixture covers independent 2×3 field views, different whole records, exact name fallback, partial coverage, genuine within-selector URL ambiguity, same-field aliases and joint roles, duplicate raw reference UUIDs across sources, target-file independence, unchanged v1 comparator bytes, count overflow, and new-directory recovery. It compares emitted leaves with the actual public matcher. No timing or mock-call-count assertion establishes correctness.

Build and run the fixture before using the new mode. A successful development export establishes source projection and artifact provenance; it does not establish identity accuracy or launch authority.
