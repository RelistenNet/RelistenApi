# C1: inspect the real responses

Start the API with `./local-dev/v4-review/run.sh`, then open
[Swagger](http://127.0.0.1:43823/api-docs-v4) or run [requests.http](requests.http).
The [local setup](../../../../../local-dev/v4-review/README.md) explains startup,
captures and restoring the local approval flags.

The sample payloads in [responses](responses/) were captured from the restored
2026-10-29 production catalog. They retain the original titles, playback URLs and
normalization results; they are not hand-built responses. Full shows and headers
are captured locally in `local-dev/v4-review/.output/`.

## Review navigation first

Alec expanded this checkpoint before account/auth work. Follow the first six
requests in the collection: artist list → Grateful Dead → years → 1985 by number
or typed year ID → show summaries. Then use a summary's show ID to retrieve its
sources and playable tracks. Swagger now exposes eight routes.

The captured [artist response](responses/artist.json) and [year list](responses/years.json)
show the typed navigation IDs. The restored catalog's Grateful Dead responses agree on 2,081 shows and 18,226
sources across 31 years. The 1985 detail includes 73 show summaries. Summaries
omit `sources`; full show detail owns the recording hierarchy. Venue identity and full name history are identical in both responses; clients
choose a historical label from the show date. These observations validate the
local snapshot, not production performance.

Artist capabilities and provider metadata are included. Popularity augmentation,
other route families, account migration and mobile remain later work. Auth stays
on hold until Alec has reviewed this expanded browsing contract.

Review these questions before proceeding:

1. Does the [resolved medley](responses/matched-track.json) clearly preserve one
   playable file and its original title while exposing two normalized songs?
2. Does the [partial result](responses/partial-track.json) make unresolved text
   clear? The [longer JRAD example](responses/partial-medley-track.json) shows
   partial normalization on a real complex title.
3. Are [raw](responses/raw-track.json) and [disabled](responses/disabled-track.json)
   states clear and useful? All parent IDs should be typed strings.
4. Does the historical show include **The Crown** and its date range in
   `venue.names`, alongside the stored canonical venue label?
5. Are the complete show/source/set/track hierarchy and error responses usable?

The local flags are enabled for all 12 artists with normalized data, as Alec
requested. Production had none enabled. No normalization results were changed.

Verification: solution build passed; 51 focused tests passed, zero skipped
(including the core browsing journey, typed IDs, snapshot/session behavior and
legacy Swagger). Tests cover
normalization corrections with unchanged imported timestamps, merges, failure
and recovery, nullable IDs, schema agreement, gzip/304 and browser preflight.
Live requests separately checked real full/partial shows, exact parent links,
historical display, CORS, gzip and unchanged 304s. Fresh review caught and fixed
Swagger naming, nullable ID serialization and legacy CORS integration issues.

This qualifies the local C1 response review. It does not qualify production
capacity, the remaining v4 routes, account migration or mobile rollout.

C1 feedback: closed categorical fields now have typed string enums in OpenAPI,
including normalization/part states, unavailable reasons, kinds, precision, FLAC
type and qualifier items. JSON spellings and null values are unchanged. The
HTTP tests compare advertised enum values with actual responses.

Expanded browsing review also corrected nullable nested-object schemas. Missing
venues/eras and unresolved song links now admit null without making shared
resource definitions or required objects nullable.

Venue feedback: canonical resources are now `venue` and `song`. The venue
includes names with inclusive-start/exclusive-end date periods; null bounds are
unbounded. `venue_display` was removed at Alec's request. Clients select an
active official name, then fall back to the stored venue label and imported data.
The [Henry J. Kaiser venue example](responses/kaiser-venue.json) preserves all six
stored name rows, including their
overlapping/open-ended periods.

For a client reading raw JSON, the default historical label can be selected as
follows. Keep the calendar day encoded in `date`; do not parse `display_date` or
shift the timestamp into the device's timezone for this lookup.

```js
const date = show.date.slice(0, 10);
const official = show.venue?.names.find(name =>
  name.kind === "official" &&
  (name.valid_from === null || name.valid_from <= date) &&
  (name.valid_to === null || date < name.valid_to)
);
const label = official?.name ?? show.venue?.name ?? show.imported_venue?.name;
```

Imported machine dates may be imputed for fuzzy labels. A client can choose the
canonical-label fallback instead when the source date is uncertain.
