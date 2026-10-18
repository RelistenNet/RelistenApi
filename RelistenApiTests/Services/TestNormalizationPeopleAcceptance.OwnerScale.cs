using System.Diagnostics;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Data;

namespace RelistenApiTests.Services;

public sealed partial class TestNormalizationPeopleAcceptance
{
    private const int DistinctActorOwnerScale = 6788;
    private const string OwnerScalePolicy = "owner-scale-mechanics-fixture-v1";

    [Test]
    public async Task DistinctRecordingOwnerScaleMeasuresReaderFreshnessAndKnownContributionCapacityGap()
    {
        await ActivatePolicy();
        var seeded = await MeasureOwnerScale(
            "seed_two_service_sentinels_and_mechanical_copies",
            SeedDistinctActorOwners
        );
        var before = await MeasureOwnerScale(
            "candidate_read",
            () => ReadAggregate(seeded.First.Name)
        );
        AssertDistinctOwnerSnapshot(before, seeded, DistinctActorOwnerScale);
        await WriteOwnerScaleSizes(before, seeded);

        await MeasureOwnerScaleWork(before);
        await MeasureOwnerScaleContributionCapacity(before);
        var current = await MeasureUnwitnessedOwnerReview(before, seeded);
        await MeasureGenuineSentinelRetraction(current, seeded);
    }

    private async Task<OwnerScaleSeed> SeedDistinctActorOwners()
    {
        var name = UniqueName();
        var person = await Person(name);
        var baseAlias = UniqueName();
        var uniqueAlias = UniqueName();
        var baseContact = RandomWord() + "@example.test";
        var uniqueContact = RandomWord() + "@example.test";
        var uniqueProfile = RandomWord();
        var first = await PublishActorContext($"aka {baseAlias} ({baseContact})", person, name);
        var second = await PublishActorContext(
            $"aka {uniqueAlias} ({uniqueContact}) (https://archive.org/details/@{uniqueProfile})",
            person,
            name
        );

        // Only these two sentinels were published by extraction/resolution/acceptance services.
        // The copies below preserve relational reader invariants and typed bytes, but their
        // copied citations, attempts and work receipts do not prove a new source publication.
        var copied = await SeedDistinctActorOwnerCopies(first, DistinctActorOwnerScale - 2);
        var inventory = await ReadOwnerScaleInventory(person, first.Source.SourceUuid);
        inventory.Rows.Should().Be(DistinctActorOwnerScale);
        inventory.Sources.Should().Be(DistinctActorOwnerScale);
        inventory.Credits.Should().Be(DistinctActorOwnerScale);
        inventory.Supports.Should().Be(DistinctActorOwnerScale);
        inventory.Owners.Should().Be(DistinctActorOwnerScale);
        inventory.InvalidJoins.Should().Be(0);
        inventory.InvalidTypedCopies.Should().Be(0);
        copied.Should().HaveCount(DistinctActorOwnerScale - 2);

        var observations = await ReadOwnerScaleOriginalObservations(person);
        observations.Should().HaveCount(DistinctActorOwnerScale + 1);
        var templateOwner = await fixture.OwnerUuid("people", "recording", first.Source.SourceUuid);
        var secondOwner = await fixture.OwnerUuid("people", "recording", second.Source.SourceUuid);
        var identityOwner = await fixture.OwnerUuid("people", "person", person);
        var templateStamp = observations.Single(value => value.SubjectUuid == templateOwner);
        var copiedOwners = copied.Select(value => value.OwnerUuid).ToHashSet();
        observations
            .Select(value => value.SubjectUuid)
            .Order()
            .Should()
            .Equal(
                copiedOwners.Append(templateOwner).Append(secondOwner).Append(identityOwner).Order()
            );
        observations
            .Where(value => copiedOwners.Contains(value.SubjectUuid))
            .Should()
            .OnlyContain(value =>
                value.DesiredGeneration == templateStamp.DesiredGeneration
                && value.EditorialRevision == templateStamp.EditorialRevision
                && value.PublishedWorkUuid == templateStamp.PublishedWorkUuid
            );

        WriteOwnerScaleMeasurement(
            "seed_inventory",
            new
            {
                provenance = "two_service_published_sentinels_and_mechanical_relational_copies_only",
                servicePublishedSentinels = 2,
                mechanicallyCopiedAccounts = copied.Length,
                inventory,
                originalOwnerStampCount = observations.Length,
            }
        );
        return new(
            first,
            second,
            copied,
            observations,
            baseAlias,
            uniqueAlias,
            baseContact,
            uniqueContact,
            uniqueProfile
        );
    }

    private async Task<OwnerScaleCopy[]> SeedDistinctActorOwnerCopies(
        PublishedActorContext first,
        int copies
    ) =>
        await fixture.Db.WithWriteConnection(async connection =>
        {
            await using var transaction = await (
                (Npgsql.NpgsqlConnection)connection
            ).BeginTransactionAsync();
            // Temporary map is private to this transaction. Sources stay on the first existing
            // artist; no tracks or fabricated audio/publication history are required by this read.
            var map = (
                await connection.QueryAsync<OwnerScaleCopy>(
                    """
                    CREATE TEMP TABLE owner_scale_copy_map ON COMMIT DROP AS
                    SELECT gen_random_uuid() AS source_uuid,gen_random_uuid() AS credit_uuid,
                        gen_random_uuid() AS support_uuid,gen_random_uuid() AS owner_uuid,
                        gen_random_uuid() AS context_uuid,gen_random_uuid() AS proposal_uuid
                    FROM generate_series(1,@copies);
                    SELECT source_uuid AS "SourceUuid",credit_uuid AS "CreditUuid",support_uuid AS "SupportUuid",
                        owner_uuid AS "OwnerUuid",context_uuid AS "ContextUuid" FROM owner_scale_copy_map;
                    """,
                    new { copies },
                    transaction
                )
            ).ToArray();
            await connection.ExecuteAsync(
                """
                INSERT INTO sources(artist_id,upstream_identifier,display_date,uuid,updated_at,is_soundboard,is_remaster,has_jamcharts,taper)
                SELECT s.artist_id,'mechanical-owner-scale-fixture:'||m.source_uuid,s.display_date,m.source_uuid,s.updated_at,
                    s.is_soundboard,s.is_remaster,s.has_jamcharts,s.taper
                FROM sources s CROSS JOIN owner_scale_copy_map m WHERE s.uuid=@source;

                INSERT INTO normalization.work_subjects(uuid,domain,subject_kind,subject_uuid,desired_generation,
                    editorial_revision,policy_version,configuration_hash,published_generation,published_work_uuid,updated_at)
                SELECT m.owner_uuid,o.domain,o.subject_kind,m.source_uuid,o.desired_generation,o.editorial_revision,
                    o.policy_version,o.configuration_hash,o.published_generation,o.published_work_uuid,o.updated_at
                FROM normalization.work_subjects o CROSS JOIN owner_scale_copy_map m
                WHERE o.domain='people' AND o.subject_kind='recording' AND o.subject_uuid=@source;

                INSERT INTO normalization.recording_credits(uuid,source_uuid,role,original_text,person_uuid,scope_track_uuid,
                    state,basis,reason_code,decision_uuid,revision,updated_at)
                SELECT m.credit_uuid,m.source_uuid,c.role,c.original_text,c.person_uuid,NULL,c.state,c.basis,c.reason_code,
                    c.decision_uuid,c.revision,c.updated_at
                FROM normalization.recording_credits c CROSS JOIN owner_scale_copy_map m
                WHERE c.source_uuid=@source AND c.person_uuid=@person AND c.state='accepted';

                INSERT INTO normalization.recording_credit_managed(credit_uuid,introduced_work_uuid)
                SELECT m.credit_uuid,managed.introduced_work_uuid
                FROM normalization.recording_credit_managed managed
                JOIN normalization.recording_credits c ON c.uuid=managed.credit_uuid CROSS JOIN owner_scale_copy_map m
                WHERE c.source_uuid=@source AND c.person_uuid=@person AND c.state='accepted';

                INSERT INTO normalization.recording_credit_evidence(credit_uuid,input_revision_uuid,json_pointer,
                    start_code_point,end_code_point,quote,credit_revision)
                SELECT m.credit_uuid,e.input_revision_uuid,e.json_pointer,e.start_code_point,e.end_code_point,e.quote,e.credit_revision
                FROM normalization.recording_credit_evidence e
                JOIN normalization.recording_credits c ON c.uuid=e.credit_uuid CROSS JOIN owner_scale_copy_map m
                WHERE c.source_uuid=@source AND c.person_uuid=@person AND c.state='accepted';

                INSERT INTO normalization.recording_credit_contributor_support(uuid,credit_uuid,contributor_provider,
                    contributor_type,contributor_key,input_revision_uuid,json_pointer,start_code_point,end_code_point,
                    quote,introduced_work_uuid)
                SELECT m.support_uuid,m.credit_uuid,s.contributor_provider,s.contributor_type,s.contributor_key,
                    s.input_revision_uuid,s.json_pointer,s.start_code_point,s.end_code_point,s.quote,s.introduced_work_uuid
                FROM normalization.recording_actor_contexts a
                JOIN normalization.recording_credit_contributor_support s ON s.uuid=a.support_uuid
                CROSS JOIN owner_scale_copy_map m
                WHERE a.source_uuid=@source AND a.person_uuid=@person AND a.retired_work_uuid IS NULL;

                INSERT INTO normalization.recording_actor_contexts(uuid,support_uuid,credit_uuid,credit_revision,credit_basis,
                    person_uuid,source_uuid,owner_subject_uuid,contributor_provider,contributor_type,contributor_key,
                    extraction_attempt_uuid,proposal_uuid,input_revision_uuid,application_attempt_uuid,introduced_work_uuid,
                    basis,credited_name,facts_hash,facts)
                SELECT m.context_uuid,m.support_uuid,m.credit_uuid,a.credit_revision,a.credit_basis,a.person_uuid,m.source_uuid,
                    m.owner_uuid,a.contributor_provider,a.contributor_type,a.contributor_key,a.extraction_attempt_uuid,
                    m.proposal_uuid,a.input_revision_uuid,a.application_attempt_uuid,a.introduced_work_uuid,a.basis,
                    a.credited_name,a.facts_hash,a.facts
                FROM normalization.recording_actor_contexts a CROSS JOIN owner_scale_copy_map m
                WHERE a.source_uuid=@source AND a.person_uuid=@person AND a.retired_work_uuid IS NULL;

                INSERT INTO normalization.recording_actor_context_aliases(context_uuid,fact_uuid,wording,name_key)
                SELECT m.context_uuid,h.fact_uuid,h.wording,h.name_key
                FROM normalization.recording_actor_context_aliases h
                JOIN normalization.recording_actor_contexts a ON a.uuid=h.context_uuid CROSS JOIN owner_scale_copy_map m
                WHERE a.source_uuid=@source AND a.person_uuid=@person AND a.retired_work_uuid IS NULL;
                INSERT INTO normalization.recording_actor_context_contacts(context_uuid,fact_uuid,contact_type,lookup_key)
                SELECT m.context_uuid,h.fact_uuid,h.contact_type,h.lookup_key
                FROM normalization.recording_actor_context_contacts h
                JOIN normalization.recording_actor_contexts a ON a.uuid=h.context_uuid CROSS JOIN owner_scale_copy_map m
                WHERE a.source_uuid=@source AND a.person_uuid=@person AND a.retired_work_uuid IS NULL;
                INSERT INTO normalization.recording_actor_context_profiles(context_uuid,fact_uuid,provider,namespace,lookup_key)
                SELECT m.context_uuid,h.fact_uuid,h.provider,h.namespace,h.lookup_key
                FROM normalization.recording_actor_context_profiles h
                JOIN normalization.recording_actor_contexts a ON a.uuid=h.context_uuid CROSS JOIN owner_scale_copy_map m
                WHERE a.source_uuid=@source AND a.person_uuid=@person AND a.retired_work_uuid IS NULL;
                """,
                new { source = first.Source.SourceUuid, person = first.PersonUuid },
                transaction
            );
            await transaction.CommitAsync();
            return map;
        });

    private Task<OwnerScaleInventory> ReadOwnerScaleInventory(Guid person, Guid source) =>
        fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<OwnerScaleInventory>(
                """
                WITH active AS (
                    SELECT * FROM normalization.recording_actor_contexts
                    WHERE person_uuid=@person AND retired_work_uuid IS NULL
                ), original AS (
                    SELECT facts_hash,facts FROM active WHERE source_uuid=@source
                )
                SELECT count(*) AS "Rows",count(DISTINCT a.source_uuid) AS "Sources",
                    count(DISTINCT a.credit_uuid) AS "Credits",count(DISTINCT a.support_uuid) AS "Supports",
                    count(DISTINCT a.owner_subject_uuid) AS "Owners",
                    count(*) FILTER(WHERE c.uuid IS NULL OR c.source_uuid<>a.source_uuid OR c.person_uuid<>a.person_uuid
                        OR c.state<>'accepted' OR c.revision<>a.credit_revision OR c.basis<>a.credit_basis
                        OR s.uuid IS NULL OR s.credit_uuid<>c.uuid OR s.retired_work_uuid IS NOT NULL
                        OR o.uuid IS NULL OR o.domain<>'people' OR o.subject_kind<>'recording' OR o.subject_uuid<>a.source_uuid
                        OR src.uuid IS NULL) AS "InvalidJoins",
                    count(*) FILTER(WHERE src.upstream_identifier LIKE 'mechanical-owner-scale-fixture:%'
                        AND NOT EXISTS(SELECT 1 FROM original b WHERE b.facts_hash=a.facts_hash AND b.facts=a.facts)) AS "InvalidTypedCopies",
                    sum(octet_length(a.facts::text))::bigint AS "RawJsonTextBytes",
                    sum(pg_column_size(a.facts))::bigint AS "StoredFactsDatumBytes"
                FROM active a LEFT JOIN normalization.recording_credits c ON c.uuid=a.credit_uuid
                LEFT JOIN normalization.recording_credit_contributor_support s ON s.uuid=a.support_uuid
                LEFT JOIN normalization.work_subjects o ON o.uuid=a.owner_subject_uuid
                LEFT JOIN sources src ON src.uuid=a.source_uuid
                """,
                new { person, source }
            )
        );

    private Task<NormalizationSubjectObservation[]> ReadOwnerScaleOriginalObservations(
        Guid person
    ) =>
        fixture.Db.WithWriteConnection(async c =>
            (
                await c.QueryAsync<NormalizationSubjectObservation>(
                    """
                    SELECT o.uuid AS "SubjectUuid",o.desired_generation AS "DesiredGeneration",
                        o.editorial_revision AS "EditorialRevision",o.published_work_uuid AS "PublishedWorkUuid"
                    FROM normalization.work_subjects o
                    WHERE o.uuid IN (SELECT owner_subject_uuid FROM normalization.recording_actor_contexts
                        WHERE person_uuid=@person AND retired_work_uuid IS NULL)
                        OR (o.domain='people' AND o.subject_kind='person' AND o.subject_uuid=@person)
                    ORDER BY o.uuid
                    """,
                    new { person }
                )
            ).ToArray()
        );

    private static void AssertDistinctOwnerSnapshot(
        NormalizationCandidateFactsSnapshot<PersonCandidateFacts> snapshot,
        OwnerScaleSeed seeded,
        int activeCount
    )
    {
        snapshot.Complete.Should().BeTrue();
        var fact = snapshot.Facts.Should().ContainSingle().Which;
        fact.Hit.Uuid.Should().Be(seeded.First.PersonUuid);
        fact.ActorContextCount.Should().Be(activeCount);
        fact.ActorEvidence.Should().ContainSingle();
        fact.ActorEvidenceTruncated.Should().BeTrue();
        fact.ArtistContexts.SelectMany(value => value.Witnesses).Should().BeEmpty();
        fact.ActorMatchingFacts!.Aliases.Should()
            .BeEquivalentTo(new[] { seeded.BaseAlias, seeded.UniqueAlias });
        fact.ActorMatchingFacts.Contacts.Select(value => value.LookupKey)
            .Should()
            .BeEquivalentTo(new[] { seeded.BaseContact, seeded.UniqueContact });
        fact.ActorMatchingFacts.Profiles.Select(value => value.LookupKey)
            .Should()
            .ContainSingle()
            .Which.Should()
            .Be(seeded.UniqueProfile);
        snapshot.ReadSet.OwnerObservations.Should().Equal(seeded.OriginalObservations);
        snapshot
            .ReadSet.OwnerObservations.Select(value => value.SubjectUuid)
            .Should()
            .OnlyHaveUniqueItems();
    }

    private async Task WriteOwnerScaleSizes(
        NormalizationCandidateFactsSnapshot<PersonCandidateFacts> snapshot,
        OwnerScaleSeed seeded
    )
    {
        var inventory = await ReadOwnerScaleInventory(
            seeded.First.PersonUuid,
            seeded.First.Source.SourceUuid
        );
        var fact = snapshot.Facts.Single();
        WriteOwnerScaleMeasurement(
            "reader_sizes",
            new
            {
                inventory.RawJsonTextBytes,
                inventory.StoredFactsDatumBytes,
                fullSnapshotSerializedUtf8Bytes = OwnerScaleBytes(snapshot),
                matchingAggregateSerializedUtf8Bytes = OwnerScaleBytes(fact.ActorMatchingFacts),
                witnessWrapperListSerializedUtf8Bytes = OwnerScaleBytes(fact.ActorEvidence),
                ownerStampsSerializedUtf8Bytes = OwnerScaleBytes(
                    snapshot.ReadSet.OwnerObservations
                ),
                readSetSerializedUtf8Bytes = OwnerScaleBytes(snapshot.ReadSet),
                activeActorRows = fact.ActorContextCount,
                totalOwnerObservations = snapshot.ReadSet.OwnerObservations.Count,
                retainedRawWitnesses = fact.ActorEvidence!.Count,
                meaning = "JSON text, stored datum and serializer sizes are separate; none measures network bytes, peak memory, TOAST IO or throughput",
            }
        );
    }

    private Task<NormalizationWorkTicket> BeginOwnerScaleObservation(
        NormalizationCandidateFactsSnapshot<PersonCandidateFacts> snapshot,
        Guid workUuid,
        NormalizationSubjectKey subject
    ) =>
        fixture.Work.BeginWorkAsync(
            workUuid,
            subject,
            OwnerScalePolicy,
            [],
            [],
            catalogInputs: snapshot.ReadSet.CatalogInputs,
            candidateUniverses: snapshot.ReadSet.CandidateUniverses,
            expectedOwnerObservations: snapshot.ReadSet.OwnerObservations
        );

    private async Task MeasureOwnerScaleWork(
        NormalizationCandidateFactsSnapshot<PersonCandidateFacts> snapshot
    )
    {
        var work = Guid.NewGuid();
        var subject = new NormalizationSubjectKey("people", "owner_scale_observer", Guid.NewGuid());
        var ticket = await MeasureOwnerScale(
            "work_admission",
            () => BeginOwnerScaleObservation(snapshot, work, subject)
        );
        ticket.OwnerObservations.Should().Equal(snapshot.ReadSet.OwnerObservations);
        ticket.CandidateUniverses.Should().Equal(snapshot.ReadSet.CandidateUniverses);
        ticket.CatalogInputs.Should().Equal(snapshot.ReadSet.CatalogInputs);
        var replay = await MeasureOwnerScale(
            "work_uuid_replay",
            () => BeginOwnerScaleObservation(snapshot, work, subject)
        );
        replay.Should().BeEquivalentTo(ticket);
        WriteOwnerScaleMeasurement(
            "ticket_size",
            new { serializedUtf8Bytes = OwnerScaleBytes(ticket), exactOriginalStamps = true }
        );
        var callbacks = 0;
        Task Callback(Npgsql.NpgsqlConnection _, Npgsql.NpgsqlTransaction __, CancellationToken ___)
        {
            callbacks++;
            return Task.CompletedTask;
        }
        var publication = await MeasureOwnerScale(
            "unchanged_noop_work_publication",
            () => fixture.Work.TryPublishAsync(work, Callback)
        );
        publication.Status.Should().Be(NormalizationPublicationStatus.Published);
        publication.Replayed.Should().BeFalse();
        var publishedReplay = await fixture.Work.TryPublishAsync(work, Callback);
        publishedReplay.Replayed.Should().BeTrue();
        callbacks.Should().Be(1, "this is a Work no-op callback, not an actor publication");
    }

    private async Task MeasureOwnerScaleContributionCapacity(
        NormalizationCandidateFactsSnapshot<PersonCandidateFacts> snapshot
    )
    {
        var subject = new NormalizationSubjectKey(
            "people",
            "owner_scale_contributors",
            Guid.NewGuid()
        );
        var ownerKeys = snapshot
            .ReadSet.OwnerObservations.Select(value => value.SubjectUuid)
            .ToArray();
        var replacements = new[] { "first", "second" }
            .Select(key => new NormalizationPreparedInputContribution(
                new(new("tests", "owner_scale_mechanics", key), 0, [], [], ownerKeys),
                [],
                CatalogInputs: snapshot.ReadSet.CatalogInputs,
                CandidateUniverses: snapshot.ReadSet.CandidateUniverses,
                ExpectedOwnerObservations: snapshot.ReadSet.OwnerObservations
            ))
            .ToArray();
        var ticket = await MeasureOwnerScale(
            "public_two_contributor_batch_admission",
            () =>
                fixture.Work.BeginContributedBatchAsync(
                    Guid.NewGuid(),
                    subject,
                    OwnerScalePolicy,
                    replacements,
                    expectedContributionRevisions: []
                )
        );
        ticket.OwnerObservations.Should().Equal(snapshot.ReadSet.OwnerObservations);
        Func<Task> defaultRead = async () =>
        {
            await fixture.Work.GetInputContributionsAsync(subject);
        };
        var failure = await MeasureOwnerScale(
            "default_contribution_metadata_read_expected_failure",
            async () =>
                (
                    await defaultRead
                        .Should()
                        .ThrowAsync<NormalizationInputContributionsCapacityException>()
                ).Which
        );
        failure.Membership.Contributors.Should().Be(2);
        failure.Membership.Bindings.Should().BeGreaterThan(10000);
        var expanded = await MeasureOwnerScale(
            "explicit_larger_supported_metadata_read",
            () =>
                fixture.Work.GetInputContributionsAsync(
                    subject,
                    new(MaximumContributors: 100, MaximumBindings: 100000)
                )
        );
        expanded.UnmanagedBaseline.Should().BeNull();
        expanded.Contributions.Should().HaveCount(2);
        foreach (var contribution in expanded.Contributions)
        {
            contribution.OwnerObservationSubjectUuids.Should().Equal(ownerKeys);
            contribution
                .CatalogArtistUuids.Should()
                .BeEquivalentTo(snapshot.ReadSet.CatalogInputs.Select(value => value.ArtistUuid));
            contribution
                .CandidateUniverses.Should()
                .BeEquivalentTo(
                    snapshot.ReadSet.CandidateUniverses.Select(value => value.Universe)
                );
        }
        var storedOwnerBindings = expanded.Contributions.Sum(value =>
            value.OwnerObservationSubjectUuids!.Count
        );
        storedOwnerBindings.Should().Be(2 * ownerKeys.Length);
        expanded
            .Contributions.SelectMany(value => value.OwnerObservationSubjectUuids!)
            .Distinct()
            .Should()
            .BeEquivalentTo(ownerKeys);
        var expectedBindings =
            2L
            * (
                ownerKeys.Length
                + snapshot.ReadSet.CatalogInputs.Count
                + snapshot.ReadSet.CandidateUniverses.Count
            );
        failure.Membership.Bindings.Should().Be(expectedBindings);
        WriteOwnerScaleMeasurement(
            "known_pipeline_capacity_gap",
            new
            {
                defaultMaximumContributors = 100,
                defaultMaximumStoredBindings = 10000,
                explicitMetadataReadMaximumBindings = 100000,
                storedOwnerBindings,
                distinctOwnerUnion = ownerKeys.Length,
                totalStoredBindings = failure.Membership.Bindings,
                contributorCount = expanded.Contributions.Count,
                checkpoint = "No extraction/preparation/batch checkpoint was fabricated by this metadata-only fixture",
                pipelineRecoveryEstablished = false,
                meaning = "Public metadata override reads every key; current people entrypoints still use defaults",
            }
        );
    }

    private async Task<
        NormalizationCandidateFactsSnapshot<PersonCandidateFacts>
    > MeasureUnwitnessedOwnerReview(
        NormalizationCandidateFactsSnapshot<PersonCandidateFacts> snapshot,
        OwnerScaleSeed seeded
    )
    {
        var witness = snapshot.Facts.Single().ActorEvidence!.Single().SourceUuid;
        var omitted = seeded.Copies.First(value => value.SourceUuid != witness);
        var stamp = snapshot.ReadSet.OwnerObservations.Single(value =>
            value.SubjectUuid == omitted.OwnerUuid
        );
        var staleTicket = await BeginOwnerScaleObservation(
            snapshot,
            Guid.NewGuid(),
            new("people", "owner_scale_stale_observer", Guid.NewGuid())
        );
        await MeasureOwnerScale(
            "unwitnessed_owner_noop_review",
            () =>
                fixture.Work.ApplyReviewAsync(
                    new(
                        Guid.NewGuid(),
                        stamp.SubjectUuid,
                        stamp.EditorialRevision,
                        "accept",
                        JsonSerializer.SerializeToElement(
                            new
                            {
                                note = "Mechanical owner-scale fixture review; no actor truth asserted",
                            }
                        ),
                        "Invalidate an omitted original owner clock",
                        Operator
                    ),
                    (_, _, _) => Task.CompletedTask
                )
        );
        var current = await MeasureOwnerScale(
            "candidate_read_after_owner_review",
            () => ReadAggregate(seeded.First.Name)
        );
        current.Complete.Should().BeTrue();
        current.ReadSet.CandidateUniverses.Should().Equal(snapshot.ReadSet.CandidateUniverses);
        current.ReadSet.CatalogInputs.Should().Equal(snapshot.ReadSet.CatalogInputs);
        current
            .Facts.Single()
            .ActorMatchingFacts.Should()
            .BeEquivalentTo(snapshot.Facts.Single().ActorMatchingFacts);
        var changed = current.ReadSet.OwnerObservations.Single(value =>
            value.SubjectUuid == stamp.SubjectUuid
        );
        changed.EditorialRevision.Should().Be(stamp.EditorialRevision + 1);
        changed.DesiredGeneration.Should().Be(stamp.DesiredGeneration + 1);
        changed.PublishedWorkUuid.Should().Be(stamp.PublishedWorkUuid);
        current
            .ReadSet.OwnerObservations.Where(value => value.SubjectUuid != stamp.SubjectUuid)
            .Should()
            .Equal(
                snapshot.ReadSet.OwnerObservations.Where(value =>
                    value.SubjectUuid != stamp.SubjectUuid
                )
            );
        Func<Task> oldAdmission = async () =>
        {
            await BeginOwnerScaleObservation(
                snapshot,
                Guid.NewGuid(),
                new("people", "owner_scale_old_snapshot", Guid.NewGuid())
            );
        };
        await MeasureOwnerScale(
            "old_snapshot_admission_rejected",
            async () =>
                (await oldAdmission.Should().ThrowAsync<NormalizationConflictException>()).Which
        );
        var callbacks = 0;
        var stale = await MeasureOwnerScale(
            "admitted_publication_after_review",
            () =>
                fixture.Work.TryPublishAsync(
                    staleTicket.WorkUuid,
                    (_, _, _) =>
                    {
                        callbacks++;
                        return Task.CompletedTask;
                    }
                )
        );
        stale.Status.Should().Be(NormalizationPublicationStatus.Stale);
        callbacks.Should().Be(0);
        WriteOwnerScaleMeasurement(
            "unwitnessed_review_clocks",
            new
            {
                original = stamp,
                changed,
                allOtherOriginalStampsPreserved = true,
                candidateUniverseUnchanged = true,
            }
        );
        return current;
    }

    private async Task MeasureGenuineSentinelRetraction(
        NormalizationCandidateFactsSnapshot<PersonCandidateFacts> before,
        OwnerScaleSeed seeded
    )
    {
        await SetTaper(seeded.Second.Source, "");
        var timer = Stopwatch.StartNew();
        string phase = "resolve_genuine_second_sentinel";
        try
        {
            var resolution = await Resolve(seeded.Second.Source);
            await WriteOwnerScaleSavedCheckpointSizes(resolution.AttemptUuid);
            phase = "apply_genuine_second_sentinel_retraction";
            var application = await applier.ApplyRecordingAsync(
                Guid.NewGuid(),
                resolution.AttemptUuid
            );
            application.Result!.Disposition.Should().Be(PeopleAcceptanceDisposition.Applied);
            var after = await ReadAggregate(seeded.First.Name);
            after.Complete.Should().BeTrue();
            after.Facts.Single().ActorContextCount.Should().Be(DistinctActorOwnerScale - 1);
            after.Facts.Single().ActorMatchingFacts!.Aliases.Should().Equal(seeded.BaseAlias);
            after
                .Facts.Single()
                .ActorMatchingFacts!.Contacts.Select(value => value.LookupKey)
                .Should()
                .Equal(seeded.BaseContact);
            after.Facts.Single().ActorMatchingFacts!.Profiles.Should().BeEmpty();
            var removedOwner = (
                await fixture.Work.GetSubjectAsync(
                    new NormalizationSubjectKey(
                        "people",
                        "recording",
                        seeded.Second.Source.SourceUuid
                    )
                )
            )!.SubjectUuid;
            after
                .ReadSet.OwnerObservations.Should()
                .Equal(
                    before.ReadSet.OwnerObservations.Where(value =>
                        value.SubjectUuid != removedOwner
                    )
                );
            Func<Task> oldAdmission = async () =>
            {
                await BeginOwnerScaleObservation(
                    before,
                    Guid.NewGuid(),
                    new("people", "owner_scale_pre_retraction", Guid.NewGuid())
                );
            };
            await oldAdmission.Should().ThrowAsync<NormalizationConflictException>();
            WriteOwnerScaleMeasurement(
                "genuine_sentinel_retraction",
                new
                {
                    status = "service_retracted",
                    elapsedMilliseconds = timer.Elapsed.TotalMilliseconds,
                    uniqueSemanticsRemoved = true,
                    removedOwner,
                }
            );
        }
        catch (NormalizationInputContributionsCapacityException failure)
        {
            // This known default-bound failure is evidence of a pipeline gap, not a pass
            // for retraction. Preserve the account and report it; never replace it with SQL.
            var after = await ReadAggregate(seeded.First.Name);
            after
                .Facts.Single()
                .ActorMatchingFacts!.Contacts.Select(value => value.LookupKey)
                .Should()
                .Contain(seeded.UniqueContact);
            WriteOwnerScaleMeasurement(
                "genuine_sentinel_retraction",
                new
                {
                    status = "blocked_by_inherited_default_contribution_limit",
                    phase,
                    elapsedMilliseconds = timer.Elapsed.TotalMilliseconds,
                    failure.Owner,
                    failure.Membership,
                    uniqueSemanticsRemoved = false,
                    pipelineRecoveryEstablished = false,
                }
            );
        }
    }

    private Task WriteOwnerScaleSavedCheckpointSizes(Guid attempt) =>
        fixture.Db.WithWriteConnection(async c =>
        {
            var sizes = await c.QuerySingleAsync<OwnerScaleCheckpointSizes>(
                """
                SELECT octet_length(preparation::text)::bigint AS "PreparationJsonTextBytes",
                    pg_column_size(preparation)::bigint AS "PreparationStoredDatumBytes",
                    octet_length(batch::text)::bigint AS "BatchJsonTextBytes",pg_column_size(batch)::bigint AS "BatchStoredDatumBytes"
                FROM normalization.people_resolution_attempts WHERE attempt_uuid=@attempt
                """,
                new { attempt }
            );
            WriteOwnerScaleMeasurement(
                "actual_saved_retraction_resolution_checkpoint",
                new
                {
                    attempt,
                    sizes,
                    meaning = "Actual saved empty-credit retraction preparation/batch; not a many-claim capacity measurement",
                }
            );
        });

    private static int OwnerScaleBytes<T>(T value) =>
        JsonSerializer.SerializeToUtf8Bytes(value).Length;

    private static async Task<T> MeasureOwnerScale<T>(string phase, Func<Task<T>> operation)
    {
        var timer = Stopwatch.StartNew();
        var result = await operation();
        WriteOwnerScaleMeasurement(
            phase,
            new
            {
                elapsedMilliseconds = timer.Elapsed.TotalMilliseconds,
                timingIsDiagnosticOnly = true,
            }
        );
        return result;
    }

    private static void WriteOwnerScaleMeasurement<T>(string phase, T details) =>
        TestContext.Progress.WriteLine(
            "OWNER_SCALE_MEASUREMENT " + JsonSerializer.Serialize(new { phase, details })
        );

    private sealed record OwnerScaleCopy(
        Guid SourceUuid,
        Guid CreditUuid,
        Guid SupportUuid,
        Guid OwnerUuid,
        Guid ContextUuid
    );

    private sealed record OwnerScaleSeed(
        PublishedActorContext First,
        PublishedActorContext Second,
        OwnerScaleCopy[] Copies,
        NormalizationSubjectObservation[] OriginalObservations,
        string BaseAlias,
        string UniqueAlias,
        string BaseContact,
        string UniqueContact,
        string UniqueProfile
    );

    private sealed record OwnerScaleInventory(
        long Rows,
        long Sources,
        long Credits,
        long Supports,
        long Owners,
        long InvalidJoins,
        long InvalidTypedCopies,
        long RawJsonTextBytes,
        long StoredFactsDatumBytes
    );

    private sealed record OwnerScaleCheckpointSizes(
        long? PreparationJsonTextBytes,
        long? PreparationStoredDatumBytes,
        long? BatchJsonTextBytes,
        long? BatchStoredDatumBytes
    );
}
