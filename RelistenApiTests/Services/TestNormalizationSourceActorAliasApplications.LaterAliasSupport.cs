using Dapper;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Data;

namespace RelistenApiTests.Services;

public sealed partial class TestNormalizationSourceActorAliasApplications
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task LaterSupportOnAnExistingAssignmentRequiresItsOwnIntroducingWorksCertificate(
        bool corruptLaterCertificate
    )
    {
        await SelectPolicy(Policy());
        var name = IndependentActorName();
        var alias = IndependentActorName();
        var person = await Person(name);
        var reviewedPerson = await Person(IndependentActorName());
        await ReviewCanonicalAlias(reviewedPerson, alias, "retract");
        var reviewedOwner = (
            await fixture.Work.GetSubjectAsync(
                new NormalizationSubjectKey("people", "person", reviewedPerson)
            )
        )!;
        var mergeUuid = Guid.NewGuid();
        await people.ApplyIdentityDecisionAsync(
            new(
                mergeUuid,
                reviewedOwner.SubjectUuid,
                reviewedOwner.EditorialRevision,
                "merge",
                reviewedPerson,
                await PersonRevision(reviewedPerson),
                new(person, name),
                await PersonRevision(person),
                [],
                "Merge the fixture identities while retaining the reviewed alias at its original owner."
            ),
            Operator
        );
        var source = await Source(Personnel(name, alias));
        var initial = await Apply(source);
        initial
            .Result!.Disposition.Should()
            .BeOneOf(
                SourceActorAliasApplicationDisposition.Partial,
                SourceActorAliasApplicationDisposition.Review
            );
        await AssertCertifiedAliasSupportCountApplication(initial.AttemptUuid);
        var initialNames = Receipts(initial).SelectMany(receipt => receipt.Names).ToArray();
        initialNames
            .Where(outcome => outcome.Wording == alias)
            .Should()
            .NotBeEmpty()
            .And.OnlyContain(outcome =>
                outcome.SupportUuid == null
                && (
                    outcome.Disposition == SourceActorAliasNameDisposition.Pending
                    || outcome.Disposition
                        == SourceActorAliasNameDisposition.ProtectedReviewedNaturalClaim
                )
            );
        var assignmentUuid = initialNames
            .Where(outcome => outcome.Wording == name && outcome.AssignmentUuid.HasValue)
            .Select(outcome => outcome.AssignmentUuid!.Value)
            .Distinct()
            .Single();
        var originalWork = await fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<Guid>(
                "SELECT introduced_work_uuid FROM normalization.source_actor_alias_assignments WHERE uuid=@assignmentUuid",
                new { assignmentUuid }
            )
        );
        reviewedOwner = (await fixture.Work.GetSubjectAsync(reviewedOwner.SubjectUuid))!;

        // Reversing the redirect removes the other person's veto from this identity component.
        // Its review/history remain intact. The primary spelling keeps the source assignment
        // active, while a new application can accept the previously pending alias spelling.
        await people.ReverseMergeAsync(
            new(
                Guid.NewGuid(),
                reviewedOwner.SubjectUuid,
                reviewedOwner.EditorialRevision,
                mergeUuid,
                reviewedPerson,
                person,
                await PersonRevision(reviewedPerson),
                await PersonRevision(person),
                "Separate the fixture identities and retain each original person's editorial claims."
            ),
            Operator
        );
        var resolution = await resolver.ResolveAsync(Guid.NewGuid(), source.Extraction.AttemptUuid);
        resolution.Result!.Disposition.Should().Be(SourceActorResolutionDisposition.Published);
        source = source with { Resolution = resolution };
        var later = await PublishAliasSupportCountProjection(
            source,
            person,
            corruptLaterCertificate ? "wrong_hash" : "unchanged"
        );
        var added = later
            .Receipt!.Names.Where(outcome =>
                outcome.Wording == alias && outcome.SupportUuid.HasValue
            )
            .ToArray();
        added.Should().NotBeEmpty();
        added.Should().OnlyContain(outcome => outcome.AssignmentUuid == assignmentUuid);
        added
            .Select(outcome => outcome.RequestedAliasUuid)
            .Should()
            .NotIntersectWith(
                initialNames
                    .Where(outcome => outcome.Wording == alias)
                    .Select(outcome => outcome.RequestedAliasUuid)
            );
        var introduced = (
            await fixture.Db.WithWriteConnection(c =>
                c.QueryAsync<Guid>(
                    """
                    SELECT introduced_work_uuid FROM normalization.person_alias_contributor_support
                    WHERE uuid=ANY(@supports)
                    """,
                    new
                    {
                        supports = added
                            .Select(outcome => outcome.SupportUuid!.Value)
                            .Distinct()
                            .ToArray(),
                    }
                )
            )
        ).ToArray();
        introduced
            .Should()
            .NotBeEmpty()
            .And.OnlyContain(work => work == later.WorkUuid && work != originalWork);
        (await ActiveAssignments(source.Source.SourceUuid)).Should().Be(1);
        var expectedSupports = initialNames
            .Concat(later.Receipt.Names)
            .Where(outcome => outcome.SupportUuid.HasValue)
            .Select(outcome => outcome.SupportUuid!.Value)
            .Distinct()
            .LongCount();

        var snapshot = await ReadAliasSupportCountFacts(name);

        snapshot.Complete.Should().Be(!corruptLaterCertificate);
        snapshot
            .Facts.Single(value => value.Hit.Uuid == person)
            .AliasSupportCount.Should()
            .Be(expectedSupports);
        snapshot
            .Issues.Any(issue =>
                issue.CandidateUuid == person
                && issue.Kind == CandidateFactsIssueKind.InvalidStoredFact
                && issue.FactKind == "person_alias_support_proof"
            )
            .Should()
            .Be(corruptLaterCertificate);
        await AssertAliasSupportCountOwners(snapshot, person, source);
    }
}
