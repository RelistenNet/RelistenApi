using System.Text.Json;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace RelistenApiTests.Normalization;

[TestFixture]
public sealed class TestSourceActorAliasProjection
{
    [Test]
    public void ConditionalSourceAliasProjectsNamesWithoutCreditKindOrAttendanceAuthority()
    {
        const string raw =
            "Band line-up:\n-- and some or all of the following --\n"
            + "Jon Cunningham (aka Corn Mo) - lead, backing and choir vocals";
        var assertion = Extract(raw);
        var artist = Guid.NewGuid();

        var projected = PeopleActorProjection.Project(assertion, artist);

        projected.Complete.Should().BeTrue();
        projected.Query!.Names.Should().Equal("Jon Cunningham", "Corn Mo");
        projected.Query.PeopleHints.Should().BeNull();
        projected.Query.ExternalIdentities.Should().BeEmpty();
        projected.Record.Uuid.Should().BeNull();
        projected.Record.Kind.Should().BeNull();
        projected.Record.ActorContext.Should().BeNull();
        projected.Record.ExternalIdentities.Should().BeEmpty();
        projected.Record.ArtistUuids.Should().BeEquivalentTo(new[] { artist });
        assertion.Context.Qualifiers.Should().ContainSingle();
        assertion.Relationships.Should().OnlyContain(relationship => relationship.Roles.Count == 0);
    }

    [Test]
    public void PureAndRetainedAssertionsProduceIdenticalComparisonInputs()
    {
        const string raw = "The Band:\nZoë aka <b>Lark</b> - vocals";
        var input = new NormalizationInputRevision
        {
            Uuid = Guid.NewGuid(),
            Resource = new("archive.org", "item_metadata", "source-actor-projection"),
            Payload = JsonSerializer.SerializeToUtf8Bytes(new { description = raw }),
            MediaType = "application/json",
            CaptureVersion = "archive-item-response-v1",
        };
        var retained = new RetainedSourceActorAliasExtractor().Extract(
            input,
            [new("description", new(raw, new(input.Uuid, "/description")))]
        );
        var artist = Guid.NewGuid();

        var pureProjection = PeopleActorProjection.Project(Extract(raw), artist);
        var retainedProjection = PeopleActorProjection.Project(
            retained.Assertions.Single(),
            artist
        );

        JsonElement
            .DeepEquals(
                JsonSerializer.SerializeToElement(pureProjection),
                JsonSerializer.SerializeToElement(retainedProjection)
            )
            .Should()
            .BeTrue();
        PeopleActorProjection
            .QueryFingerprint(pureProjection.Query!)
            .Should()
            .Be(PeopleActorProjection.QueryFingerprint(retainedProjection.Query!));
    }

    [TestCase(true)]
    [TestCase(false)]
    public void DroppedOrUnqueryableExplicitNamesMakeTheProjectionIncomplete(bool excessiveCount)
    {
        var original = Extract("Personnel:\nAlice Vale aka Lark - vocals");
        var alias = original.Aliases.Single();
        var assertion = original with
        {
            Aliases = excessiveCount
                ? Enumerable
                    .Range(0, 17)
                    .Select(index => alias with { Wording = "Alias " + index })
                    .ToArray()
                : [alias with { Wording = new string('a', 201) }],
        };

        var projected = PeopleActorProjection.Project(assertion, Guid.NewGuid());

        projected.Complete.Should().BeFalse();
        projected.Query!.Names.Count.Should().BeLessThanOrEqualTo(16);
        projected.Record.Kind.Should().BeNull();
        projected.Record.ActorContext.Should().BeNull();
    }

    [Test]
    public void UnresolvedAttachmentCannotBecomeACompleteComparisonInput()
    {
        var assertion = Extract("Personnel:\nAlice Vale aka Lark - vocals");
        assertion = assertion with
        {
            Unresolved = [new("ambiguous_alias_boundary", [], [assertion.Context.Statement])],
        };

        var projected = PeopleActorProjection.Project(assertion, Guid.NewGuid());

        projected.Query!.Names.Should().Equal("Alice Vale", "Lark");
        projected.Complete.Should().BeFalse();
    }

    private static SourceActorAliasAssertion<NormalizationTextEvidence> Extract(string raw) =>
        new SourceActorAliasExtractor()
            .Extract(new Dictionary<string, string?> { ["description"] = raw })
            .Assertions.Single();
}
