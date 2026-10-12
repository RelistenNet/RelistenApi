# Readability recommendation after the formatting rebase

This is a recommendation for the next revision, not a description of changes already implemented. The rebase adopts `codex/csharpier-format`, including its catalog fixes, CSharpier 1.3.0, and repository instructions. Formatting now passes. The nested workflow, numbered fetch protocol, and indefinite input storage still need redesign.

## Put the execution sequence in the workflow

The artist entrypoint should express prepare, import providers, and rebuild. Each provider operation should express prerequisites, paginated item imports, and reconciliation. The item entrypoint should call the same item activity without entering artist-wide enumeration or absence reconciliation.

```text
Batch: run up to ten artist workflows
Artist: prepare → import providers → rebuild if needed
Provider: prepare catalogs → discover pages → import items → reconcile
Item: fetch related inputs → parse → commit catalog changes → return result
```

Use ordinary named workflow methods to group those responsibilities. Each method should stay at one level of detail. Keep Temporal activity calls recognizable; avoid another generic orchestration framework. Keep prerequisites and Archive deletion verification explicit. Keep cancellation propagation and exhausted-item handling next to the operation they govern. Centralize checkpoint advancement and ContinueAsNew decisions at completed-item/page boundaries.

This does not require an item child workflow. Artist and manual-item workflows can share activities. A child is useful when an item needs its own durable lifecycle, not merely to give a block of code a name.

## Choose activities by useful retry units

The default should be one item activity that fetches its related inputs, parses them, and commits catalog writes. Fetch before opening the catalog transaction. Safe GETs can repeat if the activity retries. Use an independent activity for an expensive fetch or reusable catalog preparation when avoiding repeated work is worth the additional boundary.

For example, Phish.net redirects, ratings, and reviews can be handled inside one item activity. Remove the one-request assertion and the numbered `part`/`Done`/`PreviousFetch` protocol. Keep HTTP timeouts, cancellation, and Retry-After handling. Use typed provider operations where providers differ; do not disguise prerequisites as ordinary items with a `catalog/` prefix.

Provider queues remain at ten concurrent activities. Several sequential GETs per activity fit that model. An activity dispatch rate is no longer a request-per-second limit; document configuration in those terms. There is no need to add a custom limiter or internal parallel request fanout.

## Store only what needs to survive

Raw responses should normally live in activity memory. Return small typed results and item identifiers through Temporal. Use bounded discovery pages so a large artist does not require a huge workflow payload.

Separate two questions: whether a fetch is worth checkpointing, and whether its output is too large for Temporal. A separate fetch activity can return a small parsed result without external storage. External artifacts should be an exception for measured size or an explicit reuse requirement. Any exception needs `expires_at`, automatic cleanup, and a documented recovery window. Do not silently expire inputs still required by an active workflow; define a bounded execution/recovery lifetime and fail clearly or start a fresh import after expiry.

Keep database writes safe under activity retries. Evaluate existing upserts and transactional operations first. Retain a compact transactional receipt only where it prevents a demonstrated duplicate effect or incorrect result. Receipt retention must cover the supported retry/recovery window; it is independent of raw-response retention. Remove application records that merely duplicate Temporal's execution progress.

Archive completeness and deletion evidence remain domain requirements. Retain the specific evidence needed to justify a deletion; there is no requirement to save every response from every provider for that purpose.

## Make local code readable after formatting

CSharpier handles layout. Authors still need to separate conceptual steps with blank lines, use braces around control-flow blocks, name compound conditions, and reduce nesting with early returns. Avoid terse names such as `part`, `done`, and `key` when a domain name would explain the value.

`DiscoverArtists` should read as: fetch a collection page, validate pagination, apply collections, return the next cursor. HTTP client construction belongs in the existing client/service setup. Pagination validation belongs in a named operation with a clear error. Transactional idempotency should be visible at the write boundary. Reconsider the initial receipt lookup under the revised retry model rather than preserving it automatically.

## Preserve behavior while removing machinery

Retain provider catalog comparisons, cancellation and retry tests, workflow replay/rollover checks, and Archive Present/Deleted/Unknown tests. Replace tests that enforce one physical GET per activity with tests of the intended import and retry behavior.

The rebase also exposed a provider gap: the newer Phantasy Tour legacy importer repairs existing setlist dates after complete listing enumeration. The Temporal adapter currently skips known shows and does not perform that final repair. Carry that repair into provider finalization, with coverage for an existing show with an incorrect date and an incomplete listing. The obsolete in-memory tour-date guard has already been removed; the adapter now calls the new database-derived tour-date calculation.

Make the next changes in reviewable chunks: simplify one representative provider end to end, make artist/item workflows express the resulting operations, then adapt the remaining providers and remove obsolete staging/recovery code. Preserve the seven enabled providers, PanicStream exclusion, stable identities, API contracts, and the separate worker.

A review is complete when a reader can explain execution order, retry boundaries, partial failure, cancellation, and deletion policy directly from the workflow and activity entrypoints. A formatter and passing tests support that review; neither substitutes for it.

## Rebase verification

CSharpier checked all 310 files. The API test run passed 109 tests and skipped 44, including the 20 opt-in local PostgreSQL tests. The worker build passed. Docker was unavailable, so the database integration suite was not rerun. Comparing the rebased ingestion changes with a formatted copy of the old branch found only the inherited Phantasy Tour repair changes, the corresponding adapter adjustment, and SQL indentation.
