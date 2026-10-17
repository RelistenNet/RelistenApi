using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace RelistenApiTests.Normalization;

/// <summary>Complete histories cannot be reduced to the location most convenient for a match.</summary>
[TestFixture]
public sealed class TestVenueLocationHistoryMatching
{
    private readonly IdentityCandidateMatcher matcher = new();
    private static readonly MatchingVenueLocation first = new(
        "US",
        "us:ca:san-francisco",
        "1805 Geary Boulevard"
    );
    private static readonly MatchingVenueLocation second = new(
        "US",
        "us:co:denver",
        "1510 Clarkson Street"
    );

    [TestCase(false)]
    [TestCase(true)]
    public void KnownObservationDateUsesTheSupportedHistoricalLocation(bool reverse)
    {
        var observed = Venue([new(first)]) with { ObservedDate = new DateOnly(1975, 1, 1) };
        var history = Venue([During(first, 1970, 1979), During(second, 2000, 2009)]);

        var result = Compare(reverse ? history : observed, reverse ? observed : history);

        result.Disposition.Should().Be(IdentityMatchDisposition.AutomaticMatch);
        result.ProposedRule.Should().Be(IdentityMatchRule.VenueNameAndAddress);
    }

    [Test]
    public void UnknownObservationDatePreservesDisjointAlternatives()
    {
        var result = Compare(
            Venue([new(first)]),
            Venue([During(first, 1970, 1979), During(second, 2000, 2009)])
        );

        result.Disposition.Should().Be(IdentityMatchDisposition.NeedsEvidence);
        result.SupportedRules.Should().NotContain(IdentityMatchRule.VenueNameAndAddress);
        result
            .Features.Should()
            .NotContain(feature => feature.Agreement == IdentityFeatureAgreement.Conflicts);
        result
            .Features.Should()
            .Contain(feature =>
                feature.Name == "location_history"
                && feature.Agreement == IdentityFeatureAgreement.Missing
            );
    }

    [TestCase(null, null)]
    [TestCase(1980, null)]
    [TestCase(null, 2010)]
    public void UnknownOrOpenBoundsDoNotInventConcurrencyOrDiscardAnAlternative(int? from, int? to)
    {
        var alternative = new MatchingVenueLocationAssertion(
            second,
            from.HasValue ? new DateOnly(from.Value, 1, 1) : null,
            to.HasValue ? new DateOnly(to.Value, 12, 31) : null
        );

        var result = Compare(Venue([new(first)]), Venue([During(first, 1970, 1979), alternative]));

        result.Disposition.Should().Be(IdentityMatchDisposition.NeedsEvidence);
        result
            .Features.Should()
            .NotContain(feature => feature.Agreement == IdentityFeatureAgreement.Conflicts);
    }

    [Test]
    public void ProvenConcurrentResolvedCitiesRequireReviewEvenWhenOneLocationAgrees()
    {
        var observed = Venue([new(first)]) with { ObservedDate = new DateOnly(1985, 1, 1) };
        var history = Venue([During(first, 1970, 1990), During(second, 1980, 2000)]);

        var result = Compare(observed, history);

        result.Disposition.Should().Be(IdentityMatchDisposition.Review);
        result
            .Features.Should()
            .Contain(feature =>
                feature.Name == "locality"
                && feature.Agreement == IdentityFeatureAgreement.Conflicts
            );
    }

    [TestCase(1989, IdentityMatchDisposition.NeedsEvidence)]
    [TestCase(1990, IdentityMatchDisposition.Review)]
    public void ClosedIntervalsPreserveInclusiveBoundaries(
        int firstEnds,
        IdentityMatchDisposition expected
    )
    {
        var history = Venue([
            new(first, new DateOnly(1970, 1, 1), new DateOnly(firstEnds, 1, 1)),
            new(second, new DateOnly(1990, 1, 1), new DateOnly(2000, 1, 1)),
        ]);

        Compare(Venue([new(first)]), history).Disposition.Should().Be(expected);
    }

    [Test]
    public void AProvenOutsideOpenBoundCanBeExcludedAtAKnownDate()
    {
        var observed = Venue([new(first)]) with { ObservedDate = new DateOnly(1975, 1, 1) };
        var history = Venue([During(first, 1970, 1979), new(second, new DateOnly(1980, 1, 1))]);

        Compare(observed, history).Disposition.Should().Be(IdentityMatchDisposition.AutomaticMatch);
    }

    [Test]
    public void AnUndatedAlternativeRemainsPossibleAtAKnownDate()
    {
        var observed = Venue([new(first)]) with { ObservedDate = new DateOnly(1975, 1, 1) };
        var history = Venue([During(first, 1970, 1979), new(second)]);

        Compare(observed, history).Disposition.Should().Be(IdentityMatchDisposition.NeedsEvidence);
    }

    [Test]
    public void ASharedObservationDateDoesNotDateEveryUndatedHistoricalAssertion()
    {
        var date = new DateOnly(1975, 1, 1);
        var observed = Venue([new(first)]) with { ObservedDate = date };
        var history = Venue([new(first), new(second)]) with { ObservedDate = date };

        var result = Compare(observed, history);

        result.Disposition.Should().Be(IdentityMatchDisposition.NeedsEvidence);
        result
            .Features.Should()
            .NotContain(feature => feature.Agreement == IdentityFeatureAgreement.Conflicts);
    }

    [Test]
    public void ADateOnALegacySingleObservationSupportsAPointWhenComparingACollection()
    {
        var observed = Venue([]) with
        {
            Location = first,
            LocationAssertions = null,
            ObservedDate = new DateOnly(1975, 1, 1),
        };
        var history = Venue([During(second, 1970, 1979)]);

        Compare(observed, history).Disposition.Should().Be(IdentityMatchDisposition.Review);
    }

    [Test]
    public void DifferentObservationDatesDoNotSelectOneConvenientHistoricalAccount()
    {
        var observed = Venue([new(first)]) with { ObservedDate = new DateOnly(1975, 1, 1) };
        var history = Venue([During(first, 1970, 1979), During(second, 2000, 2009)]) with
        {
            ObservedDate = new DateOnly(2005, 1, 1),
        };

        Compare(observed, history).Disposition.Should().Be(IdentityMatchDisposition.NeedsEvidence);
    }

    [Test]
    public void EmptyAuthoritativeCollectionDoesNotUseTheLegacyLocation()
    {
        var observed = Venue([]) with { Location = first };
        var candidate = Venue([new(first)]);

        var result = Compare(observed, candidate);

        result.Disposition.Should().Be(IdentityMatchDisposition.NeedsEvidence);
        result
            .Features.Should()
            .Contain(feature =>
                feature.Name == "locality" && feature.Agreement == IdentityFeatureAgreement.Missing
            );
    }

    [Test]
    public void LocalityAndAddressCannotBeAssembledFromSeparatePartialAssertions()
    {
        var candidate = Venue([
            new(first with { Address = null }),
            new(first with { LocalityKey = null }),
        ]);

        var result = Compare(Venue([new(first)]), candidate);

        result.Disposition.Should().Be(IdentityMatchDisposition.NeedsEvidence);
        result.SupportedRules.Should().NotContain(IdentityMatchRule.VenueNameAndAddress);
    }

    [Test]
    public void EveryPossibleLocationCanAgreeWithoutAnObservationDate()
    {
        var history = Venue([During(first, 1970, 1979), During(first, 2000, 2009), new(first)]);

        Compare(Venue([new(first)]), history)
            .Disposition.Should()
            .Be(IdentityMatchDisposition.AutomaticMatch);
    }

    [Test]
    public void DifferentAddressWordingDoesNotProveAConcurrentIdentityConflict()
    {
        var history = Venue([
            During(first, 1970, 1990),
            During(first with { Address = "1805 Geary Blvd" }, 1980, 2000),
        ]);

        var result = Compare(Venue([new(first)]), history);

        result.Disposition.Should().Be(IdentityMatchDisposition.NeedsEvidence);
        result
            .Features.Should()
            .NotContain(feature => feature.Agreement == IdentityFeatureAgreement.Conflicts);
        result
            .Features.Should()
            .Contain(feature =>
                feature.Name == "address" && feature.Agreement == IdentityFeatureAgreement.Differs
            );
    }

    [TestCase(false, IdentityMatchDisposition.AutomaticMatch)]
    [TestCase(true, IdentityMatchDisposition.Review)]
    public void IndependentlyValidatedEntityIdentifiersRemainUsableUnlessThereIsAProvenConflict(
        bool concurrent,
        IdentityMatchDisposition expected
    )
    {
        MatchingExternalIdentity[] identities =
        [
            new("venue-authority", "venue", "site-123", true, true),
        ];
        var observed = Venue([new(first)]) with { ExternalIdentities = identities };
        var history = Venue([
            During(first, 1970, concurrent ? 1990 : 1979),
            During(second, concurrent ? 1980 : 2000, 2009),
        ]) with
        {
            ExternalIdentities = identities,
        };

        var result = matcher.CompareVenues(
            observed,
            history,
            Policy(IdentityMatchRule.QualifiedEntityIdentifier)
        );

        result.Disposition.Should().Be(expected);
        result.SupportedRules.Should().NotContain(IdentityMatchRule.VenueNameAndAddress);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void UndatedCountryDifferencesBlockTheLocationRuleEvenWhenLocalityAndAddressAgree(
        bool includeUnknown
    )
    {
        var locations = new List<MatchingVenueLocationAssertion>
        {
            new(first with { CountryCode = "CA" }),
        };
        if (includeUnknown)
        {
            locations.Add(new(first with { CountryCode = null }));
        }

        var result = Compare(Venue([new(first)]), Venue(locations));

        result.Disposition.Should().Be(IdentityMatchDisposition.NeedsEvidence);
        result.SupportedRules.Should().NotContain(IdentityMatchRule.VenueNameAndAddress);
        result
            .Features.Should()
            .Contain(feature =>
                feature.Name == "country" && feature.Agreement == IdentityFeatureAgreement.Differs
            );
        result
            .Features.Should()
            .Contain(feature =>
                feature.Name == "locality" && feature.Agreement == IdentityFeatureAgreement.Agrees
            );
        result
            .Features.Should()
            .Contain(feature =>
                feature.Name == "address" && feature.Agreement == IdentityFeatureAgreement.Agrees
            );
    }

    [Test]
    public void MissingCountryAloneDoesNotBlockCompleteLocalityAndAddressAgreement()
    {
        var result = Compare(Venue([new(first)]), Venue([new(first with { CountryCode = null })]));

        result.Disposition.Should().Be(IdentityMatchDisposition.AutomaticMatch);
        result.ProposedRule.Should().Be(IdentityMatchRule.VenueNameAndAddress);
        result
            .Features.Should()
            .Contain(feature =>
                feature.Name == "country" && feature.Agreement == IdentityFeatureAgreement.Missing
            );
    }

    [Test]
    public void IndependentEntityIdentifiersCanResolveUndatedCountryAmbiguity()
    {
        MatchingExternalIdentity[] identities =
        [
            new("venue-authority", "venue", "site-123", true, true),
        ];
        var observed = Venue([new(first)]) with { ExternalIdentities = identities };
        var candidate = Venue([new(first with { CountryCode = "CA" })]) with
        {
            ExternalIdentities = identities,
        };

        var result = matcher.CompareVenues(
            observed,
            candidate,
            Policy(IdentityMatchRule.QualifiedEntityIdentifier)
        );

        result.Disposition.Should().Be(IdentityMatchDisposition.AutomaticMatch);
        result.ProposedRule.Should().Be(IdentityMatchRule.QualifiedEntityIdentifier);
        result.SupportedRules.Should().NotContain(IdentityMatchRule.VenueNameAndAddress);
    }

    [Test]
    public void ProvenConcurrentCountryDifferencesRequireReviewEvenWithEqualLocalityAndAddress()
    {
        var candidate = Venue([
            During(first, 1970, 1990),
            During(first with { CountryCode = "CA" }, 1980, 2000),
        ]);

        var result = Compare(Venue([new(first)]), candidate);

        result.Disposition.Should().Be(IdentityMatchDisposition.Review);
        result
            .Features.Should()
            .Contain(feature =>
                feature.Name == "country" && feature.Agreement == IdentityFeatureAgreement.Conflicts
            );
        result.SupportedRules.Should().NotContain(IdentityMatchRule.VenueNameAndAddress);
    }

    [TestCase(false, "???", "---")]
    [TestCase(true, "???", "---")]
    [TestCase(false, "  ", "\t")]
    [TestCase(true, "  ", "\t")]
    [TestCase(false, "???", "1805 Geary Boulevard")]
    [TestCase(true, "???", "1805 Geary Boulevard")]
    public void AddressesWithoutAnyNormalizedTextRemainMissing(
        bool authoritative,
        string left,
        string right
    )
    {
        var observed = SingleLocation(first with { Address = left }, authoritative);
        var candidate = SingleLocation(first with { Address = right }, authoritative);

        var result = Compare(observed, candidate);

        result.Disposition.Should().Be(IdentityMatchDisposition.NeedsEvidence);
        result.SupportedRules.Should().NotContain(IdentityMatchRule.VenueNameAndAddress);
        result
            .Features.Should()
            .Contain(feature =>
                feature.Name == "address" && feature.Agreement == IdentityFeatureAgreement.Missing
            );
    }

    [TestCase(false)]
    [TestCase(true)]
    public void OrdinaryAddressPunctuationStillPermitsTheLocationRule(bool authoritative)
    {
        var observed = SingleLocation(first, authoritative);
        var candidate = SingleLocation(
            first with
            {
                Address = "1805, Geary Boulevard!",
            },
            authoritative
        );

        Compare(observed, candidate)
            .Disposition.Should()
            .Be(IdentityMatchDisposition.AutomaticMatch);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void MissingAddressDoesNotBlockIndependentValidatedEntityIdentifiers(bool authoritative)
    {
        MatchingExternalIdentity[] identities =
        [
            new("venue-authority", "venue", "site-123", true, true),
        ];
        var observed = SingleLocation(first with { Address = "???" }, authoritative) with
        {
            ExternalIdentities = identities,
        };
        var candidate = SingleLocation(first with { Address = "---" }, authoritative) with
        {
            ExternalIdentities = identities,
        };

        var result = matcher.CompareVenues(
            observed,
            candidate,
            Policy(IdentityMatchRule.QualifiedEntityIdentifier)
        );

        result.Disposition.Should().Be(IdentityMatchDisposition.AutomaticMatch);
        result.ProposedRule.Should().Be(IdentityMatchRule.QualifiedEntityIdentifier);
        result.SupportedRules.Should().NotContain(IdentityMatchRule.VenueNameAndAddress);
    }

    [TestCase(false, IdentityMatchDisposition.Review)]
    [TestCase(true, IdentityMatchDisposition.NeedsEvidence)]
    public void AddressNormalizationDoesNotNormalizeOpaqueGeographicIdentifiers(
        bool authoritative,
        IdentityMatchDisposition expected
    )
    {
        var observed = SingleLocation(first with { LocalityKey = "authority:123" }, authoritative);
        var candidate = SingleLocation(first with { LocalityKey = "authority/123" }, authoritative);

        var result = Compare(observed, candidate);

        result.Disposition.Should().Be(expected);
        result
            .Features.Should()
            .NotContain(feature =>
                feature.Name == "locality" && feature.Agreement == IdentityFeatureAgreement.Agrees
            );
    }

    [Test]
    public void StoredPageIdentifiersDoNotAcquireAuthorityFromTheLocationHistory()
    {
        MatchingExternalIdentity[] identities = [new("venue-pages", "venue", "site-123")];
        var observed = Venue([new(first)]) with { ExternalIdentities = identities };
        var history = Venue([During(first, 1970, 1979), During(second, 2000, 2009)]) with
        {
            ExternalIdentities = identities,
        };

        matcher
            .CompareVenues(observed, history, Policy(IdentityMatchRule.QualifiedEntityIdentifier))
            .Disposition.Should()
            .Be(IdentityMatchDisposition.NeedsEvidence);
    }

    [Test]
    public void RawCountryAndCityLabelsCannotSupplyResolvedGeographicKeys()
    {
        var unresolved = Venue([new(new(null, null, first.Address))]);

        Compare(unresolved, unresolved)
            .Disposition.Should()
            .Be(IdentityMatchDisposition.NeedsEvidence);
    }

    private IdentityCandidateComparison Compare(
        VenueMatchRecord observed,
        VenueMatchRecord candidate
    ) => matcher.CompareVenues(observed, candidate, Policy(IdentityMatchRule.VenueNameAndAddress));

    private static VenueMatchRecord Venue(
        IReadOnlyList<MatchingVenueLocationAssertion> locations
    ) =>
        new(
            Guid.NewGuid(),
            "site",
            null,
            [new("Fillmore Auditorium")],
            [],
            null,
            LocationAssertions: locations
        );

    private static VenueMatchRecord SingleLocation(
        MatchingVenueLocation location,
        bool authoritative
    ) =>
        authoritative
            ? Venue([new(location)])
            : Venue([]) with
            {
                Location = location,
                LocationAssertions = null,
            };

    private static MatchingVenueLocationAssertion During(
        MatchingVenueLocation location,
        int from,
        int to
    ) => new(location, new DateOnly(from, 1, 1), new DateOnly(to, 12, 31));

    private static EvaluationBackedIdentityPolicy Policy(IdentityMatchRule rule) =>
        new(
            "fixture-venue-history",
            Guid.NewGuid(),
            IdentityMatchingDomain.Venues,
            new HashSet<IdentityMatchRule> { rule }
        );
}
