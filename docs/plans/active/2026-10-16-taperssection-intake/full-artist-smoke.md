# Full Pavement import and v3 verification

The complete indexed Taper’s Section inventory for Pavement was imported into the isolated local catalog on October 16, 2026. The final v3 catalog contains **100 shows, 107 sources and 1,993 tracks across 13 years**, spanning December 14, 1989 through July 25, 2026. All 107 recordings publish. Partial precision and metadata/identifier disagreements remain in internal date notes.

This extends the [first live smoke](live-smoke.md), which tested one recording per artist. The [inventory and result manifest](evidence/pavement-full-artist.json) records all 107 input identifiers and their final publication outcomes.

## What “complete” means here

Archive advanced search returned 107 recordings for:

```text
collection:taperssection AND mediatype:audio AND creator:"Pavement"
```

All results named exactly Pavement, belonged to Taper’s Section and had media type audio. Sorted pages returned 100 items, then seven, then an empty terminal page. The total stayed at 107, all identifiers were unique, and a repeat of the first page returned the same identifiers. The results supplied 100 distinct dates. This verifies the artist’s indexed inventory at capture time; it does not prove that every Pavement concert is archived.

The first full import used the existing `TapersSectionArtistWorkflow` item-snapshot argument to select those 107 identifiers with the already approved Pavement candidate. This avoided observing the entire mixed collection to exercise one artist. The candidate ID was an expected assignment, not authorization to bypass metadata checks: the ordinary activities fetched, retained and classified current metadata before each publication transaction. No upstream metadata was edited for the test.

Subsequent runs used the normal operator command:

```sh
dotnet RelistenIngestionWorker/bin/Debug/net10.0/RelistenIngestionWorker.dll \
  --taperssection-artist=5fea3fa5-64fa-9f69-08e7-27e50730c8a2
```

Follow the [first smoke’s setup procedure](live-smoke.md#reproduce-the-bounded-smoke), using this run’s database and ports listed under [Scope and remaining checks](#scope-and-remaining-checks). The same reviewed artist was approved once in the clean catalog. No additional artist or item review was needed for later recordings.

The 107-item workflow exercised a real continue-as-new checkpoint: the first execution continued into another execution and completed the remaining work before rebuilding. Every item was observed; the final result reported no failures and a completed rebuild.

## What the full import found

Two valid recordings were initially held by broad context keywords:

- `pavement1994-03-02.fm.flac16`: transfer notes said “best of my abilities.” That phrase describes audio repair, not a compilation.
- `pavement1994-05-14.fm.flac16`: the venue was Robert F. Kennedy Memorial Stadium. The venue name does not establish a tribute performance.

The evaluator requires event wording after “memorial.” Description prose needs recording-related context after “best of”; titles retain their existing compilation cue. Standalone “tribute” detection remains unchanged. The date policy is version 5, with [selection and nonblocking notes](contracts.md#date-selection-and-nonblocking-notes) kept separately from publication holds.

The full inventory also exercises two date cases that now publish with retained context:

| Item | Selected date | Internal date details |
|---|---|---|
| `pavement1992-00-00` | `1992-XX-XX` | Year precision; the title’s unknown month/day explains structured January 1 as a placeholder |
| `pavement1994-06-06.mtx.flac16` | `1994-03-06` | Day precision; current metadata takes precedence over the older identifier date |

The first recording retains both the title’s partial date and structured metadata’s January 1 value as evidence. V3 returns `display_date=1992-XX-XX` and the existing sorting surrogate `date=1992-01-01`. The second retains the identifier’s June 6 value alongside the selected metadata date. Neither case has a blocking reason.

`--taperssection-status --item=IDENTIFIER` returns the selected date and structured `date_details`: precision, selected field/rule and nonblocking notes with supporting values. Both records remain `published` with `reason=null`. Repeating the refresh preserves their notes. These details are not exposed through listener APIs; the show, source and track JSON field sets were compared before and after the change and are unchanged.

Known-year full and partial dates can be corrected by current metadata. Integration tests move one recording from a full date to a partial date and then to a different year, verifying stable recording/set/track IDs, removal of obsolete show/year groups and refreshed artist caches. Actual contradictory current metadata or a changed reviewed artist still holds an update.

## Verification through the public API

The verification made 117 HTTP GET requests against the running API on local port 13823. Every request returned 200:

- The default `/api/v3/artists` list and artist-by-slug endpoint included Pavement with 100 shows and 107 sources.
- The artist-normalized endpoint returned 100 unique shows and 13 years. Its dates matched all published intake assignments.
- The years list and all 13 year-detail endpoints agreed with the normalized shows and source counts.
- All 100 `/api/v3/shows/{uuid}` responses contained the expected published sources. Their identifiers and UUIDs matched intake ownership; artist, source and show references agreed throughout.

All 1,993 tracks had unique UUIDs, MP3 URLs and finite positive durations. Every published source had tracks, and every committed item revision had a matching completed rebuild revision. Every inventory identifier matched a published source. All 105 recordings published before the policy refinement retained their existing show/source/track identities.

Small range requests for the first MP3 of recordings dated 1989-12-14, 1994-03-19 and 2026-07-25 each returned HTTP 206, `audio/mpeg`, and 1,024 bytes. Range requests for both date-note examples also succeeded, including two tracks from the year-only recording. One initial Archive request returned HTTP 500 before subsequent range requests succeeded. These checks sample media access; they are not a full playback audit of every file.

A final artist refresh reported 107 unchanged recordings, zero review holds and zero failures. Repeated HTTP verification checked that source, show and track UUIDs stayed stable, with no duplicate recordings. The final workflow counts and verification results are retained in the manifest.

The focused regression checks include PostgreSQL and Temporal success, partial failure and recovery cases, ordinary Archive date handling and partial-date catalog rebuilds. All 133 checks passed; their exact command is retained in the manifest. Fresh correctness and readability reviews checked the policy changes, date evidence and final manifest. The solution build and repository formatting check passed; existing dependency advisory warnings remain.

## Scope and remaining checks

The catalog used `relisten_tapers_full_verified_20261016`, a separate Redis container on loopback port 16381 and a separate local Temporal server on port 17234. The API uses port 13823; unset the unused OTLP endpoint instead of supplying an empty URI. Earlier smoke data is retained offline and is not served by this API. The existing local catalog and production ingestion were preserved. No schedule was registered or enabled. The API remains available for local inspection; the smoke worker is stopped after verification.

Operator date details were also read with Temporal pointed at an unavailable endpoint. The collection-wide scrape cursor’s terminal behavior remains an operational release check. Completing this artist’s advanced-search inventory does not establish that separate protocol. An identifier mismatch alone does not require a review hold. Remaining review cases concern unusable dates, unresolved conflicting current metadata and protected artist identity.
