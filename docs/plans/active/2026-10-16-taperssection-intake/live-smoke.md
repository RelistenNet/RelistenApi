# Live local import and v3 verification

On October 16, 2026, a bounded import fetched real Archive metadata, published two approved Taper’s Section artists, and exposed their recordings through a running API host. This supplements the synthetic integration suite. It does not establish accuracy across the full collection.

## Results

Discovery selected 20 item identifiers. It retained all 20 metadata observations, held 19 items for review or artist approval, and deferred one existing Phish item. No item failed. After reviewing the retained creator, title, date and description, the local operator approved Chris Thile and Pavement. Neither name nor slug appeared in the current production artist listing with `include_autocreated=true` (4,358 entries). The isolated seed contained five existing artists; it was not a copy of the full production registry.

The ordinary `--taperssection-refresh` command completed with `Observed=2`, `Published=2`, `Failed=0`. Both item revisions were rebuilt and both artists became visible in the default v3 artist list.

| Artist | Show date | Archive item | Sources | Tracks |
|---|---|---|---:|---:|
| Chris Thile | 2022-06-16 | `01-christhile-2022-06-16.koto.-di-gi-ho-ar-de-rs` | 1 | 1 |
| Pavement | 1992-05-27 | `01-gary-intro` | 1 | 16 |

Actual HTTP requests returned 200 for:

- `/api/v3/artists`, with both artists visible without `include_autocreated`.
- `/api/v3/artists/9f146454-982b-21fb-122f-a56f5ba77793/normalized`, with one year and one Chris Thile show.
- `/api/v3/artists/5fea3fa5-64fa-9f69-08e7-27e50730c8a2/normalized`, with one year and one Pavement show.
- `/api/v3/shows/e472cebc-aef0-d50b-8f6b-f48214dc881b`, with the Chris Thile source, set and track.
- `/api/v3/shows/c0f4c207-e150-646c-a275-b565023e3946`, with the Pavement source, set and 16 tracks.

All 17 tracks had UUIDs and MP3 URLs. A `Range: bytes=0-1023` request to the first Pavement MP3 returned HTTP 206, `audio/mpeg`, and 1,024 bytes. This checks upstream media access; the API returns upstream URLs rather than proxying audio. A complete playback session was not exercised.

A second refresh returned `Observed=2`, `Unchanged=2`, `Failed=0`. Repeating the HTTP checks confirmed stable show, source and track UUIDs, with no duplicate rows. The artist-normalized responses were fetched before and after refresh, exercising cached reads after rebuild invalidation.

## Discovery correction

The first real run exposed a request contract the fixture tests had missed. With `fields=identifier`, Archive returned one unrelated `theses-and-dissertations` collection item for the Taper’s Section query. Metadata inspection held that item as unavailable; no artist or recording was published from it.

Requesting `identifier,collection,mediatype` returned 100 matching first-page items and a continuation cursor. A separate metadata-only continuation check returned 5,000 new matching identifiers with no overlap. The reported total decreased from 30,175 to 30,075. Every checked row belonged to `taperssection` and had media type `audio`.

The client now requests those membership fields and rejects an entire page containing an unrelated or unclassifiable row before retaining identifiers. Regression tests cover unrelated collection/type, missing fields, malformed media type, scalar/array collections and existing pagination behavior. Public responses do not identify the internal upstream cause.

After the discovery correction, `./test-local.sh --no-restore --filter 'FullyQualifiedName~TapersSection'` passed all 71 focused tests, including PostgreSQL and Temporal success, partial failure and recovery cases. Fresh correctness and readability reviews found no remaining issue in the correction.

The first failed discovery remains in the isolated database as evidence. Its one unavailable item is separate from the successful 20-item run. Full traversal and terminal-page behavior remain release checks.

## Reproduce the bounded smoke

Use the [local setup](../../../../local-dev/README.md), then create a separate database and seed it with `local-dev/seed/load-seed.sh`. Use a separate Redis instance on another port: the current `RedisService` does not select a database from the Redis URL path. Start a separate local Temporal development server. Do not register schedules.

The run used:

- PostgreSQL database `relisten_tapers_live_20261016` on local port 15432.
- An ephemeral Redis container on loopback port 16380, without a volume.
- Temporal on loopback port 17233, namespace `default`.
- The API on port 13823.

Set `DATABASE_URL` to the separate database, `REDIS_URL` and `Temporal__PayloadRedisUrl` to the separate Redis port, `Temporal__Address` to the separate Temporal server, and `Ingestion__Engine=Temporal`. Clear inherited PgBouncer host overrides and Sentry configuration. Build the solution, then use the worker DLL from `RelistenIngestionWorker/bin/Debug/net10.0/`:

```sh
WORKER_DLL=RelistenIngestionWorker/bin/Debug/net10.0/RelistenIngestionWorker.dll
Ingestion__LocalSchemaBaseline=12 dotnet "$WORKER_DLL" --migrate
dotnet "$WORKER_DLL"  # Keep the worker running in another terminal.
dotnet "$WORKER_DLL" --taperssection-discover --max-items=20
```

The command prints workflow IDs before completion. Inspect the execution result in that separate Temporal server. Review `normalization.archive_artist_review`, then approve explicit candidate UUIDs as described in the [operations guide](verification-and-operations.md#review-artists-in-postgresql). Research candidate names and UUIDs in this record are not approval for another catalog.

```sh
dotnet "$WORKER_DLL" --taperssection-refresh
```

Wait for the workflow result and inspect published and rebuilt revisions. Start the API from its `RelistenApi/` directory using the same isolated database and Redis settings, with `ASPNETCORE_ENVIRONMENT=Development`, `DOTNET_ENVIRONMENT=Development`, `ENABLE_HANGFIRE_SERVER=false`, and `PORT=13823`. Unset `OTEL_EXPORTER_OTLP_ENDPOINT` when unused; an empty value is treated as an invalid URI. Use `dotnet bin/net10.0/RelistenApi.dll`, not the stale legacy DLL directly under `bin/`.

Check `/health/ready`, then the v3 artist list, normalized artist and show endpoints. Compare IDs across a second refresh. Dates and counts above describe this captured run; live metadata and search selection can change. `PORT` binds the API on all interfaces under the current host configuration.

The existing local catalog and Redis instance were preserved. Production received only read requests for the artist listing. No production migration, approval, import or schedule change occurred.

After verification, the smoke worker and its Temporal server were stopped. The separate catalog, Redis container and API host were left available for local inspection at `http://127.0.0.1:13823`. Stop the API before removing its Redis container (`relisten-tapers-live-redis`); the catalog is separate from `relisten_db`.
