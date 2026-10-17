using Dapper;
using FluentAssertions;
using Npgsql;
using Relisten.Api.Models.Normalization;
using Relisten.Data;

namespace RelistenApiTests.Services;

/// <summary>Public identity and credit writers invalidate prior candidate decisions transactionally.</summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestNormalizationPeopleVenueCandidateUniverses
{
    private NormalizationDomainFixture fixture = null!;
    private NormalizedPeopleService people = null!;
    private NormalizedVenueService venues = null!;

    [OneTimeSetUp]
    public void Start()
    {
        fixture = new();
        people = new(fixture.Db, fixture.Work);
        venues = new(fixture.Db, fixture.Work);
    }

    [OneTimeTearDown]
    public void Stop() => fixture?.Dispose();

    [TestCase("people", false)]
    [TestCase("people", true)]
    [TestCase("venues", false)]
    [TestCase("venues", true)]
    public async Task NewIdentityInvalidatesAnEmptyCandidateDecision(
        string domain,
        bool beforeAdmission
    )
    {
        var name = Guid.NewGuid().ToString("N");
        var query = new NormalizationCandidateQuery(
            domain == "people" ? IdentityMatchingDomain.People : IdentityMatchingDomain.Venues,
            [name],
            []
        );
        var candidates = new NormalizationCandidateService(fixture.Db);
        (await candidates.FindAsync(query)).Items.Should().BeEmpty();
        var observed = await Observe(Scope(domain));
        var ticket = beforeAdmission ? null : await Admit(observed);
        var identity = await CreateIdentity(domain, name);
        (await candidates.FindAsync(query))
            .Items.Should()
            .Contain(hit => hit.Uuid == identity.Uuid);

        if (beforeAdmission)
        {
            var admit = () => Admit(observed);
            await admit.Should().ThrowAsync<NormalizationConflictException>();
        }
        else
        {
            (await fixture.Work.TryPublishAsync(ticket!.WorkUuid, NeverWrite))
                .Status.Should()
                .Be(NormalizationPublicationStatus.Stale);
        }
    }

    [TestCase("people")]
    [TestCase("venues")]
    public async Task ReviewedAliasInvalidatesOldFactsAndExactReplayDoesNotAdvanceAgain(
        string domain
    )
    {
        var identity = await CreateIdentity(domain);
        var before = await Observe(Scope(domain));
        var compositionBefore = await Observe(NormalizationCandidateUniverseKey.Compositions);
        var ticket = await Admit(before);
        var owner = (await fixture.Work.GetSubjectAsync(identity.Input.Ticket.SubjectUuid))!;
        var operation = Guid.NewGuid();
        Func<Task<NormalizationReviewResult>> review;
        if (domain == "people")
        {
            var command = new PersonMetadataDecision(
                operation,
                owner.SubjectUuid,
                owner.EditorialRevision,
                identity.Uuid,
                1,
                "accept",
                "Retain an explicit alias",
                Alias: new(Guid.NewGuid(), identity.Uuid, "Alias name", identity.Input.Reference)
            );
            review = () => people.ApplyMetadataDecisionAsync(command, "fixture-operator");
        }
        else
        {
            var command = new VenueMetadataDecision(
                operation,
                owner.SubjectUuid,
                owner.EditorialRevision,
                identity.Uuid,
                1,
                "accept",
                "Retain an explicit historical name",
                Name: new(
                    Guid.NewGuid(),
                    identity.Uuid,
                    "Alias name",
                    null,
                    null,
                    null,
                    identity.Input.Reference
                )
            );
            review = () => venues.ApplyMetadataDecisionAsync(command, "fixture-operator");
        }

        (await review()).Replayed.Should().BeFalse();
        var after = await Observe(Scope(domain));
        after.Generation.Should().BeGreaterThan(before.Generation);
        (await review()).Replayed.Should().BeTrue();
        (await Observe(Scope(domain))).Should().Be(after);
        (await fixture.Work.TryPublishAsync(ticket.WorkUuid, NeverWrite))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Stale);
        if (domain == "people")
        {
            (await Observe(NormalizationCandidateUniverseKey.Compositions))
                .Generation.Should()
                .BeGreaterThan(compositionBefore.Generation);
        }
    }

    [Test]
    public async Task NewCreditChangesPersonContextWithoutChangingPersonIdentityOrCompositionFacts()
    {
        var person = await CreateIdentity("people");
        var source = await fixture.Source();
        var before = await Observe(NormalizationCandidateUniverseKey.People);
        var compositionBefore = await Observe(NormalizationCandidateUniverseKey.Compositions);
        var revision = await PersonRevision(person.Uuid);
        var ticket = await Admit(before);

        await Credit(person, source.SourceUuid);

        (await Observe(NormalizationCandidateUniverseKey.People))
            .Generation.Should()
            .BeGreaterThan(before.Generation);
        (await Observe(NormalizationCandidateUniverseKey.Compositions))
            .Should()
            .Be(compositionBefore);
        (await PersonRevision(person.Uuid)).Should().Be(revision);
        (await fixture.Work.TryPublishAsync(ticket.WorkUuid, NeverWrite))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Stale);
    }

    [Test]
    public async Task CatalogArtistMoveInvalidatesPersonContextEvenWithUnchangedCreditAndIdentity()
    {
        var person = await CreateIdentity("people");
        var original = await fixture.Source();
        var otherArtist = await fixture.Source();
        await Credit(person, original.SourceUuid);
        var revision = await PersonRevision(person.Uuid);
        var before = await Observe(NormalizationCandidateUniverseKey.People);
        var ticket = await Admit(before);

        // The old track artist deliberately remains unchanged. Artist context belongs to the
        // accepted recording credit, so an invalid playback join must not hide this change.
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "UPDATE sources SET artist_id=@ArtistId WHERE uuid=@SourceUuid",
                new { otherArtist.ArtistId, original.SourceUuid }
            )
        );

        (await Observe(NormalizationCandidateUniverseKey.People))
            .Generation.Should()
            .BeGreaterThan(before.Generation);
        (await PersonRevision(person.Uuid)).Should().Be(revision);
        (await fixture.Work.TryPublishAsync(ticket.WorkUuid, NeverWrite))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Stale);
    }

    [Test]
    public async Task ReviewedCreditReservesCompositionBeforePeopleBecauseItAlsoUpdatesPersonRevision()
    {
        var person = await CreateIdentity("people");
        var source = await fixture.Source();
        var credit = await Credit(person, source.SourceUuid);
        var owner = (
            await fixture.Work.GetSubjectAsync(
                new NormalizationSubjectKey("people", "recording", source.SourceUuid)
            )
        )!;
        var compositionsBefore = await Observe(NormalizationCandidateUniverseKey.Compositions);
        await using var barrier = new NpgsqlConnection(LocalTestDatabase.ConnectionString);
        await barrier.OpenAsync();
        await using var transaction = await barrier.BeginTransactionAsync();
        await barrier.ExecuteAsync(
            "SELECT generation FROM normalization.candidate_universe_revisions WHERE domain='compositions' FOR SHARE",
            transaction: transaction
        );
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var review = people.ApplyDecisionAsync(
            new(
                Guid.NewGuid(),
                owner.SubjectUuid,
                owner.EditorialRevision,
                1,
                "retract",
                credit,
                "Correct the recording credit"
            ),
            "fixture-operator",
            timeout.Token
        );
        try
        {
            await WaitForBlockedWriter(barrier.ProcessID);
            // The reviewer must still be before the people scope and domain rows. Taking
            // compositions for the first time inside StoreCreditRevision would fail this probe.
            await barrier.ExecuteAsync(
                "SELECT generation FROM normalization.candidate_universe_revisions WHERE domain='people' FOR UPDATE NOWAIT",
                transaction: transaction
            );
        }
        catch
        {
            timeout.Cancel();
            throw;
        }
        finally
        {
            await transaction.RollbackAsync();
            if (timeout.IsCancellationRequested)
            {
                try
                {
                    await review.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch
                {
                    // The original probe failure remains the useful assertion. Drain its
                    // cancelled writer so it cannot outlive this fixture's database state.
                }
            }
        }
        (await review).Replayed.Should().BeFalse();
        (await Observe(NormalizationCandidateUniverseKey.Compositions))
            .Generation.Should()
            .BeGreaterThan(compositionsBefore.Generation);
    }

    private async Task<CreatedIdentity> CreateIdentity(string domain, string name = "Original name")
    {
        var uuid = Guid.NewGuid();
        var input = await fixture.Capture(name + "; Alias name", domain);
        input = await fixture.Own(input, domain == "people" ? "person" : "venue", uuid);
        NormalizationPublicationResult result;
        if (domain == "people")
        {
            result = await people.PublishAsync(
                input.Ticket.WorkUuid,
                new([new(uuid, name)], [new(Guid.NewGuid(), uuid, name, input.Reference)], [], [])
            );
        }
        else
        {
            result = await venues.PublishAsync(
                input.Ticket.WorkUuid,
                new(
                    [new(uuid, name)],
                    [new(Guid.NewGuid(), uuid, name, null, null, null, input.Reference)],
                    [],
                    [],
                    [],
                    []
                )
            );
        }
        result.Status.Should().Be(NormalizationPublicationStatus.Published);
        return new(uuid, input);
    }

    private async Task<RecordingCredit> Credit(CreatedIdentity person, Guid source)
    {
        var recording = await fixture.Own(
            person.Input,
            "recording",
            source,
            [person.Input.Ticket.SubjectUuid]
        );
        var credit = new RecordingCredit(
            Guid.NewGuid(),
            source,
            "taper",
            "Original name",
            person.Uuid,
            null,
            [person.Input.Reference]
        );
        var result = await people.PublishAsync(
            recording.Ticket.WorkUuid,
            new([], [], [], [credit])
        );
        result.Status.Should().Be(NormalizationPublicationStatus.Published);
        return credit;
    }

    private async Task WaitForBlockedWriter(int blockerPid)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < until)
        {
            if (
                await fixture.Db.WithWriteConnection(c =>
                    c.ExecuteScalarAsync<bool>(
                        "SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE @blockerPid=ANY(pg_blocking_pids(pid)))",
                        new { blockerPid }
                    )
                )
            )
            {
                return;
            }
            await Task.Delay(20);
        }
        throw new AssertionException("The credit reviewer did not reach the held candidate scope.");
    }

    private Task<long> PersonRevision(Guid uuid) =>
        fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<long>(
                "SELECT revision FROM normalization.people WHERE uuid=@uuid",
                new { uuid }
            )
        );

    private async Task<NormalizationCandidateUniverseDependency> Observe(
        NormalizationCandidateUniverseKey universe
    ) =>
        new(
            universe,
            await fixture.Db.WithWriteConnection(c =>
                c.ExecuteScalarAsync<long?>(
                    "SELECT generation FROM normalization.candidate_universe_revisions WHERE domain=@Domain AND scope_uuid=@ScopeUuid",
                    universe
                )
            ) ?? 0
        );

    private Task<NormalizationWorkTicket> Admit(
        NormalizationCandidateUniverseDependency observed
    ) =>
        fixture.Work.BeginWorkAsync(
            Guid.NewGuid(),
            new("people", "person", Guid.NewGuid()),
            "candidate-evaluation",
            [],
            [],
            candidateUniverses: [observed]
        );

    private static NormalizationCandidateUniverseKey Scope(string domain) =>
        new(domain, Guid.Empty);

    private static Task NeverWrite(
        NpgsqlConnection _,
        NpgsqlTransaction __,
        CancellationToken ___
    ) =>
        throw new InvalidOperationException(
            "A stale candidate decision must not invoke its writer."
        );

    private sealed record CreatedIdentity(Guid Uuid, NormalizationDomainFixture.DomainInput Input);
}
