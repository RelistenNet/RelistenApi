using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Relisten;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
using Relisten.Normalization;
using Relisten.Normalization.Batch;

namespace RelistenApiTests.Normalization;

/// <summary>
/// Synthetic HTTP with the real local ledger verifies parallel request ownership,
/// durable output and recovery. These tests cannot call the provider.
/// </summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestLunaCreditEvaluationConcurrency
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };
    private DbService db = null!;
    private NormalizationExtractionCacheService cache = null!;
    private string? previousWrite;
    private string? previousRead;
    private string directory = null!;
    private string input = null!;
    private ParallelResponses handler = null!;
    private HttpClient http = null!;

    [OneTimeSetUp]
    public void Initialize()
    {
        if (Environment.GetEnvironmentVariable("RELISTEN_LOCAL_INGESTION_TESTS") != "1")
        {
            Assert.Ignore("Run ./test-local.sh to include local integration tests.");
        }
        previousWrite = DbService.ConnStr;
        previousRead = DbService.ReadOnlyConnStr;
        db = new(
            LocalTestDatabase.DatabaseUrl,
            null,
            null,
            Host.CreateApplicationBuilder().Environment,
            NullLogger<DbService>.Instance
        );
        cache = new(db);
    }

    [OneTimeTearDown]
    public void Restore()
    {
        if (previousWrite is not null)
        {
            DbService.ConnStr = previousWrite;
            DbService.ReadOnlyConnStr = previousRead!;
        }
    }

    [SetUp]
    public void Start()
    {
        directory = Path.Combine(
            Path.GetTempPath(),
            "relisten-luna-parallel-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(directory);
        input = Path.Combine(directory, "extractions.jsonl");
        handler = new();
        http = new(handler);
    }

    [TearDown]
    public void Stop()
    {
        http.Dispose();
        Directory.Delete(directory, true);
    }

    [Test]
    public async Task Bounded_workers_share_identical_requests_and_replay_without_new_attempts()
    {
        var rows = Enumerable.Range(0, 12).Select(_ => Source()).ToArray();
        var repeated = Enumerable
            .Range(0, 5)
            .Select(_ =>
                rows[0] with
                {
                    ResourceUuid = Guid.NewGuid(),
                    ArtistUuid = Guid.NewGuid(),
                }
            );
        await WriteInputAsync(rows.Concat(repeated).ToArray());
        handler.HoldUntilReleased(3);
        var job = Guid.NewGuid();
        var running = RunAsync(job, "first", concurrency: 3);
        try
        {
            await handler.FirstWave.Task.WaitAsync(TimeSpan.FromSeconds(15));
            handler.MaximumActive.Should().Be(3);
        }
        finally
        {
            handler.Release();
        }
        var first = await running;

        first.State.Should().Be("completed");
        first.Records.Should().Be(17);
        first.UniqueRequests.Should().Be(12);
        first.ReusedWithinBatch.Should().Be(5);
        first
            .SelectionSha256.Should()
            .Be(
                Convert.ToHexStringLower(
                    SHA256.HashData(
                        await File.ReadAllBytesAsync(
                            Path.Combine(directory, "first", "selection.json")
                        )
                    )
                )
            );
        first
            .ResultsSha256.Should()
            .Be(
                Convert.ToHexStringLower(
                    SHA256.HashData(
                        await File.ReadAllBytesAsync(
                            Path.Combine(directory, "first", "results.jsonl")
                        )
                    )
                )
            );
        handler.Calls.Should().Be(12);
        handler.MaximumActive.Should().Be(3);
        var output = await ReadResultsAsync("first");
        output.Should().HaveCount(17);
        output
            .Select(row => row.GetProperty("resourceUuid").GetGuid())
            .Distinct()
            .Should()
            .HaveCount(17);
        output
            .Should()
            .OnlyContain(row =>
                row.GetProperty("luna").GetProperty("state").GetString() == "extracted"
            );
        (await AttemptsAsync(job))
            .Should()
            .HaveCount(12)
            .And.OnlyContain(attempt => attempt.State == "succeeded");

        var replay = await RunAsync(job, "replay", concurrency: 4, key: "");
        replay.FromCache.Should().Be(17);
        replay.AccountedUsd.Should().Be(first.AccountedUsd);
        handler.Calls.Should().Be(12);
        (await AttemptsAsync(job)).Should().HaveCount(12);
    }

    [Test]
    public async Task Field_order_shares_a_request_but_null_and_empty_text_remain_distinct()
    {
        var first = Source();
        var name = first.Inputs["taper"];
        first = first with
        {
            Inputs = new Dictionary<string, string?> { ["taper"] = name, ["description"] = null },
        };
        var reordered = first with
        {
            ResourceUuid = Guid.NewGuid(),
            Inputs = new Dictionary<string, string?> { ["description"] = null, ["taper"] = name },
        };
        var empty = first with
        {
            ResourceUuid = Guid.NewGuid(),
            Inputs = new Dictionary<string, string?> { ["description"] = "", ["taper"] = name },
        };
        await WriteInputAsync([first, reordered, empty]);

        var report = await RunAsync(Guid.NewGuid(), "field-maps", concurrency: 2);

        report.State.Should().Be("completed");
        report.UniqueRequests.Should().Be(2);
        report.ReusedWithinBatch.Should().Be(1);
        handler.Calls.Should().Be(2);
        var rows = await ReadResultsAsync("field-maps");
        rows.Should().HaveCount(3);
        rows.Select(row => row.GetProperty("luna").GetProperty("cacheKey").GetString())
            .Distinct()
            .Should()
            .HaveCount(2);
    }

    [Test]
    public async Task Concurrent_admission_preserves_an_existing_job_attempt_cap()
    {
        await WriteInputAsync(Enumerable.Range(0, 9).Select(_ => Source()).ToArray());
        var job = Guid.NewGuid();
        await cache.CreateJobAsync(job, 25m, maximumTotalAttempts: 3);

        var report = await RunAsync(job, "bounded", concurrency: 4);

        report.State.Should().Be("completed_partial");
        report.Records.Should().Be(9);
        report.States["job_attempts_exhausted"].Should().Be(6);
        handler.Calls.Should().Be(3);
        (await AttemptsAsync(job)).Should().HaveCount(3);
        (await cache.GetJobAsync(job)).MaximumTotalAttempts.Should().Be(3);
    }

    [Test]
    public async Task Cancellation_drains_attempts_retains_reservations_and_recovers_in_a_new_directory()
    {
        await WriteInputAsync(Enumerable.Range(0, 6).Select(_ => Source()).ToArray());
        var job = Guid.NewGuid();
        handler.HoldUntilReleased(2);
        handler.AllowBeforeHold = 1;
        using var cancellation = new CancellationTokenSource();
        var running = RunAsync(job, "cancelled", concurrency: 2, ct: cancellation.Token);
        try
        {
            await handler.FirstWave.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await WaitForPrefixAsync("cancelled", 1);
        }
        finally
        {
            cancellation.Cancel();
        }
        await FluentActions
            .Awaiting(() => running)
            .Should()
            .ThrowAsync<OperationCanceledException>();

        var attempts = await AttemptsAsync(job);
        attempts.Length.Should().BeInRange(2, 3);
        attempts.Should().NotContain(attempt => attempt.State == "running");
        attempts.Count(attempt => attempt.State == "succeeded").Should().Be(1);
        attempts
            .Count(attempt => attempt.State == "failed" && attempt.Reason == "cancelled")
            .Should()
            .Be(attempts.Length - 1);
        (await ReadResultsAsync("cancelled")).Should().ContainSingle();
        var reserved = (await cache.GetJobAsync(job)).AccountedUsd;
        reserved.Should().BeGreaterThan(0);
        File.Exists(Path.Combine(directory, "cancelled", "manifest.json")).Should().BeFalse();
        using var progress = JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(directory, "cancelled", "progress.json"))
        );
        progress.RootElement.GetProperty("state").GetString().Should().Be("interrupted");
        progress.RootElement.GetProperty("records").GetInt32().Should().Be(1);

        handler.Release();
        var recovered = await RunAsync(job, "recovered", concurrency: 2);
        recovered.State.Should().Be("completed");
        recovered.Records.Should().Be(6);
        recovered.FromCache.Should().Be(1);
        recovered
            .AccountedUsd.Should()
            .BeGreaterThan(reserved, "uncertain cancelled reservations remain accounted");
        (await AttemptsAsync(job))
            .Should()
            .HaveCount(6 + attempts.Length - 1)
            .And.NotContain(attempt => attempt.State == "running");
    }

    [Test]
    public async Task One_provider_failure_keeps_independent_source_results_and_partial_counts()
    {
        var rows = Enumerable.Range(0, 5).Select(_ => Source()).ToArray();
        await WriteInputAsync(rows);
        handler.RejectName = rows[2].Inputs["taper"];

        var report = await RunAsync(Guid.NewGuid(), "partial", concurrency: 3);

        report.State.Should().Be("completed_partial");
        report.Records.Should().Be(5);
        report.States["extracted"].Should().Be(4);
        report.States["unresolved"].Should().Be(1);
        var output = await ReadResultsAsync("partial");
        output.Should().HaveCount(5);
        output
            .Single(row => row.GetProperty("resourceUuid").GetGuid() == rows[2].ResourceUuid)
            .GetProperty("luna")
            .GetProperty("reason")
            .GetString()
            .Should()
            .Be("provider_error");
    }

    [Test]
    public async Task Output_failure_drains_workers_and_never_publishes_completion()
    {
        await WriteInputAsync(Enumerable.Range(0, 6).Select(_ => Source()).ToArray());
        handler.HoldUntilReleased(2);
        var job = Guid.NewGuid();
        var running = RunAsync(job, "storage-failure", concurrency: 2);
        try
        {
            await handler.FirstWave.Task.WaitAsync(TimeSpan.FromSeconds(15));
            // A completed provider result can still encounter an independent output failure.
            Directory.CreateDirectory(
                Path.Combine(directory, "storage-failure", "progress.json.writing")
            );
        }
        finally
        {
            handler.Release();
        }
        await FluentActions.Awaiting(() => running).Should().ThrowAsync<Exception>();

        File.Exists(Path.Combine(directory, "storage-failure", "manifest.json")).Should().BeFalse();
        handler.Active.Should().Be(0);
        (await AttemptsAsync(job))
            .Should()
            .NotBeEmpty()
            .And.NotContain(attempt => attempt.State == "running");
    }

    private async Task WaitForPrefixAsync(string output, int records)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(15))
        {
            using var progress = JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.Combine(directory, output, "progress.json"))
            );
            if (progress.RootElement.GetProperty("records").GetInt32() >= records)
            {
                return;
            }
            await Task.Delay(10);
        }
        throw new TimeoutException("No flushed result prefix appeared.");
    }

    private Task<LunaCreditEvaluationReport> RunAsync(
        Guid job,
        string output,
        int concurrency,
        string key = "fixture-key",
        CancellationToken ct = default
    ) =>
        new LunaCreditEvaluationBatch(
            new LunaRecordingCreditExtractor(http, cache, new(), () => key),
            cache
        ).RunAsync(input, Path.Combine(directory, output), job, 100, ct, concurrency);

    private Task<Attempt[]> AttemptsAsync(Guid job) =>
        db.WithWriteConnection(async connection =>
            (
                await connection.QueryAsync<Attempt>(
                    "SELECT state AS State,reason AS Reason FROM normalization.extraction_attempts WHERE job_uuid=@job",
                    new { job }
                )
            ).ToArray()
        );

    private async Task<JsonElement[]> ReadResultsAsync(string output) =>
        (await File.ReadAllLinesAsync(Path.Combine(directory, output, "results.jsonl")))
            .Select(line => JsonSerializer.Deserialize<JsonElement>(line))
            .ToArray();

    private async Task WriteInputAsync(NormalizationBatchExtraction[] rows)
    {
        await File.WriteAllLinesAsync(
            input,
            rows.Select(row => JsonSerializer.Serialize(row, Json))
        );
        await File.WriteAllTextAsync(
            Path.Combine(directory, "manifest.json"),
            JsonSerializer.Serialize(
                new
                {
                    state = "completed",
                    outputRecords = rows.Length,
                    snapshot = new
                    {
                        kind = "snapshot",
                        snapshot = "fixture:1:",
                        read_only = "on",
                        in_recovery = true,
                    },
                    snapshotCompletion = new
                    {
                        kind = "snapshot_complete",
                        completed_at = "2026-10-18T12:00:00Z",
                    },
                    inputSha256 = new string('a', 64),
                    runIdentitySha256 = new string('b', 64),
                    extractorVersions = new
                    {
                        recording_credits = RecordingCreditExtractor.Version,
                        track_title = TrackTitleExtractor.Version,
                    },
                }
            )
        );
    }

    private static NormalizationBatchExtraction Source()
    {
        var fields = new Dictionary<string, string?>
        {
            ["taper"] = "Taper Person " + Guid.NewGuid().ToString("N"),
        };
        return new(
            "source_sample",
            1,
            Guid.NewGuid(),
            1,
            Guid.NewGuid(),
            "fixture-family",
            fields,
            null,
            new RecordingCreditExtractor().Extract(fields),
            null
        );
    }

    private sealed record Attempt(string State, string? Reason);

    private sealed class ParallelResponses : HttpMessageHandler
    {
        private readonly object gate = new();
        private TaskCompletionSource? release;
        private int threshold;
        private int active;
        internal int Calls { get; private set; }
        internal int AllowBeforeHold { get; set; }
        internal int Active
        {
            get
            {
                lock (gate)
                {
                    return active;
                }
            }
        }
        internal int MaximumActive { get; private set; }
        internal string? RejectName { get; set; }
        internal TaskCompletionSource FirstWave { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void HoldUntilReleased(int count)
        {
            threshold = count;
            release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        internal void Release() => release?.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken ct
        )
        {
            int callNumber;
            lock (gate)
            {
                callNumber = ++Calls;
                active++;
                MaximumActive = Math.Max(MaximumActive, active);
                if (active >= threshold)
                {
                    FirstWave.TrySetResult();
                }
            }
            try
            {
                if (release is not null && callNumber > AllowBeforeHold)
                {
                    await release.Task.WaitAsync(ct);
                }
                using var rendered = JsonDocument.Parse(
                    await request.Content!.ReadAsStringAsync(ct)
                );
                using var input = JsonDocument.Parse(
                    rendered.RootElement.GetProperty("input")[1].GetProperty("content").GetString()!
                );
                var field = input.RootElement.GetProperty("fields").GetProperty("taper");
                var name = field.GetString()!;
                if (name == RejectName)
                {
                    return new(HttpStatusCode.ServiceUnavailable);
                }
                var credits = JsonSerializer.Serialize(
                    new
                    {
                        credits = new[]
                        {
                            new
                            {
                                name,
                                role = "taper",
                                field = "taper",
                                quote = name,
                                start = (int?)null,
                                end = (int?)null,
                            },
                        },
                    }
                );
                return new(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        JsonSerializer.Serialize(
                            new
                            {
                                status = "completed",
                                usage = new { input_tokens = 42, output_tokens = 12 },
                                output = new[]
                                {
                                    new
                                    {
                                        type = "message",
                                        content = new[]
                                        {
                                            new { type = "output_text", text = credits },
                                        },
                                    },
                                },
                            }
                        ),
                        Encoding.UTF8,
                        "application/json"
                    ),
                };
            }
            finally
            {
                lock (gate)
                {
                    active--;
                }
            }
        }
    }
}
