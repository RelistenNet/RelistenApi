using System.Text;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization.Batch;

namespace RelistenApiTests.Normalization;

/// <summary>Source context joins exact cited tokens; labels and co-mentions supply no actor facts.</summary>
[TestFixture]
public sealed class TestPeopleSourceContextProjection
{
    private const string EvidenceId = "synthetic-recording";
    private static readonly Guid ArtistUuid = new("c525678e-f24d-4fd0-99c7-6bc11ecf69ee");

    [TestCase("aka")]
    [TestCase("a.k.a.")]
    [TestCase("also known as")]
    public void ExplicitAliasAndBaseAnchorSelectTheSameSourceActor(string marker)
    {
        var raw = $"Recorded by Alice Vale {marker} Night Owl";
        var projector = Source("description", raw);

        var actor = Project(projector, "Alice Vale", Anchor("description", raw, "Alice Vale"));
        var alias = Project(projector, "Night Owl", Anchor("description", raw, "Night Owl"));

        actor.Status.Should().Be("matched");
        actor.MatchedVia.Should().Be("actor_anchor");
        actor.ReferenceUuid.Should().NotBeNull();
        alias.Status.Should().Be("matched");
        alias.MatchedVia.Should().Be("explicit_alias");
        alias.ReferenceUuid.Should().Be(actor.ReferenceUuid);
        actor
            .Projection.Record.Names.Select(name => name.Wording)
            .Should()
            .Equal("Alice Vale", "Night Owl");
        alias
            .Projection.Record.Names.Select(name => name.Wording)
            .Should()
            .BeEquivalentTo(new[] { "Alice Vale", "Night Owl" });
        actor.Projection.Record.Uuid.Should().BeNull();
        actor.Projection.Record.Kind.Should().BeNull();
        actor.Projection.Record.ExternalIdentities.Should().BeEmpty();
    }

    [Test]
    public void CoordinatedActorsNeverAcquireEachOthersNamesOrAttachedContacts()
    {
        const string raw =
            "Recorded by Alice Vale (alice@example.test) and Bob Reed (bob@example.test)";
        var projector = Source("description", raw);

        var alice = Project(projector, "Alice Vale", Anchor("description", raw, "Alice Vale"));
        var bob = Project(projector, "Bob Reed", Anchor("description", raw, "Bob Reed"));

        alice.Status.Should().Be("matched");
        bob.Status.Should().Be("matched");
        (alice.ReferenceUuid != bob.ReferenceUuid).Should().BeTrue();
        alice.Projection.Record.Names.Select(name => name.Wording).Should().Equal("Alice Vale");
        bob.Projection.Record.Names.Select(name => name.Wording).Should().Equal("Bob Reed");
        alice
            .Projection.Record.ActorContext!.Contacts.Should()
            .Equal(new PeopleContactLookup("email", "alice@example.test"));
        bob.Projection.Record.ActorContext!.Contacts.Should()
            .Equal(new PeopleContactLookup("email", "bob@example.test"));
    }

    [Test]
    public void HtmlEntitiesAndEmojiUseOriginalFieldCodePointsRatherThanDecodedOrUtf16Offsets()
    {
        const string raw =
            "🎵<div>Recorded by <b>Renée&nbsp;River</b> <river&#64;example.test></div>";
        var projector = Source("description", raw);
        var anchor = Anchor("description", raw, "Renée&nbsp;River");

        var exact = Project(projector, "Renée River", anchor);
        var utf16 = Project(
            projector,
            "Renée River",
            anchor with
            {
                StartCodePoint = anchor.StartCodePoint + 1,
                EndCodePoint = anchor.EndCodePoint + 1,
            }
        );

        exact.Status.Should().Be("matched");
        exact.Projection.Record.Names.Select(name => name.Wording).Should().Equal("Renée River");
        exact
            .Projection.Record.ActorContext!.Contacts.Should()
            .Equal(new PeopleContactLookup("email", "river@example.test"));
        utf16.Status.Should().Be("name_only");
        utf16.ReferenceUuid.Should().BeNull();
        utf16.Projection.Record.ActorContext.Should().BeNull();
    }

    [Test]
    public void AttachedProfileAndContactRemainHintsWithoutCanonicalIdentityOrInferredKind()
    {
        const string raw =
            "Alice Vale (alice@example.test) (https://archive.org/details/@night_owl)";

        var result = Project(
            Source("taper", raw),
            "Alice Vale",
            Anchor("taper", raw, "Alice Vale")
        );

        result.Status.Should().Be("matched");
        result.Projection.Complete.Should().BeTrue();
        result.Projection.Record.Kind.Should().BeNull();
        result.Projection.Record.Uuid.Should().BeNull();
        result.Projection.Record.ExternalIdentities.Should().BeEmpty();
        result
            .Projection.Record.ActorContext!.Contacts.Should()
            .Equal(new PeopleContactLookup("email", "alice@example.test"));
        result
            .Projection.Record.ActorContext.Profiles.Should()
            .ContainSingle()
            .Which.LookupKey.Should()
            .Be("night_owl");
    }

    [Test]
    public void SharedTrailingHintsStayUnattributedForBothActors()
    {
        const string raw =
            "Recorded by Alice Vale and Bob Reed; contact: shared@example.test; profile: https://archive.org/details/@shared";
        var projector = Source("description", raw);

        foreach (var name in new[] { "Alice Vale", "Bob Reed" })
        {
            var result = Project(projector, name, Anchor("description", raw, name));

            result.Status.Should().Be("matched");
            result.Projection.Record.ActorContext!.Contacts.Should().BeEmpty();
            result.Projection.Record.ActorContext.Profiles.Should().BeEmpty();
            result.Projection.Record.ActorContext.HasAttachmentUncertainty.Should().BeTrue();
            result
                .Issues.Should()
                .Contain("unattributed_contact")
                .And.Contain("unattributed_profile");
        }
    }

    [Test]
    public void SameSpellingElsewhereInTheFieldDoesNotSelectTheFirstCreditedActor()
    {
        const string raw =
            "Alice Vale was in attendance. Recorded by Alice Vale (alice@example.test)";
        var projector = Source("description", raw);

        var uncredited = Project(projector, "Alice Vale", Anchor("description", raw, "Alice Vale"));
        var credited = Project(
            projector,
            "Alice Vale",
            Anchor(
                "description",
                raw,
                "Alice Vale",
                raw.IndexOf("Recorded", StringComparison.Ordinal)
            )
        );

        uncredited.Status.Should().Be("name_only");
        uncredited.Issues.Should().Contain("source_actor_not_found");
        uncredited.ReferenceUuid.Should().BeNull();
        uncredited.Projection.Record.ActorContext.Should().BeNull();
        uncredited
            .Projection.Record.Names.Select(name => name.Wording)
            .Should()
            .Equal("Alice Vale");
        credited.Status.Should().Be("matched");
        credited.Projection.Record.ActorContext!.Contacts.Should().ContainSingle();
    }

    [TestCase("evidence")]
    [TestCase("pointer")]
    [TestCase("quote")]
    [TestCase("name")]
    public void AChangedEvidenceJoinCannotBorrowFactsFromAConvenientNameMatch(string changed)
    {
        const string raw = "Alice Vale (alice@example.test)";
        var anchor = Anchor("taper", raw, "Alice Vale");
        var evidence = changed == "evidence" ? "different-recording" : EvidenceId;
        var name = changed == "name" ? "Bob Reed" : "Alice Vale";
        anchor = changed switch
        {
            "pointer" => anchor with { JsonPointer = "/description" },
            "quote" => anchor with { Quote = "Bob Reed" },
            _ => anchor,
        };

        var result = Source("taper", raw).Project(evidence, name, "name", ArtistUuid, anchor);

        result.Status.Should().Be("name_only");
        result.MatchedVia.Should().BeNull();
        result.ReferenceUuid.Should().BeNull();
        result.Projection.Record.ActorContext.Should().BeNull();
        result.Projection.Record.Names.Select(value => value.Wording).Should().Equal(name);
    }

    [TestCase("credit_expression")]
    [TestCase("unresolved_reference")]
    public void UnsupportedMentionExpressionsRemainVisibleWithoutSourceActorContext(string kind)
    {
        const string raw = "Alice Vale (alice@example.test)";

        var result = Source("taper", raw)
            .Project(
                EvidenceId,
                "Alice Vale",
                kind,
                ArtistUuid,
                Anchor("taper", raw, "Alice Vale")
            );

        result.Status.Should().Be("unsupported_mention_expression");
        result.MatchedVia.Should().BeNull();
        result.ReferenceUuid.Should().BeNull();
        result.Projection.Record.ActorContext.Should().BeNull();
        result.Projection.Record.Names.Select(name => name.Wording).Should().Equal("Alice Vale");
    }

    [Test]
    public void AmbiguousUnboundedAliasRetainsUncertaintyWithoutJoiningASmallerAliasToken()
    {
        const string raw = "Alice Vale aka Night & Day";
        var projector = Source("taper", raw);

        var actor = Project(projector, "Alice Vale", Anchor("taper", raw, "Alice Vale"));
        var alias = Project(projector, "Night", Anchor("taper", raw, "Night"));

        actor.Status.Should().Be("matched");
        actor.Issues.Should().Contain("ambiguous_alias_boundary");
        actor.Projection.Record.Names.Select(name => name.Wording).Should().Equal("Alice Vale");
        actor.Projection.Record.ActorContext!.HasAttachmentUncertainty.Should().BeTrue();
        alias.Status.Should().Be("name_only");
        alias.ReferenceUuid.Should().BeNull();
        alias.Projection.Record.ActorContext.Should().BeNull();
    }

    [Test]
    public void QuotedParentheticalAliasDoesNotBorrowContextForAnInnerOnlyTokenCitation()
    {
        const string raw = "Alice Vale (aka \"Night Owl\")";
        var projector = Source("taper", raw);

        var actor = Project(projector, "Alice Vale", Anchor("taper", raw, "Alice Vale"));
        var innerToken = Project(projector, "Night Owl", Anchor("taper", raw, "Night Owl"));

        actor.Status.Should().Be("matched");
        actor
            .Projection.Record.Names.Select(name => name.Wording)
            .Should()
            .Equal("Alice Vale", "Night Owl");
        innerToken.Status.Should().Be("name_only");
        innerToken.Issues.Should().Contain("source_actor_not_found");
        innerToken.MatchedVia.Should().BeNull();
        innerToken.ReferenceUuid.Should().BeNull();
        innerToken.Projection.Record.ActorContext.Should().BeNull();
    }

    [Test]
    public void OneJointRoleActorDoesNotBecomeAnAmbiguousJoin()
    {
        const string raw = "Recorded and transferred by Alice Vale aka Night Owl";

        var result = Project(
            Source("description", raw),
            "Alice Vale",
            Anchor("description", raw, "Alice Vale")
        );

        result.Status.Should().Be("matched");
        result.ReferenceUuid.Should().NotBeNull();
        result.Issues.Should().NotContain("ambiguous_source_actor");
        result
            .Projection.Record.Names.Select(name => name.Wording)
            .Should()
            .Equal("Alice Vale", "Night Owl");
    }

    private static PeopleSourceContextProjection Source(string field, string raw) =>
        new(
            new Dictionary<string, IReadOnlyDictionary<string, string?>>
            {
                [EvidenceId] = new Dictionary<string, string?> { [field] = raw },
            }
        );

    private static PeopleSourceContextResult Project(
        PeopleSourceContextProjection projector,
        string name,
        NormalizationTextEvidence anchor
    ) => projector.Project(EvidenceId, name, "name", ArtistUuid, anchor);

    private static NormalizationTextEvidence Anchor(
        string field,
        string raw,
        string quote,
        int searchStart = 0
    )
    {
        var start = raw.IndexOf(quote, searchStart, StringComparison.Ordinal);
        start
            .Should()
            .BeGreaterThanOrEqualTo(0, "the synthetic citation must exist in the original field");
        return new(
            field,
            "/" + field.Replace("~", "~0").Replace("/", "~1"),
            quote,
            raw[..start].EnumerateRunes().Count(),
            raw[..(start + quote.Length)].EnumerateRunes().Count()
        );
    }
}
