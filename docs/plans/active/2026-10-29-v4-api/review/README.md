# C1: inspect the real responses

Start the API with `./local-dev/v4-review/run.sh`, then open
[Swagger](http://127.0.0.1:43823/api-docs-v4) or run [requests.http](requests.http).
The [local setup](../../../../../local-dev/v4-review/README.md) explains startup,
captures and restoring the local approval flags.

The sample payloads in [responses](responses/) were captured from the restored
2026-10-29 production catalog. They retain the original titles, playback URLs and
normalization results; they are not hand-built responses. Full shows and headers
are captured locally in `local-dev/v4-review/.output/`.

Review these questions before we expand route families or mobile storage:

1. Does the [resolved medley](responses/matched-track.json) clearly preserve one
   playable file and its original title while exposing two normalized songs?
2. Does the [partial result](responses/partial-track.json) make unresolved text
   clear? The [longer JRAD example](responses/partial-medley-track.json) shows
   partial normalization on a real complex title.
3. Are [raw](responses/raw-track.json) and [disabled](responses/disabled-track.json)
   states clear and useful? All parent IDs should be typed strings.
4. Does the historical show request display **The Crown** on 1998-11-14 while
   preserving **Heritage Bank Center** as the venue's current name?
5. Are the complete show/source/set/track hierarchy and error responses usable?

The local flags are enabled for all 12 artists with normalized data, as Alec
requested. Production had none enabled. No normalization results were changed.

Verification: solution build passed; 33 focused tests passed, zero skipped
(15 identifier, 13 HTTP, 3 snapshot/session, 2 legacy Swagger). Tests cover
normalization corrections with unchanged imported timestamps, merges, failure
and recovery, nullable IDs, schema agreement, gzip/304 and browser preflight.
Live requests separately checked real full/partial shows, exact parent links,
historical display, CORS, gzip and unchanged 304s. Fresh review caught and fixed
Swagger naming, nullable ID serialization and legacy CORS integration issues.

This qualifies the local C1 response review. It does not qualify production
capacity, the remaining v4 routes, account migration or mobile rollout.
