using System.Text.Json;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
using Relisten.Normalization;

namespace RelistenApiTests.Normalization;

/// <summary>Rule behavior does not qualify its independent accuracy or activate publication.</summary>
[TestFixture]
public class TestVenueResolutionPolicyEvaluator
{
    private static readonly Guid VenueUuid = Guid.Parse("ea1d0e40-0678-4b49-8b51-0360399b2e6c");
    private static readonly NormalizedEvidenceReference Evidence = new(
        Guid.Parse("c9c3fc46-f162-4d44-98fb-ab20384e7a08"),
        "/venue"
    );
    private readonly VenueResolutionPolicyEvaluator evaluator = new();

    [Test]
    public void SupportedAddressAndResolvedLocalityProposeTheUniquePhysicalVenue()
    {
        var result = Evaluate(Facts());

        result.Action.Should().Be(VenueCorrespondenceAction.ProposeMatch);
        result.VenueUuid.Should().Be(VenueUuid);
        result
            .Candidates.Single()
            .Features.Should()
            .Contain("exact_name_address_and_resolved_locality");
        result.Candidates.Single().Evidence.Should().Contain(Evidence);
    }

    [Test]
    public void RawNameLocalityAndCityCoordinatesCannotReplaceResolvedGeography()
    {
        var facts = Facts(localityKey: null);
        var result = Evaluate(facts);

        result.Action.Should().Be(VenueCorrespondenceAction.NeedsReview);
        result.Candidates.Single().Eligible.Should().BeFalse();
    }

    [Test]
    public void AQualifiedPhysicalIdentifierCanMatchDifferentNamesWithoutAddress()
    {
        var facts = Facts(localityKey: null, address: null, qualifiedIdentity: true);
        var target = facts.SupportedFacts!.Candidates.Single();
        var candidate = facts.Candidates.Single() with { Names = [] };
        facts = facts with
        {
            Candidates = [candidate],
            SupportedFacts = facts.SupportedFacts with
            {
                Candidates = [target with { Match = target.Match with { Names = [] } }],
            },
        };

        Evaluate(facts).Action.Should().Be(VenueCorrespondenceAction.ProposeMatch);
    }

    [Test]
    public void AnUnqualifiedProviderPageIdentifierDoesNotEstablishPhysicalIdentity()
    {
        var facts = Facts(localityKey: null, qualifiedIdentity: true);
        var source = facts.SupportedFacts!.Source;
        facts = facts with
        {
            SupportedFacts = facts.SupportedFacts with
            {
                Source = source with
                {
                    Match = source.Match with
                    {
                        ExternalIdentities =
                        [
                            source.Match.ExternalIdentities.Single() with
                            {
                                IdentifiesEntity = false,
                            },
                        ],
                    },
                },
            },
        };

        Evaluate(facts).Action.Should().Be(VenueCorrespondenceAction.NeedsReview);
    }

    [TestCase("legacy_catalog")]
    [TestCase("LEGACY_CATALOG")]
    public void CatalogCoordinatesCannotBeQualifiedAsPhysicalIdentifiers(string provider)
    {
        var facts = Facts(localityKey: null, address: null, qualifiedIdentity: true);
        MatchingExternalIdentity Replace(MatchingExternalIdentity value) =>
            value with
            {
                Provider = provider,
            };
        var candidate = facts.Candidates.Single();
        var supported = facts.SupportedFacts!;
        facts = facts with
        {
            OriginalVenueObservation = facts.OriginalVenueObservation with
            {
                Identities = facts
                    .OriginalVenueObservation.Identities.Select(value =>
                        value with
                        {
                            Identity = value.Identity with { Provider = provider },
                        }
                    )
                    .ToArray(),
            },
            Source = facts.Source with
            {
                ExternalIdentities = facts.Source.ExternalIdentities.Select(Replace).ToArray(),
            },
            Candidates =
            [
                candidate with
                {
                    ExternalIdentities = candidate
                        .ExternalIdentities.Select(value => value with { Provider = provider })
                        .ToArray(),
                },
            ],
            SupportedFacts = supported with
            {
                Source = supported.Source with
                {
                    Match = supported.Source.Match with
                    {
                        ExternalIdentities = supported
                            .Source.Match.ExternalIdentities.Select(Replace)
                            .ToArray(),
                    },
                },
                Candidates =
                [
                    supported.Candidates.Single() with
                    {
                        Match = supported.Candidates.Single().Match with
                        {
                            ExternalIdentities = supported
                                .Candidates.Single()
                                .Match.ExternalIdentities.Select(Replace)
                                .ToArray(),
                        },
                    },
                ],
            },
        };

        Evaluate(facts).Action.Should().Be(VenueCorrespondenceAction.NeedsReview);
    }

    [Test]
    public void CandidateRuleFlagsAreRecomputedRatherThanTrusted()
    {
        var facts = Facts(localityKey: null);
        facts = facts with
        {
            Comparisons =
            [
                facts.Comparisons.Single() with
                {
                    SupportedRules =
                    [
                        IdentityMatchRule.VenueNameAndAddress,
                        IdentityMatchRule.QualifiedEntityIdentifier,
                    ],
                },
            ],
        };

        Evaluate(facts).Action.Should().Be(VenueCorrespondenceAction.NeedsReview);
    }

    [TestCase(false, "accepted")]
    [TestCase(true, "rejected")]
    public void CurrentAssignmentsAndReviewedExclusionsRemainAuthoritative(
        bool targetMatches,
        string state
    )
    {
        var facts = Facts() with
        {
            Assignments =
            [
                new(
                    Guid.NewGuid(),
                    1,
                    VenueUuid,
                    targetMatches ? VenueUuid : Guid.NewGuid(),
                    state,
                    "reviewed",
                    "review",
                    Guid.NewGuid(),
                    false,
                    null,
                    [Evidence]
                ),
            ],
        };

        Evaluate(facts).Action.Should().Be(VenueCorrespondenceAction.NeedsReview);
    }

    [Test]
    public void HistoricalExclusionDoesNotOverrideItsLaterCorrection()
    {
        var facts = Facts() with
        {
            Assignments =
            [
                new(
                    Guid.NewGuid(),
                    1,
                    VenueUuid,
                    VenueUuid,
                    "rejected",
                    "reviewed",
                    "review",
                    Guid.NewGuid(),
                    true,
                    Guid.NewGuid(),
                    [Evidence]
                ),
            ],
        };

        Evaluate(facts).Action.Should().Be(VenueCorrespondenceAction.ProposeMatch);
    }

    [Test]
    public void AddressMatchingCannotGuessThatAnUnknownKindIsASite()
    {
        var facts = Facts(kind: "");

        Evaluate(facts).Action.Should().Be(VenueCorrespondenceAction.NeedsReview);
    }

    [Test]
    public void RoomsRequireTheSameKnownParentForAddressMatching()
    {
        Evaluate(Facts(kind: "room")).Action.Should().Be(VenueCorrespondenceAction.NeedsReview);
        Evaluate(Facts(kind: "room", parent: Guid.NewGuid()))
            .Action.Should()
            .Be(VenueCorrespondenceAction.ProposeMatch);
    }

    [Test]
    public void CompetingSupportedPhysicalVenuesRequireReview()
    {
        var facts = Facts();
        var otherUuid = Guid.NewGuid();
        var second = facts.Candidates.Single() with
        {
            Header = facts.Candidates.Single().Header with { Uuid = otherUuid },
        };
        var secondMatch = facts.SupportedFacts!.Candidates.Single() with
        {
            Match = facts.SupportedFacts.Candidates.Single().Match with { Uuid = otherUuid },
        };
        facts = facts with
        {
            Candidates = [facts.Candidates.Single(), second],
            Comparisons =
            [
                facts.Comparisons.Single(),
                new IdentityCandidateMatcher().CompareVenues(facts.Source, secondMatch.Match),
            ],
            SupportedFacts = facts.SupportedFacts with
            {
                Candidates = [facts.SupportedFacts.Candidates.Single(), secondMatch],
            },
        };

        Evaluate(facts).Reason.Should().Be("competing_supported_venues");
    }

    [Test]
    public void SupportedProjectionCannotDiscardAnAlternativeHistoricalLocation()
    {
        var facts = Facts();
        var candidate = facts.Candidates.Single();
        facts = facts with
        {
            Candidates =
            [
                candidate with
                {
                    Locations =
                    [
                        candidate.Locations.Single(),
                        candidate.Locations.Single() with
                        {
                            Address = "99 Other Street",
                        },
                    ],
                },
            ],
        };

        Evaluate(facts).Reason.Should().Be("missing_or_inconsistent_supported_facts");
    }

    [Test]
    public void AnUndatedAlternativeAddressPreventsTheAddressRule()
    {
        var facts = Facts();
        var candidate = facts.Candidates.Single();
        var projection = facts.SupportedFacts!.Candidates.Single();
        facts = facts with
        {
            Candidates =
            [
                candidate with
                {
                    Locations =
                    [
                        candidate.Locations.Single(),
                        candidate.Locations.Single() with
                        {
                            Address = "99 Other Street",
                        },
                    ],
                },
            ],
            SupportedFacts = facts.SupportedFacts with
            {
                Candidates =
                [
                    projection with
                    {
                        Match = projection.Match with
                        {
                            LocationAssertions =
                            [
                                projection.Match.LocationAssertions!.Single(),
                                new(new("US", "geonames:5391959", "99 Other Street")),
                            ],
                        },
                    },
                ],
            },
        };

        Evaluate(facts).Action.Should().Be(VenueCorrespondenceAction.NeedsReview);
    }

    [Test]
    public void AConflictingHistoricalNameBlocksEvenTheIdentifierRule()
    {
        var facts = Facts(qualifiedIdentity: true);
        var candidate = facts.Candidates.Single();
        var projection = facts.SupportedFacts!.Candidates.Single();
        var oldName = candidate.Names.Single() with { ValidTo = new DateOnly(1968, 12, 31) };
        facts = facts with
        {
            Candidates = [candidate with { Names = [oldName] }],
            SupportedFacts = facts.SupportedFacts with
            {
                Candidates =
                [
                    projection with
                    {
                        Match = projection.Match with
                        {
                            Names = [new(oldName.Wording, oldName.ValidFrom, oldName.ValidTo)],
                        },
                    },
                ],
            },
        };

        Evaluate(facts).Action.Should().Be(VenueCorrespondenceAction.NeedsReview);
    }

    [Test]
    public void ConflictingParentAccountsBlockTheIdentifierRule()
    {
        var facts = Facts(kind: "room", parent: Guid.NewGuid(), qualifiedIdentity: true);
        var candidate = facts.Candidates.Single();
        var projection = facts.SupportedFacts!.Candidates.Single();
        facts = facts with
        {
            Candidates =
            [
                candidate with
                {
                    Parents =
                    [
                        candidate.Parents.Single(),
                        new(VenueUuid, Guid.NewGuid(), Guid.NewGuid(), [], []),
                    ],
                },
            ],
            SupportedFacts = facts.SupportedFacts with
            {
                Candidates =
                [
                    projection with
                    {
                        Match = projection.Match with { ParentVenueUuid = null },
                    },
                ],
            },
        };

        var result = Evaluate(facts);
        result.Action.Should().Be(VenueCorrespondenceAction.NeedsReview);
        result
            .Candidates.Single()
            .Contradictions.Should()
            .Contain("alternative_parent_site_accounts");
    }

    [Test]
    public void AnUnresolvedCandidateParentCannotOverrideTheKnownSourceParent()
    {
        var facts = Facts(kind: "room", parent: Guid.NewGuid(), qualifiedIdentity: true);
        var projection = facts.SupportedFacts!.Candidates.Single();
        facts = facts with
        {
            Candidates = [facts.Candidates.Single() with { Parents = [] }],
            SupportedFacts = facts.SupportedFacts with
            {
                Candidates =
                [
                    projection with
                    {
                        Match = projection.Match with { ParentVenueUuid = null },
                    },
                ],
            },
        };

        var result = Evaluate(facts);
        result.Action.Should().Be(VenueCorrespondenceAction.NeedsReview);
        result.Candidates.Single().Contradictions.Should().Contain("known_parent_site_conflict");
    }

    [Test]
    public void SupportingAssertionsMustRetainEvidence()
    {
        var facts = Facts();
        facts = facts with
        {
            SupportedFacts = facts.SupportedFacts! with
            {
                Source = facts.SupportedFacts.Source with { Evidence = [] },
            },
        };

        Evaluate(facts).Reason.Should().Be("missing_or_inconsistent_supported_facts");
    }

    [Test]
    public void IncompletePoolCannotProposeAMatch()
    {
        Evaluate(Facts() with { Complete = false })
            .Reason.Should()
            .Be("incomplete_or_inconsistent_candidate_pool");
        Evaluate(Facts() with { Comparisons = [] })
            .Reason.Should()
            .Be("incomplete_or_inconsistent_candidate_pool");
    }

    [Test]
    public void FixtureAndPhysicalRulesHaveSeparateVersionAndEvaluationContracts()
    {
        var artifact = Policy().Artifact;
        VenueResolutionPolicyEvaluator.Supports(artifact).Should().BeTrue();
        VenueResolutionPolicyEvaluator
            .Supports(artifact with { FixtureOnly = true })
            .Should()
            .BeFalse();
        VenueResolutionPolicyEvaluator
            .Supports(
                artifact with
                {
                    SourceContextVersion = VenueResolutionPolicyEvaluator.SourceContextVersion,
                }
            )
            .Should()
            .BeFalse();
        var policy = Policy();
        evaluator
            .Evaluate(
                Facts(),
                policy with
                {
                    Evaluation = policy.Evaluation with { FixtureOnly = true },
                }
            )
            .Reason.Should()
            .Be("unsupported_policy_or_fact_versions");
    }

    [Test]
    public void ExistingFixturePolicyStillEvaluatesItsOriginalRawFacts()
    {
        var policy = Policy();
        policy = policy with
        {
            Artifact = policy.Artifact with
            {
                FixtureOnly = true,
                MatchingRuleId = VenueResolutionPolicyEvaluator.FixtureRuleId,
                SourceContextVersion = VenueResolutionPolicyEvaluator.SourceContextVersion,
            },
            Evaluation = policy.Evaluation with { FixtureOnly = true },
        };
        var facts = Facts() with
        {
            SourceContextVersion = VenueResolutionPolicyEvaluator.SourceContextVersion,
            SupportedFacts = null,
        };

        var result = evaluator.Evaluate(facts, policy);
        result.Action.Should().Be(VenueCorrespondenceAction.ProposeMatch);
        result.Reason.Should().Be("fixture_unique_supported_venue");
    }

    [Test]
    public void OmittingSupportedFactsPreservesOldSerializedReceiptShape()
    {
        var facts = Facts() with { SupportedFacts = null };
        JsonSerializer
            .SerializeToElement(facts)
            .TryGetProperty("SupportedFacts", out _)
            .Should()
            .BeFalse();
        Evaluate(facts).Reason.Should().Be("missing_or_inconsistent_supported_facts");
    }

    private VenueCorrespondenceDecision Evaluate(VenueCorrespondenceFacts facts) =>
        evaluator.Evaluate(facts, Policy());

    private static VenueCorrespondenceFacts Facts(
        string? localityKey = "geonames:5391959",
        string? address = "1805 Geary Boulevard",
        bool qualifiedIdentity = false,
        string kind = "site",
        Guid? parent = null
    )
    {
        var date = new DateOnly(1969, 4, 22);
        var identifiers = qualifiedIdentity
            ? new[] { new MatchingExternalIdentity("musicbrainz", "place", "physical-place", true) }
            : [];
        var source = new VenueMatchRecord(
            null,
            kind,
            parent,
            [new("Fillmore")],
            identifiers,
            null,
            date,
            [new(new("US", localityKey, address))]
        );
        var target = source with { Uuid = VenueUuid, ObservedDate = null };
        var provenance = new CandidateAssertionProvenance(
            Guid.NewGuid(),
            VenueUuid,
            Guid.NewGuid(),
            1,
            "reviewed",
            [Evidence]
        );
        var candidate = new VenueCandidateFacts
        {
            Hit = new(VenueUuid, 1, "Fillmore", provenance.OwnerSubjectUuid, 1, ["name"]),
            Header = new(VenueUuid, "Fillmore", 1, provenance.OwnerSubjectUuid, kind),
            Names = [new(provenance, "Fillmore")],
            Locations =
            [
                new(
                    provenance,
                    "San Francisco, CA",
                    "US",
                    "CA",
                    "San Francisco",
                    address,
                    37.77,
                    -122.43,
                    "city",
                    null,
                    null
                ),
            ],
            ExternalIdentities = identifiers
                .Select(value => new CandidateExternalIdentityFact(
                    provenance,
                    value.Provider,
                    value.ResourceType,
                    value.ProviderKey
                ))
                .ToArray(),
            Parents = parent == null ? [] : [new(VenueUuid, parent, parent, [], [])],
        };
        return new(
            new ProviderVenueObservation
            {
                Names = [new("Fillmore", Evidence)],
                OriginalLocality = new("San Francisco, CA", Evidence),
                Address = address == null ? null : new(address, Evidence),
                Identities = identifiers
                    .Select(value => new ObservedProviderIdentity(
                        new(value.Provider, value.ResourceType, value.ProviderKey),
                        Evidence
                    ))
                    .ToArray(),
            },
            VenueResolutionSourceScope.Imported,
            source,
            [],
            [candidate],
            [new IdentityCandidateMatcher().CompareVenues(source, target)],
            true,
            VenueResolutionPolicyEvaluator.ProcessorVersion,
            VenueResolutionPolicyEvaluator.SupportedSourceContextVersion,
            NormalizationCandidateService.FactsVersion,
            VenueResolutionPolicyEvaluator.MatcherVersion,
            new(new(source, [Evidence]), [new(target, [Evidence])])
        );
    }

    private static RegisteredVenueAcceptancePolicy Policy()
    {
        var artifact = new VenueAcceptancePolicyArtifact(
            Guid.NewGuid(),
            "venue-physical-rule-v1",
            "",
            VenueResolutionPolicyEvaluator.ProcessorVersion,
            VenueResolutionPolicyEvaluator.SupportedSourceContextVersion,
            NormalizationCandidateService.FactsVersion,
            VenueResolutionPolicyEvaluator.MatcherVersion,
            VenueResolutionPolicyEvaluator.PhysicalIdentityRuleId
        );
        return new(
            artifact,
            new(
                Guid.NewGuid(),
                artifact.Uuid,
                "",
                "",
                "",
                "",
                "",
                "behavior-tests-only",
                "test",
                "test",
                new(1, 0)
            )
        );
    }
}
