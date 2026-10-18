using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Relisten;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
using Relisten.Normalization;

namespace RelistenApiTests.Services;

/// <summary>
/// Consumes an operator-restored, genuine v7/alias-v2 accepted capture with current services.
/// Historical evidence permits withdrawal of its exact support, never new positive authority.
/// The operator owns dump/restore; this test creates, drops and migrates no database.
/// </summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestSourceActorAliasHistoricalWithdrawal
{
    private const string Operator = "historical-source-alias-withdrawal-operator";
    private const string Reason = "Reviewed the exact retained pre-upgrade source assertion.";

    // Captured claims and resolver facts contain IReadOnlySet<Guid>. Use the same reader
    // as retained checkpoints; hashes still use the original bytes before deserialization.
    private static readonly JsonSerializerOptions CaptureOptions = new()
    {
        Converters = { new ReadOnlyGuidSetJsonConverter() },
    };

    [TestCase("reject")]
    [TestCase("retract")]
    [Explicit(
        "Restore the pinned genuine v7/alias-v2 accepted capture into its owned local database."
    )]
    public async Task CurrentNegativeReviewWithdrawsOnlyHistoricalSourceSupport(string name)
    {
        var control = await ReadControlAsync();
        var item = control.Capture.Cases.Single(value => value.Name == name);
        var previousWrite = DbService.ConnStr;
        var previousRead = DbService.ReadOnlyConnStr;
        try
        {
            // Disable host defaults so this test never consults application configuration.
            var host = Host.CreateApplicationBuilder(
                new HostApplicationBuilderSettings
                {
                    DisableDefaults = true,
                    EnvironmentName = Environments.Production,
                }
            );
            var db = new DbService(
                $"postgresql://relisten:local_dev_password@127.0.0.1:15432/{control.Database}",
                null,
                null,
                host.Environment,
                NullLogger<DbService>.Instance
            );
            var probe = new WithdrawalProbe(db);
            await probe.RequireCapturedStateAsync(item);
            var originalRows = await probe.ReadOriginalRowsAsync(item);
            await probe.RefuseHistoricalPublicationAsync(item);

            var refreshed = await probe.ExtractAndResolveAsync(item.Source.SourceUuid);
            var negative = await probe.ReviewHistoricalClaimAsync(item, refreshed);
            var current = await probe.ExtractAndResolveAsync(item.Source.SourceUuid);
            var application = await probe.WithdrawAsync(item, current);

            await probe.RequireWithdrawnStateAsync(item, application, negative);
            (await probe.ReadOriginalRowsAsync(item)).Should().Be(originalRows);
            await probe.RequireOriginalArtifactsAsync(item);
            await RequireCaptureHashAsync(control.Path);
        }
        finally
        {
            DbService.ConnStr = previousWrite;
            DbService.ReadOnlyConnStr = previousRead;
        }
    }

    // One probe composes current public services for the restored database. SQL below reads
    // retained outcomes; it neither manufactures review decisions nor edits historical proof.
    private sealed class WithdrawalProbe
    {
        private readonly DbService db;
        private readonly NormalizationWorkService work;
        private readonly NormalizationRecordingProposalService proposals;
        private readonly NormalizationRecordingProcessor extractor;
        private readonly SourceActorResolutionStore resolutions;
        private readonly SourceActorResolutionService resolver;
        private readonly SourceActorReviewStore reviewStore;
        private readonly SourceActorReviewService reviews;
        private readonly SourceActorAliasApplicationStore applications;
        private readonly SourceActorAliasApplicationService publisher;

        internal WithdrawalProbe(DbService db)
        {
            this.db = db;
            work = new(db);
            var evidence = new NormalizationEvidenceService(db);
            proposals = new(db);
            extractor = new(
                new(db, evidence),
                evidence,
                new(db),
                new(),
                new(new(), new()),
                work,
                proposals,
                new(db)
            );
            var candidates = new NormalizationCandidateService(db);
            var policies = new PeopleResolutionPolicyService(db, work);
            resolutions = new(db);
            resolver = new(candidates, policies, new(new(), new()), resolutions, work);
            reviewStore = new(db);
            reviews = new(db, work, reviewStore);
            applications = new(db);
            publisher = new(
                db,
                resolutions,
                candidates,
                policies,
                new(new(), new()),
                applications,
                new(db, work),
                work
            );
        }

        internal async Task RequireCapturedStateAsync(CapturedCase item)
        {
            await RequireOriginalArtifactsAsync(item);
            (await ReadNativeProofAsync(item.Native)).Should().Be(item.NativeProof.Json);
            var review = await ReadReviewAsync(item.AcceptedReview.Uuid);
            review.GetProperty("uuid").GetGuid().Should().Be(item.AcceptedReview.Uuid);
            review.GetProperty("state").GetString().Should().Be("accepted");
            review.GetProperty("review_revision").GetInt64().Should().Be(1);
            item.AcceptedReview.ReviewRevision.Should().Be(1);
            item.AcceptedReview.State.Should().Be("accepted");
            item.AcceptedReview.SelectedPersonUuid.Should().Be(item.Native.PersonUuid);
            Same(item.AcceptedClaim.Coordinates, item.OriginalClaim.Coordinates);
            Same(item.AcceptedClaim.Coordinates, item.AcceptedReview.Coordinates);
            item.AcceptedClaim.SourceReviewRevision.Should().Be(1);
            var sourceRows = await ReadAuthorityRowsAsync(item.Source.SourceUuid);
            Same(sourceRows, item.AcceptedRows);
            var aliases = await ReadPublicAliasIdsAsync(item.Native.PersonUuid);
            aliases.Should().Contain(item.PublicAliases.Select(value => value.Uuid));
        }

        internal async Task RequireOriginalArtifactsAsync(CapturedCase item)
        {
            foreach (
                var proof in new[]
                {
                    item.OriginalExtraction,
                    item.OriginalResolution,
                    item.AcceptedExtraction,
                    item.AcceptedResolution,
                    item.ApplicationProof,
                    item.NativeProof,
                }
            )
            {
                Hash(Encoding.UTF8.GetBytes(proof.Json)).Should().Be(proof.Sha256);
            }
            foreach (var proof in new[] { item.OriginalExtraction, item.AcceptedExtraction })
            {
                var captured = JsonSerializer.Deserialize<NormalizationRecordingProposalBatch>(
                    proof.Json,
                    CaptureOptions
                )!;
                Same(await proposals.GetAttemptBatchAsync(captured.AttemptUuid), captured);
            }
            foreach (var proof in new[] { item.OriginalResolution, item.AcceptedResolution })
            {
                var captured = JsonSerializer.Deserialize<SourceActorResolutionAttempt>(
                    proof.Json,
                    CaptureOptions
                )!;
                Same(await resolutions.GetAttemptAsync(captured.AttemptUuid), captured);
            }
            Same(
                await reviewStore.GetPreparationAsync(
                    item.AcceptedReviewPreparation.PreparationUuid
                ),
                item.AcceptedReviewPreparation
            );
            var app = (await applications.GetApplicationAsync(item.Application.AttemptUuid))!;
            var stages = new List<SourceActorAliasPersonStage>();
            foreach (var stage in app.Stages)
            {
                stages.Add((await applications.GetStageAsync(stage.StageUuid))!);
            }
            var expanded = new
            {
                app.AttemptUuid,
                app.ResolutionAttemptUuid,
                app.SourceUuid,
                app.Version,
                app.Plan,
                Stages = stages,
                app.Result,
            };
            Same(expanded, JsonSerializer.Deserialize<JsonElement>(item.ApplicationProof.Json));
        }

        internal async Task RefuseHistoricalPublicationAsync(CapturedCase item)
        {
            var authority = await ReadAcceptedAuthorityAsync();
            var attempted = await publisher.ApplyAsync(
                Guid.NewGuid(),
                item.Application.ResolutionAttemptUuid
            );
            attempted.Result!.Disposition.Should().Be(SourceActorAliasApplicationDisposition.Stale);
            attempted.Result.Reason.Should().Be("source_application_preparation_changed");
            attempted.Plan.Should().NotBeNull();
            attempted
                .Plan!.Proposal.Should()
                .Be(
                    new SourceActorAliasProposalReference(
                        item.Application.ResolutionAttemptUuid,
                        Attempt(item.AcceptedExtraction),
                        JsonSerializer
                            .Deserialize<JsonElement>(item.AcceptedResolution.Json)
                            .GetProperty("ProcessingVersion")
                            .GetString()!
                    )
                );
            attempted
                .Plan.Claims.Should()
                .BeEmpty("a refused producer supplied no current accepted interpretation");
            attempted.Plan.NegativeReason.Should().Be(attempted.Result.Reason);
            attempted.Stages.Should().BeEmpty();
            Same(await applications.GetApplicationAsync(attempted.AttemptUuid), attempted);

            var replay = await publisher.ApplyAsync(
                attempted.AttemptUuid,
                item.Application.ResolutionAttemptUuid
            );
            replay.Result!.Replayed.Should().BeTrue();
            Same(replay.Plan, attempted.Plan);
            Same(replay.Stages, attempted.Stages);
            Same(replay.Result with { Replayed = false }, attempted.Result);
            var otherResolution = Attempt(item.OriginalResolution);
            otherResolution.Should().NotBe(item.Application.ResolutionAttemptUuid);
            var changed = () => publisher.ApplyAsync(attempted.AttemptUuid, otherResolution);
            await changed.Should().ThrowAsync<NormalizationConflictException>();
            Same(await applications.GetApplicationAsync(attempted.AttemptUuid), attempted);
            (await ReadAcceptedAuthorityAsync()).Should().Be(authority);
        }

        internal async Task<SourceActorResolutionAttempt> ExtractAndResolveAsync(Guid sourceUuid)
        {
            var extracted = await extractor.ProcessAsync(Guid.NewGuid(), sourceUuid);
            extracted
                .Disposition.Should()
                .Be(NormalizationRecordingProcessingDisposition.Published);
            var resolved = await resolver.ResolveAsync(Guid.NewGuid(), extracted.AttemptUuid);
            resolved.ProcessingVersion.Should().Be(SourceActorResolutionService.Version);
            resolved.Result!.Disposition.Should().Be(SourceActorResolutionDisposition.Published);
            return resolved;
        }

        internal async Task<SourceActorReviewPreparation> ReviewHistoricalClaimAsync(
            CapturedCase item,
            SourceActorResolutionAttempt current
        )
        {
            var owner = (
                await work.GetSubjectAsync(item.AcceptedReview.SourceResolutionOwnerUuid)
            )!;
            var preparation = await reviews.PrepareAsync(
                Guid.NewGuid(),
                new(
                    item.AcceptedReviewPreparation.Request.ExtractionAttemptUuid,
                    item.AcceptedClaim.Coordinates,
                    item.LaterAction,
                    owner.EditorialRevision,
                    item.AcceptedReview.ReviewRevision
                )
            );
            preparation.Target.Should().BeNull();
            Same(preparation.OriginalField, item.AcceptedReviewPreparation.OriginalField);
            preparation.SourceOwner.EditorialRevision.Should().Be(owner.EditorialRevision);
            var extraction = (await proposals.GetAttemptBatchAsync(current.ExtractionAttemptUuid))!;
            preparation.ReadSet.Inputs.Should().Contain(extraction.Ticket.Inputs);
            var operation = Guid.NewGuid();
            var applied = await reviews.ApplyAsync(
                operation,
                preparation.PreparationUuid,
                Reason,
                Operator
            );
            applied.Replayed.Should().BeFalse();
            (await reviews.ApplyAsync(operation, preparation.PreparationUuid, Reason, Operator))
                .Replayed.Should()
                .BeTrue();
            return preparation;
        }

        internal async Task<SourceActorAliasApplication> WithdrawAsync(
            CapturedCase item,
            SourceActorResolutionAttempt current
        )
        {
            var application = await publisher.ApplyAsync(Guid.NewGuid(), current.AttemptUuid);
            application.Result.Should().NotBeNull();
            var stages = new List<SourceActorAliasPersonStage>();
            foreach (var stage in application.Stages)
            {
                stages.Add((await applications.GetStageAsync(stage.StageUuid))!);
            }
            var retired = stages
                .Where(stage => stage.Receipt?.RetiredAssignmentUuids.Count > 0)
                .ToArray();
            retired.Should().NotBeEmpty();
            retired
                .Should()
                .OnlyContain(stage => stage.OriginalPersonUuid == item.Native.PersonUuid);
            retired.SelectMany(stage => stage.Receipt!.Names).Should().BeEmpty();
            retired
                .SelectMany(stage => stage.Deltas)
                .Should()
                .OnlyContain(delta =>
                    delta.Kind == SourceActorAliasDeltaKind.Retire && delta.Names.Count == 0
                );
            retired
                .SelectMany(stage => stage.Receipt!.RetiredAssignmentUuids)
                .Distinct()
                .Should()
                .BeEquivalentTo(Ids(item.AcceptedRows, "assignments"));
            retired
                .SelectMany(stage => stage.Receipt!.RetiredSupportUuids)
                .Distinct()
                .Should()
                .BeEquivalentTo(Ids(item.AcceptedRows, "support"));

            // A newly understood unrelated byline may still need review. The withdrawal's
            // committed receipts establish success independently of the overall disposition.
            var replay = await publisher.ApplyAsync(application.AttemptUuid, current.AttemptUuid);
            replay.Result!.Replayed.Should().BeTrue();
            Same(replay.Plan, application.Plan);
            Same(replay.Stages, application.Stages);
            return application;
        }

        internal async Task RequireWithdrawnStateAsync(
            CapturedCase item,
            SourceActorAliasApplication application,
            SourceActorReviewPreparation negative
        )
        {
            var rows = await ReadAuthorityRowsAsync(item.Source.SourceUuid);
            foreach (var kind in new[] { "assignments", "support" })
            {
                var before = item.AcceptedRows.GetProperty(kind).EnumerateArray().ToArray();
                var after = rows.GetProperty(kind).EnumerateArray().ToArray();
                after
                    .Select(row => row.GetProperty("uuid").GetGuid())
                    .Should()
                    .BeEquivalentTo(before.Select(row => row.GetProperty("uuid").GetGuid()));
                foreach (var old in before)
                {
                    var retained = after.Single(row =>
                        row.GetProperty("uuid").GetGuid() == old.GetProperty("uuid").GetGuid()
                    );
                    retained
                        .GetProperty("retired_work_uuid")
                        .ValueKind.Should()
                        .Be(JsonValueKind.String);
                    var retirement = retained.GetProperty("retired_work_uuid").GetGuid();
                    application.Stages.Select(stage => stage.WorkUuid).Should().Contain(retirement);
                    SameExceptRetirement(retained, old);
                }
            }
            Same(rows.GetProperty("aliases"), item.AcceptedRows.GetProperty("aliases"));
            Same(rows.GetProperty("alias_history"), item.AcceptedRows.GetProperty("alias_history"));
            var review = await ReadReviewAsync(item.AcceptedReview.Uuid);
            review
                .GetProperty("state")
                .GetString()
                .Should()
                .Be(item.Name == "reject" ? "rejected" : "retracted");
            review.GetProperty("review_revision").GetInt64().Should().Be(2);
            Same(negative.Request.Claim, item.AcceptedClaim.Coordinates);
            var publicAliases = await ReadPublicAliasIdsAsync(item.Native.PersonUuid);
            publicAliases.Should().NotIntersectWith(Ids(item.AcceptedRows, "aliases"));
            publicAliases.Should().Contain([item.Native.PersonUuid, item.Native.Alias.Uuid]);
            (await ReadNativeProofAsync(item.Native)).Should().Be(item.NativeProof.Json);
        }

        internal Task<string> ReadOriginalRowsAsync(CapturedCase item) =>
            db.WithWriteConnection(c =>
                c.QuerySingleAsync<string>(
                    """
                    SELECT jsonb_build_object(
                        'extractions',(SELECT jsonb_agg(to_jsonb(a) ORDER BY attempt_uuid) FROM normalization.recording_processing_attempts a WHERE attempt_uuid=ANY(@extractions)),
                        'resolutions',(SELECT jsonb_agg(to_jsonb(a) ORDER BY attempt_uuid) FROM normalization.source_actor_resolution_attempts a WHERE attempt_uuid=ANY(@resolutions)),
                        'application',(SELECT to_jsonb(a) FROM normalization.source_actor_alias_applications a WHERE attempt_uuid=@application),
                        'stages',(SELECT jsonb_agg(to_jsonb(s) ORDER BY stage_uuid) FROM normalization.source_actor_alias_person_stages s WHERE application_uuid=@application),
                        'preparation',(SELECT to_jsonb(p) FROM normalization.source_actor_review_preparations p WHERE preparation_uuid=@preparation),
                        'accepted_review_history',(SELECT to_jsonb(r) FROM normalization.source_actor_alias_review_state_history r WHERE uuid=@review AND review_revision=1),
                        'inputs',(SELECT jsonb_agg(to_jsonb(i) ORDER BY uuid) FROM normalization.input_revisions i WHERE uuid=ANY(@revisions)))::text
                    """,
                    new
                    {
                        extractions = new[]
                        {
                            Attempt(item.OriginalExtraction),
                            Attempt(item.AcceptedExtraction),
                        }
                            .Distinct()
                            .ToArray(),
                        resolutions = new[]
                        {
                            Attempt(item.OriginalResolution),
                            Attempt(item.AcceptedResolution),
                        }
                            .Distinct()
                            .ToArray(),
                        application = item.Application.AttemptUuid,
                        preparation = item.AcceptedReviewPreparation.PreparationUuid,
                        review = item.AcceptedReview.Uuid,
                        revisions = new[] { item.AcceptedClaim.Coordinates.InputRevisionUuid },
                    }
                )
            );

        private Task<JsonElement> ReadReviewAsync(Guid uuid) =>
            db.WithWriteConnection(async c =>
                JsonSerializer.Deserialize<JsonElement>(
                    await c.QuerySingleAsync<string>(
                        "SELECT to_jsonb(r)::text FROM normalization.source_actor_alias_review_state r WHERE uuid=@uuid",
                        new { uuid }
                    )
                )
            );

        private Task<Guid[]> ReadPublicAliasIdsAsync(Guid personUuid) =>
            db.WithWriteConnection(async c =>
                (
                    await c.QueryAsync<Guid>(
                        "SELECT uuid FROM normalization.current_person_aliases WHERE person_uuid=@personUuid ORDER BY uuid",
                        new { personUuid }
                    )
                ).ToArray()
            );

        private Task<string> ReadAcceptedAuthorityAsync() =>
            db.WithWriteConnection(c =>
                c.QuerySingleAsync<string>(
                    """
                    SELECT jsonb_build_object(
                        'people',(SELECT jsonb_agg(to_jsonb(p) ORDER BY uuid) FROM normalization.people p),
                        'aliases',(SELECT jsonb_agg(to_jsonb(a) ORDER BY uuid) FROM normalization.person_aliases a),
                        'assignments',(SELECT jsonb_agg(to_jsonb(a) ORDER BY uuid) FROM normalization.source_actor_alias_assignments a),
                        'support',(SELECT jsonb_agg(to_jsonb(s) ORDER BY uuid) FROM normalization.person_alias_contributor_support s),
                        'credits',(SELECT jsonb_agg(to_jsonb(c) ORDER BY uuid) FROM normalization.recording_credits c))::text
                    """
                )
            );

        private Task<string> ReadNativeProofAsync(CapturedNative native) =>
            db.WithWriteConnection(c =>
                c.QuerySingleAsync<string>(
                    """
                    SELECT jsonb_build_object(
                        'person',(SELECT jsonb_build_array(uuid,display_name,kind) FROM normalization.people WHERE uuid=@PersonUuid),
                        'aliases',(SELECT jsonb_agg(to_jsonb(alias) ORDER BY uuid) FROM normalization.person_aliases alias WHERE uuid=ANY(@aliases)),
                        'history',(SELECT jsonb_agg(to_jsonb(history) ORDER BY uuid,revision) FROM normalization.person_aliases_history history WHERE uuid=ANY(@aliases)),
                        'unmanaged',(SELECT jsonb_agg(to_jsonb(support) ORDER BY uuid) FROM normalization.person_alias_unmanaged_support support WHERE alias_uuid=ANY(@aliases)),
                        'original_input',(SELECT to_jsonb(revision) FROM normalization.input_revisions revision WHERE uuid=@InputRevisionUuid),
                        'original_work',(SELECT to_jsonb(run) FROM normalization.work_runs run WHERE uuid=@OriginalWorkUuid))::text
                    """,
                    new
                    {
                        native.PersonUuid,
                        aliases = new[] { native.PersonUuid, native.Alias.Uuid },
                        native.InputRevisionUuid,
                        native.OriginalWorkUuid,
                    }
                )
            );

        private Task<JsonElement> ReadAuthorityRowsAsync(Guid sourceUuid) =>
            db.WithWriteConnection(async c =>
                JsonSerializer.Deserialize<JsonElement>(
                    await c.QuerySingleAsync<string>(
                        """
                        WITH assignments AS MATERIALIZED (
                            SELECT * FROM normalization.source_actor_alias_assignments WHERE source_uuid=@sourceUuid
                        ), support AS MATERIALIZED (
                            SELECT support.* FROM normalization.person_alias_contributor_support support
                            JOIN assignments ON assignments.uuid=support.assignment_uuid
                        )
                        SELECT jsonb_build_object(
                            'assignments',(SELECT jsonb_agg(to_jsonb(a) ORDER BY uuid) FROM assignments a),
                            'support',(SELECT jsonb_agg(to_jsonb(s) ORDER BY uuid) FROM support s),
                            'aliases',(SELECT jsonb_agg(to_jsonb(a) ORDER BY uuid) FROM normalization.person_aliases a WHERE uuid IN (SELECT alias_uuid FROM support)),
                            'alias_history',(SELECT jsonb_agg(to_jsonb(a) ORDER BY uuid,revision) FROM normalization.person_aliases_history a WHERE uuid IN (SELECT alias_uuid FROM support)),
                            'unmanaged_source_alias_support',(SELECT coalesce(jsonb_agg(to_jsonb(s) ORDER BY uuid),'[]') FROM normalization.person_alias_unmanaged_support s WHERE alias_uuid IN (SELECT alias_uuid FROM support)),
                            'review_state',(SELECT to_jsonb(r) FROM normalization.source_actor_alias_review_state r WHERE source_uuid=@sourceUuid),
                            'review_history',(SELECT jsonb_agg(to_jsonb(r) ORDER BY review_revision) FROM normalization.source_actor_alias_review_state_history r WHERE source_uuid=@sourceUuid))::text
                        """,
                        new { sourceUuid }
                    )
                )
            );
    }

    private static async Task<Control> ReadControlAsync()
    {
        Environment.GetEnvironmentVariable("RELISTEN_LOCAL_INGESTION_TESTS").Should().Be("1");
        var path = Environment.GetEnvironmentVariable("RELISTEN_ACCEPTED_SOURCE_CAPTURE_FILE");
        path.Should().NotBeNullOrWhiteSpace();
        var bytes = await RequireCaptureHashAsync(path!);
        var capture = JsonSerializer.Deserialize<Capture>(bytes, CaptureOptions)!;
        capture.CaptureVersion.Should().Be("source-actor-accepted-v7-alias-v2-withdrawal-v1");
        capture.DatabaseHost.Should().Be("127.0.0.1");
        capture.DatabasePort.Should().Be(15432);
        Regex.IsMatch(capture.DatabaseName, "^relisten_test_[0-9a-f]{32}$").Should().BeTrue();
        capture.DecoderVersion.Should().Be("provider-observations-v7");
        capture
            .ProcessorVersion.Should()
            .Be(SourceActorResolutionStore.LegacyAliasV2DecoderV7ProcessingVersion);
        capture.ProcessorVersion.Should().NotBe(NormalizationRecordingProcessor.Version);
        capture
            .AliasExtractorVersion.Should()
            .Be("source-actor-aliases-v2/recording-actor-evidence-v1");
        capture
            .ProjectionVersion.Should()
            .Be("people-source-actor-alias-projection-v1/" + capture.AliasExtractorVersion);
        capture.Cases.Select(value => value.Name).Should().BeEquivalentTo(["reject", "retract"]);
        foreach (var item in capture.Cases)
        {
            item.LaterAction.Should()
                .Be(
                    item.Name == "reject"
                        ? SourceActorReviewAction.Reject
                        : SourceActorReviewAction.Retract
                );
        }
        var database = Environment.GetEnvironmentVariable(
            "RELISTEN_ACCEPTED_SOURCE_UPGRADE_DATABASE"
        );
        database.Should().NotBeNullOrWhiteSpace();
        Regex.IsMatch(database!, "^relisten_actor_alias_upgrade_[0-9a-f]{32}$").Should().BeTrue();
        database.Should().NotBe(capture.DatabaseName);
        return new(path!, capture, database!);
    }

    private static async Task<byte[]> RequireCaptureHashAsync(string path)
    {
        var bytes = await File.ReadAllBytesAsync(path);
        Hash(bytes)
            .Should()
            .Be(Environment.GetEnvironmentVariable("RELISTEN_ACCEPTED_SOURCE_CAPTURE_SHA256"));
        return bytes;
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static Guid Attempt(CapturedProof proof) =>
        JsonSerializer.Deserialize<JsonElement>(proof.Json).GetProperty("AttemptUuid").GetGuid();

    private static Guid[] Ids(JsonElement rows, string property) =>
        rows.GetProperty(property)
            .EnumerateArray()
            .Select(row => row.GetProperty("uuid").GetGuid())
            .ToArray();

    private static void Same<TLeft, TRight>(TLeft left, TRight right) =>
        JsonElement
            .DeepEquals(
                JsonSerializer.SerializeToElement(left),
                JsonSerializer.SerializeToElement(right)
            )
            .Should()
            .BeTrue();

    private static void SameExceptRetirement(JsonElement actual, JsonElement original) =>
        Same(
            actual
                .EnumerateObject()
                .Where(field => field.Name != "retired_work_uuid")
                .ToDictionary(field => field.Name, field => field.Value),
            original
                .EnumerateObject()
                .Where(field => field.Name != "retired_work_uuid")
                .ToDictionary(field => field.Name, field => field.Value)
        );

    private sealed record Control(string Path, Capture Capture, string Database);

    private sealed record Capture(
        string CaptureVersion,
        string DatabaseHost,
        int DatabasePort,
        string DatabaseName,
        string DecoderVersion,
        string ProcessorVersion,
        string AliasExtractorVersion,
        string ProjectionVersion,
        CapturedCase[] Cases
    );

    private sealed record CapturedProof(string Json, string Sha256);

    private sealed record CapturedSource(
        int SourceId,
        Guid SourceUuid,
        Guid TrackUuid,
        int ArtistId
    );

    private sealed record CapturedAlias(Guid Uuid, string Wording);

    private sealed record CapturedNative(
        Guid PersonUuid,
        string Name,
        PersonAlias Alias,
        Guid InputRevisionUuid,
        Guid OriginalWorkUuid
    );

    private sealed record CapturedApplication(Guid AttemptUuid, Guid ResolutionAttemptUuid);

    private sealed record CapturedCase(
        string Name,
        SourceActorReviewAction LaterAction,
        CapturedSource Source,
        CapturedNative Native,
        CapturedProof NativeProof,
        SourceActorResolutionClaim OriginalClaim,
        SourceActorResolutionClaim AcceptedClaim,
        SourceActorReviewPreparation AcceptedReviewPreparation,
        SourceActorAliasReviewState AcceptedReview,
        CapturedProof OriginalExtraction,
        CapturedProof OriginalResolution,
        CapturedProof AcceptedExtraction,
        CapturedProof AcceptedResolution,
        CapturedApplication Application,
        CapturedProof ApplicationProof,
        CapturedAlias[] PublicAliases,
        JsonElement AcceptedRows
    );
}
