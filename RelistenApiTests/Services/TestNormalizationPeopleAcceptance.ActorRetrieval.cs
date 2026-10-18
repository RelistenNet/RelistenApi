using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
using CatalogSource = RelistenApiTests.Services.NormalizationDomainFixture.CatalogSource;

namespace RelistenApiTests.Services;

public sealed partial class TestNormalizationPeopleAcceptance
{
    [Test]
    public async Task NonexclusiveContactAndProfileLookupsReturnAllPeopleWithoutNameOverlap()
    {
        await ActivatePolicy();
        var contact = RandomWord() + "@example.test";
        var profile = RandomWord();
        var first = await PublishActorContext(
            $"({contact}) (https://archive.org/details/@{profile})"
        );
        var second = await PublishActorContext(
            $"({contact}) (https://archive.org/details/@{profile})"
        );

        var snapshot = await candidates.FindPeopleFactsAsync(
            new(
                IdentityMatchingDomain.People,
                [],
                [],
                NameLimit: 1,
                PeopleHints: new([new("email", contact)], [new("archive.org", "account", profile)])
            )
        );

        snapshot.Complete.Should().BeTrue();
        snapshot
            .Facts.Select(fact => fact.Hit.Uuid)
            .Should()
            .BeEquivalentTo(new[] { first.PersonUuid, second.PersonUuid });
        snapshot
            .Facts.Should()
            .OnlyContain(fact =>
                fact.Hit.Reasons.Contains("recording_contact")
                && fact.Hit.Reasons.Contains("recording_profile")
            );
        snapshot.Facts.Should().OnlyContain(fact => fact.ExternalIdentities.Count == 0);
        foreach (var fact in snapshot.Facts)
        {
            fact.ActorEvidence.Should().ContainSingle();
            fact.ActorEvidence!.Single()
                .Facts.Contacts.Should()
                .Contain(hint => hint.LookupKey == contact);
            fact.ActorEvidence.Single()
                .Facts.Profiles.Should()
                .Contain(hint => hint.LookupKey == profile);
            snapshot
                .ReadSet.OwnerObservations.Should()
                .Contain(owner =>
                    owner.SubjectUuid == fact.ActorEvidence.Single().OwnerSubjectUuid
                );
        }
    }

    [Test]
    public async Task AcceptedSourceAliasRetrievesItsPersonAndFullEvidenceWithoutArtistWitnesses()
    {
        await ActivatePolicy();
        var alias = UniqueName();
        var published = await PublishActorContext("aka " + alias);

        var snapshot = await candidates.FindPeopleFactsAsync(
            new(IdentityMatchingDomain.People, [alias], []),
            new() { MaximumWitnessesPerArtist = 0, MaximumMissingContextWitnesses = 0 }
        );

        snapshot.Complete.Should().BeTrue();
        var fact = snapshot.Facts.Should().ContainSingle().Which;
        fact.Hit.Uuid.Should().Be(published.PersonUuid);
        fact.Hit.Reasons.Should().Contain("credited_name");
        fact.ArtistContexts.SelectMany(artist => artist.Witnesses).Should().BeEmpty();
        var actor = fact.ActorEvidence.Should().ContainSingle().Which;
        actor.CreditedName.Should().Be(published.Name);
        actor.StoredPersonUuid.Should().Be(published.PersonUuid);
        actor.SourceUuid.Should().Be(published.Source.SourceUuid);
        actor.Basis.Should().Be("validated_match");
        actor.Facts.Aliases.Should().Contain(value => value.Wording == alias);
        actor.Facts.Aliases.SelectMany(value => value.Evidence).Should().NotBeEmpty();
        snapshot
            .ReadSet.OwnerObservations.Should()
            .Contain(owner => owner.SubjectUuid == actor.OwnerSubjectUuid);
    }

    [Test]
    public async Task ContextLimitReportsTruncationInsteadOfPresentingOneSharedContactAsUnique()
    {
        await ActivatePolicy();
        var contact = RandomWord() + "@example.test";
        await PublishActorContext($"({contact})");
        await PublishActorContext($"({contact})");

        var snapshot = await candidates.FindPeopleFactsAsync(ContactQuery(contact, limit: 1));

        snapshot.Selection.Items.Should().ContainSingle();
        snapshot.Selection.NameCandidatesTruncated.Should().BeFalse();
        snapshot.Selection.ContextCandidatesTruncated.Should().BeTrue();
        snapshot.Complete.Should().BeFalse();
    }

    [TestCase("rows", "recording_actor_context")]
    [TestCase("contacts", "recording_actor_contacts")]
    [TestCase("evidence", "recording_actor_contacts")]
    public async Task FullActorEvidenceBoundsAreIndependentAndPreventAutomaticAcceptance(
        string bound,
        string factKind
    )
    {
        await ActivatePolicy();
        var name = UniqueName();
        var person = await Person(name);
        var contact = RandomWord() + "@example.test";
        await PublishActorContext($"({contact})", person, name);
        await PublishActorContext($"({RandomWord()}@example.test)", person, name);
        var limits = bound switch
        {
            "rows" => new CandidateFactsLimits { MaximumActorContexts = 1 },
            "contacts" => new CandidateFactsLimits { MaximumAssertionsPerKind = 1 },
            "evidence" => new CandidateFactsLimits { MaximumEvidencePerAssertion = 1 },
            _ => throw new ArgumentOutOfRangeException(nameof(bound)),
        };

        var snapshot = await candidates.FindPeopleFactsAsync(
            new(IdentityMatchingDomain.People, [name], []),
            limits
        );

        snapshot.Complete.Should().BeFalse();
        snapshot
            .Issues.Should()
            .Contain(issue =>
                issue.FactKind == factKind
                && issue.Kind
                    == (
                        bound == "evidence"
                            ? CandidateFactsIssueKind.EvidenceLimit
                            : CandidateFactsIssueKind.AssertionLimit
                    )
            );
        if (bound == "rows")
        {
            snapshot.Facts.Single().ActorEvidence.Should().ContainSingle();
        }
        else if (bound == "contacts")
        {
            snapshot
                .Facts.Single()
                .ActorEvidence!.SelectMany(actor => actor.Facts.Contacts)
                .Should()
                .ContainSingle();
        }
    }

    [Test]
    public async Task ActorEvidenceByteBudgetOmitsPayloadsButRetainsTheirOriginalOwnerObservations()
    {
        await ActivatePolicy();
        var published = await PublishActorContext($"({RandomWord()}@example.test)");
        var sourceOwner = await fixture.OwnerUuid(
            "people",
            "recording",
            published.Source.SourceUuid
        );

        var snapshot = await candidates.FindPeopleFactsAsync(
            new(IdentityMatchingDomain.People, [published.Name], []),
            new() { MaximumActorEvidenceBytes = 1, MaximumWitnessesPerArtist = 0 }
        );

        snapshot.Complete.Should().BeFalse();
        snapshot.Facts.Should().ContainSingle().Which.Hit.Uuid.Should().Be(published.PersonUuid);
        snapshot.Facts.Single().ActorEvidence.Should().BeEmpty();
        snapshot
            .Issues.Should()
            .Contain(issue =>
                issue.Kind == CandidateFactsIssueKind.EvidenceLimit
                && issue.FactKind == "recording_actor_bytes"
            );
        snapshot
            .Issues.Should()
            .NotContain(issue => issue.Kind == CandidateFactsIssueKind.InvalidStoredFact);
        snapshot
            .ReadSet.OwnerObservations.Should()
            .Contain(owner => owner.SubjectUuid == sourceOwner);
    }

    [Test]
    public async Task SourceContextSurvivesAReviewedIdentityMergeThroughItsOriginalPerson()
    {
        await ActivatePolicy();
        var contact = RandomWord() + "@example.test";
        var original = await PublishActorContext($"({contact})");
        var survivorName = UniqueName();
        var survivorUuid = await Person(survivorName);
        var before = await candidates.FindPeopleFactsAsync(ContactQuery(contact));
        var originalContext = before.Facts.Single().ActorEvidence!.Single();
        var originalOwner = (
            await fixture.Work.GetSubjectAsync(
                new NormalizationSubjectKey("people", "person", original.PersonUuid)
            )
        )!;
        var survivor = (
            await candidates.FindPeopleFactsAsync(
                new(IdentityMatchingDomain.People, [survivorName], [])
            )
        ).Facts.Single();
        var credit = (await people.ReadCreditContextAsync(original.Source.SourceUuid))
            .Credits.Single()
            .Credit;
        await people.ApplyIdentityDecisionAsync(
            new(
                Guid.NewGuid(),
                originalOwner.SubjectUuid,
                originalOwner.EditorialRevision,
                "merge",
                original.PersonUuid,
                before.Facts.Single().Header.Revision,
                new(survivorUuid, survivorName),
                survivor.Header.Revision,
                [credit.Uuid],
                "Confirm the two synthetic identities represent one actor"
            ),
            Operator
        );

        var snapshot = await candidates.FindPeopleFactsAsync(ContactQuery(contact));

        snapshot.Complete.Should().BeTrue();
        var fact = snapshot.Facts.Should().ContainSingle().Which;
        fact.Hit.Uuid.Should().Be(survivorUuid);
        fact.IdentityClosure.Select(identity => identity.Uuid)
            .Should()
            .Contain(new[] { original.PersonUuid, survivorUuid });
        var actor = fact.ActorEvidence.Should().ContainSingle().Which;
        actor.ContextUuid.Should().Be(originalContext.ContextUuid);
        actor.StoredPersonUuid.Should().Be(original.PersonUuid);
        snapshot
            .ReadSet.OwnerObservations.Should()
            .Contain(owner => owner.SubjectUuid == actor.OwnerSubjectUuid);
    }

    [Test]
    public async Task UnrelatedReviewedCreditReassignmentDoesNotTransferItsOldActorHints()
    {
        await ActivatePolicy();
        var contact = RandomWord() + "@example.test";
        var original = await PublishActorContext($"({contact})");
        var replacement = await Person(UniqueName());
        var context = await people.ReadCreditContextAsync(original.Source.SourceUuid);
        var claim = context.Credits.Single();
        var owner = context.RecordingOwnerObservation!;
        await people.ApplyDecisionAsync(
            new(
                Guid.NewGuid(),
                owner.SubjectUuid,
                owner.EditorialRevision,
                claim.Revision,
                "replace",
                claim.Credit with
                {
                    PersonUuid = replacement,
                },
                "Correct an unrelated synthetic namesake assignment"
            ),
            Operator
        );

        var snapshot = await candidates.FindPeopleFactsAsync(ContactQuery(contact));

        snapshot.Complete.Should().BeTrue();
        snapshot.Facts.Should().BeEmpty();
        snapshot.Selection.Items.Should().BeEmpty();
    }

    [Test]
    public async Task ContextLookupQueryIsFrozenAndParticipatesInItsFactsHash()
    {
        await ActivatePolicy();
        var contact = RandomWord() + "@example.test";
        var published = await PublishActorContext($"({contact})");
        var hints = new List<PeopleContactLookup> { new("email", contact) };
        var query = new NormalizationCandidateQuery(
            IdentityMatchingDomain.People,
            [],
            [],
            PeopleHints: new(hints, [])
        );
        var reading = candidates.FindPeopleFactsAsync(query);
        hints[0] = new("email", RandomWord() + "@example.test");

        var original = await reading;
        var changed = await candidates.FindPeopleFactsAsync(query);

        original.Query.PeopleHints!.Contacts.Single().LookupKey.Should().Be(contact);
        original.Facts.Should().ContainSingle().Which.Hit.Uuid.Should().Be(published.PersonUuid);
        changed.Facts.Should().BeEmpty();
        changed.QueryHash.Should().NotBe(original.QueryHash);
    }

    private static NormalizationCandidateQuery ContactQuery(string contact, int limit = 100) =>
        new(
            IdentityMatchingDomain.People,
            [],
            [],
            PeopleHints: new([new("email", contact)], [], limit)
        );

    private async Task<PublishedActorContext> PublishActorContext(
        string attachments,
        Guid? personUuid = null,
        string? name = null
    )
    {
        name ??= UniqueName();
        personUuid ??= await Person(name);
        var source = await Source(name + " " + attachments);
        var resolution = await Resolve(source);
        var application = await applier.ApplyRecordingAsync(Guid.NewGuid(), resolution.AttemptUuid);
        application
            .Result!.Disposition.Should()
            .Be(
                PeopleAcceptanceDisposition.Applied,
                "the synthetic source context must be accepted before retrieval: {0}",
                ApplicationAudit(application)
            );
        return new(personUuid.Value, name, source);
    }

    private sealed record PublishedActorContext(Guid PersonUuid, string Name, CatalogSource Source);
}
