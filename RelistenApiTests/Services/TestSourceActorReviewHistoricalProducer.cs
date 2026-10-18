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
/// Cross-binary authority checks against a genuine v6 public capture restored by the operator.
/// This probe creates or drops no database and never rebinds UUIDs or producer versions.
/// </summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestSourceActorReviewHistoricalProducer
{
    private const string Operator = "historical-negative-upgrade-operator";
    private const string Reason = "Reviewed the exact retained v6 source field.";

    [TestCase("reject-new")]
    [TestCase("reject-saved")]
    [TestCase("retract-new")]
    [TestCase("retract-saved")]
    [Explicit(
        "Root must restore the exact public v6 capture into a separately owned local database."
    )]
    public async Task HistoricalNegativeReviewRetainsOriginalAuthorityAcrossDecoderUpgrade(
        string name
    )
    {
        var (capture, database) = await ReadControlAsync();
        var item = capture.Cases.Single(value => value.Name == name);
        var previousWrite = DbService.ConnStr;
        var previousRead = DbService.ReadOnlyConnStr;
        try
        {
            var db = new DbService(
                $"postgresql://relisten:local_dev_password@127.0.0.1:15432/{database}",
                null,
                null,
                Host.CreateApplicationBuilder().Environment,
                NullLogger<DbService>.Instance
            );
            var probe = new HistoricalReviewProbe(db);
            await probe.RequireCapturedProofAsync(item);
            var authority = await AcceptedAuthorityAsync(db);

            await probe.RefuseOldPositiveAsync(item);
            var preparation = await probe.PrepareNegativeAsync(item);
            await probe.ApplyNegativeAsync(item, preparation);

            (await AcceptedAuthorityAsync(db)).Should().Be(authority);
            await probe.RequireCapturedProofAsync(item);
        }
        finally
        {
            DbService.ConnStr = previousWrite;
            DbService.ReadOnlyConnStr = previousRead;
        }
    }

    // One probe owns the services for the exact restored database. Each named phase stays
    // on public review/store interfaces; the read-only SQL checks assert durable outcomes.
    private sealed class HistoricalReviewProbe
    {
        private readonly DbService db;
        private readonly NormalizationWorkService work;
        private readonly SourceActorReviewStore store;
        private readonly SourceActorReviewService reviews;
        private readonly NormalizationRecordingProposalService proposals;

        internal HistoricalReviewProbe(DbService db)
        {
            this.db = db;
            work = new(db);
            store = new(db);
            reviews = new(db, work, store);
            proposals = new(db);
        }

        internal async Task RequireCapturedProofAsync(CapturedCase item)
        {
            await RequireOriginalExtractionAsync(proposals, item);
            AssertSame(
                await store.GetPreparationAsync(item.Negative.PreparationUuid),
                item.Negative
            );
            AssertSame(
                await store.GetPreparationAsync(item.Positive.PreparationUuid),
                item.Positive
            );
        }

        internal async Task RefuseOldPositiveAsync(CapturedCase item)
        {
            var sourceBefore = (await work.GetSubjectAsync(item.Negative.SourceOwner.SubjectUuid))!;
            // v6 may still describe today's exact heads. The version boundary alone must not
            // authorize positive interpretation through either a new or a saved preparation.
            await RequireArgumentRefusalAsync(() =>
                reviews.PrepareAsync(Guid.NewGuid(), item.Positive.Request)
            );
            var operation = Guid.NewGuid();
            await RequireArgumentRefusalAsync(() =>
                reviews.ApplyAsync(operation, item.Positive.PreparationUuid, Reason, Operator)
            );
            (await work.GetSubjectAsync(sourceBefore.SubjectUuid))
                .Should()
                .BeEquivalentTo(sourceBefore);
            (await ReceiptCountAsync(db, operation)).Should().Be(0);
        }

        internal async Task<SourceActorReviewPreparation> PrepareNegativeAsync(CapturedCase item)
        {
            var currentRevision = item.Negative.Request.Claim.InputRevisionUuid;
            if (!item.UseSavedPreparation)
            {
                currentRevision = await RemoveOriginalFieldAsync(db, item.Negative);
                var operation = Guid.NewGuid();
                var stale = () =>
                    reviews.ApplyAsync(operation, item.Negative.PreparationUuid, Reason, Operator);
                var refusal = await stale.Should().ThrowAsync<Exception>();
                ContainsConflict(refusal.Which).Should().BeTrue();
                (await ReceiptCountAsync(db, operation)).Should().Be(0);
            }
            var preparation = item.UseSavedPreparation
                ? item.Negative
                : await reviews.PrepareAsync(Guid.NewGuid(), item.Negative.Request);
            AssertSame(preparation.OriginalField, item.Negative.OriginalField);
            preparation.Target.Should().BeNull();
            preparation
                .ReadSet.Inputs.Should()
                .Contain(input => input.RevisionUuid == currentRevision);
            if (!item.UseSavedPreparation)
            {
                preparation
                    .ReadSet.Inputs.Should()
                    .NotContain(input =>
                        input.RevisionUuid == item.Negative.Request.Claim.InputRevisionUuid
                    );
            }
            return preparation;
        }

        internal async Task ApplyNegativeAsync(
            CapturedCase item,
            SourceActorReviewPreparation preparation
        )
        {
            var operation = Guid.NewGuid();
            var result = await reviews.ApplyAsync(
                operation,
                preparation.PreparationUuid,
                Reason,
                Operator
            );
            result.Replayed.Should().BeFalse();
            (await reviews.ApplyAsync(operation, preparation.PreparationUuid, Reason, Operator))
                .Replayed.Should()
                .BeTrue();
            var row = await db.WithWriteConnection(c =>
                c.QuerySingleAsync<Decision>(
                    """
                    SELECT state AS "State",input_revision_uuid AS "InputRevisionUuid",
                        original_scope::text AS "Scope",last_operation_uuid AS "OperationUuid"
                    FROM normalization.source_actor_alias_review_state WHERE uuid=@uuid
                    """,
                    new { uuid = preparation.ReviewStateUuid }
                )
            );
            row.State.Should()
                .Be(
                    item.Name.StartsWith("retract", StringComparison.Ordinal)
                        ? "retracted"
                        : "rejected"
                );
            row.InputRevisionUuid.Should().Be(item.Negative.Request.Claim.InputRevisionUuid);
            row.OperationUuid.Should().Be(operation);
            using var retainedScope = JsonDocument.Parse(row.Scope);
            AssertSame(retainedScope.RootElement, preparation.OriginalField.Scope);
        }
    }

    private static async Task<(Capture Capture, string Database)> ReadControlAsync()
    {
        Environment.GetEnvironmentVariable("RELISTEN_LOCAL_INGESTION_TESTS").Should().Be("1");
        var path = Environment.GetEnvironmentVariable("RELISTEN_REVIEW_V6_CAPTURE_FILE");
        path.Should().NotBeNullOrWhiteSpace();
        var bytes = await File.ReadAllBytesAsync(path!);
        Convert
            .ToHexStringLower(SHA256.HashData(bytes))
            .Should()
            .Be(
                Environment.GetEnvironmentVariable("RELISTEN_REVIEW_V6_CAPTURE_SHA256"),
                "root must pin the exact cross-binary capture"
            );
        var capture = JsonSerializer.Deserialize<Capture>(bytes)!;
        capture.CaptureVersion.Should().Be("source-review-v6-negative-upgrade-v1");
        capture.DatabaseHost.Should().Be("127.0.0.1");
        capture.DatabasePort.Should().Be(15432);
        Regex.IsMatch(capture.DatabaseName, "^relisten_test_[0-9a-f]{32}$").Should().BeTrue();
        capture.DecoderVersion.Should().Be("provider-observations-v6");
        NormalizationProviderDecoder.Version.Should().Be("provider-observations-v7");
        capture
            .ProcessorVersion.Should()
            .Be(
                NormalizationRecordingProcessor.Version.Replace(
                    "/provider-observations-v7/",
                    "/provider-observations-v6/",
                    StringComparison.Ordinal
                )
            );
        capture
            .Cases.Select(value => value.Name)
            .Should()
            .BeEquivalentTo(new[] { "reject-new", "reject-saved", "retract-new", "retract-saved" });
        var database = Environment.GetEnvironmentVariable("RELISTEN_REVIEW_V7_UPGRADE_DATABASE");
        database.Should().NotBeNullOrWhiteSpace();
        Regex.IsMatch(database!, "^relisten_review_v7_upgrade_[0-9a-f]{32}$").Should().BeTrue();
        database.Should().NotBe(capture.DatabaseName);
        return (capture, database!);
    }

    private static async Task RequireOriginalExtractionAsync(
        NormalizationRecordingProposalService proposals,
        CapturedCase item
    )
    {
        Convert
            .ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(item.ExtractionJson)))
            .Should()
            .Be(item.ExtractionSha256);
        var batch = await proposals.GetAttemptBatchAsync(
            item.Negative.Request.ExtractionAttemptUuid
        );
        batch.Should().NotBeNull();
        using var expected = JsonDocument.Parse(item.ExtractionJson);
        AssertSame(JsonSerializer.SerializeToElement(batch), expected.RootElement);
    }

    private static async Task<Guid> RemoveOriginalFieldAsync(
        DbService db,
        SourceActorReviewPreparation preparation
    )
    {
        var evidence = new NormalizationEvidenceService(db);
        var current = (
            await evidence.GetCurrentInputAsync(preparation.OriginalField.Scope.Resource)
        )!;
        var changed = await evidence.CaptureInputAsync(
            new(
                Guid.NewGuid(),
                current.Resource,
                Encoding.UTF8.GetBytes("{\"field_removed\":true}"),
                current.Revision.MediaType,
                current.Revision.CaptureVersion,
                current.Generation,
                DateTimeOffset.UtcNow
            )
        );
        return changed.Revision.Uuid;
    }

    private static Task<int> ReceiptCountAsync(DbService db, Guid operation) =>
        db.WithWriteConnection(c =>
            c.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM normalization.review_operations WHERE uuid=@operation",
                new { operation }
            )
        );

    private static Task<string> AcceptedAuthorityAsync(DbService db) =>
        db.WithWriteConnection(c =>
            c.QuerySingleAsync<string>(
                """
                SELECT jsonb_build_object(
                    'people',(SELECT jsonb_agg(to_jsonb(v) ORDER BY uuid) FROM normalization.people v),
                    'aliases',(SELECT jsonb_agg(to_jsonb(v) ORDER BY uuid) FROM normalization.person_aliases v),
                    'assignments',(SELECT jsonb_agg(to_jsonb(v) ORDER BY uuid) FROM normalization.source_actor_alias_assignments v),
                    'supports',(SELECT jsonb_agg(to_jsonb(v) ORDER BY uuid) FROM normalization.person_alias_contributor_support v),
                    'credits',(SELECT jsonb_agg(to_jsonb(v) ORDER BY uuid) FROM normalization.recording_credits v))::text
                """
            )
        );

    private static async Task RequireArgumentRefusalAsync(Func<Task> action)
    {
        var failure = await action.Should().ThrowAsync<Exception>();
        ContainsArgumentFailure(failure.Which)
            .Should()
            .BeTrue("old positive interpretation must remain unsupported");
    }

    private static bool ContainsArgumentFailure(Exception error) =>
        error is ArgumentException
        || error.InnerException is { } inner && ContainsArgumentFailure(inner);

    private static bool ContainsConflict(Exception error) =>
        error is NormalizationConflictException
        || error.InnerException is { } inner && ContainsConflict(inner);

    private static void AssertSame<TLeft, TRight>(TLeft left, TRight right) =>
        JsonElement
            .DeepEquals(
                JsonSerializer.SerializeToElement(left),
                JsonSerializer.SerializeToElement(right)
            )
            .Should()
            .BeTrue();

    private sealed record Capture(
        string CaptureVersion,
        string DatabaseHost,
        int DatabasePort,
        string DatabaseName,
        string DecoderVersion,
        string ProcessorVersion,
        CapturedCase[] Cases
    );

    private sealed record CapturedCase(
        string Name,
        bool UseSavedPreparation,
        SourceActorReviewPreparation Negative,
        SourceActorReviewPreparation Positive,
        string ExtractionJson,
        string ExtractionSha256
    );

    private sealed record Decision(
        string State,
        Guid InputRevisionUuid,
        string Scope,
        Guid OperationUuid
    );
}
