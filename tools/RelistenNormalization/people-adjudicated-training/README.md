# Add the adjudicated people pairs without changing the original labels

This preparation adapter combines the original 168 development questions with
the separately reviewed 55 identity pairs and six distinctness controls. It
creates source selectors and a separate target table, then stops before matcher
feature export. The 55 questions select contact or URL tokens. The source-reference
projection contract supports those tokens; export of the combined package is the
next implementation step.

The package is for matcher development. The reviews are agent development
judgments. They are neither untouched release labels nor permission to create
canonical people, merge identities, publish credits or activate a policy.

## Inputs and trust boundary

The script pins and executes the existing original-bundle validator and review
validator from their captured source bytes. The original validator remains the
authority for source reservations, original candidate pools and original
citations. The review validator remains the authority for the two packet
formats, complete first reviews, adjudication row links and exact citations.
This adapter does not repeat those source-selection rules.

The original 168-question bundle, both frozen packets and all review files stay
unchanged. The adapter verifies each current and retained input named by the
two completed review receipts, reruns their validation and requires the same
summary. Large annotation and snapshot files are hashed as bytes. Only the
original validator decodes its already-selected source rows.

Every decoded input has a 32 MiB limit; JSONL input has a 10,000-row limit.
Large provenance-only files are streamed for hashing. Inputs and outputs are
rechecked before the final manifest is published. A failed run may leave partial
output files, but cannot publish a completion manifest. Use a new output
directory for every run.

## Package contract: `people-adjudicated-training-inputs:1`

All JSONL rows are UTF-8 JSON objects, separated by physical LF bytes. Spans use
Unicode code points, cover the exact original quotation and name a canonical
`/raw_record/row/<field>` path. Field hashes cover complete UTF-8 field bytes.

| File | Reader and responsibility |
| --- | --- |
| `source/records.jsonl` | The C# projector reads complete original source records. Repeated evidence IDs must have identical original content. No new source sampling occurs. |
| `source/endpoints.jsonl` | The projector reads exact selector coordinates, original artist/family coordinates and an endpoint ID. Original mention IDs survive. New IDs hash the version, evidence ID and sorted selectors; answers do not affect them. |
| `source/questions.jsonl` | The feature exporter reads all declared focus/candidate edges and grouping keys. Original whole candidate pools survive. New pairs have `candidate_scope=reviewed_declared_pairs`; they do not assert exhaustive catalog candidate coverage. |
| `source/grouping.jsonl` | The trainer starts with components over every task, including actor-kind tasks, unknown answers and unsupported expressions. It must add any later projector-derived bridges before choosing folds. |
| `targets/decisions.jsonl` | Only the training/evaluation join reads targets. Each declared identity edge appears once. Original draft and new agent-adjudicated provenance remain distinct. Unknown labels are JSON null. |
| `manifest.json` | The operator authenticates source, target, executable and review-receipt provenance. Its explicit pending-projection status must survive into downstream evidence. |

The source feature exporter must use only the four `source/` files. It must not
read `targets/decisions.jsonl`, review reasons or adjudication citations to pick
an actor. The top manifest contains hashes and input paths for the operator;
those paths are not an instruction for the projector to open target files.

Original expressions retain their `mention_kind` and a `source_expression`
selector. They remain in grouping even when the projector cannot compare them.
The six controls have exact `actor_name` selectors. The 55-pair packet supplies
`source_contact_reference` or `source_url_reference` selectors, retaining its
literal `email`, `obfuscated_email` or `url` kind. A URL token does not establish
profile ownership.

For each reference selector, the C# feature exporter must parse the complete
original field and join the selector to original fact evidence. It must return
zero, one or multiple source-local actor references, with attachment and
uncertainty. Lookup-key equality or a nearby name alone cannot establish
ownership. The feature exporter may compare a uniquely attributed source actor;
it must retain missing, unattributed and ambiguous results with reasons. It must
not use an adjudicated identity to choose a name. The five runtime feature
values still come from actual C# `ComparePeople` behavior.

## Grouping and targets

The adapter sends all tasks to the existing component builder before any fit
filter. Shared sources, original recording/duplicate-family groups, literal
source-name hypotheses and new review-hypothesis/control keys connect tasks.
It preserves the original split keys. It creates no random partitions and no
new release test set. Projector-derived source-reference or alias bridges remain
pending, so these components are not a final identity-disjoint split.

An extracted actor reference UUID is local to its source. It can repeat across
unrelated records with identical text coordinates. Every later actor-reference
bridge and projected endpoint key must include the original evidence ID,
field/source location and reference UUID. A raw UUID match across sources must
not connect components. Cross-source connections need actual source-family,
hypothesis, explicit-alias or duplicate evidence.

New adjudication rows join by exact question ID to that question's declared
candidate. Their original physical row SHA, adjudication-file SHA and validation
receipt SHA accompany each target. No evidence text or reason enters an
endpoint. Unknown judgments never become negatives. A new reviewed judgment
does not overwrite an older draft label, including an older unknown.

After projection, the trainer must identify repeated or reversed projected
actor pairs. It must report duplicate fit units and contradictory resolved
targets before fitting. Conflicting targets need explicit treatment; they must
not silently overwrite each other or count as independent examples. This
adapter records source coordinates and target provenance so that join remains
auditable. Pair deduplication and fit weighting are not decided here.

The expected real input inventory is 229 tasks: the original 168 plus 61 new
pairs, including all 79 actor-kind bridge tasks. There are 219 declared identity
edges before projection. These counts describe rows, not independent people or
candidate completeness across the catalog. Of the six controls, only two are
cross-recording negatives; the other negative examples distinguish actors
co-credited within one source. The actual preparation must verify counts from
the frozen inputs rather than take this paragraph as evidence.

## Root runbook

Run the synthetic contracts before preparing a new package:

```sh
RELISTEN_REPOSITORY="$PWD" python3 -m unittest discover \
  -s tools/RelistenNormalization/people-adjudicated-training \
  -p 'test_prepare_people_identity_training.py' -v
```

Create a private specification with the following exact contract. Paths are
relative to `--root`; the specification itself must be hash-pinned at invocation.

```json
{
  "version": "people-adjudicated-training-inputs:1",
  "review_packets": [
    {
      "packet": "local-dev/normalization-data/2026-10-17-cross-artist/people-identity-adjudication-v1",
      "validation_receipt": "local-dev/normalization-data/2026-10-17-cross-artist/people-adjudication-validation-v2/receipt.json",
      "validation_receipt_sha256": "7b02090338ccf0ade07d99c5fec22fed4028bb3340a1211a9245caba5d1e9e3d"
    },
    {
      "packet": "local-dev/normalization-data/2026-10-17-cross-artist/people-identity-distinctness-controls-v1",
      "validation_receipt": "local-dev/normalization-data/2026-10-17-cross-artist/people-controls-adjudication-validation-v1/receipt.json",
      "validation_receipt_sha256": "3aa3ed49d22e8fd845d693f06aa1b3178fd6a426b68ec31c7e6d19834474b432"
    }
  ]
}
```

Then invoke the preparation with a new ignored output directory:

```sh
python3 tools/RelistenNormalization/people-adjudicated-training/prepare_people_identity_training.py \
  --root "$PWD" --spec "$PRIVATE_SPEC" --spec-sha256 "$SPEC_SHA256" \
  --output "$NEW_PRIVATE_OUTPUT"
```

The script prints counts and a manifest hash only. Retain its exact executed
source, specification, stdout/stderr and output manifest. Check target class
and projection counts separately when the C# selector/export adapter is ready.
Do not train on the new targets until that source-only projection and duplicate
handling are implemented and independently reviewed.

The inspected original named-mention context cohort has no attached contact or
profile facts. The 61 new questions are therefore needed to exercise these
features, in addition to supplying more identity judgments. Preparation alone
does not establish that their reference selectors will resolve to actors.

The synthetic tests cover exact Unicode citations, field tampering, source-ID
conflicts, target-only answers, whole declared edge coverage, unknown handling,
actor-kind grouping bridges, failed publication and cached-bytecode bypass.
Actual source preparation and real projection remain root-owned verification.

## Retained development run

All 15 synthetic contracts pass with optimized Python. The actual preparation
authenticates the original bundle and both completed review receipts, producing
229 tasks, 318 source endpoints, 227 complete source records and 219 declared
identity edges. Targets contain 115 same-entity, 26 different-entity and 78 unknown
judgments. These counts include the original drafts and are not independent
identity counts. The 93 components remain explicitly incomplete until source
projection adds its grouping bridges.

The ignored directory
`local-dev/normalization-data/2026-10-17-cross-artist/training-development-people-adjudicated-v1/`
retains the private specification and `prepared-v1/manifest.json`. Source-reference
projection, pair deduplication and conflicting targets must be resolved before
these inputs enter model fitting.
