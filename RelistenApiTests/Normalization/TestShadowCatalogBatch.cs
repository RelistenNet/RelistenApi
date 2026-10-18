using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Relisten.Normalization.Batch;

namespace RelistenApiTests.Normalization;

[TestFixture]
public sealed partial class TestShadowCatalogBatch
{
    private string directory = null!;

    [SetUp]
    public void SetUp()
    {
        directory = Path.Combine(
            Path.GetTempPath(),
            "relisten-shadow-tests-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(directory);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(directory, recursive: true);

    [Test]
    public async Task ExtractionAndIndexFailuresKeepOtherInputsAndDomainsWorking()
    {
        var fixture = await Capture(3, errorAt: 2);
        await new ShadowCatalogBatch([
            new ContentAdapter(),
            new PlaybackAdapter(),
            new BrokenIndexAdapter(),
        ]).RunAsync(fixture.Snapshot, fixture.Extractions, fixture.Output);

        var results = ReadResults(fixture.Output);
        results.Should().HaveCount(9);
        results
            .Where(value => Text(value, "domain") == "contents")
            .Select(value => Text(value, "outcome"))
            .Should()
            .Equal("proposed", "failed", "proposed");
        results
            .Where(value => Text(value, "domain") == "playback")
            .Should()
            .OnlyContain(value => Text(value, "outcome") == "proposed");
        results
            .Where(value => Text(value, "domain") == "broken_index")
            .Should()
            .OnlyContain(value => Text(value, "outcome") == "failed");
        var manifest = JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(fixture.Output, "manifest.json"))
        );
        Text(manifest.RootElement, "state").Should().Be("completed_with_failures");
        manifest.RootElement.GetProperty("outputRows").GetInt64().Should().Be(9);
    }

    [Test]
    public async Task CancellationDuringIndexingLeavesAResumableRun()
    {
        var fixture = await Capture(3);
        using var cancellation = new CancellationTokenSource();
        var adapter = new ContentAdapter(onObserve: () => cancellation.Cancel());
        var first = () =>
            new ShadowCatalogBatch([adapter]).RunAsync(
                fixture.Snapshot,
                fixture.Extractions,
                fixture.Output,
                ct: cancellation.Token
            );
        await first.Should().ThrowAsync<OperationCanceledException>();

        await new ShadowCatalogBatch([new ContentAdapter()]).RunAsync(
            fixture.Snapshot,
            fixture.Extractions,
            fixture.Output,
            resume: true
        );

        ReadResults(fixture.Output).Should().HaveCount(3);
    }

    [Test]
    public async Task CancellationAtCheckpointPreservesBothDomainsAndResumeDoesNotDoubleCount()
    {
        var fixture = await Capture(2005);
        using var cancellation = new CancellationTokenSource();
        var first = () =>
            new ShadowCatalogBatch([
                new ContentAdapter(onProcess: index =>
                {
                    if (index == 1999)
                    {
                        cancellation.Cancel();
                    }
                }),
                new PlaybackAdapter(),
            ]).RunAsync(
                fixture.Snapshot,
                fixture.Extractions,
                fixture.Output,
                ct: cancellation.Token
            );
        await first.Should().ThrowAsync<OperationCanceledException>();

        await new ShadowCatalogBatch([new ContentAdapter(), new PlaybackAdapter()]).RunAsync(
            fixture.Snapshot,
            fixture.Extractions,
            fixture.Output,
            resume: true
        );

        var results = ReadResults(fixture.Output);
        results.Should().HaveCount(4010);
        results
            .Select(value => Text(value, "domain") + Text(value, "inputKey"))
            .Distinct()
            .Should()
            .HaveCount(4010);
        using var manifest = JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(fixture.Output, "manifest.json"))
        );
        manifest.RootElement.GetProperty("outputRows").GetInt64().Should().Be(4010);
        Text(manifest.RootElement, "state").Should().Be("completed");
    }

    [Test]
    public async Task CompletedReplayKeepsOutputsAndChangedInputsCannotReuseTheRun()
    {
        var fixture = await Capture(3);
        await new ShadowCatalogBatch([new ContentAdapter()]).RunAsync(
            fixture.Snapshot,
            fixture.Extractions,
            fixture.Output
        );
        var files = Directory.GetFiles(fixture.Output, "*.jsonl.gz", SearchOption.AllDirectories);

        await new ShadowCatalogBatch([new ContentAdapter()]).RunAsync(
            fixture.Snapshot,
            fixture.Extractions,
            fixture.Output,
            resume: true
        );
        Directory
            .GetFiles(fixture.Output, "*.jsonl.gz", SearchOption.AllDirectories)
            .Should()
            .BeEquivalentTo(files);
        ReadResults(fixture.Output).Should().HaveCount(3);

        await File.AppendAllTextAsync(fixture.Extractions, "\n");
        var changed = () =>
            new ShadowCatalogBatch([new ContentAdapter()]).RunAsync(
                fixture.Snapshot,
                fixture.Extractions,
                fixture.Output,
                resume: true
            );
        await changed.Should().ThrowAsync<InvalidDataException>();
        ReadResults(fixture.Output).Should().HaveCount(3);
    }

    private async Task<CaptureFiles> Capture(int count, int? errorAt = null)
    {
        var artist = Guid.NewGuid();
        var rows = new List<object>
        {
            new
            {
                kind = "artist",
                row = new
                {
                    id = 1,
                    uuid = artist,
                    name = "Captured Artist",
                },
                configured_provider_mappings = Array.Empty<object>(),
            },
        };
        var extractions = new List<object>();
        for (var index = 1; index <= count; index++)
        {
            var uuid = Guid.NewGuid();
            var record = new
            {
                kind = "track_sample",
                row = new
                {
                    id = index,
                    uuid,
                    artist_id = 1,
                    title = "Track " + index,
                    mp3_url = "https://example.org/audio/" + index,
                },
            };
            rows.Add(record);
            extractions.Add(
                errorAt == index
                    ? new
                    {
                        kind = "extraction_error",
                        reason = "demonstrated_title_failure",
                        input = (object)record,
                    }
                    : new { kind = "track_sample", resourceUuid = uuid }
            );
        }

        var snapshot = Path.Combine(directory, "snapshot.jsonl");
        var extractionPath = Path.Combine(directory, "extractions.jsonl");
        await File.WriteAllLinesAsync(
            snapshot,
            rows.Select(value => JsonSerializer.Serialize(value))
        );
        await File.WriteAllLinesAsync(
            extractionPath,
            extractions.Select(value => JsonSerializer.Serialize(value))
        );
        var sha = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(snapshot)));
        await File.WriteAllTextAsync(
            Path.Combine(directory, "manifest.json"),
            JsonSerializer.Serialize(
                new
                {
                    inputSha256 = sha,
                    state = errorAt == null ? "completed" : "completed_partial",
                }
            )
        );
        return new(snapshot, extractionPath, Path.Combine(directory, "output"));
    }

    private static List<JsonElement> ReadResults(string output)
    {
        var results = new List<JsonElement>();
        foreach (
            var path in Directory
                .GetFiles(output, "*.jsonl.gz", SearchOption.AllDirectories)
                .Order()
        )
        {
            using var gzip = new GZipStream(File.OpenRead(path), CompressionMode.Decompress);
            using var reader = new StreamReader(gzip, Encoding.UTF8);
            while (reader.ReadLine() is { } line)
            {
                using var document = JsonDocument.Parse(line);
                results.Add(document.RootElement.Clone());
            }
        }
        return results.OrderBy(value => Text(value, "domain")).ToList();
    }

    private static string Text(JsonElement value, string property) =>
        value.GetProperty(property).GetString()!;

    private sealed record CaptureFiles(string Snapshot, string Extractions, string Output);

    private sealed class ContentAdapter(Action? onObserve = null, Action<int>? onProcess = null)
        : IShadowCatalogAdapter
    {
        public string Version => "test-content-v1";

        public IReadOnlyList<string> DomainsFor(string kind) =>
            kind == "track_sample" ? ["contents"] : [];

        public void Observe(JsonElement record)
        {
            if (Text(record, "kind") == "track_sample")
            {
                onObserve?.Invoke();
            }
        }

        public IEnumerable<ShadowCatalogResult> Process(ShadowCatalogInput input)
        {
            onProcess?.Invoke(input.Row.GetProperty("id").GetInt32());
            yield return ShadowCatalogResult.For(
                input,
                "contents",
                input.Extraction == null ? "failed" : "proposed",
                Version,
                input.Extraction == null ? null : new { title = Text(input.Row, "title") },
                new { row = input.Row },
                input.Extraction == null ? "missing_extraction" : null
            );
        }
    }

    private sealed class PlaybackAdapter : IShadowCatalogAdapter
    {
        public string Version => "test-playback-v1";

        public IReadOnlyList<string> DomainsFor(string kind) =>
            kind == "track_sample" ? ["playback"] : [];

        public void Observe(JsonElement record) { }

        public IEnumerable<ShadowCatalogResult> Process(ShadowCatalogInput input)
        {
            yield return ShadowCatalogResult.For(
                input,
                "playback",
                "proposed",
                Version,
                new { mp3Url = Text(input.Row, "mp3_url") },
                new { row = input.Row }
            );
        }
    }

    private sealed class BrokenIndexAdapter : IShadowCatalogAdapter
    {
        public string Version => "test-broken-index-v1";

        public IReadOnlyList<string> DomainsFor(string kind) =>
            kind == "track_sample" ? ["broken_index"] : [];

        public void Observe(JsonElement record) =>
            throw new InvalidOperationException("index unavailable");

        public IEnumerable<ShadowCatalogResult> Process(ShadowCatalogInput input) =>
            throw new AssertionException("A failed index cannot authorize output.");
    }
}
