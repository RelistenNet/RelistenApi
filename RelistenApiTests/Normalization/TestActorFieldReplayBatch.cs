using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Relisten.Normalization.Batch;

namespace RelistenApiTests.Normalization;

[TestFixture]
public sealed class TestActorFieldReplayBatch
{
    private ActorFieldFixture fixture = null!;

    [SetUp]
    public void Start() => fixture = new();

    [TearDown]
    public void Stop() => fixture.Dispose();

    [Test]
    public async Task Public_extractor_replays_all_whole_fields_and_keeps_unicode_html_attachment()
    {
        fixture.SetField(0, "taper", "Renée 🌿 <b>River</b> (river@example.test)\u2028");
        await fixture.WriteAsync();
        var report = await fixture.RunAsync();
        report.State.Should().Be("completed");
        report.ProcessedFields.Should().Be(543);
        report.ErrorFields.Should().Be(0);
        File.Exists(Path.Combine(fixture.Output, "manifest.json")).Should().BeTrue();
        var rows = await File.ReadAllLinesAsync(Path.Combine(fixture.Output, "results.jsonl"));
        rows.Should().HaveCount(543);
        using var first = JsonDocument.Parse(rows[0]);
        var credits = first.RootElement.GetProperty("extraction").GetProperty("credits");
        credits.EnumerateArray().Should().ContainSingle();
        var credit = credits[0];
        credit.GetProperty("name").GetString().Should().Be("Renée 🌿 River");
        var actor = credit.GetProperty("actorFacts");
        actor.GetProperty("contacts").EnumerateArray().Should().ContainSingle();
        actor
            .GetProperty("contacts")[0]
            .GetProperty("literal")
            .GetString()
            .Should()
            .Be("river@example.test");
        var quote = actor.GetProperty("anchor").GetProperty("quote").GetString();
        quote.Should().Contain("<b>");
        report.ApiAssembly.File.Sha256.Should().HaveLength(64);
        report
            .Results!.Sha256.Should()
            .Be(
                ActorFieldFixture.Hash(
                    await File.ReadAllBytesAsync(Path.Combine(fixture.Output, "results.jsonl"))
                )
            );
    }

    [TestCase("missing")]
    [TestCase("duplicate")]
    [TestCase("text")]
    [TestCase("field")]
    [TestCase("heldout")]
    public async Task Preflight_rejects_malformed_population_even_when_outer_receipts_are_refreshed(
        string mutation
    )
    {
        if (mutation == "missing")
        {
            fixture.Fields.RemoveAt(542);
        }
        else if (mutation == "duplicate")
        {
            fixture.Fields[1]["task_id"] = fixture.Fields[0]["task_id"]!.GetValue<string>();
        }
        else if (mutation == "text")
        {
            fixture.Fields[0]["field_text"] = "Different source text";
        }
        else if (mutation == "field")
        {
            fixture.Fields[0]["field"] = "description";
        }
        else
        {
            fixture.Index[0]["family_partition"] = "test";
        }
        await fixture.WriteAsync();
        await FluentActions
            .Awaiting(() => fixture.RunAsync())
            .Should()
            .ThrowAsync<InvalidDataException>();
        Directory.Exists(fixture.Output).Should().BeFalse();
    }

    [TestCase("receipt")]
    [TestCase("field-index")]
    [TestCase("integer")]
    public async Task Malformed_metadata_shape_is_a_contextual_preflight_error(string mutation)
    {
        if (mutation == "field-index")
        {
            fixture.Index[0].Remove("code_points");
        }
        await fixture.WriteAsync();
        if (mutation is "receipt" or "integer")
        {
            var receipt = JsonNode.Parse(await File.ReadAllTextAsync(fixture.Receipt))!.AsObject();
            if (mutation == "receipt")
            {
                receipt.Remove("fields_file");
            }
            else
            {
                receipt["task_count"] = JsonNode.Parse("999999999999999999999999");
            }
            await File.WriteAllTextAsync(fixture.Receipt, receipt.ToJsonString());
        }
        await FluentActions
            .Awaiting(() => fixture.RunAsync())
            .Should()
            .ThrowAsync<InvalidDataException>()
            .WithMessage("Malformed actor-field replay input metadata:*");
        Directory.Exists(fixture.Output).Should().BeFalse();
    }

    [Test]
    public async Task Existing_output_is_preserved_without_overwriting_any_file()
    {
        await fixture.WriteAsync();
        Directory.CreateDirectory(fixture.Output);
        var sentinel = Path.Combine(fixture.Output, "results.jsonl");
        await File.WriteAllTextAsync(sentinel, "original results");
        await FluentActions.Awaiting(() => fixture.RunAsync()).Should().ThrowAsync<IOException>();
        (await File.ReadAllTextAsync(sentinel)).Should().Be("original results");
        Directory.GetFiles(fixture.Output).Should().HaveCount(1);
    }

    [Test]
    public async Task Competing_runs_cannot_publish_or_replace_each_others_status()
    {
        await fixture.WriteAsync();
        async Task<bool> TryRun()
        {
            try
            {
                return (await fixture.RunAsync()).State == "completed";
            }
            catch (IOException)
            {
                return false;
            }
        }
        var outcomes = await Task.WhenAll(TryRun(), TryRun());
        outcomes.Count(completed => completed).Should().Be(1);
        using var status = JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(fixture.Output, "status.json"))
        );
        status.RootElement.GetProperty("state").GetString().Should().Be("completed");
        status.RootElement.GetProperty("processedFields").GetInt32().Should().Be(543);
        File.Exists(Path.Combine(fixture.Output, "manifest.json")).Should().BeTrue();
    }

    [Test]
    public async Task Cancellation_retains_partial_results_without_manifest_and_recovers_in_fresh_directory()
    {
        await fixture.WriteAsync();
        using var cancellation = new CancellationTokenSource();
        var progress = new SynchronousProgress(_ => cancellation.Cancel());
        await FluentActions
            .Awaiting(() => fixture.RunAsync(cancellation.Token, progress))
            .Should()
            .ThrowAsync<OperationCanceledException>();
        File.Exists(Path.Combine(fixture.Output, "manifest.json")).Should().BeFalse();
        var partial = await File.ReadAllLinesAsync(Path.Combine(fixture.Output, "results.jsonl"));
        partial.Should().HaveCount(1);
        using var status = JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(fixture.Output, "status.json"))
        );
        status.RootElement.GetProperty("state").GetString().Should().Be("cancelled");
        var original = await File.ReadAllBytesAsync(Path.Combine(fixture.Output, "results.jsonl"));
        var recovery = await new ActorFieldReplayBatch().RunAsync(
            fixture.Input,
            fixture.Receipt,
            fixture.Bundle,
            fixture.Output + "-recovery"
        );
        recovery.State.Should().Be("completed");
        (await File.ReadAllBytesAsync(Path.Combine(fixture.Output, "results.jsonl")))
            .Should()
            .Equal(original);
    }

    [Test]
    public async Task Command_failure_after_a_field_write_has_failed_status_and_no_success_manifest()
    {
        await fixture.WriteAsync();
        var progress = new SynchronousProgress(_ =>
            throw new IOException("Interrupted progress consumer")
        );
        await FluentActions
            .Awaiting(() => fixture.RunAsync(progress: progress))
            .Should()
            .ThrowAsync<IOException>();
        File.Exists(Path.Combine(fixture.Output, "manifest.json")).Should().BeFalse();
        using var status = JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(fixture.Output, "status.json"))
        );
        status.RootElement.GetProperty("state").GetString().Should().Be("failed");
        status.RootElement.GetProperty("processedFields").GetInt32().Should().Be(1);
    }

    private sealed class SynchronousProgress(Action<int> callback) : IProgress<int>
    {
        public void Report(int value) => callback(value);
    }

    private sealed class ActorFieldFixture : IDisposable
    {
        private readonly string root = Path.Combine(
            Path.GetTempPath(),
            "actor-field-test-" + Guid.NewGuid()
        );
        public string Input => Path.Combine(root, "fields.jsonl");
        public string Receipt => Path.Combine(root, "receipt.json");
        public string Bundle => Path.Combine(root, "bundle");
        public string Output => Path.Combine(root, "output");
        public List<JsonObject> Fields { get; } = [];
        public List<JsonObject> Index { get; } = [];

        public ActorFieldFixture()
        {
            Directory.CreateDirectory(Bundle);
            for (var i = 0; i < 543; i++)
            {
                Fields.Add(
                    new JsonObject { ["task_id"] = $"task:{i}", ["evidence_id"] = $"source:{i}" }
                );
                Index.Add(
                    new JsonObject
                    {
                        ["task_id"] = $"task:{i}",
                        ["evidence_id"] = $"source:{i}",
                        ["artist_partition"] = "development",
                        ["family_partition"] = "development",
                    }
                );
                SetField(i, "taper", "Test contributor " + i);
            }
        }

        public void SetField(int index, string field, string text)
        {
            Fields[index]["field"] = field;
            Fields[index]["field_path"] = "/raw_record/row/" + field;
            Fields[index]["field_text"] = text;
            Fields[index]["field_sha256"] = Hash(Encoding.UTF8.GetBytes(text));
            Index[index]["field"] = field;
            Index[index]["field_path"] = "/raw_record/row/" + field;
            Index[index]["field_sha256"] = Fields[index]["field_sha256"]!.GetValue<string>();
            Index[index]["code_points"] = text.EnumerateRunes().Count();
        }

        public async Task WriteAsync()
        {
            var fields = Encoding.UTF8.GetBytes(
                string.Join("\n", Fields.Select(row => row.ToJsonString())) + "\n"
            );
            var index = Encoding.UTF8.GetBytes(
                string.Join("\n", Index.Select(row => row.ToJsonString())) + "\n"
            );
            await File.WriteAllBytesAsync(Input, fields);
            await File.WriteAllBytesAsync(Path.Combine(Bundle, "field-index.jsonl"), index);
            var manifest = JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    schema_version = "actor-facts-development-v1",
                    usage = "development_only",
                    evaluation_eligible = false,
                    source_scope = new { whole_fields = 543 },
                    source_annotation_evidence_sha256 = "annotation",
                    original_sources = new { sha256 = "originals" },
                    reservation_index = new { sha256 = "reservations" },
                    files = new Dictionary<string, object>
                    {
                        ["field-index.jsonl"] = ReceiptFor(index),
                        ["labels.jsonl"] = new { sha256 = "labels" },
                        ["citations.jsonl"] = new { sha256 = "citations" },
                    },
                }
            );
            await File.WriteAllBytesAsync(Path.Combine(Bundle, "manifest.json"), manifest);
            var receipt = new
            {
                schema_version = "actor-field-replay-input-v1",
                usage = "development_only",
                state = "completed",
                task_count = 543,
                fields_file = new
                {
                    name = "fields.jsonl",
                    bytes = fields.Length,
                    sha256 = Hash(fields),
                },
                frozen_bundle = new
                {
                    manifest = ReceiptFor(manifest),
                    field_index = ReceiptFor(index),
                },
                source_provenance = new
                {
                    annotation_evidence_sha256 = "annotation",
                    original_sources_sha256 = "originals",
                    reservation_index_sha256 = "reservations",
                    labels_sha256 = "labels",
                    citations_sha256 = "citations",
                },
            };
            await File.WriteAllTextAsync(Receipt, JsonSerializer.Serialize(receipt));
        }

        private static object ReceiptFor(byte[] bytes) =>
            new { bytes = bytes.Length, sha256 = Hash(bytes) };

        public static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

        public Task<ActorFieldReplayReport> RunAsync(
            CancellationToken token = default,
            IProgress<int>? progress = null
        ) => new ActorFieldReplayBatch().RunAsync(Input, Receipt, Bundle, Output, token, progress);

        public void Dispose() => Directory.Delete(root, true);
    }
}
