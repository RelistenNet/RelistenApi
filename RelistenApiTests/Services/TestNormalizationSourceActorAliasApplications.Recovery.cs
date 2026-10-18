using Dapper;
using FluentAssertions;
using Npgsql;
using Relisten.Api.Models.Normalization;
using Relisten.Data;

namespace RelistenApiTests.Services;

public sealed partial class TestNormalizationSourceActorAliasApplications
{
    [Test]
    public async Task CancellationBeforeAdmissionResumesTheExactSavedPreparationAfterRestart()
    {
        await SelectPolicy(Policy());
        var name = Unique("Cancelled Robin");
        var person = await Person(name);
        var source = await Source(Personnel(name, Unique("Cancelled Finch")));
        var applicationUuid = Guid.NewGuid();
        await using var blocker = new NpgsqlConnection(LocalTestDatabase.ConnectionString);
        await blocker.OpenAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await blocker.ExecuteAsync(
            "SELECT pg_advisory_xact_lock(hashtextextended(@key,0))",
            new { key = $"normalization:configuration:people:person:{person}" },
            transaction
        );
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var pending = publisher.ApplyAsync(
            applicationUuid,
            source.Resolution.AttemptUuid,
            cancellation.Token
        );
        ExpandedApplication saved;
        try
        {
            saved = await WaitForSavedStage(applicationUuid);
            saved.Stages.Should().ContainSingle();
            saved.Stages[0].Ticket.Should().BeNull();
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
            catch (OperationCanceledException)
            {
                // The persisted first stage is intentionally the recovery checkpoint.
            }
        }

        var resumed = await NewPublisher()
            .ApplyAsync(applicationUuid, source.Resolution.AttemptUuid);

        resumed.Result!.Disposition.Should().Be(SourceActorAliasApplicationDisposition.Complete);
        resumed.Stages[0].StageUuid.Should().Be(saved.Stages[0].StageUuid);
        resumed.Stages[0].WorkUuid.Should().Be(saved.Stages[0].WorkUuid);
        resumed.Stages[0].Facts.Should().BeEquivalentTo(saved.Stages[0].Facts);
        resumed.Stages[0].Ticket.Should().NotBeNull();
        resumed.Stages[0].Receipt.Should().NotBeNull();
    }

    [Test]
    public async Task ACallbackFaultRollsBackCanonicalRowsAuthorityAndReceiptButRetainsRetryableAdmission()
    {
        await SelectPolicy(Policy());
        var name = Unique("Rollback Robin");
        await Person(name);
        var alias = Unique("Rollback Finch");
        var source = await Source(Personnel(name, alias));
        var applicationUuid = Guid.NewGuid();
        var before = await AcceptedAuthority();
        var hook = "source_alias_fixture_fault_" + Guid.NewGuid().ToString("N");
        ExpandedApplication saved;
        try
        {
            await InstallSupportFailure(hook, source.Source.SourceUuid);
            var apply = () => publisher.ApplyAsync(applicationUuid, source.Resolution.AttemptUuid);
            var failure = await apply.Should().ThrowAsync<Exception>();
            HasSyntheticFailure(failure.Which).Should().BeTrue();
            (await AcceptedAuthority()).Should().Be(before);
            saved = (await ReadExpandedApplication(applicationUuid))!;
            saved.Result.Should().BeNull();
            saved.Stages.Should().ContainSingle();
            saved.Stages[0].Ticket.Should().NotBeNull();
            saved.Stages[0].Receipt.Should().BeNull();
            saved.Stages[0].TerminalReason.Should().BeNull();
        }
        finally
        {
            await RemoveSupportFailure(hook);
        }

        var resumed = await NewPublisher()
            .ApplyAsync(applicationUuid, source.Resolution.AttemptUuid);

        resumed.Result!.Disposition.Should().Be(SourceActorAliasApplicationDisposition.Complete);
        resumed.Stages[0].StageUuid.Should().Be(saved.Stages[0].StageUuid);
        resumed.Stages[0].Facts.Should().BeEquivalentTo(saved.Stages[0].Facts);
        Receipts(resumed)
            .SelectMany(receipt => receipt.Names)
            .Should()
            .Contain(outcome => outcome.Wording == alias);
    }

    [Test]
    public async Task ChangedSourceAfterPreparationCannotBeRestampedDuringAdmission()
    {
        await SelectPolicy(Policy());
        var name = Unique("Stale Robin");
        var person = await Person(name);
        var source = await Source(Personnel(name, Unique("Stale Finch")));
        var applicationUuid = Guid.NewGuid();
        var before = await AcceptedAuthority();
        await using var blocker = new NpgsqlConnection(LocalTestDatabase.ConnectionString);
        await blocker.OpenAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await blocker.ExecuteAsync(
            "SELECT pg_advisory_xact_lock(hashtextextended(@key,0))",
            new { key = $"normalization:configuration:people:person:{person}" },
            transaction
        );
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var pending = publisher.ApplyAsync(
            applicationUuid,
            source.Resolution.AttemptUuid,
            cancellation.Token
        );
        var released = false;
        try
        {
            var saved = await WaitForSavedStage(applicationUuid);
            await SetDescription(
                source.Source,
                "The source field changed after this exact stage was saved."
            );
            await transaction.RollbackAsync();
            released = true;
            var result = await pending;
            result.Result!.Disposition.Should().Be(SourceActorAliasApplicationDisposition.Stale);
            result.Stages[0].Facts.Should().BeEquivalentTo(saved.Stages[0].Facts);
            Receipts(result).Should().BeEmpty();
            (await AcceptedAuthority()).Should().Be(before);
        }
        finally
        {
            cancellation.Cancel();
            if (!released)
            {
                await transaction.RollbackAsync();
            }
            try
            {
                await pending;
            }
            catch (OperationCanceledException)
            {
                // Test cleanup must release both the blocked admission and its caller.
            }
        }
    }

    [Test]
    public async Task InputRoundTripToTheSameRevisionCannotAdmitAnOlderSavedApplication()
    {
        await SelectPolicy(Policy());
        var name = Unique("Input ABA Robin");
        var person = await Person(name);
        var source = await Source(Personnel(name, Unique("Input ABA Finch")));
        var applicationUuid = Guid.NewGuid();
        var before = await AcceptedAuthority();
        await using var blocker = new NpgsqlConnection(LocalTestDatabase.ConnectionString);
        await blocker.OpenAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await blocker.ExecuteAsync(
            "SELECT pg_advisory_xact_lock(hashtextextended(@key,0))",
            new { key = $"normalization:configuration:people:person:{person}" },
            transaction
        );
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var pending = publisher.ApplyAsync(
            applicationUuid,
            source.Resolution.AttemptUuid,
            cancellation.Token
        );
        var released = false;
        try
        {
            var saved = await WaitForSavedStage(applicationUuid);
            var expected = saved
                .Stages[0]
                .Contributions.SelectMany(value => value.ExpectedInputRevisions)
                .ToArray();
            expected.Should().OnlyContain(input => input.Generation != null);
            var resource = source.Extraction.Preparation.Binding.Resource;
            var evidence = new NormalizationEvidenceService(fixture.Db);
            var original = (await evidence.GetCurrentInputAsync(resource))!;
            var changed = await evidence.CaptureInputAsync(
                new(
                    Guid.NewGuid(),
                    resource,
                    System.Text.Encoding.UTF8.GetBytes("{\"fixture\":\"temporary changed bytes\"}"),
                    original.Revision.MediaType,
                    original.Revision.CaptureVersion,
                    original.Generation,
                    DateTimeOffset.UtcNow
                )
            );
            var restored = await evidence.CaptureInputAsync(
                new(
                    Guid.NewGuid(),
                    resource,
                    original.Revision.Payload,
                    original.Revision.MediaType,
                    original.Revision.CaptureVersion,
                    changed.Fetch.ResourceGeneration,
                    DateTimeOffset.UtcNow
                )
            );
            restored.Revision.Uuid.Should().Be(original.Revision.Uuid);
            restored.Fetch.ResourceGeneration.Should().BeGreaterThan(original.Generation);
            await transaction.RollbackAsync();
            released = true;

            var result = await pending;

            result.Result!.Disposition.Should().Be(SourceActorAliasApplicationDisposition.Stale);
            result.Stages[0].Facts.Should().BeEquivalentTo(saved.Stages[0].Facts);
            result
                .Stages[0]
                .Ticket.Should()
                .BeNull("the original input-generation CAS must fail before configuration commit");
            result.Stages[0].TerminalReason.Should().NotBeNull();
            var admitted = await fixture.Db.WithWriteConnection(c =>
                c.ExecuteScalarAsync<int>(
                    "SELECT count(*) FROM normalization.work_runs WHERE uuid=@WorkUuid",
                    new { saved.Stages[0].WorkUuid }
                )
            );
            admitted.Should().Be(0);
            (await AcceptedAuthority()).Should().Be(before);
        }
        finally
        {
            cancellation.Cancel();
            if (!released)
            {
                await transaction.RollbackAsync();
            }
            try
            {
                await pending;
            }
            catch (OperationCanceledException)
            {
                // The stale original preparation is retained; another attempt needs fresh authority.
            }
        }
    }

    private async Task<ExpandedApplication> WaitForSavedStage(Guid applicationUuid)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var current = await ReadExpandedApplication(applicationUuid);
            if (current?.Stages.Count > 0)
            {
                return current;
            }
            await Task.Delay(10);
        }
        throw new AssertionException(
            "Public Apply must save its immutable person stage before waiting for admission."
        );
    }

    private Task InstallSupportFailure(string hook, Guid sourceUuid) =>
        fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                $"""
                CREATE FUNCTION normalization.{hook}() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF EXISTS(SELECT 1 FROM normalization.source_actor_alias_assignments assignment
                        WHERE assignment.uuid=NEW.assignment_uuid AND assignment.source_uuid='{sourceUuid}'::uuid) THEN
                        RAISE EXCEPTION 'source_alias_synthetic_callback_failure';
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER {hook} BEFORE INSERT ON normalization.person_alias_contributor_support
                    FOR EACH ROW EXECUTE FUNCTION normalization.{hook}();
                """
            )
        );

    private Task RemoveSupportFailure(string hook) =>
        fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                $"""
                DROP TRIGGER IF EXISTS {hook} ON normalization.person_alias_contributor_support;
                DROP FUNCTION IF EXISTS normalization.{hook}();
                """
            )
        );

    private static bool HasSyntheticFailure(Exception exception) =>
        exception is PostgresException postgres
            && postgres.MessageText.Contains(
                "source_alias_synthetic_callback_failure",
                StringComparison.Ordinal
            )
        || exception.InnerException != null && HasSyntheticFailure(exception.InnerException);
}
