using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Relisten;
using Relisten.Api.Models.Normalization;
using Relisten.Data;

namespace RelistenApiTests.Services;

public sealed partial class TestNormalizationSourceActorAliasUpgrade
{
    [Test, CancelAfter(120_000)]
    public async Task VisibilityUpgradeAndCanonicalReviewUseTheSameCandidateBeforeHistoryLockOrder()
    {
        if (Environment.GetEnvironmentVariable("RELISTEN_LOCAL_INGESTION_TESTS") != "1")
        {
            Assert.Ignore("Run ./test-local.sh to include disposable local database coverage.");
        }

        var database = "relisten_alias_upgrade_" + Guid.NewGuid().ToString("N");
        var previousWrite = DbService.ConnStr;
        var previousRead = DbService.ReadOnlyConnStr;
        var created = false;
        try
        {
            await CreateSchema52(database);
            created = true;
            var environment = Host.CreateApplicationBuilder(
                new HostApplicationBuilderSettings { EnvironmentName = Environments.Production }
            ).Environment;
            var db = new DbService(
                $"postgresql://relisten:local_dev_password@127.0.0.1:15432/{database}",
                null,
                null,
                environment,
                NullLogger<DbService>.Instance
            );
            var work = new NormalizationWorkService(db);
            var evidence = new NormalizationEvidenceService(db);
            var people = new NormalizedPeopleService(db, work);
            await ApplyActual53(db);
            await ApplyActual54(db);
            await ApplyVisibilityUpgradeSchema(db, Migrations.AddSourceActorResolution.SchemaSql);
            await ApplyVisibilityUpgradeSchema(
                db,
                Migrations.AddSourceActorReviewPreparations.SchemaSql
            );
            await ApplyVisibilityUpgradeSchema(
                db,
                Migrations.AddSourceActorAliasPublication.SchemaSql
            );

            var wording = "concurrentalias" + Guid.NewGuid().ToString("N");
            var original = await PublishVisibilityUpgradeOriginal(
                db,
                work,
                evidence,
                people,
                wording
            );
            (await people.FindCandidatesAsync(wording)).Should().ContainSingle();
            var command = await PrepareVisibilityConcurrencyReview(db, work, original, wording);
            var before = await db.WithWriteConnection(c =>
                c.QuerySingleAsync<string>(
                    "SELECT to_jsonb(alias)::text FROM normalization.person_aliases alias WHERE uuid=@Alias",
                    new { original.Alias }
                )
            );

            var result = await RunVisibilityUpgradeAgainstPausedReview(database, people, command);

            result.OperationUuid.Should().Be(command.OperationUuid);
            result.Replayed.Should().BeFalse();
            result.EditorialRevision.Should().Be(command.ExpectedEditorialRevision + 1);
            await AssertRetainedVisibilityConcurrencyReview(db, command, before);
            (await people.FindCandidatesAsync(wording)).Should().BeEmpty();
            await AssertVisibilityUpgradeAliasRows(db, wording, expected: 0);
        }
        finally
        {
            DbService.ConnStr = previousWrite;
            DbService.ReadOnlyConnStr = previousRead;
            if (created)
            {
                await DropOwnedDatabase(database);
            }
        }
    }

    private static async Task<PersonMetadataDecision> PrepareVisibilityConcurrencyReview(
        DbService db,
        NormalizationWorkService work,
        (Guid Person, Guid Alias) original,
        string wording
    )
    {
        var owner = (
            await work.GetSubjectAsync(
                new NormalizationSubjectKey("people", "person", original.Person)
            )
        )!;
        var citation = await db.WithWriteConnection(c =>
            c.QuerySingleAsync<NormalizedEvidenceReference>(
                """
                SELECT input_revision_uuid AS "InputRevisionUuid",json_pointer AS "JsonPointer",
                    start_code_point AS "StartCodePoint",end_code_point AS "EndCodePoint",quote AS "Quote"
                FROM normalization.person_aliases WHERE uuid=@Alias
                """,
                new { original.Alias }
            )
        );
        var revision = await db.WithWriteConnection(c =>
            c.QuerySingleAsync<long>(
                "SELECT revision FROM normalization.person_aliases WHERE uuid=@Alias",
                new { original.Alias }
            )
        );
        return new(
            Guid.NewGuid(),
            owner.SubjectUuid,
            owner.EditorialRevision,
            original.Person,
            await VisibilityUpgradeRevision(db, original.Person),
            "reject",
            "Keep the public metadata review and visibility upgrade in the same lock order",
            Alias: new(original.Alias, original.Person, wording, citation),
            ExpectedAssertionRevision: revision
        );
    }

    private static async Task<NormalizationReviewResult> RunVisibilityUpgradeAgainstPausedReview(
        string database,
        NormalizedPeopleService people,
        PersonMetadataDecision command
    )
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.CurrentContext.CancellationToken
        );
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var ct = deadline.Token;
        await using var gate = CatalogConnection(database);
        await using var migration = CatalogConnection(database);
        await using var observer = CatalogConnection(database);
        await gate.OpenAsync(ct);
        await migration.OpenAsync(ct);
        await observer.OpenAsync(ct);
        await using var gateTransaction = await gate.BeginTransactionAsync(ct);
        await gate.ExecuteAsync(
            new CommandDefinition(
                "LOCK TABLE normalization.person_aliases_history IN SHARE MODE",
                transaction: gateTransaction,
                cancellationToken: ct
            )
        );
        Task<NormalizationReviewResult>? reviewTask = null;
        Task? migrationTask = null;
        try
        {
            reviewTask = people.ApplyMetadataDecisionAsync(command, "fixture-operator", ct);
            var reviewPid = await WaitForVisibilityReviewHistoryLock(observer, gate.ProcessID, ct);
            migrationTask = ApplyVisibilityMigrationOnConnection(migration, ct);
            await WaitForVisibilityMigrationCandidateLock(
                observer,
                migration.ProcessID,
                reviewPid,
                ct
            );
            (
                await observer.ExecuteScalarAsync<bool>(
                    new CommandDefinition(
                        """
                        SELECT EXISTS(SELECT 1 FROM pg_locks
                            WHERE pid=@migrationPid
                                AND relation='normalization.person_aliases_history'::regclass
                                AND mode IN ('ShareLock','ShareRowExclusiveLock','ExclusiveLock','AccessExclusiveLock'))
                        """,
                        new { migrationPid = migration.ProcessID },
                        cancellationToken: ct
                    )
                )
            ).Should().BeFalse("migration must wait for the review's candidate clocks before requesting a history lock that blocks its append");

            // The public review already holds the candidate clocks and is waiting to append
            // actual history. A migration that locks history first keeps it blocked after
            // this gate is released and creates the inverse dependency/deadlock. Checking
            // its actual requested locks also catches a history lock queued behind the
            // review, and an internal public-service deadlock retry cannot hide bad order.
            await gateTransaction.RollbackAsync(CancellationToken.None);
            await Task.WhenAll(reviewTask, migrationTask).WaitAsync(ct);
            return await reviewTask;
        }
        finally
        {
            try
            {
                // Dispose also releases an active gate after an earlier assertion fails.
                // A completed Npgsql transaction can still expose its connection.
                await gateTransaction.DisposeAsync();
            }
            finally
            {
                deadline.Cancel();
                // Observe both operations before their connections and owned database end.
                // Failure here is diagnostic cleanup; the original test failure is preserved.
                try
                {
                    await Task.WhenAll(new Task?[] { reviewTask, migrationTask }.OfType<Task>());
                }
                catch (Exception exception)
                {
                    TestContext.Progress.WriteLine(
                        $"Visibility race cleanup observed: {exception.GetType().Name}"
                    );
                }
            }
        }
    }

    private static async Task<int> WaitForVisibilityReviewHistoryLock(
        NpgsqlConnection observer,
        int gatePid,
        CancellationToken ct
    )
    {
        while (true)
        {
            var pid = await observer.QuerySingleOrDefaultAsync<int?>(
                new CommandDefinition(
                    """
                    SELECT pid FROM pg_stat_activity
                    WHERE datname=current_database() AND wait_event_type='Lock'
                        AND @gatePid=ANY(pg_blocking_pids(pid))
                        AND query LIKE '%INSERT INTO normalization.person_aliases_history%'
                    """,
                    new { gatePid },
                    cancellationToken: ct
                )
            );
            if (pid.HasValue)
            {
                return pid.Value;
            }
            await Task.Delay(10, ct);
        }
    }

    private static async Task WaitForVisibilityMigrationCandidateLock(
        NpgsqlConnection observer,
        int migrationPid,
        int reviewPid,
        CancellationToken ct
    )
    {
        while (true)
        {
            if (
                await observer.ExecuteScalarAsync<bool>(
                    new CommandDefinition(
                        """
                        SELECT EXISTS(SELECT 1 FROM pg_stat_activity
                            WHERE datname=current_database() AND pid=@migrationPid AND wait_event_type='Lock'
                                AND @reviewPid=ANY(pg_blocking_pids(pid))
                                AND query LIKE '%candidate_universe_revisions%')
                        """,
                        new { migrationPid, reviewPid },
                        cancellationToken: ct
                    )
                )
            )
            {
                return;
            }
            await Task.Delay(10, ct);
        }
    }

    private static async Task ApplyVisibilityMigrationOnConnection(
        NpgsqlConnection connection,
        CancellationToken ct
    )
    {
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await connection.ExecuteAsync(
            new CommandDefinition(
                Migrations.AddCanonicalAliasReviewVisibility.SchemaSql,
                transaction: transaction,
                commandTimeout: 30,
                cancellationToken: ct
            )
        );
        await transaction.CommitAsync(ct);
    }

    private static async Task AssertRetainedVisibilityConcurrencyReview(
        DbService db,
        PersonMetadataDecision command,
        string before
    )
    {
        var retained = await db.WithWriteConnection(c =>
            c.QuerySingleAsync<(bool Raw, bool History, bool Support, bool Receipt)>(
                """
                SELECT
                    EXISTS(SELECT 1 FROM normalization.person_aliases alias WHERE uuid=@alias
                        AND person_uuid=@PersonUuid AND state='rejected' AND basis='reviewed'
                        AND revision=@revision+1
                        AND to_jsonb(alias)-'state'-'basis'-'revision'=(@before::jsonb)-'state'-'basis'-'revision'),
                    EXISTS(SELECT 1 FROM normalization.person_aliases_history history WHERE uuid=@alias
                        AND superseding_decision_uuid=@OperationUuid
                        AND to_jsonb(history)-'superseding_decision_uuid'=@before::jsonb),
                    EXISTS(SELECT 1 FROM normalization.person_alias_unmanaged_support WHERE alias_uuid=@alias),
                    EXISTS(SELECT 1 FROM normalization.review_operations WHERE uuid=@OperationUuid)
                """,
                new
                {
                    alias = command.Alias!.Uuid,
                    command.PersonUuid,
                    revision = command.ExpectedAssertionRevision,
                    before,
                    command.OperationUuid,
                }
            )
        );
        retained
            .Raw.Should()
            .BeTrue("review preserves the original alias UUID, owner and citation");
        retained
            .History.Should()
            .BeTrue("the public review appends its exact original alias and operation receipt");
        retained
            .Support.Should()
            .BeTrue("visibility does not remove the original publication support");
        retained.Receipt.Should().BeTrue();
    }
}
