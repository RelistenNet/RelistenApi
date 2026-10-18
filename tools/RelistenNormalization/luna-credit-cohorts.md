# Compare credit extractors on a frozen cohort

`luna-credit-cohort` compares local rules and Luna on 1–600 complete original fields. It creates extraction proposals, never accepted people or recording credits. Use it after independent labeling and adjudication. The [legacy command](luna-credit-fields.md) retains its original 100-field interface.

## Freeze the inputs

The operator supplies a `luna-credit-cohort-job-v1` plan and its externally retained SHA-256 hash. The plan binds four relative artifact paths and their hashes: labels, original fields, a cohort receipt and source scope. Typed JSON contracts are defined in [LunaCreditFieldEvaluationBatch.Cohort.cs](LunaCreditFieldEvaluationBatch.Cohort.cs).

The plan declares the field count, purpose, artist and recording-family partitions, monetary cap and total attempt limit. The maximum profile is 600 fields, $30 and 1,200 admitted attempts. A smaller job can use lower limits. Labels and original fields each have a 128 MiB file limit; other input artifacts each have a 1 MiB limit. Fields are never shortened. The extractor separately enforces its rendered-request bound.

Development requires development partitions. Tuning requires a validation partition and no test partition. Untouched evaluation requires a test partition. Nondevelopment labels must be adjudicated and name their adjudicator. These checks authenticate declarations; the operator must separately establish actual exposure history, family separation and label correctness.

Source scope binds each field to its source UUID, artist UUID, original text hash and partitions. Provider origins use retained provider-input or importer-execution evidence for Archive.org, Local or Phish.in. Unknown origin remains explicit. An artist's provider configuration does not establish the origin of a particular field.

The label resource provider distinguishes two original-text authorities. Use
[relisten_catalog_snapshot](luna-credit-fields.md) for retained catalog field text.
Cohorts may instead use the explicit namespace relisten_retained_provider_field
for a current provider field linked to the same catalog source UUID. That namespace
requires provider_origin to name Archive.org, Local or Phish.in, use
retained_provider_input, and retain both evidence and resource-key SHA-256 hashes.
Unknown origin or importer-execution evidence alone cannot authorize native field
text. The legacy 100-field mode remains catalog-snapshot only.

Both namespaces use the experiment field path /raw_record/row/FIELD. This is a
normalized one-field experiment envelope, not a claim about the native JSON shape.
The source receipt must preserve the native pointer, exact complete provider value
and retained response identity. When the current native value differs from the
catalog snapshot, keep the native value and its own hash; do not describe it as
catalog snapshot text.

## Run and recover

The current extractor is `luna-recording-credits-v3`. Structured taper and
transferrer fields supply their named role. Prose quotes must attribute the role
to the proposed actor; another person's role elsewhere in the text is insufficient.
Explicit named tape/DAT/CD conversion and CD extraction can supply Transferrer.
Encoding, mastering, uploading, receiving audio or supplying process information
alone cannot. Credited personal handles are retained; email addresses are excluded
from credited names. None of these proposals resolves a global person identity.

Quotes and names stay literal, including aliases, misspellings and HTML. A unique
quote can use null coordinates, which the validator derives from the original
field. Repeated quotes require an exact Unicode codepoint range. Wrong supplied
coordinates are rejected. One unsupported credit refuses the whole response;
valid siblings are not published as a partial cache result. Failed paid responses
retain their body, measured usage and classified failure, including validation
timeouts.

The rendered request includes the extractor version, so v3 uses separate cache
keys without a database migration. Existing v2 evidence remains unchanged. The
cohort audits local successes as well as failures; production fallback admission
has not changed and still needs routing evaluation.

Use the dedicated local database and cache migrations described in the legacy guide. The tool reads `OPENAI_API_KEY` from its process environment. Choose a new job UUID and output directory:

```sh
dotnet run --project tools/RelistenNormalization -- luna-credit-cohort \
  PLAN_JSON EXTERNALLY_PINNED_PLAN_SHA256 NEW_OUTPUT JOB_UUID
```

All input authentication completes before job creation or provider requests. Only the original one-field dictionary enters the model request, with null context. Labels and provenance do not enter the prompt.

Fields caught by the email-shaped contact check remain in the output with their complete original text and local extraction. They produce no rendered request, cache entry or HTTP call: Luna and replay are null, state is `not_requested_contact_scope`, and routing is `not_evaluated`. This check does not detect every form of contact information. Empty fields receive `no_text`. Report these cases separately from usable Luna predictions.

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
--cohort-luna-version luna-recording-credits-v3
```

`--cohort-luna-version` selects the expected extractor revision for the new cohort.
It accepts `luna-recording-credits-v2` or `luna-recording-credits-v3` and defaults to
v2 when omitted. Supply this flag only with all four cohort arguments. Each
successful retained request must name the selected revision in its system
instructions. Mixed revisions fail authentication before model fitting or output
creation. The scorer never chooses a revision from returned predictions.

The completed run manifest's `lunaExtractorVersion` must also match the selected
revision. Historical v2 manifests may omit this property; v3 manifests must declare
it. Missing or conflicting v3 declarations fail before scoring.

The report records `cohort.luna_extractor_version_expected` and
`cohort.luna_extractor_version_effective`. The effective revision is null when no
nonempty field has an authenticated successful Luna response. Contact exclusions
and deterministic empty-field results do not establish an effective model revision.

The extractor revision identifies the request and extraction contract. It is
separate from the API model name, retained assembly/source hashes and trained
local-model weights. Selecting v3 does not change the old 100-field comparison or
its v2 code receipt. The training reference stays v2 and retains its original
labels, inputs and source/binary authentication.

This mode fits the two trained extractors once on the old study's 84 complete, nonexcluded development fields, freezes the fit IDs and models, then scores the new cohort. It does not fit on the new answers. The direct field-overlap guard does not establish separation of whole identity or recording families; retain the selection ledger for that purpose.

A fresh successful Luna response requires an equal successful keyless cache replay and retained request/response evidence. Unavailable responses are not scored as Luna by using the embedded local fallback. Uncertain or incomplete labels retain diagnostics but do not become complete negative examples. Results qualify no automatic acceptance or fallback policy by themselves.
