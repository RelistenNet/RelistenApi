using Dapper;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
using DomainInput = RelistenApiTests.Services.NormalizationDomainFixture.DomainInput;

namespace RelistenApiTests.Services;

[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestImportedVenueResolutionContext
{
    private NormalizationDomainFixture fixture = null!;
    private NormalizedVenueService venues = null!;
    private NormalizationCandidateService candidates = null!;

    [OneTimeSetUp]
    public void Start()
    {
        fixture = new();
        venues = new(fixture.Db, fixture.Work);
        candidates = new(fixture.Db);
    }

    [OneTimeTearDown]
    public void Stop() => fixture?.Dispose();

    [Test]
    public async Task IncludesAcceptedAssignmentsAndEveryEquivalentExclusionOutsideNameRecall()
    {
        var setup = await Prepare();
        var first = await Decide(setup, setup.First.Uuid, "reject");
        var equivalent = await Decide(setup, setup.First.Uuid, "reject");
        var accepted = await Decide(setup, setup.Second.Uuid, "accept");
        var expectedOwner = (await fixture.Work.GetSubjectAsync(setup.Owner.Ticket.SubjectUuid))!;
        var context = await Read(setup);

        context.Complete.Should().BeTrue();
        context.Candidates.Selection.Items.Should().BeEmpty();
        context.Candidates.Facts.Should().BeEmpty();
        context
            .Assignments.Select(row => row.AssignmentUuid)
            .Should()
            .BeEquivalentTo(new Guid[] { first.Uuid, equivalent.Uuid, accepted.Uuid });
        context
            .Assignments.Should()
            .OnlyContain(row => !row.IsHistorical && row.Basis == "reviewed");
        context.Assignments.Where(row => row.State == "rejected").Should().HaveCount(2);
        context
            .AssignmentTargets.Select(fact => fact.Header.Uuid)
            .Should()
            .BeEquivalentTo(new Guid[] { setup.First.Uuid, setup.Second.Uuid });
        context.Owner.Should().BeEquivalentTo(expectedOwner);
        context
            .ReadSet.OwnerObservations.Should()
            .Contain(
                new NormalizationSubjectObservation(
                    expectedOwner.SubjectUuid,
                    expectedOwner.DesiredGeneration,
                    expectedOwner.EditorialRevision,
                    expectedOwner.PublishedWorkUuid
                )
            );
        foreach (var assignment in context.Assignments)
        {
            assignment.Evidence.Should().Equal(setup.Input.Reference);
        }
        context.ReadSet.OwnerObservations.Should().HaveCount(3);
    }

    [Test]
    public async Task HistoricalRejectedRevisionKeepsItsOwnEvidenceAfterLaterAcceptance()
    {
        var setup = await Prepare();
        var rejected = await Decide(setup, setup.First.Uuid, "reject");
        var acceptedEvidence = setup.Input.Reference with
        {
            StartCodePoint = null,
            EndCodePoint = null,
            Quote = null,
        };
        var operation = Guid.NewGuid();
        await Decide(
            setup,
            setup.First.Uuid,
            "accept",
            rejected.Uuid,
            1,
            [acceptedEvidence],
            operation
        );
        var context = await Read(setup);
        var current = context.Assignments.Single(row => !row.IsHistorical);
        var historical = context.Assignments.Single(row => row.IsHistorical);
        current.State.Should().Be("accepted");
        current.Revision.Should().Be(2);
        current.Evidence.Should().Equal(acceptedEvidence);
        historical.State.Should().Be("rejected");
        historical.Revision.Should().Be(1);
        historical.Evidence.Should().Equal(setup.Input.Reference);
        historical.SupersedingDecisionUuid.Should().Be(operation);
    }

    [Test]
    public async Task RejectedTargetRedirectPreservesOriginalEndpointAndCurrentSurvivorFacts()
    {
        var setup = await Prepare();
        var rejected = await Decide(setup, setup.First.Uuid, "reject");
        var identityOwner = await fixture.OwnerUuid("venues", "venue", setup.First.Uuid);
        await venues.ApplyIdentityDecisionAsync(
            new(
                Guid.NewGuid(),
                identityOwner,
                0,
                "merge",
                setup.First.Uuid,
                1,
                setup.Second,
                1,
                [rejected.Uuid],
                [],
                "Same physical venue after independent review"
            ),
            "venue-reviewer"
        );
        var context = await Read(setup);
        var assignment = context.Assignments.Single(row =>
            row.AssignmentUuid == rejected.Uuid && row.IsHistorical
        );
        assignment.StoredVenueUuid.Should().Be(setup.First.Uuid);
        assignment.ResolvedVenueUuid.Should().Be(setup.Second.Uuid);
        var target = context.AssignmentTargets.Should().ContainSingle().Which;
        target.Header.Uuid.Should().Be(setup.Second.Uuid);
        target
            .IdentityClosure.Select(header => header.Uuid)
            .Should()
            .BeEquivalentTo(new Guid[] { setup.First.Uuid, setup.Second.Uuid });
        target
            .Redirects.Should()
            .ContainSingle(redirect =>
                redirect.OriginalUuid == setup.First.Uuid
                && redirect.SurvivorUuid == setup.Second.Uuid
            );
    }

    [Test]
    public async Task AssignmentAndEvidenceBoundsFailWholeReadInsteadOfLosingExclusions()
    {
        var setup = await Prepare();
        await Decide(setup, setup.First.Uuid, "reject");
        var wholeValue = setup.Input.Reference with
        {
            StartCodePoint = null,
            EndCodePoint = null,
            Quote = null,
        };
        await Decide(
            setup,
            setup.Second.Uuid,
            "reject",
            evidence: [setup.Input.Reference, wholeValue]
        );
        await FluentActions
            .Invoking(() => Read(setup, maximumAssignments: 1))
            .Should()
            .ThrowAsync<InvalidOperationException>();
        await FluentActions
            .Invoking(() => Read(setup, new() { MaximumEvidencePerAssertion = 1 }))
            .Should()
            .ThrowAsync<InvalidOperationException>();
        var complete = await Read(setup);
        complete.Assignments.Should().HaveCount(2);
        complete
            .Assignments.Single(row => row.StoredVenueUuid == setup.Second.Uuid)
            .Evidence.Should()
            .HaveCount(2);
    }

    [Test]
    public async Task OriginalContextStampAndVenueEpochRejectInterveningReviewAtAdmission()
    {
        var setup = await Prepare();
        await Decide(setup, setup.First.Uuid, "reject");
        var context = await Read(setup);
        await Decide(setup, setup.Second.Uuid, "accept");
        await FluentActions
            .Invoking(() => Admit(setup, context))
            .Should()
            .ThrowAsync<NormalizationConflictException>();
        var current = await Read(setup);
        var ticket = await Admit(setup, current);
        (await fixture.Work.TryPublishAsync(ticket.WorkUuid, (_, _, _) => Task.CompletedTask))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);
    }

    [Test]
    public async Task NewTargetAfterPreparationMakesPublicationStaleAndRetryUsesFreshContext()
    {
        var setup = await Prepare();
        var context = await Read(setup);
        var ticket = await Admit(setup, context);
        await Decide(setup, setup.First.Uuid, "reject");
        var invoked = false;
        var stale = await fixture.Work.TryPublishAsync(
            ticket.WorkUuid,
            (_, _, _) =>
            {
                invoked = true;
                return Task.CompletedTask;
            }
        );
        stale.Status.Should().Be(NormalizationPublicationStatus.Stale);
        invoked.Should().BeFalse();
        var fresh = await Read(setup);
        var retry = await Admit(setup, fresh);
        (await fixture.Work.TryPublishAsync(retry.WorkUuid, (_, _, _) => Task.CompletedTask))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);
    }

    [Test]
    public async Task AssignmentOwnerAndFinalReadSetShareOneSnapshotAcrossConcurrentReview()
    {
        var setup = await Prepare();
        var original = (await fixture.Work.GetSubjectAsync(setup.Owner.Ticket.SubjectUuid))!;
        await using var gateConnection = fixture.Db.CreateConnection(false, false);
        await gateConnection.OpenAsync();
        await using var gate = await gateConnection.BeginTransactionAsync();
        var gatePid = await gateConnection.ExecuteScalarAsync<int>(
            "SELECT pg_backend_pid()",
            transaction: gate
        );
        await gateConnection.ExecuteAsync(
            "LOCK normalization.canonical_venues IN ACCESS EXCLUSIVE MODE",
            transaction: gate
        );
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var pending = candidates.FindImportedVenueResolutionContextAsync(
            setup.CatalogUuid,
            new(IdentityMatchingDomain.Venues, [setup.AbsentName], []),
            cancellationToken: deadline.Token
        );
        var released = false;
        try
        {
            await WaitForAssignmentRead(gatePid, deadline.Token);
            await fixture.Work.ApplyReviewAsync(
                new(
                    Guid.NewGuid(),
                    original.SubjectUuid,
                    original.EditorialRevision,
                    "accept",
                    System.Text.Json.JsonSerializer.SerializeToElement(
                        new { note = "Confirm current assignment" }
                    ),
                    "Editorial confirmation without changing candidate facts",
                    "venue-reviewer"
                ),
                (_, _, _) => Task.CompletedTask,
                deadline.Token
            );
            await gate.RollbackAsync();
            released = true;
            var context = await pending;
            context.Owner.Should().BeEquivalentTo(original);
            context
                .ReadSet.OwnerObservations.Single(owner =>
                    owner.SubjectUuid == original.SubjectUuid
                )
                .EditorialRevision.Should()
                .Be(original.EditorialRevision);
            await FluentActions
                .Invoking(() => Admit(setup, context))
                .Should()
                .ThrowAsync<NormalizationConflictException>();
            (await Read(setup))
                .Owner!.EditorialRevision.Should()
                .Be(original.EditorialRevision + 1);
        }
        finally
        {
            try
            {
                if (!released)
                {
                    await gate.RollbackAsync();
                }
            }
            finally
            {
                deadline.Cancel();
                try
                {
                    await pending.WaitAsync(TimeSpan.FromSeconds(10));
                }
                catch (OperationCanceledException) { }
            }
        }
    }

    private async Task WaitForAssignmentRead(int gatePid, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var blocked = await fixture.Db.WithWriteConnection(c =>
                c.ExecuteScalarAsync<bool>(
                    new CommandDefinition(
                        """
                        SELECT EXISTS(SELECT 1 FROM pg_stat_activity activity WHERE activity.datname=current_database()
                            AND @gatePid=ANY(pg_blocking_pids(activity.pid)) AND activity.query LIKE '%WITH assignments AS%')
                        """,
                        new { gatePid },
                        cancellationToken: ct
                    )
                )
            );
            if (blocked)
            {
                return;
            }
            await Task.Delay(20, ct);
        }
    }

    [Test]
    public async Task MissingOwnerIsExplicitAndCancelledContextReadDoesNotRegisterOne()
    {
        var catalog = await CatalogVenue();
        var query = new NormalizationCandidateQuery(
            IdentityMatchingDomain.Venues,
            [Guid.NewGuid().ToString("N")],
            []
        );
        var context = await candidates.FindImportedVenueResolutionContextAsync(catalog, query);
        context.Owner.Should().BeNull();
        context.Complete.Should().BeFalse();
        context
            .Candidates.Issues.Should()
            .Contain(issue => issue.Kind == CandidateFactsIssueKind.MissingOwner);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await FluentActions
            .Invoking(() =>
                candidates.FindImportedVenueResolutionContextAsync(
                    catalog,
                    query,
                    cancellationToken: cancelled.Token
                )
            )
            .Should()
            .ThrowAsync<OperationCanceledException>();
        (
            await fixture.Work.GetSubjectAsync(
                new NormalizationSubjectKey("venues", "imported_venue", catalog)
            )
        )
            .Should()
            .BeNull();
    }

    private Task<NormalizationImportedVenueResolutionContext> Read(
        SetupData setup,
        CandidateFactsLimits? limits = null,
        int maximumAssignments = 1000
    ) =>
        candidates.FindImportedVenueResolutionContextAsync(
            setup.CatalogUuid,
            new(IdentityMatchingDomain.Venues, [setup.AbsentName], []),
            limits,
            maximumAssignments
        );

    private Task<NormalizationWorkTicket> Admit(
        SetupData setup,
        NormalizationImportedVenueResolutionContext context
    ) =>
        fixture.Work.BeginWorkAsync(
            Guid.NewGuid(),
            setup.Owner.Subject,
            "venue-context-test",
            [setup.Input.Resource],
            [],
            context.Owner!.DesiredGeneration,
            candidateUniverses: context.ReadSet.CandidateUniverses,
            expectedOwnerObservations: context.ReadSet.OwnerObservations,
            catalogInputs: context.ReadSet.CatalogInputs,
            expectedInputRevisions:
            [
                new(setup.Input.Reference.InputRevisionUuid, setup.Input.Resource),
            ]
        );

    private async Task<VenueAssignment> Decide(
        SetupData setup,
        Guid target,
        string action,
        Guid? assignmentUuid = null,
        long revision = 0,
        IReadOnlyList<NormalizedEvidenceReference>? evidence = null,
        Guid? operation = null
    )
    {
        var owner = (await fixture.Work.GetSubjectAsync(setup.Owner.Ticket.SubjectUuid))!;
        var assignment = new VenueAssignment(
            assignmentUuid ?? Guid.NewGuid(),
            setup.CatalogUuid,
            target,
            evidence ?? [setup.Input.Reference]
        );
        await venues.ApplyDecisionAsync(
            new(
                operation ?? Guid.NewGuid(),
                owner.SubjectUuid,
                owner.EditorialRevision,
                revision,
                action,
                false,
                assignment,
                "Reviewed venue assignment"
            ),
            "venue-reviewer"
        );
        return assignment;
    }

    private async Task<SetupData> Prepare()
    {
        var prefix = Guid.NewGuid().ToString("N");
        var first = new CanonicalVenue(Guid.NewGuid(), "First hall " + prefix);
        var second = new CanonicalVenue(Guid.NewGuid(), "Second hall " + prefix);
        var input = await fixture.Capture(first.DisplayName + "; " + second.DisplayName, "venues");
        await fixture.PublishVenues(venues, input, new([first, second], [], [], [], [], []));
        var catalog = await CatalogVenue();
        var owner = await fixture.Own(input, "imported_venue", catalog);
        return new(input, owner, catalog, first, second, Guid.NewGuid().ToString("N"));
    }

    private Task<Guid> CatalogVenue() =>
        fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<Guid>(
                "INSERT INTO venues(uuid,name,location,slug,upstream_identifier,created_at,updated_at) VALUES(@uuid,@name,'Unknown Location',@name,@name,now(),now()) RETURNING uuid",
                new
                {
                    uuid = Guid.NewGuid(),
                    name = "resolution-fixture-" + Guid.NewGuid().ToString("N"),
                }
            )
        );

    private sealed record SetupData(
        DomainInput Input,
        DomainInput Owner,
        Guid CatalogUuid,
        CanonicalVenue First,
        CanonicalVenue Second,
        string AbsentName
    );
}
