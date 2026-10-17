# Local Jerry import and reviewed performance split

The local Jerry collection contains **348 playable recordings across six performing projects**. Actual v3 reads passed before the split, after the split and after a repeat import. A reviewed Legion of Mary early/late split retained the original show UUID, moved two recordings to a new show, and preserved every recording, set, track and media URL.

The [sanitized evidence](evidence/jerry-smoke.json) records selection hashes, workflow executions, recording references, HTTP summaries and identity comparisons. The [sample commands](live-samples/README.md) explain how to repeat the process. Raw metadata and complete playback manifests remain outside Git under `/tmp/relisten-acceptance-20261017/jerry-expanded`.

## Selection and publication

The proof retained 500 metadata responses from the bounded Jerry inventory. Review selected 349 items with supported project billing; 151 remained outside the runnable plan. Of the reviewed items, 347 published and two remained held. One previously published recording outside the new selection brings the collection to 348. This is a finite sample, not the full Jerry catalog.

The six projects are Garcia/Saunders, Legion of Mary, historical Jerry Garcia Band, Reconstruction, Garcia/Kahn and Jerry Garcia Acoustic Band. Collection membership explicitly includes those artists. It excludes Grateful Dead and the existing Melvin Seals `JGB` identity. Generic Jerry billing was resolved per item, without installing a global artist alias.

The first six artist workflows reported 348 published outcomes, three review outcomes and zero failures. The repeat reported 348 unchanged outcomes, three review outcomes and zero failures. Every rebuild completed. These workflow counters describe evaluations, including prior items and repeated review outcomes; the API count describes distinct playable recordings.

Two reviewed items remain unpublished: `jerry-2021-05-14` has ambiguous creator billing; `jg82-03-18.015193.jgb.vallee.gale.sbeok.t-flac16` triggers the conservative tribute detector. The latter contains historical Jerry Garcia Band billing, but its transfer notes mention a tribute to a lost venue. This is a known context-classification limitation that needs separate review. Neither hold concerns uncertainty between performances on one day.

## A split with real upstream evidence

Five recordings explicitly identify Legion of Mary at the Tower Theater on April 11, 1975. The [reviewed plan](live-samples/legion-of-mary-performances.json) retains their exact description headings and metadata hashes. The operator checked current supporting fields and approved performer decisions before assigning any source through `ShowPerformanceService`.

Three early recordings retained show UUID `8f949dd1-526a-4074-8da3-4986785068e8`. Two late recordings moved to new show UUID `8963bf0a-2819-408e-bc36-a9f54c67b9fd`. The existing date destination still resolves to the retained show, and the date-performance route returns both. All 348 recording UUIDs, 348 set UUIDs, 3,700 track UUIDs and complete media URLs survived; only the two late recordings and their tracks changed show association. An ordinary artist refresh preserved the reviewed split.

Twenty other published recordings have internal combined coverage and four have unresolved coverage. They remain playable under their primary/default show. Those internal classifications do not appear in listener responses or authorize performance-specific enrichment. The remaining assignments comprise 319 legacy/default associations and the five reviewed early/late assignments.

## Listener verification and repeat

The API ran on port 13825 against the isolated `relisten_jerry_20261017` catalog, with current API and worker code from `5a34337`. No production writes or schedules were involved. All 843 initial HTTP checks passed. After the split, 844 checks passed; the repeat passed the same 844 checks and returned an equal complete identity manifest.

The final API reads contain 269 shows, 348 recordings, 348 sets and 3,700 tracks across 266 artist/date groups. Four shows have partial dates. Two date groups contain several show rows: the reviewed early/late pair and a partial-date group whose equal dates do not establish one performance. Only the reviewed pair has early/late labels.

The final and repeat identity digest is `a0bbc7120c4b92061869a6f242822f8bfc0af3d4f2486085951bb06a53babf3a`. The manifest compares ordered playback, relationships, dates, labels and full media URLs. A sampled track returned HTTP 206 and 4,096 bytes of `audio/mpeg`; this verifies access to that track at the time of the check.

These results establish the larger playable sample, stable split identities and repeat behavior. The separate [local restore check](evidence/local-restore.json) preserved the catalog and reviewed assignments. Production backfill, backup/restore rehearsal and deployment remain operational work in the [plan](README.md).
