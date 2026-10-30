# Normalization vocabulary

`CatalogueVocabulary.cs` owns song kinds, venue kinds, venue-name kinds,
coordinate precision, and track qualifiers. Normalization models and API
resources use these types directly. API-specific availability and response
states belong to the API.

`EnumMember` values are the persisted text contract. Use `NormalizationEnums`
to format SQL parameters, parse text arrays, and supply structured model-output
schemas. Do not use `Enum.ToString()` for stored values: `WithGuest` must remain
`with_guest`. The existing simple-name scalar tokens can be read by Dapper;
qualifier arrays require explicit text conversion.

System.Text.Json converters preserve historical activity payloads, including
nullable values. The API's Newtonsoft adapter uses the same `EnumMember`
values without changing unrelated legacy numeric enums. This change needs no
database migration or workflow drain.

Unknown model-proposed song kinds are rejected as invalid answers before any
write. Previously they reached the database's CHECK constraint. Missing or
null song kinds remain valid. Venue proposal fallback rules are unchanged.

Run `Relisten.NormalizationTests` for parser, matcher, model-answer, and payload
contracts. `RelistenApiTests/Normalization` exercises SQL writes and reads on
disposable local databases; see `../local-dev/README.md` for setup.

## Stored artist venue counts

Migration 15 adds `artist_venues`: stable `artist_venue_…` identities and stored
show counts for artist/place pairs. Counts follow accepted show-level assignments
resolved to their canonical venue. They do not depend on artist rollout flags or
the number of recordings. Zero-count rows keep their IDs while both parents
exist; foreign keys cascade deletion when the artist or venue is removed.
`norm.venues` owns venue merges. Counts roll up to its canonical venue while an
existing pair for the merged venue stays at zero. Relationship rows contain no
copied merge pointer. Creation and update timestamps describe the derived row.

The `ArtistVenueCounts` service attempts refresh after import and normalization
work commits. The admin app refreshes within its venue-edit transaction, so a
refresh failure rolls the edit back. Artist-scoped recounts share the venue gate
and take ordered per-artist locks, so disjoint scopes can run concurrently. Full
recounts and venue edits use the gate exclusively. The SQL function grants the
restricted admin role narrow execution access.
There is no recurring count job or pending queue. Failed post-commit hooks and
direct SQL changes require an explicit refresh; no background repair cadence is
promised.

These derived rows have no human-edit or run provenance fields. Normalization
decisions and their audit history remain authoritative. The foundation changes no
public API response; v4 can consume the stored relationships in a later change.
See the [implementation plan](../docs/plans/active/2026-10-30-artist-venues/README.md)
for ownership, validation, and rollout details.

## Stored song counts

Migration 16 adds `norm.song_counts` for canonical matched-file/show counts and
`public.setlist_song_counts` for imported setlist-backed catalog-show counts.
Both are derived rows with cascading parent keys, zero values, and timestamps
that change only when their counters change. They carry no normalization run or
human-edit provenance.

`norm.song_recording_matches` owns exact recording membership for both recounts
and v4 discovery. `norm.refresh_song_counts` refreshes one artist under the
existing `norm-catalogue` artist lock; different artists remain independent.
`SongCounts` processes full repairs one artist/commit at a time, without a global
refresh lock. Imports, legacy rebuilds, and song normalization attempt refresh
after their source work commits. Admin song edits refresh atomically with the
edit; a failure rolls that transaction back.

There is no recurring refresh, pending queue, or per-row source trigger. A failed
post-commit hook logs the need for explicit repair without repeating successful
source/model work. Cancellation propagates. Counts can briefly lag membership;
GETs read counters and never refresh them. See the
[song-count foundation](../docs/plans/active/2026-10-30-song-counts/README.md) for
count definitions, maintenance ownership, repair, and rollout order.
