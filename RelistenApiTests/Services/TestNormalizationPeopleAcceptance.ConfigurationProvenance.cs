using Dapper;
using FluentAssertions;
using Npgsql;
using Relisten.Api.Models.Normalization;
using Relisten.Data;

namespace RelistenApiTests.Services;

public sealed partial class TestNormalizationPeopleAcceptance
{
    [Test]
    public async Task OrdinaryWorkReusingRecordingStageUuidCannotAuthenticateAcceptedCreditOwnership()
    {
        var name = UniqueName();
        var person = await Person(name);
        await ActivatePolicy();
        var source = await Source(name);
        var resolution = await Resolve(source);
        var saved = await SaveRecordingStageBeforeAdmission(resolution);
        var stage = saved.Stages.Single();
        stage.ExpectedContributions.Should().BeEmpty();
        var contribution = stage.Contributions.Single();
        var owner = new NormalizationSubjectKey("people", "recording", source.SourceUuid);
        var unrelated = await fixture.Work.BeginContributedBatchAsync(
            Guid.NewGuid(),
            owner,
            saved.Version,
            stage.Contributions,
            stage.ExpectedConfigurationGeneration,
            expectedContributionRevisions: stage.ExpectedContributions
        );
        var installed = await fixture.Work.GetInputContributionsAsync(owner);
        installed
            .Contributions.Should()
            .ContainSingle()
            .Which.Revision.Should()
            .Be(contribution.Contribution.ExpectedRevision + 1);

        var ordinary = await fixture.Work.BeginWorkAsync(
            stage.WorkUuid,
            owner,
            saved.Version,
            contribution.Contribution.Resources,
            contribution.Contribution.DependencySubjectUuids,
            installed.Owner!.DesiredGeneration,
            catalogInputs: contribution.CatalogInputs,
            sourceIdentities: contribution.SourceIdentities,
            mappingRegistry: contribution.MappingRegistry,
            expectedInputRevisions: contribution.ExpectedInputRevisions,
            importedVenues: contribution.ImportedVenues,
            candidateUniverses: contribution.CandidateUniverses,
            expectedOwnerObservations: contribution.ExpectedOwnerObservations,
            sourceUuids: contribution.SourceUuids
        );

        // The ordinary request has the same policy and complete returned ticket, and leaves
        // the contributed headers unchanged. Neither those values nor the UUID proves mode.
        ordinary.Should().BeEquivalentTo(unrelated with { WorkUuid = stage.WorkUuid });
        (await fixture.Work.GetInputContributionsAsync(owner)).Should().BeEquivalentTo(installed);
        (await applier.GetApplicationAsync(saved.AttemptUuid))
            .Should()
            .BeEquivalentTo(
                saved,
                options => options.WithStrictOrdering().AllowingInfiniteRecursion()
            );

        // Registering the recording owner changes observed absence. A new public proposal
        // consumes that actual owner so refusal below must reach configuration discovery.
        var current = await Resolve(source);
        var before = await ReadConfigurationCreditStorage(source.SourceUuid);
        var context = await people.ReadCreditContextAsync(source.SourceUuid);
        var applicationUuid = Guid.NewGuid();

        var refused = await applier.ApplyRecordingAsync(applicationUuid, current.AttemptUuid);

        refused.Result!.Disposition.Should().Be(PeopleAcceptanceDisposition.Unresolved);
        refused.Result.Reason.Should().Be("unsupported_recording_owner_inputs");
        refused.Result.Publications.Should().BeEmpty();
        refused.Stages.Should().BeEmpty();
        refused
            .Plan!.Decisions.Should()
            .ContainSingle()
            .Which.Action.Should()
            .Be(PeopleResolutionAction.MatchExisting);
        refused.Plan.Decisions.Single().PersonUuid.Should().Be(person);
        (await fixture.Work.GetInputContributionsAsync(owner)).Should().BeEquivalentTo(installed);
        (await ReadConfigurationCreditStorage(source.SourceUuid)).Should().Be(before);
        (await people.ReadCreditContextAsync(source.SourceUuid)).Should().BeEquivalentTo(context);
        (await applier.GetApplicationAsync(saved.AttemptUuid))
            .Should()
            .BeEquivalentTo(
                saved,
                options => options.WithStrictOrdering().AllowingInfiniteRecursion()
            );
        var replay = await applier.ApplyRecordingAsync(applicationUuid, current.AttemptUuid);
        replay.Result!.Replayed.Should().BeTrue();
        replay.Result.Reason.Should().Be("unsupported_recording_owner_inputs");
        (await fixture.Work.GetInputContributionsAsync(owner)).Should().BeEquivalentTo(installed);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task GenuineRecordingAdmissionBeforeTicketAttachmentSupportsResumeAndNewProposal(
        bool replaceWithNewProposal
    )
    {
        var name = UniqueName();
        var person = await Person(name);
        await ActivatePolicy();
        var source = await Source(name);
        var resolution = await Resolve(source);
        var saved = await SaveRecordingStageBeforeAdmission(resolution);
        var stage = saved.Stages.Single();
        var owner = new NormalizationSubjectKey("people", "recording", source.SourceUuid);
        var ticket = await fixture.Work.BeginContributedBatchAsync(
            stage.WorkUuid,
            owner,
            saved.Version,
            stage.Contributions,
            stage.ExpectedConfigurationGeneration,
            expectedContributionRevisions: stage.ExpectedContributions
        );
        var admitted = await fixture.Work.GetInputContributionsAsync(owner);
        var installed = admitted.Contributions.Should().ContainSingle().Which;
        (await applier.GetApplicationAsync(saved.AttemptUuid))
            .Should()
            .BeEquivalentTo(
                saved,
                options => options.WithStrictOrdering().AllowingInfiniteRecursion()
            );
        stage.Ticket.Should().BeNull();
        stage.Receipt.Should().BeNull();
        (await people.ReadCreditContextAsync(source.SourceUuid)).Credits.Should().BeEmpty();
        var currentProposal = replaceWithNewProposal ? await Resolve(source) : resolution;
        var applicationUuid = replaceWithNewProposal ? Guid.NewGuid() : saved.AttemptUuid;

        var applied = await applier.ApplyRecordingAsync(
            applicationUuid,
            currentProposal.AttemptUuid
        );

        applied
            .Result!.Disposition.Should()
            .Be(
                PeopleAcceptanceDisposition.Applied,
                "application audit: {0}",
                ApplicationAudit(applied)
            );
        var published = applied.Stages.Should().ContainSingle().Which;
        published.PersonUuid.Should().BeNull();
        published.DecisionOnly.Should().BeFalse();
        published.Receipt.Should().NotBeNull();
        var membership = await fixture.Work.GetInputContributionsAsync(owner);
        var contribution = membership.Contributions.Should().ContainSingle().Which;
        contribution.Contributor.Should().Be(installed.Contributor);
        contribution.Revision.Should().Be(installed.Revision + (replaceWithNewProposal ? 1 : 0));
        var credit = (await people.ReadCreditContextAsync(source.SourceUuid))
            .Credits.Should()
            .ContainSingle()
            .Which.Credit;
        credit.PersonUuid.Should().Be(person);
        credit.State.Should().Be("accepted");
        credit.Basis.Should().Be("validated_match");
        if (replaceWithNewProposal)
        {
            published
                .ExpectedContributions.Should()
                .Equal(
                    new NormalizationInputContributionRevision(
                        installed.Contributor,
                        installed.Revision
                    )
                );
            published
                .Facts.ExtractionObservation.PublishedWorkUuid.Should()
                .Be(currentProposal.ExtractionAttemptUuid);
            (await applier.GetApplicationAsync(saved.AttemptUuid))
                .Should()
                .BeEquivalentTo(
                    saved,
                    options => options.WithStrictOrdering().AllowingInfiniteRecursion()
                );
        }
        else
        {
            published.Ticket.Should().BeEquivalentTo(ticket);
            (published with { Ticket = null, Receipt = null })
                .Should()
                .BeEquivalentTo(
                    stage,
                    options => options.WithStrictOrdering().AllowingInfiniteRecursion()
                );
            applied
                .Plan.Should()
                .BeEquivalentTo(
                    saved.Plan,
                    options => options.WithStrictOrdering().AllowingInfiniteRecursion()
                );
            membership.Contributions.Should().BeEquivalentTo(admitted.Contributions);
        }
        var accepted = await ReadConfigurationCreditStorage(source.SourceUuid);

        var replay = await applier.ApplyRecordingAsync(
            applicationUuid,
            currentProposal.AttemptUuid
        );

        replay.Result!.Replayed.Should().BeTrue();
        replay
            .Stages.Should()
            .BeEquivalentTo(
                applied.Stages,
                options => options.WithStrictOrdering().AllowingInfiniteRecursion()
            );
        (await ReadConfigurationCreditStorage(source.SourceUuid)).Should().Be(accepted);
        (await fixture.Work.GetInputContributionsAsync(owner)).Should().BeEquivalentTo(membership);
    }

    private async Task<PeopleAcceptanceApplication> SaveRecordingStageBeforeAdmission(
        NormalizationPeopleResolutionAttempt resolution
    )
    {
        var applicationUuid = Guid.NewGuid();
        await using var gate = new NpgsqlConnection(LocalTestDatabase.ConnectionString);
        await gate.OpenAsync();
        await using var transaction = await gate.BeginTransactionAsync();
        // Work.LockConfiguration's existing key pauses admission after the public applier
        // saves its genuine recording stage. No fixture reconstructs decisions or authority.
        await gate.ExecuteAsync(
            "SELECT pg_advisory_xact_lock(hashtextextended(@key,0))",
            new { key = $"normalization:configuration:people:recording:{resolution.SourceUuid}" },
            transaction
        );
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var pending = applier.ApplyRecordingAsync(
            applicationUuid,
            resolution.AttemptUuid,
            cancellation.Token
        );
        try
        {
            await WaitForRecordingAdmissionGate(gate.ProcessID);
            var saved = (await applier.GetApplicationAsync(applicationUuid))!;
            saved.Result.Should().BeNull();
            var stage = saved.Stages.Should().ContainSingle().Which;
            stage.PersonUuid.Should().BeNull();
            stage.DecisionOnly.Should().BeFalse();
            stage.Reconciliation.Should().NotBeNull();
            stage.Ticket.Should().BeNull();
            stage.Receipt.Should().BeNull();
            stage
                .Decisions.Should()
                .ContainSingle()
                .Which.Action.Should()
                .Be(PeopleResolutionAction.MatchExisting);
            cancellation.Cancel();
            var cancelled = () => pending;
            await cancelled.Should().ThrowAsync<OperationCanceledException>();
            return saved;
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
                // The original saved stage survives cancellation before Work admission.
            }
        }
    }

    private static async Task WaitForRecordingAdmissionGate(int blockerPid)
    {
        await using var observer = new NpgsqlConnection(LocalTestDatabase.ConnectionString);
        await observer.OpenAsync();
        var deadline = DateTime.UtcNow.AddSeconds(10);
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
        Assert.Fail("The public applier must save its recording stage before Work admission.");
    }

    private Task<string> ReadConfigurationCreditStorage(Guid sourceUuid) =>
        fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<string>(
                """
                SELECT jsonb_build_object(
                    'credits',(SELECT jsonb_agg(to_jsonb(value) ORDER BY uuid)
                        FROM normalization.recording_credits value WHERE source_uuid=@sourceUuid),
                    'history',(SELECT jsonb_agg(to_jsonb(value) ORDER BY uuid,revision,superseding_decision_uuid)
                        FROM normalization.recording_credit_history value WHERE source_uuid=@sourceUuid),
                    'evidence',(SELECT jsonb_agg(to_jsonb(value) ORDER BY credit_uuid,credit_revision,input_revision_uuid,json_pointer)
                        FROM normalization.recording_credit_evidence value JOIN normalization.recording_credits credit
                            ON credit.uuid=value.credit_uuid WHERE credit.source_uuid=@sourceUuid),
                    'managed_credits',(SELECT jsonb_agg(to_jsonb(value) ORDER BY value.credit_uuid)
                        FROM normalization.recording_credit_managed value JOIN normalization.recording_credits credit
                            ON credit.uuid=value.credit_uuid WHERE credit.source_uuid=@sourceUuid),
                    'managed_support',(SELECT jsonb_agg(to_jsonb(value) ORDER BY value.uuid)
                        FROM normalization.recording_credit_contributor_support value JOIN normalization.recording_credits credit
                            ON credit.uuid=value.credit_uuid WHERE credit.source_uuid=@sourceUuid),
                    'unmanaged_support',(SELECT jsonb_agg(to_jsonb(value) ORDER BY value.credit_uuid)
                        FROM normalization.recording_credit_unmanaged_support value JOIN normalization.recording_credits credit
                            ON credit.uuid=value.credit_uuid WHERE credit.source_uuid=@sourceUuid),
                    'actor_contexts',(SELECT jsonb_agg(to_jsonb(value) ORDER BY value.uuid)
                        FROM normalization.recording_actor_contexts value WHERE source_uuid=@sourceUuid))::text
                """,
                new { sourceUuid }
            )
        );
}
