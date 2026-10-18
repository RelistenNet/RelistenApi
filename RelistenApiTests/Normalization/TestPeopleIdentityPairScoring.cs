using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace RelistenApiTests.Normalization;

[TestFixture]
public sealed class TestPeopleIdentityPairScoring
{
    [Test]
    public void SourceWitnessesCanBeScoredWithoutCanonicalPersonUuids()
    {
        var artist = Guid.NewGuid();
        var left = new PersonMatchRecord(
            null,
            null,
            [new("Alice Vale")],
            [],
            new HashSet<Guid> { artist },
            new([new("email", "alice@example.test")], [])
        );
        var right = left with { Uuid = null };
        var comparison = new IdentityCandidateMatcher().ComparePeople(left, right);

        var result = PeopleResolutionPolicyEvaluator.ScorePair(
            comparison,
            new(-1, 3, 5, 7, 99, 99, 11, 13)
        );

        comparison.CandidateUuid.Should().BeNull();
        result.Score.Should().Be(25);
        result.Eligible.Should().BeTrue();
        result.RejectionReasons.Should().BeEmpty();
        comparison.Disposition.Should().NotBe(IdentityMatchDisposition.AutomaticMatch);
    }

    [Test]
    public void ContradictoryKindsRemainIneligibleEvenWithHighNameAgreement()
    {
        var left = new PersonMatchRecord(
            null,
            "person",
            [new("Lantern Crew")],
            [],
            new HashSet<Guid>()
        );
        var right = left with { Kind = "group" };
        var comparison = new IdentityCandidateMatcher().ComparePeople(left, right);

        var result = PeopleResolutionPolicyEvaluator.ScorePair(comparison, new(1, 10, 10, 0, 0, 0));

        result.Score.Should().Be(21);
        result.Eligible.Should().BeFalse();
        result.RejectionReasons.Should().Contain("contradictory_facts");
    }
}
