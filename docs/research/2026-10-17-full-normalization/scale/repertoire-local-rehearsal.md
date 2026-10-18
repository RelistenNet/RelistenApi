# Repertoire resolution on real local catalogs

The repertoire resolver publishes the stored Phish and Grateful Dead dictionaries
through the worker commands, and the accepted songs are visible through normalized
v3 reads. Exact replay and fresh retries preserve their identities. Every retained
legacy API response still matches, including partial dates and separate performances
on one day. The [completed receipt](evidence/repertoire-local-rehearsal-v1.json)
pins the artifacts and executable assemblies.

This verifies the repertoire implementation in commits `914a994` and `f540f69`.
The full local API suite passes 2,696 tests with no failures and 24 existing
tour-contract skips. All 66 focused repertoire and worker-command cases pass.
These results establish service behavior and compatibility; they do not measure
inferred matching accuracy or activate a production policy.

## Catalog and published results

The rehearsal uses a new local database,
`relisten_normalization_repertoire_20261018`, restored from the earlier Jerry/Aadam
rehearsal copy. It contains 268 artists, 607 shows, 701 recording rows and 7,826
tracks. The original rehearsal databases remain at their prior schema versions.

The public imported-catalog capture service retains these complete local dictionaries:

- Phish: 110 songs, including a second resolver decision page.
- Grateful Dead: 99 songs.
- Jerry Garcia Band: an existing artist with no stored dictionary.

The worker resolves all 209 imported songs as `provider_assertion` entries and
preserves their imported song UUID mappings. JGB completes a known empty account
without allocating a song. These are the stored local dictionaries, not claims
about complete current provider catalogs. JGB's recording-title normalization is
outside this rehearsal.

The initial attempts and exact replays take 37 worker invocations. Three fresh
retry attempts take another 16. Fingerprints of repertoire songs, imported song
assignments, external identities and music revisions remain identical after
retry. All three retained input heads also stay unchanged. No new song identities
are allocated during retry.

## Public API verification

The completed HTTP walk retains 1,833 responses, all with status 200:

- Six normalized artist-song list pages return exactly 110, 99 and zero entries.
- All 209 song-detail responses retain the expected artist, title, basis and
  imported song UUID. Each song's show page is also checked.
- All 1,409 earlier legacy responses remain JSON-equal to their saved baseline.
  This includes 1,406 v3 requests and three v2 saved-link compatibility requests.

The legacy comparison uses the unmodified HTTP responses from the migration-50
rehearsal. The tested database is migration 51 with 209 accepted songs. The earlier
verifier's empty-state guard and manifests are unchanged. The new comparison
reuses only its bounded loopback HTTP transport and compares the saved response
bodies directly.

The song/show pages are empty: accepted setlist and file-content consumers remain
to be implemented. No occurrence or file coverage is inferred from a dictionary
membership. Existing recordings remain available through the legacy routes.
Media references are compared as data; this walk does not request remote audio.

All 12 legacy relation fingerprints remain unchanged across migration, publication
and HTTP reads. They cover artists, shows, sources, sets, tracks, collections,
collection playback membership, imported songs and plays, venues, artist-provider
associations and providers. The four accepted music relation fingerprints also
remain unchanged during GET requests. Every retained response hash was checked
again after the completed walk.

## Retained procedure and recovery

The ignored artifact directory is:

```text
local-dev/normalization-data/2026-10-18-repertoire-rehearsal-v4/
```

It retains the exact copy procedure, read-only fingerprints, migration receipt,
capture driver/source/project/assembly hashes, coordinate-only worker requests,
per-invocation output, HTTP responses and retry receipts. The compact receipt
links their byte counts and SHA-256 hashes. Original recording prose and full
response bodies stay in ignored storage.

The `executables/` directory retains the four executed assemblies and their
dependency and runtime manifests. Its receipt records each original execution
path and the saved file's hash, so later builds can replace `bin/` output without
invalidating this evidence. Package assemblies remain in the normal dependency
cache; these copies are not a standalone runtime distribution.

The copy uses a read-only `pg_dump` and a restore into a new database, following
the [Timescale logical restore procedure](https://www.tigerdata.com/docs/deploy/self-hosted/backup-and-restore/logical-backup).
Migration 51 runs through the actual worker `--migrate` command. The capture
driver calls `NormalizationImportedCatalogService.RefreshArtistRepertoireAsync`
for the three fixed artists; it accepts only the dedicated loopback database.
It must exit zero and write a complete three-artist capture receipt before any
worker request is treated as ready.

The worker driver resumes each saved attempt to completion, then replays the
same UUID and request. The retry driver uses new attempt UUIDs with the retained
registrations. Both save their selected coordinates before the first command.
The HTTP driver runs the API with normalized reads enabled and a private Redis
instance; both processes are stopped after verification. It acquires no provider
or model data.

These drivers verify the public service and command contracts. A durable catalog
discovery/capture loop remains required for production backfill. For another
rehearsal, use new database and output names and retain separate receipts. A
partial directory or request file alone does not prove completion; keep failed
artifacts and resume only their exact saved attempt coordinates or start a new
explicitly selected run.

The next implementation is the [setlist and file-content consumers](../../../plans/active/2026-10-17-full-normalization/repertoire-consumers.md).
Their completed-account reader must distinguish a fully acknowledged repertoire
account from the partial accepted stages that the resolver uses for its own
recovery.
