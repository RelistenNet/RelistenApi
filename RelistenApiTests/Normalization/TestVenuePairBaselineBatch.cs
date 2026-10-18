using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Relisten.Normalization;
using Relisten.Normalization.Batch;

namespace RelistenApiTests.Normalization;

/// <summary>Synthetic bundles isolate the actual matcher's input projection and durable output boundary.</summary>
[TestFixture]
public sealed class TestVenuePairBaselineBatch
{
    private string directory = null!;
    private string bundle = null!;
    private string output = null!;

    [SetUp]
    public void Start()
    {
        directory = Path.Combine(
            Path.GetTempPath(),
            "venue-pair-baseline-" + Guid.NewGuid().ToString("N")
        );
        bundle = Path.Combine(directory, "bundle");
        output = Path.Combine(directory, "output");
        Directory.CreateDirectory(bundle);
    }

    [TearDown]
    public void Stop() => Directory.Delete(directory, true);

    [Test]
    public async Task ActualMatcherKeepsRawGeographyKindAndAuthorityUnknown()
    {
        await Bundle(
            [TaskRow("one", "left", "right")],
            [Evidence("left", "The Hall", "London"), Evidence("right", "The Hall", "Paris")]
        );
        var report = await new VenuePairBaselineBatch().RunAsync(bundle, output);
        report.State.Should().Be("completed");
        report.Pairs.Should().Be(1);
        report.OutputSha256.Should().Be(await HashFile(Path.Combine(output, report.OutputFile)));
        report
            .MatcherAssemblySha256.Should()
            .Be(await HashFile(typeof(IdentityCandidateMatcher).Assembly.Location));
        report
            .RunnerAssemblySha256.Should()
            .Be(await HashFile(typeof(VenuePairBaselineBatch).Assembly.Location));
        report
            .BundleManifestSha256.Should()
            .Be(await HashFile(Path.Combine(bundle, "manifest.json")));
        foreach (var input in report.InputFiles)
        {
            input.Value.Sha256.Should().Be(await HashFile(Path.Combine(bundle, input.Key)));
        }
        using var row = JsonDocument.Parse(
            File.ReadLines(Path.Combine(output, report.OutputFile)).Single()
        );
        row.RootElement.GetProperty("task_id").GetString().Should().Be("one");
        var comparison = row.RootElement.GetProperty("comparison");
        comparison.GetProperty("disposition").GetString().Should().Be("needs_evidence");
        comparison.GetProperty("isCandidate").GetBoolean().Should().BeTrue();
        comparison.GetProperty("supportedRules").GetArrayLength().Should().Be(0);
        comparison.GetProperty("policyVersion").ValueKind.Should().Be(JsonValueKind.Null);
        comparison.GetProperty("candidateUuid").ValueKind.Should().Be(JsonValueKind.Null);
        var features = comparison.GetProperty("features").EnumerateArray().ToArray();
        features
            .Single(f => f.GetProperty("name").GetString() == "name")
            .GetProperty("agreement")
            .GetString()
            .Should()
            .Be("agrees");
        foreach (
            var unknown in new[]
            {
                "country",
                "locality",
                "address",
                "venue_kind",
                "parent_site",
                "external_identifier",
            }
        )
        {
            features
                .Single(f => f.GetProperty("name").GetString() == unknown)
                .GetProperty("agreement")
                .GetString()
                .Should()
                .Be("missing");
        }
        File.Exists(Path.Combine(output, "manifest.json")).Should().BeTrue();
        File.Exists(Path.Combine(output, "baseline.partial.jsonl")).Should().BeFalse();
    }

    [Test]
    public async Task DecisionsRationalesAndSplitHintsCannotChangeMatcherResults()
    {
        var task = JsonNode.Parse(TaskRow("one", "left", "right"))!;
        task["answer"] = new JsonObject
        {
            ["decision"] = "same_entity",
            ["reason"] = "Official address agrees",
        };
        task["features"] = new JsonObject
        {
            ["country"] = "US",
            ["kind"] = "room",
            ["name_similarity"] = 1,
        };
        await Bundle(
            [task.ToJsonString()],
            [Evidence("left", "Alpha", "Same City"), Evidence("right", "Beta", "Same City")]
        );
        await new VenuePairBaselineBatch().RunAsync(bundle, output);
        var first = Comparison(output);
        task["answer"] = new JsonObject
        {
            ["decision"] = "different_entities",
            ["reason"] = "Different authoritative countries",
        };
        task["features"] = new JsonObject
        {
            ["country"] = "GB",
            ["kind"] = "site",
            ["name_similarity"] = 0,
        };
        await Bundle(
            [task.ToJsonString()],
            [Evidence("left", "Alpha", "Same City"), Evidence("right", "Beta", "Same City")],
            "{\"matcher_feature\":true,\"claim\":\"do not consume this link\"}"
        );
        var otherOutput = Path.Combine(directory, "other-output");
        await new VenuePairBaselineBatch().RunAsync(bundle, otherOutput);
        Comparison(otherOutput).Should().Be(first);
        using var comparison = JsonDocument.Parse(first);
        comparison.RootElement.GetProperty("disposition").GetString().Should().Be("not_candidate");
    }

    [Test]
    public async Task UnicodeOriginalNameIsCheckedInCodepointsWithoutAsciiFolding()
    {
        await Bundle(
            [TaskRow("unicode", "left", "right")],
            [Evidence("left", "😀 Théâtre\u2028Hall"), Evidence("right", "😀 Théâtre\u2028Hall")]
        );
        await new VenuePairBaselineBatch().RunAsync(bundle, output);
        using var comparison = JsonDocument.Parse(Comparison(output));
        comparison.RootElement.GetProperty("nameTokenSimilarity").GetDouble().Should().Be(1);
    }

    [Test]
    public async Task ManifestMismatchPreventsAnyCompletedOutput()
    {
        await Bundle(
            [TaskRow("one", "left", "right")],
            [Evidence("left", "Hall"), Evidence("right", "Hall")]
        );
        await File.AppendAllTextAsync(Path.Combine(bundle, "evidence.jsonl"), " ");
        var run = async () => await new VenuePairBaselineBatch().RunAsync(bundle, output);
        await run.Should().ThrowAsync<InvalidDataException>();
        File.Exists(Path.Combine(output, "manifest.json")).Should().BeFalse();
    }

    [Test]
    public async Task InvalidNameCitationCannotBeProjectedEvenWithAnUpdatedManifest()
    {
        var evidence = JsonNode.Parse(Evidence("left", "😀 Hall"))!;
        evidence["fields"]![0]!["span"]!["end"] = "😀 Hall".Length; // UTF-16 length is not a Unicode codepoint count.
        await Bundle(
            [TaskRow("one", "left", "right")],
            [evidence.ToJsonString(), Evidence("right", "Hall")]
        );
        var run = async () => await new VenuePairBaselineBatch().RunAsync(bundle, output);
        await run.Should().ThrowAsync<InvalidDataException>();
        File.Exists(Path.Combine(output, "manifest.json")).Should().BeFalse();
    }

    [Test]
    public async Task MissingLaterEvidenceRetainsPrefixWithoutACompletionMarker()
    {
        await Bundle(
            [TaskRow("one", "left", "right"), TaskRow("two", "left", "missing")],
            [Evidence("left", "Hall"), Evidence("right", "Hall")]
        );
        var run = async () => await new VenuePairBaselineBatch().RunAsync(bundle, output);
        await run.Should().ThrowAsync<InvalidDataException>();
        File.ReadLines(Path.Combine(output, "baseline.partial.jsonl")).Should().ContainSingle();
        File.Exists(Path.Combine(output, "manifest.json")).Should().BeFalse();
        File.Exists(Path.Combine(output, "baseline.jsonl")).Should().BeFalse();
    }

    [Test]
    public async Task DuplicateTasksCannotCreateAnApparentlyCompleteComparisonSet()
    {
        await Bundle(
            [TaskRow("one", "left", "right"), TaskRow("one", "left", "right")],
            [Evidence("left", "Hall"), Evidence("right", "Hall")]
        );
        var run = async () => await new VenuePairBaselineBatch().RunAsync(bundle, output);
        await run.Should().ThrowAsync<InvalidDataException>();
        File.Exists(Path.Combine(output, "manifest.json")).Should().BeFalse();
    }

    [Test]
    public async Task NondevelopmentEvidenceIsRejectedBeforeComparison()
    {
        var heldout = JsonNode.Parse(Evidence("right", "Hall"))!;
        heldout["artist_partition"] = "validation";
        await Bundle(
            [TaskRow("one", "left", "right")],
            [Evidence("left", "Hall"), heldout.ToJsonString()]
        );
        var run = async () => await new VenuePairBaselineBatch().RunAsync(bundle, output);
        await run.Should().ThrowAsync<InvalidDataException>();
        File.Exists(Path.Combine(output, "manifest.json")).Should().BeFalse();
    }

    [Test]
    public async Task ExistingOutputIsNotReusedOrOverwritten()
    {
        await Bundle(
            [TaskRow("one", "left", "right")],
            [Evidence("left", "Hall"), Evidence("right", "Hall")]
        );
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output, "keep"), "original");
        var run = async () => await new VenuePairBaselineBatch().RunAsync(bundle, output);
        await run.Should().ThrowAsync<IOException>();
        (await File.ReadAllTextAsync(Path.Combine(output, "keep"))).Should().Be("original");
        Directory.GetFiles(output).Should().ContainSingle();
    }

    [Test]
    public async Task CancelledRequestDoesNotCreateACompletionMarker()
    {
        await Bundle(
            [TaskRow("one", "left", "right")],
            [Evidence("left", "Hall"), Evidence("right", "Hall")]
        );
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var run = async () =>
            await new VenuePairBaselineBatch().RunAsync(bundle, output, cancellation.Token);
        await run.Should().ThrowAsync<OperationCanceledException>();
        Directory.Exists(output).Should().BeFalse();
    }

    [TestCase("manifest")]
    [TestCase("evidence")]
    [TestCase("task")]
    public Task MissingRequiredPropertyIsAControlledBundleFailure(string section) =>
        MalformedShape(section, false);

    [TestCase("manifest")]
    [TestCase("evidence")]
    [TestCase("task")]
    public Task WrongValueTypeIsAControlledBundleFailure(string section) =>
        MalformedShape(section, true);

    private async Task MalformedShape(string section, bool wrongType)
    {
        var task = JsonNode.Parse(TaskRow("one", "left", "right"))!;
        var evidence = JsonNode.Parse(Evidence("left", "Hall"))!;
        if (section != "manifest")
        {
            var document = section == "task" ? task : evidence;
            var property = section == "task" ? "evidence_ids" : "fields";
            if (wrongType)
            {
                document[property] = 42;
            }
            else
            {
                document.AsObject().Remove(property);
            }
        }
        // Rebuild hashes after changing task/evidence, so the projection guard is exercised
        // rather than an earlier manifest mismatch.
        await Bundle([task.ToJsonString()], [evidence.ToJsonString(), Evidence("right", "Hall")]);
        if (section == "manifest")
        {
            var path = Path.Combine(bundle, "manifest.json");
            var manifest = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
            if (wrongType)
            {
                manifest["files"] = 42;
            }
            else
            {
                manifest.AsObject().Remove("files");
            }
            await File.WriteAllTextAsync(path, manifest.ToJsonString());
        }
        var run = async () => await new VenuePairBaselineBatch().RunAsync(bundle, output);
        var failure = await run.Should().ThrowAsync<InvalidDataException>();
        failure.Which.InnerException.Should().NotBeNull();
        File.Exists(Path.Combine(output, "manifest.json")).Should().BeFalse();
        File.Exists(Path.Combine(output, "baseline.jsonl")).Should().BeFalse();
    }

    [Test]
    public async Task OversizedFileIsRejectedBeforeItCanBeLoadedAsAnUnboundedInput()
    {
        await Bundle([], []);
        await using (var stream = File.OpenWrite(Path.Combine(bundle, "evidence.jsonl")))
        {
            stream.SetLength(16 * 1024 * 1024 + 1);
        }
        var run = async () => await new VenuePairBaselineBatch().RunAsync(bundle, output);
        await run.Should().ThrowAsync<InvalidDataException>();
        File.Exists(Path.Combine(output, "manifest.json")).Should().BeFalse();
    }

    private static string Comparison(string path)
    {
        using var row = JsonDocument.Parse(
            File.ReadLines(Path.Combine(path, "baseline.jsonl")).Single()
        );
        return row.RootElement.GetProperty("comparison").GetRawText();
    }

    private async Task Bundle(string[] tasks, string[] evidence, string links = "")
    {
        var files = new Dictionary<string, string>
        {
            ["labels.jsonl"] = string.Join("\n", tasks) + (tasks.Length == 0 ? "" : "\n"),
            ["evidence.jsonl"] = string.Join("\n", evidence) + (evidence.Length == 0 ? "" : "\n"),
            ["split-links.jsonl"] = links + (links.Length == 0 ? "" : "\n"),
        };
        var entries = new Dictionary<string, object>();
        foreach (var (name, value) in files)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            await File.WriteAllBytesAsync(Path.Combine(bundle, name), bytes);
            entries[name] = new
            {
                sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
                bytes = bytes.Length,
                rows = name == "labels.jsonl" ? tasks.Length
                : name == "evidence.jsonl" ? evidence.Length
                : links.Length == 0 ? 0
                : 1,
            };
        }
        await File.WriteAllTextAsync(
            Path.Combine(bundle, "manifest.json"),
            JsonSerializer.Serialize(
                new { schema_version = "venue-development-pair-v1", files = entries }
            )
        );
    }

    private static string TaskRow(string id, string left, string right) =>
        JsonSerializer.Serialize(
            new
            {
                task_id = id,
                evidence_ids = new[] { left, right },
                domain = "venues",
                task_kind = "entity_link",
                family_partition = "development",
            }
        );

    private static string Evidence(string id, string name, string location = "Raw City")
    {
        object Field(string field, string text) =>
            new
            {
                path = "/raw_record/row/" + field,
                text,
                text_sha256 = Convert.ToHexStringLower(
                    SHA256.HashData(Encoding.UTF8.GetBytes(text))
                ),
                span = new
                {
                    start = 0,
                    end = text.EnumerateRunes().Count(),
                    quote = text,
                    unit = "unicode_code_points",
                },
            };
        return JsonSerializer.Serialize(
            new
            {
                evidence_id = id,
                artist_partition = "development",
                family_partition = "development",
                resource = new { resource_type = "venue", resource_uuid = Guid.NewGuid() },
                fields = new[] { Field("name", name), Field("location", location) },
                coordinates = new { latitude = 40, longitude = -70 },
                kind = "room",
                event_date = "2026-01-01",
            }
        );
    }

    private static async Task<string> HashFile(string path) =>
        Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path)));
}
