# Source actor alias binding, support and freshness contracts

This document binds the [ordered implementation plan](source-actor-aliases.md).
The [main normalization plan](README.md) supplies the evaluated matching and
rollout requirements. An explicit source alias is a statement about a local
actor. It is not a canonical identity assignment, recording credit, attendance
claim or person/group decision.

The assertion types, shared pure/retained extractors, optional recording proposal
scopes, explicit offline proposal mode and role-free proposal projection exist.
The [implementation plan](source-actor-aliases.md#progress-and-current-state)
records executed grammar and cohort verification. Migration 53, typed canonical
alias outcomes and current alias readers are integrated. The solution builds.
All 81 focused storage, reader and candidate-selection checks pass, including
a nonempty upgrade/backfill rehearsal. Broader post-fix verification is running.
The resolver, source-review entrypoints, complete producer-scope map and accepted
source publication lifecycle remain required.
Existing credit/metadata processing, review and API reads supply the authority
and recovery conventions. No source-actor matching policy is activated.

## 1. Current proposal shape and exact source identity

[SourceActorAssertions.cs](../../../../RelistenApi/Models/Normalization/SourceActorAssertions.cs)
defines `SourceActorAliasAssertion<TEvidence>`, context, coverage and extraction.
An assertion retains its format version, local assertion/reference UUIDs, exact
field hash, primary name/evidence, explicit aliases/relationships, heading,
qualifiers, statement and unresolved observations. `SourceActorAliasExtraction`
returns `Assertions`, `Unresolved` and field `Scopes` together.

The implemented envelope in
[NormalizationRecordingProcessing.cs](../../../../RelistenApi/Models/Normalization/NormalizationRecordingProcessing.cs)
is:

```csharp
public sealed record NormalizationRecordingSourceActorAliasScope(
    string ProducerVersion,
    Guid InputRevisionUuid,
    NormalizationResourceKey Resource,
    ProviderObservationOrigin RecordingOrigin,
    SourceActorAliasExtraction<NormalizedEvidenceReference> Extraction,
    ProviderObservationOrigin? FileOrigin = null);
```

The real optional `FileOrigin` property omits null during serialization. The
containing `NormalizationRecordingExtraction.SourceActorAliases` is also
optional/null-omitted for historical payload compatibility. It carries scopes,
not a second list of invented credit proposals.

The recording envelope uses `recording-source-actor-envelope-v2`; existing
credit/title proposal UUIDs retain their original producer identity. A recording
or file with no text fields adds no empty wrapper. An explicitly empty original
field remains a cited incomplete scope. Neither absence means that previously
supported aliases can be cleared. Extraction borrows its caller's validated
retained document while preserving field and origin checks.

The public recording checkpoint writer replays this alias-only builder against
the saved original inputs before it writes a batch. It checks every recording,
file and selected Local root scope, including coverage and local references.
If alias output is present, every prepared input group must reproduce its full
output. All-null historical absence remains readable and supplies no clear.
This validation does not replay credit or title extraction.

Music consumers read a named projection that excludes `SourceActorAliases`
from the primary and every additional extraction. They measure and load that
same projection under the existing byte bound, including during historical
correspondence review. All original input groups, bindings, titles, tickets and
read sets remain present. The durable producer and public proposal service keep
the full alias evidence. A null alias property in the music projection means
excluded data, not an empty complete alias account. Source-actor assignment and
review must authenticate against the full producer.

The containing published extraction supplies attempt identity and source UUID.
Its original input group supplies the contributor and binding. An immutable
source-actor claim must retain those coordinates, its full scope and the
assertion/reference UUID within that scope. There is no additional proposal UUID.
Authenticate a claim against that exact published extraction, not an assertion
copied from a later attempt with matching names.

A reference includes producer semantics, retained revision, exact field locator
and local actor/reference UUID. Retain the recording/file origin and resource to
validate account membership. Repeated provider fields with the same `Field`
label remain separate; their original pointers/coordinates determine which
field was read. A Local scope may cite a selected root supporting input rather
than the directory's primary revision. Validate it against the containing
binding's complete original input references, not only its primary input.

Offline `NormalizationTextEvidence` references use the authenticated capture,
resource UUID, original `Inputs` field and producer instead of a fabricated
retained revision or fetch receipt. Retained mapping namespaces local IDs by its
own producer and exact input coordinates. Equal raw UUID bytes across input
forms, fields, revisions or producers do not establish co-reference. Repeated
same-spelled actors at different positions remain distinct unless an explicit
source relationship connects them.

Require at least one explicit alias. Every relationship must be `ExplicitAlias`,
connect this assertion's actor and alias references, and have `Roles=[]`.
Reject foreign references, `JointRoles`, supplied recording roles and duplicate
local IDs. The first alias citation identifies its exact spelling; the statement
citation retains the complete relationship and both actor anchors. Preserve
quote delimiters and every Unicode code-point coordinate against the original
raw string. Validate the whole-field hash as well as the individual evidence;
display text or reconstructed visible HTML cannot repair a bad citation.

## 2. Extraction and replaceable field coverage

The [pure extractor](../../../../RelistenApi/Services/Normalization/SourceActorAliasExtractor.cs)
and [retained mapper](../../../../RelistenApi/Services/Normalization/RetainedSourceActorAliasExtractor.cs)
share the literal alias parsing used by recording actor evidence. The new path
must not call credit `Enrich` with an empty role list and inherit
`NamedRoleContext`. A `CreditMention` still requires a real recording role.

The supported initial context is a performer roster headed by Band/Lineup,
Personnel or Musicians wording, an actor list, a dash boundary and a recognized
instrument/vocal suffix. Preserve any “some or all” qualifier. Interpret the
complete suffix as an instrument list before splitting actors. Merely finding
an instrument word inside arbitrary prose is insufficient. Ordinary parenthetic
names without an explicit alias marker remain unsupported.

A full-field parser must preserve roster continuity across supported lines and
stop at genuine new sections, setlist entries, prose or unsupported boundaries.
Any unclassified alias marker or ambiguous boundary leaves a cited unresolved
scope. A failure on an earlier line cannot silently turn a later alias into a
complete negative result. Keep HTML/blank-line handling in the shared raw-text
reader, with exact original offsets. Evaluate the correction on full original
fields and both production and offline paths before marking coverage verified.
Song aliases, venue spellings, alternate dates and unsupported ensemble uses
must not become people assertions. Choir/list aliases attach only to their
local actor. Instrument context establishes neither attendance nor a credit.

`Complete=true` concerns one producer's exact source field. It means that the
whole supported field was available, all potentially relevant statements were
classified, and no decoder, parse, boundary or bound uncertainty remains. It
never means exhaustive knowledge of a person's names. The stable support scope
is source UUID, original contributor, input resource, exact original field
locator and producer contract. Keep recording/file account provenance with it.
Revision and assertion IDs belong to immutable claims; changing revision must
not make old support unaddressable for replacement.

Missing fields, unsupported formatting, truncated inputs, unclassified alias
markers and query/byte bounds prevent retirement. Null/empty proves a clear
only where the provider's field contract explicitly defines it; the current
pure/retained parser does not grant that meaning by itself. Extend provider
coverage and retained evidence together before supporting such clears. A fully
parsed replacement without aliases may retire this producer's old support in
that exact scope. Another provider, scope or reviewed canonical alias keeps its
own authority.

[Recording extraction](../../../../RelistenApi/Services/Normalization/NormalizationRecordingExtractor.cs)
retains recording and original file prose even if a file has no playable catalog
binding. [Local extraction](../../../../RelistenApi/Services/Normalization/NormalizationRecordingExtractor.Local.cs)
adds only the selected root account. Reuse the existing extraction owner,
published ticket, source/catalog fences and original input bindings. Add no
provider fetch or paid call to extraction.

## 3. Source assignment and independent review authority

Add a domain-specific source-actor resolution service/store under
`RelistenApi/Services/Data`. Its owner is
`(people, source_actor_resolution, SourceUuid)`, separate from credit resolution
and accepted recording credits. A saved attempt pins its processing version and
original published extraction. A claim retains the scoped assertion, complete
candidate snapshot, every actual comparison and proposed action.

The existing role-free
[PeopleActorProjection.Project](../../../../RelistenApi/Services/Normalization/PeopleActorProjection.cs)
uses the primary name and explicit aliases. It supplies no kind, canonical
identifier, contact/profile hint, attendance or accepted artist-credit count.
Artist UUID may remain catalog query context. Missing terms or projection
incompleteness cannot be accepted as a complete comparison.

Automatic assignment needs current independently evaluated applicability for
`source_actor_alias_assertion` and the exact extraction, projection and people
facts contracts. A recording-credit evaluation receipt does not qualify. Reuse
the people registry/numerical scorer with an explicit role-free input branch;
never construct a fake credit to invoke it. This input has no identity creation,
merge or kind capability. A reviewed assignment can select an existing
person/group. A handle alone resolves no person-versus-crew question.

Trusted source review names the exact source/scoped assertion, expected Work
editorial revision, expected source review-state revision, selected existing
identity and its expected revision, action and reason. Use
`NormalizationWorkService.ApplyReviewAsync` and declare every original/resolved
identity owner before locks. Keep immutable decision receipts in
`review_operations`; a mutable source review-state row is only their CAS
projection.

`accept` binds an unassigned assertion; `reject` excludes it, including before
any assignment exists; `retract` retires the link; `reassign` explicitly retires
old support before exposing a new identity link. Review revision zero must be
addressable for the first decision. A later automatic winner cannot silently
replace a reviewed binding. Conflicting automatic assignments preserve the
accepted link and remain reviewable.

A new input revision normally creates a new assertion key. Carry a reviewed
binding forward only when source, contributor, resource, field, producer and
whole original field hash match and the names, spans, context and relationships
are identical after changing only input-revision coordinates. Validate that
mapping completely. Equal name strings or one matching alias quote are not
proof. Changed text in a reviewed field holds new automatic assignments for
review; it does not assert that new actors are the old actor. A complete clear
may retire old source support while retaining the review record.

Canonical metadata review is independent. Existing `PersonMetadataDecision`
protects a person's normalized-name claim across citations, sources and new
UUIDs. Source assignment review cannot override that veto or retract an
independently reviewed canonical alias.

## 4. Additive storage and canonical write outcomes

Phase 2 source implements migration 53 (`AddSourceActorAliasAuthority`) and the
internal `WriteAutomaticAliasAsync` canonical writer. The nonempty pre-53
upgrade/backfill rehearsal and 81 focused checks pass. Broader post-fix service
verification and query-cost measurements are running. There is no public managed
support insertion or source-assignment service in this phase. Object-shaped
receipt JSON is not an evaluated-policy credential; the later accepted writer
must authenticate the complete saved stage before using these records.

Reuse `normalization.person_aliases` as the canonical assertion store. Add the
following domain-specific records in one additive migration:

1. `source_actor_alias_assignments`: immutable actor-to-existing-identity links
   with source/contributor/resource/field/producer coordinates, retained input,
   containing extraction attempt and scoped assertion key, original assertion
   JSON/hash, original person, source-resolution/extraction owners, application,
   stage, authority receipt and introduced/retired Work UUIDs. The contributor
   here identifies the original source/extraction input, not the stable
   person-owned Work producer in section 5. One current link
   per source/contributor/revision/producer/assertion key prevents concurrent
   bindings to different identities. Retain full evidence and origins.
2. `person_alias_contributor_support`: immutable links from an assignment to
   the actual canonical alias UUID, with introduced/retired Work UUIDs. One
   current link per assignment/alias. Both assignment and support must be
   unretired for source authority to remain current.
3. `source_actor_alias_review_state`: one current CAS projection per originally
   reviewed assertion key, with immutable coordinates/assertion JSON/hash,
   monotonically increasing review revision, accepted/rejected/retracted state,
   optional selected original person and last operation UUID. Accepted requires
   an existing identity; rejected/retracted grants no link. Index the stable
   source-field scope to enforce changed-field review guards.
4. `person_alias_unmanaged_support`: append authority for alias UUIDs. Backfill
   every existing alias row before enabling the current-alias view. Existing
   append publication adds unmanaged support when it successfully introduces or
   reuses an accepted automatic alias. Reviewed aliases retain their independent
   reviewed authority.

Assignments and support allow only the typed current-to-retired transition;
retain historical rows. Review state changes only under the trusted review
transaction. Its operation FK and receipt validation are deferred to commit:
Work runs the domain callback before appending its immutable review receipt.
Coordinates remain immutable and the CAS revision advances immediately.
A future source `reassign` command maps to Work's existing `replace` action;
phase 2 does not add a new common Work action. There is no FK to recording credits, fabricated role, person
metadata deletion or duplicate global alias store. Unmanaged *alias support*
is distinct from Work's `UnmanagedBaseline`; preserve both contracts explicitly.

The alias-specific writer replaces the alias use of
[AutomaticMetadataState](../../../../RelistenApi/Services/Data/NormalizedPeopleService.Metadata.cs)
with `CanonicalPersonAliasWriteOutcome`: accepted with actual UUID, exact
reusable duplicate, pending, protected reviewed claim or conflicting UUID.
The shared helper remains unchanged for external IDs and venues. Its nullable
result does not distinguish a duplicate from a protected reviewed claim, so it
cannot authorize source support. Attach current support only to confirmed accepted
rows. Pending/protected/conflicting writes do not make their scope a successful
replacement and cannot retire prior support as if they succeeded.

Maintain the existing exact-fact/citation duplicate rule. Equal spellings with
different evidence may remain different canonical assertion rows. Do not merge
UUIDs or lose citations to reduce capacity. Canonical reviewed rejection or
retraction must prevent resurrection across a new UUID, source, reactivation or
retry. Automatic support retirement must not add a metadata-review retraction
to `person_aliases_history`.

## 5. One complete producer contribution per person

This is a required phases 3–4 publication contract, not an implemented phase 2
service. Migration 53 permits many source-specific authority rows without one
Work contributor per source. Phase 2 fixtures exercise 1,001 independent support
owners in the reader; they do not prove a configured complete producer map or
many-claim checkpoint publication.

Use `(people, person, OriginalPersonUuid)` and one stable contributor:
`("relisten", "source_actor_aliases", "person_metadata")`. Partition that key by
the person owner; keep producer versions in preparations/support. Work's default
snapshot allows 100 contributors and its supported API maximum is 1,000. Do not
allocate one contributor per recording to represent thousands of source
supports. Source-specific assignments, evidence and scope ownership remain
separate inside the stable contribution.

The configured preparation contains the complete map of active and zero-alias
source scopes, their exact coverage/evidence receipts and immutable predecessor
preparation/Work/contributor revision. Prepare it with current person membership,
all retained source scopes and the explicit source delta in one primary
snapshot. Load raw revision/extraction proof for changed scopes; ownership keys
alone cannot authenticate extraction. Unsupported producer transitions preserve
old scopes until an explicit supported replacement can account for them.

Each delta changes only its named scopes. Retain other source support and its
bindings, other producers, unmanaged baseline and all peer observations. Supply
the complete `expectedContributionRevisions`. A nonempty managed contribution
without its exact configured scope map is unsupported; active support rows
cannot reconstruct cleared scopes or their prior proof. Select the configured
map by exact admitted headers/predecessor identity, not union generation or
history ordering. The current
[people checkpoint selectors](../../../../RelistenApi/Services/Data/PeopleAcceptanceApplicationStore.cs)
illustrate why replacements advance revisions even when the key union is equal.

Keep the last complete clear/retirement scope and its input revision/owner
bindings after its last alias disappears. Future unrelated updates must carry
that proof forward. Do not automatically empty the whole producer or prune
those bindings; whole-producer unregistration is a separate explicit operation
after a committed retirement receipt.

Use the explicit [people membership budget](../../../../RelistenApi/Services/Data/PeopleResolutionCapacity.cs)
and refuse overflow before partial admission. It selects 100 contributors and
100,000 stored bindings; Work defaults stay unchanged. A stable contributor does
not make its resource/owner union unbounded. Required tests include at least
1,001 distinct source supports on this contribution, many independent support
owners and another source's partial update preserving every unrelated scope.
Solve repeated many-claim checkpoint capacity as part of this route; do not
raise the 32 MiB cap or omit provenance to make it appear supported.

## 6. Accepted publication, replacement and recovery

Add a typed writer on `NormalizedPeopleService` that loads an immutable saved
person stage. Proposed entrypoints are:

```csharp
Task<SourceActorResolutionResult> ResolveAsync(
    Guid attemptUuid, Guid extractionAttemptUuid, CancellationToken ct = default);
Task<SourceActorAliasApplicationResult> ApplyAsync(
    Guid applicationAttemptUuid, Guid resolutionAttemptUuid, CancellationToken ct = default);
Task<NormalizationPublicationResult> PublishSourceActorAliasStageAsync(
    Guid applicationAttemptUuid, Guid stageUuid, CancellationToken ct = default);
Task<NormalizationReviewResult> ApplySourceActorDecisionAsync(
    SourceActorIdentityDecision command, string trustedOperator, CancellationToken ct = default);
```

These are future domain services, not current interfaces. Follow existing people
attempt/application/stage conventions rather than a generic graph engine. The
accepted writer accepts IDs, not caller-supplied assertions, candidates or policy.
Within Work publication, authenticate the original extraction, scoped assertion,
allowed policy/review receipt, candidate snapshot, review CAS, complete producer
scope map and original read set. Source-resolution and extraction owners remain
observed dependencies; a source owner does not own another person's metadata.

Preserving a peer/append alias keeps its last accepted fact; it does not publish
a new interpretation of the peer's older source. New or replaced alias writes
must cite the exact input revision consumed by the new ticket. Do not copy old
citations onto current input heads or refresh unconsumed peer observations.
Conflicting original proofs require a new complete preparation.

The transaction creates/reuses assignments, writes primary and explicit alias
names through canonical review protection, attaches support to actual accepted
UUIDs and retires only complete successful source scopes. Preserve exact name
evidence. Visibility changes update the existing person revision/timestamp
contract. Do not change display name, kind, external identities, recording
credits or participation.

Incomplete coverage may add individually supported assertions after a qualified
identity decision, but cannot retire prior support. Missing fields, parser
failures and pending/protected alias writes preserve prior support. A complete
explicit clear can retire its own old support; independent reviewed/unmanaged
alias authority remains. Reactivation may reuse an exact retained assertion;
a different citation still follows the existing exact-fact duplicate contract.
The typed callback rechecks kept-source membership under the admitted owner and
contributor CAS before applying deltas.

Read old assignments as well as new winners so an update can retire support for
an identity no longer selected. Preserve original stored person/alias owners
through redirects and observe resolved owners additionally. A redirect to a
survivor is not reassignment. Withdrawal addresses the original owner. Explicit
reassignment retires old support before publishing the new binding; report
committed old/new stages separately if continuation fails.

Every stage publishes one original person owner. After earlier stages commit,
reread derived candidates and re-evaluate the remaining assertions under the
same pinned policy, which must still be active. Save a new immutable preparation
for that next stage. Earlier aliases can change pools and winners. Copying old
winners with newer stamps is not re-evaluation. Keep original extraction/assertion
facts unchanged and respect existing/reviewed assignment conflicts.

Exact attempt/stage/Work UUID replay returns saved outcomes. A crash before
commit writes neither aliases/support nor watermark. A crash between person
stages reports the committed subset and resumes through freshness checks.
Changed input, review, pool or owner observations require a new preparation;
never edit an admitted stage's stamps. Domain results must include per-assertion
and per-stage disposition, actual alias UUID/state/reason, committed Work UUIDs
and replay flags. Credit-specific result DTOs or a single success flag cannot
represent partial alias publication.

## 7. Current visibility, review and candidate universes

Migration 53 installs `current_person_aliases`: accepted canonical rows with at
least one remaining authority—reviewed basis, unmanaged support or active accepted source support.
Last managed support disappearance leaves the raw row retained but not current;
independent authority keeps it visible. Re-adding valid support can restore
visibility without erasing history.

Use this projection wherever aliases affect behavior: `resolved_person_aliases`,
[generic candidate name lookup](../../../../RelistenApi/Services/Data/NormalizationCandidateService.cs),
[people alias facts](../../../../RelistenApi/Services/Data/NormalizationCandidateService.People.Facts.cs)
and direct alias searches/public normalized people reads. Changing only the
public resolved view would leave retired aliases available to matching.
Metadata review and duplicate detection continue reading raw assertions/history
so natural-claim protection remains effective across support sources.

Declare People and Compositions write scopes before resource/owner locks and
atomically advance their matching-universe revisions on support/unmanaged
visibility transitions, including retirement and reactivation. Alias-row
triggers miss support-only changes. Declaring a write scope locks a clock; it
does not itself advance the revision.

Managed assignment/support inserts and retirements update each affected original
person revision once per statement; the existing people trigger advances both
universes. Ordinary append publication retains its existing person revision
ownership, including new-person revision 1. Its append-only unmanaged-support
trigger advances only the two universes, so the service does not double-bump a
person. Source support retirement does not write canonical review history.

## 8. Complete matching names and original owner observations

The authored `people-candidate-facts-v5` contract adds
`AcceptedAliasMatchingNames` and `AliasMatchingNamesComplete`. New v5 snapshots
must contain both; the policy evaluator refuses absent or incomplete names.
Stored resolution proposals may retain an explicitly incomplete aggregate with
blocking issues, so bounded negative outcomes remain auditable. The names are
semantically complete: distinct exact wording sorted by `StringComparer.Ordinal`.
Normalize names downstream. Keep bounded raw `Aliases` witnesses with
`AliasEvidenceTruncated`,
`AliasAssertionCount` and `AliasSupportCount`. `AliasSupportCount` counts active
managed assignment/support pairs, not inferred independent corroboration.
Matching-name and witness budgets are separate. The default distinct-name limit
is 5,000 and matching/witness byte budgets are 4 MiB each; witness count uses the
existing `MaximumAssertionsPerKind`. The witness-byte measure is the UTF-8 JSON
size of the stored canonical alias rows, not a public response wire-size claim. The matching-byte preflight uses a
conservative JSON-encoding upper bound before names are materialized. It may
refuse an input whose eventual encoded bytes would fit; it never truncates a
claimed complete name set. Follow the existing people-v4 separation of
complete matching values and bounded witnesses.

People name retrieval now uses `people-actor-retrieval-v3`. It applies the
similarity threshold to canonical names and current aliases, groups original
person scores, resolves redirects, then limits distinct terminal people with
one lookahead. Repeated assertions, spelling variants and retired originals
cannot consume another person's place. Name/alias reasons and maximum scores
remain available. Genuine distinct-person overflow still makes the pool
incomplete. Other identity domains retain their existing retrieval query.

This query reads all threshold-qualified name rows before limiting people.
The returned-identity bound is not a database scan bound. Retain actual plans,
buffer counts and latency at narrow and broad thresholds before setting batch
concurrency. A timeout or cancellation supplies no complete candidate pool;
do not fall back to the crowded raw-alias-row limit. Historical policies retain
their original retrieval version and require new evaluation for v3.

Repeated proof rows do not consume distinct-name capacity or increase matching
confidence. Do not reject 10,000 identical alias assertions under a 5,000-name
bound. A missing aggregate is not an empty alias set. Added fields are nullable
and omitted when absent to preserve old serialized snapshots. Historical projection
can still read its old witnesses, but v5 policy/application validation requires
explicit semantic completeness. New processing fingerprints include v5;
existing evaluated v4 policy artifacts cannot authorize v5 acceptance.

These fields describe aliases; they do not add `PersonArtistContextFact` credits or manufacture credited actor
context. `PeopleActorProjection.Candidate` must consume the complete aggregate
under the new contract.

Read candidate selection, current aliases, all support membership, redirects and
owner observations in one primary repeatable-read snapshot. For every matching
alias collect original person metadata owners, current resolved owners and every
supporting source-resolution/extraction owner before witness truncation, even
for duplicate wording or an omitted witness. Preserve each complete original
`NormalizationSubjectObservation` tuple: subject UUID, desired generation,
editorial revision and published Work UUID.

Carry those observations and candidate universe generations through preparation,
contributed admission and publication for the whole resulting union, including
peers. Conflicting original observations require re-preparation; do not select
the newest stamp. A global people epoch supplements known owner observations.
It does not replace them. Universe clocks fence new owners/rivals absent from an
old read set. Unchanged desired generation with a different published Work UUID
still invalidates the old observation.

Semantic overflow, missing owners, invalid support JSON, incomplete pools and
refused complete membership prevent automatic acceptance. Bounded witness
omission alone does not. Verify independent source owners, omitted-witness
review changes, redirects/reversals, support visibility changes and stale
admission/publication through the actual services.

## 9. Migration, versioning, operation and rollback

Apply additive migration 53 before deploying the v5/current-alias readers.
Backfill unmanaged authority and install the current/resolved alias projections in the same transaction before
new readers use them. Preserve old aliases, reviewed metadata, identity and
source UUIDs, credits, playback URLs and public v3 response shapes. Deploy
storage/read compatibility first with source-actor acceptance disabled.

Version the pure/retained producer, recording envelope, role-free projection,
resolution/acceptance and new people facts contracts deliberately. Keep existing
credit/title identity versions while their actual bytes/IDs remain unchanged;
if shared-parser changes alter them, bump those producer contracts and reverify.
Optional policy applicability must omit absent fields so historical artifact
hashes do not change. Never reinterpret an old policy or terminal receipt.
Drain or reject unfinished incompatible applications under processing-version
rules; do not resume them with new facts.

The current explicit offline proposal mode shares the parser and retains whole
original `Inputs`; old modes retain their contracts. The later source-actor
context/view route needs a separate version and nullable DTO field. Scope its
keys by producer, resource, field and local reference; select exact primary or
alias citations and retain ambiguous references separately. Keep old targets and
source/view artifacts unchanged. Expanded coverage still requires independent
identity evaluation and public C# numerical parity even if the five feature
meanings remain unchanged.

Phase 2 changes no extraction or input producer version, credit/title identity,
public v3 response shape or matching-policy activation. The current pure/retained
source-alias producer remains `source-actor-aliases-v2/recording-actor-evidence-v1`.
The new people facts version changes candidate/query and processing fingerprints;
new policies need an evaluation for that contract. Exact terminal receipt replay
remains separate from unfinished processing under a new version.

Enable accepted publication only after the full support/review/read lifecycle,
scale and checkpoint tests and a qualified evaluated policy are ready. Monitor
assertion yield, unresolved fields, assignment errors, reviewed conflicts,
support retirement, lookup visibility and query cost with explicit denominators.
Fixture policies and fixed parser examples establish mechanics, not matching
accuracy. Automatic matching remains a program requirement, not permission to
activate a proposal-only route.

Rollback disables new extraction/acceptance but retains tables, proof and history.
Keep the current-alias predicate/view or use a rollback binary that understands
managed support. An old raw-table reader would resurrect retired aliases. Do
not drop support or review records to make old code run, and do not auto-prune
clear proofs whose original resources still justify withdrawal.
