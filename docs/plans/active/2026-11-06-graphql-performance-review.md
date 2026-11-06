# Qualify GraphQL web workloads and shared REST performance

This investigation reviews branch `codex/v4-catalog-graphql-plan` at `41813085b`
against the REST implementation at `5087f5a39`. It does not authorize application
changes, migrations, deployments, imports, or production writes.

## Purpose

Replace small synthetic-fixture confidence with representative web workloads from
Relisten #devs: song/venue catalogs, duration and recording comparisons, playback,
multi-select queue building, dense artist navigation, and prospective analytics.
Separate implementation defects, index opportunities, aggregate API gaps, and
missing domain data. Preserve complete REST payloads, identity, and read semantics.

## Progress

- [x] Read Discord discussion and inspect current reader/loader paths.
- [x] Start first adversarial review; obtain initial SQL plans on the local catalog.
- [x] Save 12 runnable web query documents and measure 20 GraphQL cases.
- [x] Compare 12 pre-GraphQL and current REST cases on the same local database.
- [x] Validate scoped/grouped query alternatives and inspect existing indexes,
      without persistent DB changes. New indexes remain unmeasured candidates.
- [x] Independently validate first-pass findings; publish prioritized recommendations.

## Discoveries

- Generic show membership scans the catalog even for one artist/song; local plans
  show ~216k shows versus ten returned rows. Basic direct artist selection is fast
  using an existing owner index.
- Song-show statistics currently execute even when unselected; batched track pages
  can repeat whole-song matching/ranking separately for each requested show.
- Exact count is one InitPlan, not one count per returned row. It repeats candidate
  evaluation once; retain that distinction in the final review.
- Discord connector attachment URLs returned HTTP403. User explicitly requires
  connector-only Discord access; do not use computer/browser automation for Discord.

## Decisions

- Review-only: edits are limited to reproducible queries, benchmark tooling,
  evidence and documentation. Keep the user's running API on3823 intact.
- Use the existing local catalog (215,939 shows, ~5.1m files) for benchmarks.
  Production read-only metadata inspection is limited to understanding retained
  play reports/aggregates; no production workload timing or writes. Run timing requests serially; mark first
  samples and report warm medians/ranges, not statistically meaningful p95s.
- Host the pre-GraphQL REST commit separately on loopback3824 against the same
  local database. Development starts no workers/import schedules.
- Retain hard domain distinctions: file duration is not a measured composition
  duration; matches/files/tapes/shows/performance occurrences are not interchangeable.

## Work and proof

Save executable documents and results under `docs/api/graphql/review/`. Cover
metadata-only, timing, top/all playback, full-source expansion, first/next pages,
counts on/off, low/high-frequency songs, representative filtered venue tables,
artist navigation, playlist hydration, search, and existing REST discovery.
Extract deployed-local SQL/function/index definitions and use read-only
`EXPLAIN (ANALYZE, BUFFERS)` with statement timeouts. Test proposed predicates using
read-only SELECTs; any exploratory indexes belong only to disposable scratch data.
Require the second reviewer to reject weak findings and distinguish semantic
limitations from API/query limitations. Keep rejected claims in the review record.

## Outcomes

Published [review and evidence](../../api/graphql/review/README.md): Scarlet playback
10/25 shows takes 3.0/6.4 seconds warm, Drums25 takes 12 seconds. Nested SQL confirms
ten repetitions of whole-song matching/ranking for ten shows. Tour REST search
regresses 59× with identical JSON; grouped SQL restores a cheap one-pass experiment
using existing indexes. Song/venue search regresses about 10× and fills one additional
fallback result, with its first 19 objects unchanged. These are measured query
problems; no application optimization has been applied. Query/aggregate API gaps
and unavailable telemetry are documented separately. Required fixes remain open
in the implementation plan; this review itself is complete.
