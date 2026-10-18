using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Relisten;
using Relisten.Api.Models.Normalization;
using Relisten.Data;

namespace RelistenApiTests.Services;

/// <summary>
/// Applies real 53 DDL to a nonempty 52 schema and adds 54 only at its review boundary.
/// Legacy rows are SQL migration fixtures, not claims of prior service publication,
/// operator adjudication, or identity truth. Public replay is verified by the final binary.
/// </summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestNormalizationSourceActorAliasUpgrade
{
    // This deliberately stops at 52 even after the shared test bootstrap advances.
    // The fixed sequence mirrors the source-pinned catalog bootstrap, not a 53 schema
    // with selected objects removed. It is a DDL upgrade test, not migrator bookkeeping.
    private static readonly string[] SchemaThrough52 =
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
    ];

    [Test]
    public async Task Nonempty52UpgradePreservesLegacyAuthorityAndCurrentWriterReviewContracts()
    {
        if (Environment.GetEnvironmentVariable("RELISTEN_LOCAL_INGESTION_TESTS") != "1")
        {
            Assert.Ignore("Run ./test-local.sh to include disposable local database coverage.");
        }

        var database = "relisten_alias_upgrade_" + Guid.NewGuid().ToString("N");
        var previousWrite = DbService.ConnStr;
        var previousRead = DbService.ReadOnlyConnStr;
        var created = false;
        try
        {
            await CreateSchema52(database);
            created = true;
            var environment = Host.CreateApplicationBuilder(
                new HostApplicationBuilderSettings { EnvironmentName = Environments.Production }
            ).Environment;
            var db = new DbService(
                $"postgresql://relisten:local_dev_password@127.0.0.1:15432/{database}",
                null,
                null,
                environment,
                NullLogger<DbService>.Instance
            );
            var work = new NormalizationWorkService(db);
            var evidence = new NormalizationEvidenceService(db);
            var people = new NormalizedPeopleService(db, work);
            var seed = await SeedHistoricalAliases(db, work, evidence);
            var before = await ReadLegacyImage(db);

            await ApplyActual53(db);

            (await ReadLegacyImage(db))
                .Should()
                .Be(
                    before,
                    "backfill must not rewrite canonical UUIDs, person revisions, raw assertions or history"
                );
            await AssertBaselineAuthority(db, seed);
            await AssertPublicCurrentReads(db, people, seed);
            await AssertAppendAndReviewContracts(db, work, evidence, people, seed);
        }
        finally
        {
            // DbService uses process-wide connection settings. Restore them before cleanup
            // so later fixtures cannot accidentally target this exclusively owned database.
            DbService.ConnStr = previousWrite;
            DbService.ReadOnlyConnStr = previousRead;
            if (created)
            {
                await DropOwnedDatabase(database);
            }
        }
    }

    [Test]
    public async Task Nonempty53ReviewReceiptsUpgradeTo54AndReplayWithoutNewEffects()
    {
        if (Environment.GetEnvironmentVariable("RELISTEN_LOCAL_INGESTION_TESTS") != "1")
        {
            Assert.Ignore("Run ./test-local.sh to include disposable local database coverage.");
        }

        var database = "relisten_alias_upgrade_" + Guid.NewGuid().ToString("N");
        var previousWrite = DbService.ConnStr;
        var previousRead = DbService.ReadOnlyConnStr;
        var created = false;
        try
        {
            // Reuse the fixed, actual pre-53 schema sequence. Never remove new columns or
            // disable history guards to pretend that the final binary is an older schema.
            await CreateSchema52(database);
            created = true;
            var environment = Host.CreateApplicationBuilder(
                new HostApplicationBuilderSettings { EnvironmentName = Environments.Production }
            ).Environment;
            var db = new DbService(
                $"postgresql://relisten:local_dev_password@127.0.0.1:15432/{database}",
                null,
                null,
                environment,
                NullLogger<DbService>.Instance
            );
            var work = new NormalizationWorkService(db);
            var evidence = new NormalizationEvidenceService(db);
            var people = new NormalizedPeopleService(db, work);
            var seed = await SeedHistoricalAliases(db, work, evidence);
            await ApplyActual53(db);
            await RequireReviewSchemaVersion(db, hasReadSet: false);

            // This is a labeled SQL legacy migration fixture using the pinned old hash shape,
            // not an assertion that this new binary ran a pre-54 public review implementation.
            var legacy = await SeedLegacyReviewReceipt(db, work, seed);
            var before = await ReadReviewUpgradeImage(db);
            var legacyReceiptBefore = await ReadReceiptImage(db, legacy.Command.OperationUuid);
            (await CountReviewReceipts(db)).Should().Be(1);

            await ApplyActual54(db);

            (await ReadReviewUpgradeImage(db))
                .Should()
                .BeEquivalentTo(
                    before,
                    "54 only adds nullable original proof; canonical history, owner clocks and old receipt hashes stay unchanged"
                );
            (await ReadReceiptProof(db, legacy.Command.OperationUuid)).Should().BeNull();
            (await ReadReceiptHash(db, legacy.Command.OperationUuid))
                .Should()
                .Be(legacy.CommandHash);
            await AssertLegacyPublicReplayWhileFactsAreLocked(
                database,
                db,
                work,
                people,
                seed,
                legacy
            );
            (await ReadReviewUpgradeImage(db)).Should().BeEquivalentTo(before);
            (await ReadReceiptImage(db, legacy.Command.OperationUuid))
                .Should()
                .Be(legacyReceiptBefore);
            (await CountReviewReceipts(db))
                .Should()
                .Be(
                    1,
                    "public replay must not append another decision or change its original audit proof"
                );

            var owner = (await work.GetSubjectAsync(seed.Owner))!;
            var ticket = await Begin(work, seed);
            var command = new NormalizationReviewCommand(
                Guid.NewGuid(),
                owner.SubjectUuid,
                owner.EditorialRevision,
                "accept",
                JsonSerializer.SerializeToElement(new { fixture = "post54-proof" }),
                "Migration fixture: retain a new one-off read proof",
                "upgrade-fixture-operator",
                ReadSet: new(
                    Inputs: ticket.Inputs,
                    OwnerObservations:
                    [
                        new(
                            owner.SubjectUuid,
                            owner.DesiredGeneration,
                            owner.EditorialRevision,
                            owner.PublishedWorkUuid
                        ),
                    ]
                )
            );
            var calls = 0;
            var applied = await work.ApplyReviewAsync(
                command,
                (_, _, _) =>
                {
                    calls++;
                    return Task.CompletedTask;
                }
            );
            applied.Replayed.Should().BeFalse();
            calls.Should().Be(1);
            (await CountReviewReceipts(db)).Should().Be(2);
            var retained = JsonSerializer.Deserialize<NormalizationReviewReadSet>(
                (await ReadReceiptProof(db, command.OperationUuid))!
            )!;
            retained.Should().BeEquivalentTo(command.ReadSet);
            (await ReadLegacyImage(db))
                .Should()
                .Be(
                    before.CanonicalImage,
                    "a generic fenced review does not manufacture a new canonical alias mutation"
                );
            (await ReadReceiptImage(db, legacy.Command.OperationUuid))
                .Should()
                .Be(legacyReceiptBefore);
        }
        finally
        {
            DbService.ConnStr = previousWrite;
            DbService.ReadOnlyConnStr = previousRead;
            if (created)
            {
                await DropOwnedDatabase(database);
            }
        }
    }

    private static async Task CreateSchema52(string database)
    {
        await using var admin = AdminConnection();
        await admin.OpenAsync();
        await admin.ExecuteAsync($"CREATE DATABASE {database} TEMPLATE template0");
        try
        {
            await using var catalog = CatalogConnection(database);
            await catalog.OpenAsync();
            var schema = await File.ReadAllTextAsync(
                Path.Combine(AppContext.BaseDirectory, "Fixtures", "catalog-schema.sql")
            );
            await catalog.ExecuteAsync(schema, commandTimeout: 120);
            foreach (var sql in SchemaThrough52)
            {
                await catalog.ExecuteAsync(sql, commandTimeout: 120);
            }
            var futureObjects = await catalog.ExecuteScalarAsync<int>(
                """
                SELECT count(*)::integer FROM information_schema.tables
                WHERE table_schema='normalization' AND table_name IN (
                    'source_actor_alias_assignments','person_alias_contributor_support',
                    'person_alias_unmanaged_support','source_actor_alias_review_state',
                    'source_actor_alias_review_state_history','current_person_aliases')
                """
            );
            futureObjects.Should().Be(0, "the pre-upgrade schema must not contain any 53 relation");
            var aliasColumns = await catalog.QueryAsync<string>(
                """
                SELECT column_name FROM information_schema.columns
                WHERE table_schema='normalization' AND table_name='person_aliases'
                ORDER BY ordinal_position
                """
            );
            aliasColumns
                .Should()
                .Equal(
                    "uuid",
                    "person_uuid",
                    "wording",
                    "name_key",
                    "input_revision_uuid",
                    "json_pointer",
                    "start_code_point",
                    "end_code_point",
                    "quote",
                    "state",
                    "basis",
                    "revision"
                );
        }
        catch
        {
            await admin.ExecuteAsync($"DROP DATABASE {database} WITH (FORCE)");
            throw;
        }
    }

    private static async Task<LegacyAliases> SeedHistoricalAliases(
        DbService db,
        NormalizationWorkService work,
        NormalizationEvidenceService evidence
    )
    {
        var person = Guid.NewGuid();
        var words = Enumerable
            .Range(0, 5)
            .Select(_ => "upgradealias" + Guid.NewGuid().ToString("N"))
            .ToArray();
        var resource = new NormalizationResourceKey("test", "alias-upgrade", person.ToString("D"));
        var capture = await Capture(evidence, resource, words, 0, 1);
        var owner = new NormalizationSubjectKey("people", "person", person);
        await work.BeginWorkAsync(Guid.NewGuid(), owner, "alias-upgrade-v1", [resource], []);
        var states = new[] { "accepted", "pending", "accepted", "rejected", "superseded" };
        var bases = new[]
        {
            "provider_assertion",
            "validated_match",
            "reviewed",
            "reviewed",
            "reviewed",
        };
        var aliases = words
            .Select(
                (word, index) =>
                    new LegacyAlias(
                        Guid.NewGuid(),
                        word,
                        states[index],
                        bases[index],
                        3 + index * 2,
                        new(capture.Revision.Uuid, "/names/" + index, 0, word.Length, word)
                    )
            )
            .ToArray();
        await db.WithWriteConnection(async connection =>
        {
            await using var transaction = await (
                (NpgsqlConnection)connection
            ).BeginTransactionAsync();
            await connection.ExecuteAsync(
                """
                INSERT INTO normalization.people(uuid,display_name,name_key,kind,revision,metadata_reviewed)
                VALUES(@person,@display,@nameKey,'person',17,true)
                """,
                new
                {
                    person,
                    display = "UpgradeFixturePerson",
                    nameKey = "upgradefixtureperson",
                },
                transaction
            );
            foreach (var alias in aliases)
            {
                await connection.ExecuteAsync(
                    """
                    INSERT INTO normalization.person_aliases(uuid,person_uuid,wording,name_key,input_revision_uuid,
                        json_pointer,start_code_point,end_code_point,quote,state,basis,revision)
                    VALUES(@Uuid,@person,@Wording,@nameKey,@InputRevisionUuid,@JsonPointer,@StartCodePoint,@EndCodePoint,@Quote,
                        @State,@Basis,@Revision)
                    """,
                    new
                    {
                        alias.Uuid,
                        person,
                        alias.Wording,
                        nameKey = NormalizedPeopleService.NameKey(alias.Wording),
                        alias.Evidence.InputRevisionUuid,
                        alias.Evidence.JsonPointer,
                        alias.Evidence.StartCodePoint,
                        alias.Evidence.EndCodePoint,
                        alias.Evidence.Quote,
                        alias.State,
                        alias.Basis,
                        alias.Revision,
                    },
                    transaction
                );
                if (alias.Basis == "reviewed")
                {
                    // A representative prior raw assertion snapshot. The old schema retains
                    // the operation coordinate without requiring a fabricated review service.
                    await connection.ExecuteAsync(
                        """
                        INSERT INTO normalization.person_aliases_history
                        SELECT uuid,person_uuid,wording,name_key,input_revision_uuid,json_pointer,
                            start_code_point,end_code_point,quote,'accepted','provider_assertion',
                            revision-1,@operation FROM normalization.person_aliases WHERE uuid=@Uuid
                        """,
                        new { alias.Uuid, operation = Guid.NewGuid() },
                        transaction
                    );
                }
            }
            await transaction.CommitAsync();
        });
        return new(person, owner, resource, aliases);
    }

    private static async Task ApplyActual53(DbService db)
    {
        await db.WithWriteConnection(async connection =>
        {
            await using var transaction = await (
                (NpgsqlConnection)connection
            ).BeginTransactionAsync();
            await connection.ExecuteAsync(
                Migrations.AddSourceActorAliasAuthority.SchemaSql,
                transaction: transaction,
                commandTimeout: 120
            );
            await transaction.CommitAsync();
        });
    }

    private static async Task ApplyActual54(DbService db)
    {
        await RequireReviewSchemaVersion(db, hasReadSet: false);
        await db.WithWriteConnection(async connection =>
        {
            await using var transaction = await (
                (NpgsqlConnection)connection
            ).BeginTransactionAsync();
            await connection.ExecuteAsync(
                Migrations.AddNormalizationReviewReadSet.SchemaSql,
                transaction: transaction,
                commandTimeout: 120
            );
            await transaction.CommitAsync();
        });
        await RequireReviewSchemaVersion(db, hasReadSet: true);
    }

    private static async Task RequireReviewSchemaVersion(DbService db, bool hasReadSet)
    {
        var present = await db.WithWriteConnection(c =>
            c.ExecuteScalarAsync<bool>(
                """
                SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema='normalization'
                    AND table_name='review_operations' AND column_name='read_set')
                """
            )
        );
        present.Should().Be(hasReadSet);
    }

    private static async Task<LegacyReview> SeedLegacyReviewReceipt(
        DbService db,
        NormalizationWorkService work,
        LegacyAliases seed
    )
    {
        var owner = (await work.GetSubjectAsync(seed.Owner))!;
        var accepted = seed.Aliases[0];
        var domain = new PersonMetadataDecision(
            Guid.NewGuid(),
            owner.SubjectUuid,
            owner.EditorialRevision,
            seed.PersonUuid,
            await PersonRevision(db, seed.PersonUuid),
            "retract",
            "Legacy migration fixture: withdraw an accepted alias",
            Alias: new(accepted.Uuid, seed.PersonUuid, accepted.Wording, accepted.Evidence),
            ExpectedAssertionRevision: accepted.Revision
        );
        const string trustedOperator = "legacy-upgrade-fixture-operator";
        var command = new NormalizationReviewCommand(
            domain.OperationUuid,
            domain.SubjectUuid,
            domain.ExpectedEditorialRevision,
            domain.Action,
            JsonSerializer.SerializeToElement(domain),
            domain.Reason,
            trustedOperator,
            AdditionalSubjectUuids: []
        );
        var legacyWire = new LegacyReviewWire(
            command.OperationUuid,
            command.SubjectUuid,
            command.ExpectedEditorialRevision,
            command.Action,
            command.Payload,
            command.Reason,
            command.Operator,
            []
        );
        var hash = LegacyCommandHash(legacyWire);
        await db.WithWriteConnection(async connection =>
        {
            await using var transaction = await (
                (NpgsqlConnection)connection
            ).BeginTransactionAsync();
            await connection.ExecuteAsync(
                """
                INSERT INTO normalization.person_aliases_history SELECT *,@OperationUuid
                    FROM normalization.person_aliases WHERE uuid=@alias;
                UPDATE normalization.person_aliases SET state='superseded',basis='reviewed',revision=revision+1
                    WHERE uuid=@alias;
                UPDATE normalization.people SET revision=revision+1,updated_at=clock_timestamp() WHERE uuid=@person;
                INSERT INTO normalization.review_operations(uuid,subject_uuid,command_hash,additional_subject_uuids,
                    action,payload,reason,operator_name,editorial_revision)
                VALUES(@OperationUuid,@SubjectUuid,@hash,ARRAY[]::uuid[],@Action,@payload::jsonb,@Reason,@Operator,@nextRevision);
                UPDATE normalization.work_subjects SET editorial_revision=editorial_revision+1,
                    desired_generation=desired_generation+1,updated_at=clock_timestamp() WHERE uuid=@SubjectUuid
                """,
                new
                {
                    domain.OperationUuid,
                    domain.SubjectUuid,
                    domain.Action,
                    domain.Reason,
                    command.Operator,
                    alias = accepted.Uuid,
                    person = seed.PersonUuid,
                    hash,
                    payload = command.Payload.GetRawText(),
                    nextRevision = owner.EditorialRevision + 1,
                },
                transaction
            );
            await transaction.CommitAsync();
        });
        return new(domain, command, hash, trustedOperator);
    }

    private static async Task AssertLegacyPublicReplayWhileFactsAreLocked(
        string database,
        DbService db,
        NormalizationWorkService work,
        NormalizedPeopleService people,
        LegacyAliases seed,
        LegacyReview legacy
    )
    {
        await using var blocker = CatalogConnection(database);
        await blocker.OpenAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await blocker.ExecuteAsync(
            """
            SELECT 1 FROM normalization.work_subjects WHERE uuid=@SubjectUuid FOR UPDATE;
            SELECT 1 FROM normalization.input_resources
                WHERE provider=@Provider AND resource_type=@ResourceType AND provider_key=@ProviderKey FOR UPDATE
            """,
            new
            {
                legacy.Command.SubjectUuid,
                seed.Resource.Provider,
                seed.Resource.ResourceType,
                seed.Resource.ProviderKey,
            },
            transaction
        );
        var replay = await work.ApplyReviewAsync(
                legacy.Command,
                (_, _, _) =>
                    throw new AssertionException(
                        "A committed legacy operation must not invoke its callback."
                    )
            )
            .WaitAsync(TimeSpan.FromSeconds(5));
        replay.Replayed.Should().BeTrue();
        replay.EditorialRevision.Should().Be(legacy.Command.ExpectedEditorialRevision + 1);
        // Both CAS values now deliberately differ from the original command. This public
        // metadata wrapper can only return success by honoring the immutable old receipt.
        (
            await people
                .ApplyMetadataDecisionAsync(legacy.DomainCommand, legacy.TrustedOperator)
                .WaitAsync(TimeSpan.FromSeconds(5))
        )
            .Replayed.Should()
            .BeTrue();
        (await ReadReceiptProof(db, legacy.Command.OperationUuid)).Should().BeNull();
    }

    private static async Task<ReviewUpgradeImage> ReadReviewUpgradeImage(DbService db) =>
        new(
            await ReadLegacyImage(db),
            await db.WithWriteConnection(c =>
                c.QuerySingleAsync<string>(
                    """
                    SELECT jsonb_build_object(
                        'receipts',(SELECT jsonb_agg(to_jsonb(r)-'read_set' ORDER BY uuid)
                            FROM normalization.review_operations r),
                        'owners',(SELECT jsonb_agg(to_jsonb(s) ORDER BY uuid) FROM normalization.work_subjects s))::text
                    """
                )
            )
        );

    private static Task<string> ReadReceiptImage(DbService db, Guid uuid) =>
        db.WithWriteConnection(c =>
            c.QuerySingleAsync<string>(
                "SELECT (to_jsonb(r)-'read_set')::text FROM normalization.review_operations r WHERE uuid=@uuid",
                new { uuid }
            )
        );

    private static Task<string?> ReadReceiptProof(DbService db, Guid uuid) =>
        db.WithWriteConnection(c =>
            c.QuerySingleOrDefaultAsync<string>(
                "SELECT read_set::text FROM normalization.review_operations WHERE uuid=@uuid",
                new { uuid }
            )
        );

    private static Task<string> ReadReceiptHash(DbService db, Guid uuid) =>
        db.WithWriteConnection(c =>
            c.QuerySingleAsync<string>(
                "SELECT command_hash FROM normalization.review_operations WHERE uuid=@uuid",
                new { uuid }
            )
        );

    private static Task<int> CountReviewReceipts(DbService db) =>
        db.WithWriteConnection(c =>
            c.QuerySingleAsync<int>("SELECT count(*)::integer FROM normalization.review_operations")
        );

    // This is the pinned pre-54 canonical hashing contract, with a wire DTO that has no
    // ReadSet member. Keeping it independent of the new command proves legacy hash replay.
    private static string LegacyCommandHash(LegacyReviewWire command)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteLegacyCanonical(writer, JsonSerializer.SerializeToElement(command));
        }
        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }

    private static void WriteLegacyCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (
                var property in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal)
            )
            {
                writer.WritePropertyName(property.Name);
                WriteLegacyCanonical(writer, property.Value);
            }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in value.EnumerateArray())
            {
                WriteLegacyCanonical(writer, item);
            }
            writer.WriteEndArray();
        }
        else
        {
            value.WriteTo(writer);
        }
    }

    private static Task<string> ReadLegacyImage(DbService db) =>
        db.WithWriteConnection(c =>
            c.QuerySingleAsync<string>(
                """
                SELECT jsonb_build_object(
                    'people',(SELECT jsonb_agg(to_jsonb(p) ORDER BY uuid) FROM normalization.people p),
                    'aliases',(SELECT jsonb_agg(to_jsonb(a) ORDER BY uuid) FROM normalization.person_aliases a),
                    'history',(SELECT jsonb_agg(to_jsonb(h) ORDER BY uuid,revision)
                        FROM normalization.person_aliases_history h),
                    'universes',(SELECT jsonb_agg(to_jsonb(u) ORDER BY domain,scope_uuid)
                        FROM normalization.candidate_universe_revisions u))::text
                """
            )
        );

    private static async Task AssertBaselineAuthority(DbService db, LegacyAliases seed)
    {
        var baseline = await db.WithWriteConnection(c =>
            c.QueryAsync<Guid>(
                """
                SELECT alias_uuid FROM normalization.person_alias_unmanaged_support
                WHERE introduced_work_uuid IS NULL ORDER BY alias_uuid
                """
            )
        );
        baseline.Should().BeEquivalentTo(seed.Aliases.Select(alias => alias.Uuid));
        var managed = await db.WithWriteConnection(c =>
            c.ExecuteScalarAsync<long>(
                """
                SELECT (SELECT count(*) FROM normalization.source_actor_alias_assignments)
                    +(SELECT count(*) FROM normalization.person_alias_contributor_support)
                    +(SELECT count(*) FROM normalization.source_actor_alias_review_state)
                """
            )
        );
        managed
            .Should()
            .Be(
                0,
                "migration creates baseline authority, never source assignments or review decisions"
            );
    }

    private static async Task AssertPublicCurrentReads(
        DbService db,
        NormalizedPeopleService people,
        LegacyAliases seed
    )
    {
        var reader = new NormalizationReadService(
            db,
            Options.Create(new NormalizationReadOptions { Enabled = true })
        );
        var person = await reader.GetPersonAsync(seed.PersonUuid);
        person.Should().NotBeNull();
        person!
            .aliases.Should()
            .BeEquivalentTo(
                seed.Aliases.Where(alias => alias.State == "accepted")
                    .Select(alias => alias.Wording)
            );
        person.revision.Should().Be(17);
        foreach (var alias in seed.Aliases)
        {
            var current = await people.FindCandidatesAsync(alias.Wording);
            current
                .Select(hit => hit.Uuid)
                .Contains(seed.PersonUuid)
                .Should()
                .Be(alias.State == "accepted");
        }
        var facts = await new NormalizationCandidateService(db).FindPeopleFactsAsync(
            new(IdentityMatchingDomain.People, [seed.Aliases[0].Wording], [])
        );
        facts.Complete.Should().BeTrue();
        facts
            .Facts.Single()
            .AcceptedAliasMatchingNames.Should()
            .BeEquivalentTo(
                seed.Aliases.Where(alias => alias.State == "accepted")
                    .Select(alias => alias.Wording)
            );
    }

    private static async Task AssertAppendAndReviewContracts(
        DbService db,
        NormalizationWorkService work,
        NormalizationEvidenceService evidence,
        NormalizedPeopleService people,
        LegacyAliases seed
    )
    {
        var accepted = seed.Aliases[0];
        var beforeDuplicate = await ReadLegacyImage(db);
        var duplicateWork = await Begin(work, seed);
        var publication = new PeoplePublication(
            [],
            [new(Guid.NewGuid(), seed.PersonUuid, accepted.Wording, accepted.Evidence)],
            [],
            []
        );
        (await people.PublishAsync(duplicateWork.WorkUuid, publication))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);
        (await people.PublishAsync(duplicateWork.WorkUuid, publication)).Replayed.Should().BeTrue();
        // Work acknowledgment can advance independently; canonical facts and authority do not.
        (await ReadLegacyImage(db))
            .Should()
            .Be(beforeDuplicate);
        (
            await db.WithWriteConnection(c =>
                c.QuerySingleAsync<long>(
                    "SELECT count(*) FROM normalization.person_aliases WHERE person_uuid=@person",
                    new { person = seed.PersonUuid }
                )
            )
        )
            .Should()
            .Be(5);

        var changed = await Capture(
            evidence,
            seed.Resource,
            seed.Aliases.Select(alias => alias.Wording).ToArray(),
            1,
            2
        );
        foreach (var blocked in new[] { seed.Aliases[3], seed.Aliases[4] })
        {
            var ticket = await Begin(work, seed);
            var attempt = new PeoplePublication(
                [],
                [
                    new(
                        Guid.NewGuid(),
                        seed.PersonUuid,
                        blocked.Wording,
                        blocked.Evidence with
                        {
                            InputRevisionUuid = changed.Revision.Uuid,
                        }
                    ),
                ],
                [],
                []
            );
            (await people.PublishAsync(ticket.WorkUuid, attempt))
                .Status.Should()
                .Be(NormalizationPublicationStatus.Published);
            (await people.FindCandidatesAsync(blocked.Wording)).Should().BeEmpty();
            (
                await db.WithWriteConnection(c =>
                    c.ExecuteScalarAsync<bool>(
                        """
                        SELECT EXISTS(SELECT 1 FROM normalization.person_aliases a
                            JOIN normalization.person_alias_unmanaged_support support ON support.alias_uuid=a.uuid
                            WHERE a.person_uuid=@person AND a.name_key=@name AND a.state='pending'
                                AND a.input_revision_uuid=@revision)
                        """,
                        new
                        {
                            person = seed.PersonUuid,
                            name = NormalizedPeopleService.NameKey(blocked.Wording),
                            revision = changed.Revision.Uuid,
                        }
                    )
                )
            ).Should().BeFalse();
        }

        // All 53 backfill/read/append/veto assertions above deliberately ran without 54.
        // The final binary's shared review INSERT now includes read_set, even for null proof.
        var before54 = await ReadLegacyImage(db);
        await ApplyActual54(db);
        (await ReadLegacyImage(db))
            .Should()
            .Be(
                before54,
                "the review-audit schema addition must not change any 53 alias authority"
            );

        var owner = (await work.GetSubjectAsync(seed.Owner))!;
        var correction = new PersonMetadataDecision(
            Guid.NewGuid(),
            owner.SubjectUuid,
            owner.EditorialRevision,
            seed.PersonUuid,
            await PersonRevision(db, seed.PersonUuid),
            "retract",
            "Migration fixture: withdraw the previously accepted alias",
            Alias: new(accepted.Uuid, seed.PersonUuid, accepted.Wording, accepted.Evidence),
            ExpectedAssertionRevision: accepted.Revision
        );
        (await people.ApplyMetadataDecisionAsync(correction, "upgrade-fixture-operator"))
            .Replayed.Should()
            .BeFalse();
        (await people.ApplyMetadataDecisionAsync(correction, "upgrade-fixture-operator"))
            .Replayed.Should()
            .BeTrue();
        var afterReview = await Begin(work, seed);
        (
            await people.PublishAsync(
                afterReview.WorkUuid,
                new(
                    [],
                    [
                        new(
                            Guid.NewGuid(),
                            seed.PersonUuid,
                            accepted.Wording,
                            accepted.Evidence with
                            {
                                InputRevisionUuid = changed.Revision.Uuid,
                            }
                        ),
                    ],
                    [],
                    []
                )
            )
        )
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);
        (await people.FindCandidatesAsync(accepted.Wording)).Should().BeEmpty();
        (
            await db.WithWriteConnection(c =>
                c.ExecuteScalarAsync<Guid>(
                    "SELECT uuid FROM normalization.person_aliases WHERE uuid=@Uuid",
                    accepted
                )
            )
        )
            .Should()
            .Be(accepted.Uuid);
    }

    private static async Task<NormalizationWorkTicket> Begin(
        NormalizationWorkService work,
        LegacyAliases seed
    )
    {
        var owner = (await work.GetSubjectAsync(seed.Owner))!;
        return await work.BeginWorkAsync(
            Guid.NewGuid(),
            seed.Owner,
            "alias-upgrade-v1",
            [seed.Resource],
            [],
            owner.DesiredGeneration
        );
    }

    private static Task<long> PersonRevision(DbService db, Guid person) =>
        db.WithWriteConnection(c =>
            c.QuerySingleAsync<long>(
                "SELECT revision FROM normalization.people WHERE uuid=@person",
                new { person }
            )
        );

    private static Task<NormalizationCaptureResult> Capture(
        NormalizationEvidenceService evidence,
        NormalizationResourceKey resource,
        string[] names,
        long expectedGeneration,
        int round
    ) =>
        evidence.CaptureInputAsync(
            new(
                Guid.NewGuid(),
                resource,
                JsonSerializer.SerializeToUtf8Bytes(new { names, round }),
                "application/json",
                "alias-upgrade-fixture-v1",
                expectedGeneration,
                DateTimeOffset.UtcNow
            )
        );

    private static NpgsqlConnection AdminConnection() =>
        new(
            "Host=127.0.0.1;Port=15432;Database=postgres;Username=relisten;Password=local_dev_password"
        );

    private static NpgsqlConnection CatalogConnection(string database) =>
        new(
            $"Host=127.0.0.1;Port=15432;Database={database};Username=relisten;Password=local_dev_password"
        );

    private static async Task DropOwnedDatabase(string database)
    {
        database.Should().StartWith("relisten_alias_upgrade_");
        Guid.TryParseExact(database["relisten_alias_upgrade_".Length..], "N", out _)
            .Should()
            .BeTrue();
        await using var admin = AdminConnection();
        await admin.OpenAsync();
        await admin.ExecuteAsync($"DROP DATABASE {database} WITH (FORCE)");
    }

    private sealed record LegacyReviewWire(
        Guid OperationUuid,
        Guid SubjectUuid,
        long ExpectedEditorialRevision,
        string Action,
        JsonElement Payload,
        string Reason,
        string Operator,
        IReadOnlyList<Guid> AdditionalSubjectUuids
    );

    private sealed record LegacyReview(
        PersonMetadataDecision DomainCommand,
        NormalizationReviewCommand Command,
        string CommandHash,
        string TrustedOperator
    );

    private sealed record ReviewUpgradeImage(string CanonicalImage, string ReceiptsAndOwnersImage);

    private sealed record LegacyAlias(
        Guid Uuid,
        string Wording,
        string State,
        string Basis,
        long Revision,
        NormalizedEvidenceReference Evidence
    );

    private sealed record LegacyAliases(
        Guid PersonUuid,
        NormalizationSubjectKey Owner,
        NormalizationResourceKey Resource,
        LegacyAlias[] Aliases
    );
}
