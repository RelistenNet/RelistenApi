# Full Pavement import and v3 verification

The complete indexed Taper’s Section inventory for Pavement was imported into the isolated local catalog on October 16, 2026. The final v3 catalog contains **98 shows, 105 sources and 1,954 tracks across 13 years**, spanning December 14, 1989 through July 25, 2026. Two recordings remain held: one has an unknown performance date; the other has conflicting dates.

This extends the [first live smoke](live-smoke.md), which tested one recording per artist. The [inventory and result manifest](evidence/pavement-full-artist.json) records all 107 input identifiers and their final publication outcomes.

## What “complete” means here

Archive advanced search returned 107 recordings for:

```text
collection:taperssection AND mediatype:audio AND creator:"Pavement"
```

All results named exactly Pavement, belonged to Taper’s Section and had media type audio. Sorted pages returned 100 items, then seven, then an empty terminal page. The total stayed at 107, all identifiers were unique, and a repeat of the first page returned the same identifiers. The results supplied 100 distinct dates. This verifies the artist’s indexed inventory at capture time; it does not prove that every Pavement concert is archived.

The first full import used the existing `TapersSectionArtistWorkflow` item-snapshot argument to select those 107 identifiers with the already approved Pavement candidate. This avoided observing the entire mixed collection to exercise one artist. The candidate ID was an expected assignment, not authorization to bypass metadata checks: the ordinary activities fetched, retained and classified current metadata before each publication transaction. No source metadata or date was rewritten for the test.

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

The evaluator now requires event wording after “memorial.” Description prose needs recording-related context after “best of”; titles retain their existing compilation cue. Standalone “tribute” detection remains unchanged. The rules version advanced to 4 so retained interpretations are reevaluated without replacing their evidence. Regression cases preserve actual compilation, tribute and date-conflict holds.

The full inventory also exposed a date placeholder. `pavement1992-00-00` has an unknown month and day in its identifier and title, while Archive supplies `1992-01-01` as structured metadata. Its notes explicitly say the date is uncertain. The evaluator had ignored the incomplete title/identifier date and accepted January 1. It now holds explicit `00`, `XX` or `??` month/day values in the title or identifier even when a complete structured date exists. A confirmed January 1 performance remains eligible.

The final verification started from a clean catalog and processed the full inventory under those rules. These items never acquired a published source:

| Item | Evidence | Hold reason |
|---|---|---|
| `pavement1992-00-00` | Unknown date in title/identifier; structured metadata says January 1 | `ambiguous_date` |
| `pavement1994-06-06.mtx.flac16` | Identifier says June 6; title and structured metadata say March 6 | `conflicting_date` |

Both require better date evidence before automatic publication. The test made no date override. Stricter rules retain previously published assignments, as agreed; they do not automatically withdraw a known wrong assignment. The clean run verifies that these uncertain recordings cannot be published initially.

## Verification through the public API

The verification made 115 HTTP GET requests against the running API on local port 13823. Every request returned 200:

- The default `/api/v3/artists` list and artist-by-slug endpoint included Pavement with 98 shows and 105 sources.
- The artist-normalized endpoint returned 98 unique shows and 13 years. Its dates matched all published intake assignments.
- The years list and all 13 year-detail endpoints agreed with the normalized shows and source counts.
- All 98 `/api/v3/shows/{uuid}` responses contained the expected published sources. Their identifiers and UUIDs matched intake ownership; artist, source and show references agreed throughout.

All 1,954 tracks had unique UUIDs, MP3 URLs and finite positive durations. Every published source had tracks, and every committed item revision had a matching completed rebuild revision. Both held items were absent from the published source set.

Small range requests for the first MP3 of recordings dated 1989-12-14, 1994-03-19 and 2026-07-25 each returned HTTP 206, `audio/mpeg`, and 1,024 bytes. This samples media access across the date range; it is not a full playback audit of every file.

A final artist refresh reported 105 unchanged recordings, two review holds and zero failures. Repeated HTTP verification checked that source, show and track UUIDs stayed stable, with no duplicate recordings. The final workflow counts and verification results are retained in the manifest.

The focused regression command `./test-local.sh --no-restore --filter 'FullyQualifiedName~TapersSection'` passed all 93 tests, including PostgreSQL and Temporal success, partial failure and recovery cases. Fresh correctness and readability reviews checked the policy changes, date evidence and final manifest. The solution build and repository formatting check passed; existing dependency advisory warnings remain.

## Scope and remaining checks

The catalog used `relisten_tapers_full_verified_20261016`, a separate Redis container on loopback port 16381 and a separate local Temporal server on port 17234. The API uses port 13823; unset the unused OTLP endpoint instead of supplying an empty URI. Earlier smoke data is retained offline and is not served by this API. The existing local catalog and production ingestion were preserved. No schedule was registered or enabled. The API remains available for local inspection; the smoke worker is stopped after verification.

The collection-wide scrape cursor’s terminal behavior remains an operational release check. Completing this artist’s advanced-search inventory does not establish that separate protocol. The one conflicting recording also remains unresolved.
