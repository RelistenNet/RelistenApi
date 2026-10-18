using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Relisten.Normalization.Batch;

namespace RelistenApiTests.Normalization;

/// <summary>Exercises frozen capture selection and real local extraction through the batch API.</summary>
[TestFixture]
public sealed class TestShadowInlineCatalogBatch
{
    private string directory = null!;
    private readonly Guid artistUuid = Guid.NewGuid();

    [SetUp]
    public void SetUp()
    {
        directory = Path.Combine(
            Path.GetTempPath(),
            "relisten-shadow-inline-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(directory);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(directory, recursive: true);

    [Test]
    public async Task InlineExtractionFeedsCrossArtistPeopleIndexAndKeepsOriginalCreditEvidence()
    {
        var secondArtist = Guid.NewGuid();
        var firstSource = Guid.NewGuid();
        var secondSource = Guid.NewGuid();
        const string taper = "Alice Vale (alice@example.test)";
        var path = Path.Combine(directory, "people.jsonl.gz");
        await WriteGzipAsync(
            path,
            [
                Header("people-snapshot"),
                Artist(1, artistUuid),
                Artist(2, secondArtist),
                new { kind = "totals", rows = new { artists = 2 } },
                new
                {
                    kind = "source_sample",
                    row = new
                    {
                        id = 11,
                        uuid = firstSource,
                        artist_id = 1,
                        taper,
                    },
                },
                new
                {
                    kind = "source_sample",
                    row = new
                    {
                        id = 12,
                        uuid = secondSource,
                        artist_id = 2,
                        taper,
                    },
                },
                Footer(2),
            ]
        );
        var output = Path.Combine(directory, "people-output");

        await new ShadowCatalogBatch([new ShadowPeopleAdapter()]).RunInlineAsync(path, output);

        var results = ReadResults(output);
        results.Should().HaveCount(4).And.OnlyContain(row => Text(row, "outcome") == "proposed");
        var person = results.Single(row =>
            Text(row, "domain") == "people" && row.GetProperty("inputUuid").GetGuid() == firstSource
        );
        var link = person
            .GetProperty("proposal")
            .GetProperty("relationships")
            .EnumerateArray()
            .Should()
            .ContainSingle()
            .Which;
        link.GetProperty("targetSourceUuid").GetGuid().Should().Be(secondSource);
        link.GetProperty("targetArtistUuid").GetGuid().Should().Be(secondArtist);
        var credit = results
            .Single(row =>
                Text(row, "domain") == "credits"
                && row.GetProperty("inputUuid").GetGuid() == firstSource
            )
            .GetProperty("proposal")
            .GetProperty("credits")
            .EnumerateArray()
            .Should()
            .ContainSingle()
            .Which;
        credit.GetProperty("sourceUuid").GetGuid().Should().Be(firstSource);
        credit.GetProperty("evidence").GetProperty("quote").GetString().Should().Be(taper);
        using var manifest = await ReadManifestAsync(output);
        var frame = manifest
            .RootElement.GetProperty("population")
            .GetProperty("snapshots")
            .EnumerateArray()
            .Should()
            .ContainSingle()
            .Which;
        frame
            .GetProperty("header")
            .GetProperty("snapshot")
            .GetString()
            .Should()
            .Be("people-snapshot");
        frame.GetProperty("footer").GetProperty("shard_rows").GetInt64().Should().Be(2);
    }

    [Test]
    public async Task ResumeUsesFrozenShardsWhileANewRunIncludesCaptureGrowth()
    {
        var first = await ShardAsync("first", Track(1));
        var second = await ShardAsync("second", Track(2));
        var capturePath = await ManifestAsync([first]);
        var original = Path.Combine(directory, "original");
        await new ShadowCatalogBatch([new ShadowRepertoireAdapter()]).RunInlineAsync(
            capturePath,
            original
        );

        await ManifestAsync([first, second]);
        await new ShadowCatalogBatch([new ShadowRepertoireAdapter()]).RunInlineAsync(
            capturePath,
            original,
            resume: true
        );
        ReadResults(original).Should().ContainSingle();
        var expanded = Path.Combine(directory, "expanded");
        await new ShadowCatalogBatch([new ShadowRepertoireAdapter()]).RunInlineAsync(
            capturePath,
            expanded
        );
        ReadResults(expanded).Should().HaveCount(2);

        using var originalManifest = await ReadManifestAsync(original);
        using var expandedManifest = await ReadManifestAsync(expanded);
        originalManifest
            .RootElement.GetProperty("population")
            .GetProperty("capture")
            .GetProperty("files")
            .GetArrayLength()
            .Should()
            .Be(1);
        var population = expandedManifest.RootElement.GetProperty("population");
        population
            .GetProperty("snapshots")
            .EnumerateArray()
            .Select(frame => frame.GetProperty("header").GetProperty("snapshot").GetString())
            .Should()
            .Equal("first", "second");
        population.GetProperty("artists").GetInt64().Should().Be(1);
        population
            .GetProperty("configuredProviderArtists")
            .GetProperty("archive.org")
            .GetInt64()
            .Should()
            .Be(1);
    }

    [Test]
    public async Task CancelledInlineIndexingResumesItsOriginalSelectionAfterCaptureGrowth()
    {
        var first = await ShardAsync("first", Track(1));
        var second = await ShardAsync("second", Track(2));
        var capturePath = await ManifestAsync([first]);
        var output = Path.Combine(directory, "cancelled");
        using var cancellation = new CancellationTokenSource();
        var interrupted = () =>
            new ShadowCatalogBatch([
                new CountingAdapter(() => cancellation.Cancel()),
            ]).RunInlineAsync(capturePath, output, ct: cancellation.Token);
        await interrupted.Should().ThrowAsync<OperationCanceledException>();

        await ManifestAsync([first, second]);
        await new ShadowCatalogBatch([new CountingAdapter()]).RunInlineAsync(
            capturePath,
            output,
            resume: true
        );

        ReadResults(output).Should().ContainSingle();
        using var manifest = await ReadManifestAsync(output);
        Text(manifest.RootElement, "state").Should().Be("completed");
        manifest
            .RootElement.GetProperty("population")
            .GetProperty("capture")
            .GetProperty("files")
            .GetArrayLength()
            .Should()
            .Be(1);
    }

    [Test]
    public async Task ResumeRetainsAndVerifiesTheOriginalOptionalProviderInputs()
    {
        var shard = await ShardAsync("first", Track(1));
        var capturePath = await ManifestAsync([shard]);
        var providerPath = Path.Combine(directory, "provider-inputs.jsonl");
        await File.WriteAllTextAsync(providerPath, "");
        var output = Path.Combine(directory, "provider-output");
        await new ShadowCatalogBatch([new CountingAdapter()]).RunInlineAsync(
            capturePath,
            output,
            providerInputsPath: providerPath
        );
        await File.WriteAllTextAsync(providerPath, "changed");

        // Resume uses the saved provider file even when the optional argument is omitted.
        var resume = () =>
            new ShadowCatalogBatch([new CountingAdapter()]).RunInlineAsync(
                capturePath,
                output,
                resume: true
            );

        await resume.Should().ThrowAsync<InvalidDataException>().WithMessage("*bytes changed*");
        ReadResults(output).Should().ContainSingle();
    }

    [TestCase("gzipPath")]
    [TestCase("receipt")]
    public async Task ChangedFrozenInputIsRefusedBeforeAnyAdapterObservesData(string changedFile)
    {
        var shard = await ShardAsync("first", Track(1));
        var capturePath = await ManifestAsync([shard]);
        await File.AppendAllTextAsync(
            Path.Combine(directory, shard.GetProperty(changedFile).GetString()!),
            "changed"
        );
        var observer = new CountingAdapter();
        var output = Path.Combine(directory, "refused");

        var run = () => new ShadowCatalogBatch([observer]).RunInlineAsync(capturePath, output);

        await run.Should().ThrowAsync<InvalidDataException>();
        observer.Observed.Should().Be(0);
        Directory.Exists(output).Should().BeFalse();
    }

    [Test]
    public async Task ConflictingArtistUuidAcrossShardsCannotPublishAnIdentityFromEitherShard()
    {
        var first = await ShardAsync("first", Track(1));
        var second = await ShardAsync("second", Track(2), artist: Guid.NewGuid());
        var capturePath = await ManifestAsync([first, second]);
        var output = Path.Combine(directory, "conflicting");

        var run = () =>
            new ShadowCatalogBatch([new ShadowRepertoireAdapter()]).RunInlineAsync(
                capturePath,
                output
            );

        await run.Should().ThrowAsync<InvalidDataException>().WithMessage("*artist*conflict*");
        ReadResults(output).Should().BeEmpty();
        File.Exists(Path.Combine(output, "checkpoint.json")).Should().BeTrue();
    }

    [Test]
    public async Task InlineExtractionFailurePreservesOtherRowsAndTheirOriginalEvidence()
    {
        var malformed = new
        {
            kind = "track_sample",
            row = new
            {
                id = 2,
                uuid = Guid.NewGuid(),
                artist_id = 1,
                title = 42,
            },
        };
        var shard = await ShardAsync("mixed", Track(1), additionalRows: [malformed, Track(3)]);
        var capturePath = await ManifestAsync([shard]);
        var output = Path.Combine(directory, "mixed-output");

        await new ShadowCatalogBatch([new ShadowRepertoireAdapter()]).RunInlineAsync(
            capturePath,
            output
        );

        var results = ReadResults(output);
        results.Should().HaveCount(3);
        results.Count(row => Text(row, "outcome") == "proposed").Should().Be(2);
        var failure = results.Single(row => Text(row, "outcome") == "failed");
        failure
            .GetProperty("evidence")
            .GetProperty("extractionError")
            .GetProperty("input")
            .GetProperty("row")
            .GetProperty("title")
            .GetInt64()
            .Should()
            .Be(42);
        using var manifest = await ReadManifestAsync(output);
        Text(manifest.RootElement, "state").Should().Be("completed_with_failures");
        manifest
            .RootElement.GetProperty("population")
            .GetProperty("inlineExtractionErrors")
            .GetInt64()
            .Should()
            .Be(1);
    }

    [Test]
    public async Task AHashedShardWithAnIncorrectFooterCannotProduceCompletedResults()
    {
        var shard = await ShardAsync("incorrect-footer", Track(1), footerRows: 99);
        var capturePath = await ManifestAsync([shard]);
        var output = Path.Combine(directory, "incorrect-output");

        var run = () =>
            new ShadowCatalogBatch([new ShadowRepertoireAdapter()]).RunInlineAsync(
                capturePath,
                output
            );

        await run.Should().ThrowAsync<InvalidDataException>().WithMessage("*footer*");
        ReadResults(output).Should().BeEmpty();
    }

    private object Artist(long id, Guid uuid) =>
        new
        {
            kind = "artist",
            row = new
            {
                id,
                uuid,
                name = "Captured Artist",
            },
            configured_provider_mappings = new[] { new { provider_name = "archive.org" } },
        };

    private static object Track(long id) =>
        new
        {
            kind = "track_sample",
            row = new
            {
                id,
                uuid = Guid.NewGuid(),
                artist_id = 1,
                title = "A Song " + id,
            },
        };

    private static object Header(string snapshot) =>
        new
        {
            kind = "snapshot",
            version = "shadow-raw-catalog-v1",
            snapshot,
            read_only = "on",
            in_recovery = true,
        };

    private static object Footer(long rows) =>
        new { kind = "snapshot_complete", shard_rows = rows };

    private async Task<JsonElement> ShardAsync(
        string name,
        object row,
        Guid? artist = null,
        object[]? additionalRows = null,
        long? footerRows = null
    )
    {
        var payload = new[] { row }.Concat(additionalRows ?? []).ToArray();
        var path = Path.Combine(directory, name + ".jsonl.gz");
        await WriteGzipAsync(
            path,
            new[]
            {
                Header(name),
                Artist(1, artist ?? artistUuid),
                new { kind = "totals", rows = new { artists = 1 } },
            }
                .Concat(payload)
                .Append(Footer(footerRows ?? payload.Length))
        );
        var compressed = await FingerprintAsync(path);
        var receiptPath = Path.Combine(directory, name + ".receipt.json");
        await File.WriteAllTextAsync(
            receiptPath,
            JsonSerializer.Serialize(
                new
                {
                    state = "completed",
                    shard = new { id = name },
                    gzipPath = Path.GetFileName(path),
                    compressed,
                    reconciliation = new { actualRows = payload.Length },
                }
            )
        );
        return JsonSerializer.SerializeToElement(
            new
            {
                state = "completed",
                shardId = name,
                gzipPath = Path.GetFileName(path),
                compressed,
                actualRows = payload.Length,
                receipt = Path.GetFileName(receiptPath),
                receiptFingerprint = await FingerprintAsync(receiptPath),
            }
        );
    }

    private async Task<string> ManifestAsync(JsonElement[] shards)
    {
        var path = Path.Combine(directory, "manifest.json");
        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(
                new
                {
                    version = "shadow-raw-catalog-v1",
                    state = "partial",
                    completedShards = shards,
                }
            )
        );
        return path;
    }

    private static async Task WriteGzipAsync(string path, IEnumerable<object> records)
    {
        await using var file = File.Create(path);
        await using var gzip = new GZipStream(file, CompressionMode.Compress);
        await using var writer = new StreamWriter(gzip, new UTF8Encoding(false));
        foreach (var record in records)
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(record));
        }
    }

    private static async Task<object> FingerprintAsync(string path)
    {
        await using var file = File.OpenRead(path);
        return new
        {
            bytes = file.Length,
            sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(file)),
        };
    }

    private static async Task<JsonDocument> ReadManifestAsync(string output) =>
        JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(output, "manifest.json")));

    private static string Text(JsonElement value, string name) =>
        value.GetProperty(name).GetString()!;

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
            using var reader = new StreamReader(gzip);
            while (reader.ReadLine() is { } line)
            {
                using var document = JsonDocument.Parse(line);
                results.Add(document.RootElement.Clone());
            }
        }
        return results;
    }

    private sealed class CountingAdapter(Action? onObserve = null) : IShadowCatalogAdapter
    {
        internal int Observed { get; private set; }
        public string Version => "test-counting-inline-v1";

        public IReadOnlyList<string> DomainsFor(string kind) =>
            kind == "track_sample" ? ["probe"] : [];

        public void Observe(JsonElement record)
        {
            Observed++;
            onObserve?.Invoke();
        }

        public IEnumerable<ShadowCatalogResult> Process(ShadowCatalogInput input) =>
            [
                ShadowCatalogResult.For(
                    input,
                    "probe",
                    "proposed",
                    Version,
                    new { extracted = input.Extraction != null },
                    new { input.Record }
                ),
            ];
    }
}
