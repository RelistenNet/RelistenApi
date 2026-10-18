using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Relisten;
using Relisten.Api.Models.Normalization;
using Relisten.Data;

namespace RelistenApiTests.Services;

[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestSourceActorResolutionCheckpointCompatibility
{
    private const string LegacyFormat = "source-actor-resolution-checkpoint-v1";
    private const string CurrentFormat = "source-actor-resolution-checkpoint-v2";
    private static readonly JsonSerializerOptions CaptureOptions = new()
    {
        RespectRequiredConstructorParameters = true,
        Converters = { new ReadOnlyGuidSetJsonConverter() },
    };

    [Test]
    public async Task ActualV1PublicCheckpointsRetainCompleteDomainValuesAndOriginalWorkRequests()
    {
        var capture = await ReadFixture();
        var published = ReadLegacyPreparation(capture.Published.Attempt, capture.Published.Storage);
        var admitted = ReadLegacyPreparation(capture.Admitted.Attempt, capture.Admitted.Storage);
        var foreign = ReadLegacyPreparation(capture.Foreign.Attempt, capture.Foreign.Storage);
        AssertOriginalRequest(
            capture.Published.Attempt,
            capture.Published.Storage,
            published,
            true
        );
        AssertOriginalRequest(capture.Admitted.Attempt, capture.Admitted.Storage, admitted, true);
        AssertOriginalRequest(capture.Foreign.Attempt, capture.Foreign.Storage, foreign, false);
        AssertDomainEqual(capture.Admitted.Ticket, ReadTicket(capture.Admitted.Storage));
        AssertDomainEqual(capture.Foreign.Ticket, ReadTicket(capture.Foreign.Storage));

        var legacyBatch = SourceActorResolutionCheckpoint.ReadBatch(
            capture.Published.Storage.BatchJson!
        );
        AssertDomainEqual(capture.Published.Batch, legacyBatch);
        AssertDomainEqual(legacyBatch.Ticket, ReadTicket(capture.Published.Storage));
        AssertFormat(capture.Published.Storage.BatchJson!, LegacyFormat);
        var currentBatchJson = SourceActorResolutionCheckpoint.WriteBatch(legacyBatch);
        AssertFormat(currentBatchJson, CurrentFormat);
        var currentBatch = SourceActorResolutionCheckpoint.ReadBatch(currentBatchJson);
        AssertDomainEqual(legacyBatch, currentBatch);
        SourceActorResolutionCheckpoint
            .EquivalentBatch(legacyBatch, currentBatch)
            .Should()
            .BeTrue();
        capture.Admitted.Storage.BatchJson.Should().BeNull();
        capture.Foreign.Storage.BatchJson.Should().BeNull();
    }

    [
        Test,
        Explicit(
            "Root must restore the exact private v1 capture into an owned local upgrade database."
        )
    ]
    public async Task RestoredV1HistoryPreservesAuthorityAcrossCodecAndProcessorVersions()
    {
        if (Environment.GetEnvironmentVariable("RELISTEN_LOCAL_INGESTION_TESTS") != "1")
        {
            Assert.Ignore("Use the root-owned local capture/restore workflow.");
        }
        var capture = await ReadFixture();
        var originalDatabase = await ReadDatabaseControl();
        SourceActorResolutionCheckpoint.FormatVersion.Should().Be(CurrentFormat);
        var database = Environment.GetEnvironmentVariable(
            "RELISTEN_SOURCE_ACTOR_V2_UPGRADE_DATABASE"
        );
        database.Should().NotBeNullOrWhiteSpace("root must name the database it restored and owns");
        Regex
            .IsMatch(database!, "^relisten_actor_v2_upgrade_[0-9a-f]{32}$")
            .Should()
            .BeTrue("only the explicitly owned upgrade-database prefix is permitted");
        database.Should().NotBe(originalDatabase.DatabaseName);
        var previousWrite = DbService.ConnStr;
        var previousRead = DbService.ReadOnlyConnStr;
        try
        {
            // The endpoint and local role are fixed. This probe neither creates nor drops a
            // database; root restores the exact dump here and remains responsible for cleanup.
            var db = new DbService(
                $"postgresql://relisten:local_dev_password@127.0.0.1:15432/{database}",
                null,
                null,
                Host.CreateApplicationBuilder().Environment,
                NullLogger<DbService>.Instance
            );
            var work = new NormalizationWorkService(db);
            var store = new SourceActorResolutionStore(db);
            var resolver = new SourceActorResolutionService(
                new(db),
                new(db, work, allowFixturePolicies: true),
                new(new(), new()),
                store,
                work
            );
            await AssertRestored(
                db,
                work,
                capture.Published.Attempt,
                capture.Published.Storage,
                capture.Published.Membership
            );
            await AssertRestored(
                db,
                work,
                capture.Admitted.Attempt,
                capture.Admitted.Storage,
                capture.Admitted.Membership
            );
            await AssertRestored(
                db,
                work,
                capture.Foreign.Attempt,
                capture.Foreign.Storage,
                capture.Foreign.Membership
            );
            var accepted = await AcceptedState(db);

            var replay = await resolver.ResolveAsync(
                capture.Published.Attempt.AttemptUuid,
                capture.Published.Attempt.ExtractionAttemptUuid
            );

            replay.Result!.Replayed.Should().BeTrue();
            AssertDomainEqual(capture.Published.Attempt.Preparation, replay.Preparation);
            AssertDomainEqual(
                capture.Published.Batch,
                await store.GetAttemptBatchAsync(replay.AttemptUuid)
            );
            (await ReadState(db, replay.AttemptUuid))
                .Should()
                .Be(capture.Published.Storage.State());

            var resumed = await resolver.ResolveAsync(
                capture.Admitted.Attempt.AttemptUuid,
                capture.Admitted.Attempt.ExtractionAttemptUuid
            );

            if (capture.ResolverVersion != SourceActorResolutionService.Version)
            {
                // A codec upgrade preserves historical values; it does not authorize the
                // current processor to resume or reinterpret a different producer version.
                resumed
                    .Result!.Disposition.Should()
                    .Be(SourceActorResolutionDisposition.Unresolved);
                resumed.Result.Reason.Should().Be("processing_version_changed");
                AssertDomainEqual(capture.Admitted.Attempt.Preparation, resumed.Preparation);
                (await store.GetAttemptBatchAsync(resumed.AttemptUuid)).Should().BeNull();

                foreach (
                    var history in new[]
                    {
                        (
                            capture.Published.Attempt,
                            capture.Published.Storage,
                            capture.Published.Membership
                        ),
                        (
                            capture.Admitted.Attempt,
                            capture.Admitted.Storage,
                            capture.Admitted.Membership
                        ),
                        (
                            capture.Foreign.Attempt,
                            capture.Foreign.Storage,
                            capture.Foreign.Membership
                        ),
                    }
                )
                {
                    // Resolver-only changes can reuse a compatible producer. This refusal
                    // applies when the captured extraction producer itself has changed.
                    if (
                        history.Attempt.Preparation!.ExtractionTicket.PolicyVersion
                        != NormalizationRecordingProcessor.Version
                    )
                    {
                        var freshUuid = Guid.NewGuid();
                        var resolveHistorical = () =>
                            resolver.ResolveAsync(freshUuid, history.Attempt.ExtractionAttemptUuid);

                        await resolveHistorical.Should().ThrowAsync<ArgumentException>();
                        var fresh = (await store.GetAttemptAsync(freshUuid))!;
                        fresh.Preparation.Should().BeNull();
                        fresh.Result.Should().BeNull();
                        (await store.GetAttemptBatchAsync(freshUuid)).Should().BeNull();
                        (
                            await db.WithWriteConnection(c =>
                                c.ExecuteScalarAsync<long>(
                                    "SELECT count(*) FROM normalization.work_runs WHERE uuid=@freshUuid",
                                    new { freshUuid }
                                )
                            )
                        )
                            .Should()
                            .Be(0, "old producer evidence must refuse before Work admission");
                    }

                    await AssertRestored(
                        db,
                        work,
                        history.Attempt,
                        history.Storage,
                        history.Membership
                    );
                }

                (await AcceptedState(db)).Should().Be(accepted);
                return;
            }

            resumed.Result!.Disposition.Should().Be(SourceActorResolutionDisposition.Published);
            AssertDomainEqual(capture.Admitted.Attempt.Preparation, resumed.Preparation);
            var resumedBatch = (await store.GetAttemptBatchAsync(resumed.AttemptUuid))!;
            AssertDomainEqual(capture.Admitted.Ticket, resumedBatch.Ticket);
            var resumedState = await ReadState(db, resumed.AttemptUuid);
            resumedState.PreparationJson.Should().Be(capture.Admitted.Storage.PreparationJson);
            resumedState.RequestHash.Should().Be(capture.Admitted.Storage.RequestHash);
            resumedState.TicketJson.Should().Be(capture.Admitted.Storage.TicketJson);
            resumedState.WorkState.Should().Be("published");
            AssertFormat(resumedState.PreparationJson, LegacyFormat);
            AssertFormat(resumedState.BatchJson!, CurrentFormat);
            (
                await work.GetInputContributionsAsync(
                    SourceActorResolutionService.Owner(resumed.SourceUuid)
                )
            )
                .Contributions.Should()
                .BeEquivalentTo(capture.Admitted.Membership.Contributions);

            // A fresh attempt must discover the genuinely admitted v1 preparation through
            // the format selector, then write v2 while retaining the original history bytes.
            var current = await resolver.ResolveAsync(
                Guid.NewGuid(),
                capture.Published.Attempt.ExtractionAttemptUuid
            );

            current.Result!.Disposition.Should().Be(SourceActorResolutionDisposition.Published);
            current
                .Preparation!.ExpectedContributions.Should()
                .BeEquivalentTo(
                    capture.Published.Membership.Contributions.Select(
                        value => new NormalizationInputContributionRevision(
                            value.Contributor,
                            value.Revision
                        )
                    )
                );
            AssertDomainEqual(
                capture.Published.Attempt.Preparation!.ExtractionTicket,
                current.Preparation.ExtractionTicket
            );
            var currentState = await ReadState(db, current.AttemptUuid);
            AssertFormat(currentState.PreparationJson, CurrentFormat);
            AssertFormat(currentState.BatchJson!, CurrentFormat);
            (await ReadState(db, capture.Published.Attempt.AttemptUuid))
                .Should()
                .Be(capture.Published.Storage.State());
            AssertDomainEqual(
                capture.Published.Batch,
                await store.GetAttemptBatchAsync(capture.Published.Attempt.AttemptUuid)
            );

            var refused = await resolver.ResolveAsync(
                Guid.NewGuid(),
                capture.Foreign.Attempt.ExtractionAttemptUuid
            );

            refused.Result!.Disposition.Should().Be(SourceActorResolutionDisposition.Unresolved);
            refused.Result.Reason.Should().Be("unsupported_owner_inputs");
            refused.Preparation.Should().BeNull();
            (await store.GetAttemptBatchAsync(refused.AttemptUuid)).Should().BeNull();
            await AssertRestored(
                db,
                work,
                capture.Foreign.Attempt,
                capture.Foreign.Storage,
                capture.Foreign.Membership
            );
            (await AcceptedState(db)).Should().Be(accepted);
        }
        finally
        {
            DbService.ConnStr = previousWrite;
            DbService.ReadOnlyConnStr = previousRead;
        }
    }

    private static async Task<LegacyFixture> ReadFixture()
    {
        var path =
            Environment.GetEnvironmentVariable("RELISTEN_SOURCE_ACTOR_V1_CAPTURE_FILE")
            ?? Path.Combine(
                AppContext.BaseDirectory,
                "Fixtures",
                "normalization",
                "source-actor-v1-public-fixture.json"
            );
        File.Exists(path)
            .Should()
            .BeTrue("root must capture the actual unchanged v1 writer before integrating v2");
        var capture = JsonSerializer.Deserialize<LegacyFixture>(
            await File.ReadAllTextAsync(path),
            CaptureOptions
        )!;
        capture.CaptureVersion.Should().Be("source-actor-public-v1-capture-v1");
        // This fixture records the historical writer. Later decoder versions must not
        // rewrite its authority or make storage compatibility depend on today's resolver.
        capture.ResolverVersion.Should().NotBeNullOrWhiteSpace();
        foreach (
            var history in new[]
            {
                (capture.Published.Attempt, capture.Published.Storage),
                (capture.Admitted.Attempt, capture.Admitted.Storage),
                (capture.Foreign.Attempt, capture.Foreign.Storage),
            }
        )
        {
            history.Attempt.ProcessingVersion.Should().Be(capture.ResolverVersion);
            var storage = history.Storage;
            ReadTicket(storage).PolicyVersion.Should().Be(capture.ResolverVersion);
            SourceActorResolutionCheckpoint
                .TextHash(storage.PreparationJson)
                .Should()
                .Be(storage.PreparationSha256);
            SourceActorResolutionCheckpoint
                .TextHash(storage.TicketJson)
                .Should()
                .Be(storage.TicketSha256);
            (
                storage.BatchJson == null
                    ? null
                    : SourceActorResolutionCheckpoint.TextHash(storage.BatchJson)
            )
                .Should()
                .Be(storage.BatchSha256);
        }
        return capture;
    }

    private static async Task<CaptureDatabaseControl> ReadDatabaseControl()
    {
        var path = Environment.GetEnvironmentVariable("RELISTEN_SOURCE_ACTOR_V1_CAPTURE_FILE");
        path.Should()
            .NotBeNullOrWhiteSpace("the restored probe requires the full private capture metadata");
        var control = JsonSerializer.Deserialize<CaptureDatabaseControl>(
            await File.ReadAllTextAsync(path!),
            CaptureOptions
        )!;
        control.DatabaseHost.Should().Be("127.0.0.1");
        control.DatabasePort.Should().Be(15432);
        Regex.IsMatch(control.DatabaseName, "^relisten_test_[0-9a-f]{32}$").Should().BeTrue();
        return control;
    }

    private static SourceActorResolutionPreparation ReadLegacyPreparation(
        SourceActorResolutionAttempt expected,
        CaptureStorage storage
    )
    {
        AssertFormat(storage.PreparationJson, LegacyFormat);
        var legacy = SourceActorResolutionCheckpoint.ReadPreparation(storage.PreparationJson);
        AssertDomainEqual(expected.Preparation, legacy);
        var currentJson = SourceActorResolutionCheckpoint.WritePreparation(legacy);
        AssertFormat(currentJson, CurrentFormat);
        var current = SourceActorResolutionCheckpoint.ReadPreparation(currentJson);
        AssertDomainEqual(legacy, current);
        SourceActorResolutionCheckpoint.EquivalentPreparation(legacy, current).Should().BeTrue();
        return legacy;
    }

    private static void AssertOriginalRequest(
        SourceActorResolutionAttempt attempt,
        CaptureStorage storage,
        SourceActorResolutionPreparation preparation,
        bool genuineContributedAdmission
    )
    {
        var ticket = ReadTicket(storage);
        ticket.WorkUuid.Should().Be(attempt.AttemptUuid);
        SourceActorResolutionReadSet.IsComplete(ticket, preparation).Should().BeTrue();
        NormalizationWorkService
            .MatchesContributedBatchRequest(
                storage.RequestHash,
                SourceActorResolutionService.Owner(attempt.SourceUuid),
                attempt.ProcessingVersion,
                SourceActorResolutionReadSet.Contributions(preparation),
                preparation.ExpectedConfigurationGeneration,
                preparation.ExpectedContributions
            )
            .Should()
            .Be(genuineContributedAdmission);
    }

    private static NormalizationWorkTicket ReadTicket(CaptureStorage storage) =>
        JsonSerializer.Deserialize<NormalizationWorkTicket>(storage.TicketJson, CaptureOptions)!;

    private static void AssertDomainEqual<T>(T expected, T actual) =>
        JsonElement
            .DeepEquals(
                JsonSerializer.SerializeToElement(expected),
                JsonSerializer.SerializeToElement(actual)
            )
            .Should()
            .BeTrue("every ordered public value and original observation must remain exact");

    private static void AssertFormat(string json, string format)
    {
        using var document = JsonDocument.Parse(json);
        document.RootElement.GetProperty("Format").GetString().Should().Be(format);
    }

    private static async Task AssertRestored(
        DbService db,
        NormalizationWorkService work,
        SourceActorResolutionAttempt attempt,
        CaptureStorage storage,
        NormalizationInputContributionsSnapshot membership
    )
    {
        (await ReadState(db, attempt.AttemptUuid)).Should().Be(storage.State());
        (
            await work.GetInputContributionsAsync(
                SourceActorResolutionService.Owner(attempt.SourceUuid)
            )
        )
            .Should()
            .BeEquivalentTo(membership);
    }

    private static Task<StoredState> ReadState(DbService db, Guid attemptUuid) =>
        db.WithWriteConnection(c =>
            c.QuerySingleAsync<StoredState>(
                """
                SELECT a.preparation::text AS "PreparationJson",a.batch::text AS "BatchJson",
                    r.request_hash AS "RequestHash",r.ticket::text AS "TicketJson",r.state AS "WorkState"
                FROM normalization.source_actor_resolution_attempts a
                JOIN normalization.work_runs r ON r.uuid=a.attempt_uuid WHERE a.attempt_uuid=@attemptUuid
                """,
                new { attemptUuid }
            )
        );

    private static Task<string> AcceptedState(DbService db) =>
        db.WithWriteConnection(c =>
            c.QuerySingleAsync<string>(
                """
                SELECT jsonb_build_object(
                    'people',(SELECT jsonb_agg(to_jsonb(value) ORDER BY uuid) FROM normalization.people value),
                    'aliases',(SELECT jsonb_agg(to_jsonb(value) ORDER BY uuid) FROM normalization.person_aliases value),
                    'credits',(SELECT jsonb_agg(to_jsonb(value) ORDER BY uuid) FROM normalization.recording_credits value),
                    'actor_contexts',(SELECT jsonb_agg(to_jsonb(value) ORDER BY uuid) FROM normalization.recording_actor_contexts value),
                    'assignments',(SELECT jsonb_agg(to_jsonb(value) ORDER BY uuid) FROM normalization.source_actor_alias_assignments value),
                    'support',(SELECT jsonb_agg(to_jsonb(value) ORDER BY uuid) FROM normalization.person_alias_contributor_support value),
                    'reviews',(SELECT jsonb_agg(to_jsonb(value) ORDER BY uuid) FROM normalization.source_actor_alias_review_state value))::text
                """
            )
        );

    private sealed record LegacyFixture(
        string CaptureVersion,
        string ResolverVersion,
        PublishedCapture Published,
        PendingCapture Admitted,
        PendingCapture Foreign
    );

    private sealed record CaptureDatabaseControl(
        string DatabaseHost,
        int DatabasePort,
        string DatabaseName
    );

    private sealed record PublishedCapture(
        SourceActorResolutionAttempt Attempt,
        SourceActorResolutionBatch Batch,
        CaptureStorage Storage,
        NormalizationInputContributionsSnapshot Membership
    );

    private sealed record PendingCapture(
        SourceActorResolutionAttempt Attempt,
        NormalizationWorkTicket Ticket,
        CaptureStorage Storage,
        NormalizationInputContributionsSnapshot Membership
    );

    private sealed record CaptureStorage(
        string PreparationJson,
        string? BatchJson,
        string RequestHash,
        string TicketJson,
        string WorkState,
        string PreparationSha256,
        string? BatchSha256,
        string TicketSha256
    )
    {
        public StoredState State() =>
            new(PreparationJson, BatchJson, RequestHash, TicketJson, WorkState);
    }

    private sealed record StoredState(
        string PreparationJson,
        string? BatchJson,
        string RequestHash,
        string TicketJson,
        string WorkState
    );
}
