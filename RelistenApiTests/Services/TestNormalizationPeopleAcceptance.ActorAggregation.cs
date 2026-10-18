using System.Text.Json;
using Dapper;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Data;

namespace RelistenApiTests.Services;

public sealed partial class TestNormalizationPeopleAcceptance
{
    [Test]
    public async Task NonPeopleFactsRetainTheirV3VersionAndOriginalLimitsSerialization()
    {
        var snapshot = await candidates.FindVenueFactsAsync(
            new(IdentityMatchingDomain.Venues, [UniqueName()], [])
        );

        snapshot.Version.Should().Be(NormalizationCandidateService.FactsVersion);
        snapshot.Limits.MaximumActorMatchingBytes.Should().BeNull();
        JsonSerializer.Serialize(snapshot.Limits).Should().NotContain("MaximumActorMatchingBytes");
    }

    [Test]
    public async Task RepeatedAccountsBeyondOldCountAndByteBoundsKeepCompleteMatchingFacts()
    {
        await ActivatePolicy();
        var contact = RandomWord() + "@example.test";
        var published = await PublishActorContext($"({contact})");
        await SeedRepeatedActorAccounts(published.PersonUuid, 5001);
        var size = await fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<ActorAccountSize>(
                """
                SELECT count(*) AS Count,sum(octet_length(facts::text))::bigint AS Bytes
                FROM normalization.recording_actor_contexts
                WHERE person_uuid=@person AND retired_work_uuid IS NULL
                """,
                new { person = published.PersonUuid }
            )
        );
        size.Count.Should().Be(5002);
        size.Bytes.Should().BeGreaterThan(4 * 1024 * 1024);

        var snapshot = await candidates.FindPeopleFactsAsync(
            new(IdentityMatchingDomain.People, [published.Name], []),
            new() { MaximumActorContexts = 2, MaximumActorEvidenceBytes = 1024 }
        );

        snapshot.Version.Should().Be(NormalizationCandidateService.PeopleFactsVersion);
        snapshot.Complete.Should().BeTrue();
        var fact = snapshot.Facts.Single();
        fact.ActorContextCount.Should().Be(5002);
        fact.ActorEvidence!.Count.Should().BeLessThanOrEqualTo(2);
        fact.ActorEvidenceTruncated.Should().BeTrue();
        fact.ActorMatchingFacts!.Contacts.Should()
            .ContainSingle()
            .Which.LookupKey.Should()
            .Be(contact);
        fact.ActorMatchingFacts.CreditedNames.Should().Equal(published.Name);
        snapshot.Issues.Should().NotContain(issue => issue.PreventsAutomaticAcceptance);
    }

    [Test]
    public async Task UnwitnessedAccountStillSuppliesUniqueAliasContactAndOriginalOwnerClock()
    {
        await ActivatePolicy();
        var name = UniqueName();
        var person = await Person(name);
        var firstAlias = UniqueName();
        var secondAlias = UniqueName();
        var firstContact = RandomWord() + "@example.test";
        var secondContact = RandomWord() + "@example.test";
        await PublishActorContext($"aka {firstAlias} ({firstContact})", person, name);
        await PublishActorContext($"aka {secondAlias} ({secondContact})", person, name);

        var snapshot = await ReadAggregate(name);

        snapshot.Complete.Should().BeTrue();
        var fact = snapshot.Facts.Single();
        fact.ActorEvidence.Should().ContainSingle();
        fact.ActorEvidenceTruncated.Should().BeTrue();
        fact.ActorMatchingFacts!.Aliases.Should().BeEquivalentTo(new[] { firstAlias, secondAlias });
        fact.ActorMatchingFacts.Contacts.Select(value => value.LookupKey)
            .Should()
            .BeEquivalentTo(new[] { firstContact, secondContact });
        snapshot.ReadSet.OwnerObservations.Count.Should().BeGreaterThan(2);
    }

    [Test]
    public async Task EditorialChangeOnAnUnwitnessedOwnerInvalidatesTheOriginalSnapshot()
    {
        await ActivatePolicy();
        var name = UniqueName();
        var person = await Person(name);
        var first = await PublishActorContext($"({RandomWord()}@example.test)", person, name);
        var second = await PublishActorContext($"({RandomWord()}@example.test)", person, name);
        var snapshot = await ReadAggregate(name);
        var witnessed = snapshot.Facts.Single().ActorEvidence!.Single().SourceUuid;
        var omitted = first.Source.SourceUuid == witnessed ? second : first;
        var owner = (
            await fixture.Work.GetSubjectAsync(
                new NormalizationSubjectKey("people", "recording", omitted.Source.SourceUuid)
            )
        )!;
        snapshot
            .ReadSet.OwnerObservations.Should()
            .Contain(value => value.SubjectUuid == owner.SubjectUuid);
        await fixture.Work.ApplyReviewAsync(
            new(
                Guid.NewGuid(),
                owner.SubjectUuid,
                owner.EditorialRevision,
                "accept",
                JsonSerializer.SerializeToElement(new { note = "Unwitnessed source owner review" }),
                "Reconsider original actor support",
                Operator
            ),
            (_, _, _) => Task.CompletedTask
        );

        var current = await ReadAggregate(name);

        current.ReadSet.CandidateUniverses.Should().Equal(snapshot.ReadSet.CandidateUniverses);
        current
            .ReadSet.OwnerObservations.Single(value => value.SubjectUuid == owner.SubjectUuid)
            .EditorialRevision.Should()
            .Be(owner.EditorialRevision + 1);
        await AdmitAggregate(snapshot).Should().ThrowAsync<NormalizationConflictException>();
    }

    [Test]
    public async Task RetractingUnwitnessedContributorRemovesItsMatchingFactsAndStalesOldSnapshot()
    {
        await ActivatePolicy();
        var name = UniqueName();
        var person = await Person(name);
        var firstContact = RandomWord() + "@example.test";
        var secondContact = RandomWord() + "@example.test";
        var first = await PublishActorContext($"({firstContact})", person, name);
        var second = await PublishActorContext($"({secondContact})", person, name);
        var snapshot = await ReadAggregate(name);
        var witnessed = snapshot.Facts.Single().ActorEvidence!.Single().SourceUuid;
        var omitted = first.Source.SourceUuid == witnessed ? second : first;
        var omittedContact = first.Source.SourceUuid == witnessed ? secondContact : firstContact;
        await SetTaper(omitted.Source, "");
        var resolution = await Resolve(omitted.Source);
        var application = await applier.ApplyRecordingAsync(Guid.NewGuid(), resolution.AttemptUuid);
        application.Result!.Disposition.Should().Be(PeopleAcceptanceDisposition.Applied);

        var current = await ReadAggregate(name);

        current.Complete.Should().BeTrue();
        current.Facts.Single().ActorContextCount.Should().Be(1);
        current
            .Facts.Single()
            .ActorMatchingFacts!.Contacts.Should()
            .NotContain(value => value.LookupKey == omittedContact);
        await AdmitAggregate(snapshot).Should().ThrowAsync<NormalizationConflictException>();
    }

    [Test]
    public async Task DistinctMatchingByteOverflowStillBlocksWhileWitnessByteOmissionDoesNot()
    {
        await ActivatePolicy();
        var published = await PublishActorContext(
            $"aka {UniqueName()} ({RandomWord()}@example.test)"
        );

        var snapshot = await candidates.FindPeopleFactsAsync(
            new(IdentityMatchingDomain.People, [published.Name], []),
            new() { MaximumActorMatchingBytes = 256, MaximumActorEvidenceBytes = 1 }
        );

        snapshot.Complete.Should().BeFalse();
        snapshot.Facts.Single().ActorEvidence.Should().BeEmpty();
        snapshot
            .Issues.Should()
            .Contain(issue =>
                issue.Kind == CandidateFactsIssueKind.EvidenceLimit
                && issue.FactKind == "recording_actor_matching_bytes"
            );
        JsonSerializer
            .SerializeToUtf8Bytes(snapshot.Facts.Single().ActorMatchingFacts)
            .Length.Should()
            .BeLessThanOrEqualTo(256);
    }

    private Task<NormalizationCandidateFactsSnapshot<PersonCandidateFacts>> ReadAggregate(
        string name
    ) =>
        candidates.FindPeopleFactsAsync(
            new(IdentityMatchingDomain.People, [name], []),
            new() { MaximumActorContexts = 1, MaximumWitnessesPerArtist = 0 }
        );

    private Func<Task<NormalizationWorkTicket>> AdmitAggregate(
        NormalizationCandidateFactsSnapshot<PersonCandidateFacts> snapshot
    ) =>
        () =>
            fixture.Work.BeginWorkAsync(
                Guid.NewGuid(),
                new("people", "person", Guid.NewGuid()),
                "aggregate-facts-fixture-v1",
                [],
                [],
                catalogInputs: snapshot.ReadSet.CatalogInputs,
                candidateUniverses: snapshot.ReadSet.CandidateUniverses,
                expectedOwnerObservations: snapshot.ReadSet.OwnerObservations
            );

    private Task SeedRepeatedActorAccounts(Guid person, int copies) =>
        fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                """
                WITH original AS (
                    SELECT * FROM normalization.recording_actor_contexts
                    WHERE person_uuid=@person AND retired_work_uuid IS NULL ORDER BY uuid LIMIT 1
                )
                INSERT INTO normalization.recording_actor_contexts(
                    support_uuid,credit_uuid,credit_revision,credit_basis,person_uuid,source_uuid,owner_subject_uuid,
                    contributor_provider,contributor_type,contributor_key,extraction_attempt_uuid,proposal_uuid,
                    input_revision_uuid,application_attempt_uuid,introduced_work_uuid,basis,credited_name,facts_hash,facts)
                SELECT o.support_uuid,o.credit_uuid,o.credit_revision,o.credit_basis,o.person_uuid,o.source_uuid,o.owner_subject_uuid,
                    o.contributor_provider,o.contributor_type,o.contributor_key,o.extraction_attempt_uuid,gen_random_uuid(),
                    o.input_revision_uuid,o.application_attempt_uuid,o.introduced_work_uuid,o.basis,o.credited_name,o.facts_hash,o.facts
                FROM original o CROSS JOIN generate_series(1,@copies)
                """,
                new { person, copies }
            )
        );

    private sealed record ActorAccountSize(long Count, long Bytes);
}
