# Automatically resolve people and publish recording credits

Implement an evaluated resolver that links credits to existing people, creates
evidenced identities or retains claims for review, through accepted people and
credit writers. Automatic matching across artists is the outcome.

The [proposal processor](people-resolution.md) retains complete extraction
contributors, candidate facts and reviewed context. `NormalizationPeopleResolutionApplier`
connects those proposals to guarded accepted-data publication. Proposal decisions remain policy-free, with `AutomaticAcceptanceAuthorized`
always false. Existing immutable proposal receipts keep that meaning.

## Ownership and entrypoints

Keep identity ownership at `(people, person, personUuid)`, accepted credits at
`(people, recording, sourceUuid)`, and proposals at
`(people, recording_resolution, sourceUuid)`. Use existing Work admission,
publication, observations, contribution receipts and review operations. Do not
add a second scheduler, work registry or general policy framework.

The services expose these public contracts (`CT` denotes cancellation):

```csharp
// Trusted operator entrypoints; operator identity comes from the command host.
PeopleResolutionPolicyService.RegisterEvaluatedPolicyAsync(
    PeopleAcceptancePolicyArtifact artifact,
    PeopleAcceptanceEvaluationReceipt evaluation, string authenticatedOperator, CT);
PeopleResolutionPolicyService.ApplyDecisionAsync(
    PeopleAcceptancePolicyDecision decision, string authenticatedOperator, CT);
PeopleResolutionPolicyService.ReadActiveAsync(CT); // active artifact + owner observation
PeopleResolutionPolicyService.ReadStateAsync(CT); // nullable artifact + real registry owner

NormalizationPeopleResolutionApplier.ApplyRecordingAsync(
    Guid applicationAttemptUuid, Guid resolutionAttemptUuid, CT);
NormalizationPeopleResolutionApplier.GetApplicationAsync(Guid applicationAttemptUuid, CT);
```

`ApplyRecordingAsync` returns a `PeopleAcceptanceApplication`; the reader returns
that record or null. Callers supply no winners, candidate facts, policy object or
accepted credits. The service loads and authenticates the original proposal.
A repeated application UUID resumes its immutable request or returns its terminal
receipt. Binding that UUID to another proposal conflicts.

An application groups an immutable request, initial plan, separately saved stage
preparations, publication receipts and terminal result. Each plan and stage is
an independently bounded checkpoint; the application is not one size-limited
row. Existing Work receipts remain authoritative for publication.
The result records per-claim actions and credit publication receipts, distinguishing
`Applied`, `Review`, `Unresolved` and `Stale`, including partial progress. It must
not describe a pending reviewed claim as an accepted credit.

## Evaluated policy, inactive by default

`PeopleAcceptancePolicyArtifact` contains its UUID, version and content hash;
fixed scorer implementation and exported parameters; supported input forms;
retrieval, candidate-facts, extraction and credit-context versions; match threshold,
runner-up margin and rejection rules. Creation, actor-kind classification and
within-recording co-reference are separate capabilities with separate evidence.
An artifact can enable linking while leaving creation unavailable. The closed
creation classifier currently covers one supported full-name person claim; handles,
groups and co-reference need further evidenced capabilities.

`PeopleAcceptanceEvaluationReceipt` pins the artifact hash, labeled dataset and
split hashes, adjudication record, evaluator version, measured errors and trusted
approval. Registration checks those references and supported versions. A name-role
extractor output or a caller-built `EvaluationBackedIdentityPolicy` cannot attest
to its own matching accuracy. The trusted host must authenticate the approver;
a string in a request body is not authentication.

Registration is immutable and inactive. Activation and revocation use
`PeopleAcceptancePolicyDecision`: operation UUID, policy subject UUID, expected
editorial revision, target artifact UUID or null, and reason. Register one fixed
Work owner `(people, acceptance_policy, policyKeyUuid)`. Use its existing review
ledger with accept/replace/retract actions and store the active artifact reference
in the same transaction. Registration does not imply approval to activate.

Every identity and credit publication consumes the original policy owner
observation. Inside the Work callback, reload the active artifact reference while
that owner is locked and require the prepared artifact. Policy review takes the
same owner lock. Revocation before publication makes the preparation stale;
revocation waiting behind publication takes effect after that publication. Do not
add an independent policy lock after domain locks or rely on a preflight flag.

Tests use explicitly identified fixture artifacts in isolated databases. No
migration, startup path or fixture registration activates a runtime policy.
Production activation requires adequate independent evaluation and verified authority,
not a new human approval for every label or policy revision. Record reviewer
provenance and uncertainty; Codex may supply development judgments.

An inactive registry is still an observable dependency. `ReadStateAsync` establishes
one fixed empty Work owner, without activating a policy, then reads its nullable
artifact and exact owner stamp together. First activation can therefore make an
earlier inactive-policy decision pending. Replaying the bootstrap never restores an
old configuration or overwrites a review.

## Set-level decisions

Use pure `PeopleResolutionPolicyEvaluator.Evaluate` with the complete authenticated
claim, candidate facts, reviewed context and registered artifact as inputs. Return
`PeopleAcceptanceClaimDecision`: proposal UUID, action, reason, all candidate scores
and rejection reasons, winner and margin, policy/evaluation references, and optional
creation plan. Preserve the original comparisons as diagnostic features; similarity
is not a probability or publication authority.

Rank the whole candidate pool. A clear winner among rivals can be matched when the
evaluated threshold, margin and contradiction checks pass. An irrelevant rejected
rival does not automatically veto a winner. Ties and relevant conflicts require
review. Incomplete retrieval or facts, unsupported versions and missing evidence
remain unresolved. An empty pool alone cannot authorize creation.

A creation plan supplies a stable person UUID, explicit `person` or `group` kind,
evidenced display name and co-reference group UUID. Never use the default
`CanonicalPerson.Kind` to classify an unknown actor. Equal spelling across roles
or contributors does not prove co-reference; unresolved grouping requires review.
Allocate and checkpoint creation IDs once, before any accepted writes.

Preserve reviewed exclusions and changed-field guards, including original resource,
locator and span correspondence. A file credit with unknown track association can
still receive identity comparisons, but cannot become a recording-wide credit.
Keep unsupported names, unresolved prose and decoder issues visible.

## Authenticate facts before admission

A published proposal is evidence of completed computation, not authority to accept
caller-supplied candidate JSON. Require an actual published, supported-version
proposal and its complete published extraction. Derive their original owner
observations from saved tickets and publication UUIDs, never newer owner metadata.

Load exact retained input revisions and recheck their original checked bindings.
For initial authentication, reread facts with the saved queries/bounds and call
`ReadCreditContextAsync` in the preparation phase. Require complete fact payloads
and all original read-set stamps to agree with the proposal; a changed snapshot
requires new resolution. Validate exact claim/evidence correspondence as well as
resource membership. Freeze the resulting application plan before Work admission.

Consume original input revisions, catalog artists, source identifiers/UUIDs,
mapping registry, candidate epochs, redirect/identity owners and reviewed-context
owners. Keep extraction and proposal owners as causal dependencies and compared
owners as observations. Consume winning person owners as required by the accepted
writer. The people epoch fences an empty pool and a newly arriving rival.
Conflicting original generations cannot be replaced with a maximum or current value.

Read the accepted recording owner's complete contribution membership and CAS.
Use `BeginContributedBatchAsync`, retaining recognized contributors' complete input
sets and explicit tombstones. Unexpected contributors or dependencies remain
preserved and prevent an unsupported complete replacement. Never infer that the
initiating extraction is the owner's entire input set. Callback validation checks
the exact saved application and complete ticket union, not a subset of dependencies.

### Recover configured inputs after credit review

Reviewing a taper credit advances the accepted recording owner's desired and
editorial revisions without replacing its contributor bindings. A later clear
transferrer action must still be able to use that configuration. Do not require
the earlier admission ticket to have today's desired generation.

These bindings say which retained resources, dependencies and fact scopes each
input producer owns. Match every producer's admitted revision, including
unchanged peers and retained empty headers.

Recognize only a prior-or-current admission under the same current Work policy
(the application processing version), with the complete admitted contributor
revision vector and exact binding allocation.
Authenticate its actual contributed Work request and complete ticket; matching
keys and revision headers alone do not establish ownership. Admission may
precede ticket attachment, so a saved attached ticket or published result cannot
be a prerequisite for recovery. Ambiguous ownership remains unsupported.

The new application reads current facts and admits its own original observations.
It preserves the reviewed taper's UUID, state, revision and evidence while
independently refreshing the transferrer's support. It does not restamp or
republish the old stage. Earlier terminal failures retain their results; retry
with a fresh proposal and application ID after this correction. No migration is
required.

## Publish additions and retire only complete support

An inactive or unsupported policy, or a supported evaluation with no accepted credit
actions, publishes a decision-only stage through
`NormalizedPeopleService.PublishResolutionDecisionAsync(workUuid, applicationAttemptUuid, ct)`.
This stage records an authenticated Work acknowledgment and receipt; it writes no
accepted identity or credit and advances no candidate epoch. Source, candidate,
editorial and policy changes make the recording owner pending again. A historical
application keeps its original result; reprocessing uses a new application and a
current proposal when the original facts changed.

Decision-only stages preserve independent contributors. Their coordination facts
come from the owner's last published immutable ticket and must cover its exact
current membership. The full original resource generations remain required even
when changed bytes later return to the same input revision. Unpublished or stale
peer configurations remain pending and produce a stale application. A later
acknowledgment drops removed peer keys rather than inheriting them into this
resolver's contribution. Positive credit reconciliation retains its stricter
ownership checks.

The additive plan and stage fields require the new applier version. Terminal older
applications replay unchanged; unfinished older applications stop with
`processing_version_changed`. No schema migration is needed. Pause accepted people
writers before binary rollback to an implementation that cannot interpret these
decision-only stages, and retain all plans, receipts and accepted history.

Add `NormalizedPeopleService.PublishResolutionIdentityAsync(workUuid, applicationAttemptUuid,
personUuid, ct)` and `PublishResolutionCreditsAsync(workUuid, applicationAttemptUuid, ct)`.
Both return `NormalizationPublicationResult` and load the saved stage. Reuse existing identity,
credit and reconciliation helpers within the publication transaction; do not call
another transactional Work method from its callback or introduce parallel SQL writers.
Both verify the exact Work ticket and policy authority, never a caller acceptance bit.

Guarded contributor reconciliation distinguishes evidenced additions from complete
retirement scopes. Existing `RecordingCreditContributorReplacement.CompleteScopes`
continues to authorize withdrawal only when every extracted mention in that
role/track scope has a successful corresponding credit and no unresolved coverage.
A safe match can add managed support beside an ambiguous actor without claiming
that the whole scope is complete. Validate each addition against its exact original
proposal, contributor, role, wording, evidence and checked file scope.

Reuse support retirement and reviewed-field guards. Review/unresolved claims keep
last accepted support; another contributor, unmanaged support and reviewed decisions
remain independent. Do not route additions through append-style `PublishAsync`:
its unmanaged support cannot later be withdrawn by contributor reconciliation.
Record `validated_match` basis and policy audit references, not human-review status.
The existing complete-scope API remains compatible. Saved scope coverage cites
current fields: explicit null/empty values can clear prior managed roles; missing
fields or unresolved prose cannot. Read prior support with the context owner/epoch.

## Creation requires two publication stages

A recording owner cannot create a person. Before each creation stage, checkpoint
its immutable facts/readsets, evaluated decision, policy observation, stable person
UUID and child Work UUID. Publish under that person owner with an evidenced
display-name alias and original raw/extraction/proposal proof. Reuse the domain
writer's candidate write scopes and lock order.

Creation changes the people epoch. Before credit publication, perform a full new
candidate and reviewed-context read, rerun the set-level decision and save a
separate credit preparation; later-stage facts need not equal the initial proposal.
Keep original raw inputs and extraction/proposal
receipts unchanged. Retain the initial creation proof as history; do not carry its
obsolete candidate epoch into the new credit read set or attach a new epoch to old
candidate facts. The creation receipt identifies the intended claim correspondence;
it does not permit ignoring a new rival or intervening review.

A creation receipt anchors the original claim after the full reread; it supplies no
permission to ignore a rival. A crash can leave an identity without a credit. Resume its saved child
Work UUID and publication receipt, then complete the fresh credit preparation.
With several creation groups, fully reread and reevaluate before each later
creation too: the preceding creation already changed the candidate universe.
Never allocate another identity on retry or delete the first identity because a
later credit stage is stale. No multi-owner atomic publication mechanism is needed.

## Evidence required before activation

The [credit extraction drafts](../../../research/2026-10-17-full-normalization/training/credits-development-v1/README.md) cover 100 fields across 78 Archive-configured artists. The separate
[people development bundle](../../../research/2026-10-17-full-normalization/training/people-development-v1/README.md) adds 168 actor-kind and identity questions across 137 artists. Its supported
cross-artist positive examples concern one Aadam Jacobs project owner; the labels
remain pending adjudication. These drafts and parser/Luna agreement cannot establish
broad person-matching accuracy, safe creation or actor-kind classification.

Build real complete-candidate tasks covering cross-artist aliases, namesakes, hard
negatives, handles and organizations, unseen identities, genuine new identities,
person/group classification and within-recording co-reference. Group duplicate
recordings, known families and aliases before splits. Follow the
[evaluation strategy](evaluation-strategy.md) for held-out artists, identities,
later recordings and unfamiliar formats. Measure retrieval recall and whole-pool
match/create errors, not pair accuracy alone. Thresholds come from that evidence.

## Checkpoint storage and compatibility

The [people checkpoint contract](checkpoint-format.md) defines private
`people-checkpoint-v2` storage for plans and stages, alongside resolution
preparations and batches. It shares exact repeated evidence within each value,
retains every original observation and preserves expanded public DTOs, semantic
hashes and separate tickets. The 32 MiB limit per stored checkpoint is unchanged.
Existing winners and replay are checked under the row lock before new-write
admission; state-only publication does not reapply that admission limit.

Historical expanded payloads and compact payloads can coexist. Decode each before
checking its original contributor headers and complete facts. A later stage's
fresh derived facts remain separate from earlier preparations. The current binary
reads both formats and writes compact checkpoints, with no format feature flag.
Pause people writers, deploy compatible binaries to every reader and resume path,
then resume processing. Before rollback, pause writers and use a reader-compatible
release; never rewrite immutable checkpoints or refresh their clocks to make an
older binary accept them.

## Verification, rollout and recovery

Test public application, domain writers and Work boundaries: a winner among rivals,
ties, incomplete pools, empty-pool creation refusal, explicit kinds and co-reference;
forged facts or policy receipts; changed input, rival, redirect or review before
admission/publication; revocation racing publication; unknown file scope; safe
managed additions beside ambiguity and later contributor-only retirement.

Test interrupted creation and exact resume, a new rival between creation and the
fresh credit read, concurrent preparation winners, cancellation, transaction rollback
midway through support updates and old terminal replay. Failed publication must
retain computed audit output, prior accepted credits and the previous watermark.
No accepted credit or terminal receipt may survive a rolled-back callback alone.

Implement and exercise the full path with fixture policies first. Run runtime work
in shadow mode until verified evaluation supports narrowly scoped activation.
Observe coverage, review reasons and audited errors by artist/input form. Revocation
stops new automatic writes and preserves IDs, evidence, receipts and accepted data;
correct accepted mistakes through existing reviewed decisions. Fixtures do not
complete the automatic-normalization goal.


Apply migration 46 before using these services. It adds policy artifacts and
application/stage receipts; it activates no policy. API and worker dependency
registration starts no processing or schedule. Existing public read routes and
playback identifiers do not change.

On binary rollback, stop acceptance callers and retain the new tables, policies,
Work receipts and accepted identities. Do not delete an identity left by an
interrupted application or discard its creation receipt. Resume with the same
application UUID on a compatible processor; a changed processing version retains
history and returns an explicit unresolved outcome.

The combined focused run passes 179 people, composition and venue cases. People checks cover the new negative-decision refresh path, inactive/unsupported policy changes, interrupted acknowledgment, independent peer removal, A-to-B-to-A input generations and older-version replay, alongside accepted credit and creation behavior. The solution builds with existing package advisory warnings. See the [overview](README.md#progress) for the latest broader verification. These fixtures use synthetic evaluated policies in disposable databases; no runtime matching policy is active.
