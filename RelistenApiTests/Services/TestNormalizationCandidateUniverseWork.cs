using Dapper;
using FluentAssertions;
using Npgsql;
using Relisten.Api.Models.Normalization;
using Relisten.Data;

namespace RelistenApiTests.Services;

/// <summary>
/// Pool generations fence new alternatives and relationship context that candidate-owner
/// revisions alone cannot see. These tests exercise work admission and transactional publication.
/// </summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestNormalizationCandidateUniverseWork
{
    private NormalizationDomainFixture fixture = null!;
    private static NormalizationCandidateUniverseKey People =>
        NormalizationCandidateUniverseKey.People;

    [OneTimeSetUp]
    public void Start() => fixture = new();

    [OneTimeTearDown]
    public void Stop() => fixture?.Dispose();

    [Test]
    public async Task NewRivalInvalidatesEmptyPoolPublicationAndPublishedOwners()
    {
        var name = "Universe" + Guid.NewGuid().ToString("N");
        var query = new NormalizationCandidateQuery(IdentityMatchingDomain.People, [name], []);
        var candidates = new NormalizationCandidateService(fixture.Db);
        (await candidates.FindAsync(query)).Items.Should().BeEmpty();
        var snapshot = await Snapshot(People);
        var published = await Begin(Subject(), [snapshot]);
        await Publish(published);
        var running = await Begin(Subject(), [snapshot]);
        (await Pending(published.SubjectUuid)).Should().BeFalse();

        await CreatePerson(name);

        (await candidates.FindAsync(query)).Items.Should().ContainSingle();
        (await Pending(published.SubjectUuid)).Should().BeTrue();
        (await fixture.Work.TryPublishAsync(running.WorkUuid, NeverWrite))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Stale);
    }

    [Test]
    public async Task ChangedPoolBeforeAdmissionRejectsWithoutInstallingContribution()
    {
        var snapshot = await Snapshot(People);
        await CreatePerson("Arrival before candidate admission");
        var subject = Subject();
        var contributor = Contributor();
        var workUuid = Guid.NewGuid();
        var stale = () => Contribute(subject, contributor, 0, [snapshot], workUuid);
        await stale.Should().ThrowAsync<NormalizationConflictException>();
        (await fixture.Work.GetInputContributionAsync(subject, contributor)).Should().BeNull();
        (await fixture.Work.GetSubjectAsync(subject)).Should().BeNull();

        var retried = await Contribute(subject, contributor, 0, [await Snapshot(People)], workUuid);
        await Publish(retried);
    }

    [Test]
    public async Task MissingRepertoirePoolIsZeroAndFailedExpectationRollsBackInitialization()
    {
        var universe = NormalizationCandidateUniverseKey.ForRepertoire(Guid.NewGuid());
        var subject = Subject();
        var workUuid = Guid.NewGuid();
        var contributor = Contributor();
        var wrong = () => Contribute(subject, contributor, 0, [new(universe, 1)], workUuid);
        await wrong.Should().ThrowAsync<NormalizationConflictException>();
        (await Exists(universe)).Should().BeFalse();
        (await fixture.Work.GetInputContributionAsync(subject, contributor)).Should().BeNull();
        var admitted = await Contribute(subject, contributor, 0, [new(universe, 0)], workUuid);
        admitted
            .CandidateUniverses.Should()
            .Equal(new NormalizationCandidateUniverseDependency(universe, 0));
        await Publish(admitted);

        await Bump(universe);

        (await Pending(admitted.SubjectUuid)).Should().BeTrue();
    }

    [Test]
    public async Task RepertoireChangesDoNotInvalidateAnotherArtistsPool()
    {
        var first = NormalizationCandidateUniverseKey.ForRepertoire(Guid.NewGuid());
        var second = NormalizationCandidateUniverseKey.ForRepertoire(Guid.NewGuid());
        var firstTicket = await Begin(Subject(), [await Snapshot(first)]);
        var secondTicket = await Begin(Subject(), [await Snapshot(second)]);
        await Publish(firstTicket);
        await Publish(secondTicket);

        await Bump(first);

        (await Pending(firstTicket.SubjectUuid)).Should().BeTrue();
        (await Pending(secondTicket.SubjectUuid)).Should().BeFalse();
    }

    [Test]
    public async Task PoolKeysNeedConfigurationCasButNewGenerationsDoNot()
    {
        var subject = Subject();
        var empty = await Begin(subject, []);
        var snapshot = await Snapshot(People);
        var missingCas = () => Begin(subject, [snapshot]);
        await missingCas.Should().ThrowAsync<NormalizationConflictException>();
        var added = await Begin(subject, [snapshot], empty.DesiredGeneration);
        await CreatePerson("New pool generation");
        var refreshed = await Begin(subject, [await Snapshot(People)]);
        refreshed.DesiredGeneration.Should().Be(added.DesiredGeneration);
        var removeWithoutCas = () => Begin(subject, []);
        await removeWithoutCas.Should().ThrowAsync<NormalizationConflictException>();
        var removed = await Begin(subject, [], refreshed.DesiredGeneration);
        removed.CandidateUniverses.Should().BeNull();
        await Publish(removed);
        await CreatePerson("Removed pool dependency");
        (await Pending(removed.SubjectUuid)).Should().BeFalse();
    }

    [Test]
    public async Task ContributorUnionRetainsSharedPoolsAndEmptyReplacementsAreTombstones()
    {
        var subject = Subject();
        var first = Contributor();
        var second = Contributor();
        await Contribute(subject, first, 0, [await Snapshot(People)]);
        await Contribute(subject, second, 0, [await Snapshot(People)]);
        var incomplete = () => Begin(subject, []);
        await incomplete.Should().ThrowAsync<NormalizationConflictException>();
        var retained = await Contribute(subject, first, 1, []);
        retained.CandidateUniverses!.Select(input => input.Universe).Should().Equal(People);
        var firstState = (await fixture.Work.GetInputContributionAsync(subject, first))!;
        firstState.Revision.Should().Be(2);
        firstState.CandidateUniverses.Should().BeNullOrEmpty();
        (await fixture.Work.GetInputContributionAsync(subject, second))!
            .CandidateUniverses.Should()
            .Equal(People);
        await Publish(retained);
        await CreatePerson("Shared candidate dependency");
        (await Pending(retained.SubjectUuid)).Should().BeTrue();
        var removed = await Contribute(subject, second, 1, []);
        removed.CandidateUniverses.Should().BeNull();
        await Publish(removed);
        await CreatePerson("Candidate dependency withdrawn");
        (await Pending(removed.SubjectUuid)).Should().BeFalse();
        var oldRevision = () => Contribute(subject, second, 1, []);
        await oldRevision.Should().ThrowAsync<NormalizationConflictException>();
    }

    [Test]
    public async Task ContributorsPreserveDifferentPoolsInCanonicalOrder()
    {
        var subject = Subject();
        var first = Contributor();
        var second = Contributor();
        var repertoire = NormalizationCandidateUniverseKey.ForRepertoire(Guid.NewGuid());
        await Contribute(subject, first, 0, [await Snapshot(People)]);
        var union = await Contribute(subject, second, 0, [await Snapshot(repertoire)]);
        union
            .CandidateUniverses!.Select(input => input.Universe)
            .Should()
            .Equal(People, repertoire);
        (await fixture.Work.GetInputContributionAsync(subject, first))!
            .CandidateUniverses.Should()
            .Equal(People);
        (await fixture.Work.GetInputContributionAsync(subject, second))!
            .CandidateUniverses.Should()
            .Equal(repertoire);

        var retained = await Contribute(subject, first, 1, []);

        retained.CandidateUniverses!.Select(input => input.Universe).Should().Equal(repertoire);
        await Publish(retained);
    }

    [Test]
    public async Task AdoptingPoolOnlyOwnerPreservesBaselineOwnership()
    {
        var subject = Subject();
        var original = await Begin(subject, [await Snapshot(People)]);
        var adopted = await Contribute(subject, Contributor(), 0, []);
        adopted.CandidateUniverses!.Select(input => input.Universe).Should().Equal(People);
        var baseline = await fixture.Work.GetInputContributionAsync(
            subject,
            new("relisten", "preexisting_owner_bindings", original.SubjectUuid.ToString("D"))
        );
        baseline!.CandidateUniverses.Should().Equal(People);
        await Publish(adopted);
    }

    [Test]
    public async Task ReplayKeepsOriginalPoolGenerationAndRejectsChangedReadSet()
    {
        var subject = Subject();
        var contributor = Contributor();
        var workUuid = Guid.NewGuid();
        var snapshot = await Snapshot(People);
        var original = await Contribute(subject, contributor, 0, [snapshot], workUuid);
        await CreatePerson("Candidate replay rival");

        (await Contribute(subject, contributor, 0, [snapshot], workUuid))
            .Should()
            .BeEquivalentTo(original);
        var changed = async () =>
            await Contribute(subject, contributor, 0, [await Snapshot(People)], workUuid);
        await changed.Should().ThrowAsync<NormalizationConflictException>();
        (await fixture.Work.GetInputContributionAsync(subject, contributor))!
            .Revision.Should()
            .Be(1);
        (await fixture.Work.TryPublishAsync(original.WorkUuid, NeverWrite))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Stale);
    }

    [Test]
    public async Task PublicationAcknowledgesItsOwnPoolChangesWithoutChangingItsAuditTicket()
    {
        var before = await Snapshot(People);
        var subject = Subject();
        var contributor = Contributor();
        var ticket = await Contribute(subject, contributor, 0, [before]);
        var other = await Begin(Subject(), [before]);
        await Publish(other);
        var name = "Self publication " + Guid.NewGuid().ToString("N");

        var result = await fixture.Work.TryPublishAsync(
            ticket.WorkUuid,
            (c, t, ct) => InsertPerson(c, t, name, ct),
            candidateWriteScopes: [People, NormalizationCandidateUniverseKey.Compositions]
        );

        result.Status.Should().Be(NormalizationPublicationStatus.Published);
        (await Snapshot(People)).Generation.Should().BeGreaterThan(before.Generation);
        (await Pending(ticket.SubjectUuid)).Should().BeFalse();
        (await Pending(other.SubjectUuid)).Should().BeTrue();
        var replay = await Contribute(subject, contributor, 0, [before], ticket.WorkUuid);
        replay.Should().BeEquivalentTo(ticket);
        replay.CandidateUniverses.Should().Equal(before);
        (await fixture.Work.TryPublishAsync(ticket.WorkUuid, NeverWrite))
            .Replayed.Should()
            .BeTrue();
    }

    [Test]
    public async Task FailedPublicationRollsBackFactsEpochAndPublishedWatermarkThenRetries()
    {
        var before = await Snapshot(People);
        var ticket = await Begin(Subject(), [before]);
        var name = "Rollback candidate " + Guid.NewGuid().ToString("N");
        var failed = () =>
            fixture.Work.TryPublishAsync(
                ticket.WorkUuid,
                async (c, t, ct) =>
                {
                    await InsertPerson(c, t, name, ct);
                    throw new InvalidOperationException(
                        "Candidate publication failed after insertion."
                    );
                },
                candidateWriteScopes: [People, NormalizationCandidateUniverseKey.Compositions]
            );
        await failed.Should().ThrowAsync<InvalidOperationException>();
        (await Snapshot(People)).Should().Be(before);
        (await PersonExists(name)).Should().BeFalse();
        (
            await fixture.Db.WithWriteConnection(c =>
                c.ExecuteScalarAsync<long?>(
                    "SELECT published_generation FROM normalization.work_candidate_inputs WHERE subject_uuid=@SubjectUuid",
                    ticket
                )
            )
        )
            .Should()
            .BeNull();

        (
            await fixture.Work.TryPublishAsync(
                ticket.WorkUuid,
                (c, t, ct) => InsertPerson(c, t, name, ct),
                candidateWriteScopes: [People, NormalizationCandidateUniverseKey.Compositions]
            )
        )
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);
        (await PersonExists(name)).Should().BeTrue();
        (await Pending(ticket.SubjectUuid)).Should().BeFalse();
    }

    [Test]
    public async Task CancelledPublicationRollsBackCandidateFactsAndAllowsExactRetry()
    {
        var before = await Snapshot(People);
        var ticket = await Begin(Subject(), [before]);
        var name = "Cancelled candidate " + Guid.NewGuid().ToString("N");
        using var cancellation = new CancellationTokenSource();
        var cancelled = () =>
            fixture.Work.TryPublishAsync(
                ticket.WorkUuid,
                async (c, t, ct) =>
                {
                    await InsertPerson(c, t, name, ct);
                    cancellation.Cancel();
                },
                cancellation.Token,
                [People, NormalizationCandidateUniverseKey.Compositions]
            );
        await cancelled.Should().ThrowAsync<OperationCanceledException>();
        (await Snapshot(People)).Should().Be(before);
        (await PersonExists(name)).Should().BeFalse();
        (await Pending(ticket.SubjectUuid)).Should().BeTrue();

        (
            await fixture.Work.TryPublishAsync(
                ticket.WorkUuid,
                (c, t, ct) => InsertPerson(c, t, name, ct),
                candidateWriteScopes: [People, NormalizationCandidateUniverseKey.Compositions]
            )
        )
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);
        (await Pending(ticket.SubjectUuid)).Should().BeFalse();
    }

    [Test]
    public async Task CompetingReadAndWritePublicationsSerializeWithoutLockUpgrade()
    {
        var snapshot = await Snapshot(People);
        var first = await Begin(Subject(), [snapshot]);
        var second = await Begin(Subject(), [snapshot]);
        var firstName = "Concurrent candidate " + Guid.NewGuid().ToString("N");
        var secondName = "Concurrent candidate " + Guid.NewGuid().ToString("N");
        var scopes = new[] { People, NormalizationCandidateUniverseKey.Compositions };

        var results = await Task.WhenAll(
            fixture.Work.TryPublishAsync(
                first.WorkUuid,
                (c, t, ct) => InsertPerson(c, t, firstName, ct),
                candidateWriteScopes: scopes
            ),
            fixture.Work.TryPublishAsync(
                second.WorkUuid,
                (c, t, ct) => InsertPerson(c, t, secondName, ct),
                candidateWriteScopes: scopes
            )
        );

        results
            .Count(result => result.Status == NormalizationPublicationStatus.Published)
            .Should()
            .Be(1);
        results
            .Count(result => result.Status == NormalizationPublicationStatus.Stale)
            .Should()
            .Be(1);
        new[] { await PersonExists(firstName), await PersonExists(secondName) }
            .Count(exists => exists)
            .Should()
            .Be(1);
    }

    [TestCase(false, "fdeaab2b4604c7c2eccf5524821c9b27ade2f50c5c98d078670b9f1230e494f5")]
    [TestCase(true, "028db35fa7c28fa962dd2599328dabe268b4cfc4e9989e4baa61ed8519aa9d07")]
    public async Task PreUniverseRequestHashesAndTicketsStillReplay(bool contributed, string hash)
    {
        var subject = new NormalizationSubjectKey(
            "people",
            "person",
            Guid.Parse("c0d39ca4-6a64-436f-b589-8cd3e0e68d59")
        );
        var contributor = new NormalizationResourceKey(
            "test",
            "candidate-pool",
            "pre-universe-replay"
        );
        var workUuid = Guid.NewGuid();
        Task<NormalizationWorkTicket> Start() =>
            contributed
                ? Contribute(subject, contributor, 0, [], workUuid)
                : fixture.Work.BeginWorkAsync(workUuid, subject, "universe-v1", [], []);
        var original = await Start();
        // Frozen pre-universe hashes must not be regenerated from today's model JSON.
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "UPDATE normalization.work_runs SET request_hash=@hash,ticket=ticket-'CandidateUniverses' WHERE uuid=@workUuid",
                new { hash, workUuid }
            )
        );

        (await Start()).Should().BeEquivalentTo(original);
        await Publish(original);
    }

    [Test]
    public async Task ReviewPrelocksWriteScopesAndRollbackDoesNotAdvanceThem()
    {
        var owner = await Begin(Subject(), []);
        var before = await Snapshot(People);
        var operation = new NormalizationReviewCommand(
            Guid.NewGuid(),
            owner.SubjectUuid,
            0,
            "accept",
            System.Text.Json.JsonSerializer.SerializeToElement(new { reason = "candidate review" }),
            "Candidate review fixture",
            "test"
        );
        var name = "Review candidate " + Guid.NewGuid().ToString("N");
        var failed = () =>
            fixture.Work.ApplyReviewAsync(
                operation,
                async (c, t, ct) =>
                {
                    await InsertPerson(c, t, name, ct);
                    throw new InvalidOperationException("Review failed after candidate write.");
                },
                candidateWriteScopes: [People, NormalizationCandidateUniverseKey.Compositions]
            );
        await failed.Should().ThrowAsync<InvalidOperationException>();
        (await Snapshot(People)).Should().Be(before);
        (await PersonExists(name)).Should().BeFalse();
        var completed = await fixture.Work.ApplyReviewAsync(
            operation,
            (c, t, ct) => InsertPerson(c, t, name, ct),
            candidateWriteScopes: [People, NormalizationCandidateUniverseKey.Compositions]
        );
        completed.Replayed.Should().BeFalse();
        var after = await Snapshot(People);
        after.Generation.Should().BeGreaterThan(before.Generation);
        (await fixture.Work.ApplyReviewAsync(operation, NeverWrite, candidateWriteScopes: [People]))
            .Replayed.Should()
            .BeTrue();
        (await Snapshot(People)).Should().Be(after);
    }

    [Test]
    public async Task CancellationWhileWaitingOnPoolLockLeavesAdmissionRetryable()
    {
        var snapshot = await Snapshot(People);
        var subject = Subject();
        var contributor = Contributor();
        var workUuid = Guid.NewGuid();
        await using var blocker = new NpgsqlConnection(LocalTestDatabase.ConnectionString);
        await blocker.OpenAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await blocker.ExecuteAsync(
            "SELECT generation FROM normalization.candidate_universe_revisions WHERE domain='people' AND scope_uuid=@empty FOR UPDATE",
            new { empty = Guid.Empty },
            transaction
        );
        using var cancellation = new CancellationTokenSource();
        var pending = fixture.Work.BeginContributedWorkAsync(
            workUuid,
            subject,
            "universe-v1",
            new(contributor, 0, [], []),
            cancellationToken: cancellation.Token,
            candidateUniverses: [snapshot]
        );
        try
        {
            await WaitForPoolLock(blocker.ProcessID);
            cancellation.Cancel();
            var cancelled = () => pending;
            await cancelled.Should().ThrowAsync<OperationCanceledException>();
        }
        finally
        {
            cancellation.Cancel();
            await transaction.RollbackAsync();
            try
            {
                await pending;
            }
            catch (OperationCanceledException) { }
        }
        (await fixture.Work.GetInputContributionAsync(subject, contributor)).Should().BeNull();
        await Publish(await Contribute(subject, contributor, 0, [snapshot], workUuid));
    }

    private Task<NormalizationWorkTicket> Begin(
        NormalizationSubjectKey subject,
        IReadOnlyList<NormalizationCandidateUniverseDependency> universes,
        long? expectedConfigurationGeneration = null
    ) =>
        fixture.Work.BeginWorkAsync(
            Guid.NewGuid(),
            subject,
            "universe-v1",
            [],
            [],
            expectedConfigurationGeneration,
            candidateUniverses: universes
        );

    private Task<NormalizationWorkTicket> Contribute(
        NormalizationSubjectKey subject,
        NormalizationResourceKey contributor,
        long revision,
        IReadOnlyList<NormalizationCandidateUniverseDependency> universes,
        Guid? workUuid = null
    ) =>
        fixture.Work.BeginContributedWorkAsync(
            workUuid ?? Guid.NewGuid(),
            subject,
            "universe-v1",
            new(contributor, revision, [], []),
            candidateUniverses: universes
        );

    private async Task Publish(NormalizationWorkTicket ticket) =>
        (await fixture.Work.TryPublishAsync(ticket.WorkUuid, NoWrite))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);

    private Task<NormalizationCandidateUniverseDependency> Snapshot(
        NormalizationCandidateUniverseKey universe
    ) =>
        fixture.Db.WithWriteConnection(async c => new NormalizationCandidateUniverseDependency(
            universe,
            await c.ExecuteScalarAsync<long?>(
                "SELECT generation FROM normalization.candidate_universe_revisions WHERE domain=@Domain AND scope_uuid=@ScopeUuid",
                universe
            ) ?? 0
        ));

    private Task<bool> Exists(NormalizationCandidateUniverseKey universe) =>
        fixture.Db.WithWriteConnection(c =>
            c.ExecuteScalarAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM normalization.candidate_universe_revisions WHERE domain=@Domain AND scope_uuid=@ScopeUuid)",
                universe
            )
        );

    private Task Bump(NormalizationCandidateUniverseKey universe) =>
        fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "SELECT normalization.bump_candidate_universes(@domains::text[],@scopes::uuid[])",
                new { domains = new[] { universe.Domain }, scopes = new[] { universe.ScopeUuid } }
            )
        );

    private Task CreatePerson(string name) =>
        fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "INSERT INTO normalization.people(uuid,display_name,name_key,kind) VALUES(gen_random_uuid(),@name,@key,'person')",
                new { name, key = NormalizedPeopleService.NameKey(name) }
            )
        );

    private static Task InsertPerson(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string name,
        CancellationToken ct
    ) =>
        connection.ExecuteAsync(
            new CommandDefinition(
                "INSERT INTO normalization.people(uuid,display_name,name_key,kind) VALUES(gen_random_uuid(),@name,@key,'person')",
                new { name, key = NormalizedPeopleService.NameKey(name) },
                transaction,
                cancellationToken: ct
            )
        );

    private Task<bool> PersonExists(string name) =>
        fixture.Db.WithWriteConnection(c =>
            c.ExecuteScalarAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM normalization.people WHERE display_name=@name)",
                new { name }
            )
        );

    private async Task<bool> Pending(Guid subjectUuid)
    {
        Guid? after = null;
        for (var pages = 0; pages < 1000; pages++)
        {
            var page = await fixture.Work.GetPendingSubjectsAsync(after, 1000);
            if (page.Any(row => row.SubjectUuid == subjectUuid))
            {
                return true;
            }
            if (page.Count < 1000)
            {
                return false;
            }
            after = page[^1].SubjectUuid;
        }
        throw new AssertionException("Pending work pagination did not terminate.");
    }

    private static async Task WaitForPoolLock(int blockerPid)
    {
        await using var observer = new NpgsqlConnection(LocalTestDatabase.ConnectionString);
        await observer.OpenAsync();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (
                await observer.ExecuteScalarAsync<bool>(
                    "SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE datname=current_database() AND @blockerPid=ANY(pg_blocking_pids(pid)) AND query LIKE '%candidate_universe_revisions%')",
                    new { blockerPid }
                )
            )
            {
                return;
            }
            await Task.Delay(10);
        }
        throw new AssertionException("Admission did not reach the candidate pool lock.");
    }

    private static NormalizationSubjectKey Subject() => new("people", "person", Guid.NewGuid());

    private static NormalizationResourceKey Contributor() =>
        new("test", "candidate-pool", Guid.NewGuid().ToString("D"));

    private static Task NoWrite(NpgsqlConnection c, NpgsqlTransaction t, CancellationToken ct) =>
        Task.CompletedTask;

    private static Task NeverWrite(NpgsqlConnection c, NpgsqlTransaction t, CancellationToken ct) =>
        throw new AssertionException("Stale or replayed work must not run its callback.");
}
