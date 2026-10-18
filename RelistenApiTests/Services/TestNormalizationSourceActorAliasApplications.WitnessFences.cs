using Dapper;
using FluentAssertions;
using Relisten.Api.Models.Normalization;

namespace RelistenApiTests.Services;

public sealed partial class TestNormalizationSourceActorAliasApplications
{
    [Test]
    public async Task ACarriedSourceOwnerOmittedFromBoundedWitnessesStillStalesPublication()
    {
        await SelectPolicy(Policy());
        var name = Unique("Carried witness Robin");
        var alias = Unique("Carried witness Finch");
        var person = await Person(name);
        var sources = new List<SourceContext>();
        for (var index = 0; index < 3; index++)
        {
            var source = await Source(Personnel(name, alias));
            sources.Add(source);
            (await Apply(source))
                .Result!.Disposition.Should()
                .Be(SourceActorAliasApplicationDisposition.Complete);
        }
        var bounded = await candidates.FindPeopleFactsAsync(
            new(IdentityMatchingDomain.People, [name], []),
            new() { MaximumAssertionsPerKind = 1 }
        );
        bounded
            .Complete.Should()
            .BeTrue("bounded raw examples do not omit complete alias matching membership");
        var facts = bounded.Facts.Single(value => value.Hit.Uuid == person);
        facts.AliasEvidenceTruncated.Should().BeTrue();
        var witnessInputs = facts
            .Aliases.SelectMany(value => value.Provenance.Evidence)
            .Select(value => value.InputRevisionUuid)
            .ToHashSet();
        var omitted = sources
            .Take(2)
            .First(source =>
                !source.Extraction.Ticket.Inputs.Any(input =>
                    witnessInputs.Contains(input.RevisionUuid)
                )
            );
        var omittedOwner = (
            await fixture.Work.GetSubjectAsync(
                Relisten.Data.SourceActorResolutionService.Owner(omitted.Source.SourceUuid)
            )
        )!;
        var next = await Source(Personnel(name, Unique("New witness Finch")));
        var saved = await PrepareAdmittedSourceStage(next, person);
        var stage = saved.Stages.Single();
        stage
            .Facts.CurrentReadSet.OwnerObservations.Should()
            .Contain(value => value.SubjectUuid == omittedOwner.SubjectUuid);
        var before = await AcceptedAuthority();
        var personBefore = (await fixture.Work.GetSubjectAsync(stage.OwnerSubjectUuid))!;
        var poolBefore = await fixture.Db.WithWriteConnection(c =>
            c.ExecuteScalarAsync<long>(
                "SELECT generation FROM normalization.candidate_universe_revisions WHERE domain='people' AND scope_uuid=@empty",
                new { empty = Guid.Empty }
            )
        );

        await Review(omitted, SourceActorReviewAction.Reject);

        var personAfter = (await fixture.Work.GetSubjectAsync(stage.OwnerSubjectUuid))!;
        personAfter
            .DesiredGeneration.Should()
            .Be(
                personBefore.DesiredGeneration,
                "the carried source is an observed owner, not a new causal interpretation edge"
            );
        var poolAfter = await fixture.Db.WithWriteConnection(c =>
            c.ExecuteScalarAsync<long>(
                "SELECT generation FROM normalization.candidate_universe_revisions WHERE domain='people' AND scope_uuid=@empty",
                new { empty = Guid.Empty }
            )
        );
        poolAfter
            .Should()
            .Be(poolBefore, "source review alone does not publish accepted aliases or support");
        var stale = await people.PublishSourceActorAliasStageAsync(
            saved.AttemptUuid,
            stage.StageUuid
        );
        stale.Status.Should().Be(NormalizationPublicationStatus.Stale);
        (await AcceptedAuthority())
            .Should()
            .Be(before, "the source observation fence must reject before the accepted callback");
        var resumed = await NewPublisher()
            .ApplyAsync(saved.AttemptUuid, next.Resolution.AttemptUuid);
        resumed.Result!.Disposition.Should().Be(SourceActorAliasApplicationDisposition.Stale);
        (await CurrentAliases(person)).Should().Contain(value => value.Wording == alias);
    }
}
