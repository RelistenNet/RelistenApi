using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization.Batch;

namespace RelistenApiTests.Normalization;

/// <summary>Public music publication supplies authentic complete inputs for the hypothesis batch.</summary>
[TestFixture]
public sealed class TestCompositionHypothesisBatch
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };
    private static readonly Guid FirstArtist = Guid.Parse("2ff84622-47a5-4dc7-9fa3-24e9af216c34");
    private static readonly Guid SecondArtist = Guid.Parse("11d750b2-5f9b-4ab2-a641-4fc27ca96d33");
    private static readonly Guid FirstSong = Guid.Parse("61a13bb6-241e-4f4f-8c88-1085cbcb26fe");
    private static readonly Guid SecondSong = Guid.Parse("948d2bc5-c424-427c-b2ce-093c3e388c39");
    private const string WorkId = "8973193c-0000-4000-8000-000000000001";
    private string directory = null!;

    [SetUp]
    public void Start() =>
        directory = Directory.CreateTempSubdirectory("composition-batch-tests-").FullName;

    [TearDown]
    public void Stop() => Directory.Delete(directory, recursive: true);

    [TestCase("provider_wrappers_jsonl", false)]
    [TestCase("input_revision_json", true)]
    public async Task EveryUniqueProposedTargetKeepsItsOriginalMembershipsAndGlobalWorkHypotheses(
        string format,
        bool withArtistContext
    )
    {
        var fixture = await Prepare(format, withArtistContext);
        var output = Path.Combine(directory, "hypotheses");
        var completion = await new CompositionHypothesisBatch().RunAsync(
            fixture.Request,
            output,
            fixture.RequestSha
        );

        completion.Complete.Should().BeTrue();
        completion.Songs.Should().Be(3);
        completion.Works.Should().Be(1);
        completion.Outcomes["hypotheses"].Should().Be(2);
        completion.Outcomes["missing_retained_candidates"].Should().Be(1);
        var rows = await Rows(output, completion);
        var hypotheses = rows.Where(row =>
                row.GetProperty("domain").GetString() == "composition_hypotheses"
            )
            .Select(row =>
                row.GetProperty("proposal").Deserialize<CompositionHypothesisResult>(Json)!
            )
            .ToArray();
        hypotheses.Should().HaveCount(3);
        var first = hypotheses.Single(result =>
            result.Song.Song.EntityKey == ShadowRepertoireAdapter.KnownEntityKey(FirstSong)
        );
        var second = hypotheses.Single(result =>
            result.Song.Song.EntityKey == ShadowRepertoireAdapter.KnownEntityKey(SecondSong)
        );
        first.Song.OriginalMemberships.Should().Be(2);
        first
            .Song.Relations.Should()
            .BeEquivalentTo(
                new MusicRepertoireRelation[]
                {
                    MusicRepertoireRelation.IdentityAlias,
                    MusicRepertoireRelation.Jam,
                    MusicRepertoireRelation.Tease,
                }
            );
        second.Song.OriginalMemberships.Should().Be(1);
        first.Song.ArtistUuid.Should().Be(FirstArtist);
        second.Song.ArtistUuid.Should().Be(SecondArtist);
        first.Candidates.Single().WorkIdentity.Identity.ProviderKey.Should().Be(WorkId);
        second.Candidates.Single().WorkIdentity.Identity.ProviderKey.Should().Be(WorkId);
        foreach (var hypothesis in hypotheses)
        {
            var expected = fixture.Artists.SingleOrDefault(artist =>
                artist.ArtistUuid == hypothesis.Song.ArtistUuid
            );
            hypothesis.Song.ArtistName.Should().Be(expected?.Name);
            hypothesis.Song.ArtistContext.Should().Be(expected);
            hypothesis
                .Song.ArtistContextManifestSha256.Should()
                .Be(
                    expected == null
                        ? null
                        : Hash(File.ReadAllBytes(fixture.ArtistContextManifest!))
                );
            if (expected != null)
            {
                hypothesis.Song.ArtistContext!.MusicBrainzId.Should().BeNull();
                hypothesis.Song.ArtistContext.Origin.Should().Be(expected.Origin);
            }
        }
        hypotheses
            .Should()
            .OnlyContain(result =>
                !result.PublicationPermitted && !result.ExternalUniverseComplete
            );
        hypotheses
            .Single(result => result.Outcome == "missing_retained_candidates")
            .Candidates.Should()
            .BeEmpty();
        rows.Count(row => row.GetProperty("domain").GetString() == "retained_work_candidates")
            .Should()
            .Be(1);
        var search = rows.Single(row =>
                row.GetProperty("domain").GetString() == "retained_work_search_pages"
            )
            .GetProperty("proposal")
            .Deserialize<MusicBrainzSearchPageObservation>(Json)!;
        search.Origin.Evidence.InputRevisionUuid.Should().Be(fixture.EmptySearch.Uuid);
        search.Count!.Value.Should().Be(0);
        search.Hits.Should().BeEmpty();

        var files = RetainedFiles(output);
        var replay = await new CompositionHypothesisBatch().RunAsync(
            fixture.Request,
            output,
            fixture.RequestSha,
            resume: true
        );
        replay.Should().BeEquivalentTo(completion);
        RetainedFiles(output).Should().BeEquivalentTo(files);
    }

    [TestCase("provider")]
    [TestCase("music_manifest")]
    [TestCase("music_chunk")]
    [TestCase("music_descriptors")]
    [TestCase("context_artists")]
    [TestCase("context_link")]
    public async Task ChangedPinnedInputCannotAdmitAnOutputOrCompletion(string changed)
    {
        var fixture = await Prepare(
            withArtistContext: changed.StartsWith("context_", StringComparison.Ordinal)
        );
        var requestSha = fixture.RequestSha;
        if (changed == "context_link")
        {
            var context = JsonSerializer.Deserialize<CompositionArtistContextManifest>(
                await File.ReadAllBytesAsync(fixture.ArtistContextManifest!),
                Json
            )!;
            context = context with
            {
                MusicOutputManifest = context.MusicOutputManifest with
                {
                    Sha256 = new string('0', 64),
                },
            };
            await File.WriteAllBytesAsync(
                fixture.ArtistContextManifest!,
                JsonSerializer.SerializeToUtf8Bytes(context, Json)
            );
            var request = JsonSerializer.Deserialize<CompositionHypothesisBatchRequest>(
                await File.ReadAllBytesAsync(fixture.Request),
                Json
            )!;
            request = request with
            {
                ArtistContextManifest = await Pin(fixture.ArtistContextManifest!, 1),
            };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(request, Json);
            await File.WriteAllBytesAsync(fixture.Request, bytes);
            requestSha = Hash(bytes);
        }
        else
        {
            var path = changed switch
            {
                "provider" => fixture.ProviderArtifact,
                "music_manifest" => fixture.MusicOutputManifest,
                "music_chunk" => fixture.MusicChunk,
                "context_artists" => fixture.ArtistsArtifact!,
                _ => fixture.Descriptors,
            };
            await File.AppendAllTextAsync(path, "changed");
        }
        var output = Path.Combine(directory, "refused");
        var action = () =>
            new CompositionHypothesisBatch().RunAsync(fixture.Request, output, requestSha);

        await action.Should().ThrowAsync<InvalidDataException>();
        Directory.Exists(output).Should().BeFalse();
    }

    [Test]
    public async Task WrongExternalRequestPinRefusesAdmission()
    {
        var fixture = await Prepare();
        var output = Path.Combine(directory, "wrong-request-pin");
        var action = () =>
            new CompositionHypothesisBatch().RunAsync(fixture.Request, output, new string('0', 64));

        await action.Should().ThrowAsync<InvalidDataException>();
        Directory.Exists(output).Should().BeFalse();
    }

    [Test]
    public async Task AuthenticCheckpointResumesWithoutRepeatingAnyCompletedLogicalResult()
    {
        var fixture = await Prepare();
        var output = Path.Combine(directory, "resume");
        var first = await new CompositionHypothesisBatch().RunAsync(
            fixture.Request,
            output,
            fixture.RequestSha
        );
        var retainedChunks = first.Chunks.ToDictionary(
            chunk => chunk.Path,
            chunk => Hash(File.ReadAllBytes(Path.Combine(output, chunk.Path)))
        );

        // Keep the real public checkpoint and result chunks; only the final
        // completion marker is absent, as after a crash before its publication.
        File.Delete(Path.Combine(output, "manifest.json"));
        var resumed = await new CompositionHypothesisBatch().RunAsync(
            fixture.Request,
            output,
            fixture.RequestSha,
            resume: true
        );

        resumed.Complete.Should().BeTrue();
        resumed.Songs.Should().Be(3);
        resumed.Works.Should().Be(1);
        var rows = await Rows(output, resumed);
        rows.Select(row =>
                (row.GetProperty("domain").GetString(), row.GetProperty("inputKey").GetString())
            )
            .Should()
            .OnlyHaveUniqueItems();
        resumed
            .Chunks.ToDictionary(
                chunk => chunk.Path,
                chunk => Hash(File.ReadAllBytes(Path.Combine(output, chunk.Path)))
            )
            .Should()
            .BeEquivalentTo(retainedChunks);
    }

    [Test]
    public async Task MissingAcknowledgedChunkRefusesResumeWithoutPublishingCompletion()
    {
        var fixture = await Prepare();
        var output = Path.Combine(directory, "missing-output");
        var first = await new CompositionHypothesisBatch().RunAsync(
            fixture.Request,
            output,
            fixture.RequestSha
        );
        File.Delete(Path.Combine(output, "manifest.json"));
        File.Delete(Path.Combine(output, first.Chunks.First().Path));
        var resume = () =>
            new CompositionHypothesisBatch().RunAsync(
                fixture.Request,
                output,
                fixture.RequestSha,
                resume: true
            );

        await resume.Should().ThrowAsync<InvalidDataException>();
        File.Exists(Path.Combine(output, "manifest.json")).Should().BeFalse();
        File.Exists(Path.Combine(output, "checkpoint.json")).Should().BeTrue();
    }

    [Test]
    public async Task PreCanceledRequestAdmitsNoCheckpointOrCompletion()
    {
        var fixture = await Prepare();
        var output = Path.Combine(directory, "cancelled");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var action = () =>
            new CompositionHypothesisBatch().RunAsync(
                fixture.Request,
                output,
                fixture.RequestSha,
                ct: cancellation.Token
            );

        await action.Should().ThrowAsync<OperationCanceledException>();
        Directory.Exists(output).Should().BeFalse();
    }

    private async Task<Fixture> Prepare(
        string format = "provider_wrappers_jsonl",
        bool withArtistContext = false
    )
    {
        var inputDirectory = Directory.CreateDirectory(Path.Combine(directory, "inputs")).FullName;
        var sourceRun = Directory
            .CreateDirectory(Path.Combine(inputDirectory, "retained-source"))
            .FullName;
        var titles = new[]
        {
            (Artist: FirstArtist, Title: "09.Crazy Fingers"),
            (Artist: FirstArtist, Title: "Crazy Fingers Jam (tease)"),
            (Artist: SecondArtist, Title: "09.Crazy Fingers"),
            (Artist: FirstArtist, Title: "No retained composition"),
            (Artist: FirstArtist, Title: "Crazy Fingers - Unknown passage"),
        };
        var sourceChunk = Path.Combine(sourceRun, "witnesses.jsonl.gz");
        await Gzip(
            sourceChunk,
            titles
                .Select(value => JsonSerializer.Serialize(new { title = value.Title }, Json))
                .Concat(new[] { "{\"title\":\"Crazy Fingers\"}" })
        );
        var witnessSha = Hash(await File.ReadAllBytesAsync(sourceChunk));
        MusicTitleWitness Witness(int line) =>
            new(sourceRun, "witnesses.jsonl.gz", line, line, "/title", witnessSha);
        var descriptors = titles
            .Select(
                (value, index) =>
                    new MusicRepertoireDescriptor(
                        "catalogue:fixture",
                        value.Artist,
                        ShadowRepertoireAdapter.ProvisionalEntityKey(value.Artist, value.Title),
                        value.Title,
                        "title_derived",
                        "proposed",
                        null,
                        [],
                        Witness(index + 1)
                    )
            )
            .ToArray();
        var names = new[]
        {
            (Artist: FirstArtist, Song: FirstSong),
            (Artist: SecondArtist, Song: SecondSong),
        }
            .Select(value => new MusicDictionaryName(
                "catalogue:fixture",
                value.Artist,
                ShadowRepertoireAdapter.KnownEntityKey(value.Song),
                value.Song,
                "Crazy Fingers",
                "repertoire_song:" + value.Song.ToString("D"),
                [],
                Witness(6)
            ))
            .ToArray();
        var descriptorPath = Path.Combine(inputDirectory, "descriptors.jsonl");
        var descriptorArtifact = await Metadata(descriptorPath, descriptors);
        var namesArtifact = await Metadata(Path.Combine(inputDirectory, "dictionary.jsonl"), names);
        var catalogue = Path.Combine(inputDirectory, "exporter-owned-catalogue.json");
        // These synthetic catalogue cells and the derived context use the same
        // values. The batches authenticate this exporter-owned pin without querying it.
        await File.WriteAllBytesAsync(
            catalogue,
            JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    artists = new[]
                    {
                        new
                        {
                            scope = "catalogue:fixture",
                            uuid = FirstArtist,
                            name = "Fixture performing artist A",
                        },
                        new
                        {
                            scope = "catalogue:fixture",
                            uuid = SecondArtist,
                            name = "Fixture performing artist B",
                        },
                        new
                        {
                            scope = "catalogue:fixture",
                            uuid = Guid.Empty,
                            name = "Unknown artist",
                        },
                    },
                },
                Json
            )
        );
        var catalogueBytes = await File.ReadAllBytesAsync(catalogue);
        var musicInput = new MusicConsolidationInput(
            "music-consolidation-input-v1",
            "completed",
            new(catalogue, catalogueBytes.Length, Hash(catalogueBytes), 1),
            descriptorArtifact,
            namesArtifact,
            [new(1, "catalogue:fixture", sourceRun)],
            [new(sourceRun, "witnesses.jsonl.gz", new FileInfo(sourceChunk).Length, witnessSha)]
        );
        var musicInputPath = Path.Combine(inputDirectory, "music-input.json");
        await File.WriteAllBytesAsync(
            musicInputPath,
            JsonSerializer.SerializeToUtf8Bytes(musicInput, Json)
        );
        var musicOutput = Path.Combine(directory, "complete-music");
        var music = await new MusicRepertoireConsolidationBatch().RunAsync(
            musicInputPath,
            musicOutput,
            Hash(await File.ReadAllBytesAsync(musicInputPath))
        );
        var work = ProviderInput(
            "work",
            WorkId,
            JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    id = WorkId,
                    title = "Crazy Fingers",
                    relations = Array.Empty<object>(),
                }
            )
        );
        var providerPath = Path.Combine(
            inputDirectory,
            format == "input_revision_json" ? "work-revision.json" : "work-wrappers.jsonl"
        );
        if (format == "input_revision_json")
        {
            await File.WriteAllBytesAsync(
                providerPath,
                JsonSerializer.SerializeToUtf8Bytes(work, Json)
            );
        }
        else
        {
            await File.WriteAllTextAsync(
                providerPath,
                JsonSerializer.Serialize(
                    new
                    {
                        kind = "provider_input",
                        row = new { uuid = work.Uuid },
                        input = work,
                    },
                    Json
                ) + "\n"
            );
        }
        var search = ProviderInput(
            "work-search",
            new string('b', 64),
            JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    count = 0,
                    offset = 0,
                    works = Array.Empty<object>(),
                }
            )
        );
        var searchPath = Path.Combine(inputDirectory, "empty-search.json");
        await File.WriteAllBytesAsync(
            searchPath,
            JsonSerializer.SerializeToUtf8Bytes(search, Json)
        );
        var musicOutputManifest = Path.Combine(musicOutput, "manifest.json");
        var musicInputPin = await Pin(musicInputPath, 1);
        var musicOutputPin = await Pin(musicOutputManifest, 1);
        CompositionArtistContext[] artists = [];
        string? artistsArtifact = null;
        string? artistContextManifest = null;
        MusicConsolidationArtifact? artistContextPin = null;
        if (withArtistContext)
        {
            artists = new[]
            {
                (Uuid: FirstArtist, Name: "Fixture performing artist A"),
                (Uuid: SecondArtist, Name: "Fixture performing artist B"),
                (Uuid: Guid.Empty, Name: "Unknown artist"),
            }
                .Select(artist => new CompositionArtistContext(
                    "catalogue:fixture",
                    artist.Uuid,
                    artist.Name,
                    null,
                    new(
                        "derived_catalogue_cell",
                        "artists",
                        new("catalogue:fixture", artist.Uuid),
                        "name",
                        musicInput.Catalogue.Sha256
                    )
                ))
                .ToArray();
            artistsArtifact = Path.Combine(inputDirectory, "artists.jsonl.gz");
            await Gzip(
                artistsArtifact,
                artists.Select(artist => JsonSerializer.Serialize(artist, Json))
            );
            var context = new CompositionArtistContextManifest(
                "composition-artist-context-v1",
                "completed",
                musicInputPin,
                musicOutputPin,
                musicInput.Catalogue,
                await Pin(artistsArtifact, artists.Length),
                musicInput.Sources,
                false
            );
            artistContextManifest = Path.Combine(inputDirectory, "artist-context.json");
            await File.WriteAllBytesAsync(
                artistContextManifest,
                JsonSerializer.SerializeToUtf8Bytes(context, Json)
            );
            artistContextPin = await Pin(artistContextManifest, 1);
        }
        var request = new CompositionHypothesisBatchRequest(
            "composition-hypothesis-input-v1",
            musicInputPin,
            musicOutputPin,
            [
                await ProviderPin(providerPath, format),
                await ProviderPin(searchPath, "input_revision_json"),
            ],
            artistContextPin
        );
        var requestPath = Path.Combine(directory, "composition-request.json");
        var requestBytes = JsonSerializer.SerializeToUtf8Bytes(request, Json);
        await File.WriteAllBytesAsync(requestPath, requestBytes);
        return new(
            requestPath,
            Hash(requestBytes),
            providerPath,
            musicOutputManifest,
            Path.Combine(musicOutput, music.Chunks.Single().Path),
            descriptorPath,
            search,
            artists,
            artistsArtifact,
            artistContextManifest
        );
    }

    private static NormalizationInputRevision ProviderInput(
        string type,
        string key,
        byte[] bytes
    ) =>
        new()
        {
            Uuid = Guid.NewGuid(),
            Resource = new("musicbrainz.org", type, key),
            Payload = bytes,
            Sha256 = Hash(bytes),
            MediaType = "application/json",
            CaptureVersion =
                type == "work" ? "musicbrainz-work-json-v1" : "musicbrainz-work-search-json-v1",
            CapturedAt = new DateTime(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc),
        };

    private static async Task<MusicConsolidationArtifact> Metadata<T>(string path, T[] rows)
    {
        await File.WriteAllTextAsync(
            path,
            string.Concat(rows.Select(row => JsonSerializer.Serialize(row, Json) + "\n"))
        );
        return await Pin(path, rows.Length);
    }

    private static async Task<MusicConsolidationArtifact> Pin(string path, long rows)
    {
        var bytes = await File.ReadAllBytesAsync(path);
        return new(path, bytes.Length, Hash(bytes), rows);
    }

    private static async Task<RetainedCompositionInputArtifact> ProviderPin(
        string path,
        string format
    )
    {
        var pin = await Pin(path, 1);
        return new(pin.Path, pin.Bytes, pin.Sha256, pin.Rows, format);
    }

    private static async Task<JsonElement[]> Rows(
        string output,
        CompositionHypothesisOutput completion
    )
    {
        var rows = new List<JsonElement>();
        foreach (var chunk in completion.Chunks)
        {
            await using var stream = File.OpenRead(Path.Combine(output, chunk.Path));
            await using var gzip = new GZipStream(stream, CompressionMode.Decompress);
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

    private static Dictionary<string, string> RetainedFiles(string output) =>
        Directory
            .EnumerateFiles(output, "*", SearchOption.AllDirectories)
            .ToDictionary(
                path => Path.GetRelativePath(output, path),
                path => Hash(File.ReadAllBytes(path))
            );

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private sealed record Fixture(
        string Request,
        string RequestSha,
        string ProviderArtifact,
        string MusicOutputManifest,
        string MusicChunk,
        string Descriptors,
        NormalizationInputRevision EmptySearch,
        CompositionArtistContext[] Artists,
        string? ArtistsArtifact,
        string? ArtistContextManifest
    );
}
