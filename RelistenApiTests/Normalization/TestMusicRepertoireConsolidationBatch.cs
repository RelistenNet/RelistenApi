using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;
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
                .TryGetProperty("knowledge", out _)
                .Should()
                .BeFalse();
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
        var legacyCompletion = JsonSerializer.SerializeToElement(completion, Json);
        legacyCompletion.TryGetProperty("knowledgeSha256", out _).Should().BeFalse();
        legacyCompletion.TryGetProperty("knowledgeSnapshot", out _).Should().BeFalse();
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

    [Test]
    public async Task ReviewedSnapshotChangesTheTargetWhileRetainingRawOwnershipAndSupportsReplayAndCheckpointResume()
    {
        var fixture = await Export(2);
        var snapshot = ExpertKnowledgeResolver.CreateSnapshot([ReviewedRule(fixture)]);
        var knowledge = await WriteKnowledge("accepted-knowledge.json", snapshot);
        var output = Path.Combine(directory, "reviewed");
        var batch = new MusicRepertoireConsolidationBatch();
        var first = await batch.RunAsync(
            fixture.Manifest,
            output,
            fixture.ManifestSha,
            knowledgePath: knowledge.Path,
            knowledgeSha256: knowledge.Sha
        );
        var rows = await Results(output, first);
        var proposal = rows.Single(row =>
                row.GetProperty("proposal").GetProperty("originalKey").GetString()
                == fixture.Descriptors[0].OriginalKey
            )
            .GetProperty("proposal");

        proposal
            .GetProperty("originalKey")
            .GetString()
            .Should()
            .Be(fixture.Descriptors[0].OriginalKey);
        proposal.GetProperty("originalTitle").GetString().Should().Be(fixture.Descriptors[0].Title);
        proposal
            .GetProperty("sourceWitness")
            .Deserialize<MusicTitleWitness>(Json)
            .Should()
            .Be(fixture.Descriptors[0].Witness);
        var target = proposal.GetProperty("targets")[0];
        target
            .GetProperty("song")
            .GetProperty("entityKey")
            .GetString()
            .Should()
            .Be(fixture.Names[1].SongKey);
        target
            .GetProperty("knowledge")[0]
            .GetProperty("snapshotSha256")
            .GetString()
            .Should()
            .Be(snapshot.Sha256);
        target
            .GetProperty("comparisonDiagnostics")[0]
            .GetProperty("comparison")
            .GetProperty("candidateUuid")
            .GetGuid()
            .Should()
            .Be(fixture.Names[0].LegacySongUuid);
        first.KnowledgeSha256.Should().Be(snapshot.Sha256);
        first.KnowledgeSnapshot!.Sha256.Should().Be(knowledge.Sha);
        var retained = Files(output);
        var replay = await batch.RunAsync(
            fixture.Manifest,
            output,
            fixture.ManifestSha,
            resume: true,
            knowledgePath: knowledge.Path,
            knowledgeSha256: knowledge.Sha
        );
        replay.Should().BeEquivalentTo(first);
        Files(output).Should().BeEquivalentTo(retained);

        File.Delete(Path.Combine(output, "manifest.json"));
        var resumed = await batch.RunAsync(
            fixture.Manifest,
            output,
            fixture.ManifestSha,
            resume: true,
            knowledgePath: knowledge.Path,
            knowledgeSha256: knowledge.Sha
        );

        resumed.Complete.Should().BeTrue();
        resumed.LogicalRows.Should().Be(2);
        resumed.KnowledgeSha256.Should().Be(snapshot.Sha256);
        foreach (var chunk in first.Chunks)
        {
            Hash(await File.ReadAllBytesAsync(Path.Combine(output, chunk.Path)))
                .Should()
                .Be(retained[chunk.Path]);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task AChangedReviewedSnapshotCannotReplayACompletedRunOrResumeItsCheckpoint(
        bool pendingCheckpoint
    )
    {
        var fixture = await Export(2);
        var rule = ReviewedRule(fixture);
        var snapshot = ExpertKnowledgeResolver.CreateSnapshot([rule]);
        var original = await WriteKnowledge("original-knowledge.json", snapshot);
        var output = Path.Combine(directory, "changed-review");
        var batch = new MusicRepertoireConsolidationBatch();
        await batch.RunAsync(
            fixture.Manifest,
            output,
            fixture.ManifestSha,
            knowledgePath: original.Path,
            knowledgeSha256: original.Sha
        );
        if (pendingCheckpoint)
        {
            File.Delete(Path.Combine(output, "manifest.json"));
        }
        var changed = ExpertKnowledgeResolver.CreateSnapshot([
            rule with
            {
                State = "retracted",
                Revision = 2,
                LastOperationUuid = Guid.NewGuid(),
            },
        ]);
        var replacement = await WriteKnowledge("retracted-knowledge.json", changed);
        replacement.Sha.Should().NotBe(original.Sha);
        var retained = Files(output);
        var replay = () =>
            batch.RunAsync(
                fixture.Manifest,
                output,
                fixture.ManifestSha,
                resume: true,
                knowledgePath: replacement.Path,
                knowledgeSha256: replacement.Sha
            );

        await replay.Should().ThrowAsync<InvalidDataException>();
        Files(output).Should().BeEquivalentTo(retained);
    }

    private static ExpertKnowledgeRule ReviewedRule(ExportFixture fixture) =>
        new(
            Guid.NewGuid(),
            1,
            "accepted",
            new(
                "songs",
                new(
                    "repertoire",
                    AccountStableKey: ReviewedRepertoireCorrespondences.StableKey(
                        fixture.Descriptors[0].Scope,
                        fixture.Descriptors[0].OriginalKey
                    ),
                    ArtistUuid: Artist
                ),
                new(
                    "songs",
                    "repertoire",
                    ReviewedRepertoireCorrespondences.StableKey(
                        fixture.Descriptors[0].Scope,
                        fixture.Names[1].SongKey!
                    )
                ),
                [
                    new(
                        fixture.Input.Descriptors.Path,
                        fixture.Input.Descriptors.Bytes,
                        fixture.Input.Descriptors.Sha256
                    ),
                ],
                "Synthetic exact descriptor account review against the frozen source descriptor artifact."
            ),
            Guid.NewGuid(),
            new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc)
        );

    [TestCase(false)]
    [TestCase(true)]
    public async Task LiveSongCorrectionsReuseSharedPolicyAndWithdrawalRestoresPinnedBaseline(
        bool reviewedBaseline
    )
    {
        var fixture = await Export(2);
        var rule = ReviewedRule(fixture);
        var accepted = ExpertKnowledgeResolver.CreateSnapshot([rule]);
        var withdrawn = ExpertKnowledgeResolver.CreateSnapshot([
            rule with
            {
                State = "retracted",
                Revision = 2,
            },
        ]);
        var retained = await WriteKnowledge("live-baseline.json", accepted);
        var output = Path.Combine(directory, "live-projection");
        await new MusicRepertoireConsolidationBatch().RunAsync(
            fixture.Manifest,
            output,
            fixture.ManifestSha,
            knowledgePath: reviewedBaseline ? retained.Path : null,
            knowledgeSha256: reviewedBaseline ? retained.Sha : null
        );
        var outputPath = Path.Combine(output, "manifest.json");
        var projection = await ReviewedMusicProjection.OpenAsync(
            fixture.Manifest,
            fixture.ManifestSha,
            outputPath,
            Hash(await File.ReadAllBytesAsync(outputPath))
        );
        var first = await projection.ReadAsync(accepted);
        var restored = await projection.ReadAsync(withdrawn);
        if (reviewedBaseline)
        {
            first.Items.Should().BeEmpty();
            restored
                .Items.Should()
                .ContainSingle()
                .Which.Targets.Should()
                .ContainSingle()
                .Which.Song.EntityKey.Should()
                .Be(fixture.Names[0].SongKey);
        }
        else
        {
            first
                .Items.Should()
                .ContainSingle()
                .Which.Targets.Should()
                .ContainSingle()
                .Which.Song.EntityKey.Should()
                .Be(fixture.Names[1].SongKey);
            restored.Items.Should().BeEmpty();
        }
        first.KnowledgeSha256.Should().Be(accepted.Sha256);
        restored.KnowledgeSha256.Should().Be(withdrawn.Sha256);
        first
            .Items.All(change =>
                fixture.Descriptors.Any(value => value.OriginalKey == change.OriginalKey)
            )
            .Should()
            .BeTrue();
        await File.AppendAllTextAsync(fixture.Input.Descriptors.Path, "changed");
        var changed = () => projection.ReadAsync(withdrawn);
        await changed.Should().ThrowAsync<IOException>();
    }

    [Test]
    public async Task LiveProjectionPagesKeepEveryLongTitleWithinTheReaderResponseBound()
    {
        var fixture = await Export(101, " " + new string('x', 100_000));
        var original = ReviewedRule(fixture);
        var template = original with
        {
            Definition = original.Definition with
            {
                Evidence =
                [
                    new(
                        fixture.Manifest,
                        new FileInfo(fixture.Manifest).Length,
                        fixture.ManifestSha
                    ),
                ],
            },
        };
        var snapshot = ExpertKnowledgeResolver.CreateSnapshot(
            fixture
                .Descriptors.Select(descriptor =>
                    template with
                    {
                        RuleUuid = Guid.NewGuid(),
                        Definition = template.Definition with
                        {
                            Selector = template.Definition.Selector with
                            {
                                AccountStableKey = ReviewedRepertoireCorrespondences.StableKey(
                                    descriptor.Scope,
                                    descriptor.OriginalKey
                                ),
                            },
                        },
                    }
                )
                .ToArray()
        );
        var output = Path.Combine(directory, "bounded-live-projection");
        await new MusicRepertoireConsolidationBatch().RunAsync(
            fixture.Manifest,
            output,
            fixture.ManifestSha
        );
        var outputPath = Path.Combine(output, "manifest.json");
        var projection = await ReviewedMusicProjection.OpenAsync(
            fixture.Manifest,
            fixture.ManifestSha,
            outputPath,
            Hash(await File.ReadAllBytesAsync(outputPath))
        );
        var seen = new List<string>();
        var page = await projection.ReadAsync(snapshot);
        page.Items.Count.Should().BeLessThan(100).And.BeGreaterThan(0);
        while (seen.Count < page.Total)
        {
            JsonSerializer
                .SerializeToUtf8Bytes(page, Json)
                .Length.Should()
                .BeLessThanOrEqualTo(7 * 1024 * 1024);
            seen.AddRange(page.Items.Select(change => change.OriginalKey));
            if (seen.Count < page.Total)
            {
                page = await projection.ReadAsync(snapshot, seen.Count);
            }
        }
        seen.Should().OnlyHaveUniqueItems();
        seen.Should()
            .BeEquivalentTo(fixture.Descriptors.Select(descriptor => descriptor.OriginalKey));
    }

    private async Task<(string Path, string Sha)> WriteKnowledge(
        string name,
        ExpertKnowledgeSnapshot snapshot
    )
    {
        var path = Path.Combine(directory, name);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, Json);
        await File.WriteAllBytesAsync(path, bytes);
        return (path, Hash(bytes));
    }

    private async Task<ExportFixture> Export(int count, string titleSuffix = "")
    {
        var root = Path.Combine(directory, "input-" + Guid.NewGuid().ToString("N"));
        var sourceRun = Path.Combine(root, "captured-source");
        var sourceChunk = Path.Combine(sourceRun, "repertoire", "000001.jsonl.gz");
        Directory.CreateDirectory(Path.GetDirectoryName(sourceChunk)!);
        var sourceRows = Enumerable
            .Range(0, count)
            .Select(index => SourceResult(index, "09.Song " + index + titleSuffix, "file_sample"))
            .Concat(
                Enumerable
                    .Range(0, count)
                    .Select(index =>
                        SourceResult(index, "Song " + index + titleSuffix, "repertoire_song")
                    )
            );
        await Gzip(sourceChunk, sourceRows.Select(row => JsonSerializer.Serialize(row, Json)));
        var sourceSha = Hash(await File.ReadAllBytesAsync(sourceChunk));
        var descriptors = Enumerable
            .Range(0, count)
            .Select(index => new MusicRepertoireDescriptor(
                "fixture:catalogue",
                Artist,
                ShadowRepertoireAdapter.ProvisionalEntityKey(
                    Artist,
                    "09.Song " + index + titleSuffix
                ),
                "09.Song " + index + titleSuffix,
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
                    "Song " + index + titleSuffix,
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
