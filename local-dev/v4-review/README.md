# Review C1 against the restored catalog

Run from the repository root:

```sh
./local-dev/v4-review/run.sh
```

Open <http://127.0.0.1:43823/api-docs-v4>. This is the normal Development API
with the real controller, reader, serializer and middleware registrations. It
binds to loopback and uses the restored `relisten_db`; the script never drops,
seeds or resets it. Development starts no import workers or schedules. Startup
applies the checkout's catalog migrations as usual; C1 adds no migration.

In another terminal:

```sh
./local-dev/v4-review/capture.sh
```

This selects real records from the 2026-10-29 catalog snapshot, captures full show
and track responses with headers in `.output/`, and refreshes the checked-in
[request collection](../../docs/plans/active/2026-10-29-v4-api/review/requests.http).
It changes no catalog data. The representative UUIDs come from that snapshot;
a different snapshot may require choosing replacement records.

The request collection works in Rider or a compatible HTTP client. Swagger
provides Try it out too. Start with the matched and partial tracks, compare
their full shows, then inspect the historical venue example. Playback URLs and
titles are imported values; normalized parts describe the file without replacing it.

The expanded C1 browse checkpoint implements:

- `GET /api/v4/artists` and `/api/v4/artists/{artistId}`: artist list/detail,
  counts, capabilities and provider metadata.
- `GET /api/v4/artists/{artistId}/years`: year list; append `/{yearId}` or
  `/by-number/{year}` for year detail with show summaries.
- `GET /api/v4/artists/{artistId}/shows`: the complete artist show-summary list.
- `GET /api/v4/shows/{showId}`: full show with sources, sets, tracks, imported
  venue observations, historical venue display and approved normalization.
- `GET /api/v4/source-tracks/{sourceTrackId}`: that same track resource directly,
  with exact typed artist/show/source/set parents.

Resources return directly. Every track has `normalization` with `state`,
`reason` and ordered `parts`. Raw, disabled and partial records stay playable.
Ordinary responses carry a weak HTTP ETag and require cache revalidation.
Send the returned ETag in `If-None-Match` to get 304 when unchanged. Errors use
ProblemDetails with `code`: 422 `invalid_id`, 404 `not_found`, or 503
`catalog_unavailable`; errors are no-store. The complete v4 manifest remains
future work after this review.

## Local approval flags and source provenance

The faithful snapshot had zero approved-read artists. Alec approved enabling
`norm_catalogue` locally for **all 12 artists with normalized data**. The target
set includes artists owning canonical songs, normalized show venues, or track
parts. Existing metadata, titles and interpretations were not edited.

Original feature rows are saved in `.output/normalization-flags-before.json`.
To restore the previous flags on this local snapshot:

```sh
PGPASSWORD=local_dev_password psql -X -v ON_ERROR_STOP=1 \
  -h 127.0.0.1 -p 15432 -U relisten -d relisten_db \
  -f local-dev/v4-review/.output/restore-flags.sql
```

These local files and large captures are ignored. Backup provenance and restore
verification are in `local-dev/backups/v4-c1-20261029/README.md` (local, ignored).
The archive has all catalog/normalization rows but omits playback analytics;
C1 does not depend on those event histories. Production flags were not changed.

The local setup accepts the inert `app` NOLOGIN role retained by restored
Timescale policies; it still rejects extra login roles or elevated `app` roles.
No provider imports or normalization jobs are needed for this review.

Synthetic regression data is confined to disposable test databases under
`RelistenApiTests/Fixtures/v4/`. Manual review uses the restored records.
