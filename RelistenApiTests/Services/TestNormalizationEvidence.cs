using System.Security.Cryptography;
using System.Text;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Relisten;
using Relisten.Api.Models.Normalization;
using Relisten.Data;

namespace RelistenApiTests.Services;

/// <summary>Checks retention, replay and stale response publication against a disposable PostgreSQL catalog.</summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestNormalizationEvidence
{
    private DbService db = null!;
    private NormalizationEvidenceService evidence = null!;
    private string? previousWrite;
    private string? previousRead;

    [OneTimeSetUp]
    public void Start()
    {
        if (Environment.GetEnvironmentVariable("RELISTEN_LOCAL_INGESTION_TESTS") != "1")
        {
            Assert.Ignore("Run ./test-local.sh to include local integration tests.");
        }

        previousWrite = DbService.ConnStr;
        previousRead = DbService.ReadOnlyConnStr;
        db = new DbService(
            LocalTestDatabase.DatabaseUrl,
            null,
            null,
            Host.CreateApplicationBuilder().Environment,
            NullLogger<DbService>.Instance
        );
        evidence = new NormalizationEvidenceService(db);
    }

    [OneTimeTearDown]
    public void Stop()
    {
        if (previousWrite != null)
        {
            DbService.ConnStr = previousWrite;
            DbService.ReadOnlyConnStr = previousRead!;
        }
    }

    [Test]
    public async Task RetainsExactBytesAndFetchMetadataWithoutCatalogOwnership()
    {
        var capture = Capture("{\"taper\": [\"Zoë\", \"B\"],\n\"date\":null}\n") with
        {
            ObservedAt = new DateTimeOffset(2026, 10, 17, 9, 0, 0, TimeSpan.FromHours(-7)),
            ProviderRevision = "etag:upstream-42",
        };
        var result = await evidence.CaptureInputAsync(capture);
        var retained = await evidence.GetInputAsync(result.Revision.Uuid);
        retained!.Payload.Should().Equal(capture.Payload);
        retained.Sha256.Should().Be(Convert.ToHexStringLower(SHA256.HashData(capture.Payload)));
        retained.Resource.Should().Be(capture.Resource);
        result.Fetch.ProviderRevision.Should().Be("etag:upstream-42");
        result.Fetch.ObservedAt.Should().Be(capture.ObservedAt.UtcDateTime);
        result.Fetch.AcceptedAsCurrent.Should().BeTrue();
        result.Fetch.AdvancedCurrent.Should().BeTrue();
        var current = await evidence.GetCurrentInputAsync(capture.Resource);
        current!.Generation.Should().Be(1);
        current.Revision.Uuid.Should().Be(retained.Uuid);
    }

    [Test]
    public async Task RepeatedContentRetainsDistinctFetchesAndReversionAdvancesGeneration()
    {
        var firstCapture = Capture("A");
        var first = await evidence.CaptureInputAsync(firstCapture);
        var repeated = await evidence.CaptureInputAsync(
            firstCapture with
            {
                FetchUuid = Guid.NewGuid(),
                ExpectedGeneration = 1,
                ProviderRevision = "new-provider-revision-same-bytes",
            }
        );
        repeated.Revision.Uuid.Should().Be(first.Revision.Uuid);
        repeated.Fetch.ResourceGeneration.Should().Be(2);
        repeated.Fetch.AcceptedAsCurrent.Should().BeTrue();
        repeated.Fetch.AdvancedCurrent.Should().BeFalse();
        var changed = await evidence.CaptureInputAsync(
            firstCapture with
            {
                FetchUuid = Guid.NewGuid(),
                Payload = Encoding.UTF8.GetBytes("B"),
                ExpectedGeneration = 2,
            }
        );
        var reverted = await evidence.CaptureInputAsync(
            firstCapture with
            {
                FetchUuid = Guid.NewGuid(),
                ExpectedGeneration = 3,
            }
        );
        changed.Revision.Uuid.Should().NotBe(first.Revision.Uuid);
        reverted.Revision.Uuid.Should().Be(first.Revision.Uuid);
        reverted.Fetch.ResourceGeneration.Should().Be(4);
        var history = await evidence.GetFetchHistoryAsync(firstCapture.Resource);
        history.Select(f => f.ResourceGeneration).Should().Equal(1, 2, 3, 4);
        history
            .Select(f => f.InputRevisionUuid)
            .Should()
            .Equal(
                first.Revision.Uuid,
                first.Revision.Uuid,
                changed.Revision.Uuid,
                first.Revision.Uuid
            );
        (await evidence.GetFetchHistoryAsync(firstCapture.Resource, history[1].Sequence, 1))
            .Single()
            .FetchUuid.Should()
            .Be(changed.Fetch.FetchUuid);
        (await evidence.GetCurrentInputAsync(firstCapture.Resource))!
            .Revision.Uuid.Should()
            .Be(first.Revision.Uuid);
    }

    [Test]
    public async Task StaleSlowResponseRetainsHistoryWithoutReplacingNewerCurrentInput()
    {
        var initialCapture = Capture("initial");
        await evidence.CaptureInputAsync(initialCapture);
        var newer = await evidence.CaptureInputAsync(
            initialCapture with
            {
                FetchUuid = Guid.NewGuid(),
                Payload = Encoding.UTF8.GetBytes("newer"),
                ExpectedGeneration = 1,
            }
        );
        var stale = await evidence.CaptureInputAsync(
            initialCapture with
            {
                FetchUuid = Guid.NewGuid(),
                Payload = Encoding.UTF8.GetBytes("slow older response"),
                ExpectedGeneration = 1,
            }
        );
        stale.Fetch.AcceptedAsCurrent.Should().BeFalse();
        stale.Fetch.ResourceGeneration.Should().Be(2);
        (await evidence.GetInputAsync(stale.Revision.Uuid))!
            .Payload.Should()
            .Equal(stale.Revision.Payload);
        var current = await evidence.GetCurrentInputAsync(initialCapture.Resource);
        current!.Revision.Uuid.Should().Be(newer.Revision.Uuid);
        current.Generation.Should().Be(2);
    }

    [Test]
    public async Task FreshUnchangedResponseFencesOlderDifferentResponse()
    {
        var capture = Capture("A");
        await evidence.CaptureInputAsync(capture);
        var fresh = await evidence.CaptureInputAsync(
            capture with
            {
                FetchUuid = Guid.NewGuid(),
                ExpectedGeneration = 1,
            }
        );
        fresh.Fetch.ResourceGeneration.Should().Be(2);
        fresh.Fetch.AdvancedCurrent.Should().BeFalse();
        var older = await evidence.CaptureInputAsync(
            capture with
            {
                FetchUuid = Guid.NewGuid(),
                ExpectedGeneration = 1,
                Payload = Encoding.UTF8.GetBytes("older B"),
            }
        );
        older.Fetch.AcceptedAsCurrent.Should().BeFalse();
        (await evidence.GetCurrentInputAsync(capture.Resource))!
            .Revision.Uuid.Should()
            .Be(fresh.Revision.Uuid);
    }

    [Test]
    public async Task ConcurrentCapturesWithOneExpectedGenerationPublishOnlyOneCurrentInput()
    {
        var capture = Capture("first");
        var captures = await Task.WhenAll(
            evidence.CaptureInputAsync(capture),
            evidence.CaptureInputAsync(
                capture with
                {
                    FetchUuid = Guid.NewGuid(),
                    Payload = Encoding.UTF8.GetBytes("second"),
                }
            )
        );
        captures.Count(c => c.Fetch.AcceptedAsCurrent).Should().Be(1);
        var accepted = captures.Single(c => c.Fetch.AcceptedAsCurrent);
        var current = await evidence.GetCurrentInputAsync(capture.Resource);
        current!.Generation.Should().Be(1);
        current.Revision.Uuid.Should().Be(accepted.Revision.Uuid);
        (await evidence.GetFetchHistoryAsync(capture.Resource)).Should().HaveCount(2);
    }

    [Test]
    public async Task ConcurrentReplayReturnsOneReceiptAndConflictingReplayCannotMutateHistory()
    {
        var capture = Capture("retained");
        var results = await Task.WhenAll(
            evidence.CaptureInputAsync(capture),
            evidence.CaptureInputAsync(capture)
        );
        results[0].Should().BeEquivalentTo(results[1]);
        (await evidence.GetFetchHistoryAsync(capture.Resource)).Should().HaveCount(1);
        var conflict = () => evidence.CaptureInputAsync(capture with { Payload = [1, 2, 3] });
        await conflict.Should().ThrowAsync<InvalidOperationException>();
        var otherResource = capture with
        {
            Resource = capture.Resource with { Provider = "different-provider" },
        };
        var movedReplay = () => evidence.CaptureInputAsync(otherResource);
        await movedReplay.Should().ThrowAsync<InvalidOperationException>();
        (await evidence.GetCurrentInputAsync(otherResource.Resource)).Should().BeNull();
        (await evidence.GetInputAsync(results[0].Revision.Uuid))!
            .Payload.Should()
            .Equal(capture.Payload);
    }

    [Test]
    public async Task MediaCaptureVersionsAndProviderResourceIdentityDoNotCollapse()
    {
        var capture = Capture("same bytes");
        var first = await evidence.CaptureInputAsync(capture);
        var version = await evidence.CaptureInputAsync(
            capture with
            {
                FetchUuid = Guid.NewGuid(),
                ExpectedGeneration = 1,
                CaptureVersion = "projection:2",
            }
        );
        var media = await evidence.CaptureInputAsync(
            capture with
            {
                FetchUuid = Guid.NewGuid(),
                ExpectedGeneration = 2,
                MediaType = "text/plain",
            }
        );
        var resource = await evidence.CaptureInputAsync(
            capture with
            {
                FetchUuid = Guid.NewGuid(),
                Resource = capture.Resource with { ResourceType = "other" },
            }
        );
        new[]
        {
            first.Revision.Uuid,
            version.Revision.Uuid,
            media.Revision.Uuid,
            resource.Revision.Uuid,
        }
            .Should()
            .OnlyHaveUniqueItems();
        (await evidence.GetCurrentInputAsync(capture.Resource))!.Generation.Should().Be(3);
        (await evidence.GetCurrentInputAsync(resource.Revision.Resource))!
            .Generation.Should()
            .Be(1);
    }

    [Test]
    public async Task CancellationLeavesCaptureUncommittedAndRetryCanCommit()
    {
        var capture = Capture("retry me");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelled = () => evidence.CaptureInputAsync(capture, cancellation.Token);
        await cancelled.Should().ThrowAsync<OperationCanceledException>();
        (await evidence.GetCurrentInputAsync(capture.Resource)).Should().BeNull();
        (await evidence.GetFetchHistoryAsync(capture.Resource)).Should().BeEmpty();
        var committed = await evidence.CaptureInputAsync(capture);
        committed.Fetch.AcceptedAsCurrent.Should().BeTrue();
    }

    [Test]
    public async Task CancellationWhileWaitingForResourceLockRollsBackAndReleasesFetchAdmission()
    {
        var initial = Capture("initial");
        await evidence.CaptureInputAsync(initial);
        var update = initial with
        {
            FetchUuid = Guid.NewGuid(),
            ExpectedGeneration = 1,
            Payload = Encoding.UTF8.GetBytes("update"),
        };
        await using var blocker = new NpgsqlConnection(LocalTestDatabase.ConnectionString);
        await blocker.OpenAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await blocker.ExecuteAsync(
            "SELECT uuid FROM normalization.input_resources WHERE provider_key=@ProviderKey FOR UPDATE",
            new { initial.Resource.ProviderKey },
            transaction
        );
        using var cancellation = new CancellationTokenSource();
        var pending = evidence.CaptureInputAsync(update, cancellation.Token);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        var waiting = false;
        while (!waiting && DateTime.UtcNow < deadline)
        {
            waiting = await blocker.QuerySingleAsync<bool>(
                """
                SELECT EXISTS(SELECT 1 FROM pg_stat_activity
                    WHERE datname=current_database() AND wait_event_type='Lock'
                    AND query LIKE '%normalization.input_resources%')
                """,
                transaction: transaction
            );
            if (!waiting)
            {
                await Task.Delay(10);
            }
        }
        waiting.Should().BeTrue("capture must reach the server-side lock before cancellation");
        cancellation.Cancel();
        var cancelled = () => pending;
        await cancelled.Should().ThrowAsync<OperationCanceledException>();
        await transaction.RollbackAsync();
        (await evidence.GetFetchHistoryAsync(initial.Resource)).Should().HaveCount(1);
        var retried = await evidence.CaptureInputAsync(update);
        retried.Fetch.ResourceGeneration.Should().Be(2);
        retried.Fetch.AcceptedAsCurrent.Should().BeTrue();
    }

    [Test]
    public async Task DatabaseRejectsMutationOfRetainedPayloadAndReceipt()
    {
        var capture = Capture("immutable");
        var result = await evidence.CaptureInputAsync(capture);
        var mutateRevision = () =>
            db.WithWriteConnection(c =>
                c.ExecuteAsync(
                    "UPDATE normalization.input_revisions SET payload='changed'::bytea WHERE uuid=@Uuid",
                    new { result.Revision.Uuid }
                )
            );
        var mutateFetch = () =>
            db.WithWriteConnection(c =>
                c.ExecuteAsync(
                    "DELETE FROM normalization.input_fetches WHERE fetch_uuid=@FetchUuid",
                    new { capture.FetchUuid }
                )
            );
        await mutateRevision.Should().ThrowAsync<Exception>().WithMessage("*SQL exception*");
        await mutateFetch.Should().ThrowAsync<Exception>().WithMessage("*SQL exception*");
        (await evidence.GetInputAsync(result.Revision.Uuid))!
            .Payload.Should()
            .Equal(capture.Payload);
        (await evidence.GetFetchHistoryAsync(capture.Resource)).Should().HaveCount(1);
    }

    private static NormalizationInputCapture Capture(string payload) =>
        new(
            Guid.NewGuid(),
            new("archive.org", "item", "normalization-test-" + Guid.NewGuid()),
            Encoding.UTF8.GetBytes(payload),
            "application/json",
            "raw:1",
            0,
            DateTimeOffset.UtcNow
        );
}
