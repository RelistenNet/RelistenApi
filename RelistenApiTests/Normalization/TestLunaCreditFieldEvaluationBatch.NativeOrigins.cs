using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Relisten.Normalization.Batch;

namespace RelistenApiTests.Normalization;

/// <summary>Native provider text remains distinct from its catalog source link.</summary>
[TestFixture]
public sealed class TestLunaCreditNativeFieldSelection
{
    [TestCase("archive.org")]
    [TestCase("local")]
    [TestCase("phish.in")]
    public async Task RetainedProviderFieldsKeepTheirOwnTextAndCatalogSourceLink(string provider)
    {
        using var cohort = new CreditCohortBundle(3);
        var catalogText = cohort.Fields[0]["field_text"]!.GetValue<string>();
        const string nativeText = "María Native 🌿";
        cohort.SetText(0, nativeText);
        cohort.Labels[0]["resource"]!["provider"] = "relisten_retained_provider_field";
        cohort.ScopeItems[0]["provider_origin"]!["provider"] = provider;
        await cohort.WriteAsync();

        var selection = await cohort.SelectAsync();

        selection.Items[0].Text.Should().Be(nativeText).And.NotBe(catalogText);
        selection
            .Items[0]
            .SourceUuid.Should()
            .Be(cohort.ScopeItems[0]["source_uuid"]!.GetValue<Guid>());
        selection
            .Items[0]
            .FieldSha256.Should()
            .Be(cohort.ScopeItems[0]["field_sha256"]!.GetValue<string>());
        selection.Cohort!.Scope.Items[0].ProviderOrigin.Provider.Should().Be(provider);
    }

    [TestCase("unknown")]
    [TestCase("import_execution")]
    [TestCase("missing_evidence")]
    [TestCase("missing_resource")]
    [TestCase("unsupported_provider")]
    [TestCase("unsupported_namespace")]
    public async Task NativeFieldsRequireKnownRetainedInputOrigin(string defect)
    {
        using var cohort = new CreditCohortBundle(3);
        cohort.Labels[0]["resource"]!["provider"] = "relisten_retained_provider_field";
        var origin = cohort.ScopeItems[0]["provider_origin"]!;
        switch (defect)
        {
            case "unknown":
                origin["provider"] = "unknown";
                origin["kind"] = "unknown";
                origin["evidence_sha256"] = null;
                origin["resource_key_sha256"] = null;
                break;
            case "import_execution":
                origin["kind"] = "import_execution";
                break;
            case "missing_evidence":
                origin["evidence_sha256"] = null;
                break;
            case "missing_resource":
                origin["resource_key_sha256"] = null;
                break;
            case "unsupported_provider":
                origin["provider"] = "unsupported.example";
                break;
            case "unsupported_namespace":
                cohort.Labels[0]["resource"]!["provider"] = "unsupported_namespace";
                break;
        }
        await cohort.WriteAsync();

        await FluentActions
            .Awaiting(() => cohort.SelectAsync())
            .Should()
            .ThrowAsync<InvalidDataException>();
    }

    [Test]
    public async Task LegacyReaderStillRefusesTheNativeProviderNamespace()
    {
        using var legacy = new CreditFieldBundle();
        legacy.Labels[0]["resource"]!["provider"] = "relisten_retained_provider_field";
        await legacy.WriteAsync();

        await FluentActions
            .Awaiting(() => legacy.SelectAsync())
            .Should()
            .ThrowAsync<InvalidDataException>();
    }
}

public sealed partial class TestLunaCreditFieldEvaluationBatch
{
    [Test]
    public async Task NativeOriginalTextAloneReachesTheProviderAndKeylessReplay()
    {
        using var cohort = new CreditCohortBundle(3);
        var catalogText = cohort.Fields[0]["field_text"]!.GetValue<string>();
        const string nativeText = "María Native 🌿";
        cohort.SetText(0, nativeText);
        cohort.Labels[0]["resource"]!["provider"] = "relisten_retained_provider_field";
        cohort.ScopeItems[0]["provider_origin"]!["provider"] = "local";
        await cohort.WriteAsync();
        var output = cohort.Output("native-original");

        var report = await Batch()
            .RunCohortAsync(cohort.PlanPath, cohort.PlanHash, output, Guid.NewGuid());

        report.State.Should().Be("completed");
        report.Fields.Should().Be(3);
        report.KeylessReplayFromCache.Should().Be(3);
        handler.Calls.Should().Be(3);
        var audit = await ReadCohortAudit(output);
        audit["requests"]!.AsArray().Should().HaveCount(3);
        AssertOnlyOriginalFieldsEnterCohortRequests(audit, cohort, excludedText: catalogText);
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
        };
        var results = File.ReadLines(Path.Combine(output, "results.jsonl"))
            .Select(line => JsonSerializer.Deserialize<LunaCreditFieldResult>(line, json)!)
            .ToArray();
        results[0].Luna!.Extraction.Credits.Single().Name.Should().Be(nativeText);
        results[0]
            .CacheReplay!.Extraction.Should()
            .BeEquivalentTo(results[0].Luna!.Extraction, options => options.WithStrictOrdering());
    }
}
