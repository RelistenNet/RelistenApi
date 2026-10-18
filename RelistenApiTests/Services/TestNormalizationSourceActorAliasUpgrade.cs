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
/// Applies real 53 DDL to a nonempty 52 schema. Legacy rows are SQL migration fixtures,
/// not claims of service publication, operator adjudication, or identity truth.
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
