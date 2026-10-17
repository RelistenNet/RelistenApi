using System.Diagnostics;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Data;

namespace RelistenApiTests.Services;

/// <summary>
/// A concurrent correction must not pair old candidate selection with newer accepted facts
/// or publication clocks. Database locks pause the real read without a production test hook.
/// </summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestNormalizationCandidateSnapshot
{
    private NormalizationDomainFixture fixture = null!;
    private NormalizationCandidateService candidates = null!;

    [OneTimeSetUp]
    public void Start()
    {
        fixture = new();
        candidates = new(fixture.Db);
    }

    [OneTimeTearDown]
    public void Stop() => fixture?.Dispose();

    [Test]
    public async Task EmptyFactsSnapshotRejectsAdmissionAfterANewMatchingIdentityArrives()
    {
        var name = "Empty Snapshot " + Guid.NewGuid().ToString("N");
        var empty = await candidates.FindPeopleFactsAsync(
            new(IdentityMatchingDomain.People, [name], [], MinimumNameSimilarity: 0.99)
        );
        empty.Complete.Should().BeTrue();
        empty.Selection.Items.Should().BeEmpty();
        empty.Facts.Should().BeEmpty();
        empty.ReadSet.OwnerObservations.Should().BeEmpty();
        empty.ReadSet.CandidateUniverses.Should().ContainSingle();

        var input = await fixture.Capture(name, "people");
        var person = new CanonicalPerson(Guid.NewGuid(), name);
        await fixture.PublishPeople(
            new(fixture.Db, fixture.Work),
            input,
            new([person], [], [], [])
        );

        var consumer = new NormalizationSubjectKey("people", "recording", Guid.NewGuid());
        Func<Task> stale = () =>
            fixture.Work.BeginWorkAsync(
                Guid.NewGuid(),
                consumer,
                "snapshot-fixture-v1",
                [],
                [],
                catalogInputs: empty.ReadSet.CatalogInputs,
                candidateUniverses: empty.ReadSet.CandidateUniverses,
                expectedOwnerObservations: empty.ReadSet.OwnerObservations
            );
        await stale.Should().ThrowAsync<NormalizationConflictException>();
        (await fixture.Work.GetSubjectAsync(consumer)).Should().BeNull();

        var current = await candidates.FindPeopleFactsAsync(empty.Query);
        current.Facts.Should().ContainSingle(fact => fact.Header.Uuid == person.Uuid);
        current.ReadSet.CandidateUniverses.Should().NotEqual(empty.ReadSet.CandidateUniverses);
    }

    [Test]
    public async Task FactCandidateLimitRetainsTheOriginalUntruncatedSelectionAndBlocksAcceptance()
    {
        var name = "Candidate Cap " + Guid.NewGuid().ToString("N");
        var input = await fixture.Capture(name, "people");
        var first = new CanonicalPerson(Guid.NewGuid(), name);
        var second = new CanonicalPerson(Guid.NewGuid(), name);
        await fixture.PublishPeople(
            new(fixture.Db, fixture.Work),
            input,
            new([first, second], [], [], [])
        );
        var query = new NormalizationCandidateQuery(
            IdentityMatchingDomain.People,
            [name],
            [],
            NameLimit: 2,
            MinimumNameSimilarity: .99
        );

        var capped = await candidates.FindPeopleFactsAsync(query, new() { MaximumCandidates = 1 });

        capped
            .Selection.Items.Select(item => item.Uuid)
            .Should()
            .BeEquivalentTo(new[] { first.Uuid, second.Uuid });
        capped.Selection.NameCandidatesTruncated.Should().BeFalse();
        capped.Facts.Should().ContainSingle();
        capped.Facts.Single().Hit.Uuid.Should().Be(capped.Selection.Items[0].Uuid);
        capped
            .ReadSet.OwnerObservations.Select(owner => owner.SubjectUuid)
            .Should()
            .Equal(capped.Facts.Single().Header.OwnerSubjectUuid!.Value);
        capped
            .Issues.Should()
            .Contain(issue =>
                issue.Kind == CandidateFactsIssueKind.CandidateLimit
                && issue.PreventsAutomaticAcceptance
            );
        capped.Complete.Should().BeFalse();

        var complete = await candidates.FindPeopleFactsAsync(
            query,
            new() { MaximumCandidates = 2 }
        );

        complete.Selection.NameCandidatesTruncated.Should().BeFalse();
        complete
            .Facts.Select(fact => fact.Header.Uuid)
            .Should()
            .BeEquivalentTo(new[] { first.Uuid, second.Uuid });
        complete.ReadSet.OwnerObservations.Should().HaveCount(2);
        complete.Complete.Should().BeTrue();
    }

    [Test]
    public async Task ConcurrentReviewKeepsSelectionFactsAndOriginalStampsInOneSnapshot()
    {
        var originalName = "Snapshot Recorder " + Guid.NewGuid().ToString("N");
        var correctedName = "Corrected " + originalName;
        var input = await fixture.Capture(originalName, "people");
        var person = new CanonicalPerson(Guid.NewGuid(), originalName);
        await fixture.PublishPeople(
            new(fixture.Db, fixture.Work),
            input,
            new([person], [], [], [])
        );
        var names = new List<string> { person.DisplayName };
        var identifiers = new List<MatchingExternalIdentity>();
        var query = new NormalizationCandidateQuery(
            IdentityMatchingDomain.People,
            names,
            identifiers,
            MinimumNameSimilarity: 0.99
        );
        var before = await candidates.FindPeopleFactsAsync(query);
        before.Complete.Should().BeTrue();
        var owner = (
            await fixture.Work.GetSubjectAsync(
                new NormalizationSubjectKey("people", "person", person.Uuid)
            )
        )!;

        var locked = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var operationTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var review = fixture.Work.ApplyReviewAsync(
            new(
                Guid.NewGuid(),
                owner.SubjectUuid,
                owner.EditorialRevision,
                "replace",
                JsonSerializer.SerializeToElement(new { person.Uuid, displayName = correctedName }),
                "Correct the display identity while a candidate read is in progress.",
                "test-operator"
            ),
            async (connection, transaction, ct) =>
            {
                await connection.ExecuteAsync(
                    new CommandDefinition(
                        "LOCK TABLE normalization.people IN ACCESS EXCLUSIVE MODE",
                        transaction: transaction,
                        cancellationToken: ct
                    )
                );
                var pid = await connection.ExecuteScalarAsync<int>(
                    new CommandDefinition(
                        "SELECT pg_backend_pid()",
                        transaction: transaction,
                        cancellationToken: ct
                    )
                );
                locked.SetResult(pid);
                await release.Task.WaitAsync(ct);
                await connection.ExecuteAsync(
                    new CommandDefinition(
                        """
                        UPDATE normalization.people SET display_name=@correctedName,name_key=@nameKey,
                            metadata_reviewed=true,revision=revision+1 WHERE uuid=@Uuid
                        """,
                        new
                        {
                            person.Uuid,
                            correctedName,
                            nameKey = NormalizedPeopleService.NameKey(correctedName),
                        },
                        transaction,
                        cancellationToken: ct
                    )
                );
            },
            cancellationToken: operationTimeout.Token,
            candidateWriteScopes:
            [
                NormalizationCandidateUniverseKey.Compositions,
                NormalizationCandidateUniverseKey.People,
            ]
        );

        Task<NormalizationCandidateFactsSnapshot<PersonCandidateFacts>>? reading = null;
        var blocked = false;
        try
        {
            var blocker = await locked.Task.WaitAsync(operationTimeout.Token);
            reading = candidates.FindPeopleFactsAsync(
                query,
                cancellationToken: operationTimeout.Token
            );
            blocked = await IsReaderBlockedAfterTakingSnapshot(blocker);
            // These caller-owned lists can change while I/O is paused. The admitted lookup
            // recipe must remain the one supplied at the start of the read.
            names[0] = "No matching person";
            identifiers.Add(new("test", "person", "not-present"));
        }
        finally
        {
            release.TrySetResult();
            await Task.WhenAll(review, (Task?)reading ?? Task.CompletedTask);
        }
        var snapshot = await reading!;

        blocked
            .Should()
            .BeTrue("the read must have an established snapshot before the correction commits");
        snapshot.Complete.Should().BeTrue();
        snapshot.Query.Names.Should().Equal(originalName);
        snapshot.Query.ExternalIdentities.Should().BeEmpty();
        snapshot.QueryHash.Should().Be(before.QueryHash);
        snapshot.Facts.Single().Header.DisplayText.Should().Be(originalName);
        snapshot.Facts.Single().Header.MetadataReviewed.Should().BeFalse();
        snapshot.ReadSet.Should().BeEquivalentTo(before.ReadSet);

        var current = await candidates.FindPeopleFactsAsync(
            new(IdentityMatchingDomain.People, [originalName], [], MinimumNameSimilarity: 0.99)
        );
        current.Facts.Single().Header.DisplayText.Should().Be(correctedName);
        current.Facts.Single().Header.MetadataReviewed.Should().BeTrue();
        current.ReadSet.CandidateUniverses.Should().NotEqual(snapshot.ReadSet.CandidateUniverses);
        current
            .ReadSet.OwnerObservations.Single(x => x.SubjectUuid == owner.SubjectUuid)
            .EditorialRevision.Should()
            .Be(owner.EditorialRevision + 1);

        var consumer = new NormalizationSubjectKey("people", "recording", Guid.NewGuid());
        Func<Task> staleAdmission = () =>
            fixture.Work.BeginWorkAsync(
                Guid.NewGuid(),
                consumer,
                "snapshot-fixture-v1",
                [],
                [],
                catalogInputs: snapshot.ReadSet.CatalogInputs,
                candidateUniverses: snapshot.ReadSet.CandidateUniverses,
                expectedOwnerObservations: snapshot.ReadSet.OwnerObservations
            );
        await staleAdmission.Should().ThrowAsync<NormalizationConflictException>();
        (await fixture.Work.GetSubjectAsync(consumer)).Should().BeNull();

        var refreshed = await fixture.Work.BeginWorkAsync(
            Guid.NewGuid(),
            consumer,
            "snapshot-fixture-v1",
            [],
            [],
            catalogInputs: current.ReadSet.CatalogInputs,
            candidateUniverses: current.ReadSet.CandidateUniverses,
            expectedOwnerObservations: current.ReadSet.OwnerObservations
        );
        var callbackRan = false;
        var published = await fixture.Work.TryPublishAsync(
            refreshed.WorkUuid,
            (_, _, _) =>
            {
                callbackRan = true;
                return Task.CompletedTask;
            }
        );
        published.Status.Should().Be(NormalizationPublicationStatus.Published);
        callbackRan.Should().BeTrue();
    }

    private async Task<bool> IsReaderBlockedAfterTakingSnapshot(int blocker)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(10))
        {
            var blocked = await fixture.Db.WithWriteConnection(connection =>
                connection.ExecuteScalarAsync<bool>(
                    """
                    SELECT EXISTS(SELECT 1 FROM pg_stat_activity
                        WHERE datname=current_database() AND backend_xmin IS NOT NULL
                            AND wait_event_type='Lock' AND @blocker=ANY(pg_blocking_pids(pid)))
                    """,
                    new { blocker }
                )
            );
            if (blocked)
            {
                return true;
            }
            await Task.Delay(10);
        }
        return false;
    }
}
