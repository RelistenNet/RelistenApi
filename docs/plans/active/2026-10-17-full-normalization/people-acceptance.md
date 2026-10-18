# Automatically resolve people and publish recording credits

Implement an evaluated resolver that links credits to existing people, creates
evidenced identities or retains claims for review, through accepted people and
credit writers. Automatic matching across artists is the outcome.

This is the next implementation contract. The [proposal processor](people-resolution.md)
already retains complete extraction contributors, candidate facts and reviewed
context. Its decisions remain policy-free, with `AutomaticAcceptanceAuthorized`
always false. Existing immutable proposal receipts keep that meaning.

## Ownership and entrypoints

Keep identity ownership at `(people, person, personUuid)`, accepted credits at
`(people, recording, sourceUuid)`, and proposals at
`(people, recording_resolution, sourceUuid)`. Use existing Work admission,
publication, observations, contribution receipts and review operations. Do not
add a second scheduler, work registry or general policy framework.

Add these services and models; the signatures below are proposed public contracts:

```csharp
// Trusted operator entrypoints; operator identity comes from the command host.
PeopleResolutionPolicyService.RegisterEvaluatedPolicyAsync(
    PeopleAcceptancePolicyArtifact artifact,
    PeopleAcceptanceEvaluationReceipt evaluation, string authenticatedOperator, CT);
PeopleResolutionPolicyService.ApplyDecisionAsync(
    PeopleAcceptancePolicyDecision decision, string authenticatedOperator, CT);
PeopleResolutionPolicyService.ReadActiveAsync(CT); // active artifact + owner observation

NormalizationPeopleResolutionApplier.ApplyRecordingAsync(
    Guid applicationAttemptUuid, Guid resolutionAttemptUuid, CT);
NormalizationPeopleResolutionApplier.GetApplicationAsync(Guid applicationAttemptUuid, CT);
```

`ApplyRecordingAsync` returns a `PeopleAcceptanceApplication`; the reader returns
that record or null. Callers supply no winners, candidate facts, policy object or
accepted credits. The service loads and authenticates the original proposal.
A repeated application UUID resumes its immutable request or returns its terminal
receipt. Binding that UUID to another proposal conflicts.

Use one small application checkpoint with the immutable header, initial plan,
per-stage preparations, identity publication receipts and
terminal result. Existing Work receipts remain authoritative for publication.
The result records per-claim actions and credit publication receipts, distinguishing
`Applied`, `Review`, `Unresolved` and `Stale`, including partial progress. It must
not describe a pending reviewed claim as an accepted credit.

## Evaluated policy, inactive by default

`PeopleAcceptancePolicyArtifact` contains its UUID, version and content hash;
fixed scorer implementation and exported parameters; supported input forms;
retrieval, candidate-facts, extraction and credit-context versions; match threshold,
runner-up margin and rejection rules. Creation, actor-kind classification and
within-recording co-reference are separate capabilities with separate evidence.
An artifact can enable linking while leaving creation unavailable.

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

## Set-level decisions

Add pure `PeopleResolutionPolicyEvaluator.Evaluate` with the complete authenticated
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

## Publish additions and retire only complete support

Add `NormalizedPeopleService.PublishResolutionIdentityAsync(workUuid, applicationAttemptUuid,
personUuid, ct)` and `PublishResolutionCreditsAsync(workUuid, applicationAttemptUuid, ct)`.
Both return `NormalizationPublicationResult` and load the saved stage. Reuse existing identity,
credit and reconciliation helpers within the publication transaction; do not call
another transactional Work method from its callback or introduce parallel SQL writers.
Both verify the exact Work ticket and policy authority, never a caller acceptance bit.

Extend contributor reconciliation to distinguish evidenced additions from complete
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
Keep the existing complete-scope reconciliation API compatible.

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

A crash can leave an evidenced identity without a credit. Resume its saved child
Work UUID and publication receipt, then complete the fresh credit preparation.
With several creation groups, fully reread and reevaluate before each later
creation too: the preceding creation already changed the candidate universe.
Never allocate another identity on retry or delete the first identity because a
later credit stage is stale. No multi-owner atomic publication mechanism is needed.

## Evidence required before activation

Current development labels concern extraction: 100 fields across 78 Archive-configured
artists, with cross-artist identity resolution unlabeled. Parser/Luna agreement on
names and roles cannot establish person identity, safe creation or actor kind.

Build real complete-candidate tasks covering cross-artist aliases, namesakes, hard
negatives, handles and organizations, unseen identities, genuine new identities,
person/group classification and within-recording co-reference. Group duplicate
recordings, known families and aliases before splits. Follow the
[evaluation strategy](evaluation-strategy.md) for held-out artists, identities,
later recordings and unfamiliar formats. Measure retrieval recall and whole-pool
match/create errors, not pair accuracy alone. Thresholds come from that evidence.

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
