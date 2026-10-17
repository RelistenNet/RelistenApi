# Reprocess unchanged facts without creating more work

Automatic processing must settle when the catalog stops changing. A fresh work
attempt can acknowledge current inputs without rewriting the same accepted
facts. Otherwise each rewrite advances a shared candidate generation and makes
other consumers pending again.

This contract extends exact work-UUID replay. It applies when a different work
UUID publishes facts that are already current. It changes no public API or
database schema and enables no automatic matching policy.

## Keep facts and supporting evidence distinct

People credits compare the recording, optional file scope, resolved person,
role, original wording, state, basis and reason. An unchanged automatic claim
retains its UUID, revision, decision, timestamp and history. A fresh proposed
UUID can reuse the existing claim under the credit service's existing claim
resolution rules.

Imported-venue and show-venue assignments compare their catalog object,
resolved venue, state, basis and reason. A known assignment UUID identifies the
claim to correct; an equivalent pending proposal cannot replace that target.
Fresh UUIDs can reuse an existing claim. Conflicting fresh assignments retain
the current accepted assignment and enter pending review.

Both services append missing supporting citations to the current claim revision.
A citation is the complete tuple of input revision, JSON Pointer, optional start
and end code-point offsets, and optional quote. Repeating that tuple adds
nothing. Another pointer or span in the same retained input remains independent
evidence. New evidence advances the applicable candidate generation even when
the claim revision stays the same.

Validate a supplied credit or assignment UUID's original recording/file or
catalog scope before resolving duplicate claims or returning early. Finding an
equivalent claim at the requested destination cannot authorize rebinding an
existing UUID.

## Preserve raw metadata and reviewed authority

Person aliases, venue names and venue locations deduplicate only when owner,
raw fields, basis, complete citation and resulting automatic state agree.
Normalized name keys do not establish identical facts. Language, historical
dates, geographic fields and coordinate precision remain significant.

Qualified person and venue identifiers use provider, resource type and provider
key as their raw fields. Their existing accepted natural-key constraint still
applies. Exact repeats settle; another citation or basis for an existing
accepted key returns an explicit conflict rather than reporting a successful
write that discarded the support. This increment adds no identifier citation
aggregation model. The retained input remains available after the failed
publication.

Reviewed current claims and retained review history control automatic routing
before duplicate detection. A reviewed exclusion stays effective; changed
evidence follows the existing pending-review behavior. On the accepted route,
a supplied metadata UUID must retain its original owner, raw fields, basis and full
citation. Check that binding before another row can satisfy the duplicate
probe. A different basis needs a distinct assertion UUID where the relation
permits independent assertions.

## Music revisions describe current matching facts

Repertoire songs, compositions, work credits, imported-song assignments,
repertoire/composition links and music external identities compare the full
effective typed payload against the current immutable revision. Only
`ExpectedRevision` is excluded from equality. Evidence and its order remain
significant.

Check the expected entity revision and reviewed authority before taking the
unchanged branch. Explicit merges and splits still advance identity revisions:
their redirect topology is stored outside the displayed metadata payload.
Repeated reviewed acceptance can retain the domain revision while recording
the new operator decision and editorial revision.

Setlist and file-usage writes retain their existing revision behavior. Some
catalog scope columns are derived outside their payload; payload equality alone
cannot establish an unchanged usage record.

For a file moved between recordings, current catalog ownership takes precedence
over retained content ownership. The former recording owner cannot refresh that
file's contents. The current owner can publish an unchanged typed payload with
the current expected revision to refresh derived artist/recording fields. This
retains the file/content UUIDs and media URL. Retained ownership is a fallback
for historical correction when the current catalog reference is absent.

## Transactions, verification and rollout

The [candidate-generation locks](candidate-universes.md) serialize duplicate
probes with their writes. Work publication still records successful completion
and its consumed-input watermarks. A new work UUID can therefore change a
direct work dependency even when no candidate facts change.

Failure rolls back facts, evidence and completion together. The same attempt
can retry after recovery. Contract tests exercise fresh attempts, exact and new
citations, reviewed conflicts, UUID ownership, merge/split revisions and two
candidate consumers reaching a settled state. These tests do not measure
identity-matching accuracy.

Deploy this behavior before enabling automatic candidate consumers. No data
migration is required. Reverting to the preceding binary restores repeated
write behavior; stop automatic coordinators first to avoid unnecessary
reprocessing. Retain all evidence and review history.
