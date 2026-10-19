using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Relisten.Normalization.Batch;

namespace RelistenApiTests.Normalization;

/// <summary>Changed-only refresh preserves original evidence and never completes a partial image.</summary>
[TestFixture]
public sealed class TestVenueNormalizationRefreshBatch
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private string directory = null!;
    private string baseline = null!;
    private string snapshot = null!;
    private VenueNormalizationRefreshRequest request = null!;

    [SetUp]
    public async Task PrepareOriginalVenueRun()
    {
        directory = Path.Combine(
            Path.GetTempPath(),
            "venue-refresh-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(directory);
        var geography = CreateGeography();
        snapshot = Path.Combine(directory, "snapshot.jsonl");
        var rows = new[]
        {
            JsonSerializer.SerializeToElement(
                new
                {
                    kind = "snapshot",
                    read_only = "on",
                    in_recovery = true,
                    snapshot = "fixture-original",
                }
            ),
            JsonSerializer.SerializeToElement(
                new
                {
                    kind = "artist",
                    row = new
                    {
                        id = 1,
                        uuid = Id(100),
                        name = "Example Artist",
                    },
                    configured_provider_mappings = Array.Empty<object>(),
                }
            ),
            Venue(1, "North Harbor Live"),
            Venue(2, "NHL"),
            Venue(3, "Unrelated Place"),
            JsonSerializer.SerializeToElement(
                new
                {
                    kind = "source_sample",
                    row = new
                    {
                        uuid = Id(20),
                        artist_id = 1,
                        venue_id = 1,
                        show_id = 1,
                        display_date = "2000-01-01",
                    },
                    show_context = new
                    {
                        id = 1,
                        uuid = Id(30),
                        artist_id = 1,
                        venue_id = 1,
                        display_date = "2000-01-01",
                    },
                }
            ),
            JsonSerializer.SerializeToElement(new { kind = "snapshot_complete", shard_rows = 4 }),
        };
        await File.WriteAllLinesAsync(snapshot, rows.Select(row => row.GetRawText()));
        baseline = Path.Combine(directory, "original");
        await new ShadowCatalogBatch([new EarlierVenueAdapter(geography)]).RunInlineAsync(
            snapshot,
            baseline
        );
        // The wrapper models a historical implementation, whose completed run
        // registered the real producer type rather than this fixture's class.
        var manifestPath = Path.Combine(baseline, "manifest.json");
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!;
        var methods = manifest["methods"]!.AsObject();
        methods[nameof(ShadowVenueAdapter)] = methods[nameof(EarlierVenueAdapter)]!.DeepClone();
        methods.Remove(nameof(EarlierVenueAdapter));
        await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString());
        var baseDirectory = Path.Combine(directory, "reader.sqlite3");
        await File.WriteAllTextAsync(
            baseDirectory,
            "Pinned reader generation; refresh never opens or changes this database."
        );
        request = new(
            VenueNormalizationRefreshBatch.RequestVersion,
            Pin(baseDirectory),
            Pin(Path.Combine(baseline, "manifest.json")),
            Pin(Path.Combine(baseline, "input-descriptor.json")),
            Pin(geography[..geography.LastIndexOf("#sha256=", StringComparison.Ordinal)]),
            MinimumFreeBytes: 0
        );
    }

    [TearDown]
    public void RemoveFixture() => Directory.Delete(directory, recursive: true);

    [Test]
    public async Task RefreshPublishesChangedAliasesExplicitRemovalsAndDomainOwnedGroupSummary()
    {
        var originalManifest = await File.ReadAllBytesAsync(request.PreviousRunManifest.Path);
        var originalDescriptor = await File.ReadAllBytesAsync(request.CaptureDescriptor.Path);
        var result = await RunAsync("corrections");
        var changes = Read<VenueNormalizationCorrection>(result.Changes.Path);
        var groups = Read<ShadowVenueHypothesis>(result.Groups.Path);

        result.TotalVenues.Should().Be(3);
        result.ChangedVenues.Should().Be(2);
        result.UnchangedVenues.Should().Be(1);
        var alias = changes.Single(change => change.VenueUuid == Id(2));
        alias.Previous.NormalizationKey.Should().BeNull();
        alias.Current.Name.Should().Be("North Harbor Live");
        alias.Current.NormalizationKey.Should().Be(groups.Single().Key);
        alias.LegacyId.Should().Be(2);
        alias.ArtistId.Should().Be(1);
        changes
            .Single(change => change.VenueUuid == Id(3))
            .Current.NormalizationKey.Should()
            .BeNull();
        groups.Single().AnchorVenueUuid.Should().Be(Id(1));
        groups.Single().MemberCount.Should().Be(2);
        groups.Single().SourceObservationCount.Should().Be(1);
        var original = Read<JsonElement>(alias.OriginalOutcome.Path)[
            checked((int)alias.OriginalOutcome.Line - 1)
        ];
        original.GetProperty("inputUuid").GetGuid().Should().Be(Id(2));
        original
            .GetProperty("evidence")
            .GetProperty("nativeVenue")
            .GetProperty("name")
            .GetString()
            .Should()
            .Be("NHL");
        Pin(alias.OriginalOutcome.Path).Sha256.Should().Be(alias.OriginalOutcome.Sha256);
        (await File.ReadAllBytesAsync(request.PreviousRunManifest.Path))
            .Should()
            .Equal(originalManifest);
        (await File.ReadAllBytesAsync(request.CaptureDescriptor.Path))
            .Should()
            .Equal(originalDescriptor);
        result.PreviousChunks.Should().ContainSingle();
    }

    [Test]
    public async Task OutputBoundLeavesNoCompletionAndNewAttemptCanReuseUnchangedOriginalEvidence()
    {
        request = request with { MaximumBytes = 1 };
        var failed = async () => await RunAsync("bounded");
        await failed.Should().ThrowAsync<InvalidDataException>();
        File.Exists(Path.Combine(directory, "bounded", "manifest.json")).Should().BeFalse();

        request = request with { MaximumBytes = 8 * 1024 * 1024 };
        (await RunAsync("retry")).Complete.Should().BeTrue();
    }

    [Test]
    public async Task ChangedCaptureOrMissingOriginalOutcomesCannotBecomeACompleteRefresh()
    {
        var original = await File.ReadAllTextAsync(snapshot);
        await File.AppendAllTextAsync(snapshot, "{}\n");
        var changed = async () => await RunAsync("changed");
        await changed.Should().ThrowAsync<InvalidDataException>();
        File.Exists(Path.Combine(directory, "changed", "manifest.json")).Should().BeFalse();
        await File.WriteAllTextAsync(snapshot, original);
        File.Delete(Directory.GetFiles(Path.Combine(baseline, "venues"), "*.jsonl.gz").Single());
        var missing = async () => await RunAsync("missing");
        await missing.Should().ThrowAsync<InvalidDataException>();
        File.Exists(Path.Combine(directory, "missing", "manifest.json")).Should().BeFalse();
    }

    private async Task<VenueNormalizationCorrections> RunAsync(string output)
    {
        var path = Path.Combine(directory, output + "-request.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(request, Json));
        return await new VenueNormalizationRefreshBatch().RunAsync(
            path,
            Path.Combine(directory, output),
            Pin(path).Sha256
        );
    }

    private string CreateGeography()
    {
        using (
            var zip = ZipFile.Open(Path.Combine(directory, "cities500.zip"), ZipArchiveMode.Create)
        )
        using (var writer = new StreamWriter(zip.CreateEntry("cities500.txt").Open()))
        {
            writer.WriteLine(
                "5128581\tNew York City\tNew York City\tNYC,New York,NY\t40.7\t-74\tP\tPPL\tUS\t\tNY\t\t\t\t1\t\t1\tAmerica/New_York\t2026-10-18"
            );
        }
        File.WriteAllText(
            Path.Combine(directory, "admin1CodesASCII.txt"),
            "US.NY\tNew York\tNew York\t5128638\n"
        );
        File.WriteAllText(Path.Combine(directory, "readme.txt"), "Synthetic geographic fixture.");
        var path = Path.Combine(directory, "geography.json");
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(
                new
                {
                    version = "shadow-geonames-cities500-v1",
                    files = new[] { "cities500.zip", "admin1CodesASCII.txt", "readme.txt" }.Select(
                        name =>
                        {
                            var pin = Pin(Path.Combine(directory, name));
                            return new
                            {
                                path = name,
                                pin.Bytes,
                                pin.Sha256,
                            };
                        }
                    ),
                },
                Json
            )
        );
        return path + "#sha256=" + Pin(path).Sha256;
    }

    private static Guid Id(int id) => new(id, 0, 0, new byte[8]);

    private static JsonElement Venue(int id, string name) =>
        JsonSerializer.SerializeToElement(
            new
            {
                kind = "venue_sample",
                row = new
                {
                    id,
                    uuid = Id(id),
                    artist_id = 1,
                    name,
                    location = "New York, NY",
                },
            }
        );

    private static VenueCorrectionArtifact Pin(string path) =>
        new(
            path,
            new FileInfo(path).Length,
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))
        );

    private static T[] Read<T>(string path)
    {
        using var gzip = new GZipStream(File.OpenRead(path), CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        var rows = new List<T>();
        while (reader.ReadLine() is { } line)
        {
            rows.Add(JsonSerializer.Deserialize<T>(line, Json)!);
        }
        return rows.ToArray();
    }

    // Model an earlier method that did not observe an unused alias and attached
    // an unrelated row incorrectly. Refresh must support both addition and removal.
    private sealed class EarlierVenueAdapter(string geography) : IShadowCatalogAdapter
    {
        private readonly ShadowVenueAdapter inner = new(geography);
        private ShadowVenueProposal? anchor;
        public string Version =>
            inner.Version.Replace(
                "corpus-hypotheses-v2",
                "corpus-hypotheses-v1",
                StringComparison.Ordinal
            );

        public IReadOnlyList<string> DomainsFor(string inputKind) => inner.DomainsFor(inputKind);

        public void Observe(JsonElement record)
        {
            if (
                record.GetProperty("kind").GetString() != "venue_sample"
                || record.GetProperty("row").GetProperty("id").GetInt32() == 1
            )
            {
                inner.Observe(record);
            }
        }

        public IEnumerable<ShadowCatalogResult> Process(ShadowCatalogInput input)
        {
            var result = inner.Process(input).Single();
            var proposal = (ShadowVenueProposal)result.Proposal!;
            if (input.Row.GetProperty("id").GetInt32() == 1)
            {
                anchor = proposal;
            }
            if (input.Row.GetProperty("id").GetInt32() == 3)
            {
                proposal = proposal with
                {
                    ProposedPlaceKey = anchor!.ProposedPlaceKey,
                    DisplayName = anchor.DisplayName,
                    Normalization = anchor.Normalization,
                };
            }
            yield return result with
            {
                Proposal = proposal,
                Method = Version,
            };
        }
    }
}
