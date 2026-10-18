using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using FluentAssertions;
using Npgsql;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
using Relisten.Normalization;
using CatalogSource = RelistenApiTests.Services.NormalizationDomainFixture.CatalogSource;

namespace RelistenApiTests.Services;

/// <summary>Published extraction IDs feed durable role-free proposals without accepted identity changes.</summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed partial class TestSourceActorResolution
{
    private const string Operator = "source-resolution-fixture-operator";
    private NormalizationDomainFixture fixture = null!;
    private NormalizationEvidenceService evidence = null!;
    private NormalizedPeopleService people = null!;
    private NormalizationRecordingProposalService extractions = null!;
    private NormalizationRecordingProcessor extractor = null!;
    private NormalizationCandidateService candidates = null!;
    private PeopleResolutionPolicyService policies = null!;
    private SourceActorResolutionStore store = null!;
    private SourceActorResolutionService resolver = null!;

    [OneTimeSetUp]
    public void Start()
    {
        fixture = new();
        evidence = new(fixture.Db);
        people = new(fixture.Db, fixture.Work);
        extractions = new(fixture.Db);
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
        candidates = new(fixture.Db);
        policies = new(fixture.Db, fixture.Work, allowFixturePolicies: true);
        store = new(fixture.Db);
        resolver = NewResolver();
    }

    [SetUp, TearDown]
    public async Task LeaveNoActivePolicy()
    {
        var state = await policies.ReadStateAsync();
        if (state.Active != null)
        {
            await SelectPolicy(null);
        }
    }

    [OneTimeTearDown]
    public void Stop() => fixture?.Dispose();

    [TestCase(false)]
    [TestCase(true)]
    public async Task MissingOrCreditOnlyPolicyRetainsEveryActualComparisonAsUnresolved(
        bool creditOnly
    )
    {
        if (creditOnly)
        {
            await SelectPolicy(Policy(sourceQualified: false));
        }
        var name = "Unqualified " + Guid.NewGuid().ToString("N");
        var person = await Person(name);
        var source = await Source(Personnel(name));
        var extraction = await Extract(source);
        var before = await AcceptedState();
        var epoch = await PeopleEpoch();

        var attempt = await resolver.ResolveAsync(Guid.NewGuid(), extraction.AttemptUuid);

        attempt.Result!.Disposition.Should().Be(SourceActorResolutionDisposition.Published);
        var batch = (await store.GetAsync(source.SourceUuid))!;
        var claim = Claims(batch.Preparation).Single();
        var pool = batch.Preparation.CandidatePools[claim.CandidatePoolIndex!.Value];
        pool.Facts.Should().Contain(fact => fact.Hit.Uuid == person);
        pool.Version.Should().Be(NormalizationCandidateService.PeopleFactsVersion);
        pool.Facts.Should().OnlyContain(fact => fact.AliasMatchingNamesComplete == true);
        claim
            .Comparisons.Select(pair => pair.CandidateUuid)
            .Should()
            .BeEquivalentTo(pool.Selection.Items.Select(hit => (Guid?)hit.Uuid));
        claim.Comparisons.Should().HaveCount(pool.Facts.Count);
        claim
            .Comparisons.Should()
            .OnlyContain(pair =>
                pair.Features.Any(feature =>
                    feature.Name == "person_kind"
                    && feature.Agreement == IdentityFeatureAgreement.Missing
                )
            );
        claim.Decision.Action.Should().Be(SourceActorResolutionAction.Unresolved);
        claim.Decision.Reason.Should().Be("unsupported_policy_applicability");
        claim.Decision.PersonUuid.Should().BeNull();
        (await AcceptedState()).Should().Be(before);
        (await PeopleEpoch()).Should().Be(epoch);
    }

    [TestCase("person")]
    [TestCase("group")]
    public async Task SeparatelyQualifiedPolicyCanProposeAnExistingIdentityWithoutPublishingAliases(
        string kind
    )
    {
        await SelectPolicy(Policy());
        var name = "Qualified " + Guid.NewGuid().ToString("N");
        var person = await Person(name, kind);
        var source = await Source(Personnel(name));
        var extraction = await Extract(source);
        var before = await AcceptedState();

        var attempt = await resolver.ResolveAsync(Guid.NewGuid(), extraction.AttemptUuid);

        attempt.Result!.Disposition.Should().Be(SourceActorResolutionDisposition.Published);
        var claim = Claims(attempt.Preparation!).Single();
        claim.Decision.Action.Should().Be(SourceActorResolutionAction.MatchExisting);
        claim.Decision.PersonUuid.Should().Be(person);
        claim.Decision.PolicyProposal!.PersonUuid.Should().Be(person);
        claim.SourceReviewRevision.Should().Be(0);
        claim.SourceReviewStateUuid.Should().BeNull();
        (await AcceptedState()).Should().Be(before);
        (await people.ReadCreditContextAsync(source.SourceUuid)).Credits.Should().BeEmpty();
    }

    [Test]
    public async Task NamesakesRemainCompetingCandidatesWithoutASelectedIdentity()
    {
        await SelectPolicy(Policy());
        var name = "Namesake " + Guid.NewGuid().ToString("N");
        var first = await Person(name);
        var second = await Person(name);
        var source = await Source(Personnel(name));
        var extraction = await Extract(source);

        var attempt = await resolver.ResolveAsync(Guid.NewGuid(), extraction.AttemptUuid);

        var claim = Claims(attempt.Preparation!).Single();
        var pool = attempt.Preparation!.CandidatePools[claim.CandidatePoolIndex!.Value];
        pool.Facts.Select(fact => fact.Hit.Uuid).Should().Contain([first, second]);
        claim.Decision.Action.Should().Be(SourceActorResolutionAction.Review);
        claim.Decision.PersonUuid.Should().BeNull();
        claim.Decision.PolicyProposal!.Reason.Should().Be("ambiguous_candidate_margin");
    }

    [Test]
    public async Task TruncatedActualCandidatePoolRetainsEveryReturnedComparisonAndRefusesSelection()
    {
        await SelectPolicy(Policy());
        var name = "Truncated " + Guid.NewGuid().ToString("N");
        var source = await Source(Personnel(name));
        var extraction = await Extract(source);
        var assertion = extraction
            .Extraction.SourceActorAliases!.SelectMany(scope => scope.Extraction.Assertions)
            .Single();
        var query = PeopleActorProjection.Project(assertion, extraction.ArtistUuid).Query!;
        for (var index = 0; index <= query.NameLimit; index++)
        {
            await Person(name);
        }
        var before = await AcceptedState();

        var result = await resolver.ResolveAsync(Guid.NewGuid(), extraction.AttemptUuid);

        result.Result!.Disposition.Should().Be(SourceActorResolutionDisposition.Published);
        var claim = Claims(result.Preparation!).Single();
        var pool = result.Preparation!.CandidatePools[claim.CandidatePoolIndex!.Value];
        pool.Complete.Should().BeFalse();
        pool.Selection.NameCandidatesTruncated.Should().BeTrue();
        pool.Facts.Should().HaveCount(pool.Selection.Items.Count);
        claim
            .Comparisons.Select(pair => pair.CandidateUuid)
            .Should()
            .BeEquivalentTo(pool.Selection.Items.Select(hit => (Guid?)hit.Uuid));
        claim.Decision.Action.Should().Be(SourceActorResolutionAction.Unresolved);
        claim.Decision.PersonUuid.Should().BeNull();
        (await AcceptedState()).Should().Be(before);
    }

    [Test]
    public async Task ConditionalRosterKeepsItsQualifierAndRoleFreeRelationshipInThePublishedProof()
    {
        const string qualifier = "-- and some or all of the following --";
        var source = await Source(
            $"Band line-up:\n{qualifier}\nJon Cunningham (aka Corn Mo) - vocals"
        );
        var extraction = await Extract(source);

        var result = await resolver.ResolveAsync(Guid.NewGuid(), extraction.AttemptUuid);

        var scope = result
            .Preparation!.Inputs.Single()
            .Scopes.Single(value => value.Claims.Count != 0);
        var assertion = scope.OriginalScope.Extraction.Assertions.Single();
        assertion.Context.Qualifiers.Should().ContainSingle().Which.Quote.Should().Be(qualifier);
        assertion
            .Relationships.Should()
            .OnlyContain(relationship =>
                relationship.Relationship == ActorReferenceRelationship.ExplicitAlias
                && relationship.Roles.Count == 0
            );
        scope
            .Fields.Should()
            .Contain(field => field.OriginalText.Contains(qualifier, StringComparison.Ordinal));
        (await people.ReadCreditContextAsync(source.SourceUuid)).Credits.Should().BeEmpty();
    }

    [Test]
    public async Task RepeatedFieldsKeepTheirOriginalAssertionsAndWholeFieldProof()
    {
        var source = await Source(Personnel("Repeated Robin"));
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "UPDATE sources SET taper_notes=@text WHERE id=@SourceId",
                new { text = Personnel("Repeated Robin"), source.SourceId }
            )
        );
        var extraction = await Extract(source);
        var original = extraction.Extraction.SourceActorAliases!;

        var attempt = await resolver.ResolveAsync(Guid.NewGuid(), extraction.AttemptUuid);

        var input = attempt.Preparation!.Inputs.Single();
        attempt.Preparation.ExtractionTicket.Should().BeEquivalentTo(extraction.Ticket);
        input.AliasPayloadPresent.Should().BeTrue();
        input.Scopes.Select(scope => scope.OriginalScope).Should().BeEquivalentTo(original);
        var claims = Claims(attempt.Preparation).ToArray();
        claims.Should().HaveCount(2);
        claims.Select(claim => claim.Coordinates.FieldJsonPointer).Distinct().Should().HaveCount(2);
        claims.Select(claim => claim.Coordinates.AssertionUuid).Distinct().Should().HaveCount(2);
        foreach (var scope in input.Scopes)
        {
            foreach (var claim in scope.Claims)
            {
                var assertion = scope.OriginalScope.Extraction.Assertions[claim.AssertionIndex];
                claim
                    .Coordinates.InputRevisionUuid.Should()
                    .Be(scope.OriginalScope.InputRevisionUuid);
                claim.Coordinates.AssertionUuid.Should().Be(assertion.AssertionUuid);
                claim.Coordinates.ActorReferenceUuid.Should().Be(assertion.ActorReferenceUuid);
                claim.Coordinates.FieldSha256.Should().Be(assertion.FieldSha256);
                var field = scope.Fields.Single(value =>
                    value.Evidence.JsonPointer == claim.Coordinates.FieldJsonPointer
                );
                field.OriginalText.Should().Be(Personnel("Repeated Robin"));
                field.FieldSha256.Should().Be(assertion.FieldSha256);
                field.Evidence.Quote.Should().Be(field.OriginalText);
                claim.Projection.Query.Should().NotBeNull();
            }
        }
    }

    [Test]
    public async Task UnboundFileScopeIsResolvedFromItsOriginalAccountAlongsideTheLegacyInput()
    {
        var context = await ArchiveSource();
        var extraction = await Extract(context.Source);
        var originalInputs = NormalizationRecordingReadSet.Inputs(extraction.Preparation);
        originalInputs.Should().HaveCount(2);
        var unboundFile = new NormalizationProviderDecoder()
            .Decode(context.Input)
            .Recordings.Single()
            .Files.Single(file => file.Filename?.Value == "missing.flac");
        var before = await AcceptedState();

        var attempt = await resolver.ResolveAsync(Guid.NewGuid(), extraction.AttemptUuid);

        attempt.Result!.Disposition.Should().Be(SourceActorResolutionDisposition.Published);
        attempt
            .Preparation!.Inputs.Select(input => input.OriginalInput)
            .Should()
            .BeEquivalentTo(originalInputs);
        var scope = attempt
            .Preparation.Inputs.SelectMany(input => input.Scopes)
            .Single(value => value.OriginalScope.FileOrigin == unboundFile.Origin);
        scope.Claims.Should().ContainSingle();
        scope.OriginalScope.Extraction.Assertions.Single().Name.Should().Be("Unbound Morgan");
        scope.Claims.Single().Coordinates.InputRevisionUuid.Should().Be(context.Input.Uuid);
        scope.Claims.Single().Comparisons.Should().NotBeEmpty();
        (await AcceptedState()).Should().Be(before);
    }

    [Test]
    public async Task LocalSupportingRootKeepsOnlyTheSelectedDirectoryAccount()
    {
        var context = await LocalSource();
        var extraction = await DispatchRetained(
            new(context.Directory.Uuid, context.Directory.Resource),
            new(context.Root.Uuid, context.Root.Resource)
        );
        var before = await AcceptedState();

        var attempt = await resolver.ResolveAsync(Guid.NewGuid(), extraction.AttemptUuid);

        attempt.Result!.Disposition.Should().Be(SourceActorResolutionDisposition.Published);
        var scopes = attempt.Preparation!.Inputs.Single().Scopes;
        scopes
            .SelectMany(scope => scope.OriginalScope.Extraction.Assertions)
            .Select(assertion => assertion.Name)
            .Should()
            .BeEquivalentTo(["Directory Avery", "Selected Robin"]);
        var root = scopes.Single(scope =>
            scope.OriginalScope.InputRevisionUuid == context.Root.Uuid
        );
        root.OriginalScope.FileOrigin.Should().BeNull();
        root.Claims.Should().ContainSingle();
        attempt
            .Preparation.Inputs.Single()
            .OriginalInput.Binding.Inputs.Select(input => input.InputRevisionUuid)
            .Should()
            .Contain([context.Root.Uuid, context.Directory.Uuid]);
        (await AcceptedState()).Should().Be(before);
    }

    [Test]
    public async Task HistoricalMissingAliasPayloadStaysAbsentAndDoesNotClearAcceptedRelations()
    {
        var source = await Source(Personnel("Historical Robin"));
        var extraction = await PreparedLegacyExtraction(source, aliasPayloadPresent: false);
        await PublishExtraction(extraction);
        var person = await Person("Historical Accepted Recorder");
        var creditInput = await fixture.Capture("Operator-confirmed historical credit", "people");
        var credit = new RecordingCredit(
            Guid.NewGuid(),
            source.SourceUuid,
            "taper",
            creditInput.Reference.Quote!,
            person,
            null,
            [creditInput.Reference]
        );
        await fixture.PublishPeople(people, creditInput, new([], [], [], [credit]));
        var before = await AcceptedState();

        var attempt = await resolver.ResolveAsync(Guid.NewGuid(), extraction.AttemptUuid);

        attempt.Result!.Disposition.Should().Be(SourceActorResolutionDisposition.Published);
        var input = attempt.Preparation!.Inputs.Single();
        input.AliasPayloadPresent.Should().BeFalse();
        input.Scopes.Should().BeEmpty();
        (await people.ReadCreditContextAsync(source.SourceUuid))
            .Credits.Should()
            .ContainSingle()
            .Which.Credit.Uuid.Should()
            .Be(credit.Uuid);
        (await AcceptedState()).Should().Be(before);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task UnknownOrUnfinishedExtractionCannotCreateAResolutionAttempt(bool unfinished)
    {
        var extractionUuid = Guid.NewGuid();
        if (unfinished)
        {
            var source = await Source(Personnel("Unfinished Robin"));
            var extraction = await PreparedLegacyExtraction(source);
            await extractions.SaveExtractionAsync(extraction);
            extractionUuid = extraction.AttemptUuid;
            (await extractions.GetAttemptAsync(extractionUuid))!.Result.Should().BeNull();
        }
        var attemptUuid = Guid.NewGuid();
        var before = await AcceptedState();

        var resolve = () => resolver.ResolveAsync(attemptUuid, extractionUuid);

        await resolve.Should().ThrowAsync<ArgumentException>();
        (await store.GetAttemptAsync(attemptUuid)).Should().BeNull();
        (await AcceptedState()).Should().Be(before);
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task EmptyRequestIdsAreRejected(bool emptyAttempt)
    {
        var resolve = () =>
            resolver.ResolveAsync(
                emptyAttempt ? Guid.Empty : Guid.NewGuid(),
                emptyAttempt ? Guid.NewGuid() : Guid.Empty
            );
        await resolve.Should().ThrowAsync<ArgumentException>();
    }

    [Test]
    public async Task ExactReplaySurvivesRestartAndChangedExtractionOrVersionConflicts()
    {
        var source = await Source(Personnel("Replay Robin"));
        var extraction = await Extract(source);
        var attempt = await resolver.ResolveAsync(Guid.NewGuid(), extraction.AttemptUuid);
        var saved = (await store.GetAttemptBatchAsync(attempt.AttemptUuid))!;
        var before = await AcceptedState();
        var owner = await fixture.Work.GetSubjectAsync(
            SourceActorResolutionService.Owner(source.SourceUuid)
        );

        var replay = await NewResolver().ResolveAsync(attempt.AttemptUuid, extraction.AttemptUuid);

        replay.Result!.Replayed.Should().BeTrue();
        replay.Preparation.Should().BeEquivalentTo(attempt.Preparation);
        (await store.GetAttemptBatchAsync(attempt.AttemptUuid)).Should().BeEquivalentTo(saved);
        (await fixture.Work.GetSubjectAsync(SourceActorResolutionService.Owner(source.SourceUuid)))
            .Should()
            .BeEquivalentTo(owner);
        var later = await Extract(source);
        var changedExtraction = () => resolver.ResolveAsync(attempt.AttemptUuid, later.AttemptUuid);
        await changedExtraction.Should().ThrowAsync<NormalizationConflictException>();
        var changedVersion = () =>
            store.GetOrCreateAttemptAsync(
                attempt.AttemptUuid,
                extraction.AttemptUuid,
                "different-source-processing-version"
            );
        await changedVersion.Should().ThrowAsync<NormalizationConflictException>();
        (await AcceptedState()).Should().Be(before);
    }

    [Test]
    public async Task PreCancelledRequestCanResumeWithoutLosingItsOriginalExtraction()
    {
        var source = await Source(Personnel("Cancelled Robin"));
        var extraction = await Extract(source);
        var attemptUuid = Guid.NewGuid();
        await store.GetOrCreateAttemptAsync(
            attemptUuid,
            extraction.AttemptUuid,
            SourceActorResolutionService.Version
        );
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var resolve = () =>
            resolver.ResolveAsync(attemptUuid, extraction.AttemptUuid, cancellation.Token);

        await resolve.Should().ThrowAsync<OperationCanceledException>();

        (await store.GetAttemptAsync(attemptUuid))!.Result.Should().BeNull();
        var resumed = await NewResolver().ResolveAsync(attemptUuid, extraction.AttemptUuid);
        resumed.Result!.Disposition.Should().Be(SourceActorResolutionDisposition.Published);
        resumed.ExtractionAttemptUuid.Should().Be(extraction.AttemptUuid);
    }

    [Test]
    public async Task LaterExtractionCannotRefreshAnEarlierExtractionOwnerObservation()
    {
        var source = await Source(Personnel("Original Receipt Robin"));
        var first = await Extract(source);
        var second = await Extract(source);
        second
            .Preparation.Binding.InputRevisionUuid.Should()
            .Be(first.Preparation.Binding.InputRevisionUuid);

        var stale = await resolver.ResolveAsync(Guid.NewGuid(), first.AttemptUuid);

        stale.Result!.Disposition.Should().Be(SourceActorResolutionDisposition.Stale);
        stale.Preparation!.ExtractionObservation.PublishedWorkUuid.Should().Be(first.AttemptUuid);
        (await store.GetAsync(source.SourceUuid)).Should().BeNull();
        var current = await resolver.ResolveAsync(Guid.NewGuid(), second.AttemptUuid);
        current.Result!.Disposition.Should().Be(SourceActorResolutionDisposition.Published);
    }

    [TestCase("input", true)]
    [TestCase("input", false)]
    [TestCase("input_round_trip", true)]
    [TestCase("input_round_trip", false)]
    [TestCase("catalog", true)]
    [TestCase("catalog", false)]
    [TestCase("pool", true)]
    [TestCase("pool", false)]
    [TestCase("policy", true)]
    [TestCase("policy", false)]
    [TestCase("observed_owner", true)]
    [TestCase("observed_owner", false)]
    public async Task ChangedConsumedFactsInvalidateTheOriginalPreparationAtAdmissionAndPublication(
        string change,
        bool beforeAdmission
    )
    {
        var name = "Freshness " + Guid.NewGuid().ToString("N");
        var person = await Person(name);
        var source = await Source(Personnel(name));
        var extraction = await Extract(source);
        var staged = await StageBeforeAdmission(extraction);
        var originalPreparation = JsonSerializer.Serialize(staged.Preparation);
        if (!beforeAdmission)
        {
            var ticket = await Admit(staged);
            await store.SaveProposalsAsync(
                new(
                    staged.AttemptUuid,
                    source.SourceUuid,
                    extraction.AttemptUuid,
                    staged.ProcessingVersion,
                    staged.Preparation!,
                    ticket
                )
            );
        }
        switch (change)
        {
            case "input":
                await Capture(
                    extraction.Preparation.Binding.Resource,
                    new { description = "Changed retained bytes" }
                );
                break;
            case "input_round_trip":
                var originalInput = (
                    await evidence.GetInputAsync(extraction.Preparation.Binding.InputRevisionUuid)
                )!;
                await Capture(
                    originalInput.Resource,
                    new { description = "Intermediate retained bytes" }
                );
                var currentInput = (await evidence.GetCurrentInputAsync(originalInput.Resource))!;
                var restored = await evidence.CaptureInputAsync(
                    new(
                        Guid.NewGuid(),
                        originalInput.Resource,
                        originalInput.Payload,
                        originalInput.MediaType,
                        originalInput.CaptureVersion,
                        currentInput.Generation,
                        DateTimeOffset.UtcNow
                    )
                );
                restored.Revision.Uuid.Should().Be(originalInput.Uuid);
                restored
                    .Fetch.ResourceGeneration.Should()
                    .BeGreaterThan(staged.Preparation!.ExtractionTicket.Inputs.Single().Generation);
                break;
            case "catalog":
                await fixture.Db.WithWriteConnection(c =>
                    c.ExecuteAsync(
                        "UPDATE sources SET description='Changed original catalog field' WHERE id=@SourceId",
                        source
                    )
                );
                break;
            case "pool":
                await Person(name);
                break;
            case "policy":
                await SelectPolicy(Policy());
                break;
            case "observed_owner":
                var owner = (
                    await fixture.Work.GetSubjectAsync(
                        new NormalizationSubjectKey("people", "person", person)
                    )
                )!;
                await fixture.Work.ApplyReviewAsync(
                    new(
                        Guid.NewGuid(),
                        owner.SubjectUuid,
                        owner.EditorialRevision,
                        "reject",
                        JsonSerializer.SerializeToElement(
                            new { fixture = "observed owner changed" }
                        ),
                        "Change an actually consumed candidate owner",
                        Operator
                    ),
                    (_, _, _) => Task.CompletedTask
                );
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(change));
        }
        var before = await AcceptedState();

        var result = await NewResolver().ResolveAsync(staged.AttemptUuid, extraction.AttemptUuid);

        result.Result!.Disposition.Should().Be(SourceActorResolutionDisposition.Stale);
        JsonSerializer.Serialize(result.Preparation).Should().Be(originalPreparation);
        (await store.GetAsync(source.SourceUuid)).Should().BeNull();
        if (!beforeAdmission)
        {
            (await store.GetAttemptBatchAsync(staged.AttemptUuid)).Should().NotBeNull();
        }
        (await NewResolver().ResolveAsync(staged.AttemptUuid, extraction.AttemptUuid))
            .Result!.Replayed.Should()
            .BeTrue();
        (await AcceptedState()).Should().Be(before);
    }

    [Test]
    public async Task ComputedCheckpointSurvivesPublicationRollbackAndResumesAfterRestart()
    {
        var source = await Source(Personnel("Rollback Robin"));
        var extraction = await Extract(source);
        var staged = await StageBeforeAdmission(extraction);
        var ticket = await Admit(staged);
        var batch = new SourceActorResolutionBatch(
            staged.AttemptUuid,
            source.SourceUuid,
            extraction.AttemptUuid,
            staged.ProcessingVersion,
            staged.Preparation!,
            ticket
        );
        await store.SaveProposalsAsync(batch);
        var before = await AcceptedState();
        var publish = () =>
            fixture.Work.TryPublishAsync(
                staged.AttemptUuid,
                async (c, t, ct) =>
                {
                    await store.WritePublishedAsync(c, t, batch, ct);
                    throw new InvalidOperationException(
                        "synthetic source proposal publication rollback"
                    );
                }
            );

        await publish.Should().ThrowAsync<InvalidOperationException>();

        (await store.GetAsync(source.SourceUuid)).Should().BeNull();
        (await store.GetAttemptAsync(staged.AttemptUuid))!.Result.Should().BeNull();
        (await store.GetAttemptBatchAsync(staged.AttemptUuid)).Should().BeEquivalentTo(batch);
        var resumed = await NewResolver().ResolveAsync(staged.AttemptUuid, extraction.AttemptUuid);
        resumed.Result!.Disposition.Should().Be(SourceActorResolutionDisposition.Published);
        resumed.Preparation.Should().BeEquivalentTo(staged.Preparation);
        (await AcceptedState()).Should().Be(before);
    }

    [TestCase("accepted", SourceActorResolutionAction.Review)]
    [TestCase("rejected", SourceActorResolutionAction.Rejected)]
    [TestCase("retracted", SourceActorResolutionAction.Retracted)]
    public async Task ExactSourceReviewTakesPrecedenceAndRetainsItsOriginalSelectedIdentity(
        string state,
        SourceActorResolutionAction expectedAction
    )
    {
        var selected = await Person("Xylophone Quartz");
        var source = await Source(Personnel("Reviewed Source Robin"));
        var extraction = await Extract(source);
        var staged = await StageBeforeAdmission(extraction);
        await Admit(staged);
        var review = await SeedSourceReview(staged, state, state == "accepted" ? selected : null);
        var before = await AcceptedState();

        var result = await resolver.ResolveAsync(Guid.NewGuid(), extraction.AttemptUuid);

        result.Result!.Disposition.Should().Be(SourceActorResolutionDisposition.Published);
        var claim = Claims(result.Preparation!).Single();
        claim.Decision.Action.Should().Be(expectedAction);
        claim.SourceReviewStateUuid.Should().Be(review.Uuid);
        claim.SourceReviewRevision.Should().Be(review.ReviewRevision);
        result.Preparation!.SourceReviews.Reviews.Should().ContainEquivalentOf(review);
        if (state == "accepted")
        {
            claim.Decision.Reason.Should().Be("reviewed_source_acceptance");
            claim.Decision.PersonUuid.Should().Be(selected);
            var pool = result.Preparation.CandidatePools[claim.CandidatePoolIndex!.Value];
            pool.Facts.Should().NotContain(fact => fact.Hit.Uuid == selected);
        }
        (await AcceptedState()).Should().Be(before);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task DifferentRevisionOfAReviewedFieldIsHeldWithoutInferringCorrespondence(
        bool changeFieldText
    )
    {
        var selected = await Person("Prior Reviewed Target");
        var source = await Source(Personnel("Original Robin"));
        var extraction = await Extract(source);
        var staged = await StageBeforeAdmission(extraction);
        await Admit(staged);
        var review = await SeedSourceReview(staged, "accepted", selected);
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "UPDATE sources SET description=@description,taper='Another captured field changed' WHERE id=@SourceId",
                new
                {
                    description = Personnel(changeFieldText ? "Changed Avery" : "Original Robin"),
                    source.SourceId,
                }
            )
        );
        var changed = await Extract(source);
        changed
            .Preparation.Binding.InputRevisionUuid.Should()
            .NotBe(extraction.Preparation.Binding.InputRevisionUuid);
        var before = await AcceptedState();

        var result = await resolver.ResolveAsync(Guid.NewGuid(), changed.AttemptUuid);

        result.Result!.Disposition.Should().Be(SourceActorResolutionDisposition.Published);
        var claim = Claims(result.Preparation!).Single();
        claim.Decision.Action.Should().Be(SourceActorResolutionAction.Review);
        claim.Decision.Reason.Should().Be("reviewed_source_field_requires_correspondence");
        claim.Decision.PersonUuid.Should().BeNull();
        result.Preparation!.SourceReviews.Reviews.Should().ContainEquivalentOf(review);
        review
            .Coordinates.InputRevisionUuid.Should()
            .Be(extraction.Preparation.Binding.InputRevisionUuid);
        (await AcceptedState()).Should().Be(before);
    }

    [Test]
    public async Task UnsupportedHistoricalProducerReviewRemainsAFullFieldHoldWithoutUsingTheCurrentParser()
    {
        const string historicalProducer = "unsupported-historical-source-actor-producer-v0";
        var selected = await Person("Historical Selected Target");
        var source = await Source(Personnel("Historical Field Robin"));
        var extraction = await Extract(source);
        var staged = await StageBeforeAdmission(extraction);
        await Admit(staged);
        // This is a retained migration53 historical authority fixture. Its old producer
        // coordinates and scope agree; the current published extraction remains untouched.
        var historical = await SeedSourceReview(
            staged,
            "accepted",
            selected,
            producerOverride: historicalProducer
        );
        var before = await AcceptedState();

        var result = await resolver.ResolveAsync(Guid.NewGuid(), extraction.AttemptUuid);

        result.Result!.Disposition.Should().Be(SourceActorResolutionDisposition.Published);
        var claim = Claims(result.Preparation!).Single();
        claim.Decision.Action.Should().Be(SourceActorResolutionAction.Review);
        claim.Decision.Reason.Should().Be("reviewed_source_field_requires_correspondence");
        claim.Decision.PersonUuid.Should().BeNull();
        claim.SourceReviewRevision.Should().Be(0);
        claim.SourceReviewStateUuid.Should().BeNull();
        result
            .Preparation!.SourceReviews.Reviews.Should()
            .ContainSingle()
            .Which.Should()
            .BeEquivalentTo(historical);
        result.Preparation.SourceReviews.Correspondences.Should().BeNullOrEmpty();
        historical.Coordinates.ProducerVersion.Should().Be(historicalProducer);
        historical.OriginalScope.ProducerVersion.Should().Be(historicalProducer);
        (await extractions.GetAttemptBatchAsync(extraction.AttemptUuid))
            .Should()
            .BeEquivalentTo(extraction);
        (await AcceptedState()).Should().Be(before);
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task FirstSourceRejectionInvalidatesAnAlreadyPreparedRevisionZeroProposal(
        bool beforeAdmission
    )
    {
        var source = await Source(Personnel("First Review Robin"));
        var extraction = await Extract(source);
        // Establish a legitimate contributed owner without a source review. A second attempt
        // then observes actual owner state and review absence together before admission.
        var ownerPreparation = await StageBeforeAdmission(extraction);
        await Admit(ownerPreparation);
        var staged = await StageBeforeAdmission(extraction);
        Claims(staged.Preparation!).Should().OnlyContain(claim => claim.SourceReviewRevision == 0);
        if (!beforeAdmission)
        {
            var ticket = await Admit(staged);
            await store.SaveProposalsAsync(
                new(
                    staged.AttemptUuid,
                    source.SourceUuid,
                    extraction.AttemptUuid,
                    staged.ProcessingVersion,
                    staged.Preparation!,
                    ticket
                )
            );
        }
        await SeedSourceReview(staged, "rejected");
        var before = await AcceptedState();

        var result = await resolver.ResolveAsync(staged.AttemptUuid, extraction.AttemptUuid);

        result.Result!.Disposition.Should().Be(SourceActorResolutionDisposition.Stale);
        (await store.GetAsync(source.SourceUuid)).Should().BeNull();
        var current = await resolver.ResolveAsync(Guid.NewGuid(), extraction.AttemptUuid);
        current.Result!.Disposition.Should().Be(SourceActorResolutionDisposition.Published);
        Claims(current.Preparation!)
            .Single()
            .Decision.Action.Should()
            .Be(SourceActorResolutionAction.Rejected);
        (await AcceptedState()).Should().Be(before);
    }

    [Test]
    public async Task UnrepresentedContributorBindingsRemainOwnedWithoutPartialReplacement()
    {
        var source = await Source(Personnel("Owned Robin"));
        var extraction = await Extract(source);
        await resolver.ResolveAsync(Guid.NewGuid(), extraction.AttemptUuid);
        var published = (await store.GetAsync(source.SourceUuid))!;
        var owner = SourceActorResolutionService.Owner(source.SourceUuid);
        var membership = await fixture.Work.GetInputContributionsAsync(owner);
        var contributor = membership.Contributions.Single();
        var extra = await fixture.Capture("Evidence outside the exact extraction", "people");
        await fixture.Work.BeginContributedWorkAsync(
            Guid.NewGuid(),
            owner,
            SourceActorResolutionService.Version,
            new(
                contributor.Contributor,
                contributor.Revision,
                [.. contributor.Resources, extra.Resource],
                contributor.DependencySubjectUuids
            ),
            membership.Owner!.DesiredGeneration
        );
        var configured = await fixture.Work.GetInputContributionsAsync(owner);
        var before = await AcceptedState();

        var result = await resolver.ResolveAsync(Guid.NewGuid(), extraction.AttemptUuid);

        result.Result!.Disposition.Should().Be(SourceActorResolutionDisposition.Unresolved);
        (await fixture.Work.GetInputContributionsAsync(owner)).Should().BeEquivalentTo(configured);
        (await store.GetAsync(source.SourceUuid)).Should().BeEquivalentTo(published);
        (await AcceptedState()).Should().Be(before);
    }

    [TestCase(99, true)]
    [TestCase(100, false)]
    [TestCase(101, false)]
    public async Task RetainedEmptyHeadersBoundTheCompleteContributorUnion(
        int retainedHeaders,
        bool withinLimit
    )
    {
        var source = await Source(Personnel("Header Capacity Robin"));
        var extraction = await Extract(source);
        var owner = SourceActorResolutionService.Owner(source.SourceUuid);
        var emptyContributors = Enumerable
            .Range(0, retainedHeaders)
            .Select(index => new NormalizationPreparedInputContribution(
                new(
                    new("fixture", "retained_empty_source_header", index.ToString("D3")),
                    0,
                    [],
                    []
                ),
                []
            ))
            .ToArray();
        await fixture.Work.BeginContributedBatchAsync(
            Guid.NewGuid(),
            owner,
            SourceActorResolutionService.Version,
            emptyContributors,
            expectedContributionRevisions: []
        );
        var auditLimits = new NormalizationInputContributionsLimits(1000, 100000);
        var membership = await fixture.Work.GetInputContributionsAsync(owner, limits: auditLimits);
        membership.Contributions.Should().HaveCount(retainedHeaders);
        membership.Contributions.Should().OnlyContain(value => value.Resources.Count == 0);
        var before = await AcceptedState();

        var result = await resolver.ResolveAsync(Guid.NewGuid(), extraction.AttemptUuid);

        var after = await fixture.Work.GetInputContributionsAsync(owner, limits: auditLimits);
        if (withinLimit)
        {
            result.Result!.Disposition.Should().Be(SourceActorResolutionDisposition.Published);
            after.Contributions.Should().HaveCount(100);
            after
                .Contributions.Where(value =>
                    value.Contributor.ResourceType == "retained_empty_source_header"
                )
                .Should()
                .BeEquivalentTo(membership.Contributions);
        }
        else
        {
            // A current101-header snapshot exceeds the reader's profile. At100 headers the
            // read fits, but adding this extraction's new contributor would produce101.
            result.Result!.Disposition.Should().Be(SourceActorResolutionDisposition.Unresolved);
            result.Result.Reason.Should().Be("contribution_capacity_exceeded");
            result.Preparation.Should().BeNull();
            (await store.GetAttemptBatchAsync(result.AttemptUuid)).Should().BeNull();
            (await store.GetAsync(source.SourceUuid)).Should().BeNull();
            after.Should().BeEquivalentTo(membership);
        }
        (await AcceptedState()).Should().Be(before);
    }

    [TestCase(1000, true)]
    [TestCase(1001, false)]
    public async Task CompleteClaimInventoryIsPublishedWithinTheLimitAndExplicitlyRefusedAboveIt(
        int count,
        bool withinLimit
    )
    {
        // Repeated statements have distinct original spans and assertion UUIDs. The field is
        // below the extractor's text bound; this exercises the resolver's total claim limit.
        var actor = "Actor " + Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        var alias = "Alias " + Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        var statement = $"{actor} aka {alias} - vocals";
        var text = "Personnel:\n" + string.Join("\n", Enumerable.Repeat(statement, count));
        var source = await Source(text);
        var extraction = await Extract(source);
        var original = extraction
            .Extraction.SourceActorAliases!.SelectMany(scope => scope.Extraction.Assertions)
            .ToArray();
        original.Should().HaveCount(count);
        var query = PeopleActorProjection.Project(original[0], extraction.ArtistUuid).Query!;
        var candidatesBeforeResolution = await candidates.FindPeopleFactsAsync(query);
        candidatesBeforeResolution
            .Facts.Should()
            .BeEmpty(
                "the claim-count boundary must use an empty actual pool rather than shared fixture identities"
            );
        var before = await AcceptedState();

        var result = await resolver.ResolveAsync(Guid.NewGuid(), extraction.AttemptUuid);

        if (withinLimit)
        {
            result.Result!.Disposition.Should().Be(SourceActorResolutionDisposition.Published);
            Claims(result.Preparation!)
                .Select(claim => claim.Coordinates.AssertionUuid)
                .Should()
                .BeEquivalentTo(original.Select(assertion => assertion.AssertionUuid));
            (await store.GetAsync(source.SourceUuid)).Should().NotBeNull();
        }
        else
        {
            result.Result!.Disposition.Should().Be(SourceActorResolutionDisposition.Unresolved);
            result.Result.Reason.Should().Be("source_actor_claim_limit");
            result.Preparation.Should().BeNull();
            (await store.GetAttemptBatchAsync(result.AttemptUuid)).Should().BeNull();
            (await store.GetAsync(source.SourceUuid)).Should().BeNull();
        }
        (await AcceptedState()).Should().Be(before);
    }

    [TestCase(75_000, true, "checkpoint_capacity_exceeded")]
    [TestCase(195_000, false, "source_consumption_capacity_exceeded")]
    [Explicit(
        "Retains several MiB of original field text to exercise separate source-consumption and checkpoint boundaries."
    )]
    public async Task CompleteFileScopeProofRefusesItsExceededByteBudgetWithoutTruncatingThePublishedExtraction(
        int fieldPadding,
        bool fitsConsumption,
        string expectedReason
    )
    {
        const int fileCount = 100;
        var files = Enumerable
            .Range(0, fileCount)
            .Select(index =>
                (object)
                    new
                    {
                        name = $"unbound-{index:D3}.flac",
                        title = $"Unbound file {index:D3}",
                        format = "Flac",
                        // Each original field has different bytes and is below the extractor's field
                        // limit. Its whole-field coverage and retained field proof both remain required.
                        description = $"Original file account {index:D3}\n"
                            + new string((char)('A' + index % 26), fieldPadding),
                    }
            )
            .ToArray();
        var context = await ArchiveSource(files);
        context.Input.Payload.Length.Should().BeLessThan(32 * 1024 * 1024);
        var extraction = await Extract(context.Source);
        var originalGroups = new[] { extraction.Extraction }
            .Concat((extraction.AdditionalExtractions ?? []).Select(group => group.Extraction))
            .ToArray();
        originalGroups
            .SelectMany(group => group.SourceActorAliases ?? [])
            .Count(scope =>
                scope.FileOrigin != null && scope.InputRevisionUuid == context.Input.Uuid
            )
            .Should()
            .Be(fileCount);
        originalGroups
            .SelectMany(group => group.SourceActorAliases ?? [])
            .SelectMany(scope => scope.Extraction.Assertions)
            .Should()
            .BeEmpty();
        var consumedBytes = await fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<int>(
                $"SELECT octet_length(({SourceActorResolutionStore.SourceActorConsumptionProjectionSql})::text) FROM normalization.recording_processing_attempts WHERE attempt_uuid=@AttemptUuid",
                new { extraction.AttemptUuid }
            )
        );
        TestContext.Out.WriteLine(
            $"Source capacity fixture: payload={context.Input.Payload.Length} bytes; consumption={consumedBytes} bytes; field padding={fieldPadding}."
        );
        if (fitsConsumption)
        {
            consumedBytes.Should().BeLessThan(32 * 1024 * 1024);
        }
        else
        {
            consumedBytes.Should().BeGreaterThan(32 * 1024 * 1024);
        }
        var before = await AcceptedState();

        var result = await resolver.ResolveAsync(Guid.NewGuid(), extraction.AttemptUuid);

        result.Result!.Disposition.Should().Be(SourceActorResolutionDisposition.Unresolved);
        result.Result.Reason.Should().Be(expectedReason);
        result.Preparation.Should().BeNull();
        (await store.GetAttemptBatchAsync(result.AttemptUuid)).Should().BeNull();
        (await store.GetAsync(context.Source.SourceUuid)).Should().BeNull();
        (
            await fixture.Work.GetSubjectAsync(
                SourceActorResolutionService.Owner(context.Source.SourceUuid)
            )
        )
            .Should()
            .BeNull();
        var replay = await NewResolver().ResolveAsync(result.AttemptUuid, extraction.AttemptUuid);
        replay.Result!.Replayed.Should().BeTrue();
        replay.Result.Reason.Should().Be(expectedReason);
        replay.Preparation.Should().BeNull();
        (await extractions.GetAttemptBatchAsync(extraction.AttemptUuid))
            .Should()
            .BeEquivalentTo(extraction);
        (await AcceptedState()).Should().Be(before);
    }

    [
        Test,
        Explicit(
            "Publishes a producer batch above 32 MiB while its source-actor consumption remains below that bound."
        )
    ]
    public async Task LargeTitleProducerKeepsItsFullPublicBatchAndPublishesItsSmallerSourceConsumption()
    {
        const int fileCount = 2500;
        var names = Enumerable
            .Range(0, fileCount)
            .Select(index => index == 0 ? "file.mp3" : $"large-title-{index:D4}.mp3")
            .ToArray();
        var title = "Unrelated title " + new string('Q', 3000);
        var files = names
            .Select(name =>
                (object)
                    new
                    {
                        name,
                        title,
                        format = "VBR MP3",
                    }
            )
            .ToArray();
        var context = await ArchiveSource(files, names);
        context.Input.Payload.Length.Should().BeLessThan(32 * 1024 * 1024);
        var extraction = (await extractions.GetAsync(context.Source.SourceUuid))!;
        extraction.Extraction.Titles.Should().HaveCount(fileCount);
        extraction
            .Extraction.SourceActorAliases!.SelectMany(scope => scope.Extraction.Assertions)
            .Should()
            .BeEmpty();
        var sizes = await fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<ProducerSizes>(
                $"SELECT octet_length(batch::text) AS FullBytes,octet_length(({SourceActorResolutionStore.SourceActorConsumptionProjectionSql})::text) AS ConsumedBytes FROM normalization.recording_processing_attempts WHERE attempt_uuid=@AttemptUuid",
                new { extraction.AttemptUuid }
            )
        );
        TestContext.Out.WriteLine(
            $"Title producer fixture: payload={context.Input.Payload.Length} bytes; full producer={sizes.FullBytes} bytes; source consumption={sizes.ConsumedBytes} bytes."
        );
        sizes.FullBytes.Should().BeGreaterThan(32 * 1024 * 1024);
        sizes.ConsumedBytes.Should().BeLessThan(32 * 1024 * 1024);
        var before = await AcceptedState();

        var result = await resolver.ResolveAsync(Guid.NewGuid(), extraction.AttemptUuid);

        result.Result!.Disposition.Should().Be(SourceActorResolutionDisposition.Published);
        result
            .Preparation!.Inputs.Single()
            .OriginalInput.Binding.Files.Should()
            .HaveCount(fileCount);
        Claims(result.Preparation).Should().BeEmpty();
        (await extractions.GetAttemptBatchAsync(extraction.AttemptUuid))
            .Should()
            .BeEquivalentTo(extraction);
        (await AcceptedState()).Should().Be(before);
    }

    [TestCase("missing", "missing")]
    [TestCase("null", "null")]
    [TestCase("empty", "empty")]
    [TestCase("groups", "present")]
    [TestCase("malformed", "present")]
    public async Task SourceConsumptionProjectionPreservesAbsenceOrderAndEveryOtherOriginalValue(
        string additionalShape,
        string aliasShape
    )
    {
        var original = JsonNode.Parse(
            """
            {
                "Extraction":{"Credits":[{"Name":"excluded"}],"UnresolvedCredits":[{"Reason":"excluded"}],"Titles":[{"Name":"excluded"}],"DecodeIssues":[{"Reason":"retained"}],"SourceActorAliases":[{"Name":"first"},{"Name":"second"}]},
                "Preparation":{"Credits":"unrelated nested value","Titles":"unrelated nested value"},
                "Ticket":{"Inputs":[{"Generation":7}]}
            }
            """
        )!;
        var expected = JsonNode.Parse(
            """
            {
                "Extraction":{"DecodeIssues":[{"Reason":"retained"}],"SourceActorAliases":[{"Name":"first"},{"Name":"second"}]},
                "Preparation":{"Credits":"unrelated nested value","Titles":"unrelated nested value"},
                "Ticket":{"Inputs":[{"Generation":7}]}
            }
            """
        )!;
        if (aliasShape == "missing")
        {
            original["Extraction"]!.AsObject().Remove("SourceActorAliases");
            expected["Extraction"]!.AsObject().Remove("SourceActorAliases");
        }
        else if (aliasShape is "null" or "empty")
        {
            original["Extraction"]!["SourceActorAliases"] = JsonNode.Parse(
                aliasShape == "null" ? "null" : "[]"
            );
            expected["Extraction"]!["SourceActorAliases"] = JsonNode.Parse(
                aliasShape == "null" ? "null" : "[]"
            );
        }
        if (additionalShape != "missing")
        {
            var additional = additionalShape switch
            {
                "null" => "null",
                "empty" => "[]",
                "malformed" => "{\"retain_invalid_shape\":true}",
                _ => """
                    [
                        {"InputRevisionUuid":"second","Extraction":{"Credits":[1],"Titles":[2],"SourceActorAliases":[{"Name":"second group"}],"DecodeIssues":[]}},
                        {"InputRevisionUuid":"third","Extraction":null},
                        {"InputRevisionUuid":"second","Extraction":{"UnresolvedCredits":[3],"SourceActorAliases":null,"DecodeIssues":[{"Reason":"retained"}]}}
                    ]
                    """,
            };
            var expectedAdditional =
                additionalShape == "groups"
                    ? """
                        [
                            {"InputRevisionUuid":"second","Extraction":{"SourceActorAliases":[{"Name":"second group"}],"DecodeIssues":[]}},
                            {"InputRevisionUuid":"third","Extraction":null},
                            {"InputRevisionUuid":"second","Extraction":{"SourceActorAliases":null,"DecodeIssues":[{"Reason":"retained"}]}}
                        ]
                        """
                    : additional;
            original["AdditionalExtractions"] = JsonNode.Parse(additional);
            expected["AdditionalExtractions"] = JsonNode.Parse(expectedAdditional);
        }

        var projected = await fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<string>(
                $"SELECT ({SourceActorResolutionStore.SourceActorConsumptionProjectionSql})::text FROM (SELECT CAST(@payload AS jsonb) AS batch) AS original",
                new { payload = original.ToJsonString() }
            )
        );

        JsonNode.DeepEquals(JsonNode.Parse(projected), expected).Should().BeTrue();
    }

    [TestCase("omit_scope")]
    [TestCase("invent_selection")]
    [TestCase("foreign_source")]
    [TestCase("refresh_extraction")]
    [TestCase("refresh_input_generation")]
    public async Task InternalStoreRejectsInconsistentClaimsAndKeepsTheFirstAuthenticPreparation(
        string change
    )
    {
        var source = await Source(Personnel("Authenticated Robin"));
        var extraction = await Extract(source);
        var published = await resolver.ResolveAsync(Guid.NewGuid(), extraction.AttemptUuid);
        var original = published.Preparation!;
        var input = original.Inputs.Single();
        var scope = input.Scopes.Single(value => value.Claims.Count != 0);
        var claim = scope.Claims.Single();
        var changedClaim = change switch
        {
            "invent_selection" => claim with
            {
                Decision = new(
                    SourceActorResolutionAction.MatchExisting,
                    "forged_policy",
                    Guid.NewGuid(),
                    null
                ),
            },
            "foreign_source" => claim with
            {
                Coordinates = claim.Coordinates with { SourceUuid = Guid.NewGuid() },
            },
            _ => claim,
        };
        var invalid = original with
        {
            Inputs =
            [
                input with
                {
                    Scopes =
                        change == "omit_scope"
                            ? []
                            : input
                                .Scopes.Select(value =>
                                    value == scope ? value with { Claims = [changedClaim] } : value
                                )
                                .ToArray(),
                },
            ],
            ExtractionObservation =
                change == "refresh_extraction"
                    ? original.ExtractionObservation with
                    {
                        PublishedWorkUuid = Guid.NewGuid(),
                    }
                    : original.ExtractionObservation,
            ExtractionTicket =
                change == "refresh_input_generation"
                    ? original.ExtractionTicket with
                    {
                        Inputs = original
                            .ExtractionTicket.Inputs.Select(value =>
                                value with
                                {
                                    Generation = value.Generation + 1,
                                }
                            )
                            .ToArray(),
                    }
                    : original.ExtractionTicket,
        };
        var attempt = await store.GetOrCreateAttemptAsync(
            Guid.NewGuid(),
            extraction.AttemptUuid,
            SourceActorResolutionService.Version
        );

        var save = () => store.SavePreparationAsync(attempt.AttemptUuid, invalid);

        await save.Should().ThrowAsync<ArgumentException>();
        (await store.GetAttemptAsync(attempt.AttemptUuid))!.Preparation.Should().BeNull();
        // Reusing this authentic preparation tests storage authentication only. Its original
        // contribution expectations are deliberately not reused for a fresh publication.
        var saved = await store.SavePreparationAsync(attempt.AttemptUuid, original);
        saved.Preparation.Should().BeEquivalentTo(original);
        var replacement = await store.SavePreparationAsync(
            attempt.AttemptUuid,
            original with
            {
                ExpectedConfigurationGeneration = 999,
            }
        );
        replacement.Preparation.Should().BeEquivalentTo(original);
    }

    private SourceActorResolutionService NewResolver() =>
        new(candidates, policies, new(new(), new()), store, fixture.Work);

    private static IEnumerable<SourceActorResolutionClaim> Claims(
        SourceActorResolutionPreparation preparation
    ) => preparation.Inputs.SelectMany(input => input.Scopes).SelectMany(scope => scope.Claims);

    private async Task<SourceActorResolutionAttempt> StageBeforeAdmission(
        NormalizationRecordingProposalBatch extraction
    )
    {
        var attemptUuid = Guid.NewGuid();
        await using var gate = new NpgsqlConnection(LocalTestDatabase.ConnectionString);
        await gate.OpenAsync();
        await using var transaction = await gate.BeginTransactionAsync();
        // This is Work.LockConfiguration's existing advisory key, not an imitation of
        // resolution internals. Reading and saving the complete preparation does not take it.
        await gate.ExecuteAsync(
            "SELECT pg_advisory_xact_lock(hashtextextended(@key,0))",
            new
            {
                key = $"normalization:configuration:people:source_actor_resolution:{extraction.SourceUuid}",
            },
            transaction
        );
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var pending = resolver.ResolveAsync(
            attemptUuid,
            extraction.AttemptUuid,
            cancellation.Token
        );
        try
        {
            await WaitForConfigurationLock(gate.ProcessID);
            var saved = (await store.GetAttemptAsync(attemptUuid))!;
            saved.Preparation.Should().NotBeNull();
            saved.Result.Should().BeNull();
            cancellation.Cancel();
            var cancelled = () => pending;
            await cancelled.Should().ThrowAsync<OperationCanceledException>();
            return saved;
        }
        finally
        {
            cancellation.Cancel();
            await transaction.RollbackAsync();
            try
            {
                await pending;
            }
            catch (OperationCanceledException)
            {
                // Cancellation intentionally leaves the first preparation resumable.
            }
        }
    }

    private static async Task WaitForConfigurationLock(int blockerPid)
    {
        await using var observer = new NpgsqlConnection(LocalTestDatabase.ConnectionString);
        await observer.OpenAsync();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (
                await observer.ExecuteScalarAsync<bool>(
                    "SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE datname=current_database() AND @blockerPid=ANY(pg_blocking_pids(pid)) AND query LIKE '%pg_advisory_xact_lock%')",
                    new { blockerPid }
                )
            )
            {
                return;
            }
            await Task.Delay(10);
        }
        Assert.Fail(
            "The resolver must save its preparation before waiting for Work configuration admission."
        );
    }

    private Task<NormalizationWorkTicket> Admit(SourceActorResolutionAttempt attempt) =>
        fixture.Work.BeginContributedBatchAsync(
            attempt.AttemptUuid,
            SourceActorResolutionService.Owner(attempt.SourceUuid),
            attempt.ProcessingVersion,
            SourceActorResolutionReadSet.Contributions(attempt.Preparation!),
            attempt.Preparation!.ExpectedConfigurationGeneration,
            expectedContributionRevisions: attempt.Preparation.ExpectedContributions
        );

    // Fixture setup uses migration 53's existing Work receipt/CAS contract with exact original
    // proof. This is not the trusted human-review wrapper under test in the separate suite.
    private async Task<SourceActorAliasReviewState> SeedSourceReview(
        SourceActorResolutionAttempt attempt,
        string state,
        Guid? selected = null,
        string? producerOverride = null
    )
    {
        var scope = attempt
            .Preparation!.Inputs.Single()
            .Scopes.Single(value => value.Claims.Count != 0);
        var claim = scope.Claims.Single();
        var assertion = scope.OriginalScope.Extraction.Assertions[claim.AssertionIndex];
        // An override models a retained historical53 row only. It does not assert that
        // the current extraction was published by that unsupported producer.
        var coordinates =
            producerOverride == null
                ? claim.Coordinates
                : claim.Coordinates with
                {
                    ProducerVersion = producerOverride,
                };
        var originalScope =
            producerOverride == null
                ? scope.OriginalScope
                : scope.OriginalScope with
                {
                    ProducerVersion = producerOverride,
                };
        var uuid = Guid.NewGuid();
        var revision = 0L;
        var lastOperation = Guid.Empty;
        var ownerUuid = Guid.Empty;
        foreach (
            var nextState in state == "retracted"
                ? new[] { "rejected", "retracted" }
                : new[] { state }
        )
        {
            var owner = (
                await fixture.Work.GetSubjectAsync(
                    SourceActorResolutionService.Owner(attempt.SourceUuid)
                )
            )!;
            ownerUuid = owner.SubjectUuid;
            lastOperation = Guid.NewGuid();
            revision++;
            var action = nextState switch
            {
                "accepted" => "accept",
                "rejected" => "reject",
                "retracted" => "retract",
                _ => throw new ArgumentOutOfRangeException(nameof(state)),
            };
            await fixture.Work.ApplyReviewAsync(
                new(
                    lastOperation,
                    owner.SubjectUuid,
                    owner.EditorialRevision,
                    action,
                    JsonSerializer.SerializeToElement(
                        new
                        {
                            uuid,
                            coordinates,
                            revision,
                            nextState,
                            selected,
                        }
                    ),
                    "Seed a genuine original source assertion review",
                    Operator
                ),
                (c, t, _) =>
                    c.ExecuteAsync(
                        revision == 1
                            ? """
                            INSERT INTO normalization.source_actor_alias_review_state(
                                uuid,source_uuid,contributor_provider,contributor_type,contributor_key,
                                resource_uuid,input_revision_uuid,field_json_pointer,producer_version,
                                assertion_uuid,actor_reference_uuid,field_sha256,assertion_sha256,
                                original_assertion,original_scope,source_resolution_owner_uuid,
                                review_revision,state,selected_person_uuid,last_operation_uuid)
                            VALUES(@uuid,@SourceUuid,@Provider,@ResourceType,@ProviderKey,
                                @ResourceUuid,@InputRevisionUuid,@FieldJsonPointer,@ProducerVersion,
                                @AssertionUuid,@ActorReferenceUuid,@FieldSha256,@AssertionSha256,
                                @assertion::jsonb,@scope::jsonb,@ownerUuid,@revision,@nextState,@selected,@lastOperation)
                            """
                            : """
                            UPDATE normalization.source_actor_alias_review_state SET
                                review_revision=@revision,state=@nextState,selected_person_uuid=@selected,
                                last_operation_uuid=@lastOperation WHERE uuid=@uuid
                            """,
                        new
                        {
                            uuid,
                            coordinates.SourceUuid,
                            coordinates.Contributor.Provider,
                            coordinates.Contributor.ResourceType,
                            coordinates.Contributor.ProviderKey,
                            coordinates.ResourceUuid,
                            coordinates.InputRevisionUuid,
                            coordinates.FieldJsonPointer,
                            coordinates.ProducerVersion,
                            coordinates.AssertionUuid,
                            coordinates.ActorReferenceUuid,
                            coordinates.FieldSha256,
                            claim.AssertionSha256,
                            assertion = JsonSerializer.Serialize(assertion),
                            scope = JsonSerializer.Serialize(originalScope),
                            ownerUuid,
                            revision,
                            nextState,
                            selected,
                            lastOperation,
                        },
                        t
                    )
            );
        }
        return new(
            uuid,
            coordinates,
            claim.AssertionSha256,
            assertion,
            originalScope,
            ownerUuid,
            revision,
            state,
            selected,
            lastOperation
        );
    }

    private async Task<Guid> Person(string name, string kind = "person")
    {
        var input = await fixture.Capture(name, "people");
        var uuid = Guid.NewGuid();
        await fixture.PublishPeople(people, input, new([new(uuid, name, kind)], [], [], []));
        return uuid;
    }

    private async Task<CatalogSource> Source(string? description = null)
    {
        var source = await fixture.Source();
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "UPDATE sources SET taper=NULL,transferrer=NULL,lineage=NULL,source=NULL,description=@description,taper_notes=NULL WHERE id=@SourceId",
                new { description, source.SourceId }
            )
        );
        return source;
    }

    private static string Personnel(string name) => $"Personnel:\n{name} aka Finch - vocals";

    private async Task<NormalizationRecordingProposalBatch> Extract(CatalogSource source)
    {
        var result = await extractor.ProcessAsync(Guid.NewGuid(), source.SourceUuid);
        result.Disposition.Should().Be(NormalizationRecordingProcessingDisposition.Published);
        return (await extractions.GetAttemptBatchAsync(result.AttemptUuid))!;
    }

    // These exceptional extraction cases still pass through the production retention,
    // binding, checkpoint and Work contracts. No test invents a published extraction ID.
    private async Task<NormalizationRecordingProposalBatch> PreparedLegacyExtraction(
        CatalogSource source,
        bool aliasPayloadPresent = true
    )
    {
        var snapshot = (
            await new NormalizationLegacyInputService(fixture.Db, evidence).RefreshRecordingAsync(
                source.SourceUuid
            )
        )!;
        var decoded = new NormalizationProviderDecoder().Decode(snapshot.Capture.Revision);
        var binding = await new NormalizationCatalogBindingService(
            fixture.Db
        ).BindRefreshedRecordingAsync(snapshot, decoded.Recordings.Single());
        var contributor = new NormalizationResourceKey(
            "relisten",
            "recording",
            source.SourceUuid.ToString("D")
        );
        var preparation = new NormalizationRecordingPreparation(
            snapshot.Capture.Fetch.FetchUuid,
            snapshot.CatalogInputs,
            binding,
            null,
            0,
            contributor,
            ExpectedContributions: []
        );
        var attempt = await extractions.GetOrCreateAttemptAsync(
            Guid.NewGuid(),
            source.SourceUuid,
            NormalizationRecordingProcessor.Version
        );
        await extractions.SavePreparationAsync(attempt.AttemptUuid, preparation);
        var ticket = await fixture.Work.BeginContributedBatchAsync(
            attempt.AttemptUuid,
            new("extractions", "recording", source.SourceUuid),
            NormalizationRecordingProcessor.Version,
            [
                new(
                    new(
                        contributor,
                        0,
                        binding.Inputs.Select(input => input.Resource).ToArray(),
                        []
                    ),
                    binding.Inputs,
                    binding.CatalogInputs,
                    binding.SourceIdentities,
                    binding.MappingRegistry,
                    SourceUuids: binding.SourceUuids
                ),
            ],
            expectedContributionRevisions: []
        );
        var extraction = new NormalizationRecordingExtractor(new(), new()).Extract(
            snapshot.Capture.Revision,
            decoded,
            binding
        );
        if (!aliasPayloadPresent)
        {
            extraction = extraction with { SourceActorAliases = null };
        }
        return new(
            attempt.AttemptUuid,
            source.SourceUuid,
            binding.Recording.Value!.ArtistUuid,
            NormalizationRecordingProcessor.Version,
            preparation,
            ticket,
            extraction
        );
    }

    private async Task PublishExtraction(NormalizationRecordingProposalBatch batch)
    {
        await extractions.SaveExtractionAsync(batch);
        var published = await fixture.Work.TryPublishAsync(
            batch.AttemptUuid,
            (c, t, ct) => extractions.WritePublishedAsync(c, t, batch, ct)
        );
        published.Status.Should().Be(NormalizationPublicationStatus.Published);
    }

    private async Task<(CatalogSource Source, NormalizationInputRevision Input)> ArchiveSource(
        IReadOnlyList<object>? files = null,
        IReadOnlyList<string>? boundFileNames = null
    )
    {
        if (files == null)
        {
            await Person("Unbound Morgan");
        }
        var source = await Source(files == null ? Personnel("Legacy Robin") : null);
        var key = "source-resolution-archive-" + Guid.NewGuid().ToString("N");
        var provider = await Provider("archive.org", "https://archive.org");
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                """
                UPDATE sources SET upstream_identifier=@key WHERE id=@SourceId;
                UPDATE source_tracks SET mp3_url=@media WHERE uuid=@TrackUuid;
                INSERT INTO links(source_id,upstream_source_id,for_source,for_reviews,for_ratings,url,label)
                    VALUES(@SourceId,@provider,true,false,false,@url,'Archive');
                """,
                new
                {
                    source.SourceId,
                    source.TrackUuid,
                    provider,
                    key,
                    media = $"https://archive.org/download/{key}/file.mp3",
                    url = $"https://archive.org/details/{key}",
                }
            )
        );
        if (boundFileNames is { Count: > 1 })
        {
            await fixture.Db.WithWriteConnection(c =>
                c.ExecuteAsync(
                    """
                    INSERT INTO source_tracks(source_id,source_set_id,artist_id,track_position,title,slug,mp3_url,uuid,updated_at)
                    SELECT original.source_id,original.source_set_id,original.artist_id,extra.ord+1,'Song',extra.uuid::text,
                        'https://archive.org/download/'||@key||'/'||extra.name,extra.uuid,now()
                    FROM source_tracks original CROSS JOIN unnest(@tracks::uuid[],@names::text[]) WITH ORDINALITY extra(uuid,name,ord)
                    WHERE original.uuid=@TrackUuid
                    """,
                    new
                    {
                        key,
                        source.TrackUuid,
                        tracks = boundFileNames.Skip(1).Select(_ => Guid.NewGuid()).ToArray(),
                        names = boundFileNames.Skip(1).ToArray(),
                    }
                )
            );
        }
        var input = await Capture(
            new("archive.org", "item_metadata", key),
            new
            {
                metadata = new
                {
                    identifier = key,
                    description = files == null ? Personnel("Recording Avery") : null,
                },
                files = files
                    ?? new object[]
                    {
                        new
                        {
                            name = "file.mp3",
                            title = "Song",
                            format = "VBR MP3",
                        },
                        new
                        {
                            name = "missing.flac",
                            title = "Unbound Song",
                            format = "Flac",
                            description = Personnel("Unbound Morgan"),
                        },
                    },
            }
        );
        await DispatchRetained(new(input.Uuid, input.Resource));
        return (source, input);
    }

    private async Task<(
        NormalizationInputRevision Root,
        NormalizationInputRevision Directory
    )> LocalSource()
    {
        var source = await Source();
        var key = "source-resolution-local-" + Guid.NewGuid().ToString("N");
        var provider = await Provider("local", "https://audio.relisten.net");
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                """
                INSERT INTO artists_upstream_sources(artist_id,upstream_source_id,upstream_identifier)
                    VALUES(@ArtistId,@provider,@key);
                UPDATE source_tracks SET mp3_url=@media WHERE uuid=@TrackUuid;
                """,
                new
                {
                    source.ArtistId,
                    source.TrackUuid,
                    provider,
                    key,
                    media = $"https://audio.relisten.net/{key}/disc/one.mp3",
                }
            )
        );
        var root = await Capture(
            new("local", "metadata_root", key),
            new
            {
                data = new Dictionary<string, object>
                {
                    ["1970-xx-xx"] = new[]
                    {
                        new
                        {
                            dir = "disc",
                            date = "1970-xx-xx",
                            taper_notes = Personnel("Selected Robin"),
                        },
                        new
                        {
                            dir = "other",
                            date = "1991-01-01",
                            taper_notes = Personnel("Other Morgan"),
                        },
                    },
                },
            }
        );
        var directory = await Capture(
            NormalizationProviderResources.LocalDirectory(key, "disc"),
            new
            {
                txts = new[] { Personnel("Directory Avery") },
                tracks = new[]
                {
                    new
                    {
                        mp3 = "one.mp3",
                        tags = new { title = "Local Song", track = new { no = 1 } },
                    },
                },
            }
        );
        return (root, directory);
    }

    private async Task<NormalizationRecordingProposalBatch> DispatchRetained(
        NormalizationBindingInputReference input,
        NormalizationBindingInputReference? root = null
    )
    {
        var lookups = new NormalizationRecordingLookupStore(fixture.Db);
        var lookupService = new NormalizationRecordingLookupService(
            lookups,
            new(fixture.Db, evidence),
            evidence,
            new(fixture.Db),
            new(),
            fixture.Work
        );
        var lookup =
            root == null
                ? await lookupService.LookupRetainedAsync(Guid.NewGuid(), input)
                : await lookupService.LookupLocalAsync(Guid.NewGuid(), root, input);
        var dispatch = await new NormalizationRecordingLookupDispatcher(
            lookups,
            extractor
        ).DispatchAsync(lookup.Result!.HandoffUuid!.Value);
        dispatch
            .ProcessingResult.Disposition.Should()
            .Be(NormalizationRecordingProcessingDisposition.Published);
        return (await extractions.GetAttemptBatchAsync(dispatch.ProcessingResult.AttemptUuid))!;
    }

    private async Task<NormalizationInputRevision> Capture(
        NormalizationResourceKey resource,
        object value
    )
    {
        var current = await evidence.GetCurrentInputAsync(resource);
        var captureVersion =
            current?.Revision.CaptureVersion
            ?? resource.Provider switch
            {
                "archive.org" => "archive-item-response-v1",
                "local" => "local-http-json-v1",
                _ => "source-resolution-fixture-json-v1",
            };
        return (
            await evidence.CaptureInputAsync(
                new(
                    Guid.NewGuid(),
                    resource,
                    JsonSerializer.SerializeToUtf8Bytes(value),
                    "application/json",
                    captureVersion,
                    current?.Generation ?? 0,
                    DateTimeOffset.UtcNow
                )
            )
        ).Revision;
    }

    private Task<int> Provider(string name, string url) =>
        fixture.Db.WithWriteConnection(async c =>
            await c.QuerySingleOrDefaultAsync<int?>(
                "SELECT id FROM upstream_sources WHERE name=@name ORDER BY id LIMIT 1",
                new { name }
            )
            ?? await c.QuerySingleAsync<int>(
                "INSERT INTO upstream_sources(id,name,url) SELECT COALESCE(max(id),0)+1,@name,@url FROM upstream_sources RETURNING id",
                new { name, url }
            )
        );

    private Task<long> PeopleEpoch() =>
        fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<long>(
                "SELECT generation FROM normalization.candidate_universe_revisions WHERE domain='people' AND scope_uuid=@scope",
                new { scope = Guid.Empty }
            )
        );

    private Task<string> AcceptedState() =>
        fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<string>(
                """
                SELECT jsonb_build_object(
                    'people',(SELECT jsonb_agg(to_jsonb(value) ORDER BY uuid) FROM normalization.people value),
                    'aliases',(SELECT jsonb_agg(to_jsonb(value) ORDER BY uuid) FROM normalization.person_aliases value),
                    'external_identities',(SELECT count(*) FROM normalization.person_external_identities),
                    'redirects',(SELECT count(*) FROM normalization.person_redirects),
                    'credit_moves',(SELECT count(*) FROM normalization.person_credit_moves),
                    'credits',(SELECT jsonb_agg(to_jsonb(value) ORDER BY uuid) FROM normalization.recording_credits value),
                    'credit_evidence',(SELECT count(*) FROM normalization.recording_credit_evidence),
                    'managed_credit_support',(SELECT count(*) FROM normalization.recording_credit_contributor_support),
                    'unmanaged_credit_support',(SELECT count(*) FROM normalization.recording_credit_unmanaged_support),
                    'assignments',(SELECT jsonb_agg(to_jsonb(value) ORDER BY uuid) FROM normalization.source_actor_alias_assignments value),
                    'managed_alias_support',(SELECT jsonb_agg(to_jsonb(value) ORDER BY uuid) FROM normalization.person_alias_contributor_support value),
                    'unmanaged_alias_support',(SELECT jsonb_agg(to_jsonb(value) ORDER BY uuid) FROM normalization.person_alias_unmanaged_support value),
                    'source_reviews',(SELECT jsonb_agg(to_jsonb(value) ORDER BY uuid) FROM normalization.source_actor_alias_review_state value),
                    'source_review_history',(SELECT count(*) FROM normalization.source_actor_alias_review_state_history),
                    'alias_history',(SELECT count(*) FROM normalization.person_aliases_history),
                    'identity_history',(SELECT count(*) FROM normalization.people_metadata_history),
                    'actor_context',(SELECT count(*) FROM normalization.recording_actor_contexts),
                    'venues',(SELECT count(*) FROM normalization.canonical_venues),
                    'repertoire',(SELECT count(*) FROM normalization.repertoire_songs),
                    'compositions',(SELECT count(*) FROM normalization.compositions),
                    'occurrences',(SELECT count(*) FROM normalization.song_occurrences),
                    'contents',(SELECT count(*) FROM normalization.track_contents),
                    'playback',(SELECT jsonb_agg(jsonb_build_array(s.uuid,tr.uuid,ss.uuid,tr.mp3_url) ORDER BY tr.uuid)
                        FROM source_tracks tr JOIN sources s ON s.id=tr.source_id
                        JOIN source_sets ss ON ss.id=tr.source_set_id))::text
                """
            )
        );

    private async Task SelectPolicy(RegisteredPeopleAcceptancePolicy? policy)
    {
        if (policy != null)
        {
            await policies.RegisterEvaluatedPolicyAsync(
                policy.Artifact,
                policy.Evaluation,
                Operator
            );
        }
        var state = await policies.ReadStateAsync();
        await policies.ApplyDecisionAsync(
            new(
                Guid.NewGuid(),
                state.OwnerObservation.SubjectUuid,
                state.OwnerObservation.EditorialRevision,
                state.Active?.Policy.Artifact.Uuid,
                policy?.Artifact.Uuid,
                "Select the fixture matching policy"
            ),
            Operator
        );
    }

    private static RegisteredPeopleAcceptancePolicy Policy(bool sourceQualified = true)
    {
        var applicability = new SourceActorAliasPolicyApplicability(
            PeopleResolutionPolicyEvaluator.SourceInputForm,
            NormalizationRecordingProcessor.Version,
            NormalizationRecordingExtractor.Version,
            SourceActorAliasExtractor.Version,
            PeopleActorProjection.SourceAliasVersion,
            NormalizationCandidateService.PeopleFactsVersion,
            PeopleResolutionPolicyEvaluator.RetrievalVersion,
            PeopleResolutionPolicyEvaluator.ScorerVersion
        );
        var artifact = new PeopleAcceptancePolicyArtifact(
            Guid.NewGuid(),
            "source-resolution-fixture-v1",
            "",
            PeopleResolutionPolicyEvaluator.ScorerVersion,
            PeopleResolutionPolicyEvaluator.RetrievalVersion,
            NormalizationCandidateService.PeopleFactsVersion,
            NormalizationRecordingExtractor.Version,
            NormalizedPeopleService.CreditContextVersion,
            sourceQualified
                ? [PeopleResolutionPolicyEvaluator.SourceInputForm]
                : ["retained_credit"],
            new(0, 1, 1, 1, 2, 0.5),
            FixtureOnly: true,
            SourceActorAliasApplicability: sourceQualified ? applicability : null
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
            "fixture-credit-evaluator-v1",
            "fixture-credit-reviewer",
            Operator,
            new(100, 0),
            FixtureOnly: true,
            SourceActorAliasEvaluation: sourceQualified
                ? new(
                    PeopleResolutionPolicyService.ComputeSourceActorApplicabilityHash(
                        applicability
                    ),
                    new string('a', 64),
                    new string('b', 64),
                    new string('c', 64),
                    "fixture-source-evaluator-v1",
                    "fixture-source-reviewer",
                    new(100, 0)
                )
                : null
        );
        evaluation = evaluation with
        {
            ReceiptHash = PeopleResolutionPolicyService.ComputeEvaluationHash(evaluation),
        };
        return new(artifact, evaluation);
    }

    private sealed record ProducerSizes(int FullBytes, int ConsumedBytes);
}
