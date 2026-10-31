# Review native v4 reads and legacy isolation

V4 evolves independently. Existing v2/v3 routes keep their services, DTOs, lookup
behavior, caches and Redis popularity, with targeted bug/security fixes as needed.
There is no legacy adapter migration backlog or rollout flag. Existing operator
artist writes/import commands also retain their paths and authorization.

The experimental legacy recording integration was removed. Its mapping class,
configuration, DI, extra numeric fields, reader policy switches, score/freshness
mode and adapter-only tests are gone. Existing legacy services and their original
constructor signatures are restored. The old empty-source exception behavior is
also unchanged; fixing legacy bugs is a separate targeted change.

## Retained v4 optimization

`CatalogRecordingReader` reads sources first, then batches sets, playable tracks
and links by source ID. This preserves two round trips and materializes each
source description once. It takes the caller's `CatalogReadSession`, preserving
the primary repeatable-read snapshot and cancellation timeout. Ordering is always
deterministic. It has no legacy modes, artist-scope parameter or nullable
transaction/connection choices.

V4 keeps its selected artist-local Postgres score lookup. Existing calculations
still publish to both Postgres and Redis as before. Storage/schema compatibility
remains required while old clients run, but v4 reads and response models do not
inherit legacy wire requirements. Share low-level helpers only where doing so
simplifies v4 without importing old behavior.

## Check the real responses

Start the standard local review host:

```sh
./local-dev/v4-review/run.sh
```

Review [the large v4 show](http://127.0.0.1:43823/api/v4/shows/show_5w5b5p4tvnf8f7pgc45r5ayga0),
[the corresponding legacy show](http://127.0.0.1:43823/api/v3/shows/bc2acb62-6b75-7a1e-7b41-842e0aaf4140),
and [an imported-only legacy show](http://127.0.0.1:43823/api/v2/artists/spafford/shows/2026-09-15).
The v4 source/set/track hierarchy, normalization and ETags retain their existing
contract. Old responses keep numeric/UUID identities, imported fields and cache
headers through their original services.

For repeatable comparisons, run an original build and the candidate against the
same static local database/Redis with imports and workers disabled:

```sh
python3 local-dev/v4-review/compare-catalog-reads.py \
  http://127.0.0.1:43824 http://127.0.0.1:43823
```

The GET-only script warms each of nine real routes four times, alternates the two
builds for 15 samples, compares full JSON/cache/ETag headers, and saves payloads
and timings under ignored `local-dev/v4-review/.output/catalog-read-comparison/`.
It exits nonzero on response differences. Latency requires judgment; schema/data
must remain static during a comparison. Commit `92b4ff5e5` is the pre-optimization
reference. Prior adapter experiments are historical evidence only.

The [verification log](../verification.md) records the current checks. The removed
adapter's legacy speedup is not a benefit of this native-only implementation.
