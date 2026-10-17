using System.Text.Json;
using Dapper;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Data;

namespace RelistenApiTests.Services;

/// <summary>
/// Observed owners must remain locked until publication commits, even when an editorial
/// operation changes no candidate facts and therefore takes no candidate-universe write lock.
/// </summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestNormalizationOwnerObservationConcurrency
{
    private NormalizationDomainFixture fixture = null!;

    [OneTimeSetUp]
    public void Start() => fixture = new();

    [OneTimeTearDown]
    public void Stop() => fixture?.Dispose();

    [Test]
    public async Task PublicationLocksObservedOwnerUntilCommitThenEditorialChangeRequiresRefresh()
    {
        var observedKey = Subject();
        var observed = await fixture.Work.BeginWorkAsync(
            Guid.NewGuid(),
            observedKey,
            "owner-lock-v1",
            [],
            []
        );
        await fixture.Work.TryPublishAsync(observed.WorkUuid, (_, _, _) => Task.CompletedTask);
        var original = await Observe(observedKey);
        var universe = await PeopleUniverse();
        var observerKey = Subject();
        var observer = await BeginObserver(observerKey, original, universe);
        var callbackEntered = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var releasePublication = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var reviewEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task<NormalizationReviewResult>? review = null;
        var publication = fixture.Work.TryPublishAsync(
            observer.WorkUuid,
            async (c, t, ct) =>
            {
                callbackEntered.TrySetResult(
                    await c.ExecuteScalarAsync<int>(
                        new CommandDefinition(
                            "SELECT pg_backend_pid()",
                            transaction: t,
                            cancellationToken: ct
                        )
                    )
                );
                await releasePublication.Task.WaitAsync(ct);
            },
            deadline.Token
        );

        try
        {
            var publisherPid = await callbackEntered.Task.WaitAsync(deadline.Token);
            review = fixture.Work.ApplyReviewAsync(
                new(
                    Guid.NewGuid(),
                    original.SubjectUuid,
                    original.EditorialRevision,
                    "accept",
                    JsonSerializer.SerializeToElement(new { note = "Confirm current facts" }),
                    "Editorial confirmation without changing accepted data",
                    "operator"
                ),
                (_, _, _) =>
                {
                    reviewEntered.TrySetResult();
                    return Task.CompletedTask;
                },
                deadline.Token,
                candidateWriteScopes: []
            );

            // Actual PostgreSQL wait edges establish the race. Elapsed time alone cannot
            // prove that review reached the observed-owner lock before publication finished.
            await WaitForOwnerLock(publisherPid, deadline.Token);
            reviewEntered.Task.IsCompleted.Should().BeFalse();
            review.IsCompleted.Should().BeFalse();
            releasePublication.TrySetResult();
            (await publication.WaitAsync(deadline.Token))
                .Status.Should()
                .Be(NormalizationPublicationStatus.Published);
            (await review.WaitAsync(deadline.Token))
                .EditorialRevision.Should()
                .Be(original.EditorialRevision + 1);
            reviewEntered.Task.IsCompletedSuccessfully.Should().BeTrue();
            (await PeopleUniverse())
                .Should()
                .Be(universe, "editorial-only review writes no accepted candidate facts");
            (await IsPending(observer.SubjectUuid)).Should().BeTrue();

            var refreshed = await BeginObserver(
                observerKey,
                await Observe(observedKey),
                await PeopleUniverse()
            );
            (
                await fixture.Work.TryPublishAsync(
                    refreshed.WorkUuid,
                    (_, _, _) => Task.CompletedTask
                )
            )
                .Status.Should()
                .Be(NormalizationPublicationStatus.Published);
            (await IsPending(observer.SubjectUuid)).Should().BeFalse();
        }
        finally
        {
            releasePublication.TrySetResult();
            deadline.Cancel();
            var operations = review == null ? new Task[] { publication } : [publication, review];
            try
            {
                await Task.WhenAll(operations).WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (OperationCanceledException)
            {
                // Both tasks have been observed after releasing the callback and cancelling
                // their database operations. No paused publisher survives a failed assertion.
            }
        }
    }

    private async Task WaitForOwnerLock(int publisherPid, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var blocked = await fixture.Db.WithWriteConnection(c =>
                c.ExecuteScalarAsync<bool>(
                    new CommandDefinition(
                        """
                        SELECT EXISTS(SELECT 1 FROM pg_stat_activity activity
                            WHERE activity.datname=current_database()
                                AND @publisherPid=ANY(pg_blocking_pids(activity.pid))
                                AND activity.query LIKE '%FROM normalization.work_subjects%FOR UPDATE%')
                        """,
                        new { publisherPid },
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

    private async Task<NormalizationWorkTicket> BeginObserver(
        NormalizationSubjectKey key,
        NormalizationSubjectObservation observation,
        NormalizationCandidateUniverseDependency universe
    )
    {
        var current = await fixture.Work.GetSubjectAsync(key);
        return await fixture.Work.BeginWorkAsync(
            Guid.NewGuid(),
            key,
            "owner-lock-v1",
            [],
            [],
            current?.DesiredGeneration,
            candidateUniverses: [universe],
            expectedOwnerObservations: [observation]
        );
    }

    private async Task<NormalizationSubjectObservation> Observe(NormalizationSubjectKey key)
    {
        var state = (await fixture.Work.GetSubjectAsync(key))!;
        return new(
            state.SubjectUuid,
            state.DesiredGeneration,
            state.EditorialRevision,
            state.PublishedWorkUuid
        );
    }

    private Task<NormalizationCandidateUniverseDependency> PeopleUniverse() =>
        fixture.Db.WithWriteConnection(async c => new NormalizationCandidateUniverseDependency(
            NormalizationCandidateUniverseKey.People,
            await c.ExecuteScalarAsync<long>(
                "SELECT generation FROM normalization.candidate_universe_revisions WHERE domain='people' AND scope_uuid='00000000-0000-0000-0000-000000000000'"
            )
        ));

    private async Task<bool> IsPending(Guid subjectUuid)
    {
        Guid? cursor = null;
        for (var pageNumber = 0; pageNumber < 1000; pageNumber++)
        {
            var page = await fixture.Work.GetPendingSubjectsAsync(cursor, 1000);
            if (page.Any(subject => subject.SubjectUuid == subjectUuid))
            {
                return true;
            }
            if (page.Count < 1000)
            {
                return false;
            }
            cursor = page[^1].SubjectUuid;
        }
        throw new AssertionException("Pending-subject traversal exceeded the test safety bound.");
    }

    private static NormalizationSubjectKey Subject() => new("people", "person", Guid.NewGuid());
}
