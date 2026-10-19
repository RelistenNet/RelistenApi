using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization.Batch;

namespace RelistenApiTests.Normalization;

/// <summary>
/// Tests the operator-file boundary with captured catalog identities and fake durable
/// transport. Search planning and provider decoding retain their own focused tests.
/// </summary>
[TestFixture]
public sealed class TestShadowCompositionAcquisitionBatch
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Guid ArtistMbid = Guid.Parse("e01646f2-2a04-450d-8bf2-0d993082e058");
    private static readonly Guid SongUuid = Guid.Parse("6f20eb1e-2094-368b-4227-a5b2a753d80b");

    // Projected columns from full-raw capture 0013-songs-1919-34812-attempt-001.jsonl.gz,
    // SHA256 c67f3fdd59e33e5ff0fa82d30cc6306efa4d27484db36ffde4ca54cefd96f91f.
    // These are native Phish/Tweezer identities, not generated normalization identities.
    private const string CatalogRows = """
        {"kind":"artist","row":{"id":4,"uuid":"ca53d281-0041-ae33-a050-c87702d93b0c","name":"Phish","musicbrainz_id":"e01646f2-2a04-450d-8bf2-0d993082e058"}}
        {"kind":"song_sample","row":{"id":33846,"uuid":"6f20eb1e-2094-368b-4227-a5b2a753d80b","artist_id":4,"name":"Tweezer"}}
        """;

    private string directory = null!;

    [SetUp]
    public void SetUp()
    {
        directory = Path.Combine(
            Path.GetTempPath(),
            "shadow-composition-batch-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(directory);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(directory, recursive: true);

    [Test]
    public async Task PinnedCompressedCatalogRetainsPerformerQualificationWithoutAssigningWorkAuthorship()
    {
        var requestPath = await WriteRequestAsync();
        var client = new DurableClient();

        var result = await new ShadowCompositionAcquisitionBatch(client).RunAsync(
            requestPath,
            Output
        );

        var recording = client.Gets.Single(command =>
            command.Search?.Query.Kind == MusicBrainzLookupKind.Recording
        );
        recording.Search!.Query.Text.Should().Be("Tweezer");
        recording
            .Search.Query.RelatedArtist.Should()
            .Be(
                new MusicBrainzSearchArtistConstraint(
                    MusicBrainzSearchArtistUse.RecordingPerformer,
                    ExternalArtistMbid: ArtistMbid
                )
            );
        var work = client.Gets.Single(command =>
            command.Search?.Query.Kind == MusicBrainzLookupKind.Work
        );
        work.Search!.Query.Text.Should().Be("Tweezer");
        work.Search.Query.RelatedArtist.Should().BeNull();
        result
            .Commands.Should()
            .OnlyContain(command => command.CatalogUuids.SequenceEqual(new[] { SongUuid }));
        result.PublicationPermitted.Should().BeFalse();
        result.ExternalUniverseComplete.Should().BeFalse();
        client.Advances.Should().Be(0, "terminal durable receipts need no provider dispatch");
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ChangedLateOperatorFilePreventsEvenReadOnlyClientConsultation(
        bool originalCommands
    )
    {
        var latePath = Path.Combine(
            directory,
            originalCommands ? "original-commands.json" : "retained-input.json"
        );
        var revision = EmptySearchInput(
            new("musicbrainz.org", "work-search", "retained-fixture"),
            MusicBrainzLookupKind.Work
        );
        await File.WriteAllBytesAsync(
            latePath,
            originalCommands
                ? JsonSerializer.SerializeToUtf8Bytes(
                    Array.Empty<ShadowCompositionAcquisitionCommand>(),
                    Json
                )
                : JsonSerializer.SerializeToUtf8Bytes(revision, Json)
        );
        var late = await PinAsync(latePath);
        var requestPath = await WriteRequestAsync(
            originalCommands ? [] : [late],
            originalCommands ? late : null
        );
        await File.AppendAllTextAsync(latePath, "\n");
        var client = new DurableClient();

        var run = () => new ShadowCompositionAcquisitionBatch(client).RunAsync(requestPath, Output);

        await run.Should().ThrowAsync<InvalidDataException>().WithMessage("*file changed*");
        client.Gets.Should().BeEmpty();
        client.Advances.Should().Be(0);
        client.InputReads.Should().Be(0);
        Directory.Exists(Output).Should().BeFalse();
    }

    [Test]
    public async Task ChangedProgramReferencesRefuseBeforeContextParsingOrClientConsultation()
    {
        // The selection plan is deliberately unparseable as a selection contract.
        // Complete file authentication must reject the changed final pin first.
        var selectionPath = Path.Combine(directory, "selection-plan.json");
        await File.WriteAllTextAsync(selectionPath, "{}");
        var referencesPath = Path.Combine(directory, "program-references.json");
        await File.WriteAllTextAsync(referencesPath, "[]");
        var sources = new CompositionProgramReferenceSources(
            Guid.NewGuid(),
            await PinAsync(selectionPath),
            await PinAsync(referencesPath)
        );
        var requestPath = await WriteRequestAsync(programSources: sources);
        await File.AppendAllTextAsync(referencesPath, "\n");
        var client = new DurableClient();

        var run = () => new ShadowCompositionAcquisitionBatch(client).RunAsync(requestPath, Output);

        await run.Should().ThrowAsync<InvalidDataException>().WithMessage("*file changed*");
        client.Gets.Should().BeEmpty();
        client.Advances.Should().Be(0);
        client.InputReads.Should().Be(0);
        Directory.Exists(Output).Should().BeFalse();
    }

    [Test]
    public async Task CompletedCacheExportsExactInputBytesAndReplaysWithoutAnotherClientRead()
    {
        var requestPath = await WriteRequestAsync();
        var client = new DurableClient(returnCompletedCache: true);
        var batch = new ShadowCompositionAcquisitionBatch(client);

        var result = await batch.RunAsync(requestPath, Output);

        result.State.Should().Be("plan_exhausted");
        result.ReservedServiceCalls.Should().Be(0);
        result.Inputs.Should().HaveCount(2);
        foreach (var saved in result.Inputs)
        {
            var exported = JsonSerializer.Deserialize<NormalizationInputRevision>(
                await File.ReadAllBytesAsync(Path.Combine(Output, saved.File)),
                Json
            )!;
            var original = client.Inputs[saved.Uuid];
            exported.Uuid.Should().Be(original.Uuid);
            exported.Resource.Should().Be(original.Resource);
            exported.Payload.Should().Equal(original.Payload);
            exported.Sha256.Should().Be(original.Sha256);
        }
        var getCount = client.Gets.Count;
        var readCount = client.InputReads;

        var replay = await batch.RunAsync(requestPath, Output);

        replay.Inputs.Should().BeEquivalentTo(result.Inputs);
        replay.PublicationPermitted.Should().BeFalse();
        client.Gets.Should().HaveCount(getCount);
        client.InputReads.Should().Be(readCount);
        client.Advances.Should().Be(0);
    }

    private string Output => Path.Combine(directory, "output");

    private async Task<string> WriteRequestAsync(
        IReadOnlyList<ShadowCompositionAcquisitionFile>? retained = null,
        ShadowCompositionAcquisitionFile? originalCommands = null,
        CompositionProgramReferenceSources? programSources = null
    )
    {
        var catalogPath = Path.Combine(directory, "catalog.jsonl.gz");
        await using (var file = File.Create(catalogPath))
        await using (var gzip = new GZipStream(file, CompressionMode.Compress))
        await using (var writer = new StreamWriter(gzip, new UTF8Encoding(false)))
        {
            await writer.WriteLineAsync(CatalogRows);
        }
        var request = new ShadowCompositionAcquisitionRequest(
            [await PinAsync(catalogPath)],
            retained ?? [],
            new(0, 0, TimeSpan.FromMinutes(1)),
            originalCommands,
            programSources
        );
        var requestPath = Path.Combine(directory, "request.json");
        await File.WriteAllBytesAsync(
            requestPath,
            JsonSerializer.SerializeToUtf8Bytes(request, Json)
        );
        return requestPath;
    }

    private static async Task<ShadowCompositionAcquisitionFile> PinAsync(string path)
    {
        await using var file = File.OpenRead(path);
        return new(
            Path.GetFileName(path),
            Convert.ToHexStringLower(await SHA256.HashDataAsync(file))
        );
    }

    private static NormalizationInputRevision EmptySearchInput(
        NormalizationResourceKey resource,
        MusicBrainzLookupKind kind
    )
    {
        var member = kind == MusicBrainzLookupKind.Work ? "works" : "recordings";
        var bytes = Encoding.UTF8.GetBytes(
            "{ \"count\": 0, \"offset\": 0, \"" + member + "\": [] }\n"
        );
        return new()
        {
            Uuid = Guid.NewGuid(),
            Resource = resource,
            CaptureVersion =
                kind == MusicBrainzLookupKind.Work
                    ? "musicbrainz-work-search-json-v1"
                    : "musicbrainz-recording-search-json-v1",
            MediaType = "application/json",
            Payload = bytes,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
        };
    }

    /// <summary>Returns durable results; an accidental dispatch always fails this test.</summary>
    private sealed class DurableClient(bool returnCompletedCache = false)
        : IShadowCompositionAcquisitionClient
    {
        internal List<ShadowCompositionAcquisitionCommand> Gets { get; } = [];
        internal Dictionary<Guid, NormalizationInputRevision> Inputs { get; } = [];
        internal int Advances { get; private set; }
        internal int InputReads { get; private set; }

        public Task<ShadowCompositionAcquisitionReceipt?> GetAsync(
            ShadowCompositionAcquisitionCommand command,
            CancellationToken ct
        )
        {
            Gets.Add(command);
            if (!returnCompletedCache)
            {
                return Task.FromResult<ShadowCompositionAcquisitionReceipt?>(
                    new(
                        command.CommandUuid,
                        MusicBrainzAcquisitionState.Failed,
                        null,
                        new(
                            MusicBrainzAcquisitionDisposition.Failed,
                            Reason: "retained_terminal_fixture"
                        ),
                        [],
                        JsonSerializer.SerializeToElement(new { state = "failed" })
                    )
                );
            }

            var input = EmptySearchInput(command.Resource, command.Search!.Query.Kind);
            Inputs.Add(input.Uuid, input);
            return Task.FromResult<ShadowCompositionAcquisitionReceipt?>(
                new(
                    command.CommandUuid,
                    MusicBrainzAcquisitionState.Completed,
                    null,
                    new(MusicBrainzAcquisitionDisposition.Cached, new(input.Uuid, input.Resource)),
                    [],
                    JsonSerializer.SerializeToElement(
                        new { state = "completed", inputUuid = input.Uuid }
                    )
                )
            );
        }

        public Task<ShadowCompositionAcquisitionReceipt> AdvanceAsync(
            ShadowCompositionAcquisitionCommand command,
            CancellationToken ct
        )
        {
            Advances++;
            throw new AssertionException(
                "A completed or terminal cache fixture must not dispatch provider work."
            );
        }

        public Task<NormalizationInputRevision?> ReadInputAsync(
            Guid inputUuid,
            CancellationToken ct
        )
        {
            InputReads++;
            return Task.FromResult(Inputs.GetValueOrDefault(inputUuid));
        }
    }
}
