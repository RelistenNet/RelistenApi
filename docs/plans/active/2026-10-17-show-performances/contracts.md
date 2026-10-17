# Show identity, date browsing and provider assignments

This contract extends the recorded catalog so one artist can have several performances on a date. A `Show` identifies one performance. Its `Source` rows identify recordings, whose sets and tracks remain the playback objects. The [execution plan](README.md) defines verification and the larger Jerry and Aadam samples.

## Identity and migration

Migration 16 keeps every existing show row and UUID. It replaces uniqueness on `(artist_id, display_date)` with `(artist_id, display_date, performance_key)`. `performance_key` is internal. It supplies a discriminator, while `show_performance_keys` retains artist-scoped provider or reviewed keys across date corrections. A key must identify a performance independently of mutable labels. A performance category alone cannot identify a show.

Existing shows begin with a legacy key and survivor marker. The first supported assignment for an existing date can claim that row and UUID; the claim persists. Additional performances receive new rows and UUIDs. Later imports, label edits and presentation order cannot change which show retained the original UUID. A reviewed split moves sources to their assigned show. The source, set and track UUIDs and media URLs stay unchanged; the `show_uuid` returned on source and track reads reflects the move.

An old show UUID continues to select the retained show with its current source list. It does not retain a second identity for the former whole-day aggregate. Date browsing provides access to all shows on that date.

Show rows and accepted keys survive the temporary absence of all their sources. Public recorded-show lists and counts exclude these empty shows. Their identities remain available when a recording returns. Empty recorded years and artists must not appear merely because internal show rows remain.

## Public show fields and routes

The existing show object gains these additive fields:

- `performance_type`: a string enum, `unknown`, `afternoon`, `early`, `late` or `other`; default `unknown`.
- `performance_label`: nullable provider or reviewed wording, such as `Early show`.
- `display_label`: the unchanged `display_date` when no category or label is available; otherwise the date, ` · `, and the supplied label or category.

`display_date` remains a date or supported partial date. It never acquires an early/late suffix. A billed show date does not establish a start timestamp or time zone. Date disagreement and precision notes remain internal.

`GET /api/v3/shows/{showUuid}` returns the one recorded show bearing that UUID, with only its assigned sources. Random-show selection loads the selected show by UUID.

`GET /api/v2/artists/{artistIdOrSlug}/shows/{showDate}` retains its single `ShowWithSources` response. On a split date it selects the retained legacy survivor; when no survivor exists, it selects the oldest recorded show by internal ID. This preserves the old response shape. New clients that need the whole date use:

```text
GET /api/v3/artists/{artistIdOrSlug}/shows/{showDate}/performances
```

The added route returns a JSON array of `ShowWithSources`, following existing successful show responses. It matches the literal `display_date`, including supported `XX` components. A missing artist or a date with no recorded performances returns 404 with the existing error envelope (`success=false`, `error_code=404`, `data=false`). Existing artist visibility rules apply. No pagination is added for this per-date result. Same-date presentation order is early, afternoon, late, other, unknown, then UUID. This order is a display convention; it asserts no start time. Existing date and ranking order remains primary in broader show lists.

Collection recording reads continue to return each included source once and its current primary `show_uuid`. Collection show counts count distinct current show UUIDs. Membership does not copy recordings or take ownership of their import.

## Accepted assignments and uncertainty

`ShowPerformanceService` owns durable assignments and artist-scoped performance keys. `AssignSourceAsync` accepts an artist, source ID, stable key, display date, typed performance category, optional label, JSON evidence and assignment kind. `AssignSetlistAsync` links a provider setlist to a supported show of the same artist. Both operations validate catalog ownership and run within a catalog transaction.

`source_show_assignments` records the source, primary show, assignment kind, source date used as its basis, evidence and update time. Assignment kinds distinguish accepted evidence from retained legacy, unresolved and combined coverage. `setlist_show_assignments` records the provider setlist's show and evidence. Accepted assignments and provider facts are separate records; rebuilds consume the assignments rather than deriving accepted identity from display text.

The existing `Source.show_id` and public `show_uuid` remain one primary association. Otherwise eligible new recordings with uncertain or combined same-day performance coverage publish under the default show: the retained survivor, or the oldest existing show when no survivor exists. If no show exists, create the ordinary default destination. Already-published uncertain recordings keep their prior primary association and playback identity. Internal evidence marks coverage unresolved or combined. This primary reference does not establish that a recording belongs wholly to one performance and cannot authorize performance-specific enrichment. Later supported assignments can move sources without replacing playback identities. This increment adds no public coverage array and invents no audio boundaries. Artist conflicts, unusable dates and compilations across dates retain their separate checks.

Ordinary exact-date recordings can bootstrap a legacy show until evidence distinguishes performances. New partial-date recordings receive separate unresolved identities; equal unknown dates do not establish a shared performance. Existing partial-date associations survive migration pending supported correction. A split leaves uncertain legacy sources under their last accepted primary association and prevents date-only enrichment from treating it as evidence.

A provider setlist can bootstrap from an exact date only when both the recorded performance and provider setlist are unique on that date. The retained basis allows the bootstrap to be removed if later evidence makes the date ambiguous. Explicit accepted assignments do not depend on this fallback.

## Provider responsibilities

Phish.in uses its numeric show ID for recording and provider-setlist identity. An unambiguous legacy date-keyed setlist is upgraded in place, preserving UUID and song references. An ambiguous old row is retained rather than arbitrarily claimed. The adapter supplies explicit source and setlist assignments from the provider identity.

Phish.net retains provider show ID and official permalink in optional activity input fields and accepted provider evidence. Date-based enrichment requires one matching provider show and one supported canonical performance. An ambiguous match returns a skipped outcome without fetching or overwriting ratings, reviews or notes. HTTP remains outside the write transaction; eligibility is checked again under the catalog lock before applying fetched data.

Archive uses its shared recording writer for native and reviewed intake publication. Clear title billing such as `Early Show`, `Late Show` or `Afternoon Show` can support performance classification; ordinary set numbers and passing mentions do not. Retain the exact supporting title and venue evidence. Uncertain or combined same-day billing publishes under the default show with internal coverage evidence. Existing unclear recordings retain their last primary association while ordinary metadata can refresh. Reviewed overrides use durable keys and retained supporting fields; an absent title label supplies no contradictory evidence. A changed supporting review field makes the assignment unresolved rather than withholding playable audio. Metadata-first dates and the existing partial-date resolver remain in force.

## Transactions, recovery and ownership

Performance-aware writers acquire the artist performance lock before changing source rows. The rebuild acquires the same lock before deriving associations and aggregates. Network work happens before that transaction. This serializes fetched-input writes for one artist and prevents lock-order inversion between assignment, source updates and rebuild. Different artists remain independent.

Assignment and source moves commit together. A failed item transaction leaves neither a partial move nor a half-created key. A committed assignment can precede its aggregate rebuild; the existing artist workflow reports and recovers that pending rebuild. Repeating import or rebuild reuses accepted keys and UUIDs. Redis and Temporal history are not the owner of identity or review decisions.

Migration 17 separately permits reviewed intake bindings to existing artists. Its durable ownership distinction keeps native artist edits, mappings and source refreshes under their original owner. See the [existing-artist publication contract](../2026-10-17-archive-collections/existing-artist-publication.md) alongside the [collection contracts](../2026-10-17-archive-collections/contracts.md). A collection membership or an existing-artist binding alone cannot claim a native recording.

Before production rollout, stop or drain incompatible workers, apply additive migrations and deploy matching readers and writers. Old rebuild code assumes one show per date and must not write after splits. Rollback pauses new writes and retains accepted identity/evidence; collapsing shows or dropping the assignment tables requires a separately reviewed data migration. Verify backup/restore and the public split, partial-failure and repeat cases before release.
