# Add dated context to the frozen venue development pairs

The 34 draft venue pairs contain 67 catalog venue resources across 66 artists. Their venue
rows have no performance dates; all retained coordinates are null and historical-name fields
are empty. This supplement can add current catalog associations and literal dates without
changing those labels or treating a matching name as physical identity.

Preparation is offline. It authenticates the frozen bundle and its original annotation rows,
checks the reservation metadata, and emits SQL for an operator to review. It makes no database,
provider, API or paid request. All query columns exist in the retained production schema12;
the queries require no later show-performance columns.

## Prepare and review the inputs

From the repository root, choose a new ignored directory:

```sh
python3 -B docs/research/2026-10-17-full-normalization/scale/scripts/prepare_venue_context.py \
  --output local-dev/normalization-data/2026-10-17-cross-artist/training-development-venues-v1/context-preparation-v1
```

The preparer retains:

- `venue-allowlist.json`: the 67 frozen venue UUID/ID/artist tuples and exact original metadata.
- `source-allowlist.json`: 51 sources across 44 of those artists, already permitted by the
  frozen development and reservation frame. Their show IDs come from their existing known
  recording-family keys. No source prose is loaded to make this list.
- The 67 selected original venue records, 51 selected source metadata records, and unchanged
  reservation index. Selected original row payload bytes are retained with LF delimiters.
- `strict-context.sql` and `metadata-discovery.sql`, generated from reviewed templates.
- `receipt.json`, published last with every input and output fingerprint. Existing output
  directories are rejected. A failed or interrupted preparation keeps its prefix and a
  failure marker; retry into a new directory.

The preparer checks partitions and reservations before opening selected original venue rows.
The validator selects physical line numbers, checks every selected line hash and missing-line
coverage, then decodes only that selection. Whole-file hashes and row counts operate on bytes.
U+2028 and U+2029 inside JSON strings remain content.

This corrects an earlier validator boundary: it decoded full annotation files while counting
and filtering them. The correction prevents future excluded-row decoding. It does not establish
which earlier commands inspected or used excluded text, and it changes none of the retained
labels, first answers, reviews or prior exposure notes.

## Capture the strict supplement first

After reviewing the generated query, an operator can use the existing replica capture tool:

```sh
python3 -B docs/research/2026-10-17-full-normalization/scale/scripts/capture.py \
  local-dev/normalization-data/<new-strict-capture> \
  --sql local-dev/normalization-data/2026-10-17-cross-artist/training-development-venues-v1/context-preparation-v1/strict-context.sql
```

The capture tool keeps its production read-only endpoint, secret handling, exact executed SQL,
fresh output directory and partial/completion protocol. Its default query is unchanged when
`--sql` is omitted. A failed query leaves `snapshot.partial.jsonl`; a final completion marker
is required before `snapshot.jsonl` is published. Do not overwrite either artifact to retry.
The capture receipt labels its 180-second connection default explicitly; these query templates
set their transaction's statement timeout to 30 seconds.

The strict query returns 67 venue observation slots and 51 source-context slots. Each row
retains explicit authentication booleans and an observation status. A nonnull current source
UUID alone is not proof that its artist and show still match the frozen tuple.

Venue identity authentication requires the original ID, UUID and artist ID/UUID. A missing,
rekeyed or reassigned row contributes metadata diagnostics but no replacement wording. An
authenticated row retains its current name, location, historical names, coordinates and
upstream identifier separately from `expected_metadata`. `baseline_metadata_unchanged` reports
whether those values still match; current catalog timestamps are row timestamps, not event dates.

Source UUID and artist must authenticate, its show assignment must remain the frozen known
show ID, and that show must still belong to the authorized artist. Source and show venue
references remain separate. Date fields appear only when this chain reaches an authenticated
venue in the 67-resource allowlist. The current show UUID is newly observed context; the
original metadata did not retain a historical show UUID to compare with it. A source that moved
to another show supplies a drift diagnostic, not permission to follow that show and read its dates.

Preserve `source_display_date` and `show_display_date` literally, including partial dates.
`show_catalog_sort_date` is the database's sortable date and may use a placeholder day or month.
Do not turn it into precise performance evidence. Any min/max summary must be labeled as the
range of these eligible catalog sort dates, with its exact source denominator; it does not
establish a venue's opening date, closing date or historical address.

No eligible matching context means missing coverage, not absence of performances. Twenty-two
of the 66 artists have no source in the existing strict intersection, and the other 51 sources
may not reference their selected venue. The queries keep unavailable and changed cases visible.

The strict query fails rather than truncates if a venue metadata object exceeds 64 KiB, combined
source/show display-date bytes exceed 8 KiB, or result cardinality exceeds the 67/51 slots.
Both queries run in a read-only repeatable-read transaction with a 30-second statement timeout.

## Discover additional associations without opening their text

Only after the strict capture is retained, review and run `metadata-discovery.sql` into another
new capture directory. It returns association kinds, catalog IDs and UUIDs only for the same
67 authenticated venue/artist tuples. It includes direct source-venue references, show-venue
references, and sources attached to those shows. Its parameters also contain identity keys
only. It returns no names, locations, dates, coordinates, credits or upstream payloads.
The query retains at most 100,001 candidate association rows and fails if more than 100,000 exist.
It applies that limit before sorting the successful output. This is an output-cardinality bound;
joins may inspect more underlying rows, and the statement timeout remains the query-work bound.
No successful partial list is published.

Classify discovered resources before generating any expanded date or wording query:

1. Match ID, UUID and artist against the already frozen resource metadata. Check reservation
   references, known recording families, duplicate-family groups and linked parent-source
   reservations. A changed tuple is drift and cannot borrow the old resource's eligibility.
2. Require both the existing artist and full family partitions to be development, and exclude
   every reserved or held-out resource and family. A development artist does not authorize all
   its recordings. Preserve grouping across all 34 venue pairs, including the 23 unknowns and
   existing split-only links, before any later fitting.
3. Mark a resource absent from the frozen frame as `outside_frozen_frame`. Do not assign it a
   new development partition by hashing its ID, name or current show. It needs a separately
   reviewed frame extension, including duplicate-family and reservation checks, before raw
   context can be exported. Existing metadata eligibility can authenticate known candidates;
   it cannot settle unseen ones.
4. Retain the complete discovery receipt and classification counts, including blocked and
   unmapped rows. Any approved second allowlist must pin these original decisions and resource
   tuples; reading current rows must not silently refresh a changed association.

No expanded raw query or classification is performed by the preparer. The 51-source strict
list is an initial safe capture, not the end of discovery or a statement that other context
does not exist.

## Use the new evidence without rewriting the baseline

Keep original labels and text hashes unchanged. A supplement is a new observation with its own
SQL, snapshot time, replica status, migration history and hashes. Compare each current venue
field with the old literal field before using it in a matching experiment. Changed wording,
identity or association stays explicit and needs review; an updated row is not a correction
to a frozen training answer.

Dated references can narrow room and relocation questions, but they cannot prove an address,
room, stage, organization continuity or geographic identity that the catalog does not store.
The unresolved Nublu, Showbox, historical-name and event/site cases remain unresolved until
dated venue/provider evidence supports a physical assignment. Current coordinates, if any now
exist, remain source claims with unknown precision unless independently established. Do not
feed label rationales or primary-source corroboration into runtime features without separately
retained matching inputs.

Offline checks:

```sh
python3 -B -O docs/research/2026-10-17-full-normalization/training/venues-development-v1/test_validate_labels.py
python3 -B -O docs/research/2026-10-17-full-normalization/scale/scripts/test_prepare_venue_context.py
python3 -B -O docs/research/2026-10-17-full-normalization/scale/scripts/test_capture.py
```

## Captured result

The [read-only capture receipt](evidence/venue-context-local-v1.json) retains both completed replica queries and the metadata-only classification. All 67 venue metadata objects match their frozen originals. Of the 51 strict source slots, two provide authenticated source/show venue references and dates; the other 49 do not reference a selected venue. These are coverage results, not venue identity judgments.

The IDs-only query finds 320 associations involving 122 distinct recordings and 105 shows. Two recordings already belong to the strict capture. The other 120 are absent from the authenticated development-only source index; this does not prove they are absent from the complete sampling frame or eligible for development. Their dates and raw payloads remain unopened by this supplement. Classify their original artist/family metadata and reservations before preparing an expanded raw query. The capture does not change any existing pair label or review.
