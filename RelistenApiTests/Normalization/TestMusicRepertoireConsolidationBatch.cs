using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Relisten.Normalization.Batch;

namespace RelistenApiTests.Normalization;

/// <summary>Externally pinned synthetic exports exercise public batch publication and recovery.</summary>
[TestFixture]
public sealed class TestMusicRepertoireConsolidationBatch
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };
    private static readonly Guid Artist = Guid.Parse("dbdaae10-f99a-4615-a4e4-4d269c69fb73");
    private string directory = null!;

    [SetUp]
    public void Start()
    {
        directory = Path.Combine(
            Path.GetTempPath(),
            "relisten-music-batch-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(directory);
    }

    [TearDown]
    public void Stop() => Directory.Delete(directory, true);

    [Test]
    public async Task CompletePopulationBeyondOneThousandKeepsEveryCorrespondenceAndReplaysWithoutWrites()
    {
        var fixture = await Export(1001);
        var output = Path.Combine(directory, "complete");
        var batch = new MusicRepertoireConsolidationBatch();
        var completion = await batch.RunAsync(fixture.Manifest, output, fixture.ManifestSha);

        completion.Complete.Should().BeTrue();
        completion.LogicalRows.Should().Be(1001);
        completion.InputManifestSha256.Should().Be(fixture.ManifestSha);
        completion.Chunks.Sum(chunk => chunk.Rows).Should().Be(1001);
        var rows = await Results(output, completion);
        rows.Select(row => row.GetProperty("proposal").GetProperty("originalKey").GetString())
            .Should()
            .BeEquivalentTo(fixture.Descriptors.Select(value => value.OriginalKey));
        rows.Should().OnlyContain(row => row.GetProperty("outcome").GetString() == "proposed");
        for (var index = 0; index < rows.Length; index++)
        {
            var proposal = rows[index].GetProperty("proposal");
            proposal.GetProperty("targets").GetArrayLength().Should().Be(1);
            proposal
                .GetProperty("targets")[0]
                .GetProperty("song")
                .GetProperty("entityKey")
                .GetString()
                .Should()
                .Be(fixture.Names[index].SongKey);
        }
        using var progress = JsonDocument.Parse(
            await File.ReadAllBytesAsync(Path.Combine(output, "progress.json"))
        );
        progress
            .RootElement.GetProperty("population")
            .GetProperty("acceptedIdentity")
            .GetBoolean()
            .Should()
            .BeFalse();

        var retained = Files(output);
        var replay = await batch.RunAsync(
            fixture.Manifest,
            output,
            fixture.ManifestSha,
            resume: true
        );
        replay.Should().BeEquivalentTo(completion);
        Files(output).Should().BeEquivalentTo(retained);
    }

    [Test]
    public async Task EmptyCompletePopulationPublishesZeroRowsAndNoInventedChunk()
    {
        var fixture = await Export(0);
        var output = Path.Combine(directory, "empty");
        var completion = await new MusicRepertoireConsolidationBatch().RunAsync(
            fixture.Manifest,
            output,
            fixture.ManifestSha
        );

        completion.Complete.Should().BeTrue();
        completion.LogicalRows.Should().Be(0);
        completion.Chunks.Should().BeEmpty();
        File.Exists(Path.Combine(output, "manifest.json")).Should().BeTrue();
        var replay = await new MusicRepertoireConsolidationBatch().RunAsync(
            fixture.Manifest,
            output,
            fixture.ManifestSha,
            resume: true
        );
        replay.Should().BeEquivalentTo(completion);
    }

    [Test]
    public async Task WrongExternalManifestPinRefusesAdmission()
    {
        var fixture = await Export(1);
        var output = Path.Combine(directory, "wrong-pin");
        var action = () =>
            new MusicRepertoireConsolidationBatch().RunAsync(
                fixture.Manifest,
                output,
                new string('0', 64)
            );

        await action.Should().ThrowAsync<InvalidDataException>();
        Directory.Exists(output).Should().BeFalse();
    }

    [TestCase("descriptors")]
    [TestCase("dictionary")]
    [TestCase("catalogue")]
    public async Task MutatedPinnedArtifactRefusesAdmissionWithoutACompletion(string artifact)
    {
        var fixture = await Export(1);
        var path = artifact switch
        {
            "descriptors" => fixture.Input.Descriptors.Path,
            "dictionary" => fixture.Input.DictionaryNames.Path,
            _ => fixture.Input.Catalogue.Path,
        };
        await File.AppendAllTextAsync(path, "changed");
        var output = Path.Combine(directory, "changed-" + artifact);
        var action = () =>
            new MusicRepertoireConsolidationBatch().RunAsync(
                fixture.Manifest,
                output,
                fixture.ManifestSha
            );

        await action.Should().ThrowAsync<InvalidDataException>();
        Directory.Exists(output).Should().BeFalse();
    }

    [Test]
    public async Task PreCanceledRunCreatesNeitherCheckpointNorCompletion()
    {
        var fixture = await Export(1);
        var output = Path.Combine(directory, "cancelled");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var action = () =>
            new MusicRepertoireConsolidationBatch().RunAsync(
                fixture.Manifest,
                output,
                fixture.ManifestSha,
                ct: cancellation.Token
            );

        await action.Should().ThrowAsync<OperationCanceledException>();
        Directory.Exists(output).Should().BeFalse();
    }

    [Test]
    public async Task AuthenticCheckpointResumesWhenOnlyTheFinalCompletionMarkerWasNotRetained()
    {
        var fixture = await Export(4);
        var output = Path.Combine(directory, "resume");
        var first = await new MusicRepertoireConsolidationBatch().RunAsync(
            fixture.Manifest,
            output,
            fixture.ManifestSha
        );
        var retainedChunks = first.Chunks.ToDictionary(
            chunk => chunk.Path,
            chunk => Hash(File.ReadAllBytes(Path.Combine(output, chunk.Path)))
        );

        // Model a crash after the durable public checkpoint but before the final
        // completion marker survives. The checkpoint and result chunks are genuine.
        File.Delete(Path.Combine(output, "manifest.json"));
        var resumed = await new MusicRepertoireConsolidationBatch().RunAsync(
            fixture.Manifest,
            output,
            fixture.ManifestSha,
            resume: true
        );

        resumed.Complete.Should().BeTrue();
        resumed.LogicalRows.Should().Be(4);
        var rows = await Results(output, resumed);
        rows.Should().HaveCount(4);
        rows.Select(row => row.GetProperty("inputKey").GetString())
            .Distinct()
            .Should()
            .HaveCount(4);
        resumed
            .Chunks.ToDictionary(
                chunk => chunk.Path,
                chunk => Hash(File.ReadAllBytes(Path.Combine(output, chunk.Path)))
            )
            .Should()
            .BeEquivalentTo(retainedChunks);
    }

    [TestCase("missing")]
    [TestCase("altered")]
    public async Task DamagedAcknowledgedResultsCannotBeSkippedOnResumeOrPublishedComplete(
        string damage
    )
    {
        var fixture = await Export(4);
        var output = Path.Combine(directory, "damaged-" + damage);
        var first = await new MusicRepertoireConsolidationBatch().RunAsync(
            fixture.Manifest,
            output,
            fixture.ManifestSha
        );
        File.Delete(Path.Combine(output, "manifest.json"));
        var path = Path.Combine(output, first.Chunks.Single().Path);
        if (damage == "missing")
        {
            File.Delete(path);
        }
        else
        {
            var rows = await Results(output, first);
            var changed = rows.Select(row =>
                    System.Text.Json.Nodes.JsonNode.Parse(row.GetRawText())!
                )
                .ToArray();
            changed[0]["proposal"]!["reason"] = "altered acknowledged interpretation";
            await Gzip(path, changed.Select(node => node.ToJsonString()));
        }
        var resume = () =>
            new MusicRepertoireConsolidationBatch().RunAsync(
                fixture.Manifest,
                output,
                fixture.ManifestSha,
                resume: true
            );

        await resume.Should().ThrowAsync<InvalidDataException>();
        File.Exists(Path.Combine(output, "manifest.json")).Should().BeFalse();
        File.Exists(Path.Combine(output, "checkpoint.json")).Should().BeTrue();
    }

    [Test]
    public async Task CompletedReplayStillAuthenticatesItsRetainedOutputBytes()
    {
        var fixture = await Export(1);
        var output = Path.Combine(directory, "changed-completion");
        var first = await new MusicRepertoireConsolidationBatch().RunAsync(
            fixture.Manifest,
            output,
            fixture.ManifestSha
        );
        await File.AppendAllTextAsync(Path.Combine(output, first.Chunks.Single().Path), "changed");
        var replay = () =>
            new MusicRepertoireConsolidationBatch().RunAsync(
                fixture.Manifest,
                output,
                fixture.ManifestSha,
                resume: true
            );

        await replay.Should().ThrowAsync<InvalidDataException>();
    }

    private async Task<ExportFixture> Export(int count)
    {
        var root = Path.Combine(directory, "input-" + Guid.NewGuid().ToString("N"));
        var sourceRun = Path.Combine(root, "captured-source");
        var sourceChunk = Path.Combine(sourceRun, "repertoire", "000001.jsonl.gz");
        Directory.CreateDirectory(Path.GetDirectoryName(sourceChunk)!);
        var sourceRows = Enumerable
            .Range(0, count)
            .Select(index => SourceResult(index, "09.Song " + index, "file_sample"))
            .Concat(
                Enumerable
                    .Range(0, count)
                    .Select(index => SourceResult(index, "Song " + index, "repertoire_song"))
            );
        await Gzip(sourceChunk, sourceRows.Select(row => JsonSerializer.Serialize(row, Json)));
        var sourceSha = Hash(await File.ReadAllBytesAsync(sourceChunk));
        var descriptors = Enumerable
            .Range(0, count)
            .Select(index => new MusicRepertoireDescriptor(
                "fixture:catalogue",
                Artist,
                ShadowRepertoireAdapter.ProvisionalEntityKey(Artist, "09.Song " + index),
                "09.Song " + index,
                "title_derived",
                "proposed",
                null,
                [],
                new(
                    sourceRun,
                    "repertoire/000001.jsonl.gz",
                    index + 1,
                    index + 1,
                    "/proposal/title",
                    sourceSha
                )
            ))
            .ToArray();
        var names = Enumerable
            .Range(0, count)
            .Select(index =>
            {
                var uuid = new Guid(index + 1, 0, 0, new byte[8]);
                return new MusicDictionaryName(
                    "fixture:catalogue",
                    Artist,
                    ShadowRepertoireAdapter.KnownEntityKey(uuid),
                    uuid,
                    "Song " + index,
                    "repertoire_song:" + uuid.ToString("D"),
                    [],
                    new(
                        sourceRun,
                        "repertoire/000001.jsonl.gz",
                        count + index + 1,
                        count + index + 1,
                        "/proposal/title",
                        sourceSha
                    )
                );
            })
            .ToArray();
        var descriptorArtifact = await Metadata(
            Path.Combine(root, "descriptors.jsonl"),
            descriptors
        );
        var dictionaryArtifact = await Metadata(Path.Combine(root, "dictionary.jsonl"), names);
        var cataloguePath = Path.Combine(root, "catalogue-snapshot.bin");
        await File.WriteAllTextAsync(
            cataloguePath,
            "Synthetic exporter-owned catalogue pin; the batch never queries this file."
        );
        var catalogueBytes = await File.ReadAllBytesAsync(cataloguePath);
        var input = new MusicConsolidationInput(
            "music-consolidation-input-v1",
            "completed",
            new(cataloguePath, catalogueBytes.Length, Hash(catalogueBytes), 1),
            descriptorArtifact,
            dictionaryArtifact,
            [new(1, "fixture:catalogue", sourceRun)],
            [
                new(
                    sourceRun,
                    "repertoire/000001.jsonl.gz",
                    new FileInfo(sourceChunk).Length,
                    sourceSha
                ),
            ]
        );
        var manifest = Path.Combine(root, "manifest.json");
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(input, Json);
        await File.WriteAllBytesAsync(manifest, manifestBytes);
        return new(manifest, Hash(manifestBytes), input, descriptors, names);
    }

    private static ShadowCatalogResult SourceResult(int index, string title, string inputKind) =>
        new(
            "repertoire",
            inputKind + ":" + index,
            inputKind,
            new Guid(index + 1, 0, 0, new byte[8]),
            Artist,
            "Fixture Artist",
            "catalog:fixture",
            "proposed",
            null,
            "retained-synthetic-export-v1",
            [],
            new { title },
            new { fixture = true }
        );

    private static async Task<MusicConsolidationArtifact> Metadata<T>(string path, T[] values)
    {
        await File.WriteAllTextAsync(
            path,
            string.Concat(values.Select(value => JsonSerializer.Serialize(value, Json) + "\n"))
        );
        var bytes = await File.ReadAllBytesAsync(path);
        return new(path, bytes.Length, Hash(bytes), values.LongLength);
    }

    private static async Task<JsonElement[]> Results(
        string output,
        MusicConsolidationOutput completion
    )
    {
        var rows = new List<JsonElement>();
        foreach (var chunk in completion.Chunks)
        {
            await using var file = File.OpenRead(Path.Combine(output, chunk.Path));
            await using var gzip = new GZipStream(file, CompressionMode.Decompress);
            using var reader = new StreamReader(gzip);
            while (await reader.ReadLineAsync() is { } line)
            {
                using var document = JsonDocument.Parse(line);
                rows.Add(document.RootElement.Clone());
            }
        }
        return rows.ToArray();
    }

    private static async Task Gzip(string path, IEnumerable<string> rows)
    {
        await using var writer = new StreamWriter(
            new GZipStream(File.Create(path), CompressionLevel.Optimal)
        );
        foreach (var row in rows)
        {
            await writer.WriteLineAsync(row);
        }
    }

    private static Dictionary<string, string> Files(string output) =>
        Directory
            .EnumerateFiles(output, "*", SearchOption.AllDirectories)
            .ToDictionary(
                path => Path.GetRelativePath(output, path),
                path => Hash(File.ReadAllBytes(path))
            );

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private sealed record ExportFixture(
        string Manifest,
        string ManifestSha,
        MusicConsolidationInput Input,
        MusicRepertoireDescriptor[] Descriptors,
        MusicDictionaryName[] Names
    );
}
