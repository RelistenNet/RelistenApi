# Preserve complete author lists as source assertions

Individual writer credits do not establish a composition's complete author list.
Store a source's explicit list separately, with its evidence and unresolved
members. Cross-artist work matching can use complete authorship only when those
assertions agree and resolve to supported global people.

Catalog artists and normalized people have different identities. A work credit
may legitimately target a catalog artist, but that artist UUID cannot substitute
for a canonical person UUID in a person-based author match. Canonical people can
include groups; their global identities still belong to the people domain.

## Additive contracts

`CompositionAuthorshipChange : MusicChange` contains `CompositionUuid`,
`CompleteAuthorship` and `Authors`. Each `CompositionAuthorMember` retains its
role, original name, optional resolved person UUID and exact name citation.
Members use `writer`, `composer` or `lyricist`. Arrangers, cover artists and
original performers remain separate work credits.

`CompleteAuthorship` records the source's explicit exhaustive-list assertion.
Populated credit rows or a successfully parsed list cannot establish that fact
by themselves. A source can assert a complete list whose names are not all
resolved; retain the list while blocking a resolved-author match.

`MusicPublication.AuthorshipAssertions` is optional. A null value preserves the
serialization of older publications and review request hashes. Publish assertions
under their `(compositions, composition, compositionUuid)` owner through
`NormalizedMusicService.PublishAsync`. Existing typed review, evidence,
dependency, revision and unchanged-publication rules apply.

`GetCompositionAuthorshipAsync(compositionUuid, ct)` reads accepted source
assertions. Candidate composition facts retain their original revision provenance,
stored and resolved people, completeness flags and supporting citations.

## Matching and ownership

Each assertion UUID belongs to its composition. It cannot move silently during
a merge. Use explicit supported changes or retraction, preserving original history
and editorial authority. The assertion consumes the owners of its resolved people;
person corrections and redirects therefore invalidate dependent work.

To establish a complete resolved set, require a nonempty complete assertion with
every author resolved. All complete assertions must agree. Partial assertions and
individual author-role credits cannot add another author or hide an unresolved
author. Retain incompatible assertions separately and block automatic completeness.
Non-author roles do not enter the author set.

`SaveCompositionLink` validates `writer_evidence` against this accepted complete
set and the original matched-person dependencies. It resolves person redirects
before comparing IDs. Qualified work-identity matching retains its existing
separate contract. A title alone establishes neither route.

Candidate projections apply explicit bounds to assertions, members and citations.
A blocking projection issue leaves aggregate `CompleteAuthorship` false and its
author set empty. Each retained source assertion still exposes its original
completeness claim. A truncated subset cannot become a convenient complete list.

## Migration, compatibility and verification

Migration 38, `AddCompositionAuthorshipAssertions`, adds the assertion family and
candidate invalidation. Candidate facts use projection version 2. Existing work
credits and playback identities remain valid; old snapshots without author-list
assertions do not acquire inferred completeness.

Stop incompatible publishers before switching versions. Retain assertions,
revisions, evidence and review history on binary rollback. No matching policy is
activated by this migration or writer change.

Verify complete, partial, unresolved and conflicting lists; person redirects;
person/artist ID separation; non-author roles; bounded projections; unchanged
publication; reviewed corrections; transaction rollback; and explicit merge
ownership. Verify author evidence through publication and candidate/read contracts,
not by checking table or configuration strings.
