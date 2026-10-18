# Compare credit extractors on a frozen cohort

`luna-credit-cohort` compares local rules and Luna on 1–600 complete original fields. It creates extraction proposals, never accepted people or recording credits. Use it after independent labeling and adjudication. The [legacy command](luna-credit-fields.md) retains its original 100-field interface.

## Freeze the inputs

The operator supplies a `luna-credit-cohort-job-v1` plan and its externally retained SHA-256 hash. The plan binds four relative artifact paths and their hashes: labels, original fields, a cohort receipt and source scope. Typed JSON contracts are defined in [LunaCreditFieldEvaluationBatch.Cohort.cs](LunaCreditFieldEvaluationBatch.Cohort.cs).

The plan declares the field count, purpose, artist and recording-family partitions, monetary cap and total attempt limit. The maximum profile is 600 fields, $30 and 1,200 admitted attempts. A smaller job can use lower limits. Labels and original fields each have a 128 MiB file limit; other input artifacts each have a 1 MiB limit. Fields are never shortened. The extractor separately enforces its rendered-request bound.

Development requires development partitions. Tuning requires a validation partition and no test partition. Untouched evaluation requires a test partition. Nondevelopment labels must be adjudicated and name their adjudicator. These checks authenticate declarations; the operator must separately establish actual exposure history, family separation and label correctness.

Source scope binds each field to its source UUID, artist UUID, original text hash and partitions. Provider origins use retained provider-input or importer-execution evidence for Archive.org, Local or Phish.in. Unknown origin remains explicit. An artist's provider configuration does not establish the origin of a particular field.

## Run and recover

Use the dedicated local database and cache migrations described in the legacy guide. The tool reads `OPENAI_API_KEY` from its process environment. Choose a new job UUID and output directory:

```sh
dotnet run --project tools/RelistenNormalization -- luna-credit-cohort \
  PLAN_JSON EXTERNALLY_PINNED_PLAN_SHA256 NEW_OUTPUT JOB_UUID
```

All input authentication completes before job creation or provider requests. Only the original one-field dictionary enters the model request, with null context. Labels and provenance do not enter the prompt.

Contact-bearing fields remain in the output with their complete original text and local extraction. They produce no rendered request, cache entry or HTTP call: Luna and replay are null, state is `not_requested_contact_scope`, and routing is `not_evaluated`. Empty fields receive `no_text`. Report these cases separately from usable Luna predictions.

The output retains the authenticated selection, per-field results, complete bounded attempt receipts and a final manifest. Coverage distinguishes selected fields, eligible fields, fields linked to this job's admitted attempts, usable predictions, contact exclusions and empty fields. A cache hit paid by another job is available without adding an attempt to this job.

Cancellation retains the result prefix and exports spend receipts without claiming completion. Recover with the **same plan hash and job UUID, in a new output directory**. The database freezes monetary and attempt limits; it does not bind the job UUID to a plan hash. The operator must preserve that binding. Never reset reservations or reuse a completed experiment's job for unrelated work.

Keep originals and response receipts in ignored private storage. Audit exports retain every admitted attempt or refuse overflow; they do not silently truncate. Failed-response diagnostics can reach 1 MiB per attempt, so allow disk and memory capacity for the complete ledger as well as the selected fields.

## Score without training on the new answers

The standalone offline scorer authenticates both the plan and completed comparison:

```sh
python3 -B tools/RelistenNormalization/luna_credit_field_report.py \
  COHORT_LABELS COHORT_RUN NEW_SCORE_OUTPUT \
  --cohort-plan PLAN_JSON --cohort-plan-sha256 PLAN_SHA256 \
  --comparison-sha256 COMPLETED_MANIFEST_SHA256
```

The matched rules/perceptron/logistic/Luna comparison uses the existing arguments to `matched_credit_experiment.py` for the frozen old study, plus:

```text
--cohort-plan PLAN_JSON
--cohort-plan-sha256 PLAN_SHA256
--cohort-comparison NEW_COHORT_OUTPUT_DIRECTORY
--cohort-comparison-sha256 COMPLETED_MANIFEST_SHA256
```

This mode fits the two trained extractors once on the old study's 84 complete, nonexcluded development fields, freezes the fit IDs and models, then scores the new cohort. It does not fit on the new answers. The direct field-overlap guard does not establish separation of whole identity or recording families; retain the selection ledger for that purpose.

A fresh successful Luna response requires an equal successful keyless cache replay and retained request/response evidence. Unavailable responses are not scored as Luna by using the embedded local fallback. Uncertain or incomplete labels retain diagnostics but do not become complete negative examples. Results qualify no automatic acceptance or fallback policy by themselves.
