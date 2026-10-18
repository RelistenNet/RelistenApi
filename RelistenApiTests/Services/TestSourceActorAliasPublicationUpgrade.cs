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
/// Applies actual 57 DDL to genuine canonical, extraction, proposal and reviewed source
/// history under schema 56, then publishes through retained application/stage APIs.
/// </summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestSourceActorAliasPublicationUpgrade
{
    private const string Operator = "source-publication-upgrade-operator";
    private const string ReviewReason =
        "Reviewed the complete retained field against the existing person.";

    // Pin the actual historical schema; never remove current objects to imitate an upgrade.
    private static readonly string[] SchemaThrough56 =
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
        Migrations.AddSourceActorReviewPreparations.SchemaSql,
    ];

    [Test]
    public async Task Nonempty56UpgradeRetainsOriginalAuthorityAndPublishesThroughNewStageStorage()
    {
        if (Environment.GetEnvironmentVariable("RELISTEN_LOCAL_INGESTION_TESTS") != "1")
        {
            Assert.Ignore("Run ./test-local.sh to include disposable local database coverage.");
        }
        var database = "relisten_source_publication_upgrade_" + Guid.NewGuid().ToString("N");
        var previousWrite = DbService.ConnStr;
        var previousRead = DbService.ReadOnlyConnStr;
        var created = false;
        try
        {
            await CreateSchema56(database);
            created = true;
            var services = new Services(
                new DbService(
                    $"postgresql://relisten:local_dev_password@127.0.0.1:15432/{database}",
                    null,
                    null,
                    Host.CreateApplicationBuilder(
                        new HostApplicationBuilderSettings
                        {
                            EnvironmentName = Environments.Production,
                        }
                    ).Environment,
                    NullLogger<DbService>.Instance
                )
            );
            var person = await PublishNativePerson(services);
            var source = await PublishReviewedSource(services, person);
            var nativeBefore = await ReadNativeAuthority(services.Db, person);
            var before = await ReadTables(services.Db);
            var originalWorkUuids = await services.Db.WithWriteConnection(c =>
                c.QueryAsync<Guid>("SELECT uuid FROM normalization.work_runs ORDER BY uuid")
            );
            var originalWorkRows = await ReadWorkRows(services.Db, originalWorkUuids.ToArray());
            before.Keys.Should().Contain("source_actor_review_preparations");
            before
                .Keys.Should()
                .NotContain([
                    "source_actor_alias_applications",
                    "source_actor_alias_person_stages",
                ]);

            await Apply57(services.Db);

            var after = await ReadTables(services.Db);
            after
                .Keys.Should()
                .BeEquivalentTo(
                    before.Keys.Concat([
                        "source_actor_alias_applications",
                        "source_actor_alias_person_stages",
                    ])
                );
            foreach (var (table, rows) in before)
            {
                after[table].Should().Be(rows, "57 must preserve every prior row in {0}", table);
            }
            after["source_actor_alias_applications"].Should().Be("[]");
            after["source_actor_alias_person_stages"].Should().Be("[]");

            var applications = new SourceActorAliasApplicationStore(services.Db);
            var publisher = new SourceActorAliasApplicationService(
                services.Db,
                services.Resolutions,
                services.Candidates,
                services.Policies,
                new(new(), new()),
                applications,
                services.People,
                services.Work
            );
            var application = await publisher.ApplyAsync(
                Guid.NewGuid(),
                source.Reviewed.AttemptUuid
            );
            await AssertPublication(services, applications, application, person, source);
            (await ReadNativeAuthority(services.Db, person)).Should().Be(nativeBefore);
            (await ReadWorkRows(services.Db, originalWorkUuids.ToArray()))
                .Should()
                .Be(originalWorkRows);
            var published = await ReadTables(services.Db);
            foreach (
                var table in new[]
                {
                    "review_operations",
                    "source_actor_alias_review_state",
                    "source_actor_alias_review_state_history",
                    "source_actor_review_preparations",
                    "recording_processing_attempts",
                    "source_actor_resolution_attempts",
                }
            )
            {
                published[table]
                    .Should()
                    .Be(before[table], "publication must retain original {0}", table);
            }

            await AssertOriginalReplay(
                services,
                applications,
                publisher,
                application,
                person,
                source
            );

            (await ReadTables(services.Db))
                .Should()
                .BeEquivalentTo(
                    published,
                    "committed application, review, extraction and Work replays must add no effects"
                );
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

    private static async Task<PersonSeed> PublishNativePerson(Services services)
    {
        const string name = "Upgrade Review Robin";
        const string alias = "Upgrade Review Finch";
        var person = new CanonicalPerson(Guid.NewGuid(), name);
        var aliasUuid = Guid.NewGuid();
        var resource = new NormalizationResourceKey(
            "test",
            "publication-upgrade-person",
            person.Uuid.ToString("N")
        );
        var capture = await services.Evidence.CaptureInputAsync(
            new(
                Guid.NewGuid(),
                resource,
                JsonSerializer.SerializeToUtf8Bytes(new { name, alias }),
                "application/json",
                "fixture",
                0,
                DateTimeOffset.UtcNow
            )
        );
        var ticket = await services.Work.BeginWorkAsync(
            Guid.NewGuid(),
            new("people", "person", person.Uuid),
            "source-publication-upgrade-native-v1",
            [resource],
            []
        );
        (
            await services.People.PublishAsync(
                ticket.WorkUuid,
                new(
                    [person],
                    [
                        new(
                            person.Uuid,
                            person.Uuid,
                            name,
                            new(capture.Revision.Uuid, "/name", 0, name.Length, name)
                        ),
                        new(
                            aliasUuid,
                            person.Uuid,
                            alias,
                            new(capture.Revision.Uuid, "/alias", 0, alias.Length, alias)
                        ),
                    ],
                    [],
                    []
                )
            )
        )
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);
        Guid[] nativeAliases = [person.Uuid, aliasUuid];
        (
            await services.Db.WithWriteConnection(c =>
                c.ExecuteScalarAsync<int>(
                    """
                    SELECT count(*)::integer FROM normalization.current_person_aliases alias
                    JOIN normalization.person_alias_unmanaged_support authority ON authority.alias_uuid=alias.uuid
                    WHERE alias.uuid=ANY(@nativeAliases)
                    """,
                    new { nativeAliases }
                )
            )
        ).Should().Be(2);
        return new(person.Uuid, name, alias, nativeAliases, ticket);
    }

    private static async Task<ReviewedSource> PublishReviewedSource(
        Services services,
        PersonSeed person
    )
    {
        var sourceUuid = await CreateCatalogSource(
            services.Db,
            $"Personnel:\n{person.Name} aka {person.Alias} - vocals"
        );
        var result = await services.Extractor.ProcessAsync(Guid.NewGuid(), sourceUuid);
        result.Disposition.Should().Be(NormalizationRecordingProcessingDisposition.Published);
        var extraction = (await services.Extractions.GetAttemptBatchAsync(result.AttemptUuid))!;
        var first = await services.Resolver.ResolveAsync(Guid.NewGuid(), extraction.AttemptUuid);
        first.Result!.Disposition.Should().Be(SourceActorResolutionDisposition.Published);
        var claim = first
            .Preparation!.Inputs.SelectMany(input => input.Scopes)
            .SelectMany(scope => scope.Claims)
            .Single();
        var owner = (
            await services.Work.GetSubjectAsync(SourceActorResolutionService.Owner(sourceUuid))
        )!;
        var revision = await services.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<long>(
                "SELECT revision FROM normalization.people WHERE uuid=@uuid",
                new { uuid = person.Uuid }
            )
        );
        var preparation = await services.Reviews.PrepareAsync(
            Guid.NewGuid(),
            new(
                extraction.AttemptUuid,
                claim.Coordinates,
                SourceActorReviewAction.Accept,
                owner.EditorialRevision,
                0,
                person.Uuid,
                revision
            )
        );
        var operation = Guid.NewGuid();
        (
            await services.Reviews.ApplyAsync(
                operation,
                preparation.PreparationUuid,
                ReviewReason,
                Operator
            )
        )
            .Replayed.Should()
            .BeFalse();
        var reviewed = await services.Resolver.ResolveAsync(Guid.NewGuid(), extraction.AttemptUuid);
        reviewed.Result!.Disposition.Should().Be(SourceActorResolutionDisposition.Published);
        var accepted = reviewed
            .Preparation!.Inputs.SelectMany(input => input.Scopes)
            .SelectMany(scope => scope.Claims)
            .Single();
        accepted.Decision.Action.Should().Be(SourceActorResolutionAction.Review);
        accepted.Decision.Reason.Should().Be("reviewed_source_acceptance");
        accepted.Decision.PersonUuid.Should().Be(person.Uuid);
        var retained = reviewed.Preparation.SourceReviews.Reviews.Should().ContainSingle().Which;
        retained.LastOperationUuid.Should().Be(operation);
        retained.SelectedPersonUuid.Should().Be(person.Uuid);
        return new(
            sourceUuid,
            extraction,
            first,
            reviewed,
            preparation,
            operation,
            (await services.Work.GetSubjectAsync(owner.SubjectUuid))!,
            (await services.Work.GetSubjectAsync(extraction.Ticket.SubjectUuid))!
        );
    }

    private static async Task AssertPublication(
        Services services,
        SourceActorAliasApplicationStore applications,
        SourceActorAliasApplication application,
        PersonSeed person,
        ReviewedSource source
    )
    {
        application
            .Result!.Disposition.Should()
            .Be(SourceActorAliasApplicationDisposition.Complete);
        application.Result.Replayed.Should().BeFalse();
        application.Stages.Should().NotBeEmpty();
        var committed = application.Stages.Where(stage => stage.Receipt != null).ToArray();
        committed.Should().HaveCount(application.Stages.Count);
        application
            .Result.CommittedStageUuids.Should()
            .BeEquivalentTo(committed.Select(stage => stage.StageUuid));
        var names = new List<SourceActorAliasNameOutcome>();
        foreach (var header in committed)
        {
            var stage = (await applications.GetStageAsync(header.StageUuid))!;
            stage.OriginalPersonUuid.Should().Be(person.Uuid);
            stage.Ticket.Should().NotBeNull();
            stage.Receipt.Should().NotBeNull();
            stage.Receipt!.WorkUuid.Should().Be(stage.WorkUuid);
            stage.Facts.Policy.Should().BeNull();
            stage.Facts.CandidatePools.Should().BeEmpty();
            stage
                .Facts.SourceReviews.Reviews.Should()
                .Contain(review =>
                    review.LastOperationUuid == source.ReviewOperation
                    && review.SelectedPersonUuid == person.Uuid
                );
            (await applications.GetApplicationForStageAsync(stage.StageUuid))!
                .AttemptUuid.Should()
                .Be(application.AttemptUuid);
            names.AddRange(stage.Receipt.Names);
            foreach (var name in stage.Receipt.Names)
            {
                name.Disposition.Should()
                    .BeOneOf(
                        SourceActorAliasNameDisposition.Accepted,
                        SourceActorAliasNameDisposition.ExactDuplicate
                    );
                name.ActualAliasUuid.Should().NotBeNull();
                name.AssignmentUuid.Should().NotBeNull();
                name.SupportUuid.Should().NotBeNull();
                var rows = await services.Db.WithWriteConnection(c =>
                    c.ExecuteScalarAsync<int>(
                        """
                        SELECT count(*)::integer FROM normalization.person_aliases alias
                        JOIN normalization.person_alias_contributor_support support ON support.alias_uuid=alias.uuid
                        JOIN normalization.source_actor_alias_assignments assignment ON assignment.uuid=support.assignment_uuid
                        WHERE alias.uuid=@alias AND alias.person_uuid=@person AND alias.wording=@wording AND alias.state='accepted'
                            AND support.uuid=@support AND support.retired_work_uuid IS NULL
                            AND assignment.uuid=@assignment AND assignment.source_uuid=@source AND assignment.person_uuid=@person
                            AND assignment.input_revision_uuid=@input AND assignment.basis='reviewed' AND assignment.retired_work_uuid IS NULL
                            AND assignment.application_attempt_uuid=@application
                        """,
                        new
                        {
                            alias = name.ActualAliasUuid,
                            person = person.Uuid,
                            wording = name.Wording,
                            support = name.SupportUuid,
                            assignment = name.AssignmentUuid,
                            source = source.SourceUuid,
                            input = source.Preparation.Request.Claim.InputRevisionUuid,
                            application = application.AttemptUuid,
                        }
                    )
                );
                rows.Should()
                    .Be(
                        1,
                        "the public receipt must name its actual committed authority and canonical assertion"
                    );
            }
        }
        names.Select(name => name.Wording).Should().Contain([person.Name, person.Alias]);
        (await applications.GetApplicationAsync(application.AttemptUuid))
            .Should()
            .BeEquivalentTo(
                application,
                options => options.WithStrictOrdering().AllowingInfiniteRecursion()
            );
        (await services.Work.GetSubjectAsync(source.SourceOwner.SubjectUuid))
            .Should()
            .BeEquivalentTo(source.SourceOwner);
        (await services.Work.GetSubjectAsync(source.ExtractionOwner.SubjectUuid))
            .Should()
            .BeEquivalentTo(source.ExtractionOwner);
        (await services.People.ReadCreditContextAsync(source.SourceUuid))
            .Credits.Should()
            .BeEmpty();
        (
            await services.Db.WithWriteConnection(c =>
                c.ExecuteScalarAsync<int>(
                    "SELECT count(*)::integer FROM normalization.current_person_aliases WHERE uuid=ANY(@aliases)",
                    new { aliases = person.NativeAliasUuids }
                )
            )
        )
            .Should()
            .Be(person.NativeAliasUuids.Length);
    }

    private static async Task AssertOriginalReplay(
        Services services,
        SourceActorAliasApplicationStore applications,
        SourceActorAliasApplicationService publisher,
        SourceActorAliasApplication application,
        PersonSeed person,
        ReviewedSource source
    )
    {
        (
            await services.Reviews.ApplyAsync(
                source.ReviewOperation,
                source.Preparation.PreparationUuid,
                ReviewReason,
                Operator
            )
        )
            .Replayed.Should()
            .BeTrue();
        (
            await services.Work.TryPublishAsync(
                person.Ticket.WorkUuid,
                (_, _, _) =>
                    throw new AssertionException(
                        "A committed native Work receipt must not invoke its writer again"
                    )
            )
        )
            .Replayed.Should()
            .BeTrue();
        (await services.Extractor.ProcessAsync(source.Extraction.AttemptUuid, source.SourceUuid))
            .Replayed.Should()
            .BeTrue();
        var proposal = await services.Resolver.ResolveAsync(
            source.Original.AttemptUuid,
            source.Extraction.AttemptUuid
        );
        proposal.Result!.Replayed.Should().BeTrue();
        proposal
            .Preparation.Should()
            .BeEquivalentTo(
                source.Original.Preparation,
                options => options.WithStrictOrdering().AllowingInfiniteRecursion()
            );
        var replay = await publisher.ApplyAsync(
            application.AttemptUuid,
            source.Reviewed.AttemptUuid
        );
        replay.Result!.Replayed.Should().BeTrue();
        replay.Plan.Should().BeEquivalentTo(application.Plan);
        replay
            .Stages.Should()
            .BeEquivalentTo(
                application.Stages,
                options => options.WithStrictOrdering().AllowingInfiniteRecursion()
            );
        replay.Result.CommittedStageUuids.Should().Equal(application.Result!.CommittedStageUuids);
        (await applications.GetApplicationAsync(application.AttemptUuid))!
            .Result!.Replayed.Should()
            .BeFalse();
    }

    private static Task<Guid> CreateCatalogSource(DbService db, string description) =>
        db.WithWriteConnection(async c =>
        {
            var artist = await c.QuerySingleAsync<int>(
                "INSERT INTO artists(name,slug,uuid,updated_at) VALUES('Source publication upgrade',@slug,@uuid,now()) RETURNING id",
                new { slug = Guid.NewGuid().ToString("N"), uuid = Guid.NewGuid() }
            );
            var sourceUuid = Guid.NewGuid();
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
                    description,
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
            return sourceUuid;
        });

    private static Task<string> ReadNativeAuthority(DbService db, PersonSeed person) =>
        db.WithWriteConnection(c =>
            c.QuerySingleAsync<string>(
                """
                SELECT jsonb_build_object(
                    'identity',(SELECT jsonb_build_object('uuid',uuid,'display_name',display_name,'kind',kind,
                        'metadata_reviewed',metadata_reviewed) FROM normalization.people WHERE uuid=@person),
                    'aliases',(SELECT jsonb_agg(to_jsonb(alias) ORDER BY uuid) FROM normalization.person_aliases alias
                        WHERE uuid=ANY(@aliases)),
                    'unmanaged',(SELECT jsonb_agg(to_jsonb(authority) ORDER BY alias_uuid)
                        FROM normalization.person_alias_unmanaged_support authority WHERE alias_uuid=ANY(@aliases)))::text
                """,
                new { person = person.Uuid, aliases = person.NativeAliasUuids }
            )
        );

    private static Task<string> ReadWorkRows(DbService db, Guid[] workUuids) =>
        db.WithWriteConnection(c =>
            c.QuerySingleAsync<string>(
                "SELECT coalesce(jsonb_agg(to_jsonb(value) ORDER BY uuid),'[]'::jsonb)::text FROM normalization.work_runs value WHERE uuid=ANY(@workUuids)",
                new { workUuids }
            )
        );

    // No concurrent actor uses this exclusively owned database. Snapshot every base table,
    // including full old evidence, canonical rows, source reviews, original Work and receipts.
    private static Task<Dictionary<string, string>> ReadTables(DbService db) =>
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

    private static async Task CreateSchema56(string database)
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
            foreach (var sql in SchemaThrough56)
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

    private static Task Apply57(DbService db) =>
        db.WithWriteConnection(async c =>
        {
            await using var t = await ((NpgsqlConnection)c).BeginTransactionAsync();
            await c.ExecuteAsync(
                Migrations.AddSourceActorAliasPublication.SchemaSql,
                transaction: t,
                commandTimeout: 120
            );
            await t.CommitAsync();
        });

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
        const string prefix = "relisten_source_publication_upgrade_";
        database.Should().StartWith(prefix);
        Guid.TryParseExact(database[prefix.Length..], "N", out _).Should().BeTrue();
        await using var admin = Admin();
        await admin.OpenAsync();
        await admin.ExecuteAsync($"DROP DATABASE {database} WITH (FORCE)");
    }

    private sealed class Services
    {
        public Services(DbService db)
        {
            Db = db;
            Work = new(db);
            Evidence = new(db);
            People = new(db, Work);
            Extractions = new(db);
            Extractor = new(
                new(db, Evidence),
                Evidence,
                new(db),
                new(),
                new(new(), new()),
                Work,
                Extractions,
                new(db)
            );
            Candidates = new(db);
            Policies = new(db, Work);
            Resolutions = new(db);
            Resolver = new(Candidates, Policies, new(new(), new()), Resolutions, Work);
            Reviews = new(db, Work, new(db));
        }

        public DbService Db { get; }
        public NormalizationWorkService Work { get; }
        public NormalizationEvidenceService Evidence { get; }
        public NormalizedPeopleService People { get; }
        public NormalizationRecordingProposalService Extractions { get; }
        public NormalizationRecordingProcessor Extractor { get; }
        public NormalizationCandidateService Candidates { get; }
        public PeopleResolutionPolicyService Policies { get; }
        public SourceActorResolutionStore Resolutions { get; }
        public SourceActorResolutionService Resolver { get; }
        public SourceActorReviewService Reviews { get; }
    }

    private sealed record PersonSeed(
        Guid Uuid,
        string Name,
        string Alias,
        Guid[] NativeAliasUuids,
        NormalizationWorkTicket Ticket
    );

    private sealed record ReviewedSource(
        Guid SourceUuid,
        NormalizationRecordingProposalBatch Extraction,
        SourceActorResolutionAttempt Original,
        SourceActorResolutionAttempt Reviewed,
        SourceActorReviewPreparation Preparation,
        Guid ReviewOperation,
        NormalizationSubjectState SourceOwner,
        NormalizationSubjectState ExtractionOwner
    );
}
