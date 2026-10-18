# Compare local and Luna credit extraction on the same fields

`luna-credit-fields` runs the existing credit extractors on all 100 frozen development fields. Each request contains one original field and no source-wide context. The comparison includes apparent local successes and failures, records current fallback routing, and proves that successful Luna responses can be replayed without an API key. It creates extraction proposals only; it writes no accepted people or recording credits.

The [development labels](../../docs/research/2026-10-17-full-normalization/training/credits-development-v1/README.md) cover 78 sources and artists, all associated with Archive.org. They include structured names, compound credits, HTML, equipment and uncertain prose. This cohort does not establish coverage of other providers. The separate [source review](../../docs/research/2026-10-17-full-normalization/training/credits-luna-development-v1/README.md) preserves another reading of original fields before model outputs were available.

## Prepare the bounded run

Use a dedicated local `relisten_normalization_*` PostgreSQL database with the extraction-cache schema from migration 22 applied. The command refuses remote database hosts and does not start an API host, imports or schedules. `RELISTEN_NORMALIZATION_DATABASE_URL` must point to that database. `OPENAI_API_KEY` must already be in the process environment; this tool does not read `.env.local` itself.

From the repository root, use a **new job UUID** and a **new output directory**:

```sh
export RELISTEN_NORMALIZATION_DATABASE_URL=postgresql://relisten:local_dev_password@127.0.0.1:15432/relisten_normalization_20261017_luna

dotnet run --project tools/RelistenNormalization -- luna-credit-fields \
  docs/research/2026-10-17-full-normalization/training/credits-development-v1/labels.jsonl \
  local-dev/normalization-data/2026-10-17-cross-artist/training-development-credits-v1/fields.jsonl \
  docs/research/2026-10-17-full-normalization/training/credits-development-v1/receipt.json \
  /path/to/new-comparison-output <new-job-uuid>
```

Preflight captures and checks all three input files before creating a job. It verifies receipt hashes, field hashes and Unicode lengths, development partitions, complete-field scope, and exact source/field references. Each file is limited to 1 MiB. Fields containing email-shaped text are rejected. Label answers never enter the extraction request.

The frozen selection has 97 distinct `(field name, original text)` tuples: 96 contain text and one is empty. Three repeated fields can reuse another field's cached response. The empty field produces `no_text` without a request. Current local extraction is recomputed from the same one-field dictionary so both methods receive identical input. The earlier retained source-wide baseline was not rerun for this experiment.

## Budget, model and cache

The batch creates a job capped at **$5**, using the existing cache's reservation and usage accounting. The command uses `LunaCreditExtractionOptions` defaults: `gpt-6-luna`, low reasoning, 2,048 output tokens, a 90-second request timeout and at most two admitted attempts per exact request. The current configured accounting rates are $0.10 per million input tokens and $0.50 per million output tokens. The manifest retains those options; ledger cost is not an invoice reconciliation or a guarantee of provider pricing.

Requests use `store:false` and contain only the selected original field, with `context:null` and `auditLocalSuccess:true`. The cache key covers the rendered request, model, prompt, schema and settings. Each successful response is followed by two keyless calls: an audit replay and an `auditLocalSuccess:false` call that exposes current fallback routing. Neither can start paid work. A model alias does not pin model weights; request hashes and assembly hashes identify this experiment's inputs and code.

## Read the outputs and recover

`selection.json` owns the new output directory and retains all selected text, source references and hashes. Keep outputs under ignored `local-dev/normalization-data/`; do not commit the raw fields or receipts.

- `results.jsonl` is flushed after each field. Each row contains current local extraction, the Luna outcome, keyless cache replay and current routing.
- `receipts.json` records the job, bounded request bodies, validated response JSON, attempt states, usage, request IDs and accounted cost. It contains no authorization header or API key.
- `manifest.json` is written last. `completed` means all field outcomes and cache checks succeeded; `completed_partial` retains terminal failures for analysis. The CLI returns nonzero for a partial run.

Only `luna.state == "extracted"` is a Luna prediction. On errors or budget exhaustion, the existing extractor returns local fallback data inside its result; scoring that data as Luna would hide failures. `paidAttempts` counts all admitted attempts for the dedicated job, including failed attempts and previous recovery runs. It does not count newly billed requests in just this output directory. Read per-field cache flags and attempt receipts separately.

Cancellation or an exception can leave a result prefix without a manifest. Receipt export uses an uncancelled token so spend remains visible. If cancellation occurs before an extractor returns its cache key, the receipt includes that job's attempt and reservation but may omit the final rendered request body. The durable cache remains authoritative. Failed or uncertain attempts can retain their reservations; do not reset them to make a retry fit the budget.

Investigate the prefix, then retry with the **same job UUID and a new output directory**. Completed responses are reused. Existing output files are never overwritten, and the job cap cannot be changed on recovery. A full keyless replay uses the same command in a new directory with `OPENAI_API_KEY` removed from its environment. Missing cached results remain explicit failures.

## Score extraction separately from identity

Run the offline scorer after a terminal comparison:

```sh
python3 tools/RelistenNormalization/luna_credit_field_report.py \
  docs/research/2026-10-17-full-normalization/training/credits-development-v1/labels.jsonl \
  /path/to/comparison-output /path/to/new-score-output
```

The primary draft denominator is 85 fully resolved fields containing 70 taper/transferrer name-role assertions. Other roles are outside Luna's two-role schema. The remaining 15 uncertain fields retain diagnostics and known mentions; they do not supply negative labels or full precision denominators. All 100 fields remain in failure and routing coverage.

Compare exact names and roles, local-to-Luna corrections and new errors, current routing, and cache cost. These are development comparisons against draft labels. Separate Codex readings and model agreement do not replace independent adjudication, held-out evaluation or cross-artist person-identity checks. This run activates no fallback or automatic matching policy.
