# C1: inspect the real responses

Start the API with `./local-dev/v4-review/run.sh`, then open
[Swagger](http://127.0.0.1:43823/api-docs-v4) or run [requests.http](requests.http).
The [local setup](../../../../../local-dev/v4-review/README.md) explains startup,
captures and restoring the local approval flags.

The sample payloads in [responses](responses/) were captured from the restored
2026-10-29 production catalog. They retain the original titles, playback URLs and
normalization results. The venue example was reduced to the approved alias-only
response contract; stored normalization data was not changed. Full shows and
headers are captured locally in `local-dev/v4-review/.output/`.

## Review navigation and venue-to-playback links

Alec expanded this checkpoint before account/auth work. Start with the artist and
year requests in the collection: artist list → Grateful Dead → years → 1985 by number
or typed year ID → show summaries. Then use a summary's show ID to retrieve its
sources and playable tracks. Continue with the
[venue-to-playback walkthrough](venue-source-journey.md): artist venue groups →
canonical or imported venue → shows → full source → direct track. Swagger now
exposes 16 routes, including direct source detail and seven venue reads.

The captured [artist response](responses/artist.json) and [year list](responses/years.json)
show the typed navigation IDs. The restored catalog's Grateful Dead responses agree on 2,081 shows and 18,226
sources across 31 years. The 1985 detail includes 73 show summaries. Summaries
omit `sources`; full show detail owns the recording hierarchy. Venue identity and
aliases are identical in both responses. Show UI displays the imported venue label
and links to the normalized venue when available. These observations validate the
local snapshot, not production performance.

Artist capabilities and provider metadata are included. The venue slice adds
grouped artist entries, global and artist-filtered canonical reads, and imported
fallback collections with counts. Popularity augmentation, song/discovery routes,
artist bootstrap, account migration and mobile remain later work. Auth stays on
hold while Alec reviews the catalog journey.

Review these questions before proceeding:

1. Does the [resolved medley](responses/matched-track.json) clearly preserve one
   playable file and its original title while exposing two normalized songs?
2. Does the [partial result](responses/partial-track.json) make unresolved text
   clear? The [longer JRAD example](responses/partial-medley-track.json) shows
   partial normalization on a real complex title.
3. Are [raw](responses/raw-track.json) and [disabled](responses/disabled-track.json)
   states clear and useful? All parent IDs should be typed strings.
4. Does the show retain its imported venue label and normalized link, with
   **The Crown** represented as a typed alias in `venue.names`?
5. Are the complete show/source/set/track hierarchy and error responses usable?

The local flags are enabled for all 12 artists with normalized data, as Alec
requested. Production had none enabled. No normalization results were changed.

Earlier C1 verification: solution build passed; 51 focused tests passed, zero skipped
(including the core browsing journey, typed IDs, snapshot/session behavior and
legacy Swagger). Tests cover
normalization corrections with unchanged imported timestamps, merges, failure
and recovery, nullable IDs, schema agreement, gzip/304 and browser preflight.
Live requests separately checked real full/partial shows, exact parent links,
venue aliases, CORS, gzip and unchanged 304s. Fresh review caught and fixed
Swagger naming, nullable ID serialization and legacy CORS integration issues.

The [verification log](../verification.md#venue-to-playback-review-2026-10-30)
records the venue/source slice separately, including its final build, contract
tests, and real-data checks. Route counts do not establish complete API coverage.

This qualifies the local C1 response review. It does not qualify production
capacity, the remaining v4 routes, account migration or mobile rollout.

C1 feedback: closed categorical fields now have typed string enums in OpenAPI,
including normalization/part states, unavailable reasons, kinds, precision, FLAC
type and qualifier items. JSON spellings and null values are unchanged. The
HTTP tests compare advertised enum values with actual responses.

Expanded browsing review also corrected nullable nested-object schemas. Missing
venues/eras and unresolved song links now admit null without making shared
resource definitions or required objects nullable.

Venue feedback, revised 2026-10-30: canonical resources remain `venue` and `song`.
The venue includes deduplicated `{ name, kind }` aliases, where kind is `official`,
`short`, or `spelling`. Date periods and client historical-name selection have
been removed. `venue_display` remains absent. The
[Henry J. Kaiser venue example](responses/kaiser-venue.json) retains the six
name/kind pairs without their stored date ranges.

For show display, use the imported name and locality, with the canonical venue as
fallback only when the imported venue is absent. Link that label to the normalized
venue when present; normalization does not replace the imported label. For example:

```js
const label = show.imported_venue?.name ?? show.venue?.name;
const venueId = show.venue?.id; // Canonical navigation target, when available.
```

The venue-history fix branch was discarded. The API branch now builds directly
on the shared normalization enums branch. No enrichment, historical backfill, or
normalization data change was run.
