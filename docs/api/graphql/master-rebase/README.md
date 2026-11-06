# Master API contract integration

The GraphQL branch is rebased onto master `24b185192`. REST and GraphQL share
master's compact artist metadata, approval-based catalog browsing and global venue
membership. The show-first pagination and scoped matching changes are retained.

## Changes carried forward

- Artist `features` is an unsigned 64-bit decimal string in both transports.
  GraphQL provider assignments contain only `provider` and `upstreamIdentifier`;
  providers use a nullable enum and unknown stored names remain null.
- Approved artists browse only canonical songs and venues, including empty
  catalogs during incomplete normalization. Other artists browse imported entries.
  Both transports use these rules in `CatalogBrowseMembership`. Explicit imported
  inventories and search's imported fallbacks remain available.
- The new `GET /api/v4/venues` route uses shared collection membership and venue
  metadata, matching GraphQL's global venue connection. Surviving zero-show venues
  are included; counts retain approval gating.
- The executable SDL and client operations reflect the new feature/provider
  contracts. Master's DTOs, controllers, feature constants and TypeScript helper
  remain intact.

## History and review

The original tip `7deffbab4` is retained at
`codex/v4-graphql-pre-rebase-20261106`. The [range-diff summary](range-diff.txt)
accounts for all 22 original commits: 20 are patch-equivalent, and two contain
conflict resolutions for shared readers/global venues and the integration guide.
Commit `937287088` adds the approval-policy adaptation and contract checks after
that rebase. Both independent review passes found no actionable correctness or
readability issues. The simplicity pass keeps policy in the existing selector;
it adds no cache, projection layer or schema migration.

To reproduce the original patch comparison:

```sh
git range-diff \
  05da1c055..codex/v4-graphql-pre-rebase-20261106 \
  24b185192..a58fc60a1
```

## Verification

All **1,043 tests pass**: 677 API and 366 normalization tests, with 37 existing API
skips. The focused V4 run passes 238 tests. New checks cover known/unknown GraphQL
providers, string feature masks, approval changes to empty canonical catalogs and
global venue ordering/count parity. Master's REST contract tests remain green.
The solution build, CSharpier and whitespace checks pass. The generated feature
module is current, and both TypeScript helper tests pass.

The [real-catalog checks](verification.json) run against application `937287088`,
with no concurrent tests or intentional SQL profiling. They verify 402 canonical
Grateful Dead songs and 479 artist venues against REST, plus 4,766 global venues,
including 1,189 with zero approved shows. Artist masks and provider assignments
also match their shared REST projection.

Five requests of the exact dashboard take 2,150/286/260/215/202 ms, rounded;
the four warm samples have a 237 ms median and 202–286 ms range. This is a fresh
smoke measurement after restart, not an isolated old/new load test. Earlier
performance artifacts describe the pre-master artist contract: their frozen
feature selections and artist payload hashes are historical, while current
executable operation files target the new schema.

With the current local API running, repeat the read-only checks using:

```sh
python3 docs/api/graphql/master-rebase/verify.py \
  --application-commit ACTUAL_RUNNING_COMMIT \
  --output /tmp/relisten-master-contracts.json
dotnet run --project tools/generate-artist-features -- --check
node --test clients/typescript/artist-features.test.mjs
```

The rebase and commits are local. No push or production deployment occurred. The
updated API remains available at port 3823 with the admin GraphQL explorer.
