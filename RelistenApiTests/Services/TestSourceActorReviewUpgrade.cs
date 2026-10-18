using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Relisten;
using Relisten.Api.Models.Normalization;
using Relisten.Data;

namespace RelistenApiTests.Services;

/// <summary>
/// Applies actual 56 DDL to a nonempty 55 schema. The pre-upgrade source review uses
/// authenticated extraction evidence and a real Work receipt under 53/54 constraints;
/// the post-upgrade decision uses the trusted public review service.
/// </summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestSourceActorReviewUpgrade
{
    // A fixed older schema is essential: do not remove objects from the current bootstrap
    // to imitate an upgrade. The final source resolver requires 55 and 56 together, so it
    // is not invoked before 56. Retained extraction, source authority and Work are genuine.
    private static readonly string[] SchemaThrough55 =
    [
        Migrations.AddArchiveIntake.SchemaSql,
        Migrations.AddRelistenCollections.SchemaSql,
        Migrations.AddArchivePerformerReviews.SchemaSql,
        Migrations.AddShowPerformances.SchemaSql,
        Migrations.AddExistingArchiveArtistPublication.SchemaSql,
        Migrations.AddNormalizationEvidence.SchemaSql,
        Migrations.AddNormalizationWork.SchemaSql,
        Migrations.AddCanonicalPeopleAndVenues.SchemaSql,
        Migrations.AddCanonicalMusic.SchemaSql,
        Migrations.AddNormalizationExtractionCache.SchemaSql,
        Migrations.AddNormalizationCandidateIndexes.SchemaSql,
        Migrations.AddNormalizationCatalogBindings.SchemaSql,
        Migrations.AddNormalizationMappingRegistry.SchemaSql,
        Migrations.AddNormalizationRecordingProcessing.SchemaSql,
        Migrations.AddNormalizationRecordingPages.SchemaSql,
        Migrations.FenceNormalizationArtistMappings.SchemaSql,
        Migrations.AddNormalizationImportedVenueBindings.SchemaSql,
        Migrations.AddNormalizationCandidateUniverses.SchemaSql,
        Migrations.AddNormalizationOwnerObservations.SchemaSql,
        Migrations.AddNormalizationCandidateFactIndexes.SchemaSql,
        Migrations.AddNormalizationSourceUuidBindings.SchemaSql,
        Migrations.AddNormalizationRecordingLookups.SchemaSql,
        Migrations.AddNormalizationRecordingInputGroups.SchemaSql,
        Migrations.AddNormalizationSetlistProcessing.SchemaSql,
        Migrations.AddRecordingCreditContributorSupport.SchemaSql,
        Migrations.AddCompositionAuthorshipAssertions.SchemaSql,
        Migrations.AddNormalizationPeopleResolution.SchemaSql,
        Migrations.AddImportedVenueResolutionIndexes.SchemaSql,
        Migrations.AddAcceptedSetlistStructures.SchemaSql,
        Migrations.AddSetlistCorrespondenceAuthorizations.SchemaSql,
        Migrations.AddMusicBrainzAcquisition.SchemaSql,
        Migrations.AddNormalizationExtractionFailureDiagnostics.SchemaSql,
        Migrations.AddNormalizationExtractionJobAttemptLimit.SchemaSql,
        Migrations.AddNormalizationPeopleAcceptance.SchemaSql,
        Migrations.AddMusicBrainzSearchProfiles.SchemaSql,
        Migrations.AddAcceptedRecordingActorContext.SchemaSql,
        Migrations.AddCompositionResolution.SchemaSql,
        Migrations.AddVenueResolution.SchemaSql,
        Migrations.AddRepertoireResolution.SchemaSql,
        Migrations.AddRepertoireConsumers.SchemaSql,
        Migrations.AddSourceActorAliasAuthority.SchemaSql,
        Migrations.AddNormalizationReviewReadSet.SchemaSql,
        Migrations.AddSourceActorResolution.SchemaSql,
    ];

    [Test]
    public async Task Nonempty55UpgradeRetainsSourceAuthorityAndReadSetReceiptsBeforeTrustedReview()
    {
        if (Environment.GetEnvironmentVariable("RELISTEN_LOCAL_INGESTION_TESTS") != "1")
        {
            Assert.Ignore("Run ./test-local.sh to include disposable local database coverage.");
        }

        var database = "relisten_source_review_upgrade_" + Guid.NewGuid().ToString("N");
        var previousWrite = DbService.ConnStr;
        var previousRead = DbService.ReadOnlyConnStr;
        var created = false;
        try
        {
            await CreateSchema55(database);
            created = true;
            var db = new DbService(
                $"postgresql://relisten:local_dev_password@127.0.0.1:15432/{database}",
                null,
                null,
                Host.CreateApplicationBuilder(
                    new HostApplicationBuilderSettings { EnvironmentName = Environments.Production }
                ).Environment,
                NullLogger<DbService>.Instance
            );
            var work = new NormalizationWorkService(db);
            var evidence = new NormalizationEvidenceService(db);
            var person = await PublishPerson(db, work, evidence);
            var source = await PublishExtraction(db, work, evidence);
            var legacy = await AcceptBefore56(work, source, person);
            var before = await ReadExistingTables(db);
            var priorReceipt = await Receipt(db, legacy.OperationUuid);
            var priorHistory = await History(db, source.ReviewStateUuid, 1);
            var proof = JsonSerializer.Deserialize<NormalizationReviewReadSet>(
                await db.WithWriteConnection(c =>
                    c.QuerySingleAsync<string>(
                        "SELECT read_set::text FROM normalization.review_operations WHERE uuid=@OperationUuid",
                        legacy
                    )
                )
            )!;
            proof.Inputs.Should().BeEquivalentTo(source.Batch.Ticket.Inputs);
            proof
                .OwnerObservations.Should()
                .Contain(value => value.SubjectUuid == person.Owner.SubjectUuid);
            priorHistory.Should().NotBeNullOrEmpty();
            (await TableExists(db, "source_actor_review_preparations")).Should().BeFalse();
            (await TableExists(db, "source_actor_resolution_attempts")).Should().BeTrue();

            await Apply56(db);

            var after = await ReadExistingTables(db);
            after
                .Keys.Should()
                .BeEquivalentTo(before.Keys.Append("source_actor_review_preparations"));
            foreach (var (table, rows) in before)
            {
                after[table].Should().Be(rows, "56 must retain every prior row in {0}", table);
            }
            after["source_actor_review_preparations"].Should().Be("[]");
            (await Receipt(db, legacy.OperationUuid)).Should().Be(priorReceipt);
            (
                await work.ApplyReviewAsync(
                    legacy,
                    (_, _, _) =>
                        throw new AssertionException(
                            "A retained pre-56 receipt must replay without its callback"
                        )
                )
            )
                .Replayed.Should()
                .BeTrue();

            var store = new SourceActorReviewStore(db);
            var review = new SourceActorReviewService(db, work, store);
            var sourceOwner = (await work.GetSubjectAsync(source.Owner.SubjectUuid))!;
            var preparation = await review.PrepareAsync(
                Guid.NewGuid(),
                new(
                    source.Batch.AttemptUuid,
                    source.Claim,
                    SourceActorReviewAction.Retract,
                    sourceOwner.EditorialRevision,
                    1
                )
            );
            preparation.PriorReview!.ReviewRevision.Should().Be(1);
            preparation.PriorReview.LastOperationUuid.Should().Be(legacy.OperationUuid);
            preparation.OriginalField.Scope.Should().BeEquivalentTo(source.Scope);
            preparation.ReadSet.Inputs.Should().BeEquivalentTo(source.Batch.Ticket.Inputs);
            (await store.GetPreparationAsync(preparation.PreparationUuid))
                .Should()
                .BeEquivalentTo(preparation);
            await AssertImmutablePreparation(db, preparation.PreparationUuid);
            var operation = Guid.NewGuid();
            const string reason =
                "Withdraw the earlier source claim after reviewing its complete retained field.";
            var result = await review.ApplyAsync(
                operation,
                preparation.PreparationUuid,
                reason,
                "upgrade-operator"
            );

            result.Replayed.Should().BeFalse();
            result.EditorialRevision.Should().Be(sourceOwner.EditorialRevision + 1);
            (await Receipt(db, legacy.OperationUuid)).Should().Be(priorReceipt);
            (await History(db, source.ReviewStateUuid, 1)).Should().Be(priorHistory);
            var state = await db.WithWriteConnection(c =>
                c.QuerySingleAsync<ReviewState>(
                    """
                    SELECT review_revision AS "Revision",state AS "State",selected_person_uuid AS "Person",
                        last_operation_uuid AS "Operation" FROM normalization.source_actor_alias_review_state WHERE uuid=@uuid
                    """,
                    new { uuid = source.ReviewStateUuid }
                )
            );
            state.Revision.Should().Be(2);
            state.State.Should().Be("retracted");
            state.Person.Should().BeNull();
            state.Operation.Should().Be(operation);
            (await History(db, source.ReviewStateUuid, 2)).Should().NotBeNullOrEmpty();
            (await work.GetSubjectAsync(person.Owner.SubjectUuid))
                .Should()
                .BeEquivalentTo(person.Owner);
            (await work.GetSubjectAsync(source.Batch.Ticket.SubjectUuid))
                .Should()
                .BeEquivalentTo(source.ExtractionOwner);
            (
                await review.ApplyAsync(
                    operation,
                    preparation.PreparationUuid,
                    reason,
                    "upgrade-operator"
                )
            )
                .Replayed.Should()
                .BeTrue();
            (await Receipt(db, legacy.OperationUuid)).Should().Be(priorReceipt);
            (await History(db, source.ReviewStateUuid, 1)).Should().Be(priorHistory);
            var final = await ReadExistingTables(db);
            foreach (
                var (table, rows) in before.Where(value =>
                    value.Key
                        is not (
                            "source_actor_alias_review_state"
                            or "source_actor_alias_review_state_history"
                            or "review_operations"
                            or "work_subjects"
                        )
                )
            )
            {
                final[table].Should().Be(rows, "source review must not change {0}", table);
            }
        }
        finally
        {
            DbService.ConnStr = previousWrite;
            DbService.ReadOnlyConnStr = previousRead;
            if (created)
            {
                await DropDatabase(database);
            }
        }
    }

    private static async Task<PersonSeed> PublishPerson(
        DbService db,
        NormalizationWorkService work,
        NormalizationEvidenceService evidence
    )
    {
        const string name = "Review Robin";
        var person = new CanonicalPerson(Guid.NewGuid(), name);
        var resource = new NormalizationResourceKey(
            "test",
            "upgrade-person",
            person.Uuid.ToString("N")
        );
        var retained = await evidence.CaptureInputAsync(
            new(
                Guid.NewGuid(),
                resource,
                JsonSerializer.SerializeToUtf8Bytes(new { text = name }),
                "application/json",
                "fixture",
                0,
                DateTimeOffset.UtcNow
            )
        );
        var ticket = await work.BeginWorkAsync(
            Guid.NewGuid(),
            new("people", "person", person.Uuid),
            "source-review-upgrade-person-v1",
            [resource],
            []
        );
        var reference = new NormalizedEvidenceReference(
            retained.Revision.Uuid,
            "/text",
            0,
            name.Length,
            name
        );
        (
            await new NormalizedPeopleService(db, work).PublishAsync(
                ticket.WorkUuid,
                new([person], [new(person.Uuid, person.Uuid, name, reference)], [], [])
            )
        )
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);
        return new(person.Uuid, (await work.GetSubjectAsync(ticket.SubjectUuid))!);
    }

    private static async Task<SourceSeed> PublishExtraction(
        DbService db,
        NormalizationWorkService work,
        NormalizationEvidenceService evidence
    )
    {
        var sourceUuid = Guid.NewGuid();
        await db.WithWriteConnection(async c =>
        {
            var artist = await c.QuerySingleAsync<int>(
                "INSERT INTO artists(name,slug,uuid,updated_at) VALUES('Source review upgrade',@slug,@uuid,now()) RETURNING id",
                new { slug = Guid.NewGuid().ToString("N"), uuid = Guid.NewGuid() }
            );
            var sourceId = await c.QuerySingleAsync<int>(
                """
                INSERT INTO sources(artist_id,upstream_identifier,display_date,uuid,updated_at,
                    is_soundboard,is_remaster,has_jamcharts,description)
                VALUES(@artist,@key,'2026-10-17',@sourceUuid,now(),false,false,false,@description) RETURNING id
                """,
                new
                {
                    artist,
                    key = sourceUuid.ToString("N"),
                    sourceUuid,
                    description = "Personnel:\nReview Robin aka Finch - vocals",
                }
            );
            var setId = await c.QuerySingleAsync<long>(
                "INSERT INTO source_sets(source_id,index,name,uuid,updated_at) VALUES(@sourceId,0,'Set',@uuid,now()) RETURNING id",
                new { sourceId, uuid = Guid.NewGuid() }
            );
            await c.ExecuteAsync(
                """
                INSERT INTO source_tracks(source_id,source_set_id,artist_id,track_position,title,slug,mp3_url,uuid,updated_at)
                VALUES(@sourceId,@setId,@artist,1,'Song',@slug,'https://example.test/upgrade.mp3',@uuid,now())
                """,
                new
                {
                    sourceId,
                    setId,
                    artist,
                    slug = Guid.NewGuid().ToString("N"),
                    uuid = Guid.NewGuid(),
                }
            );
        });
        var extractions = new NormalizationRecordingProposalService(db);
        var processor = new NormalizationRecordingProcessor(
            new(db, evidence),
            evidence,
            new(db),
            new(),
            new(new(), new()),
            work,
            extractions,
            new(db)
        );
        var result = await processor.ProcessAsync(Guid.NewGuid(), sourceUuid);
        result.Disposition.Should().Be(NormalizationRecordingProcessingDisposition.Published);
        var batch = (await extractions.GetAttemptBatchAsync(result.AttemptUuid))!;
        var scope = batch.Extraction.SourceActorAliases!.Single(value =>
            value.Extraction.Assertions.Count != 0
        );
        var assertion = scope.Extraction.Assertions.Single();
        var input = batch.Ticket.Inputs.Single(value =>
            value.RevisionUuid == scope.InputRevisionUuid
        );
        var contributor = NormalizationRecordingReadSet
            .Inputs(batch.Preparation)
            .Single(value => value.Binding.InputRevisionUuid == scope.InputRevisionUuid)
            .Contributor;
        var owner = await work.BeginWorkAsync(
            Guid.NewGuid(),
            new("people", "source_actor_resolution", sourceUuid),
            "source-review-upgrade-owner-v1",
            [],
            []
        );
        return new(
            batch,
            scope,
            new(
                sourceUuid,
                contributor,
                input.ResourceUuid,
                scope.InputRevisionUuid,
                assertion.NameEvidence.JsonPointer,
                scope.ProducerVersion,
                assertion.AssertionUuid,
                assertion.ActorReferenceUuid,
                assertion.FieldSha256
            ),
            Guid.NewGuid(),
            (await work.GetSubjectAsync(owner.SubjectUuid))!,
            (await work.GetSubjectAsync(batch.Ticket.SubjectUuid))!
        );
    }

    private static async Task<NormalizationReviewCommand> AcceptBefore56(
        NormalizationWorkService work,
        SourceSeed source,
        PersonSeed person
    )
    {
        var scope = source.Scope;
        var assertion = scope.Extraction.Assertions.Single();
        var claim = source.Claim;
        var ticket = source.Batch.Ticket;
        var proof = new NormalizationReviewReadSet(
            ticket.Inputs,
            ticket.Dependencies,
            ticket.CatalogInputs,
            ticket.SourceIdentities,
            ticket.MappingRegistry,
            ticket.ImportedVenues,
            ticket.CandidateUniverses,
            (ticket.OwnerObservations ?? [])
                .Concat(
                    new[]
                    {
                        Observe(source.Owner),
                        Observe(source.ExtractionOwner),
                        Observe(person.Owner),
                    }
                )
                .Distinct()
                .ToArray(),
            ticket.SourceUuids
        );
        var command = new NormalizationReviewCommand(
            Guid.NewGuid(),
            source.Owner.SubjectUuid,
            source.Owner.EditorialRevision,
            "accept",
            JsonSerializer.SerializeToElement(
                new
                {
                    extractionAttemptUuid = source.Batch.AttemptUuid,
                    claim,
                    assertion,
                    scope,
                    selectedPersonUuid = person.PersonUuid,
                }
            ),
            "Trusted migration fixture review of the genuine retained field",
            "upgrade-operator",
            ReadSet: proof
        );
        // This is the pre-56 domain callback, not a fabricated receipt or a claim that the new
        // preparation service ran on an older schema. Work locks the actual original read set
        // and commits the 53 decision/history together with its immutable 54 proof receipt.
        var result = await work.ApplyReviewAsync(
            command,
            async (c, t, ct) =>
            {
                (
                    await c.ExecuteScalarAsync<bool>(
                        new CommandDefinition(
                            "SELECT EXISTS(SELECT 1 FROM normalization.people WHERE uuid=@person AND kind='person')",
                            new { person = person.PersonUuid },
                            t,
                            cancellationToken: ct
                        )
                    )
                )
                    .Should()
                    .BeTrue();
                var written = await c.ExecuteAsync(
                    new CommandDefinition(
                        """
                        INSERT INTO normalization.source_actor_alias_review_state(uuid,source_uuid,
                            contributor_provider,contributor_type,contributor_key,resource_uuid,input_revision_uuid,
                            field_json_pointer,producer_version,assertion_uuid,actor_reference_uuid,field_sha256,
                            assertion_sha256,original_assertion,original_scope,source_resolution_owner_uuid,
                            review_revision,state,selected_person_uuid,last_operation_uuid)
                        VALUES(@state,@SourceUuid,@provider,@type,@key,@ResourceUuid,@InputRevisionUuid,
                            @FieldJsonPointer,@ProducerVersion,@AssertionUuid,@ActorReferenceUuid,@FieldSha256,
                            @assertionHash,@assertion::jsonb,@scope::jsonb,@owner,1,'accepted',@person,@operation)
                        """,
                        new
                        {
                            state = source.ReviewStateUuid,
                            claim.SourceUuid,
                            provider = claim.Contributor.Provider,
                            type = claim.Contributor.ResourceType,
                            key = claim.Contributor.ProviderKey,
                            claim.ResourceUuid,
                            claim.InputRevisionUuid,
                            claim.FieldJsonPointer,
                            claim.ProducerVersion,
                            claim.AssertionUuid,
                            claim.ActorReferenceUuid,
                            claim.FieldSha256,
                            assertionHash = Convert.ToHexStringLower(
                                SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(assertion))
                            ),
                            assertion = JsonSerializer.Serialize(assertion),
                            scope = JsonSerializer.Serialize(scope),
                            owner = source.Owner.SubjectUuid,
                            person = person.PersonUuid,
                            operation = command.OperationUuid,
                        },
                        t,
                        cancellationToken: ct
                    )
                );
                written.Should().Be(1);
            }
        );
        result.Replayed.Should().BeFalse();
        return command;
    }

    private static async Task CreateSchema55(string database)
    {
        await using var admin = Admin();
        await admin.OpenAsync();
        await admin.ExecuteAsync($"CREATE DATABASE {database} TEMPLATE template0");
        try
        {
            await using var catalog = Catalog(database);
            await catalog.OpenAsync();
            await catalog.ExecuteAsync(
                await File.ReadAllTextAsync(
                    Path.Combine(AppContext.BaseDirectory, "Fixtures", "catalog-schema.sql")
                ),
                commandTimeout: 120
            );
            foreach (var sql in SchemaThrough55)
            {
                await catalog.ExecuteAsync(sql, commandTimeout: 120);
            }
        }
        catch
        {
            await admin.ExecuteAsync($"DROP DATABASE {database} WITH (FORCE)");
            throw;
        }
    }

    private static Task Apply56(DbService db) =>
        db.WithWriteConnection(async connection =>
        {
            await using var transaction = await (
                (NpgsqlConnection)connection
            ).BeginTransactionAsync();
            await connection.ExecuteAsync(
                Migrations.AddSourceActorReviewPreparations.SchemaSql,
                transaction: transaction,
                commandTimeout: 120
            );
            await transaction.CommitAsync();
        });

    private static Task<bool> TableExists(DbService db, string table) =>
        db.WithWriteConnection(c =>
            c.ExecuteScalarAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM pg_tables WHERE schemaname='normalization' AND tablename=@table)",
                new { table }
            )
        );

    // Snapshot every existing normalization table, including immutable evidence, live review
    // projections, complete history, receipt hashes/proofs, Work clocks and contributor state.
    // Names come only from pg_tables; quote them as identifiers, never as caller-supplied SQL.
    private static Task<Dictionary<string, string>> ReadExistingTables(DbService db) =>
        db.WithWriteConnection(async c =>
        {
            var tables = await c.QueryAsync<string>(
                "SELECT tablename FROM pg_tables WHERE schemaname='normalization' ORDER BY tablename"
            );
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var table in tables)
            {
                var identifier = '"' + table.Replace("\"", "\"\"") + '"';
                result.Add(
                    table,
                    await c.QuerySingleAsync<string>(
                        $"SELECT coalesce(jsonb_agg(to_jsonb(value) ORDER BY to_jsonb(value)::text),'[]'::jsonb)::text FROM normalization.{identifier} value"
                    )
                );
            }
            return result;
        });

    private static Task<string> Receipt(DbService db, Guid operation) =>
        db.WithWriteConnection(c =>
            c.QuerySingleAsync<string>(
                "SELECT to_jsonb(value)::text FROM normalization.review_operations value WHERE uuid=@operation",
                new { operation }
            )
        );

    private static Task<string> History(DbService db, Guid state, long revision) =>
        db.WithWriteConnection(c =>
            c.QuerySingleAsync<string>(
                "SELECT to_jsonb(value)::text FROM normalization.source_actor_alias_review_state_history value WHERE uuid=@state AND review_revision=@revision",
                new { state, revision }
            )
        );

    private static async Task AssertImmutablePreparation(DbService db, Guid uuid)
    {
        var update = () =>
            db.WithWriteConnection(c =>
                c.ExecuteAsync(
                    "UPDATE normalization.source_actor_review_preparations SET request=request WHERE preparation_uuid=@uuid",
                    new { uuid }
                )
            );
        var failure = await update.Should().ThrowAsync<Exception>();
        var cause = failure.Which;
        while (cause is not PostgresException && cause.InnerException != null)
        {
            cause = cause.InnerException;
        }
        cause.Should().BeOfType<PostgresException>().Which.SqlState.Should().Be("P0001");
    }

    private static NormalizationSubjectObservation Observe(NormalizationSubjectState state) =>
        new(
            state.SubjectUuid,
            state.DesiredGeneration,
            state.EditorialRevision,
            state.PublishedWorkUuid
        );

    private static NpgsqlConnection Admin() =>
        new(
            "Host=127.0.0.1;Port=15432;Database=postgres;Username=relisten;Password=local_dev_password"
        );

    private static NpgsqlConnection Catalog(string database) =>
        new(
            $"Host=127.0.0.1;Port=15432;Database={database};Username=relisten;Password=local_dev_password"
        );

    private static async Task DropDatabase(string database)
    {
        const string prefix = "relisten_source_review_upgrade_";
        database.Should().StartWith(prefix);
        Guid.TryParseExact(database[prefix.Length..], "N", out _).Should().BeTrue();
        await using var admin = Admin();
        await admin.OpenAsync();
        await admin.ExecuteAsync($"DROP DATABASE {database} WITH (FORCE)");
    }

    private sealed record PersonSeed(Guid PersonUuid, NormalizationSubjectState Owner);

    private sealed record SourceSeed(
        NormalizationRecordingProposalBatch Batch,
        NormalizationRecordingSourceActorAliasScope Scope,
        SourceActorAliasCoordinates Claim,
        Guid ReviewStateUuid,
        NormalizationSubjectState Owner,
        NormalizationSubjectState ExtractionOwner
    );

    private sealed record ReviewState(long Revision, string State, Guid? Person, Guid Operation);
}
