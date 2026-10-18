# Read matching facts and their original freshness together

`NormalizationCandidateService` has four typed facts methods:
`FindPeopleFactsAsync`, `FindVenueFactsAsync`, `FindRepertoireFactsAsync` and
`FindCompositionFactsAsync`. Each accepts a `NormalizationCandidateQuery`,
optional `CandidateFactsLimits` and cancellation token. Use the method for the
query's domain. Repertoire requires one artist UUID; the other domains are global.

Each call opens one primary, read-only repeatable-read transaction. Candidate
selection, accepted assertions, redirect context, owner observations, catalog
clocks and candidate generations all come from that snapshot. Calling the older
`FindAsync` and later loading newer facts or clocks does not meet this contract.
The existing retrieval-only API remains available.

Internal people consumers that also read source evidence or policy can use
`FindPeopleFactsInSnapshotAsync(connection, transaction, query, limits, ct)`.
The caller owns the primary repeatable-read transaction and its lifetime. This
method uses the same selection, facts, limits and read-set pipeline as the public
reader; it opens no connection, commits nothing and configures no Work. Read all
other decision inputs through that same connection and transaction.

## The returned contract

`NormalizationCandidateFactsSnapshot<T>` returns the reader version, query hash,
frozen query and limits, original candidate selection, typed facts, issues and
`CandidateFactsReadSet`. The read set contains candidate-universe generations,
[owner observations](owner-observations.md) and catalog artist clocks.

The reader copies caller-owned query lists before its first asynchronous wait.
The query hash covers the full frozen request, limits and projection version.
It identifies a lookup recipe, not unchanged database contents. A matching cache
must also account for the original read set; query hash alone cannot justify
reusing accepted facts after a correction.

Pass the original read set into work admission:

```csharp
var snapshot = await candidates.FindPeopleFactsAsync(query, cancellationToken: ct);
// The resolver must also check completeness, competing candidates, evaluated
// policy and reviewed exclusions before proposing an automatic acceptance.
var ticket = await work.BeginWorkAsync(
    workUuid, subject, policyVersion, inputResources, causalDependencies,
    cancellationToken: ct,
    expectedConfigurationGeneration: currentOwner?.DesiredGeneration,
    catalogInputs: snapshot.ReadSet.CatalogInputs,
    candidateUniverses: snapshot.ReadSet.CandidateUniverses,
    expectedOwnerObservations: snapshot.ReadSet.OwnerObservations);
```

Read `currentOwner` through `GetSubjectAsync` when configuring an existing owner; a changed binding or policy requires that configuration compare-and-swap. A new owner uses null. This read does not replace any original snapshot stamp.

Retain the recording/provider input expectations separately. Accepted identity
citations can refer to historical retained inputs; the facts reader does not
claim that those citations are the current provider response. It performs no
network requests, registers no work owners and accepts no match.

`Complete` means the requested projection was observed without a blocking gap.
It does not establish identity accuracy, geographic authority or complete
authorship. Truncated retrieval, omitted matching facts, missing owners or
missing clocks prevent automatic acceptance. Unknown authority/geography and
explicitly missing recording context remain separate diagnostics; a rule must
still establish every fact it needs.

## Facts retained by each reader

**People.** Retain the terminal identity and the complete bounded reverse
redirect closure. Retired display names remain separate identity headers because
retrieval can find them even without a corresponding accepted alias. Aliases
and qualified identifiers retain their stored person endpoint, editorial owner,
assertion UUID, revision, basis and exact citation. The data does not classify
an alias as a provider-qualified handle or establish identifier exclusivity.

Artist context aggregates all accepted recording credits through that closure.
It includes recordings without playable files. Per-artist totals and role/basis
counts are exact; missing-source and missing-artist totals remain explicit.
Bounded credit witnesses provide supporting examples with current-revision
citations. Limiting witness examples does not truncate artist membership.
Original identity owners and returned witness owners supply observations; the
global people generation fences omitted or newly arriving credit contributors.
Do not infer disjoint artist histories from missing catalog context.

People snapshots use `people-candidate-facts-v5`. `ActorMatchingFacts` retains
distinct source names, aliases, contacts, profiles and uncertainty separately
from bounded `ActorEvidence` examples. All eligible actor accounts contribute
matching facts and original owner observations, including accounts omitted from
the examples. Genuine semantic overflow blocks completeness; witness truncation
does not. Other domains retain `candidate-facts-v3`, including their existing
limits serialization and query fingerprints. See the [actor contract](people-actor-evidence.md)
for bounds, payload validation and policy compatibility.

**Venues.** Retain all accepted historical names, languages, validity intervals,
location assertions and qualified identifiers within the declared bounds.
Keep original locality, country/region/city/address wording, coordinates and
coordinate precision. Raw country and city labels are not resolved geographic
keys. Headers do not become invented cited aliases.

Retain stored parent relationships separately from their forward redirect
resolution and owner observations. Bound the union of consumed parent identities
for the whole candidate. A cycle, missing endpoint or omitted parent context is
explicitly incomplete. Facts preserve every location alternative; the matcher
applies observation dates without selecting a convenient assertion or combining
an address from different assertions.

**Repertoire.** Scope candidates to one artist. Retain title/alias assertions
from every accepted identity in the reverse redirect closure, active qualified
identifiers and every active composition link on those stored identities.
Preserve each stored link endpoint and its resolved composition context. A
withdrawn composition stays an unresolved endpoint rather than supplying a
false work identity. Consume both the artist repertoire and global composition
generations, including an empty result.

**Compositions.** Retain inherited titles, active identifiers and every accepted
work-credit role with its original wording. Person targets and catalog artist
targets remain distinct; resolve each through its own supported context. The
`PersonCreditTargets` and `ArtistCreditTargets` collections cover all roles,
including arrangers and performers. Only person credits in writer, composer or
lyricist roles can contribute to a canonical person-author comparison. Individual
credits do not establish an exhaustive list, so `CompleteAuthorship` stays false.

Music provenance uses the selected immutable `music_revisions.evidence` JSON.
An unselected history revision or an appended evidence-search-index row cannot
change the accepted matching facts. Reviewed exclusions remain protected by the
owner/editorial contract and typed writers; this projection does not expose the
exclusion ledger as identity evidence.

## Bounds and failure behavior

Limits cover returned candidates, identity/parent closure, assertions per kind,
title aliases, evidence per assertion, distinct artist contexts, role/basis
groups and witness examples. Readers use lookahead to report omitted facts.
The original selection remains visible when the facts candidate cap is exceeded.
An omitted location, author claim or rival cannot silently become a complete
projection. Missing owners are reported without creating them.

Candidate count accepts 1–1,024. Identity/parent closure, assertions, evidence and
artist-context limits accept 1–10,000. Role/basis groups accept 1–128; per-artist
witnesses accept 0–32 and missing-context witnesses 0–64. Defaults live in
`CandidateFactsLimits`. Witness-only truncation is explicitly nonblocking.
These are output bounds; exact credit aggregation and indexed retrieval still
need measured database work. Measure cohort latency and query plans before
setting backfill concurrency.

A cancelled or failed read commits no facts, work or clocks. A later admission
checks the original snapshot, including empty candidate pools. New rivals,
changed assertions, source artist changes and editorial corrections cannot be
justified by attaching fresher clocks to the old result. Reload the complete
facts snapshot after a stale admission.

## Reuse people facts within one snapshot

Source identity matching can use an internal `PeopleFactsSnapshotReader` for
several queries in the same transaction. Its factory verifies the exact live
connection and transaction, repeatable-read isolation and PostgreSQL's actual
read-only setting. The caller disposes the reader before that transaction ends.
It owns no transaction and cannot be reused across publication stages.

The reader can retain the last person's complete fact body, normalized limits,
required owners and artists, and body diagnostics. Each query still retrieves
its own candidates, scores, reasons, negative lookup scope and final owner/catalog
observations. It attaches the actual query hit to the reused body. It never
reconstructs complete ownership from bounded examples or borrows another query's
whole read set. Returned collections are read-only to internal consumers.

A changed header or limit, an evicted person, an overflowing identity closure,
or an encoded body above 32 MiB uses the ordinary full read. This retention budget
does not truncate returned facts or introduce a capacity refusal. It bounds
encoded retained content, not managed heap usage or process memory. Source
proposal preparation and person-cohort publication each create their own reader
inside their existing read-only snapshot. Saved proofs, query hashes, processing
versions and admission checks are unchanged.

## Verification and rollout

Service tests cover all four domains, original-owner provenance, current versus
historical citations, bounded omissions, exact cross-artist credit aggregates,
withdrawn targets and ambiguous venue histories. PostgreSQL lock barriers verify
that a concurrent correction leaves selection, facts and stamps on the original
snapshot; admission rejects that snapshot after the correction commits.

Migration 31 installs observational read sets. Migration 32 adds reverse
person/venue redirect, current credit-evidence, work-credit and music-identifier
indexes for these queries. Apply them before enabling consumers. No matching
policy, reader flag, production backfill or deployment is enabled here.
Rollback stops normalization coordinators/publication and retains schema,
identities, evidence and contribution ownership.
