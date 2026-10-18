using System.Data;
using Dapper;
using FluentAssertions;
using Npgsql;
using Relisten.Api.Models.Normalization;
using Relisten.Data;

namespace RelistenApiTests.Services;

public sealed partial class TestNormalizationSourceActorAliasApplications
{
    [Test]
    public async Task PublicSourcePublicationCertifiesItsActualAdmissionWithoutChangingItsFullReceiptOrReplay()
    {
        await SelectPolicy(Policy());
        var name = IndependentActorName();
        await Person(name);
        var source = await Source(Personnel(name, IndependentActorName()));
        var result = await NewService().ApplyAsync(Guid.NewGuid(), source.Resolution.AttemptUuid);
        result.Result!.Disposition.Should().Be(SourceActorAliasApplicationDisposition.Complete);
        var originals = new Dictionary<Guid, SourceActorAliasPersonStage>();
        foreach (var header in result.Stages)
        {
            var actualHash = await fixture.Db.WithWriteConnection(c =>
                c.QuerySingleAsync<string>(
                    "SELECT request_hash FROM normalization.work_runs WHERE uuid=@WorkUuid",
                    new { header.WorkUuid }
                )
            );
            header
                .Receipt!.CertificateVersion.Should()
                .Be(SourceActorAliasApplicationStore.PublishedStageCertificateVersion);
            header.Receipt.AuthenticatedRequestHash.Should().Be(actualHash);
            originals.Add(header.StageUuid, (await applications.GetStageAsync(header.StageUuid))!);
            (await ReadCertifiedHeader(result.AttemptUuid, header.StageUuid))
                .Should()
                .BeEquivalentTo(header);
        }
        var accepted = await AcceptedAuthority();

        var replay = await NewService()
            .ApplyAsync(result.AttemptUuid, source.Resolution.AttemptUuid);

        replay.Result!.Replayed.Should().BeTrue();
        replay.Stages.Should().BeEquivalentTo(result.Stages);
        (await AcceptedAuthority()).Should().Be(accepted);
        foreach (var original in originals.Values)
        {
            (await applications.GetStageAsync(original.StageUuid))
                .Should()
                .BeEquivalentTo(original);
        }
    }

    [Test]
    public async Task ARunningStageCannotSupplyPublishedHistoricalAuthority()
    {
        await SelectPolicy(Policy());
        var name = IndependentActorName();
        var person = await Person(name);
        var source = await Source(Personnel(name, IndependentActorName()));
        var saved = await PrepareAdmittedSourceStage(source, person);
        var stage = saved.Stages.Single();

        var premature = () => ReadCertifiedHeader(saved.AttemptUuid, stage.StageUuid);

        await premature.Should().ThrowAsync<NormalizationConflictException>();
        (await people.PublishSourceActorAliasStageAsync(saved.AttemptUuid, stage.StageUuid))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);
        (await ReadCertifiedHeader(saved.AttemptUuid, stage.StageUuid))
            .Receipt.Should()
            .NotBeNull();
    }

    [Test]
    public async Task ACommittedLegacyHeaderUsesItsGenuineFullProofWithoutBeingRestamped()
    {
        await SelectPolicy(Policy());
        var name = IndependentActorName();
        var person = await Person(name);
        var source = await Source(Personnel(name, IndependentActorName()));
        var saved = await PrepareAdmittedSourceStage(source, person);
        var stage = saved.Stages.Single();
        var hook = "source_alias_legacy_certificate_" + Guid.NewGuid().ToString("N");
        try
        {
            // Omit the two new optional fields at the actual first receipt write. All aliases,
            // assignments, original admission and full receipt still use the real public writer.
            await InstallCertificateProjection(hook, stage.StageUuid, "legacy");
            (await people.PublishSourceActorAliasStageAsync(saved.AttemptUuid, stage.StageUuid))
                .Status.Should()
                .Be(NormalizationPublicationStatus.Published);
        }
        finally
        {
            await RemoveCertificateProjection(hook);
        }
        var original = (await applications.GetStageAsync(stage.StageUuid))!;
        var legacyHeader = await ReadCertifiedHeader(saved.AttemptUuid, stage.StageUuid);
        legacyHeader.Receipt!.CertificateVersion.Should().BeNull();
        legacyHeader.Receipt.AuthenticatedRequestHash.Should().BeNull();

        var completed = await NewPublisher()
            .ApplyAsync(saved.AttemptUuid, source.Resolution.AttemptUuid);

        completed.Result!.Disposition.Should().Be(SourceActorAliasApplicationDisposition.Complete);
        (await applications.GetStageAsync(stage.StageUuid)).Should().BeEquivalentTo(original);
        (await ReadCertifiedHeader(saved.AttemptUuid, stage.StageUuid))
            .Should()
            .BeEquivalentTo(legacyHeader);
    }

    [TestCase("wrong_hash")]
    [TestCase("unknown_version")]
    [TestCase("partial")]
    [TestCase("null_fields")]
    public async Task AContradictoryHistoricalCertificateRefusesInsteadOfFallingBack(string mode)
    {
        await SelectPolicy(Policy());
        var name = IndependentActorName();
        var person = await Person(name);
        var source = await Source(Personnel(name, IndependentActorName()));
        await Apply(source);
        source = await Replace(source, Personnel(name, IndependentActorName()));
        var saved = await PrepareAdmittedSourceStage(source, person);
        var stage = saved.Stages.Single();
        var hook = "source_alias_bad_certificate_" + Guid.NewGuid().ToString("N");
        try
        {
            await InstallCertificateProjection(hook, stage.StageUuid, mode);
            (await people.PublishSourceActorAliasStageAsync(saved.AttemptUuid, stage.StageUuid))
                .Status.Should()
                .Be(NormalizationPublicationStatus.Published);
        }
        finally
        {
            await RemoveCertificateProjection(hook);
        }
        var original = (await applications.GetStageAsync(stage.StageUuid))!;
        var accepted = await AcceptedAuthority();
        var refused = await NewPublisher()
            .ApplyAsync(saved.AttemptUuid, source.Resolution.AttemptUuid);

        // Planning now authenticates the committed map before another stage is admitted.
        // The application reports that preparation conflict through its existing stale result.
        refused.Result!.Disposition.Should().Be(SourceActorAliasApplicationDisposition.Stale);
        refused.Result.Reason.Should().Be("source_application_preparation_changed");
        refused.Stages.Should().HaveCount(saved.Stages.Count);
        var readCorruptCertificate = () => ReadCertifiedHeader(saved.AttemptUuid, stage.StageUuid);
        await readCorruptCertificate.Should().ThrowAsync<NormalizationConflictException>();
        var terminalReplay = await NewPublisher()
            .ApplyAsync(saved.AttemptUuid, source.Resolution.AttemptUuid);
        terminalReplay.Result.Should().BeEquivalentTo(refused.Result! with { Replayed = true });
        terminalReplay.Stages.Should().BeEquivalentTo(refused.Stages);

        (await AcceptedAuthority())
            .Should()
            .Be(accepted, "corrupt metadata cannot authorize finalization or retirement");
        var replay = await people.PublishSourceActorAliasStageAsync(
            saved.AttemptUuid,
            stage.StageUuid
        );
        replay.Status.Should().Be(NormalizationPublicationStatus.Published);
        (await applications.GetStageAsync(stage.StageUuid)).Should().BeEquivalentTo(original);
    }

    [TestCase("hash")]
    [TestCase("ticket")]
    public async Task PublishedCertificatesCheckTheActualWorkInsteadOfTrustingOnlySavedMetadata(
        string mode
    )
    {
        await SelectPolicy(Policy());
        var name = IndependentActorName();
        await Person(name);
        var source = await Source(Personnel(name, IndependentActorName()));
        var result = await NewService().ApplyAsync(Guid.NewGuid(), source.Resolution.AttemptUuid);
        var stage = result.Stages.First();
        await using var c = new NpgsqlConnection(LocalTestDatabase.ConnectionString);
        await c.OpenAsync();
        await using var t = await c.BeginTransactionAsync();
        try
        {
            var mutation =
                mode == "hash"
                    ? "request_hash=repeat('0',64)"
                    : "ticket=jsonb_set(ticket,'{PolicyVersion}',to_jsonb('different-policy'::text))";
            await c.ExecuteAsync(
                $"UPDATE normalization.work_runs SET {mutation} WHERE uuid=@WorkUuid",
                new { stage.WorkUuid },
                t
            );
            var read = () =>
                SourceActorAliasApplicationStore.RequirePublishedStageHeader(
                    c,
                    t,
                    result.AttemptUuid,
                    stage.StageUuid,
                    CancellationToken.None
                );

            await read.Should().ThrowAsync<NormalizationConflictException>();
        }
        finally
        {
            // This fault injection never changes the retained history after the test.
            await t.RollbackAsync();
        }
        (await ReadCertifiedHeader(result.AttemptUuid, stage.StageUuid))
            .Should()
            .BeEquivalentTo(stage);
    }

    private Task<SourceActorAliasPersonStageHeader> ReadCertifiedHeader(
        Guid applicationUuid,
        Guid stageUuid
    ) =>
        fixture.Db.WithWriteConnection(async connection =>
        {
            var c = (NpgsqlConnection)connection;
            await using var t = await c.BeginTransactionAsync(IsolationLevel.RepeatableRead);
            await c.ExecuteAsync("SET TRANSACTION READ ONLY", transaction: t);
            var header = await SourceActorAliasApplicationStore.RequirePublishedStageHeader(
                c,
                t,
                applicationUuid,
                stageUuid,
                CancellationToken.None
            );
            await t.CommitAsync();
            return header;
        });

    private Task InstallCertificateProjection(string hook, Guid stageUuid, string mode)
    {
        var projection = mode switch
        {
            "legacy" => "NEW.receipt_header-'CertificateVersion'-'AuthenticatedRequestHash'",
            "wrong_hash" =>
                "jsonb_set(NEW.receipt_header,'{AuthenticatedRequestHash}',to_jsonb(repeat('0',64)))",
            "unknown_version" =>
                "jsonb_set(NEW.receipt_header,'{CertificateVersion}',to_jsonb('unsupported-certificate'::text))",
            "partial" => "NEW.receipt_header-'CertificateVersion'",
            "null_fields" =>
                "NEW.receipt_header || '{\"CertificateVersion\":null,\"AuthenticatedRequestHash\":null}'::jsonb",
            _ => throw new ArgumentException("Unknown test certificate fault."),
        };
        return fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                $"""
                CREATE FUNCTION normalization.{hook}() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW.stage_uuid='{stageUuid:D}'::uuid AND OLD.receipt IS NULL AND NEW.receipt IS NOT NULL THEN
                        NEW.receipt_header:={projection};
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER {hook} BEFORE UPDATE ON normalization.source_actor_alias_person_stages
                    FOR EACH ROW EXECUTE FUNCTION normalization.{hook}();
                """
            )
        );
    }

    private Task RemoveCertificateProjection(string hook) =>
        fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                $"""
                DROP TRIGGER IF EXISTS {hook} ON normalization.source_actor_alias_person_stages;
                DROP FUNCTION IF EXISTS normalization.{hook}();
                """
            )
        );
}
