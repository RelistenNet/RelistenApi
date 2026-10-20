using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization.Batch;

namespace RelistenApiTests.Normalization;

[TestFixture]
public sealed class TestPhishFileReferenceBenchmark
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Guid Artist = Guid.Parse("ca53d281-0041-ae33-a050-c87702d93b0c");
    private static readonly Guid Morning = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid Evening = Guid.Parse("10000000-0000-0000-0000-000000000002");
    private string directory = null!;

    [SetUp]
    public void Start() =>
        directory = Directory.CreateTempSubdirectory("phish-file-reference-").FullName;

    [TearDown]
    public void Stop() => Directory.Delete(directory, recursive: true);

    [Test]
    public void CompoundTitlesKeepRepeatsButScoreUniqueFileMemberships()
    {
        var benchmark = new PhishFileReferenceBenchmark();
        var prediction = benchmark.Predict(
            new(4, Artist, "Morning > Evening > Morning"),
            Dictionary()
        );
        prediction
            .Segments.Select(segment => segment.Song!.LegacySongUuid)
            .Should()
            .Equal(Morning, Evening, Morning);
        var score = PhishFileReferenceBenchmark.Score(prediction, [Key(Morning), Key(Evening)]);
        score.Exact.Should().BeTrue();
        score.Intersection.Should().HaveCount(2);
        score.Extra.Should().BeEmpty();
    }

    [Test]
    public void AmbiguousDictionaryCandidatesAreNotPredictionsAndUntitledFilesRemainUnresolved()
    {
        var benchmark = new PhishFileReferenceBenchmark();
        var dictionary = Dictionary();
        dictionary.Observe(Song(Guid.NewGuid(), "Morning", "3"));
        var ambiguous = benchmark.Predict(new(4, Artist, "Morning"), dictionary);
        ambiguous.Outcome.Should().Be("unresolved");
        ambiguous.PredictedSongKeys.Should().BeEmpty();
        ambiguous.Segments.Single().CandidateSongs.Should().HaveCount(2);
        PhishFileReferenceBenchmark
            .Score(ambiguous, [Key(Morning)])
            .Missing.Should()
            .Equal(Key(Morning));
        var untitled = benchmark.Predict(new(4, Artist, null), dictionary);
        untitled.Outcome.Should().Be("unresolved");
        untitled.Extraction.Should().BeNull();
        PhishFileReferenceBenchmark
            .Score(untitled, [Key(Evening)])
            .Missing.Should()
            .Equal(Key(Evening));
    }

    [Test]
    public async Task CounterfactualReferenceChangeLeavesPredictionsUnchangedAndChangesOnlyScores()
    {
        var first = await Run("first", [new(11, "Morning", [1])]);
        var changed = await Run("counterfactual", [new(11, "Morning", [2])]);
        first
            .Single()
            .GetProperty("prediction")
            .GetRawText()
            .Should()
            .Be(changed.Single().GetProperty("prediction").GetRawText());
        first
            .Single()
            .GetProperty("predictionInput")
            .GetRawText()
            .Should()
            .Be(changed.Single().GetProperty("predictionInput").GetRawText());
        first.Single().GetProperty("score").GetProperty("exact").GetBoolean().Should().BeTrue();
        var changedScore = changed.Single().GetProperty("score");
        changedScore
            .GetProperty("missing")
            .EnumerateArray()
            .Select(value => value.GetString())
            .Should()
            .Equal(Key(Evening));
        changedScore
            .GetProperty("extra")
            .EnumerateArray()
            .Select(value => value.GetString())
            .Should()
            .Equal(Key(Morning));
    }

    [Test]
    public async Task OriginalProviderFileIdsKeepEqualTitlesSeparateAndRetainUnmappedReferences()
    {
        var files = await Run(
            "identities",
            [new(11, "Morning", [1]), new(12, "Morning", [2, 999])]
        );
        files
            .Select(file => file.GetProperty("fileKey").GetString())
            .Should()
            .Equal("phish.in/show/683/track/11", "phish.in/show/683/track/12");
        files[0].GetProperty("score").GetProperty("exact").GetBoolean().Should().BeTrue();
        files[1]
            .GetProperty("unmappedProviderSongIds")
            .EnumerateArray()
            .Select(value => value.GetString())
            .Should()
            .Equal("999");
        files[1].GetProperty("score").GetProperty("missing").GetArrayLength().Should().Be(2);
        files
            .Select(file => file.GetProperty("sourceUuid").GetGuid())
            .Distinct()
            .Should()
            .ContainSingle();
    }

    [Test]
    public async Task ChangedOriginalBodyAndOutputLimitNeverPublishACompletionManifest()
    {
        var request = MakeRequest("changed-body", [new(11, "Morning", [1])]);
        var body = Path.Combine(
            Path.GetDirectoryName(request.RetainedAdmissionDescriptorPath)!,
            "response.json"
        );
        await File.AppendAllTextAsync(body, " ");
        var output = Path.Combine(directory, "changed-output");
        var run = () => new PhishFileReferenceBenchmark().RunAsync(request, output);
        await run.Should().ThrowAsync<InvalidDataException>();
        File.Exists(Path.Combine(output, "manifest.json")).Should().BeFalse();

        var bounded = MakeRequest("bounded", [new(11, "Morning", [1])]) with
        {
            MaximumOutputBytes = 1,
        };
        var boundedOutput = Path.Combine(directory, "bounded-output");
        var boundedRun = () => new PhishFileReferenceBenchmark().RunAsync(bounded, boundedOutput);
        await boundedRun.Should().ThrowAsync<InvalidDataException>();
        File.Exists(Path.Combine(boundedOutput, "manifest.json")).Should().BeFalse();
    }

    [Test]
    public async Task ExistingDestinationsAndConflictingShowOwnershipAreRefused()
    {
        var request = MakeRequest("existing", [new(11, "Morning", [1])]);
        var output = Directory.CreateDirectory(Path.Combine(directory, "existing-output")).FullName;
        await File.WriteAllTextAsync(Path.Combine(output, "keep.txt"), "original");
        var run = () => new PhishFileReferenceBenchmark().RunAsync(request, output);
        await run.Should().ThrowAsync<ArgumentException>();
        (await File.ReadAllTextAsync(Path.Combine(output, "keep.txt"))).Should().Be("original");

        var conflict = MakeRequest("wrong-owner", [new(11, "Morning", [1], 999)]);
        var conflictOutput = Path.Combine(directory, "wrong-owner-output");
        var conflictRun = () =>
            new PhishFileReferenceBenchmark().RunAsync(conflict, conflictOutput);
        await conflictRun.Should().ThrowAsync<InvalidDataException>();
        File.Exists(Path.Combine(conflictOutput, "manifest.json")).Should().BeFalse();
    }

    private async Task<JsonElement[]> Run(string name, Track[] tracks)
    {
        var request = MakeRequest(name, tracks);
        var output = Path.Combine(directory, name + "-output");
        var manifest = await new PhishFileReferenceBenchmark().RunAsync(request, output);
        File.Exists(manifest).Should().BeTrue();
        return (await File.ReadAllLinesAsync(Path.Combine(output, "files.jsonl")))
            .Select(Parse)
            .ToArray();
    }

    private PhishFileReferenceBenchmarkRequest MakeRequest(string name, Track[] tracks)
    {
        var root = Directory.CreateDirectory(Path.Combine(directory, name)).FullName;
        var dictionaryPath = Path.Combine(root, "songs.jsonl.gz");
        using (var file = File.Create(dictionaryPath))
        using (var gzip = new GZipStream(file, CompressionMode.Compress))
        using (var writer = new StreamWriter(gzip))
        {
            writer.WriteLine("{\"kind\":\"snapshot\",\"read_only\":\"on\",\"in_recovery\":true}");
            writer.WriteLine(Song(Morning, "Morning", "1"));
            writer.WriteLine(Song(Evening, "Evening", "2"));
            writer.WriteLine("{\"kind\":\"snapshot_complete\",\"shard_rows\":2}");
        }
        var catalogPath = Path.Combine(root, "catalog.json");
        File.WriteAllBytes(
            catalogPath,
            JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    complete = true,
                    completedShards = new[]
                    {
                        new
                        {
                            shardId = "0001-songs-1-2",
                            gzipPath = "songs.jsonl.gz",
                            actualRows = 2,
                            compressed = Artifact(dictionaryPath),
                        },
                    },
                },
                Json
            )
        );
        var bodyPath = Path.Combine(root, "response.json");
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                success = true,
                data = new
                {
                    id = 683,
                    tracks = tracks.Select(track => new
                    {
                        id = track.Id,
                        show_id = track.ShowId,
                        title = track.Title,
                        song_ids = track.SongIds,
                    }),
                },
            },
            Json
        );
        File.WriteAllBytes(bodyPath, payload);
        var revision = new NormalizationInputRevision
        {
            Uuid = Guid.NewGuid(),
            Resource = new("phish.in", "show", "683"),
            Payload = payload,
            Sha256 = Hash(payload),
            MediaType = "application/json",
            CaptureVersion = "phishin-show-response-v1",
            CapturedAt = DateTime.UtcNow,
        };
        var source = Guid.NewGuid();
        var show = Guid.NewGuid();
        var catalog = Artifact(catalogPath);
        var binding = new RetainedHttpCatalogBinding(
            "bound",
            null,
            source,
            source,
            show,
            null,
            catalog,
            null,
            null
        );
        var provenance = new RetainedHttpProviderProvenance(
            revision.Uuid,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            "https://phish.in/api/v1/shows/683.json",
            200,
            catalog,
            catalog,
            catalog,
            0,
            Artifact(bodyPath),
            binding
        );
        var inputsPath = Path.Combine(root, "inputs.jsonl");
        File.WriteAllText(
            inputsPath,
            JsonSerializer.Serialize(
                new
                {
                    kind = "provider_input",
                    provider = "phish.in",
                    row = new
                    {
                        uuid = revision.Uuid,
                        artist_id = 4,
                        artist_uuid = Artist,
                    },
                    input = revision,
                    retained_http = provenance,
                    catalog_binding = binding,
                    source_context = new
                    {
                        uuid = source,
                        artist_id = 4,
                        upstream_identifier = "683",
                        show_id = 10,
                    },
                    show_context = new
                    {
                        uuid = show,
                        id = 10,
                        artist_id = 4,
                    },
                },
                Json
            ) + "\n"
        );
        var descriptor = Path.Combine(root, "descriptor.json");
        File.WriteAllBytes(
            descriptor,
            JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    version = "retained-http-shadow-admission-v1",
                    state = "completed",
                    inputs = Artifact(inputsPath),
                },
                Json
            )
        );
        return new(descriptor, dictionaryPath);
    }

    private static ShadowRepertoireAdapter Dictionary()
    {
        var dictionary = new ShadowRepertoireAdapter();
        dictionary.Observe(Song(Morning, "Morning", "1"));
        dictionary.Observe(Song(Evening, "Evening", "2"));
        return dictionary;
    }

    private static JsonElement Song(Guid uuid, string name, string key) =>
        JsonSerializer.SerializeToElement(
            new
            {
                kind = "song_sample",
                row = new
                {
                    uuid,
                    name,
                    artist_id = 4,
                    upstream_identifier = key,
                },
            },
            Json
        );

    private static string Key(Guid song) => ShadowRepertoireAdapter.KnownEntityKey(song);

    private static JsonElement Parse(string line)
    {
        using var document = JsonDocument.Parse(line);
        return document.RootElement.Clone();
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static RetainedHttpArtifact Artifact(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return new(path, bytes.LongLength, Hash(bytes));
    }

    private sealed record Track(int Id, string Title, int[] SongIds, int ShowId = 683);
}
