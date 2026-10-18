using System.Text;
using System.Text.Json;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
using Relisten.Normalization;

namespace RelistenApiTests.Normalization;

[TestFixture]
public sealed class TestPeopleActorProjection
{
    [Test]
    public void ExplicitAliasAndAttributedHintsUseSeparateNonAuthoritativeRoutes()
    {
        var mention = Credit(
            "Alice Vale aka Night Owl (alice@example.test) (https://archive.org/details/@night_owl)"
        );

        var projected = PeopleActorProjection.Project(mention, Guid.NewGuid());

        projected.Complete.Should().BeTrue();
        projected.Query!.Names.Should().Equal("Alice Vale", "Night Owl");
        projected.Query.ExternalIdentities.Should().BeEmpty();
        projected
            .Query.PeopleHints!.Contacts.Should()
            .Equal(new PeopleContactLookup("email", "alice@example.test"));
        projected
            .Query.PeopleHints.Profiles.Should()
            .ContainSingle()
            .Which.LookupKey.Should()
            .Be("night_owl");
        projected.Record.ExternalIdentities.Should().BeEmpty();
        projected.Record.Names.Should().OnlyContain(name => !name.IsHandle);
        projected.Record.Kind.Should().BeNull();
    }

    [Test]
    public void QueryCacheDistinguishesContextAndAllRetrievalControls()
    {
        var one = PeopleActorProjection
            .Project(Credit("Alice Vale (one@example.test)"), Guid.NewGuid())
            .Query!;
        var two = PeopleActorProjection
            .Project(Credit("Alice Vale (two@example.test)"), Guid.NewGuid())
            .Query!;

        PeopleActorProjection
            .QueryFingerprint(one)
            .Should()
            .NotBe(PeopleActorProjection.QueryFingerprint(two));
        PeopleActorProjection
            .QueryFingerprint(one)
            .Should()
            .NotBe(PeopleActorProjection.QueryFingerprint(one with { NameLimit = 1 }));
        PeopleActorProjection
            .QueryFingerprint(one)
            .Should()
            .NotBe(
                PeopleActorProjection.QueryFingerprint(
                    one with
                    {
                        PeopleHints = one.PeopleHints! with { Limit = 1 },
                    }
                )
            );
        PeopleActorProjection
            .QueryFingerprint(one)
            .Should()
            .Be(PeopleActorProjection.QueryFingerprint(one with { Names = one.Names.ToArray() }));
    }

    [TestCase(true)]
    [TestCase(false)]
    public void OmittedOrUnsupportedExplicitAliasCannotMakeAnApparentlyCompleteQuery(
        bool excessiveCount
    )
    {
        var mention = Credit("Alice Vale aka Night Owl");
        var alias = mention.ActorFacts!.Aliases.Single();
        var aliases = excessiveCount
            ? Enumerable
                .Range(0, 17)
                .Select(index => alias with { Wording = "Alias " + index })
                .ToArray()
            : [alias with { Wording = new string('a', 201) }];
        mention = mention with { ActorFacts = mention.ActorFacts with { Aliases = aliases } };

        var projected = PeopleActorProjection.Project(mention, Guid.NewGuid());

        projected.Query.Should().NotBeNull();
        projected.Query!.Names.Count.Should().BeLessThanOrEqualTo(16);
        projected.Complete.Should().BeFalse();
    }

    [Test]
    public void OversizedContactHintRemainsOriginalEvidenceButMakesProjectionIncomplete()
    {
        var mention = Credit("Alice Vale (alice@example.test)");
        mention = mention with
        {
            ActorFacts = mention.ActorFacts! with
            {
                Contacts =
                [
                    mention.ActorFacts.Contacts.Single() with
                    {
                        LookupKey = new string('a', 513),
                    },
                ],
            },
        };

        var projected = PeopleActorProjection.Project(mention, Guid.NewGuid());

        projected.Complete.Should().BeFalse();
        projected.Query!.Names.Should().Equal("Alice Vale");
        projected.Query.PeopleHints.Should().BeNull();
        mention.ActorFacts.Contacts.Single().LookupKey.Should().HaveLength(513);
    }

    [Test]
    public void LegacyMentionWithoutActorFactsKeepsNameOnlyAndUnknownKind()
    {
        var mention = Credit("Alice Vale") with { ActorFacts = null };

        var projected = PeopleActorProjection.Project(mention, Guid.NewGuid());

        projected.Query!.Names.Should().Equal("Alice Vale");
        projected.Query.PeopleHints.Should().BeNull();
        projected.Record.ActorContext.Should().BeNull();
        projected.Record.Kind.Should().BeNull();
        JsonSerializer.Serialize(projected.Query).Should().NotContain("PeopleHints");
    }

    [TestCase("key")]
    [TestCase("missing")]
    [TestCase("extra")]
    public void ActorAuthenticationChecksSemanticPayloadAndCompleteness(string change)
    {
        var original = new NormalizationRecordingCreditProposal(
            Guid.NewGuid(),
            Credit("Alice Vale (alice@example.test)")
        );
        var altered = original with
        {
            Mention = original.Mention with
            {
                ActorFacts =
                    change == "missing"
                        ? null
                        : original.Mention.ActorFacts! with
                        {
                            Contacts =
                            [
                                original.Mention.ActorFacts.Contacts.Single() with
                                {
                                    LookupKey = "someone@example.test",
                                },
                            ],
                        },
            },
        };
        IReadOnlyList<NormalizationRecordingCreditProposal> incoming =
            change == "extra" ? [original, altered] : [altered];

        var validate = () => PeopleActorProjection.RequireOriginalActorFacts(incoming, [original]);

        validate.Should().Throw<NormalizationConflictException>();
        PeopleActorProjection.RequireOriginalActorFacts([original], [original]);
    }

    private static RetainedCreditMention Credit(string text)
    {
        var input = new NormalizationInputRevision
        {
            Uuid = Guid.NewGuid(),
            Resource = new("archive.org", "item_metadata", "actor-projection-fixture"),
            Payload = JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    metadata = new { identifier = "actor-projection-fixture", taper = text },
                    files = Array.Empty<object>(),
                }
            ),
            MediaType = "application/json",
            CaptureVersion = "archive-item-response-v1",
        };
        var decoded = new NormalizationProviderDecoder().Decode(input);
        return new RetainedRecordingCreditExtractor()
            .Extract(input, decoded.Recordings.Single().Text)
            .Credits.Single();
    }
}
