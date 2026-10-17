using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Relisten.Normalization.Batch;

namespace RelistenApiTests.Normalization;

/// <summary>
/// Verifies the offline runner's retained-input and publication contracts. Synthetic
/// captures isolate failure behavior; real extraction semantics have separate corpus tests.
/// </summary>
[TestFixture]
public class TestNormalizationExtractionBatch
{
    private static readonly Guid ArtistUuid = Guid.Parse("4b50d2ec-6d47-4285-8a4d-b2a2aad9d1b0");
    private static readonly Guid SourceUuid = Guid.Parse("f43fd245-4241-4458-9308-cbac631ba3d7");
    private static readonly Guid TrackUuid = Guid.Parse("bde14ada-a3b0-46ed-a633-d7bab48d7292");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    private string directory = null!;
    private string inputPath = null!;
    private string outputDirectory = null!;

    [SetUp]
    public void CreateWorkspace()
    {
        directory = Path.Combine(
            Path.GetTempPath(),
            $"relisten-normalization-batch-{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(directory);
        inputPath = Path.Combine(directory, "snapshot.jsonl");
        outputDirectory = Path.Combine(directory, "output");
    }

    [TearDown]
    public void RemoveWorkspace() => Directory.Delete(directory, recursive: true);

    [Test]
    public async Task Completed_capture_preserves_exact_inputs_scope_evidence_and_operational_receipts()
    {
        const string description = "<div>🎵 Taped by Jane Doe</div>\nTransferred by Pat Smith";
        var records = Capture(
            Source(description),
            Track("...Driving Song >"),
            JsonSerializer.Serialize(
                new { kind = "venue_sample", row = new { id = 100, name = "The Room" } }
            )
        );
        await WriteInput(records);

        var report = await new NormalizationExtractionBatch().RunAsync(inputPath, outputDirectory);

        report.State.Should().Be("completed");
        report
            .InputSha256.Should()
            .Be(Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(inputPath))));
        report.InputRecords.Should().Be(records.Count);
        report.OutputRecords.Should().Be(2);
        report.ExtractedRecords.Should().Be(2);
        report.ErrorCount.Should().Be(0);
        report.UnsupportedKinds.Should().ContainKey("venue_sample").WhoseValue.Should().Be(1);
        report.CreditRoles.Should().ContainKey("taper").WhoseValue.Should().Be(1);
        report.CreditRoles.Should().ContainKey("transferrer").WhoseValue.Should().Be(1);
        report.Artists[1].Sources.Should().Be(1);
        report.Artists[1].Tracks.Should().Be(1);
        report.Artists[1].ArtistUuid.Should().Be(ArtistUuid);
        report.Snapshot!.Value.GetProperty("snapshot").GetString().Should().Be("test:1:");
        report.SnapshotCompletion.Should().NotBeNull();

        var output = await ReadExtractions();
        var source = output.Single(row => row.Kind == "source_sample");
        source.ResourceId.Should().Be(10);
        source.ResourceUuid.Should().Be(SourceUuid);
        source.ArtistUuid.Should().Be(ArtistUuid);
        source.Family.Should().Be("artist:1:show:25");
        source.Inputs["description"].Should().Be(description);
        source.CreditExtraction!.Credits.Should().Contain(credit => credit.Name == "Jane Doe");
        foreach (var credit in source.CreditExtraction.Credits)
        {
            var runes = source.Inputs[credit.Evidence.Field]!.EnumerateRunes().ToArray();
            string.Concat(
                    runes[credit.Evidence.StartCodePoint..credit.Evidence.EndCodePoint]
                        .Select(rune => rune.ToString())
                )
                .Should()
                .Be(credit.Evidence.Quote);
        }

        var track = output.Single(row => row.Kind == "track_sample");
        track.ResourceUuid.Should().Be(TrackUuid);
        track.TitleExtraction!.ArtistUuid.Should().Be(ArtistUuid);
        track.TitleExtraction.OriginalTitle.Should().Be("...Driving Song >");
        track.TitleExtraction.Segments.Single().Title.Should().Be("Driving Song");
        track.TitleExtraction.Segments.Single().OccurrenceUuid.Should().BeNull();
        File.Exists(Path.Combine(outputDirectory, "manifest.json")).Should().BeTrue();
        File.Exists(Path.Combine(outputDirectory, "extractions.partial.jsonl")).Should().BeFalse();
        var persisted = JsonSerializer.Deserialize<NormalizationExtractionBatchReport>(
            await File.ReadAllTextAsync(Path.Combine(outputDirectory, "manifest.json")),
            Json
        )!;
        persisted.InputSha256.Should().Be(report.InputSha256);
        persisted.OutputRecords.Should().Be(output.Count);
    }

    [Test]
    public async Task Run_identity_is_reproducible_and_changes_when_exact_retained_input_changes()
    {
        await WriteInput(Capture(Track("A Song")));
        var runner = new NormalizationExtractionBatch();

        var first = await runner.RunAsync(inputPath, outputDirectory);
        var repeat = await runner.RunAsync(inputPath, Path.Combine(directory, "repeat"));
        // Whitespace has no effect on JSON meaning, but the receipt identifies exact bytes.
        await File.WriteAllTextAsync(inputPath, " " + await File.ReadAllTextAsync(inputPath));
        var changed = await runner.RunAsync(inputPath, Path.Combine(directory, "changed"));

        repeat.RunIdentitySha256.Should().Be(first.RunIdentitySha256);
        changed.State.Should().Be("completed");
        changed.RunIdentitySha256.Should().NotBe(first.RunIdentitySha256);
        changed.InputSha256.Should().NotBe(first.InputSha256);
    }

    [Test]
    public async Task Missing_playback_context_and_null_titles_remain_inspectable_without_failing_capture()
    {
        await WriteInput(Capture(Track(null)));

        var report = await new NormalizationExtractionBatch().RunAsync(inputPath, outputDirectory);

        report.State.Should().Be("completed");
        report
            .MissingTrackContext.Should()
            .BeEquivalentTo(
                new Dictionary<string, long>
                {
                    ["source_context"] = 1,
                    ["set_context"] = 1,
                    ["show_context"] = 1,
                }
            );
        report.Artists[1].UnresolvedTitles.Should().Be(1);
        var track = (await ReadExtractions()).Single();
        track.Inputs["title"].Should().BeNull();
        track.TitleExtraction!.Reason.Should().Be("no_song_title_identified");
        track.TrackContext!.SourceUuid.Should().BeNull();
    }

    [Test]
    public async Task Truncated_capture_retains_partial_results_without_completed_manifest()
    {
        var records = Capture(Source("Taped by Jane Doe"));
        records.RemoveAt(records.Count - 1);
        await WriteInput(records);

        var report = await new NormalizationExtractionBatch().RunAsync(inputPath, outputDirectory);

        report.State.Should().Be("incomplete");
        report
            .InputSha256.Should()
            .NotBeNull("the truncated input file itself was read completely");
        report.OutputRecords.Should().Be(1);
        report.ErrorReasons.Should().ContainKey("incomplete_snapshot");
        File.Exists(Path.Combine(outputDirectory, "manifest.json")).Should().BeFalse();
        File.Exists(Path.Combine(outputDirectory, "extractions.jsonl")).Should().BeFalse();
        File.Exists(Path.Combine(outputDirectory, "extractions.partial.jsonl")).Should().BeTrue();
        File.Exists(Path.Combine(outputDirectory, "report.partial.json")).Should().BeTrue();
    }

    [Test]
    public async Task Failed_item_keeps_good_results_and_original_bad_input_with_partial_completion()
    {
        var badSource = JsonSerializer.Serialize(
            new
            {
                kind = "source_sample",
                row = new
                {
                    id = 10,
                    uuid = "not-a-uuid",
                    artist_id = 1,
                    description = "Taped by Jane Doe",
                },
            }
        );
        await WriteInput(Capture(badSource, Track("Jam >")));

        var report = await new NormalizationExtractionBatch().RunAsync(inputPath, outputDirectory);

        report.State.Should().Be("completed_partial");
        report.ErrorCount.Should().Be(1);
        report.OutputRecords.Should().Be(2);
        report.ExtractedRecords.Should().Be(1);
        var lines = await File.ReadAllLinesAsync(
            Path.Combine(outputDirectory, "extractions.jsonl")
        );
        using var error = JsonDocument.Parse(lines[0]);
        error.RootElement.GetProperty("kind").GetString().Should().Be("extraction_error");
        error
            .RootElement.GetProperty("input")
            .GetProperty("row")
            .GetProperty("description")
            .GetString()
            .Should()
            .Be("Taped by Jane Doe");
        JsonSerializer
            .Deserialize<NormalizationBatchExtraction>(lines[1], Json)!
            .TitleExtraction!.Segments.Single()
            .Kind.Should()
            .Be(Relisten.Api.Models.Normalization.TrackContentKind.Jam);
        File.Exists(Path.Combine(outputDirectory, "manifest.json")).Should().BeTrue();
    }

    [Test]
    public async Task Malformed_json_is_retained_as_an_error_and_does_not_drop_later_items()
    {
        const string malformed = "{broken input";
        await WriteInput(Capture(malformed, Track("A Song")));

        var report = await new NormalizationExtractionBatch().RunAsync(inputPath, outputDirectory);

        report.State.Should().Be("completed_partial");
        report.ErrorReasons.Should().ContainKey("invalid_json").WhoseValue.Should().Be(1);
        var lines = await File.ReadAllLinesAsync(
            Path.Combine(outputDirectory, "extractions.jsonl")
        );
        using var error = JsonDocument.Parse(lines[0]);
        error.RootElement.GetProperty("rawLine").GetString().Should().Be(malformed);
        lines.Should().HaveCount(2);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Rows_after_completion_cannot_publish_a_completed_receipt(bool malformed)
    {
        var records = Capture(Source("Taped by Jane Doe"));
        records.Add(malformed ? "{broken trailing input" : Track("A Song"));
        await WriteInput(records);

        var report = await new NormalizationExtractionBatch().RunAsync(inputPath, outputDirectory);

        report.State.Should().Be("incomplete");
        report
            .ErrorReasons.Should()
            .ContainKey(malformed ? "invalid_json" : "record_after_snapshot_complete");
        File.Exists(Path.Combine(outputDirectory, "manifest.json")).Should().BeFalse();
    }

    [Test]
    public async Task Existing_output_directory_is_rejected_without_changing_its_contents()
    {
        await WriteInput(Capture(Track("A Song")));
        Directory.CreateDirectory(outputDirectory);
        var sentinel = Path.Combine(outputDirectory, "keep.txt");
        await File.WriteAllTextAsync(sentinel, "preserve me");

        var run = () => new NormalizationExtractionBatch().RunAsync(inputPath, outputDirectory);

        await run.Should().ThrowAsync<IOException>();
        (await File.ReadAllTextAsync(sentinel)).Should().Be("preserve me");
        Directory.GetFiles(outputDirectory).Should().Equal(sentinel);
    }

    private Task WriteInput(IEnumerable<string> records) =>
        File.WriteAllTextAsync(
            inputPath,
            string.Join("\n", records) + "\n",
            new UTF8Encoding(false)
        );

    private async Task<List<NormalizationBatchExtraction>> ReadExtractions() =>
        (await File.ReadAllLinesAsync(Path.Combine(outputDirectory, "extractions.jsonl")))
            .Select(line => JsonSerializer.Deserialize<NormalizationBatchExtraction>(line, Json)!)
            .ToList();

    private static List<string> Capture(params string[] items) =>
        [
            JsonSerializer.Serialize(
                new
                {
                    kind = "snapshot",
                    snapshot = "test:1:",
                    read_only = "on",
                    in_recovery = true,
                }
            ),
            JsonSerializer.Serialize(
                new { kind = "artist", row = new { id = 1, uuid = ArtistUuid } }
            ),
            .. items,
            JsonSerializer.Serialize(new { kind = "totals", rows = new { artists = 1 } }),
            JsonSerializer.Serialize(
                new { kind = "snapshot_complete", completed_at = "2026-10-17T17:04:06Z" }
            ),
        ];

    private static string Source(string description) =>
        JsonSerializer.Serialize(
            new
            {
                kind = "source_sample",
                family = "artist:1:show:25",
                row = new
                {
                    id = 10,
                    uuid = SourceUuid,
                    artist_id = 1,
                    description,
                },
            }
        );

    private static string Track(string? title) =>
        JsonSerializer.Serialize(
            new
            {
                kind = "track_sample",
                family = "artist:1:show:25",
                row = new
                {
                    id = 20,
                    uuid = TrackUuid,
                    artist_id = 1,
                    title,
                },
                source_context = (object?)null,
                set_context = (object?)null,
                show_context = (object?)null,
            }
        );
}
