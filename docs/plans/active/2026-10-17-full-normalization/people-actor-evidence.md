# Use source actor evidence for global people matching

People matching can use explicit aliases, attributed profiles, contacts and actor
context from retained recording metadata. Preserve what each source claims. A
shared email, handle, project page or name can retrieve candidates and support
comparison; equality does not establish a canonical identity.

The extraction, accepted-context, retrieval and comparison changes are implemented.
The solution builds; all 238 focused checks and 1,933 broader API and normalization
regressions pass, with 24 existing tour-contract skips in the broader run. Fresh
correctness and readability reviews are clean. Large-catalog query performance
and independently retained primary pages still require verification and implementation,
respectively. No real policy is activated by this work. Keep the existing
[people acceptance](people-acceptance.md) ownership, evaluated-policy authority,
publication fences and recovery protocol. Playback and public APIs stay compatible.

## What the source can establish

The [development discovery](../../../research/2026-10-17-full-normalization/scale/evidence/people-identity-discovery-v2.json)
contains 38 family hypotheses across 207 recordings and 172 artists. Thirty-four
have credited contact, profile or owner corroboration; four retain attachment or
person-versus-project ambiguity. These are development hypotheses, not accepted
global identities or an independently evaluated launch result.

`CreditMention` and `RetainedCreditMention` have optional, null-omitted `ActorFacts`.
Old terminal JSON remains readable. `RecordingActorEvidenceExtractor` retains:

- Explicit `aka` and `also known as` aliases, including the relation's original
  marker. Equal strings elsewhere do not inherit that relationship.
- Joint roles for one actor and distinct coordinated source references. These
  source-local UUIDs never identify canonical people or prove global inequality.
- Attached email-shaped contacts and supported profile URLs. Literal wording,
  lookup keys and attachment evidence remain separate.
- Explicit person/group descriptions and role context. Kind remains unknown when
  the source does not establish it, or when its statements conflict.
- Unsupported namespaces, uncertain alias boundaries and unattributed hints as
  unresolved evidence.

This version attaches facts within one original role-bearing clause. A nearby
uploader, generic contact, performer roster or project website does not establish
an actor attachment. Bare adjacent URLs and email-shaped URL query values cannot
become a credited actor's contact. Every fact kind reports nonexhaustive coverage;
an empty list does not prove absence. Cross-field or independent-page relations
need explicit binding and original input receipts before use.

Retained mapping preserves original Unicode code-point spans before HTML/entity
rewriting. It namespaces actor and fact UUIDs by the actual input revision and
field location, keeping identical array entries and Local root accounts separate.
Proposal storage validates every nested citation against its original credited
clause and existing account/file scope. It does not turn a valid quote into
semantic identity authority.

The raw producer is `recording-credits-v4/recording-actor-evidence-v1`.
`retained-recording-credits-v2` and `recording-extraction-v4` fingerprint its output
and coordinate mapping. A recorded version is part of the evidence contract.

## Credit grammar and retained compatibility

Credit grammar v4 keeps a personal suffix such as `, IV` or `, Jr.` with the named
actor when a contact attachment follows it. It also preserves a complete email-shaped
literal inside angle brackets, including in an HTML field. The contact literal omits
the brackets; enclosing evidence preserves the original brackets, markup and entities.
Citation offsets still select the original Unicode code points. Ordinary HTML tags
and uncertain or shared contacts keep their existing treatment.

The actor-fact format and source-local ID algorithm remain `recording-actor-evidence-v1`.
The complete producer version includes the credit grammar and retained coordinate
mapping. Changing the grammar therefore changes new extraction/proposal versions
without rewriting accepted actor context or canonical identities. Existing accepted
v1 actor facts remain readable.

Completed attempts retain their exact saved replay. An unfinished recording attempt
from a previous producer ends with `processing_version_changed`; a fresh attempt
uses the current grammar. A previously published extraction remains audit data, but it cannot
newly publish accepted actor context under the current producer: admission reports
`unsupported_extraction_version`. Acceptance reproduces the original extraction
using its authenticated retained input; it must not compare an old producer receipt
against newly parsed output. Reevaluate and register a policy for the new extraction
version before enabling automatic acceptance. Do not relabel an old policy artifact.

Keep the [frozen development replay](../../../research/2026-10-17-full-normalization/training/actor-facts-replay-v1/README.md)
and its labels unchanged. Run the new producer into a separate output directory so
the original evidence and any extraction differences remain inspectable.

## Derive and authenticate one complete query

`PeopleActorProjection.Project` is the shared deterministic projection used by the
resolution processor, proposal validator and acceptance applier. It derives:

1. Ordinary query names from the primary credit and explicit aliases. An alias is
   not a provider-qualified handle merely because it resembles one.
2. `NormalizationCandidateQuery.PeopleHints`, containing typed nonunique contact
   and profile routes. Other identity domains reject these routes.
3. The comparison record, including source kind, contextual agreements and
   unresolved attachment diagnostics. It constructs no canonical external IDs.

The query supports at most 16 names, 16 contacts and 16 profiles. Unsupported or
excess terms remain in the original facts and make the incoming projection
incomplete. They cannot silently disappear from a pool used to authorize creation.
`QueryFingerprint` covers the full query, projection version and default fact
limits. Equal names with different contacts cannot share a cached candidate pool.
The applier also reproduces each saved query when it rereads candidate facts.

Before acceptance, the applier reruns the supported recorded extraction against
its original retained bytes and checked bindings. It compares every credit,
including absent actor facts, plus unresolved credits and decoder issues. This
rejects a changed lookup key, invented attachment or stripped unknown passage
even when its citations are valid. It does not read newer input heads or refresh
old catalog clocks. Unsupported historical producers remain audit-readable and
cannot newly publish context under the current producer contract.

## Accepted context belongs to contributor support

Only the guarded application writer creates accepted actor context, after the
actual accepted credit and contributor support exist. Public reconciliation
accepts no actor-fact JSON. Pending, protected and unrelated reassigned credits
cannot acquire context through this path.

Migration 48 stores the original typed payload in `recording_actor_contexts`, with
its actual credit/support UUIDs, original person, contributor, extraction,
proposal, input, application and introducing Work receipts. It also records the
accepted credit's original revision and basis. Name, contact and profile tables
are disposable lookup projections into these immutable facts. Shared keys are
allowed. `person_external_identities` retains its existing qualified-identity
contract; contacts and project accounts do not belong there.

`NormalizedPeopleService.GetRecordingActorContextsAsync(sourceUuid)` reads active
accepted source assertions on the primary. A reviewed merge preserves context
when its original person and the current credit's person resolve to the same
canonical identity. An unrelated reassignment or retraction excludes that context
from retrieval without rewriting historical evidence.

Reconciliation adds context in the accepted-credit transaction. An explicitly
complete scope can retire its own contributor support and lookup entries. Unknown,
missing, partial or failed extraction cannot clear prior support. Other
contributors, unmanaged claims and reviewed decisions remain in force. Retirement
retains the typed facts and introducing/retiring Work receipts; it does not erase
source history.

## Retrieve rivals and retain the facts used to compare them

`NormalizationCandidateService` unions accepted names, canonical qualified IDs,
source names/aliases and nonexclusive context hits before resolving redirects.
Contact and profile hits survive the ordinary name limit, including rivals that
share a crew address or account. Their route has its own limit-plus-one bound.
`ContextCandidatesTruncated` is separate from `NameCandidatesTruncated`; either
makes the candidate snapshot incomplete.

`FindPeopleFactsAsync` reads selection, full actor context, candidate epochs,
catalog clocks and owner observations in one primary repeatable-read transaction.
`PersonCandidateFacts.ActorEvidence` preserves the original support owner and
acceptance receipts alongside the typed facts. This projection is separate from
bounded artist witnesses: a few examples cannot substitute for complete actor
assertions. Reverse redirects retain original provenance.

Default actor bounds are 5,000 contexts and 4 MiB of rendered fact JSON per
candidate, plus the existing per-kind assertion and per-assertion citation bounds.
SQL omits payloads beyond the byte prefix before returning them to the client.
Every omission blocks automatic acceptance. Owner observations and the people
epoch still fence omitted facts. Context, lookup and support changes advance that
epoch even when the accepted credit row itself does not change. A prior empty
lookup therefore becomes stale when a new rival appears.

Comparison exposes nonunique contact/profile agreement separately from name,
artist and canonical-ID features. Unequal shared keys do not prove different
people. Hint overlap can make a pair comparable; it supplies no automatic matching
rule, authoritative identifier or exclusive ownership claim.

## Evaluation, publication and operation

The closed linear scorer has optional contact/profile agreement weights, both zero
by default. New scorer, retrieval and candidate-facts versions require new verified
evaluation artifacts. Existing artifacts cannot silently authorize these features.
Use independent evaluation and the agreed trusted operator contract; do not add
per-label human approval gates. Fixture policies prove mechanics in isolated test
databases and never authorize runtime acceptance.

Canonical kind and its schema/default are unchanged. The existing person-only
creation capability reviews explicit group, conflicting-kind and credited-project
evidence it cannot classify. This preserves uncertainty; it does not permanently
restrict future evaluated person/group creation or co-reference capabilities.

Identity and accepted-credit owners remain separate. Publication consumes the
original extraction/proposal stamps, complete contributor CAS, candidate epochs,
reviewed context and observed owners. Candidate write scopes lock before owners;
policy revocation and accepted writes are checked atomically. Cancellation or a
stale observation leaves prior accepted data intact. Retrying a terminal
application returns its saved receipt; changed evidence requires a new attempt.

Deploy migration 48 before this code. It adds no people, active policy or background
job. Revoke an active policy to stop new automatic writes. Preserve context history
when rolling back code; old readers ignore the additive tables and optional fields.
No public contact fields or new playback behavior are introduced.

Verification covers actual retained extraction through guarded acceptance, shared
key rivals, source aliases without common tokens, complete-query caching,
truncation and byte limits, semantic tampering with valid quotes, reviewed merges
and reassignments, unknown versus explicit clears, contributor isolation and
transaction rollback/recovery. Large-corpus query performance still needs measured
execution plans; passing fixtures alone does not establish catalog-scale cost.

## Next: independently retained primary pages

An outbound URL or research excerpt is not a runtime input receipt. Capture exact
page bytes, URL, representation and observation metadata through
`NormalizationEvidenceService` before using an external primary page.

Add a typed `ActorPrimaryPageBinding` that retains the original recording input,
page input, actor anchor, cited link and owner/actor statement, outcome and complete
input references. A supported URL transformation must connect the recording link
to the captured page. Several actors, project accounts, unattached first-person
speech or an unproved redirect remain ambiguous. Page author or uploader identity
alone does not establish the credited person.

Keep these supporting inputs separate from playback binding. Extend producer,
extractor, preparation, store and read-set validation together; no arbitrary page
may enter an extraction group. A changed page or linking input makes the original
preparation stale. Retries must not substitute a newer receipt into saved facts.

Accepted context is historical source evidence. A claim about current account
ownership also needs an evaluated maximum observation age, saved expiry and checks
at preparation and publication. Input generations detect changes, not age. Without
that contract, an old owner statement remains a historical hint.
