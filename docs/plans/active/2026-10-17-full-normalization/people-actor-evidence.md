# Retain actor evidence for global people matching

Extend people matching to use explicit aliases, attributed profiles, contacts and
supported actor context from retained recording metadata. Preserve what each
source actually claims. A shared email, handle, project page or name can retrieve
candidates and support comparison; equality does not establish canonical identity.

## Implementation status

The extraction slice now adds optional `ActorFacts` to local and retained credit
mentions. `RecordingActorEvidenceExtractor` retains explicit `aka`/`also known as`
aliases, joint roles and coordinated actors, attached email-shaped contacts,
supported profile URLs and explicit parenthesized person/group descriptions.
Unknown URL namespaces and unattributed hints remain unresolved. All fact kinds
report nonexhaustive coverage; missing facts do not prove absence. This version
attaches facts within one original role-bearing clause. Cross-field and independent
page relationships still require the binding contract below.

Retained mapping preserves raw Unicode code-point citations and namespaces actor
references by input revision and field location. Proposal storage validates nested
citations against the original credited clause and its existing account/file scope.
The raw producer is `recording-credits-v3/recording-actor-evidence-v1`;
`retained-recording-credits-v2` and `recording-extraction-v4` fingerprint its output
and coordinate mapping. Old null-omitted terminal JSON remains readable. The
solution builds, and 219 focused extraction, retained-input, evidence-storage and
people-resolution tests pass. Fresh correctness and readability reviews are clean.

Candidate queries, accepted contextual indexes, actor-aware scoring and independent
primary-page capture below remain proposed. This slice enables no policy and writes
no accepted actor context. Keep the existing [people acceptance](people-acceptance.md)
ownership, authority and publication fences. Playback and public APIs stay compatible.

## Evidence and current gaps

The [latest development discovery](../../../research/2026-10-17-full-normalization/scale/evidence/people-identity-discovery-v2.json) contains 38 family hypotheses across 207
recordings and 172 artists. Thirty-four have credited contact, profile or owner
corroboration; four retain attachment or person-versus-project ambiguity. These
are source-grounded development hypotheses, not 38 accepted global identities or
an independently evaluated launch result. Preserve each family's evidence and uncertainty.

Before this extraction increment, `RetainedCreditMention` contained a name, role,
supporting span and optional role wording. Contact text survived only in the raw
quote after removal from the display name. Typed actor facts now preserve that
context. The resolver still queries one ordinary name and projects no incoming
external identities. Accepted aliases have no handle namespace, and existing
external-ID rows do not encode provider authority or account ownership.

`NormalizationCandidateService` indexes accepted names, aliases and qualified IDs.
It cannot retrieve by nonexclusive contact/profile context. The existing accepted
`person_external_identities` table has a unique provider/type/key contract; do not
put shared contacts or project accounts there to gain an index.

## Additive extraction contracts

Add optional, null-omitted `CreditedActorFacts` to each retained credit mention.
Keep existing terminal JSON readable. The proposed container records its original
input/account/file anchor and a source-local actor UUID, with these typed members:

- `ActorAliasFact`: stable fact UUID, original alias wording, explicit same-actor
  relationship and exact relation citations. An `aka` statement connects only the
  actor expressions it names; identical strings elsewhere do not inherit that link.
- `ActorProfileFact`: stable UUID, literal URL/handle, supported provider/namespace,
  normalized lookup key, relationship and citations. Relationships distinguish an
  attributed credited-actor profile from an explicit primary-owner statement.
- `ActorContactFact`: stable UUID, contact type, lookup key, credited relationship
  and citations. A contact for a recording is different from a contact attributed
  to its named taper. Neither is a canonical person identifier.
- `ActorKindFact`: explicit `person` or `group`, supporting context and citations.
  Unknown remains null. A crew, project or organization must not become a person
  because its credited name resembles a human name.
- `ActorCoReferenceFact`: source-local actor/mention UUID endpoints, explicit
  relationship and citations. Preserve distinct coordinated actors and undivided
  compound expressions. Group IDs are not canonical person UUIDs.

Attach a fact only when its evidence connects it to that credited actor. A nearby
uploader, generic contact link, roster member or website is insufficient. A musician
roster can support an expressed actor's kind without proving that the musician
made the recording or matches an existing person.

Include `ActorEvidenceCoverage(Kind, Complete, Fields)` for each fact kind. An empty
list with complete cited field coverage differs from an unrun or uncertain extractor.
Absent coverage means unknown. The actor UUID and co-reference endpoints are scoped
to the original retained account and extractor version; they never identify a person globally.

Use a cohesive actor-evidence extractor alongside the current role parser. It
consumes original retained fields and role-bearing anchors, and emits facts plus
unresolved attachments. A name-normalization pass cannot create relation evidence.
Retain exact Unicode code-point spans before HTML/entity rewriting; cite original
attributes or raw text rather than invented DOM-text offsets. Do not copy all
contacts in a recording onto every actor.

Use closed, versioned provider URL/handle normalizers. Unsupported URL forms retain
literal wording and unknown qualification. Preserve opaque key case unless that
provider's documented contract permits normalization. Treat contact lookup keys as
internal supporting data; avoid adding public contact fields or logging their values.

## One shared candidate projection

Add a deterministic `PeopleActorProjection` used by the resolution processor,
proposal-store validator and acceptance applier. Its responsibilities are:

1. Build a bounded global `NormalizationCandidateQuery` from the primary name and
   supported aliases, retaining the facts that justify each added query term.
2. Project matching names, qualified handles, kind and contextual features from
   those same authenticated facts. Bare aliases remain ordinary names.
3. Reproduce the query and matching projection during validation. A caller cannot
   introduce an extra profile/contact key through a supplied candidate snapshot.

Add optional `PeopleCandidateHints` to the query for typed profile/handle and
contact lookups. Other domains reject that member. Keep `ExternalIdentities` for
its existing qualified-ID contract; a contact match must not set `IdentifiesEntity`,
`ExclusiveWithinEntity` or an authoritative-ID rule.

The processor currently caches by name. Replace that key with the entire frozen
query fingerprint, including names, hints, qualification, limits and projection
version. Two equal names with different profile evidence require separate pools.
The current single-name/zero-ID validation must use the shared projection too.

Union name, canonical-ID and contextual hits before redirect resolution. Context
lookup values are nonunique: return rivals sharing a crew contact or profile.
Bound each retrieval route with limit-plus-one and retain its reason. Add explicit
context-pool truncation to `NormalizationCandidateSet`; `Complete` must account
for every route and fact limit. Never drop excess aliases or hints silently, or
interpret a bounded empty pool as proof that a new identity should be created.

## Accepted context and indexed retrieval

Store accepted actor context with the recording's contributor support, not as an
implicit canonical-person merge. Add a specialized typed assertion set associated
with the actual stored credit/support UUID, extraction receipt, extractor version
and introducing Work receipt. Keep exact citations and the stated relationship.
A coalesced credit can retain independent facts from several contributors.

Reuse the typed actor-fact payload in the immutable extraction checkpoint. Add a
closed lookup projection referencing its extraction/proposal/fact UUIDs and the
actual support UUID, with introducing/retiring Work receipts. Index kind, namespace
and lookup key; join current support and accepted credits to the resolved person.
Validate each projection against its referenced typed fact; the index is not matching
authority. No second evidence store or cross-domain assertion framework is needed.

Reconciliation adds these facts in the accepted-credit transaction after the real
stored credit UUID is known. Pending/protected claims do not create accepted
retrieval support. Retiring one contributor removes only that support's current
context. Other contributors, unmanaged support, reviewed facts and history remain.
Corrections replace only explicitly complete actor-evidence kinds/fields; missing,
partial or failed extraction cannot clear prior context. Retain the original and
superseding Work receipts. Unsupported old extractor versions cannot silently
supply a newly authoritative feature.

Read all eligible actor assertions in the existing people-facts repeatable-read
snapshot. Return typed facts with original support owner, assertion UUID, revision,
basis, extractor version and full citations. Expose them as additive `PersonCandidateFacts.ActorEvidence`
contexts keyed by actual support, contributor and extraction/fact UUIDs, retaining
the typed actor facts and provenance. Preserve reverse redirects and all consumed
owner observations. Witness examples cannot replace complete assertion
coverage; an assertion cap must make automatic comparison incomplete.

These are accepted historical source assertions, not proof of current account
ownership. If a policy needs a current primary-owner statement, require the fresh
independently retained page binding below. Do not label an old citation current
merely because its accepted person or credit still exists.

## Independently retained primary pages

A research excerpt or outbound link is not a runtime input receipt. Before using
an external primary page, capture its exact bytes, representation, URL and observation
metadata through `NormalizationEvidenceService`.

Add typed `ActorPrimaryPageBinding`: original recording input, page input, actor
anchor, cited linking statement, cited owner/actor statement, binding outcome and
complete consumed input references. A supported URL transformation must connect
the retained recording link to the captured page. A page mentioning several actors,
a project account, a redirect without retained proof or an unattached first-person
statement remains ambiguous. Do not infer a person from the page author or uploader.

Represent these as explicit actor-evidence supporting inputs, separate from playback
binding. Current recording preparations allow only their defined recording inputs
and Local root support; extend that contract deliberately. The producer, store,
extractor and read-set validator must retain the exact page revision and original
resource generation alongside the recording input. No arbitrary supporting page
may enter a group. A changed page or parent-link input makes admission/publication
stale; retries cannot substitute a newer page receipt into saved facts.
A generation does not detect age. Historical statements apply at observation time;
any current-ownership feature also needs an evaluated maximum observation age in
the policy, with its saved expiry checked at preparation and publication. Without
that contract the statement remains a historical hint, not current ownership.

## Scoring, publication and compatibility

Extend `PersonMatchRecord` additively with typed contextual facts. Retain all pair
features and whole-pool rival decisions. Score explicit alias agreement, attributed
profile agreement, contact agreement, supported kind and artist/role context
separately. A weak hint cannot become a verified entity ID through projection.
Conflicting person/project evidence and attachment uncertainty remain reviewable.

New features require new independent evaluation and a registered artifact that
names their versions and applicability. Existing development authorization permits
Codex judgments; it does not make uncertain hypotheses truth. Use the agreed trusted
operator/evaluation contract, without introducing per-label human approval gates.
Creation and co-reference remain separately evaluated capabilities. Fixture policies
exercise mechanics in isolated databases and do not activate runtime acceptance.

Keep identity and accepted-credit owners separate. Reuse the applier's original
extraction/proposal observations, complete contribution CAS, candidate epochs,
reviewed-context observations and atomic active-policy check. Declare and lock the
people candidate write scope before owner locks; advance its epoch within the
accepted-write transaction. New lookup rows must be fenced even when a prior query
found no candidates. Include every consumed
support owner and page input in the appropriate complete read set. Do not refresh
owner metadata around old actor facts.

## Implementation and verification

Change extraction models/parser/retained remapping first, then the shared projection,
query validation and full-query cache. Add the accepted support schema and indexes,
people-facts projection and guarded reconciliation writes together. Extend primary
page capture/binding before enabling that feature. Finally extend the fixed scorer
and policy artifact versions, and evaluate against complete candidate sets.

Bump the actor extractor, recording extraction, query projection/retrieval,
candidate-facts and scorer versions where their behavior changes. Pin them in saved
preparations and policy artifacts; retain old terminal replay. The migration adds
no accepted identities, default policy or background job. Revoke a policy to stop
new automatic writes; retain evidence and correct accepted data through existing
review/reconciliation contracts.

Verify explicit aliases with no shared name tokens; equal names with different
contacts; shared crew contacts and project profiles; unsupported URL forms; roster
kind without taper attribution; disjoint actors in one field; and unknown attachment.
Verify whole-query caching, rival/truncation behavior, reverse redirects, changed
supporting pages, source corrections, reviewed exclusions and rollback. Verify that
retiring one contributor removes its lookup support while another remains, and that
no contact/profile equality allocates, merges or reclassifies a canonical person.
