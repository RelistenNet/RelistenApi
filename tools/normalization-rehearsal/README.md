# Verify a local catalog API after normalization migrations

This verifier checks that existing recordings remain discoverable through v3 collections,
artist pages and full shows. It compares every returned source, set and track UUID, ownership
reference and media URL with a saved SQL snapshot. It fetches no audio and starts no import.

Use the already-migrated `relisten_normalization_jerry_20261017` and
`relisten_normalization_govt_mule_20261017` copies. Run each separately with its own output
directory. The normalization identities, accepted credits, venue assignments and active
people and composition policies must remain empty. Collection membership and invalidation clocks are existing
state; the entire `normalization` schema is not expected to be empty.

## Prepare the API and comparison input

API startup runs migrations. Point it only at the local rehearsal copy, already at the
current checkout's migration version, at least migration 49. The export includes both people and
composition-policy state. Configure these values explicitly:

- `ASPNETCORE_ENVIRONMENT=Development`
- `DATABASE_URL` for the selected local normalization copy
- `REDIS_URL=redis://127.0.0.1:<dedicated-rehearsal-port>`
- `Ingestion__Engine=Temporal`
- `ENABLE_HANGFIRE_SERVER=false`
- `Temporal__Address=127.0.0.1:1`
- `PORT` for the selected API process

Unset inherited `PGBOUNCER_WRITE_HOST`, `PGBOUNCER_READ_ONLY_HOST`, `SENTRY_DSN` and
`OTEL_EXPORTER_OTLP_ENDPOINT`. Development omits Hangfire workers, and Temporal mode skips
recurring Hangfire registration. The API registers a lazy Temporal client without workers
or schedules. Popularity reads may write Redis caches or enqueue refresh jobs; the dedicated
Redis endpoint has no worker. A Redis URL's `/N` path does **not** isolate this application:
`RedisService` ignores it and uses database zero. `Program` binds `http://*:{PORT}`; an
`ASPNETCORE_URLS` value does not restrict that listener.

Export the manifest using `psql` on the selected clone. Supply connection settings through
the existing local connection configuration; do not put credentials into artifacts:

```sh
psql -X -qAt -v ON_ERROR_STOP=1 \
  -d relisten_normalization_jerry_20261017 \
  -f tools/normalization-rehearsal/expected-catalog.sql \
  > local-dev/normalization-data/<new-run>/expected.json
```

The SQL opens a read-only repeatable-read transaction and emits one JSON object. It contains
all catalog IDs and UUIDs, source ownership, current performance metadata, set and track
structure, media URLs, published collection contents and counts, and the empty normalization
state. It omits credit prose and artist configuration. Save this input unchanged.

## Run and inspect the result

```sh
python3 -B tools/normalization-rehearsal/verify_api.py \
  --base-url http://127.0.0.1:18931 \
  --manifest local-dev/normalization-data/<new-run>/expected.json \
  --output local-dev/normalization-data/<new-run>/api
```

The origin must be HTTP on `localhost` or `127.0.0.1`, with an explicit port and no path,
credentials or query. Redirects and ambient HTTP proxies are disabled. The output directory
must not exist. Every bounded HTTP response is saved under `responses/`; these responses
can contain source descriptions and contact text, so keep the directory ignored.

`result.json` is published last, from a complete staged file. Its `state` is `completed`
only when every comparison passes. Any route, pagination, ownership, UUID, property or URL
mismatch makes the result `failed`. Failures retain the response prefix and continue with
independent requests where possible. Cancellation retains a failed result and is rethrown.
Recovery uses a new output directory. Exit codes are zero for a completed comparison, one
for comparison failures, and two for invalid input or output setup.

The result pins the SQL input and each raw response by byte count and SHA-256. It reports
actual catalog, collection-membership, distinct checked source and track counts. Check
`browsable_sources_not_reached` and all errors; a large total alone is not evidence that
every recording worked. Bodies are limited to 8 MiB per response and 256 MiB per run, with
10,000 attempted requests and 1,000 pages per collection resource. Failed fetches consume
the request bound. Exceeding a bound fails explicitly.
The compact error list retains its first 1,000 entries and reports any omitted count.

The walk covers:

- Every published collection, artist facet and recording page, including every represented
  artist's collection filter. This verifies that a filter does not expand to an artist's
  recordings outside the collection.
- `/api/v3/artists?include_autocreated=true`, each visible artist detail, and every artist's
  `/normalized` show list. This older endpoint name refers to the flattened catalog response;
  it does not read the new canonical normalization layer.
- Every browsable `/api/v3/shows/{uuid}` and its complete nested sources, sets and non-orphan
  tracks, with nondecreasing set indexes and track positions. Equal track positions have no
  promised UUID order. Orphan tracks must stay excluded. Non-orphan tracks without a usable URL remain
  explicit inventory cases rather than disappearing from the comparison.
- Each actual partial-date or multiple-performance cohort through the v3 performances route
  and retained v2 date route. The latter must keep the original survivor UUID. Zero cohorts
  means that behavior was not exercised by this copy.

There is no standalone v3 source GET. Playback structure comes from the full-show response.
The verifier does not call catalog resolution, play-reporting, admin, import or provider
routes. Stored URLs are compared literally; the result makes no claim that their media is
reachable or playable over the network.

## Preserve the database checks separately

Before and after the API walk, retain the existing seven-relation catalog fingerprints and
the manifest's normalization-state query. Compare snapshots on the same migrated schema.
When comparing Govt Mule with its old source copy, use the documented projection that
excludes only the four added performance columns. These SQL comparisons establish catalog
preservation and empty accepted normalization state; HTTP checks alone cannot establish it.

The manifest deliberately retains orphan and inconsistent context. A non-orphan track whose
stored source, set or artist disagree is a failed ownership check. Sources without a current
artist/show or outside the browsable view are reported explicitly. Such findings need an
explanation in the rehearsal report; do not remove them from the input to obtain success.

The synthetic tests issue no HTTP requests:

```sh
python3 -B -O tools/normalization-rehearsal/test_verify_api.py
```
