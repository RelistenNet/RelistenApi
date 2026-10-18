using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Data;

namespace RelistenApiTests.Services;

/// <summary>Counts proposed stored ownership only; generated keys supply no source or identity authority.</summary>
[TestFixture]
public sealed class TestSourceActorResolutionCapacity
{
    [Test]
    public void BindingBoundaryDeduplicatesKeysWithinAContributor()
    {
        var resources = Resources(99_998);
        var input = Contribution("first", [.. resources, .. resources.Take(3)]) with
        {
            MappingRegistry = new(0),
            CandidateUniverses = Enumerable
                .Repeat(
                    new NormalizationCandidateUniverseDependency(
                        NormalizationCandidateUniverseKey.People,
                        0
                    ),
                    3
                )
                .ToArray(),
        };
        var preparation = Preparation();

        SourceActorResolutionReadSet.RequireCapacity(preparation, [input]);

        var observation = Guid.NewGuid();
        var over = input with
        {
            Contribution = input.Contribution with
            {
                OwnerObservationSubjectUuids = [observation, observation],
            },
        };
        var check = () => SourceActorResolutionReadSet.RequireCapacity(preparation, [over]);
        check
            .Should()
            .Throw<SourceActorResolutionCapacityException>()
            .Which.Reason.Should()
            .Be("contribution_capacity_exceeded");
    }

    [Test]
    public void SharedKeysStillConsumeOneBindingForEachContributorThatOwnsThem()
    {
        var resources = Resources(50_000);
        var first = Contribution("first", resources);
        var second = Contribution("second", resources);
        var preparation = Preparation();

        SourceActorResolutionReadSet.RequireCapacity(preparation, [first, second]);

        var over = first with { MappingRegistry = new(0) };
        var check = () => SourceActorResolutionReadSet.RequireCapacity(preparation, [over, second]);
        check
            .Should()
            .Throw<SourceActorResolutionCapacityException>()
            .Which.Reason.Should()
            .Be("contribution_capacity_exceeded");
    }

    private static NormalizationResourceKey[] Resources(int count) =>
        Enumerable
            .Range(0, count)
            .Select(index => new NormalizationResourceKey(
                "fixture",
                "capacity",
                index.ToString("D6")
            ))
            .ToArray();

    private static NormalizationPreparedInputContribution Contribution(
        string contributor,
        IReadOnlyList<NormalizationResourceKey> resources
    ) => new(new(new("fixture", "capacity_owner", contributor), 0, resources, []), []);

    private static SourceActorResolutionPreparation Preparation()
    {
        var extraction = new NormalizationSubjectObservation(Guid.NewGuid(), 0, 0, Guid.NewGuid());
        var policy = new NormalizationSubjectObservation(Guid.NewGuid(), 0, 0, null);
        return new(
            extraction,
            new(
                extraction.PublishedWorkUuid!.Value,
                extraction.SubjectUuid,
                0,
                0,
                "capacity-fixture",
                null,
                [],
                []
            ),
            new(null, policy),
            new(NormalizationCandidateUniverseKey.People, 0),
            new(null, []),
            null,
            [],
            [],
            []
        );
    }
}
