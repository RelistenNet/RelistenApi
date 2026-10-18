using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;
using Relisten.Normalization.Batch;

namespace RelistenApiTests.Normalization;

/// <summary>Joint Local inputs bind historical homes through media, while dates remain evidence.</summary>
[TestFixture]
public sealed class TestRetainedLocalProviderInputs
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Guid Artist = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid Source = Guid.Parse("10000000-0000-0000-0000-000000000002");
    private static readonly Guid Show = Guid.Parse("10000000-0000-0000-0000-000000000003");
    private const string Root = "trey";
    private string directory = null!;

    [SetUp]
    public void SetUp() =>
        directory = Directory.CreateTempSubdirectory("retained-local-tests-").FullName;

    [TearDown]
    public void TearDown() => Directory.Delete(directory, recursive: true);

    [Test]
    public async Task JointRootAndTwoDirectoriesRetainOriginalEvidenceAndOneExistingRecordingHome()
    {
        var fixture = MakeFixture(
            ["夜 / part 1", "part 2"],
            wrongSelectionHint: true,
            missingSibling: true
        );
        var result = await Prepare(fixture);
        var rows = await Rows(result.InputsPath);
        var root = rows.Single(row => row.GetProperty("kind").GetString() == "local_root_input")
            .GetProperty("input")
            .Deserialize<NormalizationInputRevision>(Json)!;
        var provider = rows.Where(row => row.GetProperty("kind").GetString() == "provider_input")
            .ToArray();

        rows.Should().HaveCount(3);
        result.DirectoryRows.Should().Be(2);
        result.BoundDirectories.Should().Be(2);
        result.DistinctBoundSources.Should().Be(1);
        root.Payload.Should()
            .Equal(
                await File.ReadAllBytesAsync(
                    Path.Combine(fixture.RootCapture, "local-root-response.json")
                )
            );
        var adapter = Observing(fixture, rows);
        foreach (var row in provider)
        {
            var revision = row.GetProperty("input").Deserialize<NormalizationInputRevision>(Json)!;
            var retained = row.GetProperty("retained_local")
                .Deserialize<RetainedLocalProviderProvenance>(Json)!;
            retained.RootInputRevisionUuid.Should().Be(root.Uuid);
            retained.Body.Sha256.Should().Be(Hash(revision.Payload));
            revision.Payload.Should().Equal(await File.ReadAllBytesAsync(retained.Body.Path));
            revision
                .Resource.Should()
                .Be(NormalizationProviderResources.LocalDirectory(Root, retained.Directory));
            retained.CatalogBinding.SourceUuid.Should().Be(Source);
            retained.CatalogBinding.ShowUuid.Should().Be(Show);
            retained.CatalogBinding.SelectionMismatch.Should().BeTrue();
            retained.CatalogBinding.ExpectedSourceUuid.Should().NotBe(Source);
            retained
                .CatalogBinding.Files.Where(file => file.Status == "bound")
                .Should()
                .OnlyContain(file =>
                    file.TrackUuid != null
                    && file.SourceSetUuid != null
                    && file.SourceUuid == Source
                    && file.Line > 0
                );
            var performance = Performance(adapter, row);
            performance.Association!.ShowUuid.Should().Be(Show);
            performance
                .Association.LocalCatalogBinding.Should()
                .BeEquivalentTo(retained.CatalogBinding);
            performance.Origin!.Evidence.InputRevisionUuid.Should().Be(root.Uuid);
            performance.Recording!.Origin.Evidence.InputRevisionUuid.Should().Be(revision.Uuid);
            performance.Recording.LiteralDate.Should().BeNull();
            performance
                .Recording.Text.Should()
                .ContainSingle()
                .Which.Text.Value.Should()
                .Be("Directory notes: " + retained.Directory);
        }
        provider
            .SelectMany(row =>
                row.GetProperty("retained_local")
                    .Deserialize<RetainedLocalProviderProvenance>(Json)!
                    .CatalogBinding.Files
            )
            .Should()
            .Contain(file => file.Reason == "missing_directory_media");
    }

    [Test]
    public async Task MatchingDateCannotReplaceAMissingPlayableUrlAnchor()
    {
        var fixture = MakeFixture(["directory"], missingMedia: true);
        var result = await Prepare(fixture);
        var rows = await Rows(result.InputsPath);
        var row = rows.Single(row => row.GetProperty("kind").GetString() == "provider_input");
        var binding = row.GetProperty("retained_local")
            .Deserialize<RetainedLocalProviderProvenance>(Json)!
            .CatalogBinding;

        result.BoundDirectories.Should().Be(0);
        binding.SourceUuid.Should().BeNull();
        binding.Reason.Should().Be("missing_directory_media");
        var performance = Performance(Observing(fixture, rows), row);
        performance.LiteralDate.Should().Be("2020-01-02");
        performance.Association!.ShowUuid.Should().BeNull();
        performance.Association.Candidates.Should().BeEmpty();
        performance.Recording!.Files.Should().ContainSingle();
    }

    [TestCase("duplicateRoot", "multiple_directory_accounts")]
    [TestCase("multipleArtists", "multiple_root_artists")]
    [TestCase("wrongSet", "file_ownership_conflict")]
    [TestCase("multipleSources", "multiple_directory_recordings")]
    [TestCase("emptySourceUuid", "file_ownership_conflict")]
    public async Task AmbiguousOrConflictingOwnershipRetainsInputsWithoutSelectingAShow(
        string fault,
        string reason
    )
    {
        var fixture = MakeFixture(["directory"], fault: fault);
        var result = await Prepare(fixture);
        var rows = await Rows(result.InputsPath);
        var row = rows.Single(row => row.GetProperty("kind").GetString() == "provider_input");
        var retained = row.GetProperty("retained_local")
            .Deserialize<RetainedLocalProviderProvenance>(Json)!;

        result.UnresolvedDirectories.Should().Be(1);
        retained.CatalogBinding.Reason.Should().Be(reason);
        retained.CatalogBinding.SourceUuid.Should().BeNull();
        retained.CatalogBinding.ShowUuid.Should().BeNull();
        Performance(Observing(fixture, rows), row).Association!.ShowUuid.Should().BeNull();
        if (fault == "multipleSources")
        {
            retained.CatalogBinding.CandidateSourceUuids.Should().HaveCount(2);
        }
        if (fault == "wrongSet")
        {
            retained
                .CatalogBinding.Files.Should()
                .ContainSingle()
                .Which.Reason.Should()
                .Be("file_ownership_conflict");
        }
    }

    [Test]
    public async Task ChangedProviderDateRemainsVisibleWithoutMovingTheCapturedHome()
    {
        var fixture = MakeFixture(["directory"], rootDate: "2020-01-03");
        var result = await Prepare(fixture);
        var rows = await Rows(result.InputsPath);
        var row = rows.Single(row => row.GetProperty("kind").GetString() == "provider_input");

        var performance = Performance(Observing(fixture, rows), row);

        performance.LiteralDate.Should().Be("2020-01-03");
        performance.Association!.ShowUuid.Should().Be(Show);
        performance
            .Association.Reason.Should()
            .Be("provider_date_conflicts_with_catalog_assignment");
        performance.Association.LocalCatalogBinding!.SourceUuid.Should().Be(Source);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ChangedRootOrDirectoryBodyCannotPublishACompletedAdmission(bool root)
    {
        var fixture = MakeFixture(["directory"]);
        var path = root
            ? Path.Combine(fixture.RootCapture, "local-root-response.json")
            : Path.Combine(fixture.DirectoryCapture, "responses", "0000.json");
        await File.AppendAllTextAsync(path, " ");
        var target = Path.Combine(directory, "failed");
        var run = () =>
            new RetainedLocalProviderInputs().PrepareAsync(
                new(fixture.Catalog, fixture.RootCapture, fixture.DirectoryCapture),
                target
            );

        await run.Should().ThrowAsync<InvalidDataException>();
        File.Exists(Path.Combine(target, "descriptor.json")).Should().BeFalse();
    }

    [Test]
    public async Task InternallyAuthenticDifferentRootCannotReplaceTheSelectionsPinnedRoot()
    {
        var fixture = MakeFixture(["directory"]);
        var bodyPath = Path.Combine(fixture.RootCapture, "local-root-response.json");
        var body = JsonNode.Parse(await File.ReadAllBytesAsync(bodyPath))!;
        body["data"]!["2020-01-02"]![0]!["venue"] = "Different retained venue";
        await File.WriteAllBytesAsync(bodyPath, JsonSerializer.SerializeToUtf8Bytes(body));
        var replacement = Fingerprint(bodyPath);
        var manifestPath = Path.Combine(fixture.RootCapture, "manifest.json");
        var manifest = JsonNode.Parse(await File.ReadAllBytesAsync(manifestPath))!;
        var declaredBody = manifest["files"]!
            .AsArray()
            .Single(value => value!["path"]!.GetValue<string>() == replacement.path)!;
        declaredBody["bytes"] = replacement.bytes;
        declaredBody["sha256"] = replacement.sha256;
        await File.WriteAllBytesAsync(manifestPath, JsonSerializer.SerializeToUtf8Bytes(manifest));
        var target = Path.Combine(directory, "wrong-root");

        var run = () =>
            new RetainedLocalProviderInputs().PrepareAsync(
                new(fixture.Catalog, fixture.RootCapture, fixture.DirectoryCapture),
                target
            );

        await run.Should().ThrowAsync<InvalidDataException>();
        File.Exists(Path.Combine(target, "descriptor.json")).Should().BeFalse();
    }

    private Task<RetainedLocalProviderInputsResult> Prepare(Fixture fixture) =>
        new RetainedLocalProviderInputs().PrepareAsync(
            new(fixture.Catalog, fixture.RootCapture, fixture.DirectoryCapture),
            Path.Combine(directory, Guid.NewGuid().ToString())
        );

    private static async Task<JsonElement[]> Rows(string path) =>
        (await File.ReadAllLinesAsync(path))
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .ToArray();

    private static ShadowPerformanceAdapter Observing(
        Fixture fixture,
        IEnumerable<JsonElement> inputs
    )
    {
        var adapter = new ShadowPerformanceAdapter();
        foreach (var row in fixture.CatalogRows.Concat(inputs))
        {
            adapter.Observe(row);
        }
        return adapter;
    }

    private static ShadowProviderPerformance Performance(
        ShadowPerformanceAdapter adapter,
        JsonElement row
    )
    {
        var input = new ShadowCatalogInput(
            row,
            null,
            new(1, Artist, "Fixture artist", ["local"]),
            "local"
        );
        var result = adapter.Process(input).Single(value => value.Domain == "performances");
        result.Outcome.Should().NotBe("failed");
        return ((IEnumerable<ShadowProviderPerformance>)result.Proposal!)
            .Should()
            .ContainSingle()
            .Which;
    }

    private Fixture MakeFixture(
        string[] names,
        bool wrongSelectionHint = false,
        bool missingSibling = false,
        bool missingMedia = false,
        string? fault = null,
        string rootDate = "2020-01-02"
    )
    {
        var rootCapture = Directory.CreateDirectory(Path.Combine(directory, "root")).FullName;
        var directoryCapture = Directory
            .CreateDirectory(Path.Combine(directory, "directories"))
            .FullName;
        Directory.CreateDirectory(Path.Combine(directoryCapture, "responses"));
        var rootPlan = Write(
            rootCapture,
            "inventory-plan.json",
            new
            {
                provider = "local",
                metadataRoot = Root,
                artistUuid = Artist,
                url = "https://audio.relisten.net/trey/metadata.json",
            }
        );
        var accounts = names
            .Select(name => new
            {
                dir = name,
                date = rootDate,
                venue = "Fixture venue",
                taper_notes = "Root notes: " + name,
            })
            .ToList();
        if (fault == "duplicateRoot")
        {
            accounts.Add(accounts[0]);
        }
        var rootBody = Write(
            rootCapture,
            "local-root-response.json",
            new { data = new Dictionary<string, object> { [rootDate] = accounts } }
        );
        var rootManifest = Write(
            rootCapture,
            "manifest.json",
            new
            {
                version = "native-credit-metadata-inventory-result-v1",
                state = "completed_metadata_root",
                httpStatus = 200,
                planSha256 = rootPlan.sha256,
                files = new[] { rootPlan, rootBody },
            }
        );

        var requests = names
            .Select(
                (name, index) =>
                    new
                    {
                        index,
                        directory = name,
                        url = "https://audio.relisten.net/trey/"
                            + Uri.EscapeDataString(name)
                            + "/metadata.json",
                    }
            )
            .ToArray();
        var plan = Write(
            directoryCapture,
            "selection-plan.json",
            new
            {
                version = "native-local-credit-fields-plan-v1",
                provider = "local",
                metadataRoot = Root,
                artistUuid = Artist,
                inputManifestSha256 = rootManifest.sha256,
                requests,
            }
        );
        var declarations = new List<Artifact>();
        var receipts = new List<object>();
        var hints = new List<object>();
        var catalogRows = new List<JsonElement>();
        var source = SourceRow(1, fault == "emptySourceUuid" ? Guid.Empty : Source);
        var show = Element(
            new
            {
                id = 11,
                uuid = Show,
                artist_id = 1,
                display_date = "2020-01-02",
            }
        );
        catalogRows.Add(
            Element(
                new
                {
                    kind = "source_sample",
                    row = source,
                    show_context = show,
                }
            )
        );
        if (fault == "multipleSources")
        {
            catalogRows.Add(
                Element(
                    new
                    {
                        kind = "source_sample",
                        row = SourceRow(2, Guid.Parse("10000000-0000-0000-0000-000000000006")),
                        show_context = show,
                    }
                )
            );
        }
        var tracks = new List<JsonElement>();
        for (var index = 0; index < names.Length; index++)
        {
            var filenames =
                missingSibling && index == 0 ? new[] { "song.mp3", "missing.mp3" }
                : fault == "multipleSources" ? new[] { "song.mp3", "other.mp3" }
                : new[] { "song.mp3" };
            var response = Write(
                Path.Combine(directoryCapture, "responses"),
                index.ToString("D4") + ".json",
                new
                {
                    tracks = filenames.Select(
                        (filename, position) =>
                            new
                            {
                                mp3 = filename,
                                tags = new
                                {
                                    title = "Fixture song",
                                    track = new { no = position + 1 },
                                },
                            }
                    ),
                    txts = new[] { "Directory notes: " + names[index] },
                }
            );
            declarations.Add(response);
            receipts.Add(
                new
                {
                    index,
                    directory = names[index],
                    requests[index].url,
                    httpStatus = 200,
                    state = "captured",
                    response,
                }
            );
            hints.Add(
                new
                {
                    source_uuid = wrongSelectionHint
                        ? Guid.Parse("10000000-0000-0000-0000-000000000007")
                        : Source,
                    native_responses = new[] { response },
                }
            );
            if (!missingMedia)
            {
                foreach (var filename in filenames.Where(name => name != "missing.mp3"))
                {
                    var selected =
                        filename == "other.mp3" ? catalogRows[1].GetProperty("row") : source;
                    var setId = index + 21;
                    var set = Element(
                        new
                        {
                            id = setId,
                            uuid = Guid.NewGuid(),
                            source_id = fault == "wrongSet"
                                ? 999L
                                : selected.GetProperty("id").GetInt64(),
                        }
                    );
                    tracks.Add(
                        Element(
                            new
                            {
                                kind = "track_sample",
                                row = new
                                {
                                    id = tracks.Count + 100L,
                                    uuid = Guid.NewGuid(),
                                    artist_id = 1,
                                    source_id = selected.GetProperty("id").GetInt64(),
                                    source_set_id = setId,
                                    is_orphaned = false,
                                    mp3_url = $"https://audio.relisten.net/{Root}/{names[index]}/{filename}",
                                },
                                source_context = selected,
                                set_context = set,
                                show_context = show,
                            }
                        )
                    );
                }
            }
        }
        var receiptArtifact = Write(directoryCapture, "request-outcomes.json", receipts);
        var hintsPath = Path.Combine(directoryCapture, "native-fields.jsonl");
        File.WriteAllLines(hintsPath, hints.Select(value => JsonSerializer.Serialize(value, Json)));
        var hintArtifact = Fingerprint(hintsPath);
        Write(
            directoryCapture,
            "manifest.json",
            new
            {
                version = "native-local-credit-fields-result-v1",
                state = "completed_capture_with_explicit_outcomes",
                requestCount = names.Length,
                files = new[] { plan, receiptArtifact, hintArtifact },
                responseArtifacts = declarations,
            }
        );

        var artists = new List<JsonElement> { ArtistRow(1, Artist) };
        if (fault == "multipleArtists")
        {
            artists.Add(ArtistRow(2, Guid.NewGuid()));
        }
        var catalog = Directory.CreateDirectory(Path.Combine(directory, "catalog")).FullName;
        var sourceShard = Shard(catalog, "0000-sources-fixture", artists, catalogRows);
        var fileShard = Shard(catalog, "0001-files-fixture", artists, tracks);
        Write(
            catalog,
            "manifest.json",
            new { complete = true, completedShards = new[] { sourceShard, fileShard } }
        );
        return new(
            rootCapture,
            directoryCapture,
            Path.Combine(catalog, "manifest.json"),
            catalogRows.Concat(tracks).ToArray()
        );
    }

    private static JsonElement SourceRow(long id, Guid uuid) =>
        Element(
            new
            {
                id,
                uuid,
                artist_id = 1,
                show_id = 11,
                display_date = "2020-01-02",
                upstream_identifier = "2020-01-02",
            }
        );

    private static JsonElement ArtistRow(long id, Guid uuid) =>
        Element(
            new
            {
                kind = "artist",
                row = new
                {
                    id,
                    uuid,
                    name = "Fixture artist",
                },
                configured_provider_mappings = new[]
                {
                    new { provider_name = "local", upstream_identifier = Root },
                },
            }
        );

    private static JsonElement Shard(
        string directory,
        string id,
        IReadOnlyList<JsonElement> artists,
        IReadOnlyList<JsonElement> rows
    )
    {
        var path = Path.Combine(directory, id + ".jsonl.gz");
        using (var gzip = new GZipStream(File.Create(path), CompressionLevel.SmallestSize))
        using (var writer = new StreamWriter(gzip))
        {
            writer.WriteLine(
                JsonSerializer.Serialize(
                    new
                    {
                        kind = "snapshot",
                        read_only = "on",
                        in_recovery = true,
                    }
                )
            );
            foreach (var record in artists.Concat(rows))
            {
                writer.WriteLine(record.GetRawText());
            }
            writer.WriteLine(
                JsonSerializer.Serialize(
                    new { kind = "snapshot_complete", shard_rows = rows.Count }
                )
            );
        }
        var artifact = Fingerprint(path);
        return Element(
            new
            {
                shardId = id,
                gzipPath = Path.GetFileName(path),
                actualRows = rows.Count,
                compressed = new { artifact.bytes, artifact.sha256 },
            }
        );
    }

    private static Artifact Write(string directory, string name, object value)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(value, Json));
        return Fingerprint(path);
    }

    private static Artifact Fingerprint(string path) =>
        new(Path.GetFileName(path), new FileInfo(path).Length, Hash(File.ReadAllBytes(path)));

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static JsonElement Element(object value) =>
        JsonSerializer.SerializeToElement(value, Json);

    private sealed record Artifact(string path, long bytes, string sha256);

    private sealed record Fixture(
        string RootCapture,
        string DirectoryCapture,
        string Catalog,
        IReadOnlyList<JsonElement> CatalogRows
    );
}
