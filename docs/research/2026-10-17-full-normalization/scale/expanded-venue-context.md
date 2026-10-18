# Prepare date context for the verified development recordings

The [original grouping replay](original-group-replay.md) recovered 29 recordings
across 21 artists whose original development partitions and catalog tuples match
the retained venue-association discovery. Two recordings were already in the
first strict allowlist; the completed capture adds date context for the other 27. The 23 held-out, nine
context-only and 61 absent standalone sources remain excluded.

`scripts/prepare_expanded_venue_context.py` authenticates that completed replay
and prepares a new query for the 29 recordings and the same 67 venues. It makes
no database or provider requests and assigns no new partitions. Its inputs are
metadata artifacts; original annotation and snapshot files are authenticated by
byte hashes without decoding their payloads.

## Prepare a new packet

Run from the repository root with a new ignored output directory:

```sh
python3 -B docs/research/2026-10-17-full-normalization/scale/scripts/prepare_expanded_venue_context.py \
  --repo-root "$PWD" \
  --replay local-dev/normalization-data/2026-10-17-cross-artist/training-development-venues-v1/original-group-replay-v1 \
  --preparation local-dev/normalization-data/2026-10-17-cross-artist/training-development-venues-v1/context-preparation-v2 \
  --discovery local-dev/normalization-data/2026-10-17-cross-artist/training-development-venues-v1/association-discovery-capture-v1 \
  --output local-dev/normalization-data/2026-10-17-cross-artist/training-development-venues-v1/expanded-context-preparation-v1
```

The [completed receipt](evidence/expanded-venue-context-v1.json) authenticates the
preparation and two separate read-only replica captures. All 29 selected
recordings supplied date context, covering 21 of the 67 selected venue records.
The other 46 venues still have no date reference in this packet. All 67 venue
metadata records match the frozen baseline. There are 29 source venue references
and 27 show venue references; these remain separate observations.

The capture ran at 04:14 UTC on October 18, 2026. A second capture at 04:15 UTC
supplied an intentionally wrong expected show UUID for the same 29 recordings.
All 29 returned `discovery_show_uuid_changed` with all three date fields null.
Their source, artist and numeric show-assignment checks still passed, so the
probe isolates the UUID check. Probe output is for contract verification only;
it establishes no real catalog drift and must not enter training.

The 13 synthetic tests cover selection, tuple drift, reservations, provenance,
bounds and failed publication. They execute no SQL. The separate replica probes
verify observable success and UUID-drift behavior. Neither capture changes
catalog data, labels or matching policies.

## What must match

The tracked [replay receipt](evidence/original-group-replay-v1.json) pins the
private completed receipt. The preparer authenticates all its recorded inputs
and outputs, including executable sources, the original preparation and discovery.
The original 67-venue allowlist is copied byte for byte.

The preparer reads exactly 86,655 resource metadata rows and 122 classifications.
It checks byte, physical-row and line-size bounds before JSON decoding. A verified
status alone is insufficient: each selected recording must match the complete
original metadata, both development partitions, recomputed reservation closure,
and exact discovery tuple and associations. Missing or duplicate sources, changed
IDs, unsupported flags and an altered 29-recording/21-artist count fail explicitly.

`source-allowlist.json` retains original source and artist IDs/UUIDs, numeric show
ID, literal recording family, full original group and partitions. It records
`original_show_uuid: null`: the original standalone source rows do not supply that
UUID. `current_discovery_show_uuid` is the separate UUID observed by the later
discovery capture. It must be present and canonical.

## Query and outputs

`strict-context.sql` uses the new
[expanded schema12 template](sql/venue-context-expanded.sql). The earlier strict
template and packets remain unchanged. The query authenticates source ID/UUID,
artist ID/UUID, original numeric show assignment and the saved current discovery
show UUID before exposing source display date, show display date or catalog sort
date. A changed discovery show UUID produces `discovery_show_uuid_changed`, with
all three date fields null and the newly observed UUID retained as metadata.
Missing shows or changed artists retain their own drift status and no dates.

Source venue and show venue references stay separate. Date context requires an
authenticated selected venue reference. Literal partial display dates are retained
as written; the catalog sort date establishes neither event-date precision nor a
venue's lifetime. An authenticated association does not prove that two venue
records describe the same physical place.

The query retains the original repeatable-read/read-only transaction, 30-second
statement timeout, 67-venue/51-source upper bounds and explicit field-size guards.
Only 29 sources are supplied. Bounds fail rather than omit rows or shorten strings.
Any later capture must keep missing or changed associations as observations. It
must retain the same selection and preserve replacement identities as drift.

The packet contains `venue-allowlist.json`, `source-allowlist.json`,
`strict-context.sql` and `receipt.json`. The receipt pins inputs, executable sources,
the new template and output hashes; it records 29 sources, 21 artists, two prior
recordings and 27 additional recordings. Outputs are bounded at 8 MiB each.
All inputs are checked again before atomic publication. Failed publication keeps
partial files and a failure receipt without a completed receipt. Recovery uses a
new directory; earlier manifests, labels, reviews and baselines stay unchanged.

Run a separate capture after preparation, using a new output directory:

```sh
python3 -B docs/research/2026-10-17-full-normalization/scale/scripts/capture.py \
  local-dev/normalization-data/2026-10-17-cross-artist/training-development-venues-v1/expanded-context-capture-v1 \
  --sql local-dev/normalization-data/2026-10-17-cross-artist/training-development-venues-v1/expanded-context-preparation-v1/strict-context.sql
```

The completed packet pins the prepared and executed SQL, response bytes,
read-only replica markers and completion marker. The UUID probe retains its
separate modified allowlist and exact query under
`expanded-context-show-uuid-probe-preparation-v1/`, with its response under
`expanded-context-show-uuid-probe-capture-v1/` in the same ignored parent directory.
Earlier packets remain unchanged.

Date context alone establishes neither physical venue identity nor a venue's
historical lifetime. The packet supplies no matching-accuracy measurement.

Run the synthetic tests:

```sh
python3 -B -O docs/research/2026-10-17-full-normalization/scale/scripts/test_prepare_expanded_venue_context.py
```
