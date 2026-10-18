using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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

[TestFixture]
public sealed class TestLunaCreditFieldSelection
{
    private CreditFieldBundle bundle = null!;

    [SetUp]
    public void Start() => bundle = new();

    [TearDown]
    public void Stop() => bundle.Dispose();

    [Test]
    public async Task Exact_original_fields_include_duplicates_unicode_and_empty_input()
    {
        bundle.SetText(4, "Renée 🌿\u2028River");
        await bundle.WriteAsync();
        var selected = await bundle.SelectAsync();
        selected.Items.Should().HaveCount(100);
        selected.UniqueFieldTuples.Should().Be(97);
        selected.UniqueNonemptyRequestInputs.Should().Be(96);
        selected.Items.Count(item => item.NoText).Should().Be(1);
        selected.Items[4].Text.Should().Be("Renée 🌿\u2028River");
    }

    [Test]
    public async Task Changed_text_cannot_be_justified_by_refreshing_only_file_receipt()
    {
        bundle.Fields[0]["field_text"] = "A different person";
        await bundle.WriteAsync();
        await FluentActions
            .Awaiting(() => bundle.SelectAsync())
            .Should()
            .ThrowAsync<InvalidDataException>();
    }

    [Test]
    public async Task Held_out_field_is_rejected_even_with_matching_file_hashes()
    {
        bundle.Labels[0]["artist_partition"] = "test";
        await bundle.WriteAsync();
        await FluentActions
            .Awaiting(() => bundle.SelectAsync())
            .Should()
            .ThrowAsync<InvalidDataException>();
    }

    [TestCase("uuid")]
    [TestCase("missing")]
    public async Task Malformed_record_shape_is_a_contextual_preflight_error(string mutation)
    {
        if (mutation == "uuid")
        {
            bundle.Labels[0]["resource"]!["resource_uuid"] = "invalid-uuid";
        }
        else
        {
            bundle.Labels[0].Remove("resource");
        }
        await bundle.WriteAsync();
        await FluentActions
            .Awaiting(() => bundle.SelectAsync())
            .Should()
            .ThrowAsync<InvalidDataException>()
            .WithMessage("Malformed field comparison*");
    }

    [Test]
    public async Task Source_reference_conflict_is_not_replaced_with_label_context()
    {
        bundle.Fields[0]["evidence_id"] = "source_sample:" + Guid.NewGuid();
        await bundle.WriteAsync();
        await FluentActions
            .Awaiting(() => bundle.SelectAsync())
            .Should()
            .ThrowAsync<InvalidDataException>();
    }

    [Test]
    public async Task Truncation_duplicate_fields_and_contacts_fail_preflight()
    {
        bundle.Fields[1]["label_id"] = bundle.Fields[0]["label_id"]!.GetValue<string>();
        await bundle.WriteAsync();
        await FluentActions
            .Awaiting(() => bundle.SelectAsync())
            .Should()
            .ThrowAsync<InvalidDataException>();
        bundle.Fields[1]["label_id"] = bundle.Labels[1]["label_id"]!.GetValue<string>();
        bundle.SetText(3, "person@example.test");
        await bundle.WriteAsync();
        await FluentActions
            .Awaiting(() => bundle.SelectAsync())
            .Should()
            .ThrowAsync<InvalidDataException>();
        bundle.Fields.RemoveAt(99);
        await bundle.WriteAsync();
        await FluentActions
            .Awaiting(() => bundle.SelectAsync())
            .Should()
            .ThrowAsync<InvalidDataException>();
    }
}

[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestLunaCreditFieldEvaluationBatch
{
    private DbService db = null!;
    private NormalizationExtractionCacheService cache = null!;
    private string? previousWrite;
    private string? previousRead;
    private CreditFieldBundle bundle = null!;
    private CreditFieldResponses handler = null!;
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
    public async Task Start()
    {
        bundle = new();
        await bundle.WriteAsync();
        handler = new();
        http = new(handler);
    }

    [TearDown]
    public void Stop()
    {
        http.Dispose();
        bundle.Dispose();
    }

    private LunaCreditFieldEvaluationBatch Batch(Func<string>? key = null) =>
        new(http, cache, db, new(), key ?? (() => "fixture-token"));

    private Task<LunaCreditFieldEvaluationReport> Run(
        Guid job,
        string output,
        Func<string>? key = null,
        CancellationToken ct = default
    ) =>
        Batch(key)
            .RunAsync(bundle.LabelsPath, bundle.FieldsPath, bundle.ReceiptPath, output, job, ct);

    [Test]
    public async Task All_fields_compare_exact_single_input_and_keyless_replay_needs_no_paid_retry()
    {
        var job = Guid.NewGuid();
        var first = await Run(job, bundle.Output("first"));
        first.State.Should().Be("completed");
        first.Fields.Should().Be(100);
        first.NoText.Should().Be(1);
        first.LunaFromCache.Should().Be(3);
        first.KeylessReplayFromCache.Should().Be(99);
        first.PaidAttempts.Should().Be(96);
        first.Job.CapUsd.Should().Be(5m);
        handler.Calls.Should().Be(96);
        var second = await Run(job, bundle.Output("replay"), () => "");
        second.State.Should().Be("completed");
        second.LunaFromCache.Should().Be(99);
        second.KeylessReplayFromCache.Should().Be(99);
        second.Job.AccountedUsd.Should().Be(first.Job.AccountedUsd);
        handler.Calls.Should().Be(96);
        var audit = JsonNode.Parse(
            await File.ReadAllTextAsync(Path.Combine(bundle.Output("first"), "receipts.json"))
        )!;
        audit["requests"]!.AsArray().Should().HaveCount(96);
        foreach (var request in audit["requests"]!.AsArray())
        {
            using var rendered = JsonDocument.Parse(
                request!["renderedRequest"]!.GetValue<string>()
            );
            using var input = JsonDocument.Parse(
                rendered.RootElement.GetProperty("input")[1].GetProperty("content").GetString()!
            );
            input.RootElement.GetProperty("fields").EnumerateObject().Should().ContainSingle();
            input.RootElement.GetProperty("context").ValueKind.Should().Be(JsonValueKind.Null);
        }
    }

    [Test]
    public async Task Invalid_preflight_and_existing_output_create_no_job_or_http_work()
    {
        var invalidJob = Guid.NewGuid();
        bundle.Labels[0]["family_partition"] = "validation";
        await bundle.WriteAsync();
        await FluentActions
            .Awaiting(() => Run(invalidJob, bundle.Output("invalid")))
            .Should()
            .ThrowAsync<InvalidDataException>();
        handler.Calls.Should().Be(0);
        (await JobCount(invalidJob)).Should().Be(0);
        var existing = bundle.Output("existing");
        Directory.CreateDirectory(existing);
        await File.WriteAllTextAsync(Path.Combine(existing, "original"), "preserve");
        await FluentActions
            .Awaiting(() => Run(Guid.NewGuid(), existing))
            .Should()
            .ThrowAsync<IOException>();
        (await File.ReadAllTextAsync(Path.Combine(existing, "original"))).Should().Be("preserve");
    }

    [Test]
    public async Task Budget_exhaustion_reports_local_fallback_without_claiming_luna_output()
    {
        var job = Guid.NewGuid();
        await cache.CreateJobAsync(job, 5m);
        await db.WithWriteConnection(connection =>
            connection.ExecuteAsync(
                "UPDATE normalization.extraction_jobs SET accounted_usd=4.999999 WHERE uuid=@job",
                new { job }
            )
        );
        var report = await Run(job, bundle.Output("budget"));
        report.State.Should().Be("completed_partial");
        report.States["budget_exhausted"].Should().Be(99);
        report.PaidAttempts.Should().Be(0);
        handler.Calls.Should().Be(0);
        report.Job.AccountedUsd.Should().Be(4.999999m);
    }

    [Test]
    public async Task FailedResponseDiagnosticsAreExportedWithTheirAttemptAndExactBytes()
    {
        handler.InvalidFirst = true;
        var output = bundle.Output("failure-audit");
        var report = await Run(Guid.NewGuid(), output);

        report.State.Should().Be("completed_partial");
        var audit = JsonNode.Parse(
            await File.ReadAllTextAsync(Path.Combine(output, "receipts.json"))
        )!;
        var failure = audit["attempts"]!
            .AsArray()
            .Should()
            .ContainSingle(attempt =>
                attempt!["failureStage"] != null
                && attempt["failureStage"]!.GetValue<string>() == "credit_schema"
            )
            .Which!;
        failure["state"]!.GetValue<string>().Should().Be("failed");
        Convert
            .FromBase64String(failure["failedResponseBody"]!.GetValue<string>())
            .Should()
            .Equal(handler.InvalidBody!);
    }

    [Test]
    public async Task TotalJobExhaustionStopsHttpAndStillExportsTheCompleteOwnJobReceipt()
    {
        var job = Guid.NewGuid();
        await cache.CreateJobAsync(job, 5m, maximumTotalAttempts: 200);
        var claim = await cache.TryClaimAsync(
            job,
            "earlier-job-work-" + job,
            .01m,
            201,
            TimeSpan.FromMinutes(3)
        );
        await cache.FailAsync(
            claim.AttemptUuid!.Value,
            "invalid_response",
            receipt: new("", "old-fixture", 0, 2),
            actualUsd: .000001m
        );
        await AddHistoricalFailures(job, claim.CacheKey, 199);
        var before = await cache.GetJobAsync(job);
        var output = bundle.Output("attempt-cap");

        var report = await Run(job, output);

        report.State.Should().Be("completed_partial");
        report.States["job_attempts_exhausted"].Should().Be(99);
        report.PaidAttempts.Should().Be(200);
        report.Job.MaximumTotalAttempts.Should().Be(200);
        report.Job.AccountedUsd.Should().Be(before.AccountedUsd);
        handler.Calls.Should().Be(0);
        var audit = JsonNode.Parse(
            await File.ReadAllTextAsync(Path.Combine(output, "receipts.json"))
        )!;
        audit["attempts"]!.AsArray().Should().HaveCount(200);
    }

    [Test]
    public async Task ForeignRequestHistoryCannotOverflowTheOwnJobAuditBound()
    {
        var selected = (await bundle.SelectAsync()).Items[0];
        var request = LunaRecordingCreditExtractor.RenderRequest(
            new Dictionary<string, string?> { [selected.Field] = selected.Text },
            null,
            new()
        );
        var foreignJob = Guid.NewGuid();
        await cache.CreateJobAsync(foreignJob, 5m);
        var claim = await cache.TryClaimAsync(
            foreignJob,
            request,
            .01m,
            203,
            TimeSpan.FromMinutes(3)
        );
        var response = TestLunaRecordingCredits.Credit(
            selected.Text,
            "taper",
            selected.Field,
            selected.Text
        );
        await cache.CompleteAsync(
            claim.AttemptUuid!.Value,
            new LunaCreditResponse(response, "foreign-fixture", 0, 2),
            .000001m
        );
        // Other cache users can have longer histories. Keep that retained evidence in PostgreSQL,
        // but do not charge or export it as this comparison job's paid work.
        await AddHistoricalFailures(foreignJob, claim.CacheKey, 201);
        var ownJob = Guid.NewGuid();
        var output = bundle.Output("foreign-history");

        var report = await Run(ownJob, output);

        report.State.Should().Be("completed");
        report.PaidAttempts.Should().Be(95);
        handler.Calls.Should().Be(95);
        var audit = JsonNode.Parse(
            await File.ReadAllTextAsync(Path.Combine(output, "receipts.json"))
        )!;
        var attempts = audit["attempts"]!.AsArray();
        attempts.Should().HaveCount(95);
        attempts.Should().OnlyContain(attempt => attempt!["jobUuid"]!.GetValue<Guid>() == ownJob);
        var cachedRequest = audit["requests"]!
            .AsArray()
            .Should()
            .ContainSingle(row => row!["cacheKey"]!.GetValue<string>() == claim.CacheKey)
            .Which!;
        cachedRequest["responseJson"]!.GetValue<string>().Should().Be(response);
        (await cache.GetJobAsync(foreignJob)).AccountedUsd.Should().Be(.000202m);
    }

    private Task AddHistoricalFailures(Guid job, string cacheKey, int count) =>
        db.WithWriteConnection(async connection =>
        {
            using var transaction = connection.BeginTransaction();
            await connection.ExecuteAsync(
                """
                INSERT INTO normalization.extraction_attempts(
                    uuid,request_sha256,job_uuid,state,lease_until,reserved_usd,actual_usd,
                    input_tokens,output_tokens,reason,completed_at
                )
                SELECT gen_random_uuid(),@cacheKey,@job,'failed',clock_timestamp(),.01,.000001,
                    0,2,'invalid_response',clock_timestamp()
                FROM generate_series(1,@count);
                UPDATE normalization.extraction_jobs SET accounted_usd=accounted_usd+@count*.000001 WHERE uuid=@job;
                """,
                new
                {
                    job,
                    cacheKey,
                    count,
                },
                transaction
            );
            transaction.Commit();
        });

    [Test]
    public async Task Failed_provider_attempt_retains_reservation_and_successful_cache_recovers()
    {
        handler.FailFirst = true;
        var job = Guid.NewGuid();
        var first = await Run(job, bundle.Output("partial"));
        first.State.Should().Be("completed_partial");
        first.States["unresolved"].Should().Be(1);
        first.PaidAttempts.Should().Be(97);
        var calls = handler.Calls;
        var replay = await Run(job, bundle.Output("recovered"), () => "");
        replay.State.Should().Be("completed");
        replay.Job.AccountedUsd.Should().Be(first.Job.AccountedUsd);
        handler.Calls.Should().Be(calls);
        first.Job.AccountedUsd.Should().BeGreaterThan(96m * (42m * .1m + 12m * .5m) / 1_000_000m);
    }

    [Test]
    public async Task Cancellation_keeps_cost_receipt_without_completed_manifest()
    {
        using var cancellation = new CancellationTokenSource();
        handler.CancelDuringResponse = cancellation;
        var job = Guid.NewGuid();
        var output = bundle.Output("cancelled");
        await FluentActions
            .Awaiting(() => Run(job, output, ct: cancellation.Token))
            .Should()
            .ThrowAsync<OperationCanceledException>();
        File.Exists(Path.Combine(output, "manifest.json")).Should().BeFalse();
        File.Exists(Path.Combine(output, "receipts.json")).Should().BeTrue();
        (await cache.GetJobAsync(job)).AccountedUsd.Should().BeGreaterThan(0);
        handler.CancelDuringResponse = null;
        var recovered = await Run(job, bundle.Output("retry"));
        recovered.State.Should().Be("completed");
    }

    [Test]
    public async Task Precancelled_work_has_no_job_or_provider_request()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var job = Guid.NewGuid();
        await FluentActions
            .Awaiting(() => Run(job, bundle.Output("never"), ct: cancellation.Token))
            .Should()
            .ThrowAsync<OperationCanceledException>();
        (await JobCount(job)).Should().Be(0);
        handler.Calls.Should().Be(0);
    }

    private Task<int> JobCount(Guid job) =>
        db.WithWriteConnection(connection =>
            connection.QuerySingleAsync<int>(
                "SELECT count(*)::int FROM normalization.extraction_jobs WHERE uuid=@job",
                new { job }
            )
        );
}

/// <summary>Independent field UUIDs permit real cache deduplication without sharing fixtures across tests.</summary>
internal sealed class CreditFieldBundle : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(),
        "credit-fields-" + Guid.NewGuid().ToString("N")
    );
    internal List<JsonObject> Labels { get; } = [];
    internal List<JsonObject> Fields { get; } = [];
    internal string LabelsPath => Path.Combine(directory, "labels.jsonl");
    internal string FieldsPath => Path.Combine(directory, "fields.jsonl");
    internal string ReceiptPath => Path.Combine(directory, "receipt.json");

    internal CreditFieldBundle()
    {
        Directory.CreateDirectory(directory);
        var nonce = Guid.NewGuid().ToString("N");
        for (var index = 0; index < 100; index++)
        {
            var source = Guid.NewGuid();
            var field = index < 78 ? "taper" : "transferrer";
            var text = index == 99 ? "" : "Jane " + nonce + " " + (index < 4 ? 0 : index);
            var path = "/raw_record/row/" + field;
            var id = $"credit-field:{source:D}:{field}";
            var evidence = "source_sample:" + source.ToString("D");
            var hash = Hash(Encoding.UTF8.GetBytes(text));
            Fields.Add(
                new()
                {
                    ["label_id"] = id,
                    ["evidence_id"] = evidence,
                    ["field_path"] = path,
                    ["field_text"] = text,
                    ["field_sha256"] = hash,
                }
            );
            Labels.Add(
                new()
                {
                    ["label_id"] = id,
                    ["evidence_id"] = evidence,
                    ["task_id"] = "extract:" + evidence,
                    ["status"] = "draft_development",
                    ["scope"] = "complete_single_field",
                    ["family_partition"] = "development",
                    ["artist_partition"] = "development",
                    ["field_path"] = path,
                    ["field_sha256"] = hash,
                    ["field_codepoints"] = text.EnumerateRunes().Count(),
                    ["resource"] = new JsonObject
                    {
                        ["provider"] = "relisten_catalog_snapshot",
                        ["resource_type"] = "source",
                        ["resource_uuid"] = source.ToString("D"),
                    },
                    ["artist"] = new JsonObject { ["uuid"] = Guid.NewGuid().ToString("D") },
                    ["answer"] = new JsonObject
                    {
                        ["reason"] = "This answer must never enter the model request.",
                    },
                }
            );
        }
    }

    internal void SetText(int index, string text)
    {
        var hash = Hash(Encoding.UTF8.GetBytes(text));
        Fields[index]["field_text"] = text;
        Fields[index]["field_sha256"] = hash;
        Labels[index]["field_sha256"] = hash;
        Labels[index]["field_codepoints"] = text.EnumerateRunes().Count();
    }

    internal async Task WriteAsync()
    {
        await File.WriteAllTextAsync(
            LabelsPath,
            string.Join("\n", Labels.Select(item => item.ToJsonString())) + "\n"
        );
        await File.WriteAllTextAsync(
            FieldsPath,
            string.Join("\n", Fields.Select(item => item.ToJsonString())) + "\n"
        );
        await File.WriteAllTextAsync(
            ReceiptPath,
            new JsonObject
            {
                ["status"] = "draft_development",
                ["schema_version"] = "credit-field-draft-v1",
                ["fields"] = 100,
                ["labels_sha256"] = Hash(await File.ReadAllBytesAsync(LabelsPath)),
                ["raw_fields_sha256"] = Hash(await File.ReadAllBytesAsync(FieldsPath)),
            }.ToJsonString()
        );
    }

    internal Task<LunaCreditFieldSelection> SelectAsync() =>
        LunaCreditFieldEvaluationBatch.ReadSelectionAsync(LabelsPath, FieldsPath, ReceiptPath);

    internal string Output(string name) => Path.Combine(directory, name);

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public void Dispose() => Directory.Delete(directory, true);
}

internal sealed class CreditFieldResponses : HttpMessageHandler
{
    internal int Calls { get; private set; }
    internal bool FailFirst { get; set; }
    internal bool InvalidFirst { get; set; }
    internal byte[]? InvalidBody { get; private set; }
    internal CancellationTokenSource? CancelDuringResponse { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken ct
    )
    {
        Calls++;
        using var rendered = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
        using var input = JsonDocument.Parse(
            rendered.RootElement.GetProperty("input")[1].GetProperty("content").GetString()!
        );
        var fields = input.RootElement.GetProperty("fields").EnumerateObject().ToArray();
        fields.Should().ContainSingle();
        input.RootElement.GetProperty("context").ValueKind.Should().Be(JsonValueKind.Null);
        var field = fields.Single();
        if (FailFirst && Calls == 1)
        {
            return new(HttpStatusCode.ServiceUnavailable);
        }
        var name = field.Value.GetString()!;
        var extracted = JsonSerializer.Serialize(
            new
            {
                credits = new[]
                {
                    new
                    {
                        name,
                        role = field.Name == "transferrer" ? "transferrer" : "taper",
                        field = field.Name,
                        quote = name,
                        start = 0,
                        end = name.EnumerateRunes().Count(),
                    },
                },
            }
        );
        var body = JsonSerializer.Serialize(
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
                            new
                            {
                                type = "output_text",
                                text = InvalidFirst && Calls == 1 ? "{}" : extracted,
                            },
                        },
                    },
                },
            }
        );
        if (InvalidFirst && Calls == 1)
        {
            InvalidBody = Encoding.UTF8.GetBytes(body);
        }
        CancelDuringResponse?.Cancel();
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        response.Headers.Add("x-request-id", "fixture-request-" + Calls);
        return response;
    }
}
