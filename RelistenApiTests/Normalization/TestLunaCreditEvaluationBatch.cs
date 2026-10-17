using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;
using Relisten.Normalization.Batch;

namespace RelistenApiTests.Normalization;

[TestFixture]
public sealed class TestLunaCreditEvaluationBatch
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };
    private string directory = null!;
    private string input = null!;

    [SetUp]
    public void Start()
    {
        directory = Path.Combine(
            Path.GetTempPath(),
            "relisten-luna-selection-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(directory);
        input = Path.Combine(directory, "extractions.jsonl");
    }

    [TearDown]
    public void Stop() => Directory.Delete(directory, true);

    [Test]
    public async Task Comparison_includes_local_successes_and_unresolved_inputs_with_exact_bytes_receipt()
    {
        var rows = new[]
        {
            Source("Taped by Jane Doe"),
            Source("Transferred by Pat Smith"),
            Source("unknown"),
        };
        await Write(rows);

        var result = await LunaCreditEvaluationBatch.SelectAsync(input, 1);

        result.Items.Should().HaveCount(2);
        result
            .Items.Select(item => item.Cohort)
            .Should()
            .BeEquivalentTo("local_credit_present", "local_unresolved_or_missing");
        result
            .Items.Single(item => item.Cohort == "local_unresolved_or_missing")
            .Input.Inputs["taper"]
            .Should()
            .Be("unknown");
        result
            .InputSha256.Should()
            .Be(Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(input))));

        await Write(rows.Reverse().ToArray());
        var reordered = await LunaCreditEvaluationBatch.SelectAsync(input, 1);
        reordered
            .Items.Select(item => item.Input.ResourceUuid)
            .Should()
            .Equal(result.Items.Select(item => item.Input.ResourceUuid));
        reordered.InputSha256.Should().NotBe(result.InputSha256);
    }

    [Test]
    public async Task Truncated_output_is_rejected_before_any_paid_work()
    {
        await Write([Source("Jane Doe")], expectedCount: 2);
        var select = () => LunaCreditEvaluationBatch.SelectAsync(input, 1);
        await select.Should().ThrowAsync<InvalidDataException>();
    }

    [Test]
    public async Task Duplicate_sources_cannot_inflate_comparison_sample()
    {
        var row = Source("Jane Doe");
        await Write([row, Source("Pat Smith"), row]);
        var select = () => LunaCreditEvaluationBatch.SelectAsync(input, 1);
        await select.Should().ThrowAsync<InvalidDataException>();
    }

    [Test]
    public async Task Noncredit_local_roles_do_not_count_as_supported_taper_successes()
    {
        var fields = new Dictionary<string, string?> { ["uploader"] = "Bob" };
        var row = Source("Jane Doe") with
        {
            Inputs = fields,
            CreditExtraction = new RecordingCreditExtractor().Extract(fields),
        };
        await Write([row]);
        var result = await LunaCreditEvaluationBatch.SelectAsync(input, 1);
        result.Items.Single().Cohort.Should().Be("local_unresolved_or_missing");
    }

    [Test]
    public async Task Unrelated_minimal_manifest_cannot_authorize_paid_work()
    {
        await Write([Source("Jane Doe")]);
        await File.WriteAllTextAsync(
            Path.Combine(directory, "manifest.json"),
            "{\"state\":\"completed\",\"outputRecords\":1}"
        );
        var select = () => LunaCreditEvaluationBatch.SelectAsync(input, 1);
        await select.Should().ThrowAsync<InvalidDataException>();
    }

    private async Task Write(NormalizationBatchExtraction[] rows, long? expectedCount = null)
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
                    outputRecords = expectedCount ?? rows.Length,
                    snapshot = new
                    {
                        kind = "snapshot",
                        snapshot = "test:1:",
                        read_only = "on",
                        in_recovery = true,
                    },
                    snapshotCompletion = new
                    {
                        kind = "snapshot_complete",
                        completed_at = "2026-10-17T17:04:06Z",
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

    private static NormalizationBatchExtraction Source(string taper)
    {
        var fields = new Dictionary<string, string?> { ["taper"] = taper };
        return new(
            "source_sample",
            1,
            Guid.NewGuid(),
            1,
            Guid.NewGuid(),
            "show-family",
            fields,
            null,
            new RecordingCreditExtractor().Extract(fields),
            null
        );
    }
}
