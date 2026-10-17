using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace RelistenApiTests.Normalization;

/// <summary>
/// These comparisons exercise domain decisions using realistic names and metadata shapes.
/// They are regression tests, not the independent accuracy evaluation needed to activate a rule.
/// </summary>
[TestFixture]
public class TestIdentityCandidateMatcher
{
    private readonly IdentityCandidateMatcher matcher = new();

    [Test]
    public void EqualPeopleNamesAreCandidatesWithoutEstablishingGlobalIdentity()
    {
        var result = matcher.ComparePeople(Person("John Miller"), Person("John Miller"));

        result.IsCandidate.Should().BeTrue();
        result.Disposition.Should().Be(IdentityMatchDisposition.NeedsEvidence);
        result.ProposedRule.Should().BeNull();
        result
            .Features.Should()
            .Contain(feature =>
                feature.Name == "artist_context"
                && feature.Agreement == IdentityFeatureAgreement.Missing
            );
    }

    [Test]
    public void DifferentArtistsDoNotContradictTheSameTaper()
    {
        var identifier = new MatchingExternalIdentity("example", "person", "taper-42", true);
        var first = Person("Taper Jeff") with
        {
            ArtistUuids = new HashSet<Guid> { Guid.NewGuid() },
            ExternalIdentities = [identifier],
        };
        var second = Person("Jeff") with
        {
            ArtistUuids = new HashSet<Guid> { Guid.NewGuid() },
            ExternalIdentities = [identifier],
        };
        var policy = Policy(
            IdentityMatchingDomain.People,
            IdentityMatchRule.QualifiedEntityIdentifier
        );

        var result = matcher.ComparePeople(first, second, policy);

        result.Disposition.Should().Be(IdentityMatchDisposition.AutomaticMatch);
        result
            .Features.Should()
            .Contain(feature =>
                feature.Name == "artist_context"
                && feature.Agreement == IdentityFeatureAgreement.Differs
            );
        result.EvaluationReceiptUuid.Should().Be(policy.EvaluationReceiptUuid);
    }

    [Test]
    public void QualifiedIdentifierIsOnlyProposedUntilItsPolicyIsActivated()
    {
        var identifier = new MatchingExternalIdentity("musicbrainz", "person", "person-42", true);
        var person = Person("Charlie Miller") with { ExternalIdentities = [identifier] };

        var result = matcher.ComparePeople(person, person);

        result.ProposedRule.Should().Be(IdentityMatchRule.QualifiedEntityIdentifier);
        result.Disposition.Should().Be(IdentityMatchDisposition.NeedsEvidence);
        result.PolicyVersion.Should().BeNull();
    }

    [Test]
    public void AnUploadIdentifierDoesNotEstablishItsUploaderAsTheTaper()
    {
        var identifier = new MatchingExternalIdentity("archive", "item", "aadam-jacobs-item");
        var person = Person("Aadam Jacobs") with { ExternalIdentities = [identifier] };

        var result = matcher.ComparePeople(
            person,
            person,
            Policy(IdentityMatchingDomain.People, IdentityMatchRule.QualifiedEntityIdentifier)
        );

        result.ProposedRule.Should().BeNull();
        result.Disposition.Should().Be(IdentityMatchDisposition.NeedsEvidence);
    }

    [Test]
    public void HandlesRequireTheSameProviderAndSeparateSupportedNameEvidence()
    {
        var claim = Person("Charlie Miller") with
        {
            Names = [new("Charlie Miller"), new("cmiller", true, "archive")],
        };
        var candidate = Person("Charlie Miller") with
        {
            Names = [new("Charlie Miller"), new("cmiller", true, "etree")],
        };
        var policy = Policy(
            IdentityMatchingDomain.People,
            IdentityMatchRule.PersonNameAndQualifiedHandle
        );

        matcher
            .ComparePeople(claim, candidate, policy)
            .Disposition.Should()
            .Be(IdentityMatchDisposition.NeedsEvidence);

        candidate = candidate with
        {
            Names = [new("Charlie Miller"), new("cmiller", true, "archive")],
        };
        matcher
            .ComparePeople(claim, candidate, policy)
            .Disposition.Should()
            .Be(IdentityMatchDisposition.AutomaticMatch);

        claim = claim with { Names = [new("cmiller", true, "archive")] };
        matcher
            .ComparePeople(claim, candidate, policy)
            .Disposition.Should()
            .Be(IdentityMatchDisposition.NeedsEvidence);
    }

    [Test]
    public void MatchingIdentifiersCannotOverrideAReviewedExclusionOrPersonGroupConflict()
    {
        var person = Person("Team Taper") with
        {
            ExternalIdentities = [new("example", "person", "42", true)],
        };
        var policy = Policy(
            IdentityMatchingDomain.People,
            IdentityMatchRule.QualifiedEntityIdentifier
        );

        matcher
            .ComparePeople(person, person, policy, hasReviewedExclusion: true)
            .Disposition.Should()
            .Be(IdentityMatchDisposition.Review);
        matcher
            .ComparePeople(person, person with { Kind = "group" }, policy)
            .Disposition.Should()
            .Be(IdentityMatchDisposition.Review);
    }

    [Test]
    public void AliasesCanRetrieveDiacriticVariantsWithoutErasingOriginalNames()
    {
        var claim = Person("José García");
        var candidate = Person("Jose Garcia");

        var result = matcher.ComparePeople(claim, candidate);

        result.IsCandidate.Should().BeTrue();
        result.NameTokenSimilarity.Should().Be(1);
        result
            .Features.Should()
            .Contain(feature =>
                feature.Name == "name" && feature.Agreement == IdentityFeatureAgreement.Differs
            );
        result.Disposition.Should().Be(IdentityMatchDisposition.NeedsEvidence);
        claim.Names.Single().Wording.Should().Be("José García");
    }

    [Test]
    public void DuplicateProviderPagesAreDifferentEvidenceRatherThanAnInventedConflict()
    {
        var first = Person("Taper Jeff") with
        {
            ExternalIdentities = [new("example", "profile", "page-1")],
        };
        var second = Person("Taper Jeff") with
        {
            ExternalIdentities = [new("example", "profile", "page-2")],
        };

        var result = matcher.ComparePeople(first, second);

        result.Disposition.Should().Be(IdentityMatchDisposition.NeedsEvidence);
        result
            .Features.Should()
            .Contain(feature =>
                feature.Name == "external_identifier"
                && feature.Agreement == IdentityFeatureAgreement.Differs
            );
    }

    [Test]
    public void AnExclusiveAuthorityConflictBlocksAnotherMatchingIdentifier()
    {
        var shared = new MatchingExternalIdentity("example", "person", "42", true);
        var first = Person("Charlie Miller") with
        {
            ExternalIdentities = [shared, new("authority", "person", "a", true, true)],
        };
        var second = Person("Charlie Miller") with
        {
            ExternalIdentities = [shared, new("authority", "person", "b", true, true)],
        };

        var result = matcher.ComparePeople(
            first,
            second,
            Policy(IdentityMatchingDomain.People, IdentityMatchRule.QualifiedEntityIdentifier)
        );

        result.Disposition.Should().Be(IdentityMatchDisposition.Review);
        result
            .Features.Should()
            .Contain(feature =>
                feature.Name == "exclusive_identifier"
                && feature.Agreement == IdentityFeatureAgreement.Conflicts
            );
    }

    [Test]
    public void TheSameVenueNameInDifferentResolvedCitiesConflicts()
    {
        var claim = Venue("The Fillmore", "us:ca:san-francisco", "1805 Geary Boulevard");
        var candidate = Venue("The Fillmore", "us:co:denver", "1510 Clarkson Street");

        var result = matcher.CompareVenues(
            claim,
            candidate,
            Policy(IdentityMatchingDomain.Venues, IdentityMatchRule.VenueNameAndAddress)
        );

        result.Disposition.Should().Be(IdentityMatchDisposition.Review);
        result
            .Features.Should()
            .Contain(feature =>
                feature.Name == "locality"
                && feature.Agreement == IdentityFeatureAgreement.Conflicts
            );
    }

    [Test]
    public void AValidatedVenueRuleNeedsLocalityAndAddress()
    {
        var venue = Venue("Madison Square Garden", "us:ny:new-york", "4 Pennsylvania Plaza");
        var policy = Policy(IdentityMatchingDomain.Venues, IdentityMatchRule.VenueNameAndAddress);

        matcher
            .CompareVenues(venue, venue, policy)
            .Disposition.Should()
            .Be(IdentityMatchDisposition.AutomaticMatch);
        matcher
            .CompareVenues(venue with { Location = null }, venue, policy)
            .Disposition.Should()
            .Be(IdentityMatchDisposition.NeedsEvidence);
    }

    [Test]
    public void ABuildingAndItsRoomRemainDifferentVenues()
    {
        var venue = Venue("The Warfield", "us:ca:san-francisco", "982 Market Street");
        var room = venue with { Kind = "room", ParentVenueUuid = Guid.NewGuid() };

        matcher.CompareVenues(venue, room).Disposition.Should().Be(IdentityMatchDisposition.Review);
        matcher
            .CompareVenues(room, room with { ParentVenueUuid = Guid.NewGuid() })
            .Disposition.Should()
            .Be(IdentityMatchDisposition.Review);
    }

    [Test]
    public void AHistoricalNameOutsideItsKnownDatesRequiresReview()
    {
        var claim = Venue("Wachovia Spectrum", "us:pa:philadelphia", "3601 South Broad Street") with
        {
            ObservedDate = new DateOnly(1970, 1, 1),
        };
        var candidate = claim with
        {
            ObservedDate = null,
            Names =
            [
                new("Wachovia Spectrum", new DateOnly(2003, 1, 1), new DateOnly(2009, 10, 31)),
            ],
        };
        var policy = Policy(IdentityMatchingDomain.Venues, IdentityMatchRule.VenueNameAndAddress);

        matcher
            .CompareVenues(claim, candidate, policy)
            .Disposition.Should()
            .Be(IdentityMatchDisposition.Review);

        candidate = candidate with { Names = [new("Wachovia Spectrum")] };
        matcher
            .CompareVenues(claim, candidate, policy)
            .Disposition.Should()
            .Be(IdentityMatchDisposition.AutomaticMatch);
    }

    [Test]
    public void DiscriminatingVenueTokensAreRetained()
    {
        var arena = Venue("Oakland Arena", "us:ca:oakland", "7000 Coliseum Way");
        var stadium = arena with { Names = [new("Oakland Stadium")] };

        var result = matcher.CompareVenues(
            arena,
            stadium,
            Policy(IdentityMatchingDomain.Venues, IdentityMatchRule.VenueNameAndAddress)
        );

        result.NameTokenSimilarity.Should().BeApproximately(1d / 3, 0.00001);
        result.Disposition.Should().Be(IdentityMatchDisposition.NeedsEvidence);
    }

    [Test]
    public void EqualTitlesCannotMoveRepertoireBetweenArtists()
    {
        var song = Repertoire("Dark Star", Guid.NewGuid()) with
        {
            CompositionUuid = Guid.NewGuid(),
        };
        var policy = Policy(
            IdentityMatchingDomain.Repertoire,
            IdentityMatchRule.RepertoireTitleAndComposition
        );

        matcher
            .CompareRepertoire(song, song with { ArtistUuid = Guid.NewGuid() }, policy)
            .Disposition.Should()
            .Be(IdentityMatchDisposition.Review);
        matcher
            .CompareRepertoire(song, song, policy)
            .Disposition.Should()
            .Be(IdentityMatchDisposition.AutomaticMatch);
    }

    [Test]
    public void ATitleDerivedSongDoesNotInventACompositionOrPerformance()
    {
        var song = Repertoire("Stop That Train", Guid.NewGuid());

        var result = matcher.CompareRepertoire(
            song,
            song,
            Policy(
                IdentityMatchingDomain.Repertoire,
                IdentityMatchRule.RepertoireTitleAndComposition
            )
        );

        result.Disposition.Should().Be(IdentityMatchDisposition.NeedsEvidence);
        result.ProposedRule.Should().BeNull();
        result
            .Features.Should()
            .Contain(feature =>
                feature.Name == "composition"
                && feature.Agreement == IdentityFeatureAgreement.Missing
            );
    }

    [Test]
    public void CrossArtistCompositionNeedsSupportedCompleteAuthorship()
    {
        var authors = new HashSet<Guid> { Guid.NewGuid(), Guid.NewGuid() };
        var work = Composition("Not Fade Away", authors, true);
        var policy = Policy(
            IdentityMatchingDomain.Compositions,
            IdentityMatchRule.CompositionTitleAndCompleteAuthorship
        );

        matcher
            .CompareCompositions(work, work, policy)
            .Disposition.Should()
            .Be(IdentityMatchDisposition.AutomaticMatch);
        matcher
            .CompareCompositions(
                work with
                {
                    CompleteAuthorship = false,
                },
                work with
                {
                    CompleteAuthorship = false,
                },
                policy
            )
            .Disposition.Should()
            .Be(IdentityMatchDisposition.NeedsEvidence);
        matcher
            .CompareCompositions(work with { AuthorUuids = new HashSet<Guid>() }, work, policy)
            .Disposition.Should()
            .Be(IdentityMatchDisposition.NeedsEvidence);
    }

    [Test]
    public void CompleteConflictingWritersBlockEvenASharedWorkIdentifier()
    {
        var identifier = new MatchingExternalIdentity("example", "work", "42", true);
        var first = Composition("The Wind", new HashSet<Guid> { Guid.NewGuid() }, true) with
        {
            ExternalIdentities = [identifier],
        };
        var second = Composition("The Wind", new HashSet<Guid> { Guid.NewGuid() }, true) with
        {
            ExternalIdentities = [identifier],
        };

        var result = matcher.CompareCompositions(
            first,
            second,
            Policy(IdentityMatchingDomain.Compositions, IdentityMatchRule.QualifiedEntityIdentifier)
        );

        result.Disposition.Should().Be(IdentityMatchDisposition.Review);
        result
            .Features.Should()
            .Contain(feature =>
                feature.Name == "authorship"
                && feature.Agreement == IdentityFeatureAgreement.Conflicts
            );
    }

    [Test]
    public void APolicyForAnotherDomainCannotAcceptAPair()
    {
        var person = Person("Taper Jeff") with
        {
            ExternalIdentities = [new("example", "person", "42", true)],
        };

        matcher
            .ComparePeople(
                person,
                person,
                Policy(
                    IdentityMatchingDomain.Compositions,
                    IdentityMatchRule.QualifiedEntityIdentifier
                )
            )
            .Disposition.Should()
            .Be(IdentityMatchDisposition.NeedsEvidence);
    }

    [Test]
    public void IdentifierValuesAreQualifiedByBothProviderAndResourceType()
    {
        var first = Person("Charlie Miller") with
        {
            ExternalIdentities = [new("provider-a", "person", "42", true)],
        };
        var second = first with { ExternalIdentities = [new("provider-b", "person", "42", true)] };
        var policy = Policy(
            IdentityMatchingDomain.People,
            IdentityMatchRule.QualifiedEntityIdentifier
        );

        matcher
            .ComparePeople(first, second, policy)
            .Disposition.Should()
            .Be(IdentityMatchDisposition.NeedsEvidence);
        second = second with { ExternalIdentities = [new("provider-a", "collection", "42", true)] };
        matcher
            .ComparePeople(first, second, policy)
            .Disposition.Should()
            .Be(IdentityMatchDisposition.NeedsEvidence);
    }

    [Test]
    public void OneEnabledSupportedRuleCanAcceptWhenAnotherRuleIsDisabled()
    {
        var person = Person("Charlie Miller") with
        {
            Names = [new("Charlie Miller"), new("cmiller", true, "archive")],
            ExternalIdentities = [new("example", "person", "42", true)],
        };

        var result = matcher.ComparePeople(
            person,
            person,
            Policy(IdentityMatchingDomain.People, IdentityMatchRule.PersonNameAndQualifiedHandle)
        );

        result.Disposition.Should().Be(IdentityMatchDisposition.AutomaticMatch);
        result.ProposedRule.Should().Be(IdentityMatchRule.PersonNameAndQualifiedHandle);
        result.SupportedRules.Should().Contain(IdentityMatchRule.QualifiedEntityIdentifier);
    }

    [Test]
    public void ANewIdentityProposalCannotPretendToMatchAnExistingCandidate()
    {
        var person = Person("Charlie Miller") with
        {
            ExternalIdentities = [new("example", "person", "42", true)],
        };

        var result = matcher.ComparePeople(
            person,
            person with
            {
                Uuid = null,
            },
            Policy(IdentityMatchingDomain.People, IdentityMatchRule.QualifiedEntityIdentifier)
        );

        result.Disposition.Should().Be(IdentityMatchDisposition.NeedsEvidence);
        result.CandidateUuid.Should().BeNull();
        result.EvaluationReceiptUuid.Should().BeNull();
    }

    [Test]
    public void APolicyWithoutAnEvaluationReceiptCannotEnableAutomaticMatching()
    {
        var person = Person("Charlie Miller") with
        {
            ExternalIdentities = [new("example", "person", "42", true)],
        };
        var policy = Policy(
            IdentityMatchingDomain.People,
            IdentityMatchRule.QualifiedEntityIdentifier
        ) with
        {
            EvaluationReceiptUuid = Guid.Empty,
        };

        matcher
            .ComparePeople(person, person, policy)
            .Disposition.Should()
            .Be(IdentityMatchDisposition.NeedsEvidence);
    }

    [Test]
    public void APartialCreditContradictsACompleteListThatExcludesItsWriter()
    {
        var first = Composition("Dark Star", new HashSet<Guid> { Guid.NewGuid() }, true);
        var second = Composition("Dark Star", new HashSet<Guid> { Guid.NewGuid() }, false);

        var result = matcher.CompareCompositions(first, second);

        result.Disposition.Should().Be(IdentityMatchDisposition.Review);
        result
            .Features.Should()
            .Contain(feature =>
                feature.Name == "authorship"
                && feature.Agreement == IdentityFeatureAgreement.Conflicts
            );
    }

    [Test]
    public void UnrelatedNamesWithNoOtherEvidenceDoNotEnterTheCandidateSet()
    {
        var result = matcher.ComparePeople(Person("Charlie Miller"), Person("Aadam Jacobs"));

        result.IsCandidate.Should().BeFalse();
        result.Disposition.Should().Be(IdentityMatchDisposition.NotCandidate);
    }

    private static PersonMatchRecord Person(string name) =>
        new(Guid.NewGuid(), "person", [new(name)], [], new HashSet<Guid>());

    private static VenueMatchRecord Venue(string name, string locality, string address) =>
        new(Guid.NewGuid(), "site", null, [new(name)], [], new("US", locality, address));

    private static RepertoireMatchRecord Repertoire(string title, Guid artistUuid) =>
        new(Guid.NewGuid(), artistUuid, [new(title)], []);

    private static CompositionMatchRecord Composition(
        string title,
        IReadOnlySet<Guid> authors,
        bool complete
    ) => new(Guid.NewGuid(), [new(title)], [], authors, complete);

    private static EvaluationBackedIdentityPolicy Policy(
        IdentityMatchingDomain domain,
        IdentityMatchRule rule
    ) =>
        new(
            "regression-test-policy",
            Guid.NewGuid(),
            domain,
            new HashSet<IdentityMatchRule> { rule }
        );
}
