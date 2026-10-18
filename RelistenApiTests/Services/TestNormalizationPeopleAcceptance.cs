using System.Text.Json;
using Dapper;
using FluentAssertions;
using Npgsql;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
using Relisten.Normalization;
using CatalogSource = RelistenApiTests.Services.NormalizationDomainFixture.CatalogSource;

namespace RelistenApiTests.Services;

/// <summary>
/// Exercises accepted recording credits through retained extraction, complete candidate
/// proposals and the guarded application boundary. Policies here are isolated test fixtures,
/// not evaluated production artifacts.
/// </summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed partial class TestNormalizationPeopleAcceptance
{
    private const string Operator = "acceptance-fixture-operator";
    private NormalizationDomainFixture fixture = null!;
    private NormalizedPeopleService people = null!;
    private NormalizationRecordingProposalService extractions = null!;
    private NormalizationRecordingProcessor extractor = null!;
    private NormalizationPeopleResolutionStore resolutions = null!;
    private NormalizationPeopleResolutionProcessor resolver = null!;
    private NormalizationCandidateService candidates = null!;
    private PeopleResolutionPolicyService policies = null!;
    private PeopleAcceptanceApplicationStore applications = null!;
    private NormalizationPeopleResolutionApplier applier = null!;
    private readonly NormalizationPeopleResolutionDecisionService baseline = new(new());

    [OneTimeSetUp]
    public void Start()
    {
        fixture = new();
        people = new(fixture.Db, fixture.Work);
        extractions = new(fixture.Db);
        var evidence = new NormalizationEvidenceService(fixture.Db);
        extractor = new(
            new(fixture.Db, evidence),
            evidence,
            new(fixture.Db),
            new(),
            new(new(), new()),
            fixture.Work,
            extractions,
            new(fixture.Db)
        );
        resolutions = new(fixture.Db);
        candidates = new(fixture.Db);
        resolver = new(people, extractions, candidates, baseline, resolutions, fixture.Work);
        policies = new(fixture.Db, fixture.Work, allowFixturePolicies: true);
        applications = new(fixture.Db);
        applier = new(
            fixture.Db,
            resolutions,
            extractions,
            candidates,
            new(fixture.Db),
            people,
            baseline,
            new(),
            policies,
            applications,
            fixture.Work
        );
    }

    [OneTimeTearDown]
    public async Task Stop()
    {
        if (fixture == null)
        {
            return;
        }
        try
        {
            if (await policies.ReadActiveAsync() != null)
            {
                await SetActivePolicy(null);
            }
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Test]
    public async Task CompletePoolUsesArtistContextToSelectOneNamesakeAndReplaysTheReceipt()
    {
        var name = UniqueName();
        var winner = await Person(name);
        var rival = await Person(name);
        var source = await Source(name);
        await SeedArtistCredit(winner, name, source.ArtistId);
        await ActivatePolicy(matchThreshold: 2.5);
        var resolution = await Resolve(source);
        var attemptUuid = Guid.NewGuid();

        var application = await applier.ApplyRecordingAsync(attemptUuid, resolution.AttemptUuid);

        application
            .Result!.Disposition.Should()
            .Be(
                PeopleAcceptanceDisposition.Applied,
                "application audit: {0}",
                ApplicationAudit(application)
            );
        var decision = application.Plan!.Decisions.Single();
        decision.Action.Should().Be(PeopleResolutionAction.MatchExisting);
        decision.PersonUuid.Should().Be(winner);
        decision
            .Candidates.Select(candidate => candidate.PersonUuid)
            .Should()
            .Contain([winner, rival]);
        var credit = (await people.ReadCreditContextAsync(source.SourceUuid))
            .Credits.Single()
            .Credit;
        credit.PersonUuid.Should().Be(winner);
        credit.Basis.Should().Be("validated_match");
        credit.Evidence.Should().ContainSingle();
        (await applier.ApplyRecordingAsync(attemptUuid, resolution.AttemptUuid))
            .Result!.Replayed.Should()
            .BeTrue();
        (await people.ReadCreditContextAsync(source.SourceUuid)).Credits.Should().ContainSingle();
        (await applier.GetApplicationAsync(attemptUuid))!
            .Plan.Should()
            .BeEquivalentTo(application.Plan);
    }

    [Test]
    public async Task EquallySupportedNamesakesRemainForReviewWithoutPublishingCredits()
    {
        var name = UniqueName();
        await Person(name);
        await Person(name);
        await ActivatePolicy();
        var source = await Source(name);
        var resolution = await Resolve(source);

        var application = await applier.ApplyRecordingAsync(Guid.NewGuid(), resolution.AttemptUuid);

        application.Result!.Disposition.Should().Be(PeopleAcceptanceDisposition.Review);
        application.Plan!.Decisions.Single().Reason.Should().Be("ambiguous_candidate_margin");
        (await people.ReadCreditContextAsync(source.SourceUuid)).Credits.Should().BeEmpty();
    }

    [Test]
    public async Task EmptyPoolDoesNotInventActorKindForAnUnqualifiedHandle()
    {
        var name = "taper" + RandomWord();
        await ActivatePolicy(allowCreation: true);
        var source = await Source(name);
        var resolution = await Resolve(source);

        var application = await applier.ApplyRecordingAsync(Guid.NewGuid(), resolution.AttemptUuid);

        application.Result!.Disposition.Should().Be(PeopleAcceptanceDisposition.Review);
        application.Plan!.Decisions.Single().Reason.Should().Be("actor_kind_unresolved");
        (await people.ReadCreditContextAsync(source.SourceUuid)).Credits.Should().BeEmpty();
        (await candidates.FindPeopleFactsAsync(new(IdentityMatchingDomain.People, [name], [])))
            .Facts.Should()
            .BeEmpty();
    }

    [Test]
    public async Task CreationPublishesAnEvidencedIdentityThenCreditsFromFreshFactsAndReplaysOnce()
    {
        var name = UniqueName();
        await ActivatePolicy(allowCreation: true);
        var source = await Source(name);
        var resolution = await Resolve(source);
        var attemptUuid = Guid.NewGuid();

        var application = await applier.ApplyRecordingAsync(attemptUuid, resolution.AttemptUuid);

        application
            .Result!.Disposition.Should()
            .Be(
                PeopleAcceptanceDisposition.Applied,
                "application audit: {0}",
                ApplicationAudit(application)
            );
        var creation = application.Plan!.Decisions.Single().Creation!;
        creation.Kind.Should().Be("person");
        application.Stages.Should().HaveCount(2);
        application.Result.Publications.Should().HaveCount(2);
        var identityStage = application.Stages.Single(stage => stage.PersonUuid.HasValue);
        var creditStage = application.Stages.Single(stage => !stage.PersonUuid.HasValue);
        identityStage.Facts.Inputs.Single().Claims.Single().Candidates!.Facts.Should().BeEmpty();
        creditStage
            .Facts.Inputs.Single()
            .Claims.Single()
            .Candidates!.Facts.Should()
            .Contain(fact => fact.Hit.Uuid == creation.PersonUuid);
        creditStage
            .Facts.ExtractionObservation.Should()
            .Be(identityStage.Facts.ExtractionObservation);
        (await people.ReadCreditContextAsync(source.SourceUuid))
            .Credits.Single()
            .Credit.PersonUuid.Should()
            .Be(creation.PersonUuid);

        var replay = await applier.ApplyRecordingAsync(attemptUuid, resolution.AttemptUuid);

        replay.Result!.Replayed.Should().BeTrue();
        replay.Plan.Should().BeEquivalentTo(application.Plan);
        (await candidates.FindPeopleFactsAsync(new(IdentityMatchingDomain.People, [name], [])))
            .Facts.Should()
            .ContainSingle()
            .Which.Hit.Uuid.Should()
            .Be(creation.PersonUuid);
        (await people.ReadCreditContextAsync(source.SourceUuid)).Credits.Should().ContainSingle();
    }

    [Test]
    public async Task InterruptedApplicationResumesItsPublishedIdentityBeforeAddingTheRecordingCredit()
    {
        var staged = await PrepareCreation();
        var publication = await people.PublishResolutionIdentityAsync(
            staged.Stage.WorkUuid,
            staged.Application.AttemptUuid,
            staged.PersonUuid
        );
        publication.Status.Should().Be(NormalizationPublicationStatus.Published);
        var interrupted = (await applier.GetApplicationAsync(staged.Application.AttemptUuid))!;
        interrupted.Result.Should().BeNull();
        interrupted.Stages.Single().Receipt.Should().NotBeNull();
        (await people.ReadCreditContextAsync(staged.Source.SourceUuid)).Credits.Should().BeEmpty();

        var resumed = await applier.ApplyRecordingAsync(
            staged.Application.AttemptUuid,
            staged.Application.ResolutionAttemptUuid
        );

        resumed
            .Result!.Disposition.Should()
            .Be(
                PeopleAcceptanceDisposition.Applied,
                "application audit: {0}",
                ApplicationAudit(resumed)
            );
        resumed.Stages.Should().HaveCount(2);
        resumed.Result.Publications.Should().HaveCount(2);
        resumed
            .Result.Publications.Should()
            .Contain(receipt =>
                receipt.WorkUuid == staged.Stage.WorkUuid && receipt.PersonUuid == staged.PersonUuid
            );
        (
            await candidates.FindPeopleFactsAsync(
                new(IdentityMatchingDomain.People, [staged.Name], [])
            )
        )
            .Facts.Should()
            .ContainSingle()
            .Which.Hit.Uuid.Should()
            .Be(staged.PersonUuid);
        (await people.ReadCreditContextAsync(staged.Source.SourceUuid))
            .Credits.Single()
            .Credit.PersonUuid.Should()
            .Be(staged.PersonUuid);
    }

    [TestCase("policy")]
    [TestCase("rival")]
    [TestCase("source")]
    public async Task ChangesAfterWorkAdmissionPreventIdentityPublication(string change)
    {
        var staged = await PrepareCreation();
        if (change == "policy")
        {
            await SetActivePolicy(null);
        }
        else if (change == "rival")
        {
            await Person(staged.Name);
        }
        else
        {
            await SetTaper(staged.Source, UniqueName());
        }

        var application = await applier.ApplyRecordingAsync(
            staged.Application.AttemptUuid,
            staged.Application.ResolutionAttemptUuid
        );

        application.Result!.Disposition.Should().Be(PeopleAcceptanceDisposition.Stale);
        application.Result.Publications.Should().BeEmpty();
        application.Stages.Single().Receipt.Should().BeNull();
        (
            await candidates.FindPeopleFactsAsync(
                new(IdentityMatchingDomain.People, [staged.Name], [])
            )
        )
            .Facts.Should()
            .NotContain(fact => fact.Hit.Uuid == staged.PersonUuid);
        (await people.ReadCreditContextAsync(staged.Source.SourceUuid)).Credits.Should().BeEmpty();
    }

    [Test]
    public async Task IdentityAndStageReceiptRollbackTogetherAndTheSameWorkCanRetry()
    {
        var staged = await PrepareCreation();
        var triggerName = "acceptance_receipt_failure_" + Guid.NewGuid().ToString("N");
        // Fail after domain writes, at the receipt boundary in their shared transaction.
        // The trigger targets only this application and is removed even if an assertion fails.
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                $"""
                CREATE FUNCTION normalization.{triggerName}() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW.application_uuid='{staged.Application.AttemptUuid:D}'::uuid THEN
                        RAISE EXCEPTION 'synthetic acceptance receipt failure';
                    END IF;
                    RETURN NEW;
                END;
                $$;
                CREATE TRIGGER {triggerName} BEFORE UPDATE OF receipt ON normalization.people_acceptance_stages
                    FOR EACH ROW EXECUTE FUNCTION normalization.{triggerName}();
                """
            )
        );
        try
        {
            var publish = () =>
                people.PublishResolutionIdentityAsync(
                    staged.Stage.WorkUuid,
                    staged.Application.AttemptUuid,
                    staged.PersonUuid
                );
            var failure = await publish.Should().ThrowAsync<Exception>();
            var databaseFailure = failure
                .Which.InnerException.Should()
                .BeOfType<PostgresException>()
                .Which;
            databaseFailure.SqlState.Should().Be("P0001");
            databaseFailure.MessageText.Should().Be("synthetic acceptance receipt failure");
            (await applier.GetApplicationAsync(staged.Application.AttemptUuid))!
                .Stages.Single()
                .Receipt.Should()
                .BeNull();
            (
                await candidates.FindPeopleFactsAsync(
                    new(IdentityMatchingDomain.People, [staged.Name], [])
                )
            )
                .Facts.Should()
                .BeEmpty();
        }
        finally
        {
            await fixture.Db.WithWriteConnection(c =>
                c.ExecuteAsync(
                    $"""
                    DROP TRIGGER {triggerName} ON normalization.people_acceptance_stages;
                    DROP FUNCTION normalization.{triggerName}();
                    """
                )
            );
        }

        var recovered = await applier.ApplyRecordingAsync(
            staged.Application.AttemptUuid,
            staged.Application.ResolutionAttemptUuid
        );

        recovered
            .Result!.Disposition.Should()
            .Be(
                PeopleAcceptanceDisposition.Applied,
                "application audit: {0}",
                ApplicationAudit(recovered)
            );
        recovered.Result.Publications.Should().HaveCount(2);
        recovered
            .Result.Publications.Should()
            .Contain(receipt =>
                receipt.WorkUuid == staged.Stage.WorkUuid && receipt.PersonUuid == staged.PersonUuid
            );
        (await people.ReadCreditContextAsync(staged.Source.SourceUuid))
            .Credits.Single()
            .Credit.PersonUuid.Should()
            .Be(staged.PersonUuid);
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task SafeContributorAdditionSurvivesUnresolvedWordingThenCompleteReplacementRetiresIt(
        bool unknownInSameRole
    )
    {
        var aliceName = UniqueName();
        var bobName = UniqueName();
        var alice = await Person(aliceName);
        var bob = await Person(bobName);
        await ActivatePolicy();
        var source = await Source(aliceName);
        await SetLegacyCredits(
            source,
            unknownInSameRole ? aliceName + "\nunknown" : aliceName,
            unknownInSameRole ? null : "unknown"
        );
        var first = await Resolve(source);
        first.Preparation!.Inputs.Single().UnresolvedCredits.Should().NotBeEmpty();

        var partial = await applier.ApplyRecordingAsync(Guid.NewGuid(), first.AttemptUuid);

        partial.Result!.Disposition.Should().Be(PeopleAcceptanceDisposition.Unresolved);
        partial.Result!.Publications.Should().NotBeEmpty();
        (await people.ReadCreditContextAsync(source.SourceUuid))
            .Credits.Single()
            .Credit.PersonUuid.Should()
            .Be(alice);
        await SetLegacyCredits(source, aliceName + "\n" + bobName, null);
        var complete = await Resolve(source);
        await applier.ApplyRecordingAsync(Guid.NewGuid(), complete.AttemptUuid);
        (await people.ReadCreditContextAsync(source.SourceUuid))
            .Credits.Where(item => item.Credit.State == "accepted")
            .Select(item => item.Credit.PersonUuid)
            .Should()
            .BeEquivalentTo(new Guid?[] { alice, bob });

        await SetTaper(source, bobName);
        var replacement = await Resolve(source);
        var applied = await applier.ApplyRecordingAsync(Guid.NewGuid(), replacement.AttemptUuid);

        applied
            .Result!.Disposition.Should()
            .Be(
                PeopleAcceptanceDisposition.Applied,
                "application audit: {0}",
                ApplicationAudit(applied)
            );
        var afterReplacement = (await people.ReadCreditContextAsync(source.SourceUuid)).Credits;
        afterReplacement
            .Where(item => item.Credit.State == "accepted")
            .Should()
            .ContainSingle()
            .Which.Credit.PersonUuid.Should()
            .Be(bob);
        afterReplacement
            .Should()
            .Contain(item => item.Credit.PersonUuid == alice && item.Credit.State == "superseded");
    }

    [TestCase(null)]
    [TestCase("")]
    public async Task ExplicitlyClearedTaperPublishesRetirementWithoutCreatingAnActor(
        string? cleared
    )
    {
        var name = UniqueName();
        var alice = await Person(name);
        await ActivatePolicy();
        var source = await Source(name);
        var first = await Resolve(source);
        await applier.ApplyRecordingAsync(Guid.NewGuid(), first.AttemptUuid);

        await SetTaper(source, cleared);
        var empty = await Resolve(source);
        empty.Preparation!.Inputs.Single().Claims.Should().BeEmpty();
        var application = await applier.ApplyRecordingAsync(Guid.NewGuid(), empty.AttemptUuid);

        application
            .Result!.Disposition.Should()
            .Be(
                PeopleAcceptanceDisposition.Applied,
                "application audit: {0}",
                ApplicationAudit(application)
            );
        application.Plan!.Decisions.Should().BeEmpty();
        var stage = application.Stages.Should().ContainSingle().Which;
        stage.PersonUuid.Should().BeNull();
        stage
            .Reconciliation!.Contributors.SelectMany(contributor => contributor.Credits)
            .Should()
            .BeEmpty();
        stage
            .ScopeCoverage.Should()
            .Contain(coverage =>
                coverage.Complete && coverage.Scope.Role == "taper" && coverage.Fields.Count > 0
            );
        var credits = (await people.ReadCreditContextAsync(source.SourceUuid)).Credits;
        credits
            .Should()
            .ContainSingle()
            .Which.Credit.Should()
            .Match<RecordingCredit>(credit =>
                credit.PersonUuid == alice && credit.State == "superseded"
            );
    }

    [Test]
    public async Task RemovingTaperRetiresAliceWhileTheTransferrerRemainsAccepted()
    {
        var aliceName = UniqueName();
        var bobName = UniqueName();
        var alice = await Person(aliceName);
        var bob = await Person(bobName);
        await ActivatePolicy();
        var source = await Source(aliceName);
        await SetLegacyCredits(source, aliceName, bobName);
        var first = await Resolve(source);
        await applier.ApplyRecordingAsync(Guid.NewGuid(), first.AttemptUuid);

        await SetLegacyCredits(source, null, bobName);
        var current = await Resolve(source);
        var application = await applier.ApplyRecordingAsync(Guid.NewGuid(), current.AttemptUuid);

        application
            .Result!.Disposition.Should()
            .Be(
                PeopleAcceptanceDisposition.Applied,
                "application audit: {0}",
                ApplicationAudit(application)
            );
        var credits = (await people.ReadCreditContextAsync(source.SourceUuid)).Credits;
        credits
            .Where(item => item.Credit.State == "accepted")
            .Should()
            .ContainSingle()
            .Which.Credit.Should()
            .Match<RecordingCredit>(credit =>
                credit.PersonUuid == bob && credit.Role == "transferrer"
            );
        credits
            .Should()
            .Contain(item =>
                item.Credit.PersonUuid == alice
                && item.Credit.Role == "taper"
                && item.Credit.State == "superseded"
            );
    }

    [TestCase("unknown")]
    [TestCase("not known")]
    [TestCase("Sony PCM-D50 > Audacity")]
    public async Task UnresolvedCurrentRoleDoesNotTurnZeroClaimsIntoPermissionToRetire(
        string wording
    )
    {
        var name = UniqueName();
        var alice = await Person(name);
        await ActivatePolicy();
        var source = await Source(name);
        var first = await Resolve(source);
        await applier.ApplyRecordingAsync(Guid.NewGuid(), first.AttemptUuid);

        await SetTaper(source, wording);
        var current = await Resolve(source);
        current.Preparation!.Inputs.Single().Claims.Should().BeEmpty();
        current.Preparation.Inputs.Single().UnresolvedCredits.Should().NotBeEmpty();
        var application = await applier.ApplyRecordingAsync(Guid.NewGuid(), current.AttemptUuid);

        application.Result!.Disposition.Should().Be(PeopleAcceptanceDisposition.Unresolved);
        AssertDecisionAcknowledged(application);
        (await people.ReadCreditContextAsync(source.SourceUuid))
            .Credits.Should()
            .ContainSingle()
            .Which.Credit.Should()
            .Match<RecordingCredit>(credit =>
                credit.PersonUuid == alice && credit.State == "accepted"
            );
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task MissingOrUnsupportedRetainedFieldPreservesItsPreviouslyAcceptedContributorSupport(
        bool unsupportedValue
    )
    {
        var name = UniqueName();
        var alice = await Person(name);
        await ActivatePolicy();
        var source = await Source("");
        var first = await ExtractArchive(source, name);
        var firstResolution = await resolver.ResolveRecordingAsync(
            Guid.NewGuid(),
            first.AttemptUuid
        );
        var accepted = await applier.ApplyRecordingAsync(
            Guid.NewGuid(),
            firstResolution.AttemptUuid
        );
        accepted
            .Result!.Disposition.Should()
            .Be(
                PeopleAcceptanceDisposition.Applied,
                "application audit: {0}",
                ApplicationAudit(accepted)
            );

        var missing = await ExtractArchive(
            source,
            unsupportedValue ? 42 : null,
            includeRecordingTaper: unsupportedValue
        );
        var current = await resolver.ResolveRecordingAsync(Guid.NewGuid(), missing.AttemptUuid);
        current.Preparation!.Inputs.Single().Claims.Should().BeEmpty();
        if (unsupportedValue)
        {
            current.Preparation.Inputs.Single().DecodeIssues.Should().NotBeEmpty();
        }
        var application = await applier.ApplyRecordingAsync(Guid.NewGuid(), current.AttemptUuid);

        application.Result!.Disposition.Should().Be(PeopleAcceptanceDisposition.Unresolved);
        AssertDecisionAcknowledged(application);
        (await people.ReadCreditContextAsync(source.SourceUuid))
            .Credits.Should()
            .ContainSingle()
            .Which.Credit.Should()
            .Match<RecordingCredit>(credit =>
                credit.PersonUuid == alice && credit.State == "accepted"
            );
    }

    [Test]
    public async Task EmptyContributorBindingsPreserveAcceptedSupportAndAcknowledgeTheUnresolvedDecision()
    {
        var name = UniqueName();
        var alice = await Person(name);
        await ActivatePolicy();
        var source = await Source(name);
        var first = await Resolve(source);
        await applier.ApplyRecordingAsync(Guid.NewGuid(), first.AttemptUuid);
        var owner = new NormalizationSubjectKey("people", "recording", source.SourceUuid);
        var previous = await fixture.Work.GetInputContributionsAsync(owner);
        var contribution = previous.Contributions.Single();

        // Work retains an empty contributor revision as a tombstone. That is configuration
        // state, with no field evidence establishing that an accepted credit was cleared.
        await fixture.Work.BeginContributedWorkAsync(
            Guid.NewGuid(),
            owner,
            NormalizationPeopleResolutionApplier.Version,
            new(contribution.Contributor, contribution.Revision, [], []),
            previous.Owner!.DesiredGeneration,
            expectedInputRevisions: []
        );
        await SetTaper(source, "unknown");
        var current = await Resolve(source);
        var application = await applier.ApplyRecordingAsync(Guid.NewGuid(), current.AttemptUuid);

        application.Result!.Disposition.Should().Be(PeopleAcceptanceDisposition.Unresolved);
        AssertDecisionAcknowledged(application);
        (await fixture.Work.GetSubjectAsync(owner))!
            .PublishedWorkUuid.Should()
            .Be(application.Stages.Single().WorkUuid);
        (await people.ReadCreditContextAsync(source.SourceUuid))
            .Credits.Should()
            .ContainSingle()
            .Which.Credit.Should()
            .Match<RecordingCredit>(credit =>
                credit.PersonUuid == alice && credit.State == "accepted"
            );
    }

    [TestCase("rival")]
    [TestCase("source")]
    public async Task ChangedOriginalInputsOrCandidateUniverseRequireNewResolution(string change)
    {
        var name = UniqueName();
        var person = await Person(name);
        await ActivatePolicy();
        var source = await Source(name);
        var resolution = await Resolve(source);
        if (change == "rival")
        {
            await Person(name);
        }
        else
        {
            await SetTaper(source, UniqueName());
        }

        var application = await applier.ApplyRecordingAsync(Guid.NewGuid(), resolution.AttemptUuid);

        application.Result!.Disposition.Should().Be(PeopleAcceptanceDisposition.Stale);
        (await people.ReadCreditContextAsync(source.SourceUuid)).Credits.Should().BeEmpty();
        (await candidates.FindPeopleFactsAsync(new(IdentityMatchingDomain.People, [name], [])))
            .Facts.Should()
            .Contain(fact => fact.Hit.Uuid == person);
    }

    [Test]
    public async Task ShapeValidButForgedCandidateFactsCannotAuthorizeAcceptedData()
    {
        var name = UniqueName();
        await Person(name);
        await ActivatePolicy();
        var source = await Source(name);
        var extraction = await Extract(source);
        var original = await Prepare(extraction);
        var input = original.Inputs.Single();
        var claim = input.Claims.Single();
        var snapshot = claim.Candidates!;
        var fact = snapshot.Facts.Single();
        // Proposal storage verifies shape and baseline decisions, not authority to accept
        // arbitrary supplied candidate facts. The application must authenticate the payload.
        var forged = snapshot with
        {
            Facts =
            [
                fact with
                {
                    Header = fact.Header with { DisplayText = "Forged Canonical Name" },
                },
            ],
        };
        var preparation = original with
        {
            Inputs =
            [
                input with
                {
                    Claims =
                    [
                        baseline.Compare(
                            claim.Proposal,
                            input.OriginalInput.Binding,
                            forged,
                            original.CreditContext
                        ),
                    ],
                },
            ],
        };
        var attempt = await resolutions.GetOrCreateAttemptAsync(
            Guid.NewGuid(),
            extraction.AttemptUuid,
            NormalizationPeopleResolutionProcessor.Version
        );
        await resolutions.SavePreparationAsync(attempt.AttemptUuid, preparation);
        var resolution = await resolver.ResolveRecordingAsync(
            attempt.AttemptUuid,
            extraction.AttemptUuid
        );
        resolution
            .Result!.Disposition.Should()
            .Be(NormalizationPeopleResolutionDisposition.Published);

        var application = await applier.ApplyRecordingAsync(Guid.NewGuid(), resolution.AttemptUuid);

        application.Result!.Disposition.Should().Be(PeopleAcceptanceDisposition.Stale);
        (await people.ReadCreditContextAsync(source.SourceUuid)).Credits.Should().BeEmpty();
    }

    [Test]
    public async Task CreditOnAnUnboundRetainedFileIsNeverWidenedToTheWholeRecording()
    {
        var name = UniqueName();
        var person = await Person(name);
        await ActivatePolicy();
        var source = await Source("");
        var extraction = await ExtractArchive(source, null, name, includeRecordingTaper: false);
        var resolution = await resolver.ResolveRecordingAsync(
            Guid.NewGuid(),
            extraction.AttemptUuid
        );
        var claim = resolution.Preparation!.Inputs.Single().Claims.Single();
        claim.Candidates!.Facts.Should().Contain(fact => fact.Hit.Uuid == person);
        claim.ScopeIssue.Should().NotBeNull();

        var application = await applier.ApplyRecordingAsync(Guid.NewGuid(), resolution.AttemptUuid);

        application.Plan!.Decisions.Single().Reason.Should().Be("unbound_file_scope");
        AssertDecisionAcknowledged(application);
        (await people.ReadCreditContextAsync(source.SourceUuid)).Credits.Should().BeEmpty();
    }

    [Test]
    public async Task RevokedPolicyStopsNewApplicationsWithoutWithdrawingAcceptedCredits()
    {
        var name = UniqueName();
        var person = await Person(name);
        await ActivatePolicy();
        var source = await Source(name);
        var resolution = await Resolve(source);
        var accepted = await applier.ApplyRecordingAsync(Guid.NewGuid(), resolution.AttemptUuid);
        accepted
            .Result!.Disposition.Should()
            .Be(
                PeopleAcceptanceDisposition.Applied,
                "application audit: {0}",
                ApplicationAudit(accepted)
            );
        await SetActivePolicy(null);
        resolution = await Resolve(source);

        var application = await applier.ApplyRecordingAsync(Guid.NewGuid(), resolution.AttemptUuid);

        application.Result!.Disposition.Should().Be(PeopleAcceptanceDisposition.Unresolved);
        AssertDecisionAcknowledged(application);
        (await people.ReadCreditContextAsync(source.SourceUuid))
            .Credits.Single()
            .Credit.PersonUuid.Should()
            .Be(person);
    }

    [Test]
    public async Task ApplicationUuidCannotBeReusedForAnotherPublishedResolution()
    {
        var name = UniqueName();
        await Person(name);
        await ActivatePolicy();
        var source = await Source(name);
        var first = await Resolve(source);
        var attemptUuid = Guid.NewGuid();
        await applier.ApplyRecordingAsync(attemptUuid, first.AttemptUuid);
        var later = await Resolve(source);

        var changed = () => applier.ApplyRecordingAsync(attemptUuid, later.AttemptUuid);

        await changed.Should().ThrowAsync<NormalizationConflictException>();
        (await people.ReadCreditContextAsync(source.SourceUuid)).Credits.Should().ContainSingle();
    }

    private async Task ActivatePolicy(
        double matchThreshold = 1.5,
        bool allowCreation = false,
        double contactWeight = 0,
        double profileWeight = 0,
        string inputForm = "retained_credit"
    )
    {
        var artifact = new PeopleAcceptancePolicyArtifact(
            Guid.NewGuid(),
            "acceptance-pipeline-fixture-v1",
            "",
            PeopleResolutionPolicyEvaluator.ScorerVersion,
            PeopleResolutionPolicyEvaluator.RetrievalVersion,
            NormalizationCandidateService.FactsVersion,
            NormalizationRecordingExtractor.Version,
            NormalizedPeopleService.CreditContextVersion,
            [inputForm],
            new(0, 1, 1, 1, matchThreshold, 0.5, contactWeight, profileWeight),
            allowCreation
                ? new(PeopleResolutionPolicyEvaluator.CreationClassifierVersion, 0.25)
                : null,
            FixtureOnly: true
        );
        artifact = artifact with
        {
            ContentHash = PeopleResolutionPolicyService.ComputeArtifactHash(artifact),
        };
        var evaluation = new PeopleAcceptanceEvaluationReceipt(
            Guid.NewGuid(),
            artifact.Uuid,
            artifact.ContentHash,
            "",
            new string('a', 64),
            new string('b', 64),
            new string('c', 64),
            "fixture-whole-pool-evaluator-v1",
            "independent-fixture-reviewer",
            Operator,
            new(100, 0),
            allowCreation ? new(100, 0) : null,
            allowCreation ? new(100, 0) : null,
            FixtureOnly: true
        );
        evaluation = evaluation with
        {
            ReceiptHash = PeopleResolutionPolicyService.ComputeEvaluationHash(evaluation),
        };
        await policies.RegisterEvaluatedPolicyAsync(artifact, evaluation, Operator);
        await SetActivePolicy(artifact.Uuid);
    }

    // Save a genuine one-owner checkpoint through the same store and Work contracts as the
    // applier. Tests stop at this boundary without timing sleeps or production-only hooks.
    private async Task<PreparedCreation> PrepareCreation()
    {
        var name = UniqueName();
        await ActivatePolicy(allowCreation: true);
        var source = await Source(name);
        var resolution = await Resolve(source);
        var proposal = (await resolutions.GetAttemptBatchAsync(resolution.AttemptUuid))!;
        var policy = (await policies.ReadActiveAsync())!;
        var claim = proposal.Preparation.Inputs.Single().Claims.Single();
        var personUuid = Guid.NewGuid();
        var decision = new PeopleResolutionPolicyEvaluator().Evaluate(
            claim,
            policy.Policy,
            1,
            "retained_credit",
            personUuid,
            Guid.NewGuid()
        );
        decision.Action.Should().Be(PeopleResolutionAction.CreateIdentity);
        var application = await applications.GetOrCreateAsync(
            Guid.NewGuid(),
            resolution.AttemptUuid,
            NormalizationPeopleResolutionApplier.Version,
            CancellationToken.None
        );
        application = await applications.SavePlanAsync(
            application.AttemptUuid,
            new(proposal, policy, [decision]),
            CancellationToken.None
        );
        var owner = new NormalizationSubjectKey("people", "person", personUuid);
        var membership = await fixture.Work.GetInputContributionsAsync(owner);
        var proposalStamp = new NormalizationSubjectObservation(
            proposal.Ticket.SubjectUuid,
            proposal.Ticket.DesiredGeneration,
            proposal.Ticket.EditorialRevision,
            proposal.AttemptUuid
        );
        var stage = new PeopleAcceptanceStage(
            Guid.NewGuid(),
            personUuid,
            policy,
            proposal.Preparation,
            [decision],
            PeopleAcceptanceReadSet.Contributions(
                proposal.Preparation,
                proposalStamp,
                policy,
                membership,
                []
            ),
            membership.Owner?.DesiredGeneration,
            membership
                .Contributions.Select(value => new NormalizationInputContributionRevision(
                    value.Contributor,
                    value.Revision
                ))
                .ToArray(),
            new(
                [new(personUuid, name, decision.Creation!.Kind)],
                [
                    new(
                        Guid.NewGuid(),
                        personUuid,
                        name,
                        claim.Proposal.Mention.Evidence,
                        "validated_match"
                    ),
                ],
                [],
                []
            ),
            null
        );
        await applications.SaveStageAsync(application.AttemptUuid, stage, CancellationToken.None);
        var ticket = await fixture.Work.BeginContributedBatchAsync(
            stage.WorkUuid,
            owner,
            application.Version,
            stage.Contributions,
            stage.ExpectedConfigurationGeneration,
            expectedContributionRevisions: stage.ExpectedContributions
        );
        application = await applications.AttachTicketAsync(
            application.AttemptUuid,
            stage.WorkUuid,
            ticket,
            CancellationToken.None
        );
        return new(application, application.Stages.Single(), source, name, personUuid);
    }

    private async Task SetActivePolicy(Guid? artifactUuid)
    {
        var active = await policies.ReadActiveAsync();
        var owner = await fixture.Work.GetSubjectAsync(PeopleResolutionPolicyService.Owner);
        if (owner == null)
        {
            await fixture.Work.BeginWorkAsync(
                Guid.NewGuid(),
                PeopleResolutionPolicyService.Owner,
                PeopleResolutionPolicyService.RegistryVersion,
                [],
                []
            );
            owner = (await fixture.Work.GetSubjectAsync(PeopleResolutionPolicyService.Owner))!;
        }
        await policies.ApplyDecisionAsync(
            new(
                Guid.NewGuid(),
                owner.SubjectUuid,
                owner.EditorialRevision,
                active?.Policy.Artifact.Uuid,
                artifactUuid,
                "Isolated acceptance pipeline fixture"
            ),
            Operator
        );
    }

    private async Task<Guid> Person(string name)
    {
        var input = await fixture.Capture(name, "people");
        var uuid = Guid.NewGuid();
        await fixture.PublishPeople(people, input, new([new(uuid, name)], [], [], []));
        return uuid;
    }

    private async Task SeedArtistCredit(Guid person, string name, int artistId)
    {
        var context = await fixture.Source();
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "UPDATE sources SET artist_id=@artistId WHERE id=@SourceId",
                new { artistId, context.SourceId }
            )
        );
        var input = await fixture.Capture(name, "people");
        await fixture.PublishPeople(
            people,
            input,
            new(
                [],
                [],
                [],
                [
                    new(
                        Guid.NewGuid(),
                        context.SourceUuid,
                        "taper",
                        name,
                        person,
                        null,
                        [input.Reference]
                    ),
                ]
            )
        );
    }

    private async Task<CatalogSource> Source(string name)
    {
        var source = await fixture.Source();
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "UPDATE sources SET taper=@name,transferrer=NULL,lineage=NULL,source=NULL,description=NULL,taper_notes=NULL WHERE id=@SourceId",
                new { name, source.SourceId }
            )
        );
        return source;
    }

    private Task SetTaper(CatalogSource source, string? name) =>
        fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "UPDATE sources SET taper=@name WHERE id=@SourceId",
                new { name, source.SourceId }
            )
        );

    private Task SetLegacyCredits(CatalogSource source, string? taper, string? transferrer) =>
        fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "UPDATE sources SET taper=@taper,transferrer=@transferrer WHERE id=@SourceId",
                new
                {
                    taper,
                    transferrer,
                    source.SourceId,
                }
            )
        );

    private async Task<NormalizationRecordingProposalBatch> ExtractArchive(
        CatalogSource source,
        object? recordingTaper,
        string? unboundFileTaper = null,
        bool includeRecordingTaper = true
    )
    {
        var key = "acceptance-file-" + source.SourceUuid.ToString("N");
        await fixture.Db.WithWriteConnection(async c =>
        {
            var upstream =
                await c.QuerySingleOrDefaultAsync<int?>(
                    "SELECT id FROM upstream_sources WHERE name='archive.org' ORDER BY id LIMIT 1"
                )
                ?? await c.QuerySingleAsync<int>(
                    "INSERT INTO upstream_sources(id,name,url) SELECT COALESCE(max(id),0)+1,'archive.org','https://archive.org' FROM upstream_sources RETURNING id"
                );
            await c.ExecuteAsync(
                """
                UPDATE sources SET upstream_identifier=@key WHERE id=@SourceId;
                UPDATE source_tracks SET mp3_url=@media WHERE uuid=@TrackUuid;
                INSERT INTO links(source_id,upstream_source_id,for_source,for_reviews,for_ratings,url,label)
                    SELECT @SourceId,@upstream,true,false,false,@url,'Archive'
                    WHERE NOT EXISTS(SELECT 1 FROM links WHERE source_id=@SourceId AND url=@url);
                """,
                new
                {
                    source.SourceId,
                    source.TrackUuid,
                    key,
                    upstream,
                    media = $"https://archive.org/download/{key}/file.mp3",
                    url = $"https://archive.org/details/{key}",
                }
            );
        });
        var metadata = new Dictionary<string, object?> { ["identifier"] = key };
        if (includeRecordingTaper)
        {
            metadata["taper"] = recordingTaper;
        }
        var files = new List<object>
        {
            new
            {
                name = "file.mp3",
                title = "Song",
                format = "VBR MP3",
            },
        };
        if (unboundFileTaper != null)
        {
            files.Add(
                new
                {
                    name = "unmapped.flac",
                    title = "Song",
                    format = "Flac",
                    taper = unboundFileTaper,
                }
            );
        }
        var evidence = new NormalizationEvidenceService(fixture.Db);
        var resource = new NormalizationResourceKey("archive.org", "item_metadata", key);
        var previous = await evidence.GetCurrentInputAsync(resource);
        var captured = await evidence.CaptureInputAsync(
            new(
                Guid.NewGuid(),
                resource,
                JsonSerializer.SerializeToUtf8Bytes(new { metadata, files }),
                "application/json",
                "archive-item-response-v1",
                previous?.Generation ?? 0,
                DateTimeOffset.UtcNow
            )
        );
        var lookups = new NormalizationRecordingLookupStore(fixture.Db);
        var lookupService = new NormalizationRecordingLookupService(
            lookups,
            new(fixture.Db, evidence),
            evidence,
            new(fixture.Db),
            new(),
            fixture.Work
        );
        var lookup = await lookupService.LookupRetainedAsync(
            Guid.NewGuid(),
            new(captured.Revision.Uuid, captured.Revision.Resource)
        );
        var dispatch = await new NormalizationRecordingLookupDispatcher(
            lookups,
            extractor
        ).DispatchAsync(lookup.Result!.HandoffUuid!.Value);
        dispatch
            .ProcessingResult.Disposition.Should()
            .Be(NormalizationRecordingProcessingDisposition.Published);
        return (await extractions.GetAttemptBatchAsync(dispatch.ProcessingResult.AttemptUuid))!;
    }

    private async Task<NormalizationRecordingProposalBatch> Extract(CatalogSource source)
    {
        var result = await extractor.ProcessAsync(Guid.NewGuid(), source.SourceUuid);
        result.Disposition.Should().Be(NormalizationRecordingProcessingDisposition.Published);
        return (await extractions.GetAttemptBatchAsync(result.AttemptUuid))!;
    }

    private async Task<NormalizationPeopleResolutionAttempt> Resolve(CatalogSource source)
    {
        var extraction = await Extract(source);
        var resolution = await resolver.ResolveRecordingAsync(
            Guid.NewGuid(),
            extraction.AttemptUuid
        );
        resolution
            .Result!.Disposition.Should()
            .Be(NormalizationPeopleResolutionDisposition.Published);
        return resolution;
    }

    private async Task<NormalizationPeopleResolutionPreparation> Prepare(
        NormalizationRecordingProposalBatch extraction
    )
    {
        var original = NormalizationRecordingReadSet.Inputs(extraction.Preparation).Single();
        var context = await people.ReadCreditContextAsync(extraction.SourceUuid);
        var claims = new List<NormalizationPeopleResolutionClaim>();
        foreach (var proposal in extraction.Extraction.Credits)
        {
            var facts = await candidates.FindPeopleFactsAsync(
                new(IdentityMatchingDomain.People, [proposal.Mention.Name], [])
            );
            claims.Add(baseline.Compare(proposal, original.Binding, facts, context));
        }
        return new(
            new(
                extraction.Ticket.SubjectUuid,
                extraction.Ticket.DesiredGeneration,
                extraction.Ticket.EditorialRevision,
                extraction.AttemptUuid
            ),
            context,
            null,
            [],
            [
                new(
                    original,
                    0,
                    claims,
                    extraction.Extraction.UnresolvedCredits,
                    extraction.Extraction.DecodeIssues
                ),
            ]
        );
    }

    private static string ApplicationAudit(PeopleAcceptanceApplication application) =>
        JsonSerializer.Serialize(
            new
            {
                application.Result,
                Decisions = application.Plan?.Decisions,
                Inputs = application.Plan?.Proposal.Preparation.Inputs.Select(input => new
                {
                    input.OriginalInput.Contributor,
                    input.UnresolvedCredits,
                    input.DecodeIssues,
                }),
            }
        );

    private static string UniqueName() => Capitalize(RandomWord()) + " " + Capitalize(RandomWord());

    private static string Capitalize(string word) => char.ToUpperInvariant(word[0]) + word[1..];

    private static string RandomWord() =>
        string.Concat(Guid.NewGuid().ToByteArray().Select(value => (char)('a' + value % 26)));

    private sealed record PreparedCreation(
        PeopleAcceptanceApplication Application,
        PeopleAcceptanceStage Stage,
        CatalogSource Source,
        string Name,
        Guid PersonUuid
    );
}
