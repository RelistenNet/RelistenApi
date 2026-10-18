# Read accepted sets without losing empty boundaries

`GET /api/v3/normalization/setlists/{editionUuid}/sets` lists accepted sets for one
edition. It includes empty sets, repeated labels and unnamed sets. Each set has a
stable UUID and an edition UUID. Occurrences identify their accepted set through
`set_uuid`; clients can display an empty set even when it has no occurrences.

The endpoint uses the existing normalization read flag, `limit` and `cursor`
parameters, and `items` / `next_cursor` response. Limits are 1–200, default 50.
Responses use `Cache-Control: no-store`. Invalid UUIDs, limits or cursors return
`400 invalid_request`; disabled reads return `503 normalization_unavailable`.
A missing or retracted edition returns 404. A known active edition without
accepted sets returns an empty page. Retracted sets are excluded.

Known positions sort in ascending order, followed by unknown positions. UUIDs
break ties and paginate unknown positions; that order does not establish a
performance sequence. A null position remains distinct from the maximum integer
position. Cursors belong to one edition and this route.

Each item exposes `uuid`, `edition_uuid`, nullable `position`, `label` and
`encore_number`, plus the existing music relationship metadata. `revision`,
`basis`, `decision_uuid` and `updated_at` describe the complete structure decision
that accepted the set. They are not separate set-edit revisions.

Evidence contains only that set's exact citations from the current accepted
structure revision. It retains original input UUIDs, JSON pointers, spans and
quotes. Each citation and the set report whether its resource head is `current`,
`changed` or `missing`. A provider update changes evidence freshness without
rewriting accepted wording or citations. Whole-account evidence and other sets'
citations are not added to the item.

The read runs in one primary repeatable-read transaction. It pages the indexed
accepted set table before projecting the selected sets' citations from the
immutable structure payload. Migration 41 adds the set table and page index;
apply it before enabling this route with the new binary.

Provider annotations use the separate bounded [annotation endpoint](setlist-annotations.md),
`GET /api/v3/normalization/setlists/{editionUuid}/annotations`. It preserves source
account identity, exact annotation evidence and explicit set/passage correspondence.
The set endpoint remains focused on accepted set boundaries.

Verify service pagination across repeated, empty and unnamed sets, including
unknown positions after the maximum integer position. Exercise the HTTP route,
nullable fields, scoped evidence after provider changes, tombstones, known-empty
editions, missing editions, disabled reads and edition-bound cursors.
