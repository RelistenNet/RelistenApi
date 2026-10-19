using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Relisten.Normalization;
using Relisten.Normalization.Batch;

namespace RelistenApiTests.Normalization;

/// <summary>Complete occurrence membership survives compact export without accepting identities.</summary>
[TestFixture]
public sealed class TestSharedPeopleExport
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
            "relisten-shared-people-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(directory);
    }

    [TearDown]
    public void Stop() => Directory.Delete(directory, true);

    [Test]
    public async Task FullSourceMembershipAndCrossArtistAliasesUseSharedPolicyWithoutExampleTruncation()
    {
        var firstArtist = Guid.NewGuid();
        var secondArtist = Guid.NewGuid();
        var sources = Enumerable
            .Range(0, 230)
            .Select(index =>
                Input(
                    index % 2 == 0 ? firstArtist : secondArtist,
                    "Recorded by Pat Myers AKA DATPAT (pat@example.test)"
                )
            )
            .Append(Input(secondArtist, "DATPAT (pat@example.test)"))
            .Append(Input(secondArtist, "Pat Myers"))
            .Append(Input(secondArtist, "Unrelated Person (pat@example.test)"))
            .ToArray();
        var run = await SourceRun(sources);
        var output = Path.Combine(directory, "shared");
        await new SharedPeopleExport(minimumFreeBytes: 0).RunAsync(run, output);

        var occurrences = await Rows(output, "occurrences");
        var buckets = await Rows(output, "buckets");
        var hypotheses = await Rows(output, "hypotheses");
        occurrences
            .Select(row => row.GetProperty("person").GetProperty("sourceUuid").GetGuid())
            .Distinct()
            .Should()
            .HaveCount(233);
        buckets.Max(row => row.GetProperty("distinctSources").GetInt64()).Should().Be(230);
        buckets
            .Where(row => row.GetProperty("distinctSources").GetInt64() == 230)
            .Should()
            .OnlyContain(row => row.GetProperty("distinctArtists").GetInt64() == 2);
        var supported = hypotheses
            .Where(row => row.GetProperty("supported").GetBoolean())
            .ToArray();
        supported.Should().NotBeEmpty();
        var supportedBuckets = supported
            .SelectMany(row =>
                new[]
                {
                    row.GetProperty("anchorBucketId").GetString(),
                    row.GetProperty("candidateBucketId").GetString(),
                }
            )
            .ToHashSet();
        var members = occurrences
            .Where(row => supportedBuckets.Contains(row.GetProperty("bucketId").GetString()))
            .ToArray();
        members
            .Select(row => row.GetProperty("person").GetProperty("sourceUuid").GetGuid())
            .Distinct()
            .Should()
            .HaveCount(231);
        members
            .Select(row => row.GetProperty("person").GetProperty("artistUuid").GetGuid())
            .Distinct()
            .Should()
            .HaveCount(2);
        members
            .Should()
            .OnlyContain(row =>
                row.GetProperty("creditKeys").GetArrayLength() > 0
                || row.GetProperty("person").GetProperty("aliases").GetArrayLength() > 0
            );
        hypotheses
            .Should()
            .OnlyContain(row =>
                !row.GetProperty("acceptedIdentity").GetBoolean()
                && !row.GetProperty("qualifiedForAutomaticAcceptance").GetBoolean()
            );
        using var manifest = JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(output, "manifest.json"))
        );
        manifest.RootElement.GetProperty("state").GetString().Should().Be("completed");
        manifest.RootElement.GetProperty("sourceCount").GetInt32().Should().Be(233);
        var replay = () => new SharedPeopleExport(0).RunAsync(run, output);
        await replay.Should().ThrowAsync<IOException>();
    }

    [Test]
    public async Task TitleOnlyProducerUpgradePreservesTheSameSourceActorsAndSupport()
    {
        var run = await SourceRun([
            Input(Guid.NewGuid(), "Pat Myers (pat@example.test)"),
            Input(Guid.NewGuid(), "Pat Myers (pat@example.test)"),
        ]);
        var originalOutput = Path.Combine(directory, "original-title");
        await new SharedPeopleExport(0).RunAsync(run, originalOutput);

        var manifestPath = Path.Combine(run, "manifest.json");
        var manifest = System.Text.Json.Nodes.JsonNode.Parse(
            await File.ReadAllTextAsync(manifestPath)
        )!;
        var producer = manifest["methods"]!["CatalogRowExtraction"]!.GetValue<string>();
        manifest["methods"]!["CatalogRowExtraction"] =
            producer[..(producer.LastIndexOf('/') + 1)] + "track-title-unrelated-version";
        await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString());
        var changedOutput = Path.Combine(directory, "changed-title");
        await new SharedPeopleExport(0).RunAsync(run, changedOutput);

        foreach (var output in new[] { "occurrences", "buckets", "hypotheses" })
        {
            var original = await Rows(originalOutput, output);
            var changed = await Rows(changedOutput, output);
            changed
                .Select(row => row.GetRawText())
                .Should()
                .Equal(original.Select(row => row.GetRawText()));
        }
    }

    [Test]
    public async Task ConflictingKindsAndUncertainAttachmentsRemainUnsupported()
    {
        var run = await SourceRun([
            Input(Guid.NewGuid(), "Pat Myers (a person) (pat@example.test)"),
            Input(Guid.NewGuid(), "Pat Myers (recording group) (pat@example.test)"),
            Input(Guid.NewGuid(), "Pat Myers (pat@example.test) aka A and B"),
        ]);
        var output = Path.Combine(directory, "conflicts");
        await new SharedPeopleExport(0).RunAsync(run, output);
        var edges = await Rows(output, "hypotheses");
        edges.Should().Contain(row => row.GetProperty("hasConflict").GetBoolean());
        edges.Should().Contain(row => row.GetProperty("uncertain").GetBoolean());
        edges.Should().OnlyContain(row => !row.GetProperty("supported").GetBoolean());
    }

    [TestCase("name")]
    [TestCase("contact")]
    public async Task ReconstructedActorsAndLogicalPopulationMustMatchBeforePublication(
        string changed
    )
    {
        var run = await SourceRun([Input(Guid.NewGuid(), "Pat Myers (pat@example.test)")]);
        var path = Directory.GetFiles(Path.Combine(run, "people"), "*.gz").Single();
        var original = (await Rows(run, "people/source")).Single();
        var node = System.Text.Json.Nodes.JsonNode.Parse(original.GetRawText())!;
        if (changed == "name")
        {
            node["proposal"]!["people"]![0]!["name"] = "Altered name";
        }
        else
        {
            node["evidence"]!["creditActors"]![0]!["actorFacts"]!["contacts"]![0]!["lookupKey"] =
                "invented@example.test";
        }
        await Gzip(path, [node.ToJsonString()]);
        var output = Path.Combine(directory, "tampered");
        var action = () => new SharedPeopleExport(0).RunAsync(run, output);
        await action.Should().ThrowAsync<InvalidDataException>();
        using var manifest = JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(output, "manifest.json"))
        );
        manifest.RootElement.GetProperty("state").GetString().Should().Be("interrupted");
    }

    private ShadowCatalogInput Input(Guid artist, string taper)
    {
        var row = JsonSerializer.SerializeToElement(
            new
            {
                kind = "source_sample",
                row = new
                {
                    id = 1,
                    uuid = Guid.NewGuid(),
                    artist_id = 1,
                    taper,
                },
            },
            Json
        );
        return new(
            row,
            new CatalogRowExtraction().ExtractJson(row, artist),
            new(1, artist, artist.ToString(), []),
            "catalog:test"
        );
    }

    private Task<string> SourceRun(IReadOnlyList<ShadowCatalogInput> inputs) =>
        WriteSourceRun(
            directory,
            inputs,
            new ShadowPeopleAdapter(null, null, sourceObservationsOnly: true)
        );

    internal static async Task<string> WriteSourceRun(
        string directory,
        IReadOnlyList<ShadowCatalogInput> inputs,
        ShadowPeopleAdapter adapter
    )
    {
        var run = Path.Combine(directory, "source-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(run, "people"));
        var rows = inputs.Select(input =>
            adapter.Process(input).Single(result => result.Domain == "people")
        );
        await Gzip(
            Path.Combine(run, "people", "source.jsonl.gz"),
            rows.Select(row => JsonSerializer.Serialize(row, Json))
        );
        await File.WriteAllTextAsync(
            Path.Combine(run, "manifest.json"),
            JsonSerializer.Serialize(
                new
                {
                    state = "completed",
                    population = new { processingScope = "source_actor_observations" },
                    methods = new Dictionary<string, string>
                    {
                        ["ShadowPeopleAdapter"] = adapter.Version,
                        ["CatalogRowExtraction"] = string.Join(
                            "/",
                            "catalog-row-inline-v1",
                            RecordingCreditExtractor.Version,
                            SourceActorAliasExtractor.Version,
                            TrackTitleExtractor.Version
                        ),
                    },
                    counts = inputs
                        .GroupBy(input => input.Provider)
                        .Select(group => new
                        {
                            domain = "people",
                            provider = group.Key,
                            processed = group.Count(),
                        }),
                },
                Json
            )
        );
        return run;
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

    private static async Task<JsonElement[]> Rows(string run, string name)
    {
        await using var file = File.OpenRead(Path.Combine(run, name + ".jsonl.gz"));
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        var rows = new List<JsonElement>();
        while (await reader.ReadLineAsync() is { } line)
        {
            using var document = JsonDocument.Parse(line);
            rows.Add(document.RootElement.Clone());
        }
        return rows.ToArray();
    }
}
