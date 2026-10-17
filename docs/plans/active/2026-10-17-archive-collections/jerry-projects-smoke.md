# Jerry: three reviewed projects and one held mixed recording

Three Archive recordings with the same broad creator label, `Jerry Garcia`, passed local publication under separate performing projects on October 17, 2026. A fourth item containing acoustic and electric bands stayed held. Repeating all three artist workflows preserved artist, show, source, set and track UUIDs and complete media URLs. Both passes completed their rebuilds with zero failed items.

The [sanitized evidence](evidence/jerry-projects-smoke.json) records the current metadata fetches, exact reviewed billing spans, project identities, execution-bound workflow results, HTTP requests and first/repeat playback references. This is a bounded local check of the [next-work plan](README.md), not approval to import the full Jerry creator inventory.

## What was reviewed

All four current metadata responses say `creator: Jerry Garcia`. Item-specific review supplies the performing-project assignment; that broad label was not installed as an alias for any project.

| Performing project | Supplied date | Billing evidence | Published files |
| --- | --- | --- | ---: |
| Jerry Garcia Band | 1993-11-01 | Exact `Jerry Garcia Band` span in the item title | 13 |
| Legion of Mary | 1975-03-07 | Exact `Legion Of Mary` heading in the description | 7 |
| Garcia/Saunders | 1973-07-08 | Exact `Garcia & Saunders` heading in the description | 11 |

These are the recording metadata’s dates and playback files, not independently verified event identities or complete setlists. The JGB recording’s notes explicitly describe missing opening material. The historical Jerry Garcia Band project uses its full name; no claim was made that Archive’s separate `JGB` collection identifies the same project.

The [1987-08-29 item](https://archive.org/details/jg87-08-29.030876.jgab.jgb.fob-schoeps.webber-small.minches.t-flac16) explicitly describes `Jerry Garcia Band (Acoustic & Electric)` and labels separate acoustic and electric sections. Its approved item review retains both performer alternatives. Each pass reports one `NeedsReview` outcome; the retained resolution is `conflict`, with no canonical source and no claimed intake row. Approval does not turn those alternatives into one accepted performer.

## Publication and retry

The isolated environment uses database `relisten_jerry_20261017` on the shared local PostgreSQL server, Redis 16383, Temporal 17236 and API 13825. The checked-in development seed was loaded into a new database; migrations 13–15 were then applied. Mule and Pavement’s databases and running APIs were preserved. No production writes or schedules were created.

`ProposeProjectAsync` created the three specific reviewed project candidates and materialized their cohort artists. `RetainObservationAsync` preserved each fresh metadata projection, and `ArchivePerformerReviewService.ReviewAsync` retained the exact supporting field spans and applicability. The normal `TemporalIngestionClient.RefreshTapersSectionArtistAsync` admission started each artist workflow; publication was not called directly.

The first pass observed four items, published three recordings and held one. The repeat observed the same four, reported three unchanged recordings and held the same mixed item. All six artist results report `RebuildCompleted=true`, with no failures. Each published item’s catalog and rebuilt revisions agree. The first and repeat manifests retain all three artists, shows, sources and sets, plus all 31 track UUIDs and their complete media URLs; the manifests match exactly.

## Listener and collection checks

Each pass made 20 successful HTTP GET requests against the actual API: the default artist list; each artist, normalized artist and show; the local collection overview; paginated artists and recordings; and artist-filtered recordings. All three new projects appeared in the default artist list, each with one show and recording. Show responses contained the expected 31 ordered playback files.

The published **local fixture** `jerry-projects-smoke` includes only these three project UUIDs through explicit artist-wide memberships. Its overview reports three artists, three shows and three recordings. Pagination returns each once; each artist filter returns its single recording. Grateful Dead is excluded from this fixture. The final product Jerry collection’s editorial membership remains a separate decision.

A single 4,096-byte range request to the JGB recording’s first MP3 returned **HTTP 206**, `audio/mpeg`, with the requested content range. This checks sampled media access; it is not a listening audit or validation of every recording.

The Jerry worker was stopped after both passes. The API remains available at `http://127.0.0.1:13825` for inspection. Raw metadata, complete workflow histories, helper source and request responses remain in `/tmp/relisten-jerry-20261017`. The retained repository evidence omits full descriptions, reviews, account fields and credentials.

This proves the selected projects can publish through the normal local workflow and preserve playback references on retry. It does not measure automatic performer accuracy across the 1,657-item creator query, resolve the mixed item’s passages, or establish production rollout and backup readiness.
