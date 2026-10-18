using System.Text.Json;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace RelistenApiTests.Normalization;

/// <summary>Complete matching membership is independent of repeated source-account witnesses.</summary>
[TestFixture]
public sealed class TestPeopleActorMatchingAccumulator
{
    private const int FourMiB = 4 * 1024 * 1024;
    private static readonly Guid ArtistUuid = new("a055b56a-5a48-40dc-9c72-63e9d12033dc");

    [Test]
    public void RepeatedLargeSourceAccountsDoNotSpendSemanticCapacityOrHideLateFacts()
    {
        const int repetitions = 5001;
        var repeated = Facts(
            alias: "Night Owl",
            contact: "first@example.test",
            profile: "first_account",
            kind: "person",
            quote: new string('x', 4096)
        );
        ((long)JsonSerializer.SerializeToUtf8Bytes(repeated).Length * repetitions)
            .Should()
            .BeGreaterThan(FourMiB, "the original source payloads exceed the old byte budget");
        var accumulator = new PeopleActorMatchingAccumulator(8, FourMiB);

        for (var index = 0; index < repetitions; index++)
        {
            accumulator.Add("Alice Vale", repeated);
        }
        accumulator.Add(
            "River Crew",
            Facts(
                alias: "River Tape",
                contact: "late@example.test",
                profile: "late_account",
                kind: "group",
                issues: ["unattributed_profile", "conflicting_actor_kind"]
            )
        );
        var result = accumulator.Build();

        accumulator.OmittedKinds.Should().BeEmpty();
        result.HasActorContext.Should().BeTrue();
        result.CreditedNames.Should().BeEquivalentTo(new[] { "Alice Vale", "River Crew" });
        result.Aliases.Should().BeEquivalentTo(new[] { "Night Owl", "River Tape" });
        result
            .Contacts.Should()
            .BeEquivalentTo(
                new[]
                {
                    new PeopleContactLookup("email", "first@example.test"),
                    new PeopleContactLookup("email", "late@example.test"),
                }
            );
        result
            .Profiles.Select(profile => profile.LookupKey)
            .Should()
            .BeEquivalentTo(new[] { "first_account", "late_account" });
        result.SourceKinds.Should().BeEquivalentTo(new[] { "person", "group" });
        result.HasAttachmentUncertainty.Should().BeTrue();
        result.HasConflictingKind.Should().BeTrue();
        JsonSerializer.SerializeToUtf8Bytes(result).Length.Should().BeLessThanOrEqualTo(FourMiB);
    }

    [Test]
    public void EachDistinctSemanticSetReportsItsOwnOmissionAndStillRetainsLateUncertainty()
    {
        var accumulator = new PeopleActorMatchingAccumulator(1, FourMiB);
        accumulator.Add(
            "Alice Vale",
            Facts(
                alias: "Night Owl",
                contact: "first@example.test",
                profile: "first",
                kind: "person"
            )
        );

        accumulator.Add(
            "River Crew",
            Facts(
                alias: "River Tape",
                contact: "second@example.test",
                profile: "second",
                kind: "group",
                issues: ["ambiguous_actor_attachment", "conflicting_actor_kind"]
            )
        );
        var result = accumulator.Build();

        accumulator
            .OmittedKinds.Should()
            .BeEquivalentTo(new[] { "names", "aliases", "contacts", "profiles", "source_kinds" });
        result.CreditedNames.Should().ContainSingle();
        result.Aliases.Should().ContainSingle();
        result.Contacts.Should().ContainSingle();
        result.Profiles.Should().ContainSingle();
        result.SourceKinds.Should().ContainSingle();
        result.HasAttachmentUncertainty.Should().BeTrue();
        result.HasConflictingKind.Should().BeTrue();
    }

    [Test]
    public void ByteOverflowIsExplicitAndCannotExceedTheSerializedAggregateBudget()
    {
        const int maximumBytes = 512;
        var accumulator = new PeopleActorMatchingAccumulator(10, maximumBytes);
        var oversizedName = new string('A', 4096);

        accumulator.Add(
            oversizedName,
            Facts(issues: ["unattributed_contact", "conflicting_actor_kind"])
        );
        accumulator.Add("Bob Reed", Facts());
        var result = accumulator.Build();

        accumulator.OmittedKinds.Should().Equal("bytes");
        result.CreditedNames.Should().Equal("Bob Reed");
        result.HasActorContext.Should().BeTrue();
        result.HasAttachmentUncertainty.Should().BeTrue();
        result.HasConflictingKind.Should().BeTrue();
        JsonSerializer
            .SerializeToUtf8Bytes(result)
            .Length.Should()
            .BeLessThanOrEqualTo(maximumBytes);
    }

    [Test]
    public void DifferentSourceKindsDoNotInventAConflictOrReplaceCanonicalCandidateKind()
    {
        var accumulator = new PeopleActorMatchingAccumulator(10, FourMiB);
        accumulator.Add("Alice Vale", Facts(kind: "person"));
        accumulator.Add("River Crew", Facts(kind: "group"));
        var aggregate = accumulator.Build();
        var candidate = Candidate() with { ActorMatchingFacts = aggregate, ActorEvidence = [] };

        var projected = PeopleActorProjection.Candidate(candidate);
        var comparison = new IdentityCandidateMatcher().ComparePeople(
            Claim("Alice Vale"),
            projected
        );

        aggregate.SourceKinds.Should().BeEquivalentTo(new[] { "person", "group" });
        aggregate.HasConflictingKind.Should().BeFalse();
        projected.Kind.Should().Be("person");
        comparison.Features.Should().NotContain(feature => feature.Name == "actor_kind_conflict");
        comparison
            .Features.Single(feature => feature.Name == "person_kind")
            .Agreement.Should()
            .Be(IdentityFeatureAgreement.Agrees);
    }

    [Test]
    public void CompleteAggregateWithoutRawWitnessesPreservesFullCandidateComparison()
    {
        var first = Facts(
            alias: "Night Owl",
            contact: "first@example.test",
            profile: "shared_account",
            kind: "person"
        );
        var second = Facts(
            alias: "River Tape",
            contact: "second@example.test",
            profile: "shared_account",
            kind: "group",
            relationship: ActorProfileRelationship.ExplicitOwnerStatement,
            issues: ["unattributed_contact"]
        );
        var original = Candidate() with
        {
            ActorEvidence = [Account("Alice Vale", first), Account("River Crew", second)],
        };
        var accumulator = new PeopleActorMatchingAccumulator(10, FourMiB);
        accumulator.Add("Alice Vale", first);
        accumulator.Add("River Crew", second);
        var bounded = original with
        {
            ActorMatchingFacts = accumulator.Build(),
            ActorEvidence = [],
        };
        var claim = Claim("River Tape") with
        {
            ActorContext = new(
                [new("email", "second@example.test")],
                [
                    new(
                        "archive.org",
                        "account",
                        "shared_account",
                        ActorProfileRelationship.ExplicitOwnerStatement
                    ),
                ]
            ),
        };
        var matcher = new IdentityCandidateMatcher();

        var full = matcher.ComparePeople(claim, PeopleActorProjection.Candidate(original));
        var compact = matcher.ComparePeople(claim, PeopleActorProjection.Candidate(bounded));

        accumulator.OmittedKinds.Should().BeEmpty();
        bounded.ActorMatchingFacts!.Profiles.Should().HaveCount(2);
        compact.Should().BeEquivalentTo(full);
        compact.NameTokenSimilarity.Should().Be(1);
        foreach (
            var name in new[]
            {
                "name",
                "actor_contact",
                "actor_profile",
                "actor_profile_relationship",
            }
        )
        {
            compact
                .Features.Single(feature => feature.Name == name)
                .Agreement.Should()
                .Be(IdentityFeatureAgreement.Agrees);
        }
        compact
            .Features.Single(feature => feature.Name == "actor_attachment_uncertainty")
            .Agreement.Should()
            .Be(IdentityFeatureAgreement.Missing);
    }

    [Test]
    public void NoActorAccountsAndAnObservedEmptyAccountRemainDifferentContexts()
    {
        var accumulator = new PeopleActorMatchingAccumulator(10, FourMiB);
        var candidate = Candidate();
        var absent = accumulator.Build();

        var absentProjection = PeopleActorProjection.Candidate(
            candidate with
            {
                ActorMatchingFacts = absent,
                ActorEvidence = [],
            }
        );
        accumulator.Add("Alice Vale", Facts());
        var observed = accumulator.Build();
        var observedProjection = PeopleActorProjection.Candidate(
            candidate with
            {
                ActorMatchingFacts = observed,
                ActorEvidence = [],
            }
        );
        var matcher = new IdentityCandidateMatcher();
        var claim = Claim("Alice Vale");

        absent.HasActorContext.Should().BeFalse();
        absentProjection.ActorContext.Should().BeNull();
        matcher
            .ComparePeople(claim, absentProjection)
            .Features.Should()
            .NotContain(feature => feature.Name.StartsWith("actor_", StringComparison.Ordinal));
        observed.HasActorContext.Should().BeTrue();
        observedProjection.ActorContext.Should().NotBeNull();
        observedProjection.ActorContext!.Contacts.Should().BeEmpty();
        observedProjection.ActorContext.Profiles.Should().BeEmpty();
        matcher
            .ComparePeople(claim, observedProjection)
            .Features.Single(feature => feature.Name == "actor_contact")
            .Agreement.Should()
            .Be(IdentityFeatureAgreement.Missing);
    }

    private static CreditedActorFacts<NormalizedEvidenceReference> Facts(
        string? alias = null,
        string? contact = null,
        string? profile = null,
        string? kind = null,
        string quote = "Alice Vale",
        ActorProfileRelationship relationship = ActorProfileRelationship.AttributedProfile,
        string[]? issues = null
    )
    {
        var evidence = new NormalizedEvidenceReference(
            Guid.NewGuid(),
            "/taper",
            0,
            quote.Length,
            quote
        );
        return new(
            "recording-actor-evidence-v1",
            Guid.NewGuid(),
            Guid.NewGuid(),
            evidence,
            alias == null ? [] : [new(Guid.NewGuid(), Guid.NewGuid(), alias, [evidence])],
            profile == null
                ? []
                :
                [
                    new(
                        Guid.NewGuid(),
                        profile,
                        "archive.org",
                        "account",
                        profile,
                        relationship,
                        [evidence]
                    ),
                ],
            contact == null ? [] : [new(Guid.NewGuid(), contact, "email", contact, [evidence])],
            [],
            kind == null ? null : new(kind, [evidence]),
            [],
            [],
            (issues ?? [])
                .Select(reason => new ActorEvidenceUnresolved<NormalizedEvidenceReference>(
                    reason,
                    [],
                    [evidence]
                ))
                .ToArray()
        );
    }

    private static PersonCandidateFacts Candidate()
    {
        var personUuid = Guid.NewGuid();
        var ownerUuid = Guid.NewGuid();
        var header = new CandidateIdentityHeader(personUuid, "Alice Vale", 1, ownerUuid, "person");
        return new()
        {
            Hit = new(personUuid, 1, header.DisplayText, ownerUuid, 1, ["name"]),
            Header = header,
            IdentityClosure = [header],
            ArtistContexts = [new(ArtistUuid, 2, [], [], false)],
        };
    }

    private static PersonMatchRecord Claim(string name) =>
        new(null, "person", [new(name)], [], new HashSet<Guid> { ArtistUuid });

    private static CandidateActorEvidenceFact Account(
        string name,
        CreditedActorFacts<NormalizedEvidenceReference> facts
    ) =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            "provider_assertion",
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            new("archive.org", "item_metadata", "synthetic-actor"),
            Guid.NewGuid(),
            Guid.NewGuid(),
            facts.Anchor.InputRevisionUuid,
            Guid.NewGuid(),
            Guid.NewGuid(),
            "validated_match",
            name,
            facts
        );
}
