# Export real composition and repertoire comparisons

`composition-evaluation` runs a frozen, cited music package through the actual
`IdentityCandidateMatcher`, `MusicBrainzCandidateQueryBuilder` and optional provider
decoder. It compares each claim with every member of its frozen candidate index,
including candidates that retrieval missed. The export needs no database, HTTP,
model client or accepted publication state.

```sh
dotnet run --project tools/RelistenNormalization -- composition-evaluation \
  /absolute/package /absolute/new-output EXTERNALLY_PINNED_MANIFEST_SHA256
```

The public entrypoint is `CompositionEvaluationBatch.RunAsync(packageDirectory,
newOutputDirectory, expectedManifestSha256, cancellationToken)`. Exit zero and the
final `manifest.json` establish a complete diagnostic export. Failed or cancelled
runs retain any partial outputs without that marker. Preserve them and choose a
new output directory for retry. Existing outputs are never reused.

## Prepare the four input files

`CompositionEvaluationContracts.cs` defines the closed JSON records. Use camel-case
property names and snake-case string enums. Every field of the new package records
is required, including nullable fields written as `null`. Existing domain records
keep their documented optional defaults. Unknown properties, labels, duplicate
properties, integer enums, duplicate IDs, changed hashes and broken citations are
rejected. Do not put expected identities, labels, rationales or predictions in the
package. Freeze labels separately before running comparisons.

The package manifest is:

```json
{
  "version": "composition-evaluation-package-v1",
  "files": {
    "documents.jsonl": {"bytes": 123, "sha256": "ACTUAL_SHA256", "rows": 1},
    "records.jsonl": {"bytes": 123, "sha256": "ACTUAL_SHA256", "rows": 2},
    "universes.jsonl": {"bytes": 123, "sha256": "ACTUAL_SHA256", "rows": 1},
    "tasks.jsonl": {"bytes": 123, "sha256": "ACTUAL_SHA256", "rows": 1}
  }
}
```

Replace every illustrative size, hash and row count with its actual value. Pin the
manifest's own SHA-256 outside the package. The command reads only those fixed file
names and refuses symbolic-link files.

`documents.jsonl` retains original representations:

- `input`: the existing `NormalizationInputRevision` shape: `uuid`, `resource`
  (`provider`, `resourceType`, `providerKey`), `payload` as base64 of exact original
  bytes, `sha256` of those bytes, `mediaType`, `captureVersion`, and `capturedAt`.
- `decode`: true calls the actual provider decoder for the retained profile.
  False keeps official/reference documents available for citation checks without
  claiming they implement a provider decoder contract.
- `origin`: an explicit original source locator and whether the UUID names a real
  retained revision or a package-local offline coordinate. An offline UUID does
  not establish a database revision, fetch receipt or publication authority.
  `capturedAt` records retention metadata; it is not an event date or a verified
  historical knowledge cutoff.

No source text is rewritten, stripped or re-encoded before retention. Text/HTML
uses the empty JSON Pointer; JSON uses its original pointer. Optional spans count
Unicode code points with an exclusive end, as `NormalizedEvidenceReference` does.
Each literal title, external key and query term must reproduce its citation.

`records.jsonl` holds cited claim and candidate facts:

- `id`, `domain` (`compositions` or `repertoire`), `catalogUuid`, `artistUuid`.
- `titles`: `ObservedValue<string>` objects with `value` and `evidence`.
- `externalIdentities`: objects with the existing `MatchingExternalIdentity` as
  `identity` and its exact `evidence`. Identifier namespace and entity authority
  remain separate from the literal presence of an ID in a source.
- `authorUuids`, `completeAuthorship`, `compositionUuid`, `contextEvidence`.

Repertoire records require artist scope and cannot carry global authorship.
Composition records have null artist/composition ownership fields. Leave
`authorUuids` empty unless independently available accepted canonical-person facts
support them. Never substitute external artist MBIDs or adjudicated identity IDs.
Keep `completeAuthorship` false when a source supplies only known partial credits;
a parsed author list does not establish completeness. `contextEvidence` preserves
original scope and supports preparer-supplied facts, including empty/unknown titles.
The exporter verifies literal support; independent review still determines whether
an asserted identifier or writer relationship identifies the intended entity.

`universes.jsonl` contains `id`, `domain`, `recordIds`, `complete`, and `issues`.
Each list enumerates an independently frozen catalog/index scope. `complete` means
that enumeration is complete for that scope. It never means all MusicBrainz works
or all possible identities are known. Preserve the index before inspecting labels;
the returned search pool cannot define which known targets are eligible for recall.
A universe may be empty. Record order stays fixed in outcomes.

`tasks.jsonl` contains `id`, `domain`, `claimId`, `universeId`, `retrieval`, and
nullable `searchSeed`. The cited seed uses the existing `MusicBrainzSearchSeed`
contract and actual query builder. Performer context constrains recording search;
work-author context requires explicitly cited writer/composer/lyricist wording.

`retrieval` contains `state` (`available`, `unavailable`, `not_run`), `candidateIds`,
`unrepresentedIdentities`, `issues`, and `evidence`. Candidate IDs are unique
members of the frozen universe; unrepresented identities are original cited
provider hits without a candidate-record projection. An unavailable later page
can retain earlier results and its failure issue. A run that did not happen has
no results or result evidence. Preserve query bounds, failed pages, unhydrated
hits and unknown coverage as issues rather than silently dropping them.

## Read and score the outputs

`outcomes.jsonl` has one row per task. It retains the exact retrieval observation,
universe completeness/issues, actual query variants, and one public matcher result
for every frozen candidate. `retrieved` marks only actual retrieved pool members.
No acceptance policy is supplied, so supported pair rules remain diagnostic.
`decoded.jsonl` retains actual provider observations and their original input UUIDs.
`package-manifest.json` is a byte-exact copy of the input manifest. The final report
fingerprints input/output files and the actual executing matcher/exporter assemblies.

Join a separately frozen answer artifact by task ID after export. Keep these outcomes
separate when scoring: an existing-work match, a supported work new to the local
catalog, conflicting evidence, insufficient evidence, unavailable retrieval and an
omitted known target. A known target in the frozen index that retrieval misses is a
recall miss. A target outside that index is not a recall success. Independent
support determines whether it is new to the local catalog or remains unresolved;
a zero-result search cannot make that decision. Returned unrepresented identities
remain visible so hydration/projection omissions do not become matcher errors.

Report retrieval separately from conditional matching, then complete pipeline
outcomes. Group duplicate recordings and shared work/alias families for independent
accuracy denominators. The exporter supplies neither labels nor a production
acceptance threshold. Publication and reviewed corrections retain their existing
ownership and authority contracts.

## Bounds and focused verification

The fixed profile permits 128 MiB of aggregate package bytes, 8 MiB per raw
document, 4,096 documents/tasks, 16,384 records, 1,024 universes and 100,000 actual
pair comparisons. JSONL rows are at most 12 MiB; an outcome row is at most 32 MiB,
and each output file at most 128 MiB. Exceeding a bound refuses completion. Split
an experiment into separately pinned packages when needed; never truncate an index
and describe it as complete.

Root can run the synthetic public contracts without provider data or a database:

```sh
dotnet test RelistenApiTests/RelistenApiTests.csproj \
  --filter FullyQualifiedName~TestCompositionEvaluationBatch
```

Tests compare saved outcomes with the actual public matchers, preserve missing and
unavailable retrieval, check cross-artist repertoire scope, run a real pure decoder
and query builder, and reject label injection, changed inputs, broken Unicode spans
and output reuse. Test fixtures establish these contracts; the real retained
comparison report establishes matching quality.
