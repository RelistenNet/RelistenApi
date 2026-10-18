# Acquire a retained MusicBrainz representation locally

`musicbrainz-acquire` drives the public acquisition service once and exports a reproducible receipt. It supports fixed work, recording and artist lookups. These external assertions do not establish catalog artist, person, repertoire or composition links. Search and automatic identity acceptance remain separate work.

Use a dedicated local database named `relisten_normalization_*` on `localhost` or `127.0.0.1`, supplied through `RELISTEN_NORMALIZATION_DATABASE_URL`. Initialize its `normalization` schema and apply migrations **18 and 43** before invoking this mode. It does not need the Luna extraction cache migrations, initialize a database, start an API host or run ingestion. All processes using the same database share the MusicBrainz admission/cooldown row. Separate databases cannot coordinate that provider limit; use one dedicated database for this experiment.

Create a request file with the existing typed lookup fields:

```json
{
  "kind": "work",
  "mbid": "607fbd92-1a2c-4ddd-b3a7-1c563b184ebc",
  "cacheMode": "reuseFresh",
  "maximumCacheAge": "1.00:00:00"
}
```

Use the intended development MBID. `kind` accepts `work`, `recording` or `artist`. `reuseFresh` requires an explicit positive `maximumCacheAge` in .NET TimeSpan format; `refresh` requires that field be absent or null. Refresh still observes provider admission and backoff. The bounded request file is at most 64 KiB; duplicate, unknown or missing required fields and invalid UUIDs/enums are rejected before acquisition.

```sh
dotnet run --project tools/RelistenNormalization -- musicbrainz-acquire \
  /path/to/request.json /path/to/new-output-directory <command-uuid>
```

Use a new nonempty command UUID for a new logical request. The fixed HTTP client disables redirects, cookies and decompression. It sends `RelistenNormalization.Operator/1.0 (https://relisten.net)`, accepts JSON, and requests identity encoding. Origins, includes and capture profiles come from the acquisition service; the request file cannot supply a URL or credentials. No provider API key is needed. Keep response bodies in local evidence directories rather than console logs or source control.

Each invocation may perform at most one GET. Compact stdout reports `state`, `commandUuid`, `notBefore` and `outputDirectory`. Exit code 0 means the acquisition is `completed`; exit code 1 includes both `pending` and `failed`. A pending result returns immediately. Invoke the same request and command UUID with a **new output directory** at or after `notBefore`; the acquisition service owns bounded automatic lease recovery and its three-send budget. An exact terminal replay performs no GET. A fresh-cache hit also performs no GET or input-head advance. Changed request fields with an existing command UUID conflict.

The output directory is claimed with a CreateNew ownership file. Existing directories and files are never overwritten. A completed local invocation writes `manifest.json` last, including for an acquisition that remains pending or has failed. The manifest records executable assembly hashes, batch/acquisition/decoder versions and each artifact's byte length and SHA-256:

- `request.json`: the exact supplied request bytes.
- `command.json`: the saved public command, attempt states and original capture reference.
- `attempts/<ordinal>-<uuid>/response.json`: available immutable response metadata with the body stored separately in `body.bin`; incomplete bodies remain explicitly absent.
- `attempts/<ordinal>-<uuid>/capture.json` and `input.bin`: each available original capture receipt and exact retained bytes, including historical/nonadmitted captures.
- `decoded.json` beside an input: the current pure decoder's typed observations of that exact retained revision, bounded to 32 MiB. Qualified external IDs remain provider observations.
- `cached/`: the referenced original capture when a cache hit has no attempt of its own.

Response bodies remain bounded by the acquisition service's 8 MiB limit, with only its allowlisted provenance/backoff headers. The batch uses evidence and store reads for export; it never recaptures bytes or modifies catalog relationships. The [acquisition contract](../../docs/plans/active/2026-10-17-full-normalization/musicbrainz-acquisition-contract.md) explains leases, failure receipts, cache eligibility and original-generation CAS.

Cancellation or an unexpected exception publishes no final manifest. The batch makes a best-effort audit for up to ten seconds using an independent token: `interruption.json` records the saved command and exception type, and `recovery/` retains available responses/captures. That audit can itself be partial if the database or filesystem is unavailable. The database command remains authoritative. Resume the same command/request in a new output directory; a saved eligible response can complete capture without another GET. Preserve all original output directories. No exactly-once network delivery is claimed.
