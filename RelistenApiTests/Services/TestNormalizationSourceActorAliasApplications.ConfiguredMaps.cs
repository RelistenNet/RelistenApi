using System.Data;
using Dapper;
using FluentAssertions;
using Npgsql;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
using Relisten.Normalization;

namespace RelistenApiTests.Services;

public sealed partial class TestNormalizationSourceActorAliasApplications
{
    [Test]
    public async Task ProjectedMapsForDistinctPeopleMatchTheirCompleteAuditProofAndReplay()
    {
        await SelectPolicy(Policy());
        var actors = new List<(Guid Person, string Name, string Alias)>();
        for (var index = 0; index < 3; index++)
        {
            var name = IndependentActorName();
            actors.Add((await Person(name), name, IndependentActorName()));
        }
        var source = await Source(
            "Personnel:\n"
                + string.Join(
                    "\n",
                    actors.Select(actor => $"{actor.Name} aka {actor.Alias} - vocals")
                )
        );

        var result = await Apply(source);

        result.Result!.Disposition.Should().Be(SourceActorAliasApplicationDisposition.Complete);
        var maps = new Dictionary<Guid, SourceActorAliasConfiguredMap>();
        foreach (var actor in actors)
        {
            var map = await CompareProjectedMapWithAudit(actor.Person);
            map.Configured.Should().BeSameAs(map.Committed);
            map.Committed!.ApplicationUuid.Should().Be(result.AttemptUuid);
            map.Committed.ResultingMap.Should()
                .ContainSingle(entry => entry.Scope.SourceUuid == source.Source.SourceUuid);
            (await CurrentAliases(actor.Person))
                .Should()
                .Contain(value => value.Wording == actor.Alias);
            maps.Add(actor.Person, map);
        }
        var original = (await applications.GetStageAsync(result.Stages.First().StageUuid))!;
        original.Facts.CandidatePools.Should().NotBeEmpty();
        original
            .Facts.Inputs.SelectMany(input => input.Scopes)
            .SelectMany(scope => scope.OriginalScope.Extraction.Assertions)
            .Should()
            .HaveCount(actors.Count);

        var replay = await NewPublisher()
            .ApplyAsync(result.AttemptUuid, source.Resolution.AttemptUuid);

        replay.Result!.Replayed.Should().BeTrue();
        (await applications.GetStageAsync(original.StageUuid)).Should().BeEquivalentTo(original);
        foreach (var actor in actors)
        {
            (await CompareProjectedMapWithAudit(actor.Person))
                .Should()
                .BeEquivalentTo(maps[actor.Person], options => options.WithStrictOrdering());
        }
    }

    [Test]
    public async Task ProjectedClearTombstonesRetainTheOtherSourcesCompleteAcceptedMap()
    {
        await SelectPolicy(Policy());
        var name = IndependentActorName();
        var person = await Person(name);
        var first = await Source(Personnel(name, IndependentActorName()));
        await Apply(first);
        var peerAlias = IndependentActorName();
        var peer = await Source(Personnel(name, peerAlias));
        await Apply(peer);
        var before = await CompareProjectedMapWithAudit(person);
        var peerScopes = before
            .Committed!.ResultingMap.Where(entry =>
                entry.Scope.SourceUuid == peer.Source.SourceUuid
            )
            .ToArray();
        first = await Replace(first, "Personnel:\nRecorded Robin - guitar");
        Claims(first).Should().BeEmpty();

        var cleared = await Apply(first);
        var map = await CompareProjectedMapWithAudit(person);

        cleared.Result!.Disposition.Should().Be(SourceActorAliasApplicationDisposition.Complete);
        map.Committed!.ApplicationUuid.Should().Be(cleared.AttemptUuid);
        var tombstone = map.Committed.ResultingMap.Single(entry =>
            entry.Scope.SourceUuid == first.Source.SourceUuid
        );
        tombstone.Bindings.Should().BeEmpty();
        tombstone.LastCompleteObservation!.ExplicitClear.Should().BeTrue();
        tombstone
            .LastCompleteObservation.FieldJsonPointer.Should()
            .Be(tombstone.Scope.FieldJsonPointer);
        map.Committed.ResultingMap.Where(entry => entry.Scope.SourceUuid == peer.Source.SourceUuid)
            .Should()
            .BeEquivalentTo(peerScopes, options => options.WithStrictOrdering());
        (await ActiveAssignments(first.Source.SourceUuid)).Should().Be(0);
        (await ActiveAssignments(peer.Source.SourceUuid)).Should().BeGreaterThan(0);
        (await CurrentAliases(person)).Should().Contain(value => value.Wording == peerAlias);
    }

    [Test]
    public async Task ProjectedIncompleteObservationsRetainTheirOriginalBindingsAndFullScopeProof()
    {
        await SelectPolicy(Policy());
        var name = IndependentActorName();
        var alias = IndependentActorName();
        var person = await Person(name);
        var source = await Source(Personnel(name, alias));
        var initial = await Apply(source);
        var original = initial.Stages.First();
        var before = await CompareProjectedMapWithAudit(person);
        var originalScope = before.Committed!.ResultingMap.Single(entry =>
            entry.Scope.SourceUuid == source.Source.SourceUuid
        );
        source = await Replace(source, null);

        var partial = await Apply(source);
        var map = await CompareProjectedMapWithAudit(person);

        partial.Result!.Disposition.Should().Be(SourceActorAliasApplicationDisposition.Partial);
        map.Committed!.ApplicationUuid.Should().Be(partial.AttemptUuid);
        var retained = map.Committed.ResultingMap.Single(entry =>
            entry.Scope.SourceUuid == source.Source.SourceUuid
        );
        retained
            .Bindings.Should()
            .BeEquivalentTo(originalScope.Bindings, options => options.WithStrictOrdering());
        retained.LastCompleteObservation.Should().Be(originalScope.LastCompleteObservation);
        retained
            .IncompleteObservations.Should()
            .Contain(value =>
                !value.Complete
                && !value.ExplicitClear
                && value.Proposal.ExtractionAttemptUuid == source.Extraction.AttemptUuid
                && value.FieldJsonPointer == retained.Scope.FieldJsonPointer
            );
        (await CurrentAliases(person)).Should().Contain(value => value.Wording == alias);
        (await applications.GetStageAsync(original.StageUuid)).Should().BeEquivalentTo(original);
    }

    [Test]
    public async Task ProjectedHistoricalWithdrawalRetainsItsZeroBindingAuthorityWithoutInventingAClear()
    {
        await SelectPolicy(Policy());
        var name = IndependentActorName();
        var alias = IndependentActorName();
        var person = await Person(name);
        var historical = await Source(Personnel(name, alias));
        var accepted = await Apply(historical);
        await Replace(historical, null);
        await Review(historical, SourceActorReviewAction.Reject);

        var withdrawn = await Apply(historical);
        var map = await CompareProjectedMapWithAudit(person);

        withdrawn.Result!.Disposition.Should().Be(SourceActorAliasApplicationDisposition.Stale);
        map.Committed!.ApplicationUuid.Should().Be(withdrawn.AttemptUuid);
        var tombstone = map.Committed.ResultingMap.Single(entry =>
            entry.Scope.SourceUuid == historical.Source.SourceUuid
        );
        tombstone.Bindings.Should().BeEmpty();
        tombstone.LastCompleteObservation!.ExplicitClear.Should().BeFalse();
        tombstone
            .LastWithdrawalAuthorities.Should()
            .NotBeEmpty()
            .And.OnlyContain(value =>
                value.Kind == SourceActorAliasAuthorityKind.Withdrawal
                && value.ReviewOperationUuid != null
                && value.Claim.Proposal.ExtractionAttemptUuid == historical.Extraction.AttemptUuid
            );
        (await ActiveAssignments(historical.Source.SourceUuid)).Should().Be(0);
        (await CurrentAliases(person)).Should().NotContain(value => value.Wording == alias);
        foreach (var original in accepted.Stages)
        {
            (await applications.GetStageAsync(original.StageUuid))
                .Should()
                .BeEquivalentTo(original);
        }
    }

    [Test]
    public async Task ProjectedNativeAllocationsSurviveMetadataReviewAndANewCurrentBaselineHead()
    {
        await SelectPolicy(Policy());
        var native = await PublishNativeBaseline();
        var nativeProof = await ReadNativeBaselineProof(native);
        var sourceAlias = IndependentActorName();
        var source = await Source(Personnel(native.Name, sourceAlias));
        var accepted = await Apply(source);
        await ReviewExistingManagedAlias(ManagedAliasFromReceipt(accepted, sourceAlias), "accept");
        var reviewed = await CompareProjectedMapWithAudit(native.PersonUuid);
        reviewed.Committed!.StageUuid.Should().Be(accepted.Stages.Last().StageUuid);
        var baseline = reviewed.Committed.Contributions.Single(value =>
            value.Contribution.Contributor.ResourceType == "preexisting_owner_bindings"
        );
        baseline.Contribution.Resources.Should().Contain(native.Resource);
        await AdvanceNativeBaseline(new NormalizationEvidenceService(fixture.Db), native.Resource);
        var nextSource = await Source(Personnel(native.Name, IndependentActorName()));

        var next = await Apply(nextSource);
        var map = await CompareProjectedMapWithAudit(native.PersonUuid);

        next.Result!.Disposition.Should().Be(SourceActorAliasApplicationDisposition.Complete);
        map.Committed!.ApplicationUuid.Should().Be(next.AttemptUuid);
        var currentBaseline = map.Committed.Contributions.Single(value =>
            value.Contribution.Contributor == baseline.Contribution.Contributor
        );
        currentBaseline.Contribution.Resources.Should().Equal(baseline.Contribution.Resources);
        currentBaseline
            .Contribution.DependencySubjectUuids.Should()
            .Equal(baseline.Contribution.DependencySubjectUuids);
        currentBaseline
            .Contribution.OwnerObservationSubjectUuids.Should()
            .BeEquivalentTo(baseline.Contribution.OwnerObservationSubjectUuids);
        map.Committed.ResultingMap.Select(entry => entry.Scope.SourceUuid)
            .Should()
            .Contain([source.Source.SourceUuid, nextSource.Source.SourceUuid]);
        (await ReadNativeBaselineProof(native)).Should().Be(nativeProof);
        (await CurrentAliases(native.PersonUuid))
            .Should()
            .Contain(value => value.Wording == sourceAlias);
    }

    [Test]
    public async Task PendingMapRefusesUntilStaleRecoverySelectsTheExactCommittedPredecessor()
    {
        await SelectPolicy(Policy());
        var name = IndependentActorName();
        var oldAlias = IndependentActorName();
        var newAlias = IndependentActorName();
        var person = await Person(name);
        var source = await Source(Personnel(name, oldAlias));
        var initial = await Apply(source);
        var previous = initial.Stages.Last();
        source = await Replace(source, Personnel(name, newAlias));
        var prepared = await PrepareAdmittedSourceStage(source, person);
        var pendingStage = prepared.Stages.Single();

        var pendingRead = () => CompareProjectedMapWithAudit(person);
        await pendingRead
            .Should()
            .ThrowAsync<NormalizationConflictException>()
            .WithMessage("*Resume saved source alias stage*");
        await ReviewExistingManagedAlias(ManagedAliasFromReceipt(initial, oldAlias), "accept");
        (
            await people.PublishSourceActorAliasStageAsync(
                prepared.AttemptUuid,
                pendingStage.StageUuid
            )
        )
            .Status.Should()
            .Be(NormalizationPublicationStatus.Stale);
        var stale = await NewPublisher()
            .ApplyAsync(prepared.AttemptUuid, source.Resolution.AttemptUuid);
        stale.Result!.Disposition.Should().Be(SourceActorAliasApplicationDisposition.Stale);
        var fallback = await CompareProjectedMapWithAudit(person);
        fallback.Configured!.StageUuid.Should().Be(pendingStage.StageUuid);
        fallback.Configured.ApplicationUuid.Should().Be(prepared.AttemptUuid);
        fallback.Committed!.StageUuid.Should().Be(previous.StageUuid);
        fallback.Committed.ApplicationUuid.Should().Be(initial.AttemptUuid);
        fallback
            .Committed.ResultingMap.Should()
            .BeEquivalentTo(
                previous.Receipt!.ResultingMap,
                options => options.WithStrictOrdering()
            );
        (await applications.GetStageAsync(previous.StageUuid)).Should().BeEquivalentTo(previous);

        var recovered = await Apply(source);
        var current = await CompareProjectedMapWithAudit(person);

        recovered.Result!.Disposition.Should().Be(SourceActorAliasApplicationDisposition.Complete);
        current.Configured.Should().BeSameAs(current.Committed);
        current.Committed!.ApplicationUuid.Should().Be(recovered.AttemptUuid);
        (await CurrentAliases(person))
            .Select(value => value.Wording)
            .Should()
            .Contain([oldAlias, newAlias]);
        var retained = (await applications.GetStageAsync(pendingStage.StageUuid))!;
        retained.Facts.Should().BeEquivalentTo(pendingStage.Facts);
        retained.Ticket.Should().BeEquivalentTo(pendingStage.Ticket);
        retained.Receipt.Should().BeNull();
        (await NewPublisher().ApplyAsync(stale.AttemptUuid, source.Resolution.AttemptUuid))
            .Result!.Replayed.Should()
            .BeTrue();
    }

    [TestCase("map")]
    [TestCase("headers")]
    public async Task MissingRequiredProjectionShapeRefusesAndRollbackRestoresTheOriginalAudit(
        string field
    )
    {
        await SelectPolicy(Policy());
        var name = IndependentActorName();
        var person = await Person(name);
        var source = await Source(Personnel(name, IndependentActorName()));
        var accepted = await Apply(source);
        var original = (await applications.GetStageAsync(accepted.Stages.Last().StageUuid))!;
        await using var c = new NpgsqlConnection(LocalTestDatabase.ConnectionString);
        await c.OpenAsync();
        await using var t = await c.BeginTransactionAsync(IsolationLevel.RepeatableRead);
        try
        {
            // Privileged corruption exists only in this rolled-back test transaction. No
            // accepted alias/support is fabricated, and the immutable trigger is restored.
            await c.ExecuteAsync(
                "ALTER TABLE normalization.source_actor_alias_person_stages DISABLE TRIGGER source_actor_alias_stage_retained",
                transaction: t
            );
            var mutation =
                field == "map"
                    ? "receipt=jsonb_set(receipt,'{Value,ResultingMap}','null'::jsonb)"
                    : "stage=jsonb_set(stage,'{Value,Value,ExpectedContributions}','null'::jsonb)";
            await c.ExecuteAsync(
                $"UPDATE normalization.source_actor_alias_person_stages SET {mutation} WHERE stage_uuid=@StageUuid",
                new { original.StageUuid },
                t
            );
            await c.ExecuteAsync(
                "ALTER TABLE normalization.source_actor_alias_person_stages ENABLE TRIGGER source_actor_alias_stage_retained",
                transaction: t
            );
            var membership = await ReadConfiguredMapMembership(c, t, person);
            var read = () =>
                SourceActorAliasApplicationStore.ReadConfiguredMapInSnapshotAsync(
                    c,
                    t,
                    person,
                    membership,
                    CancellationToken.None
                );

            await read.Should().ThrowAsync<NormalizationConflictException>();
        }
        finally
        {
            await t.RollbackAsync();
        }
        (await applications.GetStageAsync(original.StageUuid)).Should().BeEquivalentTo(original);
        await CompareProjectedMapWithAudit(person);
    }

    private Task<SourceActorAliasConfiguredMap> CompareProjectedMapWithAudit(Guid person) =>
        fixture.Db.WithWriteConnection(async connection =>
        {
            var c = (NpgsqlConnection)connection;
            await using var t = await c.BeginTransactionAsync(IsolationLevel.RepeatableRead);
            var membership = await ReadConfiguredMapMembership(c, t, person);
            var projected = await SourceActorAliasApplicationStore.ReadConfiguredMapInSnapshotAsync(
                c,
                t,
                person,
                membership,
                CancellationToken.None
            );
            var configured =
                await SourceActorAliasApplicationStore.GetConfiguredStageInSnapshotAsync(
                    c,
                    t,
                    person,
                    membership,
                    CancellationToken.None
                );
            var committed =
                configured?.Receipt != null ? configured
                : configured?.PredecessorStageUuid is Guid predecessor
                    ? await SourceActorAliasApplicationStore.ReadStage(
                        c,
                        t,
                        predecessor,
                        CancellationToken.None
                    )
                : null;
            CompareProjectedStageWithAudit(projected.Configured, configured);
            CompareProjectedStageWithAudit(projected.Committed, committed);
            await t.CommitAsync();
            return projected;
        });

    private static Task<NormalizationInputContributionsSnapshot> ReadConfiguredMapMembership(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid person
    ) =>
        NormalizationWorkService.GetInputContributionsInSnapshotAsync(
            c,
            t,
            new("people", "person", person),
            PeopleResolutionCapacity.ContributionMembership,
            CancellationToken.None
        );

    private static void CompareProjectedStageWithAudit(
        SourceActorAliasMapStage? projected,
        SourceActorAliasPersonStage? original
    )
    {
        if (original == null)
        {
            projected.Should().BeNull();
            return;
        }
        projected.Should().NotBeNull();
        projected!.StageUuid.Should().Be(original.StageUuid);
        projected.WorkUuid.Should().Be(original.WorkUuid);
        projected.OriginalPersonUuid.Should().Be(original.OriginalPersonUuid);
        projected.OwnerSubjectUuid.Should().Be(original.OwnerSubjectUuid);
        projected.PredecessorStageUuid.Should().Be(original.PredecessorStageUuid);
        projected
            .ConfiguredPredecessorStageUuid.Should()
            .Be(original.ConfiguredPredecessorStageUuid);
        projected
            .ExpectedConfigurationGeneration.Should()
            .Be(original.ExpectedConfigurationGeneration);
        projected
            .ExpectedContributions.Should()
            .BeEquivalentTo(
                original.ExpectedContributions,
                options => options.WithStrictOrdering()
            );
        projected
            .UnmanagedBaseline.Should()
            .Be(original.Facts.Membership.UnmanagedBaseline?.Contributor);
        projected
            .Contributions.Should()
            .BeEquivalentTo(
                original.Contributions.Select(SourceActorAliasContributionAllocation.FromPrepared),
                options => options.WithStrictOrdering()
            );
        projected
            .ResultingMap.Should()
            .BeEquivalentTo(
                original.Receipt?.ResultingMap ?? [],
                options => options.WithStrictOrdering()
            );
    }
}
