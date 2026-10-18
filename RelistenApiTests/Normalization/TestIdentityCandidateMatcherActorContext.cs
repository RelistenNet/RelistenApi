using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace RelistenApiTests.Normalization;

/// <summary>Nonexclusive source hints retrieve pairs without granting identity or account authority.</summary>
[TestFixture]
public sealed class TestIdentityCandidateMatcherActorContext
{
    private readonly IdentityCandidateMatcher matcher = new();

    [Test]
    public void SharedContactMakesDisjointNamesComparableWithoutAnAutomaticRule()
    {
        var contact = new PeopleContactLookup("email", "crew@example.test");
        var claim = Person("Alice Vale") with { ActorContext = new([contact], []) };
        var candidate = Person("Night Owl") with { ActorContext = new([contact], []) };

        var comparison = matcher.ComparePeople(claim, candidate, AllPeopleRules());

        comparison.IsCandidate.Should().BeTrue();
        comparison.NameTokenSimilarity.Should().Be(0);
        Feature(comparison, "actor_contact").Should().Be(IdentityFeatureAgreement.Agrees);
        AssertNoHintAuthority(comparison);
        claim.Kind.Should().BeNull();
        candidate.Kind.Should().BeNull();
    }

    [TestCase("phone", "crew@example.test")]
    [TestCase("email", "Crew@example.test")]
    public void ContactTypeAndOpaqueLookupKeyMustBothAgree(string type, string key)
    {
        var claim = Person("Alice Vale") with
        {
            ActorContext = new([new("email", "crew@example.test")], []),
        };
        var candidate = Person("Night Owl") with { ActorContext = new([new(type, key)], []) };

        var comparison = matcher.ComparePeople(claim, candidate);

        comparison.IsCandidate.Should().BeFalse();
        Feature(comparison, "actor_contact").Should().Be(IdentityFeatureAgreement.Differs);
        comparison.Disposition.Should().Be(IdentityMatchDisposition.NotCandidate);
    }

    [Test]
    public void SharedCrewContactKeepsSeveralPeopleEligibleAsRivals()
    {
        var context = new PersonActorMatchContext([new("email", "crew@example.test")], []);
        var claim = Person("Alice Vale") with { ActorContext = context };
        var candidates = new[] { Person("Night Owl"), Person("Bob Reed") }
            .Select(person => person with { ActorContext = context })
            .ToArray();

        var comparisons = candidates
            .Select(candidate => matcher.ComparePeople(claim, candidate, AllPeopleRules()))
            .ToArray();

        comparisons.Should().OnlyContain(comparison => comparison.IsCandidate);
        comparisons.Select(comparison => comparison.CandidateUuid).Distinct().Should().HaveCount(2);
        foreach (var comparison in comparisons)
        {
            AssertNoHintAuthority(comparison);
        }
    }

    [Test]
    public void ProfileRelationshipDoesNotTurnASharedAccountIntoAnIdentifier()
    {
        var claim = Person("Alice Vale") with
        {
            ActorContext = new(
                [],
                [
                    new(
                        "archive.org",
                        "account",
                        "night_owl",
                        ActorProfileRelationship.AttributedProfile
                    ),
                ]
            ),
        };
        var candidate = Person("Night Owl") with
        {
            ActorContext = new(
                [],
                [
                    new(
                        "ARCHIVE.ORG",
                        "account",
                        "night_owl",
                        ActorProfileRelationship.ExplicitOwnerStatement
                    ),
                ]
            ),
        };

        var comparison = matcher.ComparePeople(claim, candidate, AllPeopleRules());

        comparison.IsCandidate.Should().BeTrue();
        Feature(comparison, "actor_profile").Should().Be(IdentityFeatureAgreement.Agrees);
        Feature(comparison, "actor_profile_relationship")
            .Should()
            .Be(IdentityFeatureAgreement.Differs);
        AssertNoHintAuthority(comparison);
    }

    [TestCase("etree.org", "account", "night_owl")]
    [TestCase("archive.org", "project", "night_owl")]
    [TestCase("archive.org", "account", "Night_Owl")]
    public void ProfileAgreementRequiresProviderNamespaceAndExactOpaqueKey(
        string provider,
        string profileNamespace,
        string key
    )
    {
        var claim = Person("Alice Vale") with
        {
            ActorContext = new([], [new("archive.org", "account", "night_owl")]),
        };
        var candidate = Person("Bob Reed") with
        {
            ActorContext = new([], [new(provider, profileNamespace, key)]),
        };

        var comparison = matcher.ComparePeople(claim, candidate);

        comparison.IsCandidate.Should().BeFalse();
        Feature(comparison, "actor_profile").Should().Be(IdentityFeatureAgreement.Differs);
        comparison
            .Features.Should()
            .NotContain(feature => feature.Agreement == IdentityFeatureAgreement.Conflicts);
    }

    [Test]
    public void DistinctNonexclusiveContactsDoNotContradictAnOrdinaryNameMatch()
    {
        var claim = Person("Alice Vale") with
        {
            ActorContext = new([new("email", "alice@example.test")], []),
        };
        var candidate = Person("Alice Vale") with
        {
            ActorContext = new([new("email", "crew@example.test")], []),
        };

        var comparison = matcher.ComparePeople(claim, candidate);

        comparison.IsCandidate.Should().BeTrue();
        comparison.Disposition.Should().Be(IdentityMatchDisposition.NeedsEvidence);
        Feature(comparison, "actor_contact").Should().Be(IdentityFeatureAgreement.Differs);
        Feature(comparison, "person_kind").Should().Be(IdentityFeatureAgreement.Missing);
    }

    [Test]
    public void ExplicitAliasIsComparedAsAnOrdinaryName()
    {
        var claim = Person("Alice Vale") with { Names = [new("Alice Vale"), new("Night Owl")] };

        var comparison = matcher.ComparePeople(claim, Person("Night Owl"), AllPeopleRules());

        Feature(comparison, "name").Should().Be(IdentityFeatureAgreement.Agrees);
        comparison.NameTokenSimilarity.Should().Be(1);
        AssertNoHintAuthority(comparison);
    }

    [Test]
    public void MissingCandidateContextCannotBecomeAnEmptySetAgreement()
    {
        var claim = Person("Alice Vale") with
        {
            ActorContext = new([new("email", "crew@example.test")], []),
        };

        var comparison = matcher.ComparePeople(claim, Person("Bob Reed"));

        comparison.IsCandidate.Should().BeFalse();
        Feature(comparison, "actor_contact").Should().Be(IdentityFeatureAgreement.Missing);
        Feature(comparison, "actor_profile").Should().Be(IdentityFeatureAgreement.Missing);
    }

    [Test]
    public void LegacyRecordsDoNotAcquireActorContextFeatures()
    {
        var comparison = matcher.ComparePeople(Person("Alice Vale"), Person("Alice Vale"));

        comparison
            .Features.Should()
            .NotContain(feature => feature.Name.StartsWith("actor_", StringComparison.Ordinal));
        comparison.Disposition.Should().Be(IdentityMatchDisposition.NeedsEvidence);
    }

    [TestCase(true)]
    [TestCase(false)]
    public void AttachmentUncertaintyRemainsAnObservedMissingFact(bool claimSide)
    {
        var context = new PersonActorMatchContext([], [], HasAttachmentUncertainty: true);
        var claim = Person("Alice Vale") with { ActorContext = claimSide ? context : null };
        var candidate = Person("Alice Vale") with { ActorContext = claimSide ? null : context };

        var comparison = matcher.ComparePeople(claim, candidate);

        Feature(comparison, "actor_attachment_uncertainty")
            .Should()
            .Be(IdentityFeatureAgreement.Missing);
        comparison.Disposition.Should().Be(IdentityMatchDisposition.NeedsEvidence);
        comparison.SupportedRules.Should().BeEmpty();
    }

    [Test]
    public void ConflictingExplicitKindStaysReviewableWithoutInventingKind()
    {
        var claim = Person("Alice Vale") with
        {
            ActorContext = new([], [], HasConflictingKind: true),
        };

        var comparison = matcher.ComparePeople(claim, Person("Alice Vale"), AllPeopleRules());

        comparison.Disposition.Should().Be(IdentityMatchDisposition.Review);
        Feature(comparison, "actor_kind_conflict").Should().Be(IdentityFeatureAgreement.Conflicts);
        Feature(comparison, "person_kind").Should().Be(IdentityFeatureAgreement.Missing);
        comparison.SupportedRules.Should().BeEmpty();
        claim.Kind.Should().BeNull();
    }

    private static PersonMatchRecord Person(string name) =>
        new(Guid.NewGuid(), null, [new(name)], [], new HashSet<Guid>());

    private static IdentityFeatureAgreement Feature(
        IdentityCandidateComparison comparison,
        string name
    ) => comparison.Features.Single(feature => feature.Name == name).Agreement;

    private static void AssertNoHintAuthority(IdentityCandidateComparison comparison)
    {
        comparison.Disposition.Should().Be(IdentityMatchDisposition.NeedsEvidence);
        comparison.SupportedRules.Should().BeEmpty();
        comparison.ProposedRule.Should().BeNull();
        comparison.PolicyVersion.Should().BeNull();
        comparison.EvaluationReceiptUuid.Should().BeNull();
        Feature(comparison, "external_identifier").Should().Be(IdentityFeatureAgreement.Missing);
        Feature(comparison, "qualified_handle").Should().Be(IdentityFeatureAgreement.Missing);
    }

    private static EvaluationBackedIdentityPolicy AllPeopleRules() =>
        new(
            "actor-context-fixture",
            Guid.NewGuid(),
            IdentityMatchingDomain.People,
            Enum.GetValues<IdentityMatchRule>().ToHashSet()
        );
}
