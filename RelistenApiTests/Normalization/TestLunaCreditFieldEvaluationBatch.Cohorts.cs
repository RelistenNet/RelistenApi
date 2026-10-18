using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using FluentAssertions;
using Relisten.Normalization;
using Relisten.Normalization.Batch;

namespace RelistenApiTests.Normalization;

[TestFixture]
public sealed class TestLunaCreditCohortSelection
{
    [TestCase("development", "development", "development")]
    [TestCase("tune", "validation", "development")]
    [TestCase("untouched_evaluation", "test", "test")]
    public async Task AuthenticatedPurposePreservesWholeFieldsAndFrozenPlanIdentity(
        string purpose,
        string artistPartition,
        string familyPartition
    )
    {
        using var cohort = new CreditCohortBundle(3, purpose, artistPartition, familyPartition);
        cohort.SetText(0, "Renée 🌿\u2028River");
        cohort.SetText(1, "Renée 🌿\u2028River");
        cohort.SetText(2, "");
        var unknownOrigin = cohort.ScopeItems[2]["provider_origin"]!;
        unknownOrigin["provider"] = "unknown";
        unknownOrigin["kind"] = "unknown";
        unknownOrigin["evidence_sha256"] = null;
        unknownOrigin["resource_key_sha256"] = null;
        await cohort.WriteAsync();

        var selection = await cohort.SelectAsync();

        selection
            .Items.Select(item => item.Text)
            .Should()
            .Equal("Renée 🌿\u2028River", "Renée 🌿\u2028River", "");
        selection
            .Items.Select(item => item.LabelId)
            .Should()
            .Equal(cohort.Labels.Select(label => label["label_id"]!.GetValue<string>()));
        selection.UniqueFieldTuples.Should().Be(2);
        selection.UniqueNonemptyRequestInputs.Should().Be(1);
        selection.Items.Count(item => item.NoText).Should().Be(1);
        selection.Cohort.Should().NotBeNull();
        selection.Cohort!.PlanSha256.Should().Be(cohort.PlanHash);
        selection.Cohort.Plan.Purpose.Should().Be(purpose);
        selection
            .Cohort.Scope.Items.Select(item => item.ProviderOrigin.Provider)
            .Should()
            .Equal("archive.org", "archive.org", "unknown");
    }

    [Test]
    public async Task SixHundredDistinctFieldsRetainTheirOriginalOrderAndSourceIdentities()
    {
        using var cohort = new CreditCohortBundle(600);
        await cohort.WriteAsync();

        var selection = await cohort.SelectAsync();

        selection.Items.Should().HaveCount(600);
        selection
            .Items.Select(item => item.SourceUuid)
            .Should()
            .Equal(cohort.ScopeItems.Select(item => item["source_uuid"]!.GetValue<Guid>()));
        selection
            .Items.Select(item => item.FieldSha256)
            .Should()
            .Equal(cohort.Fields.Select(item => item["field_sha256"]!.GetValue<string>()));
        selection.UniqueNonemptyRequestInputs.Should().Be(600);
    }
}

public sealed partial class TestLunaCreditFieldEvaluationBatch
{
    [TestCase("plan_bytes")]
    [TestCase("unknown_plan_member")]
    [TestCase("count_mismatch")]
    [TestCase("budget_above_profile")]
    [TestCase("attempts_above_profile")]
    [TestCase("labels_not_frozen")]
    [TestCase("too_many_fields")]
    [TestCase("partition_conflict")]
    [TestCase("evaluation_without_adjudication")]
    [TestCase("provider_origin")]
    [TestCase("null_scope_items")]
    [TestCase("null_provider_origin")]
    [TestCase("source_identity")]
    [TestCase("field_bytes")]
    [TestCase("unreceipted_field_file")]
    [TestCase("receipt_hash")]
    public async Task CohortPreflightRejectsConflictingFrozenEvidenceBeforeJobOrProviderWork(
        string mutation
    )
    {
        using var cohort = new CreditCohortBundle(mutation == "too_many_fields" ? 601 : 3);
        switch (mutation)
        {
            case "unknown_plan_member":
                cohort.Plan["allow_unreserved_fields"] = true;
                break;
            case "count_mismatch":
                cohort.Plan["field_count"] = 2;
                break;
            case "budget_above_profile":
                cohort.Plan["cap_usd"] = 30.01m;
                break;
            case "attempts_above_profile":
                cohort.Plan["maximum_total_attempts"] = 1201;
                break;
            case "labels_not_frozen":
                cohort.Receipt["labels_frozen_before_predictions"] = false;
                break;
            case "partition_conflict":
                cohort.Labels[0]["family_partition"] = "test";
                break;
            case "evaluation_without_adjudication":
                cohort.SetPurpose("untouched_evaluation", "test", "test");
                cohort.Labels[0]["answer"]!["adjudicator"] = null;
                break;
            case "provider_origin":
                cohort.ScopeItems[0]["provider_origin"]!["evidence_sha256"] = null;
                break;
            case "null_scope_items":
                cohort.Scope["items"] = null;
                break;
            case "null_provider_origin":
                cohort.ScopeItems[0]["provider_origin"] = null;
                break;
            case "source_identity":
                cohort.ScopeItems[0]["source_uuid"] = Guid.NewGuid();
                break;
            case "field_bytes":
                cohort.Fields[0]["field_text"] = "A different credited name";
                break;
            case "receipt_hash":
                cohort.Receipt["labels_sha256"] = new string('0', 64);
                break;
        }
        await cohort.WriteAsync(refreshReceiptHashes: mutation != "receipt_hash");
        var expectedHash = cohort.PlanHash;
        if (mutation == "plan_bytes")
        {
            await File.AppendAllTextAsync(cohort.PlanPath, "\n");
        }
        else if (mutation == "unreceipted_field_file")
        {
            await File.AppendAllTextAsync(cohort.Output("fields.jsonl"), "\n");
        }
        var job = Guid.NewGuid();
        var output = cohort.Output("rejected");

        await FluentActions
            .Awaiting(() => Batch().RunCohortAsync(cohort.PlanPath, expectedHash, output, job))
            .Should()
            .ThrowAsync<InvalidDataException>();

        handler.Calls.Should().Be(0);
        (await JobCount(job)).Should().Be(0);
        Directory.Exists(output).Should().BeFalse();
    }

    [Test]
    public async Task SixHundredRequestsAndTheirOwnLedgerSurviveKeylessCohortReplay()
    {
        using var cohort = new CreditCohortBundle(600);
        await cohort.WriteAsync();
        var job = Guid.NewGuid();
        var output = cohort.Output("first");

        var first = await Batch().RunCohortAsync(cohort.PlanPath, cohort.PlanHash, output, job);

        first.State.Should().Be("completed");
        first.Fields.Should().Be(600);
        first.PaidAttempts.Should().Be(600);
        first.KeylessReplayFromCache.Should().Be(600);
        first.Job.CapUsd.Should().Be(30m);
        first.Job.MaximumTotalAttempts.Should().Be(1200);
        handler.Calls.Should().Be(600);
        var audit = await ReadCohortAudit(output);
        audit["requests"]!.AsArray().Should().HaveCount(600);
        audit["attempts"]!.AsArray().Should().HaveCount(600);
        audit["attempts"]!
            .AsArray()
            .Should()
            .OnlyContain(attempt => attempt!["jobUuid"]!.GetValue<Guid>() == job);
        AssertOnlyOriginalFieldsEnterCohortRequests(audit, cohort);
        var receipt = await File.ReadAllBytesAsync(Path.Combine(output, "receipts.json"));

        var replay = await Batch(() => "")
            .RunCohortAsync(cohort.PlanPath, cohort.PlanHash, cohort.Output("keyless"), job);

        replay.State.Should().Be("completed");
        replay.LunaFromCache.Should().Be(600);
        replay.PaidAttempts.Should().Be(600);
        replay.Job.AccountedUsd.Should().Be(first.Job.AccountedUsd);
        handler.Calls.Should().Be(600);
        (await File.ReadAllBytesAsync(Path.Combine(output, "receipts.json")))
            .Should()
            .Equal(receipt);
    }

    [TestCase(3)]
    [TestCase(600)]
    public async Task ContactFieldsKeepLocalResultsAndScopeWhileOnlyOrdinaryFieldsReachLuna(
        int count
    )
    {
        using var cohort = new CreditCohortBundle(count);
        var contactText =
            "Rowan Vale <credit-contact-" + Guid.NewGuid().ToString("N") + "@example.test>";
        cohort.SetText(1, contactText);
        cohort.SetText(count - 1, "");
        await cohort.WriteAsync();
        var job = Guid.NewGuid();
        var output = cohort.Output("mixed-contact");

        var report = await Batch().RunCohortAsync(cohort.PlanPath, cohort.PlanHash, output, job);

        report.State.Should().Be("completed");
        report.Fields.Should().Be(count);
        report.States["not_requested_contact_scope"].Should().Be(1);
        handler.Calls.Should().Be(count - 2);
        var selectedIds = cohort
            .Labels.Select(label => label["label_id"]!.GetValue<string>())
            .ToArray();
        report.Selection.Items.Select(item => item.LabelId).Should().Equal(selectedIds);
        report
            .Selection.Cohort!.Scope.Items.Select(item => item.LabelId)
            .Should()
            .Equal(selectedIds);
        var coverage = report.CohortCoverage!;
        coverage.SelectedFields.Should().Be(count);
        coverage.LunaEligibleFields.Should().Be(count - 2);
        coverage.LunaRequestedFields.Should().Be(count - 2);
        coverage.LunaAvailableFields.Should().Be(count - 2);
        coverage.ContactExcludedFields.Should().Be(1);
        coverage.NoTextFields.Should().Be(1);
        coverage.ContactExcludedLabelIds.Should().Equal(selectedIds[1]);
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
        };
        var results = (await File.ReadAllLinesAsync(Path.Combine(output, "results.jsonl")))
            .Select(line => JsonSerializer.Deserialize<LunaCreditFieldResult>(line, json)!)
            .ToArray();
        results.Select(result => result.LabelId).Should().Equal(selectedIds);
        var excluded = results[1];
        excluded.State.Should().Be("not_requested_contact_scope");
        excluded.Luna.Should().BeNull();
        excluded.CacheReplay.Should().BeNull();
        excluded.CurrentRouting.State.Should().Be("not_evaluated");
        excluded.CurrentRouting.Reason.Should().Be("contact_scope");
        var local = new RecordingCreditExtractor().Extract(
            new Dictionary<string, string?> { ["taper"] = contactText }
        );
        local.Credits.Should().ContainSingle();
        excluded
            .Local.Should()
            .BeEquivalentTo(
                local,
                options => options.WithStrictOrdering().AllowingInfiniteRecursion()
            );
        excluded
            .CurrentRouting.Extraction.Should()
            .BeEquivalentTo(
                local,
                options => options.WithStrictOrdering().AllowingInfiniteRecursion()
            );
        var audit = await ReadCohortAudit(output);
        audit["requests"]!.AsArray().Should().HaveCount(count - 2);
        audit["attempts"]!.AsArray().Should().HaveCount(count - 2);
        AssertOnlyOriginalFieldsEnterCohortRequests(audit, cohort, excludedText: contactText);

        var replay = await Batch(() => "")
            .RunCohortAsync(cohort.PlanPath, cohort.PlanHash, cohort.Output("mixed-keyless"), job);

        replay.State.Should().Be("completed");
        replay
            .CohortCoverage.Should()
            .BeEquivalentTo(coverage, options => options.WithStrictOrdering());
        replay.Job.AccountedUsd.Should().Be(report.Job.AccountedUsd);
        handler.Calls.Should().Be(count - 2);
    }

    [Test]
    public async Task LastPermittedCohortAttemptPublishesBeforeFurtherCallsRefuseAndFullLedgerIsExported()
    {
        using var cohort = new CreditCohortBundle(600);
        await cohort.WriteAsync();
        var job = Guid.NewGuid();
        await cache.CreateJobAsync(job, 30m, maximumTotalAttempts: 1200);
        var historical = await cache.TryClaimAsync(
            job,
            "historical-cohort-" + job,
            .01m,
            1200,
            TimeSpan.FromMinutes(3)
        );
        await cache.FailAsync(
            historical.AttemptUuid!.Value,
            "invalid_response",
            receipt: new("", "historical-cohort", 0, 2),
            actualUsd: .000001m
        );
        // This existing fixture adds a declared synthetic spend history, not extraction output.
        // The final admitted attempt below still comes from the real runner and fake provider.
        await AddHistoricalFailures(job, historical.CacheKey, 1198);
        var output = cohort.Output("attempt-limit");

        var result = await Batch().RunCohortAsync(cohort.PlanPath, cohort.PlanHash, output, job);

        result.State.Should().Be("completed_partial");
        result.States["extracted"].Should().Be(1);
        result.States["job_attempts_exhausted"].Should().Be(599);
        result.PaidAttempts.Should().Be(1200);
        handler.Calls.Should().Be(1);
        var audit = await ReadCohortAudit(output);
        var attempts = audit["attempts"]!.AsArray();
        attempts.Should().HaveCount(1200);
        attempts.Should().OnlyContain(attempt => attempt!["jobUuid"]!.GetValue<Guid>() == job);
        attempts
            .Count(attempt => attempt!["state"]!.GetValue<string>() == "succeeded")
            .Should()
            .Be(1);
        attempts
            .Count(attempt => attempt!["state"]!.GetValue<string>() == "failed")
            .Should()
            .Be(1199);
        audit["requests"]!.AsArray().Should().HaveCount(600);
        (await cache.GetJobAsync(job)).AccountedUsd.Should().Be(result.Job.AccountedUsd);
    }

    [Test]
    public async Task CohortUsesItsLowerDeclaredBudgetBeforeAnyProviderAttempt()
    {
        using var cohort = new CreditCohortBundle(3);
        cohort.Plan["cap_usd"] = .000001m;
        await cohort.WriteAsync();
        var job = Guid.NewGuid();
        var output = cohort.Output("budget-limit");

        var result = await Batch().RunCohortAsync(cohort.PlanPath, cohort.PlanHash, output, job);

        result.State.Should().Be("completed_partial");
        result.States["budget_exhausted"].Should().Be(3);
        result.PaidAttempts.Should().Be(0);
        result.Job.CapUsd.Should().Be(.000001m);
        result.Job.AccountedUsd.Should().Be(0);
        handler.Calls.Should().Be(0);
        (await ReadCohortAudit(output))["attempts"]!.AsArray().Should().BeEmpty();
    }

    [Test]
    public async Task CohortCancellationRetainsChargedAttemptAndRetryUsesANewOutput()
    {
        using var cohort = new CreditCohortBundle(3);
        await cohort.WriteAsync();
        using var cancellation = new CancellationTokenSource();
        handler.CancelDuringResponse = cancellation;
        var job = Guid.NewGuid();
        var output = cohort.Output("interrupted");

        await FluentActions
            .Awaiting(() =>
                Batch()
                    .RunCohortAsync(
                        cohort.PlanPath,
                        cohort.PlanHash,
                        output,
                        job,
                        cancellation.Token
                    )
            )
            .Should()
            .ThrowAsync<OperationCanceledException>();

        File.Exists(Path.Combine(output, "manifest.json")).Should().BeFalse();
        var originalReceipt = await File.ReadAllBytesAsync(Path.Combine(output, "receipts.json"));
        var audit = await ReadCohortAudit(output);
        audit["attempts"]!.AsArray().Should().ContainSingle();
        (await cache.GetJobAsync(job)).AccountedUsd.Should().BeGreaterThan(0);
        handler.CancelDuringResponse = null;

        var recovered = await Batch()
            .RunCohortAsync(cohort.PlanPath, cohort.PlanHash, cohort.Output("recovered"), job);

        recovered.State.Should().Be("completed");
        recovered.Fields.Should().Be(3);
        recovered.PaidAttempts.Should().Be(4);
        (await File.ReadAllBytesAsync(Path.Combine(output, "receipts.json")))
            .Should()
            .Equal(originalReceipt);
    }

    [Test]
    public async Task CohortAndLegacyJobsCannotSilentlyChangeEachOthersBudgetProfile()
    {
        using var cohort = new CreditCohortBundle(3);
        await cohort.WriteAsync();
        var legacyJob = Guid.NewGuid();
        var legacy = await Run(legacyJob, bundle.Output("legacy-profile"));
        legacy.Fields.Should().Be(100);
        legacy.Job.CapUsd.Should().Be(5m);
        legacy.Job.MaximumTotalAttempts.Should().Be(200);
        var calls = handler.Calls;

        await FluentActions
            .Awaiting(() =>
                Batch()
                    .RunCohortAsync(
                        cohort.PlanPath,
                        cohort.PlanHash,
                        cohort.Output("legacy-conflict"),
                        legacyJob
                    )
            )
            .Should()
            .ThrowAsync<InvalidOperationException>();

        handler.Calls.Should().Be(calls);
        (await cache.GetJobAsync(legacyJob)).Should().Be(legacy.Job);
        var cohortJob = Guid.NewGuid();
        await cache.CreateJobAsync(cohortJob, 30m, maximumTotalAttempts: 1200);

        await FluentActions
            .Awaiting(() => Run(cohortJob, bundle.Output("cohort-conflict")))
            .Should()
            .ThrowAsync<InvalidOperationException>();

        handler.Calls.Should().Be(calls);
        (await cache.GetJobAsync(cohortJob)).CapUsd.Should().Be(30m);
        (await cache.GetJobAsync(cohortJob)).MaximumTotalAttempts.Should().Be(1200);
    }

    private static async Task<JsonNode> ReadCohortAudit(string output) =>
        JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(output, "receipts.json")))!;

    private static void AssertOnlyOriginalFieldsEnterCohortRequests(
        JsonNode audit,
        CreditCohortBundle cohort,
        string? excludedText = null
    )
    {
        var expected = cohort.Fields.ToDictionary(
            field => field["field_text"]!.GetValue<string>(),
            field => field["field_path"]!.GetValue<string>()["/raw_record/row/".Length..]
        );
        foreach (var request in audit["requests"]!.AsArray())
        {
            using var rendered = JsonDocument.Parse(
                request!["renderedRequest"]!.GetValue<string>()
            );
            using var input = JsonDocument.Parse(
                rendered.RootElement.GetProperty("input")[1].GetProperty("content").GetString()!
            );
            input
                .RootElement.EnumerateObject()
                .Select(value => value.Name)
                .Should()
                .BeEquivalentTo("fields", "context");
            input.RootElement.GetProperty("context").ValueKind.Should().Be(JsonValueKind.Null);
            var field = input
                .RootElement.GetProperty("fields")
                .EnumerateObject()
                .Should()
                .ContainSingle()
                .Which;
            expected.Should().ContainKey(field.Value.GetString()!);
            field.Name.Should().Be(expected[field.Value.GetString()!]);
            if (excludedText != null)
            {
                field.Value.GetString().Should().NotBe(excludedText);
            }
        }
    }
}

/// <summary>
/// Owns synthetic fields and provider-origin declarations. The hashes authenticate this fixture's
/// bytes; they do not represent an upstream capture or source-label adjudication.
/// </summary>
internal sealed class CreditCohortBundle : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(),
        "credit-cohort-" + Guid.NewGuid().ToString("N")
    );
    internal List<JsonObject> Labels { get; } = [];
    internal List<JsonObject> Fields { get; } = [];
    internal List<JsonObject> ScopeItems { get; } = [];
    internal JsonObject Plan { get; }
    internal JsonObject Receipt { get; }
    internal JsonObject Scope { get; }
    internal string PlanPath => Path.Combine(directory, "plan.json");
    internal string PlanHash { get; private set; } = "";

    internal CreditCohortBundle(
        int count,
        string purpose = "development",
        string artistPartition = "development",
        string familyPartition = "development"
    )
    {
        Directory.CreateDirectory(directory);
        var nonce = Guid.NewGuid().ToString("N");
        for (var index = 0; index < count; index++)
        {
            var source = Guid.NewGuid();
            var artist = Guid.NewGuid();
            const string field = "taper";
            var id = $"credit-field:{source:D}:{field}";
            var evidence = "source_sample:" + source.ToString("D");
            var text = "Jane " + nonce + " " + index;
            var hash = Hash(Encoding.UTF8.GetBytes(text));
            Labels.Add(
                new()
                {
                    ["label_id"] = id,
                    ["task_id"] = "extract:" + evidence,
                    ["evidence_id"] = evidence,
                    ["scope"] = "complete_single_field",
                    ["field_path"] = "/raw_record/row/" + field,
                    ["field_sha256"] = hash,
                    ["field_codepoints"] = text.EnumerateRunes().Count(),
                    ["resource"] = new JsonObject
                    {
                        ["provider"] = "relisten_catalog_snapshot",
                        ["resource_type"] = "source",
                        ["resource_uuid"] = source,
                    },
                    ["artist"] = new JsonObject { ["uuid"] = artist },
                    ["answer"] = new JsonObject
                    {
                        ["reason"] = "Private fixture answer must not enter the model request.",
                    },
                }
            );
            Fields.Add(
                new()
                {
                    ["label_id"] = id,
                    ["evidence_id"] = evidence,
                    ["field_path"] = "/raw_record/row/" + field,
                    ["field_text"] = text,
                    ["field_sha256"] = hash,
                }
            );
            ScopeItems.Add(
                new()
                {
                    ["label_id"] = id,
                    ["evidence_id"] = evidence,
                    ["source_uuid"] = source,
                    ["artist_uuid"] = artist,
                    ["field"] = field,
                    ["field_sha256"] = hash,
                    ["provider_origin"] = new JsonObject
                    {
                        ["provider"] = "archive.org",
                        ["kind"] = "retained_provider_input",
                        ["evidence_sha256"] = Hash(
                            Encoding.UTF8.GetBytes("synthetic evidence " + source)
                        ),
                        ["resource_key_sha256"] = Hash(
                            Encoding.UTF8.GetBytes("synthetic resource " + source)
                        ),
                    },
                }
            );
        }
        Scope = new()
        {
            ["schema_version"] = "credit-cohort-scope-v1",
            ["items"] = new JsonArray(ScopeItems.Cast<JsonNode?>().ToArray()),
        };
        Receipt = new()
        {
            ["schema_version"] = "credit-field-cohort-v1",
            ["status"] = "frozen_cohort",
            ["fields"] = count,
            ["labels_frozen_before_predictions"] = true,
        };
        Plan = new()
        {
            ["schema_version"] = "luna-credit-cohort-job-v1",
            ["field_count"] = count,
            ["cap_usd"] = 30m,
            ["maximum_total_attempts"] = 1200,
            ["labels"] = FileReference("labels.jsonl"),
            ["fields"] = FileReference("fields.jsonl"),
            ["receipt"] = FileReference("receipt.json"),
            ["scope"] = FileReference("scope.json"),
        };
        SetPurpose(purpose, artistPartition, familyPartition);
    }

    internal void SetPurpose(string purpose, string artistPartition, string familyPartition)
    {
        Plan["purpose"] = purpose;
        Plan["artist_partition"] = artistPartition;
        Plan["family_partition"] = familyPartition;
        Receipt["purpose"] = purpose;
        foreach (var label in Labels)
        {
            label["status"] = purpose == "development" ? "draft_development" : "adjudicated";
            label["artist_partition"] = artistPartition;
            label["family_partition"] = familyPartition;
            label["answer"]!["adjudicator"] =
                purpose == "development" ? null : "synthetic-reviewer";
        }
        foreach (var item in ScopeItems)
        {
            item["artist_partition"] = artistPartition;
            item["family_partition"] = familyPartition;
        }
    }

    internal void SetText(int index, string text)
    {
        var hash = Hash(Encoding.UTF8.GetBytes(text));
        Fields[index]["field_text"] = text;
        Fields[index]["field_sha256"] = hash;
        Labels[index]["field_sha256"] = hash;
        Labels[index]["field_codepoints"] = text.EnumerateRunes().Count();
        ScopeItems[index]["field_sha256"] = hash;
    }

    internal async Task WriteAsync(bool refreshReceiptHashes = true)
    {
        // Freeze dependencies before their receipt and plan, so later mutations can distinguish
        // changed bytes from contradictory evidence inside an otherwise hash-valid packet.
        await File.WriteAllTextAsync(
            Path.Combine(directory, "labels.jsonl"),
            string.Join("\n", Labels.Select(label => label.ToJsonString())) + "\n"
        );
        await File.WriteAllTextAsync(
            Path.Combine(directory, "fields.jsonl"),
            string.Join("\n", Fields.Select(field => field.ToJsonString())) + "\n"
        );
        await File.WriteAllTextAsync(Path.Combine(directory, "scope.json"), Scope.ToJsonString());
        foreach (var name in new[] { "labels", "fields", "scope" })
        {
            Plan[name]!["sha256"] = Hash(
                await File.ReadAllBytesAsync(
                    Path.Combine(directory, Plan[name]!["path"]!.GetValue<string>())
                )
            );
        }
        Receipt["raw_fields_sha256"] = Plan["fields"]!["sha256"]!.GetValue<string>();
        Receipt["scope_sha256"] = Plan["scope"]!["sha256"]!.GetValue<string>();
        if (refreshReceiptHashes)
        {
            Receipt["labels_sha256"] = Plan["labels"]!["sha256"]!.GetValue<string>();
        }
        await File.WriteAllTextAsync(
            Path.Combine(directory, "receipt.json"),
            Receipt.ToJsonString()
        );
        Plan["receipt"]!["sha256"] = Hash(
            await File.ReadAllBytesAsync(Path.Combine(directory, "receipt.json"))
        );
        await File.WriteAllTextAsync(PlanPath, Plan.ToJsonString());
        PlanHash = Hash(await File.ReadAllBytesAsync(PlanPath));
    }

    internal Task<LunaCreditFieldSelection> SelectAsync() =>
        LunaCreditFieldEvaluationBatch.ReadCohortSelectionAsync(PlanPath, PlanHash);

    internal string Output(string name) => Path.Combine(directory, name);

    private static JsonObject FileReference(string path) =>
        new() { ["path"] = path, ["sha256"] = "" };

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public void Dispose() => Directory.Delete(directory, true);
}
