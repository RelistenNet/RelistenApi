using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Relisten.Normalization.Batch;

namespace RelistenApiTests.Normalization;

/// <summary>The replay publishes a complete pinned interpretation beside immutable file ownership.</summary>
[TestFixture]
public sealed partial class TestFileTitleReplayBatch
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };
    private string directory = null!;

    [SetUp]
    public void Start()
    {
        directory = Path.Combine(
            Path.GetTempPath(),
            "relisten-file-replay-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(directory);
    }

    [TearDown]
    public void Stop() => Directory.Delete(directory, recursive: true);

    [Test]
    public async Task CompleteReplayResolvesOldDuplicateAmbiguityThroughTheFullDictionary()
    {
        var fixture = await Export();
        var output = await new FileTitleReplayBatch().RunAsync(
            fixture.Manifest.Path,
            Path.Combine(directory, "output"),
            fixture.Manifest.Sha256
        );

        output.State.Should().Be("completed");
        output.Titles.Rows.Should().Be(1);
        output.FileMap.Should().Be(fixture.Input.FileMap);
        await using var file = File.OpenRead(output.Titles.Path);
        await using var compressed = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(compressed);
        var row = JsonSerializer.Deserialize<FileTitleReplayCorrespondence>(
            (await reader.ReadLineAsync())!,
            Json
        )!;
        row.TitleId.Should().Be(1);
        row.Correspondence.OriginalTitle.Should().Be("Drums");
        row.Correspondence.Targets.Should()
            .ContainSingle()
            .Which.Disposition.Should()
            .Be("proposed");
        row.Correspondence.Targets.Single().ComparisonDiagnostics.Should().HaveCount(2);
        (await HashAsync(fixture.Input.FileMap.Path)).Should().Be(fixture.Input.FileMap.Sha256);
        File.Exists(Path.Combine(directory, "output", "manifest.json")).Should().BeTrue();
    }

    [Test]
    public async Task AnIncompleteTitleSequenceCannotPublishACompleteFileReplay()
    {
        var fixture = await Export(declaredTitles: 2);
        var output = Path.Combine(directory, "incomplete");
        var act = () =>
            new FileTitleReplayBatch().RunAsync(
                fixture.Manifest.Path,
                output,
                fixture.Manifest.Sha256
            );

        await act.Should().ThrowAsync<InvalidDataException>().WithMessage("*incomplete*");
        File.Exists(Path.Combine(output, "manifest.json")).Should().BeFalse();
        (await HashAsync(fixture.Input.FileMap.Path)).Should().Be(fixture.Input.FileMap.Sha256);
    }

    [Test]
    public async Task ChangedFileOwnershipMapRefusesAdmissionBeforeCreatingOutput()
    {
        var fixture = await Export();
        await File.AppendAllTextAsync(fixture.Input.FileMap.Path, "changed ownership");
        var output = Path.Combine(directory, "changed");
        var act = () =>
            new FileTitleReplayBatch().RunAsync(
                fixture.Manifest.Path,
                output,
                fixture.Manifest.Sha256
            );

        await act.Should().ThrowAsync<InvalidDataException>();
        Directory.Exists(output).Should().BeFalse();
    }

    [Test]
    public async Task ATitleCannotBorrowAFileWitnessFromAnotherCaptureScope()
    {
        var fixture = await Export(titleScope: "unrelated-capture");
        var output = Path.Combine(directory, "wrong-scope");
        var act = () =>
            new FileTitleReplayBatch().RunAsync(
                fixture.Manifest.Path,
                output,
                fixture.Manifest.Sha256
            );

        await act.Should()
            .ThrowAsync<InvalidDataException>()
            .WithMessage("*outside the retained source population*");
        File.Exists(Path.Combine(output, "manifest.json")).Should().BeFalse();
    }

    private async Task<(MusicConsolidationArtifact Manifest, FileTitleReplayInput Input)> Export(
        long declaredTitles = 1,
        string titleScope = "catalogue",
        IReadOnlyList<string>? replayTitles = null
    )
    {
        var artist = Guid.NewGuid();
        var run = Path.Combine(directory, "retained");
        Directory.CreateDirectory(run);
        var witnessPath = Path.Combine(run, "evidence.jsonl");
        var literals = replayTitles ?? ["Drums"];
        await File.WriteAllLinesAsync(
            witnessPath,
            new[] { "Drums" }
                .Concat(literals)
                .Select(title => JsonSerializer.Serialize(new { title }))
        );
        var witness = new MusicTitleWitness(
            run,
            "evidence.jsonl",
            1,
            1,
            "/title",
            await HashAsync(witnessPath)
        );
        var names = new[] { Guid.NewGuid(), Guid.NewGuid() }
            .Select(uuid => new MusicDictionaryName(
                "catalogue",
                artist,
                ShadowRepertoireAdapter.KnownEntityKey(uuid),
                uuid,
                "Drums",
                "song_sample:" + uuid,
                [],
                witness
            ))
            .ToArray();
        var dictionary = await LinesAsync("dictionary.jsonl.gz", names, names.LongLength);
        var descriptors = await LinesAsync(
            "descriptors.jsonl.gz",
            Array.Empty<MusicRepertoireDescriptor>(),
            0
        );
        var cataloguePath = Path.Combine(directory, "catalogue-fixture");
        await File.WriteAllTextAsync(cataloguePath, "Synthetic pinned export context");
        var catalogue = await PinAsync(cataloguePath, 1);
        var music = new MusicConsolidationInput(
            "music-consolidation-input-v1",
            "completed",
            new(catalogue.Path, catalogue.Bytes, catalogue.Sha256, 1),
            descriptors,
            dictionary,
            [new(1, "catalogue", run)],
            [new(run, "evidence.jsonl", new FileInfo(witnessPath).Length, witness.ChunkSha256)]
        );
        var musicPath = Path.Combine(directory, "music-manifest.json");
        await File.WriteAllTextAsync(musicPath, JsonSerializer.Serialize(music, Json));
        var titles = await LinesAsync(
            "titles.jsonl.gz",
            literals.Select(
                (title, index) =>
                    new FileTitleReplayTitle(
                        index + 1,
                        titleScope,
                        artist,
                        title,
                        witness with
                        {
                            Line = index + 2,
                            RecordId = index + 2,
                        }
                    )
            ),
            replayTitles == null ? declaredTitles : literals.Count
        );
        var mapPath = Path.Combine(directory, "file-map-fixture");
        await File.WriteAllTextAsync(
            mapPath,
            "Synthetic immutable file ownership map; Python tests exercise SQLite joins"
        );
        var input = new FileTitleReplayInput(
            FileTitleReplayBatch.InputVersion,
            "completed",
            await PinAsync(musicPath, 1),
            await PinAsync(mapPath, 2),
            titles,
            60,
            1024 * 1024,
            0
        )
        {
            Readers = music.Sources,
            WitnessChunks = music.WitnessChunks,
        };
        var manifest = Path.Combine(directory, "replay-input.json");
        await File.WriteAllTextAsync(manifest, JsonSerializer.Serialize(input, Json));
        return (await PinAsync(manifest, 1), input);
    }

    private async Task<MusicConsolidationArtifact> LinesAsync<T>(
        string filename,
        IEnumerable<T> values,
        long rows
    )
    {
        var path = Path.Combine(directory, filename);
        await using (var file = File.Create(path))
        {
            await using var compressed = new GZipStream(file, CompressionMode.Compress);
            await using var writer = new StreamWriter(compressed);
            foreach (var value in values)
            {
                await writer.WriteLineAsync(JsonSerializer.Serialize(value, Json));
            }
        }
        return await PinAsync(path, rows);
    }

    private static async Task<MusicConsolidationArtifact> PinAsync(string path, long rows) =>
        new(path, new FileInfo(path).Length, await HashAsync(path), rows);

    private static async Task<string> HashAsync(string path)
    {
        await using var file = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(file));
    }
}
