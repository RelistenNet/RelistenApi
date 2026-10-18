using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;
using Relisten.Normalization.Batch;

namespace RelistenApiTests.Normalization;

/// <summary>New proposal output is explicit; existing extract and view contracts retain their inputs.</summary>
[TestFixture]
public sealed class TestSourceActorExtractionBatch
{
    private string directory = null!;
    private string inputPath = null!;
    private static readonly Guid Artist = Guid.Parse("4b50d2ec-6d47-4285-8a4d-b2a2aad9d1b0");
    private static readonly Guid Source = Guid.Parse("f43fd245-4241-4458-9308-cbac631ba3d7");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    [SetUp]
    public void CreateWorkspace()
    {
        directory = Path.Combine(Path.GetTempPath(), $"source-actor-batch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        inputPath = Path.Combine(directory, "snapshot.jsonl");
    }

    [TearDown]
    public void RemoveWorkspace() => Directory.Delete(directory, recursive: true);

    [Test]
    public async Task ExplicitSourceActorModeRetainsAllAliasProposalsAndFieldCoverage()
    {
        const string raw =
            "The Band:\nAndy Moresi aka Hot Dog - Keyboard/Vocals\n"
            + "Bonus material:\nThe Chimney Tops (aka Pilotdrift)";
        await WriteCapture(raw);

        var report = await new NormalizationExtractionBatch(
            includeSourceActorAliases: true
        ).RunAsync(inputPath, Path.Combine(directory, "new"));
        var row = await ReadRow("new");

        report.State.Should().Be("completed");
        report
            .ExtractorVersions.Should()
            .ContainKey("source_actor_aliases")
            .WhoseValue.Should()
            .Be(SourceActorAliasExtractor.Version);
        row.ResourceUuid.Should().Be(Source);
        row.ArtistUuid.Should().Be(Artist);
        row.Inputs["description"].Should().Be(raw);
        row.CreditExtraction!.Credits.Should().BeEmpty();
        var aliases = row.SourceActorAliases!;
        aliases.Assertions.Should().ContainSingle().Which.Name.Should().Be("Andy Moresi");
        aliases
            .Assertions.Single()
            .Aliases.Should()
            .ContainSingle()
            .Which.Wording.Should()
            .Be("Hot Dog");
        aliases
            .Unresolved.Should()
            .ContainSingle()
            .Which.Evidence.Single()
            .Quote.Should()
            .Be("The Chimney Tops (aka Pilotdrift)");
        aliases.Scopes.Single(scope => scope.Field == "description").Complete.Should().BeFalse();
        aliases.Scopes.Single(scope => scope.Field == "taper").Complete.Should().BeFalse();
        var reproduced = new SourceActorAliasExtractor().Extract(row.Inputs);
        JsonElement
            .DeepEquals(
                JsonSerializer.SerializeToElement(aliases),
                JsonSerializer.SerializeToElement(reproduced)
            )
            .Should()
            .BeTrue();
    }

    [Test]
    public async Task ExistingExtractModeKeepsItsPayloadAndRunIdentity()
    {
        const string raw = "The Band:\nAndy Moresi aka Hot Dog - Keyboard/Vocals";
        await WriteCapture(raw);
        var old = await new NormalizationExtractionBatch().RunAsync(
            inputPath,
            Path.Combine(directory, "old")
        );
        var explicitOld = await new NormalizationExtractionBatch(
            includeSourceActorAliases: false
        ).RunAsync(inputPath, Path.Combine(directory, "explicit-old"));
        var added = await new NormalizationExtractionBatch(
            includeSourceActorAliases: true
        ).RunAsync(inputPath, Path.Combine(directory, "new"));
        var oldRow = await ReadRow("old");
        var newRow = await ReadRow("new");

        oldRow.SourceActorAliases.Should().BeNull();
        old.ExtractorVersions.Should().NotContainKey("source_actor_aliases");
        old.RunIdentitySha256.Should().Be(explicitOld.RunIdentitySha256);
        added.RunIdentitySha256.Should().NotBe(old.RunIdentitySha256);
        added.InputSha256.Should().Be(old.InputSha256);
        (await File.ReadAllBytesAsync(Output("old")))
            .Should()
            .Equal(await File.ReadAllBytesAsync(Output("explicit-old")));
        var legacyPayload = new
        {
            oldRow.Kind,
            oldRow.ResourceId,
            oldRow.ResourceUuid,
            oldRow.ArtistId,
            oldRow.ArtistUuid,
            oldRow.Family,
            oldRow.Inputs,
            oldRow.TrackContext,
            oldRow.CreditExtraction,
            oldRow.TitleExtraction,
        };
        JsonSerializer
            .Serialize(oldRow, Json)
            .Should()
            .Be(JsonSerializer.Serialize(legacyPayload, Json));
        JsonSerializer
            .Serialize(newRow with { SourceActorAliases = null }, Json)
            .Should()
            .Be(JsonSerializer.Serialize(oldRow, Json));
    }

    [Test]
    public async Task NewModeIsDeterministicAndDoesNotPublishTruncatedCaptures()
    {
        await WriteCapture("Personnel:\nZoë aka Lark - vocals");
        var runner = new NormalizationExtractionBatch(includeSourceActorAliases: true);
        var first = await runner.RunAsync(inputPath, Path.Combine(directory, "first"));
        var repeated = await runner.RunAsync(inputPath, Path.Combine(directory, "repeated"));

        first.RunIdentitySha256.Should().Be(repeated.RunIdentitySha256);
        (await File.ReadAllBytesAsync(Output("first")))
            .Should()
            .Equal(await File.ReadAllBytesAsync(Output("repeated")));

        var lines = await File.ReadAllLinesAsync(inputPath);
        await File.WriteAllLinesAsync(inputPath, lines[..^1], new UTF8Encoding(false));
        var incomplete = await runner.RunAsync(inputPath, Path.Combine(directory, "incomplete"));

        incomplete.State.Should().Be("incomplete");
        File.Exists(Path.Combine(directory, "incomplete", "manifest.json")).Should().BeFalse();
        File.Exists(Path.Combine(directory, "incomplete", "extractions.partial.jsonl"))
            .Should()
            .BeTrue();
    }

    private string Output(string output) => Path.Combine(directory, output, "extractions.jsonl");

    private async Task<NormalizationBatchExtraction> ReadRow(string output) =>
        JsonSerializer.Deserialize<NormalizationBatchExtraction>(
            (await File.ReadAllLinesAsync(Output(output))).Single(),
            Json
        )!;

    private Task WriteCapture(string description)
    {
        object[] records =
        [
            new
            {
                kind = "snapshot",
                snapshot = "test:1:",
                read_only = "on",
                in_recovery = true,
            },
            new { kind = "artist", row = new { id = 1, uuid = Artist } },
            new
            {
                kind = "source_sample",
                row = new
                {
                    id = 10,
                    uuid = Source,
                    artist_id = 1,
                    description,
                },
            },
            new { kind = "totals", rows = new { artists = 1 } },
            new { kind = "snapshot_complete", completed_at = "2026-10-17T17:04:06Z" },
        ];
        return File.WriteAllLinesAsync(
            inputPath,
            records.Select(record => JsonSerializer.Serialize(record)),
            new UTF8Encoding(false)
        );
    }
}
