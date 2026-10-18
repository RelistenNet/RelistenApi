using System.Text;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization.Batch;

namespace RelistenApiTests.Normalization;

/// <summary>Exact source references retain attachment uncertainty without inventing actor ownership.</summary>
[TestFixture]
public sealed class TestPeopleSourceReferenceProjection
{
    private const string EvidenceId = "synthetic-source-reference";
    private static readonly Guid ArtistUuid = new("1960934c-8b68-4ced-b7ad-99a68304411c");

    [Test]
    public void AttachedContactReturnsItsCreditedActorAndNonuniqueHint()
    {
        const string raw = "Alice Vale (alice@example.test)";

        var result = Source("taper", raw)
            .ProjectReference(
                EvidenceId,
                ArtistUuid,
                Selector(PeopleSourceReferenceKind.Contact, "taper", raw, "alice@example.test")
            );

        result.Status.Should().Be(PeopleSourceReferenceStatus.Matched);
        var candidate = result.Candidates.Should().ContainSingle().Which;
        candidate.Name.Should().Be("Alice Vale");
        candidate.Attachment.Should().Be(PeopleSourceReferenceAttachment.Contact);
        candidate.ReferenceUuid.Should().Be(candidate.Facts.ReferenceUuid);
        candidate
            .Facts.Contacts.Should()
            .ContainSingle()
            .Which.LookupKey.Should()
            .Be("alice@example.test");
        candidate
            .Projection.Record.ActorContext!.Contacts.Should()
            .Equal(new PeopleContactLookup("email", "alice@example.test"));
        candidate.Projection.Record.Kind.Should().BeNull();
        candidate.Projection.Record.Uuid.Should().BeNull();
        candidate.Projection.Record.ExternalIdentities.Should().BeEmpty();
    }

    [Test]
    public void AttachedProfileRetainsItsQualifiedLookupWithoutCanonicalIdentity()
    {
        const string url = "https://archive.org/details/@night_owl";
        var raw = $"Alice Vale ({url})";

        var result = Source("taper", raw)
            .ProjectReference(
                EvidenceId,
                ArtistUuid,
                Selector(PeopleSourceReferenceKind.Profile, "taper", raw, url)
            );

        result.Status.Should().Be(PeopleSourceReferenceStatus.Matched);
        var candidate = result.Candidates.Should().ContainSingle().Which;
        candidate.Attachment.Should().Be(PeopleSourceReferenceAttachment.Profile);
        candidate.Name.Should().Be("Alice Vale");
        var profile = candidate
            .Projection.Record.ActorContext!.Profiles.Should()
            .ContainSingle()
            .Which;
        profile.Provider.Should().Be("archive.org");
        profile.Namespace.Should().Be("account");
        profile.LookupKey.Should().Be("night_owl");
        candidate.Facts.Profiles.Should().ContainSingle();
        candidate.Projection.Record.ExternalIdentities.Should().BeEmpty();
        candidate.Projection.Record.Kind.Should().BeNull();
    }

    [Test]
    public void ExplicitAttachmentWithUnknownProfileNamespaceDoesNotInventASupportedLookup()
    {
        const string url = "https://example.test/profiles/alice";
        var raw = $"Alice Vale ({url})";

        var result = Source("taper", raw)
            .ProjectReference(
                EvidenceId,
                ArtistUuid,
                Selector(PeopleSourceReferenceKind.Profile, "taper", raw, url)
            );

        result.Status.Should().Be(PeopleSourceReferenceStatus.Matched);
        var candidate = result.Candidates.Should().ContainSingle().Which;
        candidate.Name.Should().Be("Alice Vale");
        candidate.Attachment.Should().Be(PeopleSourceReferenceAttachment.UnknownProfileNamespace);
        candidate.Facts.Profiles.Should().BeEmpty();
        candidate.Projection.Record.ActorContext!.Profiles.Should().BeEmpty();
        candidate.Projection.Record.ExternalIdentities.Should().BeEmpty();
        candidate
            .Facts.Unresolved.Should()
            .Contain(issue => issue.Reason == "unknown_profile_namespace");
    }

    [TestCase(
        PeopleSourceReferenceKind.Contact,
        "contact: shared@example.test",
        "shared@example.test"
    )]
    [TestCase(
        PeopleSourceReferenceKind.Profile,
        "profile: https://archive.org/details/@shared",
        "https://archive.org/details/@shared"
    )]
    public void SharedTrailingReferenceRetainsBothHypothesesWithoutAssigningTheHint(
        PeopleSourceReferenceKind kind,
        string suffix,
        string literal
    )
    {
        var raw = $"Recorded by Alice Vale and Bob Reed; {suffix}";

        var result = Source("description", raw)
            .ProjectReference(EvidenceId, ArtistUuid, Selector(kind, "description", raw, literal));

        result.Status.Should().Be(PeopleSourceReferenceStatus.Ambiguous);
        result
            .Candidates.Select(candidate => candidate.Name)
            .Should()
            .BeEquivalentTo(new[] { "Alice Vale", "Bob Reed" });
        result
            .Candidates.Select(candidate => candidate.ReferenceUuid)
            .Distinct()
            .Should()
            .HaveCount(2);
        foreach (var candidate in result.Candidates)
        {
            candidate.Attachment.Should().Be(PeopleSourceReferenceAttachment.Unattributed);
            candidate.Facts.Contacts.Should().BeEmpty();
            candidate.Facts.Profiles.Should().BeEmpty();
            candidate.Projection.Record.ActorContext!.Contacts.Should().BeEmpty();
            candidate.Projection.Record.ActorContext.Profiles.Should().BeEmpty();
            candidate.Projection.Record.ActorContext.HasAttachmentUncertainty.Should().BeTrue();
        }
    }

    [TestCase(
        PeopleSourceReferenceKind.Contact,
        "Alice Vale contact: alice@example.test",
        "alice@example.test"
    )]
    [TestCase(
        PeopleSourceReferenceKind.Profile,
        "Alice Vale https://github.com/bob",
        "https://github.com/bob"
    )]
    public void OneNearbyHypothesisStillDoesNotAssertReferenceOwnership(
        PeopleSourceReferenceKind kind,
        string raw,
        string literal
    )
    {
        var result = Source("taper", raw)
            .ProjectReference(EvidenceId, ArtistUuid, Selector(kind, "taper", raw, literal));

        result.Status.Should().Be(PeopleSourceReferenceStatus.Unattributed);
        var candidate = result.Candidates.Should().ContainSingle().Which;
        candidate.Attachment.Should().Be(PeopleSourceReferenceAttachment.Unattributed);
        candidate.Name.Should().Be("Alice Vale");
        candidate.Facts.Contacts.Should().BeEmpty();
        candidate.Facts.Profiles.Should().BeEmpty();
        candidate.Projection.Record.ActorContext!.Contacts.Should().BeEmpty();
        candidate.Projection.Record.ActorContext.Profiles.Should().BeEmpty();
    }

    [Test]
    public void HtmlHrefSelectorUsesOriginalEntitySpellingAndUnicodeCoordinates()
    {
        const string literal = "mailto:river&#64;example.test";
        const string raw =
            "🎵<div>Recorded by <a href=\"mailto:river&#64;example.test\"><b>Renée River</b></a></div>";
        var selector = Selector(PeopleSourceReferenceKind.Contact, "description", raw, literal);
        var projector = Source("description", raw);

        var exact = projector.ProjectReference(EvidenceId, ArtistUuid, selector);
        var wrongOffset = projector.ProjectReference(
            EvidenceId,
            ArtistUuid,
            selector with
            {
                Evidence = selector.Evidence with
                {
                    StartCodePoint = selector.Evidence.StartCodePoint + 1,
                    EndCodePoint = selector.Evidence.EndCodePoint + 1,
                },
            }
        );

        exact.Status.Should().Be(PeopleSourceReferenceStatus.Matched);
        var candidate = exact.Candidates.Should().ContainSingle().Which;
        candidate.Name.Should().Be("Renée River");
        candidate.Facts.Contacts.Should().ContainSingle().Which.Literal.Should().Be(literal);
        candidate
            .Projection.Record.ActorContext!.Contacts.Should()
            .Equal(new PeopleContactLookup("email", "river@example.test"));
        wrongOffset.Status.Should().Be(PeopleSourceReferenceStatus.NotFound);
        wrongOffset.Candidates.Should().BeEmpty();
    }

    [TestCase("evidence")]
    [TestCase("pointer")]
    [TestCase("quote")]
    [TestCase("span")]
    public void ChangedExactSourceCoordinatesNeverFallBackToAReferenceValueMatch(string changed)
    {
        const string raw = "Alice Vale (alice@example.test)";
        var selector = Selector(
            PeopleSourceReferenceKind.Contact,
            "taper",
            raw,
            "alice@example.test"
        );
        var evidenceId = changed == "evidence" ? "another-source" : EvidenceId;
        selector = selector with
        {
            Evidence = changed switch
            {
                "pointer" => selector.Evidence with { JsonPointer = "/description" },
                "quote" => selector.Evidence with { Quote = "bob@example.test" },
                "span" => selector.Evidence with
                {
                    StartCodePoint = selector.Evidence.StartCodePoint + 1,
                },
                _ => selector.Evidence,
            },
        };

        var result = Source("taper", raw).ProjectReference(evidenceId, ArtistUuid, selector);

        result.Status.Should().Be(PeopleSourceReferenceStatus.NotFound);
        result.Candidates.Should().BeEmpty();
    }

    [Test]
    public void RepeatedContactValueSelectsItsExactAttachmentRatherThanAllSameValuedActors()
    {
        const string literal = "shared@example.test";
        const string raw =
            "Recorded by Alice Vale (shared@example.test) and Bob Reed (shared@example.test)";
        var projector = Source("description", raw);

        var alice = projector.ProjectReference(
            EvidenceId,
            ArtistUuid,
            Selector(PeopleSourceReferenceKind.Contact, "description", raw, literal)
        );
        var bob = projector.ProjectReference(
            EvidenceId,
            ArtistUuid,
            Selector(
                PeopleSourceReferenceKind.Contact,
                "description",
                raw,
                literal,
                raw.IndexOf("Bob Reed", StringComparison.Ordinal)
            )
        );

        alice.Status.Should().Be(PeopleSourceReferenceStatus.Matched);
        bob.Status.Should().Be(PeopleSourceReferenceStatus.Matched);
        var aliceCandidate = alice.Candidates.Should().ContainSingle().Which;
        var bobCandidate = bob.Candidates.Should().ContainSingle().Which;
        aliceCandidate.Name.Should().Be("Alice Vale");
        bobCandidate.Name.Should().Be("Bob Reed");
        (aliceCandidate.ReferenceUuid != bobCandidate.ReferenceUuid).Should().BeTrue();
    }

    [Test]
    public void JointRolesReturnOneSourceActorRatherThanDuplicateHypotheses()
    {
        const string raw = "Recorded and transferred by Alice Vale (alice@example.test)";

        var result = Source("description", raw)
            .ProjectReference(
                EvidenceId,
                ArtistUuid,
                Selector(
                    PeopleSourceReferenceKind.Contact,
                    "description",
                    raw,
                    "alice@example.test"
                )
            );

        result.Status.Should().Be(PeopleSourceReferenceStatus.Matched);
        result.Candidates.Should().ContainSingle().Which.Name.Should().Be("Alice Vale");
    }

    [TestCase(
        PeopleSourceReferenceKind.Profile,
        "Alice Vale (alice@example.test)",
        "alice@example.test"
    )]
    [TestCase(
        PeopleSourceReferenceKind.Contact,
        "Alice Vale (https://github.com/alice)",
        "https://github.com/alice"
    )]
    public void SelectorKindCannotReinterpretAContactAsAProfileOrAProfileAsAContact(
        PeopleSourceReferenceKind kind,
        string raw,
        string literal
    )
    {
        var result = Source("taper", raw)
            .ProjectReference(EvidenceId, ArtistUuid, Selector(kind, "taper", raw, literal));

        result.Status.Should().Be(PeopleSourceReferenceStatus.NotFound);
        result.Candidates.Should().BeEmpty();
    }

    [Test]
    public void BareProseDoesNotInventACreditedActorToSatisfyTheSelector()
    {
        const string raw = "Alice Vale alice@example.test";

        var result = Source("description", raw)
            .ProjectReference(
                EvidenceId,
                ArtistUuid,
                Selector(
                    PeopleSourceReferenceKind.Contact,
                    "description",
                    raw,
                    "alice@example.test"
                )
            );

        result.Status.Should().Be(PeopleSourceReferenceStatus.NotFound);
        result.Candidates.Should().BeEmpty();
    }

    private static PeopleSourceContextProjection Source(string field, string raw) =>
        new(
            new Dictionary<string, IReadOnlyDictionary<string, string?>>
            {
                [EvidenceId] = new Dictionary<string, string?> { [field] = raw },
            }
        );

    private static PeopleSourceReferenceSelector Selector(
        PeopleSourceReferenceKind kind,
        string field,
        string raw,
        string quote,
        int searchStart = 0
    )
    {
        var start = raw.IndexOf(quote, searchStart, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, "the synthetic selector must cite original text");
        return new(
            kind,
            new(
                field,
                "/" + field.Replace("~", "~0").Replace("/", "~1"),
                quote,
                raw[..start].EnumerateRunes().Count(),
                raw[..(start + quote.Length)].EnumerateRunes().Count()
            )
        );
    }
}
