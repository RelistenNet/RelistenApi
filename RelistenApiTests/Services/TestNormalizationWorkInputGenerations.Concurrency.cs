using Dapper;
using FluentAssertions;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace RelistenApiTests.Services;

public sealed partial class TestNormalizationWorkInputGenerations
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task AdmissionHoldsItsObservedInputUntilCommitBeforeAHeadRefreshCanAdvance(
        bool contributed
    )
    {
        var input = await Capture("Concurrent original input");
        var subject = Subject();
        var contributor = Contributor();
        var existing = await Begin(contributed, subject, Guid.NewGuid(), contributor, input);
        await using var ownerGate = new NpgsqlConnection(LocalTestDatabase.ConnectionString);
        await ownerGate.OpenAsync();
        await using var transaction = await ownerGate.BeginTransactionAsync();
        await ownerGate.ExecuteAsync(
            "SELECT uuid FROM normalization.work_subjects WHERE uuid=@uuid FOR UPDATE",
            new { uuid = existing.SubjectUuid },
            transaction
        );
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        // Work locks inputs before its owner. The held owner row pauses admission only after
        // its resource read and generation check, so a capture must wait for Work's input lock.
        var admission = Begin(
            contributed,
            subject,
            Guid.NewGuid(),
            contributor,
            input,
            contributorRevision: contributed ? 1 : 0,
            ct: cancellation.Token
        );
        Task<Input>? refresh = null;
        var ownerGateReleased = false;
        try
        {
            var admissionPid = await WaitForBlockedSession(ownerGate.ProcessID);
            refresh = Capture("Concurrent updated input", input, cancellation.Token);
            await WaitForBlockedSession(admissionPid);
            await transaction.RollbackAsync();
            ownerGateReleased = true;
            var admitted = await admission;
            var updated = await refresh;

            admitted.Inputs.Single().Generation.Should().Be(input.Generation);
            admitted.Inputs.Single().RevisionUuid.Should().Be(input.Revision);
            updated.Generation.Should().Be(input.Generation + 1);
            (
                await fixture.Work.TryPublishAsync(
                    admitted.WorkUuid,
                    (_, _, _) =>
                        throw new AssertionException(
                            "A later input head must prevent this publication"
                        )
                )
            )
                .Status.Should()
                .Be(NormalizationPublicationStatus.Stale);
        }
        finally
        {
            cancellation.Cancel();
            if (!ownerGateReleased)
            {
                await transaction.RollbackAsync();
            }
            await IgnoreCancellation(admission);
            if (refresh != null)
            {
                await IgnoreCancellation(refresh);
            }
        }
    }

    private static async Task<int> WaitForBlockedSession(int blockerPid)
    {
        await using var observer = new NpgsqlConnection(LocalTestDatabase.ConnectionString);
        await observer.OpenAsync();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var pid = await observer.QuerySingleOrDefaultAsync<int?>(
                """
                SELECT pid FROM pg_stat_activity WHERE datname=current_database()
                    AND @blockerPid=ANY(pg_blocking_pids(pid)) ORDER BY pid LIMIT 1
                """,
                new { blockerPid }
            );
            if (pid.HasValue)
            {
                return pid.Value;
            }
            await Task.Delay(10);
        }
        throw new AssertionException(
            "The public operation did not reach its expected PostgreSQL lock."
        );
    }

    private static async Task IgnoreCancellation(Task operation)
    {
        try
        {
            await operation;
        }
        catch (Exception exception) when (HasCause(exception, typeof(OperationCanceledException)))
        {
            // Release both public operations if a lock-order assertion fails or times out.
        }
    }
}
