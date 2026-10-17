# Local Phish import and repeat verification

The bounded 2026 Phish run passed: Phish.in imported 42 recordings, Phish.net enriched them, and both provider refreshes completed again without failures or skipped items. Actual API reads returned 42 shows, 128 sets and 746 tracks. The repeat preserved their playback identities, associations, dates and complete media URLs. This verifies the provider and read contracts for ordinary dates; the selected live data contains no same-day split.

This record is for the engineer and reviewer/operator. The [sanitized evidence](evidence/phish-smoke.json) retains execution IDs, counts, response-file digests and each selected show/source reference. The [show contract](contracts.md) defines identity and compatibility; the [execution plan](README.md) tracks the subsequent Jerry and Aadam samples.

## Scope and execution

The proof used API and worker binaries built from commit `735b94fb6cb9478f5eea2867fe44ce9a761f6929`, isolated local PostgreSQL, Redis and Temporal services, and an API on port 13826. No production writes or schedules were involved. Both providers were restricted to 2026. The 42 selected dates run from January 28 through October 12; this was not a full artist-catalog import.

The order was Phish.in, Phish.net, API verification, then the same two refreshes and another API verification. Phish.in supplies recording and setlist data; Phish.net enriches those recordings. Each of the four executions reported `Completed`, 42 completed items, zero skipped items and zero failed items. The evidence retains the common artist workflow ID and all four distinct run IDs.

The catalog started with eight Phish recordings. The before/after check preserved their eight show UUIDs, eight source UUIDs, 24 set UUIDs and 136 track UUIDs. The larger result therefore checks existing identities as well as newly imported recordings.

## Observable checks

Each API pass made 129 successful GETs: artist list/detail/normalized reads, 42 show UUID reads, 42 date-performance reads and 42 legacy date reads. Both passes covered every selected show and its nested recordings, sets and tracks. UUID and date reads agreed. Across the two passes, all 258 requests returned 200; their request/response-digest files were identical.

The repeat identity comparison reported `equal=true` with no differences. Its canonical manifest digest was:

```text
4018c8229eaa48841b343e2f4211f89d4ccccd4fe1a28f7380e1250e57579b3b
```

The manifest includes ordered playback objects, UUID relationships, dates, labels and media URLs. The evidence describes its digest format and retains bounded summaries rather than raw responses or reviews. Full manifests remain outside Git.

A sampled media range returned HTTP 206 and 4,096 bytes of `audio/mpeg`. That establishes access to one track at verification time. It does not test every upstream media URL.

## What this proves and what remains

All 42 shows have `performance_type=unknown`, and each selected date has one show. No early/late label or second same-day performance was inferred from this sample. Synthetic contract tests establish split behavior, explicit setlist assignment, repeat identity and legacy-date compatibility; a reviewed live split remains a separate check in the larger Jerry sample.

These runs exercise Phish imports and reads at the named commit. They do not establish the later Archive default-show policy, Jerry project assignments, Aadam publication coverage, full live discovery pagination or production readiness. Keep those results separate in the execution plan.
