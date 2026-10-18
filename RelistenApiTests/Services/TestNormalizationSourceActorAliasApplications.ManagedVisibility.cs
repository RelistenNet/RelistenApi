using Dapper;
using FluentAssertions;
using Npgsql;
using Relisten.Api.Models.Normalization;
using Relisten.Data;

namespace RelistenApiTests.Services;

public sealed partial class TestNormalizationSourceActorAliasApplications
{
    // Requires additive migration 58. The fixture publishes every managed support through the
    // actual 55/56/57 lifecycle before testing component-aware canonical visibility.
    [Test, Category("RequiresCanonicalAliasVisibility58")]
    public async Task ManagedAliasesAcrossMergedOriginalsHonorCanonicalRejectionRestorationAndReversal()
    {
        var originalName = Unique("Visibility original Robin");
        var survivorName = Unique("Visibility survivor Robin");
        var alias = Unique("Visibility managed Finch");
        var original = await Person(originalName);
        var survivor = await Person(survivorName);
        var firstSource = await Source(Personnel(originalName, alias));
        await Review(firstSource, SourceActorReviewAction.Accept, original);
        firstSource = await Resolve(firstSource.Source);
        var firstApplication = await Apply(firstSource);
        var secondSource = await Source(Personnel(survivorName, alias));
        await Review(secondSource, SourceActorReviewAction.Accept, survivor);
        secondSource = await Resolve(secondSource.Source);
        var secondApplication = await Apply(secondSource);
        var originalAlias = ManagedAliasFromReceipt(firstApplication, alias);
        var survivorAlias = ManagedAliasFromReceipt(secondApplication, alias);
        var owner = (
            await fixture.Work.GetSubjectAsync(
                new NormalizationSubjectKey("people", "person", original)
            )
        )!;
        var mergeUuid = Guid.NewGuid();
        await people.ApplyIdentityDecisionAsync(
            new(
                mergeUuid,
                owner.SubjectUuid,
                owner.EditorialRevision,
                "merge",
                original,
                await PersonRevision(original),
                new(survivor, survivorName),
                await PersonRevision(survivor),
                [],
                "Reviewed identity component for managed visibility"
            ),
            Operator
        );

        await ReviewExistingManagedAlias(survivorAlias, "reject");

        (await CurrentAliases(original)).Should().NotContain(value => value.Wording == alias);
        (await CurrentAliases(survivor)).Should().NotContain(value => value.Wording == alias);
        (await ActiveAssignments(firstSource.Source.SourceUuid)).Should().BeGreaterThan(0);
        (await ActiveAssignments(secondSource.Source.SourceUuid)).Should().BeGreaterThan(0);
        var stillAccepted = await fixture.Db.WithWriteConnection(c =>
            c.ExecuteScalarAsync<string>(
                "SELECT state FROM normalization.person_aliases WHERE uuid=@Uuid",
                originalAlias
            )
        );
        stillAccepted
            .Should()
            .Be("accepted", "component visibility must not rewrite another original alias row");
        await ReviewExistingManagedAlias(survivorAlias, "accept");
        (await CurrentAliases(original))
            .Should()
            .Contain(value => value.Uuid == originalAlias.Uuid);
        (await CurrentAliases(survivor))
            .Should()
            .Contain(value => value.Uuid == survivorAlias.Uuid);
        await ReviewExistingManagedAlias(survivorAlias, "reject");
        owner = (await fixture.Work.GetSubjectAsync(owner.SubjectUuid))!;

        await people.ReverseMergeAsync(
            new(
                Guid.NewGuid(),
                owner.SubjectUuid,
                owner.EditorialRevision,
                mergeUuid,
                original,
                survivor,
                await PersonRevision(original),
                await PersonRevision(survivor),
                "Reversal restores independent original alias review scopes"
            ),
            Operator
        );

        (await CurrentAliases(original))
            .Should()
            .Contain(value => value.Uuid == originalAlias.Uuid);
        (await CurrentAliases(survivor)).Should().NotContain(value => value.Wording == alias);
        (await ActiveAssignments(firstSource.Source.SourceUuid)).Should().BeGreaterThan(0);
        (await ActiveAssignments(secondSource.Source.SourceUuid)).Should().BeGreaterThan(0);
    }
}
