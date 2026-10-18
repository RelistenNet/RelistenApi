# Replay source actor facts through the local C# extractor

Use this tool to inspect how the current `RecordingCreditExtractor` handles the frozen **543 original fields** in the actor-fact development bundle. It runs the public extractor on each whole field and retains its complete output. It starts no API host, database, network client or paid model.

The three stages have separate responsibilities:

1. **Python export** validates the frozen source bundle and writes only original fields plus provenance. It does not pass annotation answers to C#.
2. **C# extraction** authenticates that export against frozen field metadata, extracts all 543 fields, and preserves actual outcomes and loaded assembly hashes.
3. **Python comparison** authenticates the retained results and compares them with development drafts. It separates unresolved alignment, partial coverage and independent-review disputes.

Keep exports, results and diagnostic reports under ignored `local-dev/normalization-data/`. Source quotes and actual extractor outputs can contain public contact details. Commit compact receipts with hashes and counts separately when documenting a run.

## Run the stages

The frozen bundle and its ignored originals live in the primary checkout. A tool worktree can read them by absolute path. Replace the placeholders below with those paths and choose fresh output directories for every invocation.

```sh
python3 -B tools/actor-field-replay/export_fields.py \
  --bundle <primary-root>/docs/research/2026-10-17-full-normalization/training/actor-facts-development-v1 \
  --repo-root <primary-root> \
  --output <worktree-root>/local-dev/normalization-data/actor-field-input

dotnet run --project tools/RelistenNormalization -- actor-fields \
  <worktree-root>/local-dev/normalization-data/actor-field-input/fields.jsonl \
  <worktree-root>/local-dev/normalization-data/actor-field-input/receipt.json \
  <primary-root>/docs/research/2026-10-17-full-normalization/training/actor-facts-development-v1 \
  <worktree-root>/local-dev/normalization-data/actor-field-results

python3 -B tools/actor-field-replay/compare_fields.py \
  --bundle <primary-root>/docs/research/2026-10-17-full-normalization/training/actor-facts-development-v1 \
  --repo-root <primary-root> \
  --replay <worktree-root>/local-dev/normalization-data/actor-field-results \
  --output <worktree-root>/local-dev/normalization-data/actor-field-comparison
```

No environment key or database connection is needed. The C# command reads only the bundle manifest and field index. The Python stages use the frozen validator to authenticate eligible source copies and labels; they do not decode excluded raw-population rows.

## Input and extraction contracts

`fields.jsonl` has one physical JSON record per whole original field. Its exact fields are `task_id`, `evidence_id`, `field`, `field_path`, `field_sha256` and `field_text`. Unicode paragraph separators remain text inside a JSONL record. The export receipt pins the field file, frozen manifest and field index, plus source/reservation/annotation hashes. The export rechecks the source read set before publishing its receipt.

The C# runner rejects missing, duplicate, unknown or changed fields before creating an output directory. It checks the exact task set, text hash, original code-point length and both development partitions against frozen field-index metadata. These checks bind the input to the selected source snapshot; they do not reinterpret source meaning or read annotation answers. Each bounded input file must be at most 8 MiB.

`results.jsonl` retains one `ActorFieldReplayResult` per processed field, including the original task identity/hash, state, complete `RecordingCreditExtraction`, and any error. An extracted outcome can legitimately contain no credits or only unresolved claims. Every emitted citation must refer to the input field, use exact raw Unicode code-point coordinates and reproduce its original substring, including HTML. A citation failure preserves the returned extraction in an error outcome for inspection.

`status.json` records expected, processed and error counts; extractor version; hashes of input, export receipt and frozen metadata; the loaded API and tool assembly identity/module IDs and file hashes; and the result-file hash. `manifest.json` is published only after all 543 fields finish with zero errors. The CLI returns zero only for that state.

A per-field extraction failure is recorded and remaining fields continue; the run returns `completed_partial`, without a completion manifest. Cancellation is observed between fields so an already computed outcome is written first. Cancellation or an I/O/command failure preserves flushed partial results and attempts to write a cancelled/failed status, then returns nonzero. An exclusive owner file prevents a competing invocation from publishing another run's audit. Existing directories are never reused. Recovery means rerunning the authenticated input into a fresh directory; prior partial files remain unchanged.

## Read the comparison

`report.json` pins source, label, review, result, script and recorded assembly provenance. It reports independently reviewed and unreviewed field counts; every field diagnostic has an `independent_reviewed` flag. An unreviewed draft is not presented as independently confirmed. `fields.jsonl` supplies per-field diagnostic detail. The comparison keeps all 543 fields in its denominator, including unknown actors, failures and unprocessed fields. A partial replay produces a diagnostic report and exits nonzero; it does not become a completed evaluation.

An actor reference aligns only when its exact original raw span is unique on both sides. Equal names do not repair an unmatched span. Ambiguous or unaligned references have their own diagnostics, rather than being forced into a match or counted as false positives against incomplete labels. Repeated roles on one source reference are combined without counting another actor.

For aligned actors, the comparison checks role sets, alias wording, attached contact literals, attached profile literals and relationships, and context kinds. Profile relationships distinguish personal attribution from a credited project. It preserves the following buckets:

- Complete, undisputed development drafts.
- Partial, undisputed development drafts.
- Independently disputed facts, coverage or actor attachment.

A missed labeled positive remains visible. Extra facts in partial or disputed domains are unscored. Extras in a complete, undisputed draft are provisional review candidates, not adjudicated errors. Context-coverage disputes also mask role negatives. Unknown kind supplies no person/nonperson negatives; this bundle has no kind accuracy denominator. The comparator retains independent profile-relationship disagreements even when the literal URL agrees.

Relationship diagnostics retain every expected claim and deduplicated actual claim. Exact local reference anchors can establish a diagnostic correspondence. An alias endpoint without its own raw anchor remains explicitly unscored; wording is never used to invent the endpoint. Relationship counts are not an accuracy score.

The comparator reuses the exporter's full frozen read set, including the exact validator bytes executed, nested blind packet and retained review inputs. It checks that set again after writing diagnostics and just before atomically publishing the complete report. A source mutation leaves diagnostics for inspection and no final report.

These are correlated development annotations, with review disagreements still pending adjudication. Counts do not establish global identity, recording ownership, launch accuracy or a production acceptance policy. Comparison does not train a matcher, change labels or update the catalog. Recorded assembly hashes identify the binaries reported by the C# process; retain the run's code/build receipt when documenting that provenance.

## Verification

```sh
python3 -B -O tools/actor-field-replay/test_export_fields.py
python3 -B -O tools/actor-field-replay/test_compare_fields.py
dotnet test RelistenApiTests/RelistenApiTests.csproj --filter FullyQualifiedName~TestActorFieldReplayBatch
```

Exporter tests cover original-field binding, changed inputs, physical Unicode JSONL, source changes before receipt publication, existing output preservation and interrupted publication. C# tests exercise the real public extractor, whole-field authentication, Unicode HTML/contact attachment, malformed metadata, output ownership and cancellation/fresh-directory recovery. Comparison tests check exact-span alignment, actor-local attachment, ambiguous references, duplicate role mentions, uncertainty masks and profile relationships.
