using Dapper;
using FluentAssertions;
using Npgsql;
using Relisten.Api.Models.Normalization;
using Relisten.Data;

namespace RelistenApiTests.Services;

public sealed partial class TestNormalizationSourceActorAliasApplications
{
    [Test]
    public async Task InactivePinnedMachinePolicyDoesNotBlockAnIndependentTrustedSourceClaim()
    {
        await SelectPolicy(Policy());
        var trustedName = Unique("Independent trusted Robin");
        var trustedAlias = Unique("Independent trusted Finch");
        var machineName = Unique("Held machine Robin");
        var machineAlias = Unique("Held machine Finch");
        var trustedPerson = await Person(trustedName);
        var machinePerson = await Person(machineName);
        var source = await Source(Personnel(trustedName, trustedAlias));
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "UPDATE sources SET taper_notes=@text WHERE id=@SourceId",
                new { text = Personnel(machineName, machineAlias), source.Source.SourceId }
            )
        );
        source = await Resolve(source.Source);
        var trustedClaim = FindSourceClaim(source, trustedName);
        await ReviewOneSourceClaim(source, trustedClaim, trustedPerson);
        source = await Resolve(source.Source);
        FindSourceClaim(source, machineName)
            .Decision.Action.Should()
            .Be(SourceActorResolutionAction.MatchExisting);
        await SelectPolicy(null);

        var result = await Apply(source);

        Receipts(result)
            .Should()
            .Contain(receipt =>
                receipt.OriginalPersonUuid == trustedPerson
                && receipt.Names.Any(name =>
                    name.Wording == trustedAlias
                    && (
                        name.Disposition == SourceActorAliasNameDisposition.Accepted
                        || name.Disposition == SourceActorAliasNameDisposition.ExactDuplicate
                    )
                )
            );
        result
            .Stages.Where(stage => stage.Receipt != null)
            .Should()
            .OnlyContain(stage =>
                stage.OriginalPersonUuid == trustedPerson
                && stage.Facts.Policy == null
                && stage.Facts.CandidatePools.Count == 0
            );
        (await CurrentAliases(trustedPerson))
            .Should()
            .Contain(alias => alias.Wording == trustedAlias);
        (await CurrentAliases(machinePerson))
            .Should()
            .NotContain(alias => alias.Wording == machineAlias);
        result
            .Result!.Disposition.Should()
            .NotBe(
                SourceActorAliasApplicationDisposition.Complete,
                "the unreviewed machine field remains held under its inactive original policy"
            );
    }

    [TestCase("retract")]
    [TestCase("replace")]
    public async Task CanonicalCorrectionAfterSafeAdditionKeepsTheOldScopeUntilEveryReplacementNameIsCurrent(
        string action
    )
    {
        await SelectPolicy(Policy());
        var name = Unique("Finalization fence Robin");
        var oldAlias = Unique("Finalization old Finch");
        var newAlias = Unique("Finalization required Finch");
        var person = await Person(name);
        var source = await Source(Personnel(name, oldAlias));
        var initial = await Apply(source);
        var oldAssignments = Receipts(initial)
            .Last()
            .ResultingMap.SelectMany(entry => entry.Bindings)
            .Select(binding => binding.AssignmentUuid)
            .ToHashSet();
        source = await Replace(source, Personnel(name, newAlias));
        var saved = await PrepareAdmittedSourceStage(source, person);

        var published = await people.PublishSourceActorAliasStageAsync(
            saved.AttemptUuid,
            saved.Stages.Single().StageUuid
        );

        published.Status.Should().Be(NormalizationPublicationStatus.Published);
        var afterAddition = (await ReadExpandedApplication(saved.AttemptUuid))!;
        afterAddition.Result.Should().BeNull();
        var accepted = ManagedAliasFromReceipt(afterAddition, newAlias);
        var replacement =
            action == "replace"
                ? accepted with
                {
                    Wording = name,
                    Evidence = afterAddition
                        .Stages.Single()
                        .Deltas.SelectMany(delta => delta.Names)
                        .Single(value => value.Wording == name)
                        .Evidence,
                }
                : null;
        await ReviewExistingManagedAlias(accepted, action, replacement);

        var resumed = await NewPublisher()
            .ApplyAsync(saved.AttemptUuid, source.Resolution.AttemptUuid);

        resumed
            .Result!.Disposition.Should()
            .BeOneOf(
                SourceActorAliasApplicationDisposition.Review,
                SourceActorAliasApplicationDisposition.Partial
            );
        (await CurrentAliases(person)).Should().Contain(alias => alias.Wording == oldAlias);
        (await CurrentAliases(person)).Should().NotContain(alias => alias.Wording == newAlias);
        Receipts(resumed)
            .SelectMany(receipt => receipt.RetiredAssignmentUuids)
            .Should()
            .NotIntersectWith(oldAssignments);
        resumed
            .Stages.Single(stage => stage.StageUuid == afterAddition.Stages.Single().StageUuid)
            .Receipt.Should()
            .BeEquivalentTo(
                afterAddition.Stages.Single().Receipt,
                "current canonical facts can hold finalization without rewriting the original successful receipt"
            );
    }

    [Test]
    public async Task MetadataReviewAndSourceHeadCorrectionRetainTheExactPreviouslyConfiguredMap()
    {
        await SelectPolicy(Policy());
        var name = Unique("Configured map Robin");
        var oldAlias = Unique("Configured map old Finch");
        var newAlias = Unique("Configured map new Finch");
        var person = await Person(name);
        var source = await Source(Personnel(name, oldAlias));
        var initial = await Apply(source);
        var committed = initial.Stages.Last(stage => stage.Receipt != null);
        var before = await fixture.Work.GetInputContributionsAsync(
            new("people", "person", person),
            new(100, 100000)
        );
        var originalContribution = before.Contributions.Single(value =>
            value.Contributor == SourceActorAliasApplicationStore.StableContributor
        );
        await ReviewExistingManagedAlias(ManagedAliasFromReceipt(initial, oldAlias), "accept");
        var reviewed = await fixture.Work.GetInputContributionsAsync(
            new("people", "person", person),
            new(100, 100000)
        );
        reviewed.Owner!.DesiredGeneration.Should().BeGreaterThan(before.Owner!.DesiredGeneration);
        reviewed
            .Contributions.Single(value => value.Contributor == originalContribution.Contributor)
            .Revision.Should()
            .Be(originalContribution.Revision);
        source = await Replace(source, Personnel(name, newAlias));

        var corrected = await Apply(source);

        corrected.Result!.Disposition.Should().Be(SourceActorAliasApplicationDisposition.Complete);
        corrected
            .Stages.First(stage =>
                stage.Deltas.Any(delta => delta.Kind == SourceActorAliasDeltaKind.Add)
            )
            .PredecessorStageUuid.Should()
            .Be(
                committed.StageUuid,
                "source/current-owner invalidation does not replace the contributed configuration header"
            );
        var retired = Receipts(corrected)
            .SelectMany(receipt => receipt.RetiredAssignmentUuids)
            .ToHashSet();
        retired
            .Should()
            .Contain(
                committed
                    .Receipt!.ResultingMap.SelectMany(entry => entry.Bindings)
                    .Select(binding => binding.AssignmentUuid)
                    .Distinct()
            );
        (await CurrentAliases(person))
            .Select(alias => alias.Wording)
            .Should()
            .Contain(
                [oldAlias, newAlias],
                "the explicitly reviewed old name remains independent authority after its source support retires"
            );
    }

    [Test]
    public async Task HistoricalIncompleteProposalCannotAdmitAnObservationAfterTheSourceHeadChanges()
    {
        await SelectPolicy(Policy());
        var name = Unique("Historical incomplete Robin");
        var alias = Unique("Historical incomplete Finch");
        var person = await Person(name);
        var source = await Source(Personnel(name, alias));
        var initial = await Apply(source);
        initial.Result!.Disposition.Should().Be(SourceActorAliasApplicationDisposition.Complete);
        var incomplete = await Replace(source, null);
        Claims(incomplete).Should().BeEmpty();
        var before = await AcceptedAuthority();
        var current = await Replace(incomplete, "Recorded live with no alias markers.");
        current
            .Extraction.Ticket.Inputs.Select(input => input.RevisionUuid)
            .Should()
            .NotIntersectWith(
                incomplete.Extraction.Ticket.Inputs.Select(input => input.RevisionUuid)
            );

        var result = await Apply(incomplete);

        result.Result!.Disposition.Should().Be(SourceActorAliasApplicationDisposition.Stale);
        result
            .Stages.Should()
            .BeEmpty("an outdated incomplete field cannot authorize a current observation");
        (await CurrentAliases(person)).Should().Contain(value => value.Wording == alias);
        (await ActiveAssignments(source.Source.SourceUuid)).Should().BeGreaterThan(0);
        (await AcceptedAuthority())
            .Should()
            .Be(
                before,
                "neither the obsolete incomplete proposal nor an unapplied newer clear may retire accepted support"
            );
    }

    private static SourceActorResolutionClaim FindSourceClaim(SourceContext source, string name) =>
        source
            .Resolution.Preparation!.Inputs.SelectMany(input => input.Scopes)
            .SelectMany(scope =>
                scope.Claims.Where(claim =>
                    scope.OriginalScope.Extraction.Assertions[claim.AssertionIndex].Name == name
                )
            )
            .Single();

    private async Task ReviewOneSourceClaim(
        SourceContext source,
        SourceActorResolutionClaim claim,
        Guid person
    )
    {
        var owner = (
            await fixture.Work.GetSubjectAsync(
                SourceActorResolutionService.Owner(source.Source.SourceUuid)
            )
        )!;
        var revision = await fixture.Db.WithWriteConnection(c =>
            c.ExecuteScalarAsync<long>(
                "SELECT coalesce(max(review_revision),0) FROM normalization.source_actor_alias_review_state WHERE source_uuid=@SourceUuid AND assertion_uuid=@AssertionUuid",
                claim.Coordinates
            )
        );
        var preparation = await reviews.PrepareAsync(
            Guid.NewGuid(),
            new(
                source.Extraction.AttemptUuid,
                claim.Coordinates,
                SourceActorReviewAction.Accept,
                owner.EditorialRevision,
                revision,
                person,
                await PersonRevision(person)
            )
        );
        await reviews.ApplyAsync(
            Guid.NewGuid(),
            preparation.PreparationUuid,
            "Operator selected this assertion while a separate field remains machine controlled",
            Operator
        );
    }

    private static PersonAlias ManagedAliasFromReceipt(
        ExpandedApplication application,
        string wording
    )
    {
        var stage = application.Stages.Single(value =>
            value.Receipt?.Names.Any(name => name.Wording == wording) == true
        );
        // One literal may have several valid original spans. Select one assertion for
        // natural-claim review; all original citations remain in the accepted receipt.
        var outcome = stage
            .Receipt!.Names.Where(name => name.Wording == wording)
            .OrderBy(name => name.RequestedAliasUuid)
            .First();
        var prepared = stage
            .Deltas.SelectMany(delta => delta.Names)
            .Single(name => name.RequestedAliasUuid == outcome.RequestedAliasUuid);
        return new(
            outcome.ActualAliasUuid!.Value,
            stage.OriginalPersonUuid,
            wording,
            prepared.Evidence,
            "validated_match"
        );
    }

    private async Task ReviewExistingManagedAlias(
        PersonAlias original,
        string action,
        PersonAlias? replacement = null
    )
    {
        var owner = (
            await fixture.Work.GetSubjectAsync(
                new NormalizationSubjectKey("people", "person", original.PersonUuid)
            )
        )!;
        var revision = await fixture.Db.WithWriteConnection(c =>
            c.ExecuteScalarAsync<long>(
                "SELECT revision FROM normalization.person_aliases WHERE uuid=@Uuid",
                original
            )
        );
        await people.ApplyMetadataDecisionAsync(
            new(
                Guid.NewGuid(),
                owner.SubjectUuid,
                owner.EditorialRevision,
                original.PersonUuid,
                await PersonRevision(original.PersonUuid),
                action,
                "Check the currently accepted source alias before field finalization",
                Alias: replacement ?? original,
                ExpectedAssertionRevision: revision
            ),
            Operator
        );
    }

    private async Task<ExpandedApplication> PrepareAdmittedSourceStage(
        SourceContext source,
        Guid person
    )
    {
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
                // Reuse this public Apply preparation; no caller constructs a stage or facts.
            }
        }
        var hook = "source_alias_fence_fault_" + Guid.NewGuid().ToString("N");
        try
        {
            await InstallSupportFailure(hook, source.Source.SourceUuid);
            var admitAndPublish = () =>
                NewPublisher().ApplyAsync(applicationUuid, source.Resolution.AttemptUuid);
            var failed = await admitAndPublish.Should().ThrowAsync<Exception>();
            HasSyntheticFailure(failed.Which).Should().BeTrue();
        }
        finally
        {
            await RemoveSupportFailure(hook);
        }
        var admitted = (await ReadExpandedApplication(applicationUuid))!;
        admitted.Stages.Should().ContainSingle();
        admitted.Stages.Single().StageUuid.Should().Be(saved.Stages.Single().StageUuid);
        admitted.Stages.Single().Ticket.Should().NotBeNull();
        admitted.Stages.Single().Receipt.Should().BeNull();
        return admitted;
    }
}
