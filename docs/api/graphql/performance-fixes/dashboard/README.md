# Profile the exact web dashboard

The [query](query.graphql) combines artist navigation, Scarlet and Drums history,
venue history and a full recording. Against application `a956703b9`, five serial
requests take 1,159 / 758 / 718 / 840 / 840 ms. The four warm samples have a median
of 799 ms. [before.json](before.json) records payload hashes, sizes and completed
SQL command timings from the local development API log.

Show membership, timing statistics and nested track membership repeatedly match
the songs' full recording histories. Those dependent reads account for roughly
600–700 ms of the warm requests. The timing/track readers already know the selected
shows but restrict them after the opaque SQL function has done full-history work.
Artist song browsing is another roughly 50–60 ms; most other commands are small.

## Approach and proof

- Add a show-scoped overload of the existing matcher. Keep its complete artist
  merge map, sibling validation and relational-integrity joins. Use one active
  implementation; the existing one/two-argument calls forward with NULL show scope.
- Use explicit unscoped/scoped candidate access paths. The scoped path starts from
  show UUIDs, then existing source/show and track/source indexes. NULL retains all
  matches; an empty array means no selected shows.
- Pass known show scopes from shared statistics and collection readers. Root song
  history still requires the unscoped population. Do not cache across snapshots,
  preload arbitrary graph descendants or change projections/ranking/limits.

Two temporary-function experiments returned exactly the original filtered rows.
A nullable filter inside the original join was much slower. Explicit candidate
paths took 49/33 ms in the first scoped Drums/Scarlet observations. Warm unscoped
old/new observations were 137/129 ms for Drums and 29/28 ms for Scarlet, with exact
row parity. These single SQL observations establish a direction, not HTTP targets.

## Progress

- [x] Profile five executions of the exact query on the real local catalog.
- [x] Compare temporary scoped implementations and unscoped compatibility.
- [x] Add the function overload/migration and contract tests. Twelve matcher/count
  tests pass, including exact pre/post migration rows, legacy/default signatures,
  NULL/empty/selected show scopes and whole-file sibling validity. The sibling test
  rejects an implementation that narrows the artist map to the requested song.
- [ ] Use scoped matching in the shared readers; rerun the exact query and REST.
- [ ] Simplify, review, run required checks and commit measured results.

The change needs one additive function migration; it changes no catalog rows,
tables or indexes. Existing callers keep their signature for application rollback.
Production deployment is outside this local task. Search-specific work stays deferred.
