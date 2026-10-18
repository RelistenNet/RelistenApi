using System.Text.Json;
using Dapper;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
using Relisten.Normalization;
using static RelistenApiTests.Services.TestNormalizationSourceActorAliasAuthority;

namespace RelistenApiTests.Services;

[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestNormalizationPeopleAliasFacts
{
    private NormalizationDomainFixture fixture = null!;
    private NormalizedPeopleService people = null!;
    private NormalizationCandidateService candidates = null!;

    [OneTimeSetUp]
    public void Start()
    {
        fixture = new();
        people = new(fixture.Db, fixture.Work);
        candidates = new(fixture.Db);
    }

    [OneTimeTearDown]
    public void Stop() => fixture?.Dispose();

    [Test]
    public async Task TenThousandDuplicateAssertionsKeepCompleteNamesWithBoundedWitnesses()
    {
        var person = await Person();
        await CloneAssertions(person.Uuid, 10000);

        var snapshot = await Read(person, new() { MaximumAssertionsPerKind = 2 });

        snapshot.Version.Should().Be("people-candidate-facts-v5");
        snapshot
            .Complete.Should()
            .BeTrue(
                "duplicate alias evidence must preserve complete matching facts; issues were {0}",
                JsonSerializer.Serialize(snapshot.Issues)
            );
        var facts = snapshot.Facts.Single();
        facts.AliasAssertionCount.Should().Be(10001);
        facts.AcceptedAliasMatchingNames.Should().Equal(person.DisplayName);
        facts.AliasMatchingNamesComplete.Should().BeTrue();
        facts.Aliases.Should().HaveCount(2);
        facts.AliasEvidenceTruncated.Should().BeTrue();
        snapshot.Issues.Should().NotContain(issue => issue.PreventsAutomaticAcceptance);
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task SemanticOverflowBlocksWhileWitnessByteOmissionDoesNot(bool names)
    {
        var person = await Person();
        await CloneAssertions(person.Uuid, 1, "Different alias " + Guid.NewGuid().ToString("N"));
        var limits = names
            ? new CandidateFactsLimits
            {
                MaximumAliasMatchingNames = 1,
                MaximumAliasEvidenceBytes = 1,
            }
            : new CandidateFactsLimits
            {
                MaximumAliasMatchingBytes = 1,
                MaximumAliasEvidenceBytes = 1,
            };

        var overflow = await Read(person, limits);

        overflow.Complete.Should().BeFalse();
        overflow.Facts.Single().AliasMatchingNamesComplete.Should().BeFalse();
        overflow.Facts.Single().Aliases.Should().BeEmpty();
        overflow.Facts.Single().AliasEvidenceTruncated.Should().BeTrue();
        overflow
            .Issues.Should()
            .Contain(issue =>
                issue.FactKind
                    == (names ? "person_alias_matching_names" : "person_alias_matching_bytes")
                && issue.PreventsAutomaticAcceptance
            );
        var complete = await Read(person, new() { MaximumAliasEvidenceBytes = 1 });
        complete.Complete.Should().BeTrue();
        complete.Facts.Single().AliasMatchingNamesComplete.Should().BeTrue();
        complete.Facts.Single().AcceptedAliasMatchingNames.Should().HaveCount(2);
        complete.Facts.Single().Aliases.Should().BeEmpty();
    }

    [Test]
    public async Task CompleteNamesSortOrdinalAndProjectionDoesNotFallBackToWitnesses()
    {
        var person = await Person();
        var supplied = new[] { "z", "A", "a", "\U00010000", "\uE000", "x\"\\\n" };
        foreach (var wording in supplied)
        {
            await CloneAssertions(person.Uuid, 1, wording);
        }
        var snapshot = await Read(person, new() { MaximumAliasEvidenceBytes = 1 });
        var facts = snapshot.Facts.Single();
        facts
            .AcceptedAliasMatchingNames.Should()
            .Equal(supplied.Append(person.DisplayName).Order(StringComparer.Ordinal));
        var candidate = PeopleActorProjection.Candidate(facts);
        candidate.Names.Select(name => name.Wording).Should().Contain(supplied);
        candidate.ArtistUuids.Should().BeEmpty();
        candidate.ActorContext.Should().BeNull();
    }

    [Test]
    public async Task NonPeopleLimitsAndHistoricalFactsOmitNewOptionalProperties()
    {
        var venue = await candidates.FindVenueFactsAsync(
            new(IdentityMatchingDomain.Venues, [Guid.NewGuid().ToString("N")], [])
        );
        JsonSerializer.Serialize(venue.Limits).Should().NotContain("MaximumAlias");
        var person = await Person();
        var current = (await Read(person)).Facts.Single();
        var old = current with
        {
            AcceptedAliasMatchingNames = null,
            AliasMatchingNamesComplete = null,
            AliasAssertionCount = null,
            AliasSupportCount = null,
            AliasEvidenceTruncated = null,
        };
        var json = JsonSerializer.Serialize(old);
        json.Should().NotContain("AcceptedAliasMatchingNames");
        json.Should().NotContain("AliasMatchingNamesComplete");
        json.Should().NotContain("AliasAssertionCount");
        json.Should().NotContain("AliasSupportCount");
        json.Should().NotContain("AliasEvidenceTruncated");
        PeopleActorProjection
            .Candidate(old)
            .Names.Select(name => name.Wording)
            .Should()
            .Contain(person.DisplayName);
    }

    [Test]
    public async Task AllThousandAndOneIndependentSupportsFenceAnUnwitnessedOwnersEditorialAndReceiptChanges()
    {
        var person = await Person();
        await CloneAssertions(person.Uuid, 1);
        var aliases = await fixture.Db.WithWriteConnection(c =>
            c.QueryAsync<Guid>(
                "SELECT uuid FROM normalization.person_aliases WHERE person_uuid=@Uuid ORDER BY uuid",
                person
            )
        );
        var aliasUuids = aliases.ToArray();
        var template = await PrepareSource(fixture);
        var sources = new List<PreparedSource> { template };
        for (var index = 1; index < 1001; index++)
        {
            sources.Add(await PrepareOwnerFixture(template));
        }
        var input = await fixture.Capture(person.DisplayName, "people");
        var publication = (await fixture.Own(input, "person", person.Uuid)).Ticket;
        (
            await fixture.Work.TryPublishAsync(
                publication.WorkUuid,
                async (c, t, _) =>
                {
                    for (var index = 0; index < sources.Count; index++)
                    {
                        await InsertSupport(
                            c,
                            t,
                            publication.WorkUuid,
                            person.Uuid,
                            aliasUuids[index % aliasUuids.Length],
                            sources[index],
                            Guid.NewGuid(),
                            Guid.NewGuid()
                        );
                    }
                },
                candidateWriteScopes: WriteScopes
            )
        ).Status.Should().Be(NormalizationPublicationStatus.Published);

        var snapshot = await Read(person, new() { MaximumAssertionsPerKind = 1 });

        snapshot.Complete.Should().BeTrue();
        var facts = snapshot.Facts.Single();
        facts.AliasSupportCount.Should().Be(1001);
        facts.AliasAssertionCount.Should().Be(2);
        facts.AcceptedAliasMatchingNames.Should().Equal(person.DisplayName);
        facts.Aliases.Should().ContainSingle();
        facts.AliasEvidenceTruncated.Should().BeTrue();
        var expected = sources
            .SelectMany(source => new[] { source.SourceOwner, source.ExtractionOwner })
            .ToArray();
        snapshot
            .ReadSet.OwnerObservations.Select(owner => owner.SubjectUuid)
            .Should()
            .Contain(expected);
        var missingAlias = aliasUuids.Single(uuid =>
            uuid != facts.Aliases.Single().Provenance.AssertionUuid
        );
        var omitted = sources[998 + Array.IndexOf(aliasUuids, missingAlias)];
        var owner = snapshot.ReadSet.OwnerObservations.Single(value =>
            value.SubjectUuid == omitted.SourceOwner
        );
        var admitted = await Admit(snapshot);
        await fixture.Work.ApplyReviewAsync(
            new(
                Guid.NewGuid(),
                owner.SubjectUuid,
                owner.EditorialRevision,
                "accept",
                JsonSerializer.SerializeToElement(new { fixture = "omitted support owner" }),
                "Independent source review invalidates the original alias observation",
                "fixture-operator"
            ),
            (_, _, _) => Task.CompletedTask
        );
        var staleAdmission = () => Admit(snapshot);
        await staleAdmission.Should().ThrowAsync<NormalizationConflictException>();
        (
            await fixture.Work.TryPublishAsync(
                admitted.WorkUuid,
                (_, _, _) => throw new AssertionException("Stale alias snapshot must not publish")
            )
        )
            .Status.Should()
            .Be(NormalizationPublicationStatus.Stale);

        var refreshed = await Read(person, new() { MaximumAssertionsPerKind = 1 });
        var extraction = refreshed.ReadSet.OwnerObservations.Single(value =>
            value.SubjectUuid == omitted.ExtractionOwner
        );
        var next = await fixture.Work.BeginWorkAsync(
            Guid.NewGuid(),
            new("extractions", "recording", omitted.SourceUuid),
            "fixture-storage-authority",
            [],
            [],
            expectedConfigurationGeneration: extraction.DesiredGeneration
        );
        (await fixture.Work.TryPublishAsync(next.WorkUuid, (_, _, _) => Task.CompletedTask))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);
        var now = (await Read(person)).ReadSet.OwnerObservations.Single(value =>
            value.SubjectUuid == omitted.ExtractionOwner
        );
        now.DesiredGeneration.Should().Be(extraction.DesiredGeneration);
        now.PublishedWorkUuid.Should().NotBeNull();
        (now.PublishedWorkUuid != extraction.PublishedWorkUuid).Should().BeTrue();
        var staleReceipt = () => Admit(refreshed);
        await staleReceipt.Should().ThrowAsync<NormalizationConflictException>();
    }

    [Test]
    public async Task MalformedActiveSupportBlocksFactsWithoutDroppingItsOriginalOwners()
    {
        var person = await Person();
        var input = await fixture.Capture(person.DisplayName, "people");
        var source = await PrepareSource(fixture);
        var ticket = (await fixture.Own(input, "person", person.Uuid)).Ticket;
        (
            await fixture.Work.TryPublishAsync(
                ticket.WorkUuid,
                (c, t, _) =>
                    InsertSupport(
                        c,
                        t,
                        ticket.WorkUuid,
                        person.Uuid,
                        person.Uuid,
                        source,
                        Guid.NewGuid(),
                        Guid.NewGuid(),
                        malformed: true
                    ),
                candidateWriteScopes: WriteScopes
            )
        )
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);

        var snapshot = await Read(person);

        snapshot.Complete.Should().BeFalse();
        snapshot
            .Issues.Should()
            .Contain(issue =>
                issue.Kind == CandidateFactsIssueKind.InvalidStoredFact
                && issue.FactKind == "person_alias_support_proof"
            );
        snapshot
            .ReadSet.OwnerObservations.Select(owner => owner.SubjectUuid)
            .Should()
            .Contain([source.SourceOwner, source.ExtractionOwner]);
    }

    [Test]
    public async Task RedirectAndReversalKeepOriginalAliasOwnershipAndCurrentResolvedNames()
    {
        var original = await Person();
        var survivor = await Person();
        var owner = (
            await fixture.Work.GetSubjectAsync(
                new NormalizationSubjectKey("people", "person", original.Uuid)
            )
        )!;
        var survivorOwner = (
            await fixture.Work.GetSubjectAsync(
                new NormalizationSubjectKey("people", "person", survivor.Uuid)
            )
        )!;
        var operation = Guid.NewGuid();
        await people.ApplyIdentityDecisionAsync(
            new(
                operation,
                owner.SubjectUuid,
                owner.EditorialRevision,
                "merge",
                original.Uuid,
                await Revision(original.Uuid),
                survivor,
                await Revision(survivor.Uuid),
                [],
                "Same fixture identity retains original alias owner"
            ),
            "fixture-operator"
        );

        var merged = await Read(original);

        merged.Facts.Should().ContainSingle().Which.Hit.Uuid.Should().Be(survivor.Uuid);
        merged
            .Facts.Single()
            .AcceptedAliasMatchingNames.Should()
            .Contain([original.DisplayName, survivor.DisplayName]);
        merged
            .ReadSet.OwnerObservations.Select(value => value.SubjectUuid)
            .Should()
            .Contain([owner.SubjectUuid, survivorOwner.SubjectUuid]);
        var mergedOwner = (await fixture.Work.GetSubjectAsync(owner.SubjectUuid))!;
        await people.ReverseMergeAsync(
            new(
                Guid.NewGuid(),
                owner.SubjectUuid,
                mergedOwner.EditorialRevision,
                operation,
                original.Uuid,
                survivor.Uuid,
                await Revision(original.Uuid),
                await Revision(survivor.Uuid),
                "Reverse fixture identity merge"
            ),
            "fixture-operator"
        );
        var reversed = await Read(original);
        reversed.Facts.Should().ContainSingle().Which.Hit.Uuid.Should().Be(original.Uuid);
        reversed.Facts.Single().AcceptedAliasMatchingNames.Should().Contain(original.DisplayName);
        reversed
            .Facts.Single()
            .AcceptedAliasMatchingNames.Should()
            .NotContain(survivor.DisplayName);
    }

    private Task<long> Revision(Guid uuid) =>
        fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<long>(
                "SELECT revision FROM normalization.people WHERE uuid=@uuid",
                new { uuid }
            )
        );

    private Task<NormalizationWorkTicket> Admit(
        NormalizationCandidateFactsSnapshot<PersonCandidateFacts> snapshot
    ) =>
        fixture.Work.BeginWorkAsync(
            Guid.NewGuid(),
            new("people", "person", Guid.NewGuid()),
            "fixture-alias-reader",
            [],
            [],
            catalogInputs: snapshot.ReadSet.CatalogInputs,
            candidateUniverses: snapshot.ReadSet.CandidateUniverses,
            expectedOwnerObservations: snapshot.ReadSet.OwnerObservations
        );

    // Scale fixture only: retain the schema's typed coordinates under real published Work
    // owners. The copied metadata JSON does not authenticate a producer or policy decision.
    // The real public processor supplies the initial retained input/fetch used by this cohort.
    private async Task<PreparedSource> PrepareOwnerFixture(PreparedSource template)
    {
        var source = Guid.NewGuid();
        var extraction = await fixture.Work.BeginWorkAsync(
            Guid.NewGuid(),
            new("extractions", "recording", source),
            "fixture-storage-authority",
            [],
            []
        );
        (await fixture.Work.TryPublishAsync(extraction.WorkUuid, (_, _, _) => Task.CompletedTask))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);
        var resolution = await fixture.Work.BeginWorkAsync(
            Guid.NewGuid(),
            new("people", "source_actor_resolution", source),
            "fixture-storage-authority",
            [],
            []
        );
        (await fixture.Work.TryPublishAsync(resolution.WorkUuid, (_, _, _) => Task.CompletedTask))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                """
                INSERT INTO normalization.recording_processing_attempts(attempt_uuid,source_uuid,processing_version,
                    preparation_fetch_uuid,preparation,disposition,reason,batch,extraction_work_uuid,completed_at)
                SELECT @work,@source,'fixture-storage-authority',preparation_fetch_uuid,preparation,
                    'published','fixture storage proof only','{}'::jsonb,@work,clock_timestamp()
                FROM normalization.recording_processing_attempts WHERE attempt_uuid=@template
                """,
                new
                {
                    work = extraction.WorkUuid,
                    source,
                    template = template.ExtractionAttempt,
                }
            )
        );
        return new(
            source,
            resolution.SubjectUuid,
            extraction.SubjectUuid,
            extraction.WorkUuid,
            template.ResourceUuid,
            template.InputRevisionUuid
        );
    }

    private async Task<CanonicalPerson> Person()
    {
        var person = new CanonicalPerson(
            Guid.NewGuid(),
            "Alias facts " + Guid.NewGuid().ToString("N")
        );
        var input = await fixture.Capture(person.DisplayName, "people");
        await fixture.PublishPeople(people, input, new([person], [], [], []));
        return person;
    }

    private Task<NormalizationCandidateFactsSnapshot<PersonCandidateFacts>> Read(
        CanonicalPerson person,
        CandidateFactsLimits? limits = null
    ) =>
        candidates.FindPeopleFactsAsync(
            new(
                IdentityMatchingDomain.People,
                [person.DisplayName],
                [],
                MinimumNameSimilarity: .99
            ),
            limits
        );

    // Fixture-only authority rows exercise reader scale. They retain the original evidence
    // coordinates; variant wording is synthetic, not a producer publication or interpretation.
    private Task CloneAssertions(Guid person, int copies, string? wording = null) =>
        fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                """
                WITH original AS (
                    SELECT a.*,owner.published_work_uuid FROM normalization.person_aliases a
                    JOIN normalization.work_subjects owner ON owner.domain='people' AND owner.subject_kind='person'
                        AND owner.subject_uuid=a.person_uuid
                    WHERE a.person_uuid=@person ORDER BY a.uuid LIMIT 1
                ), inserted AS (
                    INSERT INTO normalization.person_aliases(uuid,person_uuid,wording,name_key,input_revision_uuid,
                        json_pointer,start_code_point,end_code_point,quote,state,basis,revision)
                    SELECT gen_random_uuid(),o.person_uuid,coalesce(@wording,o.wording),o.name_key,o.input_revision_uuid,
                        o.json_pointer,o.start_code_point,o.end_code_point,o.quote,o.state,o.basis,o.revision
                    FROM original o CROSS JOIN generate_series(1,@copies) RETURNING uuid
                )
                INSERT INTO normalization.person_alias_unmanaged_support(alias_uuid,introduced_work_uuid)
                SELECT inserted.uuid,o.published_work_uuid FROM inserted CROSS JOIN original o
                """,
                new
                {
                    person,
                    copies,
                    wording,
                }
            )
        );
}
