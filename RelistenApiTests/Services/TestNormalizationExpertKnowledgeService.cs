using System.Security.Cryptography;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Relisten;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
using Relisten.Normalization;

namespace RelistenApiTests.Services;

[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestNormalizationExpertKnowledgeService
{
    private DbService db = null!;
    private NormalizationWorkService work = null!;
    private NormalizationExpertKnowledgeService service = null!;
    private string? root;
    private string? previousWrite;
    private string? previousRead;

    [OneTimeSetUp]
    public async Task Start()
    {
        if (Environment.GetEnvironmentVariable("RELISTEN_LOCAL_INGESTION_TESTS") != "1")
        {
            Assert.Ignore("Run ./test-local.sh to include local integration tests.");
        }
        previousWrite = DbService.ConnStr;
        previousRead = DbService.ReadOnlyConnStr;
        db = new(
            LocalTestDatabase.DatabaseUrl,
            null,
            null,
            Host.CreateApplicationBuilder().Environment,
            NullLogger<DbService>.Instance
        );
        work = new(db);
        root = Directory.CreateTempSubdirectory("expert-knowledge-").FullName;
        service = new(db, work, root);
        await db.WithWriteConnection(async c =>
        {
            if (
                !await c.ExecuteScalarAsync<bool>(
                    "SELECT to_regclass('normalization.expert_knowledge_rules') IS NOT NULL"
                )
            )
            {
                await c.ExecuteAsync(Migrations.AddNormalizationExpertKnowledge.SchemaSql);
            }
        });
    }

    [OneTimeTearDown]
    public void Stop()
    {
        if (previousWrite != null)
        {
            DbService.ConnStr = previousWrite;
            DbService.ReadOnlyConnStr = previousRead!;
        }
        if (root != null)
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task ReviewedCorrespondenceSurvivesNewServiceSnapshotAndSupersedesWithoutLosingAudit()
    {
        var definition = await Definition();
        var command = new ExpertKnowledgeSaveCommand(
            Guid.NewGuid(),
            Guid.NewGuid(),
            0,
            "accept",
            definition,
            "Observed exact account correspondence"
        );
        var first = await service.SaveAsync(command, "expert:test");
        var replacement = definition with
        {
            Target = definition.Target with { StableKey = "venue:corrected" },
        };
        await service.SaveAsync(
            command with
            {
                OperationUuid = Guid.NewGuid(),
                ExpectedRevision = 1,
                Definition = replacement,
                Reason = "Correct room, same original account",
            },
            "expert:test"
        );
        var retry = await service.SaveAsync(command, "expert:test");
        retry.Replayed.Should().BeTrue();
        retry.Revision.Should().Be(first.Revision);
        var rebuilt = new NormalizationExpertKnowledgeService(db, new(db), root!);
        var rule = (await rebuilt.GetRuleAsync(command.RuleUuid))!;
        rule.Revision.Should().Be(2);
        rule.Definition.Target.Should().Be(replacement.Target);
        new ExpertKnowledgeResolver(await rebuilt.ReadSnapshotAsync())
            .Evaluate(Observation(definition))
            .Target.Should()
            .Be(replacement.Target);
        var history = await rebuilt.GetHistoryAsync(command.RuleUuid);
        history.Select(entry => entry.Revision).Should().Equal(1, 2);
        history.Select(entry => entry.Operator).Should().OnlyContain(name => name == "expert:test");
        history[0].Command.Definition!.Target.Should().Be(definition.Target);
        history[1].Reason.Should().Be("Correct room, same original account");
        var stale = () =>
            rebuilt.SaveAsync(
                command with
                {
                    OperationUuid = Guid.NewGuid(),
                    Definition = replacement,
                },
                "expert:test"
            );
        await stale.Should().ThrowAsync<NormalizationConflictException>();
    }

    [Test]
    public async Task RejectAndWithdrawalAreNewAuditedRevisionsAndInvalidateTheArtistPool()
    {
        var artist = Guid.NewGuid();
        var definition = await Definition("songs", artist);
        var command = new ExpertKnowledgeSaveCommand(
            Guid.NewGuid(),
            Guid.NewGuid(),
            0,
            "accept",
            definition,
            "Exact artist repertoire alias"
        );
        var before = await ArtistGeneration(artist);
        await service.SaveAsync(command, "expert:test");
        (await ArtistGeneration(artist)).Should().BeGreaterThan(before);
        await service.SaveAsync(
            command with
            {
                OperationUuid = Guid.NewGuid(),
                ExpectedRevision = 1,
                Action = "reject",
                Reason = "This alias is not that repertoire song",
            },
            "expert:test"
        );
        var rejected = new ExpertKnowledgeResolver(await service.ReadSnapshotAsync()).Evaluate(
            Observation(definition)
        );
        rejected.Target.Should().BeNull();
        rejected.ExcludedTargets.Should().Contain(definition.Target);
        await service.SaveAsync(
            command with
            {
                OperationUuid = Guid.NewGuid(),
                ExpectedRevision = 2,
                Action = "retract",
                Definition = null,
                Reason = "Withdraw this correspondence decision",
            },
            "expert:test"
        );
        new ExpertKnowledgeResolver(await service.ReadSnapshotAsync())
            .Evaluate(Observation(definition))
            .Outcome.Should()
            .Be("no_match");
        (await service.GetRuleAsync(command.RuleUuid))!.State.Should().Be("retracted");
        (await service.GetHistoryAsync(command.RuleUuid))
            .Select(entry => entry.Action)
            .Should()
            .Equal("accept", "reject", "retract");
    }

    [Test]
    public async Task CommittedReplaySurvivesChangedEvidenceButANewEditCannotUseItsOldPin()
    {
        var definition = await Definition();
        var command = new ExpertKnowledgeSaveCommand(
            Guid.NewGuid(),
            Guid.NewGuid(),
            0,
            "accept",
            definition,
            "Retained source evidence"
        );
        await service.SaveAsync(command, "expert:test");
        await File.AppendAllTextAsync(definition.Evidence.Single().Path, " changed");
        (await service.SaveAsync(command, "expert:test")).Replayed.Should().BeTrue();
        var edit = () =>
            service.SaveAsync(
                command with
                {
                    OperationUuid = Guid.NewGuid(),
                    ExpectedRevision = 1,
                },
                "expert:test"
            );
        await edit.Should().ThrowAsync<NormalizationConflictException>();
        (await service.GetRuleAsync(command.RuleUuid))!.Revision.Should().Be(1);
        (await service.GetHistoryAsync(command.RuleUuid)).Should().ContainSingle();
    }

    [Test]
    public async Task CanonicalUuidMustExistInTheCorrectDomainAndFrozenKeysNeedNone()
    {
        var definition = await Definition();
        var canonical = Guid.NewGuid();
        await db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "INSERT INTO normalization.people(uuid,display_name,name_key,kind) VALUES(@canonical,'Other domain','other domain','person')",
                new { canonical }
            )
        );
        var command = new ExpertKnowledgeSaveCommand(
            Guid.NewGuid(),
            Guid.NewGuid(),
            0,
            "accept",
            definition with
            {
                Target = definition.Target with { CanonicalUuid = canonical },
            },
            "Optional canonical identity"
        );
        var invalid = () => service.SaveAsync(command, "expert:test");
        await invalid.Should().ThrowAsync<ArgumentException>();
        (await service.GetRuleAsync(command.RuleUuid)).Should().BeNull();
        await db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "INSERT INTO normalization.canonical_venues(uuid,display_name,name_key,kind) VALUES(@canonical,'Existing site','existing site','site')",
                new { canonical }
            )
        );
        await service.SaveAsync(command, "expert:test");
        (await service.GetRuleAsync(command.RuleUuid))!
            .Definition.Target.CanonicalUuid.Should()
            .Be(canonical);
    }

    [Test]
    public async Task ChangedPriorScopeCannotBeReplacedUsingAWriteSetPreparedForAnotherArtist()
    {
        var artistA = Guid.NewGuid();
        var artistB = Guid.NewGuid();
        var artistC = Guid.NewGuid();
        var definition = await Definition("songs", artistA);
        var command = new ExpertKnowledgeSaveCommand(
            Guid.NewGuid(),
            Guid.NewGuid(),
            0,
            "accept",
            definition,
            "Initial artist scope"
        );
        await service.SaveAsync(command, "expert:test");
        var blockedOperation = Guid.NewGuid();
        await using var blocker = new NpgsqlConnection(LocalTestDatabase.ConnectionString);
        await blocker.OpenAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await blocker.ExecuteAsync(
            "SELECT pg_advisory_xact_lock(hashtextextended(@key,0))",
            new { key = $"normalization:review:{blockedOperation}" },
            transaction
        );
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var racing = service.SaveAsync(
            command with
            {
                OperationUuid = blockedOperation,
                ExpectedRevision = 2,
                Definition = definition with
                {
                    Selector = definition.Selector with { ArtistUuid = artistC },
                },
            },
            "expert:test",
            deadline.Token
        );
        var released = false;
        try
        {
            await WaitForReviewLock(blocker.ProcessID);
            await service.SaveAsync(
                command with
                {
                    OperationUuid = Guid.NewGuid(),
                    ExpectedRevision = 1,
                    Definition = definition with
                    {
                        Selector = definition.Selector with { ArtistUuid = artistB },
                    },
                },
                "expert:test"
            );
            var before = await ArtistGeneration(artistB);
            await transaction.CommitAsync();
            released = true;
            var complete = async () => await racing;
            await complete.Should().ThrowAsync<NormalizationConflictException>();
            (await service.GetRuleAsync(command.RuleUuid))!
                .Definition.Selector.ArtistUuid.Should()
                .Be(artistB);
            (await ArtistGeneration(artistB)).Should().Be(before);
        }
        finally
        {
            deadline.Cancel();
            if (!released)
            {
                await transaction.RollbackAsync();
            }
            try
            {
                await racing;
            }
            catch (OperationCanceledException)
            {
                // Failed barriers still release the owned blocked save before the next test.
            }
            catch (NormalizationConflictException)
            {
                // The stale scope preparation is the expected terminal result.
            }
        }
    }

    private static async Task WaitForReviewLock(int blockerPid)
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
            "The save must reach its owned review-operation lock after observing the old scope."
        );
    }

    private async Task<ExpertKnowledgeDefinition> Definition(
        string domain = "venues",
        Guid? artist = null
    )
    {
        var path = Path.Combine(root!, Guid.NewGuid() + ".json");
        var bytes =
            "{\"original\":\"Exact captured artist/recording/account statement\"}"u8.ToArray();
        await File.WriteAllBytesAsync(path, bytes);
        return new(
            domain,
            new("frozen", AccountStableKey: "account:" + Guid.NewGuid(), ArtistUuid: artist),
            new(domain, "frozen", domain + ":source-account"),
            [new(path, bytes.LongLength, Convert.ToHexStringLower(SHA256.HashData(bytes)))],
            "Exact retained frozen-reader payload; reviewed correspondence only."
        );
    }

    private static ExpertKnowledgeObservation Observation(ExpertKnowledgeDefinition definition) =>
        new(
            definition.Domain,
            definition.Selector.Namespace,
            definition.Selector.AccountStableKey!,
            ArtistUuid: definition.Selector.ArtistUuid
        );

    private Task<long> ArtistGeneration(Guid artist) =>
        db.WithWriteConnection(c =>
            c.ExecuteScalarAsync<long>(
                "SELECT COALESCE((SELECT generation FROM normalization.candidate_universe_revisions WHERE domain='repertoire' AND scope_uuid=@artist),0)",
                new { artist }
            )
        );
}
