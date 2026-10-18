using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;
using Relisten.Normalization.Batch;

namespace RelistenApiTests.Normalization;

[TestFixture]
public sealed class TestRetainedHttpProviderInputs
{
    private static readonly Guid ArtistUuid = Guid.Parse("4dad31c8-6a31-4dce-9e6a-560b563014d6");
    private static readonly DateTimeOffset HttpAt = DateTimeOffset.Parse("2020-01-02T03:04:05Z");
    private static readonly DateTimeOffset AdmissionAt = DateTimeOffset.Parse(
        "2026-10-18T20:30:00Z"
    );
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private string directory = null!;

    [SetUp]
    public void SetUp() =>
        directory = Directory.CreateTempSubdirectory("retained-http-tests-").FullName;

    [TearDown]
    public void TearDown() => Directory.Delete(directory, recursive: true);

    [Test]
    public async Task ExactHttpBodiesBecomeNewAdmissionsWithProvedExistingRecordingHomes()
    {
        var archive = new SourceSpec(Guid.NewGuid(), "item-one", 10, Guid.NewGuid());
        var phish = new SourceSpec(Guid.NewGuid(), "11", 11, Guid.NewGuid());
        var archiveCapture = MakeCapture("archive.org", archive, ArchiveBody());
        var phishCapture = MakeCapture("phish.in", phish, PhishBody());
        var catalog = MakeCatalog(archive, phish);
        var result = await Prepare(catalog, archiveCapture, phishCapture);
        var rows = (await File.ReadAllLinesAsync(result.InputsPath)).Select(Parse).ToArray();

        result.InputRows.Should().Be(2);
        result.BoundSources.Should().Be(2);
        result.UnresolvedSources.Should().Be(0);
        File.Exists(result.DescriptorPath).Should().BeTrue();
        foreach (var row in rows)
        {
            var input = row.GetProperty("input").Deserialize<NormalizationInputRevision>(Json)!;
            var provenance = row.GetProperty("retained_http")
                .Deserialize<RetainedHttpProviderProvenance>(Json)!;
            var source = row.GetProperty("source_context");
            var show = row.GetProperty("show_context");
            input.Payload.Should().Equal(await File.ReadAllBytesAsync(provenance.Body.Path));
            input.Sha256.Should().Be(Hash(input.Payload));
            input.CapturedAt.Should().Be(AdmissionAt.UtcDateTime);
            provenance.HttpStartedAt.Should().Be(HttpAt);
            provenance.AdmittedAt.Should().Be(AdmissionAt);
            provenance.InputRevisionUuid.Should().Be(input.Uuid);
            provenance.CatalogBinding.SourceUuid.Should().Be(source.GetProperty("uuid").GetGuid());
            provenance.CatalogBinding.ShowUuid.Should().Be(show.GetProperty("uuid").GetGuid());
            provenance.CatalogBinding.SourceLine.Should().BeGreaterThan(1);
            input.Uuid.Should().NotBe(source.GetProperty("uuid").GetGuid());
            var decoded = new NormalizationProviderDecoder().Decode(input);
            decoded.Recordings.Should().ContainSingle();
            decoded
                .Setlists.Should()
                .BeEmpty("file positions and song-ID arrays are recording facts");
            using var retained = NormalizationRetainedDocument.Open(input);
            foreach (var file in decoded.Recordings.Single().Files)
            {
                retained.ValidateEvidence(file.Title!.Evidence);
            }
        }
        rows.Select(row => row.GetProperty("input").GetProperty("uuid").GetGuid())
            .Should()
            .OnlyHaveUniqueItems();
    }

    [Test]
    public void FullPhishResponseKeepsEnvelopeCoordinatesAndObjectContractKeepsRootCoordinates()
    {
        var body = PhishBody();
        using var response = JsonDocument.Parse(body);
        var nested = JsonSerializer.SerializeToUtf8Bytes(response.RootElement.GetProperty("data"));
        var envelope = Revision(body, "phishin-show-response-v1");
        var naked = Revision(nested, "phishin-show-object-v1");
        var decoder = new NormalizationProviderDecoder();
        var current = decoder.Decode(envelope).Recordings.Single();
        var prior = decoder.Decode(naked).Recordings.Single();

        current.Origin.Evidence.JsonPointer.Should().Be("/data");
        prior.Origin.Evidence.JsonPointer.Should().Be("");
        current.LiteralDate!.Evidence.JsonPointer.Should().Be("/data/date");
        prior.LiteralDate!.Evidence.JsonPointer.Should().Be("/date");
        current.Files.Single().Title!.Evidence.JsonPointer.Should().Be("/data/tracks/0/title");
        prior.Files.Single().Title!.Evidence.JsonPointer.Should().Be("/tracks/0/title");
        current.Files.Single().Position!.Value.Should().Be(1);
        current.Files.Single().SetLabel!.Value.Should().Be("S1");
        current
            .Files.Single()
            .SongIdentities.Select(value => value.Identity.ProviderKey)
            .Should()
            .Equal("5", "6");
        current
            .Files.Single()
            .SongIdentities[1]
            .Evidence.JsonPointer.Should()
            .Be("/data/tracks/0/song_ids/1");
        current.Venue!.Names.Single().Evidence.JsonPointer.Should().Be("/data/venue/name");
        current
            .Text.Single(value => value.Field == "tag")
            .Text.Evidence.JsonPointer.Should()
            .Be("/data/tags/0/name");
        using var originalDocument = NormalizationRetainedDocument.Open(envelope);
        originalDocument.ValidateEvidence(current.Files.Single().Title!.Evidence);
        using var priorDocument = NormalizationRetainedDocument.Open(naked);
        priorDocument.ValidateEvidence(prior.Files.Single().Title!.Evidence);
    }

    [Test]
    public async Task ChangedBodyFailsBeforePublishingACompleteDescriptor()
    {
        var source = new SourceSpec(Guid.NewGuid(), "11", 11, Guid.NewGuid());
        var capture = MakeCapture("phish.in", source, PhishBody());
        var path = Path.Combine(capture, "responses/0001.json");
        var changed = await File.ReadAllBytesAsync(path);
        changed[^2] ^= 1;
        await File.WriteAllBytesAsync(path, changed);
        var target = Path.Combine(directory, "failed");
        var run = () =>
            new RetainedHttpProviderInputs(new FixedClock()).PrepareAsync(
                new(MakeCatalog(source), [capture]),
                target
            );

        await run.Should().ThrowAsync<InvalidDataException>();
        File.Exists(Path.Combine(target, "descriptor.json")).Should().BeFalse();
        File.Exists(Path.Combine(target, "inputs.jsonl")).Should().BeFalse();
    }

    [Test]
    public async Task ByteAuthenticatedResponseStillMustIdentifyTheSelectedProviderResource()
    {
        var source = new SourceSpec(Guid.NewGuid(), "11", 11, Guid.NewGuid());
        var capture = MakeCapture("phish.in", source, PhishBody(id: 12));
        var target = Path.Combine(directory, "wrong-resource");
        var run = () =>
            new RetainedHttpProviderInputs(new FixedClock()).PrepareAsync(
                new(MakeCatalog(source), [capture]),
                target
            );

        await run.Should().ThrowAsync<InvalidDataException>();
        File.Exists(Path.Combine(target, "descriptor.json")).Should().BeFalse();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task CatalogConflictsPreserveTheNativeInputWithoutAFalseSourceOrShowBinding(
        bool wrongArtist
    )
    {
        var source = new SourceSpec(Guid.NewGuid(), "11", 11, Guid.NewGuid());
        var capture = MakeCapture("phish.in", source, PhishBody());
        var changed = wrongArtist ? source with { ArtistId = 2 } : source with { Upstream = "12" };
        var result = await Prepare(MakeCatalog(changed), capture);
        var row = Parse((await File.ReadAllLinesAsync(result.InputsPath)).Single());
        var binding = row.GetProperty("catalog_binding")
            .Deserialize<RetainedHttpCatalogBinding>(Json)!;

        result.InputRows.Should().Be(1);
        result.BoundSources.Should().Be(0);
        binding.Status.Should().Be("unresolved");
        binding
            .Reason.Should()
            .Be(wrongArtist ? "source_artist_conflict" : "source_provider_identifier_conflict");
        binding.ExpectedSourceUuid.Should().Be(source.Uuid);
        binding.SourceUuid.Should().BeNull();
        binding.ShowUuid.Should().BeNull();
        row.TryGetProperty("source_context", out _).Should().BeFalse();
        new NormalizationProviderDecoder()
            .Decode(row.GetProperty("input").Deserialize<NormalizationInputRevision>(Json)!)
            .Recordings.Should()
            .ContainSingle();
    }

    [Test]
    public async Task MixedArtistNamespacesCannotAuthenticateASourceByItsNumericArtistId()
    {
        var source = new SourceSpec(Guid.NewGuid(), "11", 11, Guid.NewGuid());
        var capture = MakeCapture("phish.in", source, PhishBody());
        var target = Path.Combine(directory, "namespace-conflict");
        var run = () =>
            new RetainedHttpProviderInputs(new FixedClock()).PrepareAsync(
                new(MakeCatalog(source with { CatalogArtistUuid = Guid.NewGuid() }), [capture]),
                target
            );

        await run.Should().ThrowAsync<InvalidDataException>();
        File.Exists(Path.Combine(target, "descriptor.json")).Should().BeFalse();
        Directory.Exists(target).Should().BeFalse();
    }

    [Test]
    public async Task MissingCatalogRecordingPreservesProviderEvidenceWithoutChoosingAnotherSource()
    {
        var source = new SourceSpec(Guid.NewGuid(), "11", 11, Guid.NewGuid());
        var result = await Prepare(MakeCatalog(), MakeCapture("phish.in", source, PhishBody()));
        var row = Parse((await File.ReadAllLinesAsync(result.InputsPath)).Single());
        var binding = row.GetProperty("catalog_binding")
            .Deserialize<RetainedHttpCatalogBinding>(Json)!;

        result.InputRows.Should().Be(1);
        binding.Reason.Should().Be("missing_catalog_source");
        binding.SourceUuid.Should().BeNull();
        binding.ShowUuid.Should().BeNull();
        row.GetProperty("input")
            .GetProperty("payload")
            .GetBytesFromBase64()
            .Should()
            .Equal(PhishBody());
    }

    [Test]
    public async Task FailureAfterAValidInputRetainsItsPrefixWithoutPublishingCompletion()
    {
        var first = new SourceSpec(Guid.NewGuid(), "item-one", 10, Guid.NewGuid());
        var second = new SourceSpec(Guid.NewGuid(), "11", 11, Guid.NewGuid());
        var archive = MakeCapture("archive.org", first, ArchiveBody());
        var phish = MakeCapture("phish.in", second, PhishBody(id: 12));
        var target = Path.Combine(directory, "partial");
        var run = () =>
            new RetainedHttpProviderInputs(new FixedClock()).PrepareAsync(
                new(MakeCatalog(first, second), [archive, phish]),
                target
            );

        await run.Should().ThrowAsync<InvalidDataException>();
        File.Exists(Path.Combine(target, "descriptor.json")).Should().BeFalse();
        File.Exists(Path.Combine(target, "inputs.jsonl")).Should().BeFalse();
        var prefix = await File.ReadAllLinesAsync(Path.Combine(target, "inputs.partial.jsonl"));
        prefix.Should().ContainSingle();
        Parse(prefix[0])
            .GetProperty("source_context")
            .GetProperty("uuid")
            .GetGuid()
            .Should()
            .Be(first.Uuid);
    }

    [Test]
    public async Task MissingShowDoesNotEraseTheProvedRecordingAssociation()
    {
        var source = new SourceSpec(Guid.NewGuid(), "11", 11, Guid.NewGuid());
        var result = await Prepare(
            MakeCatalog(source with { IncludeShow = false }),
            MakeCapture("phish.in", source, PhishBody())
        );
        var row = Parse((await File.ReadAllLinesAsync(result.InputsPath)).Single());
        var binding = row.GetProperty("catalog_binding")
            .Deserialize<RetainedHttpCatalogBinding>(Json)!;

        binding.Status.Should().Be("bound");
        binding.SourceUuid.Should().Be(source.Uuid);
        binding.ShowUuid.Should().BeNull();
        binding.ShowReason.Should().Be("missing_or_conflicting_catalog_show");
        row.GetProperty("source_context").GetProperty("uuid").GetGuid().Should().Be(source.Uuid);
        row.TryGetProperty("show_context", out _).Should().BeFalse();
    }

    [Test]
    public async Task CancellationCreatesNoCompletedAdmissionAndExistingOutputCannotBeOverwritten()
    {
        var source = new SourceSpec(Guid.NewGuid(), "11", 11, Guid.NewGuid());
        var capture = MakeCapture("phish.in", source, PhishBody());
        var request = new RetainedHttpProviderInputsRequest(MakeCatalog(source), [capture]);
        var target = Path.Combine(directory, "cancelled");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelled = () =>
            new RetainedHttpProviderInputs().PrepareAsync(request, target, cancellation.Token);
        await cancelled.Should().ThrowAsync<OperationCanceledException>();
        Directory.Exists(target).Should().BeFalse();
        var completed = await new RetainedHttpProviderInputs(new FixedClock()).PrepareAsync(
            request,
            target
        );
        var original = await File.ReadAllBytesAsync(completed.InputsPath);
        var overwrite = () => new RetainedHttpProviderInputs().PrepareAsync(request, target);
        await overwrite.Should().ThrowAsync<ArgumentException>();
        (await File.ReadAllBytesAsync(completed.InputsPath)).Should().Equal(original);
    }

    private Task<RetainedHttpProviderInputsResult> Prepare(
        string catalog,
        params string[] captures
    ) =>
        new RetainedHttpProviderInputs(new FixedClock()).PrepareAsync(
            new(catalog, captures),
            Path.Combine(directory, "output-" + Guid.NewGuid().ToString("N"))
        );

    private string MakeCapture(string provider, SourceSpec source, byte[] body)
    {
        var capture = Path.Combine(directory, "capture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(capture, "responses"));
        File.WriteAllBytes(Path.Combine(capture, "responses/0001.json"), body);
        var plan = WriteJson(
            capture,
            "selection-plan.json",
            new
            {
                items = new[]
                {
                    new
                    {
                        source_uuid = source.Uuid,
                        artist_uuid = ArtistUuid,
                        upstream_identifier = source.Upstream,
                    },
                },
            }
        );
        var receipts = WriteJson(
            capture,
            "http-receipts.json",
            new[]
            {
                new
                {
                    source_uuid = source.Uuid,
                    status = 200,
                    failure = (string?)null,
                    url = provider == "phish.in"
                        ? "https://phish.in/api/v1/shows/"
                            + source.Upstream
                            + ".json?per_page=99999"
                        : "https://archive.org/metadata/" + source.Upstream + "?extended_err=1",
                    started_at_utc = HttpAt,
                    response = new
                    {
                        path = "responses/0001.json",
                        bytes = body.Length,
                        sha256 = Hash(body),
                    },
                },
            }
        );
        WriteJson(
            capture,
            "manifest.json",
            provider == "phish.in"
                ? (object)
                    new
                    {
                        schema_version = "native-phish-credit-fields-v1",
                        state = "completed",
                        captured = 1,
                        plan,
                        http_receipts = receipts,
                    }
                : new
                {
                    version = "native-archive-credit-fields-v1",
                    state = "completed",
                    captured = 1,
                    plan,
                    http_receipts = receipts,
                }
        );
        return capture;
    }

    private string MakeCatalog(params SourceSpec[] sources)
    {
        var catalog = Path.Combine(directory, "catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(catalog);
        const string name = "0000-sources-1-99.jsonl.gz";
        using (
            var gzip = new GZipStream(
                File.Create(Path.Combine(catalog, name)),
                CompressionMode.Compress
            )
        )
        using (var writer = new StreamWriter(gzip))
        {
            void Line(object value) => writer.WriteLine(JsonSerializer.Serialize(value, Json));
            Line(
                new
                {
                    kind = "snapshot",
                    read_only = "on",
                    in_recovery = true,
                }
            );
            Line(
                new
                {
                    kind = "artist",
                    row = new
                    {
                        id = 1,
                        uuid = ArtistUuid,
                        name = "Artist",
                    },
                }
            );
            foreach (var source in sources)
            {
                if (source.CatalogArtistUuid is { } artistUuid)
                {
                    Line(
                        new
                        {
                            kind = "artist",
                            row = new
                            {
                                id = source.ArtistId,
                                uuid = artistUuid,
                                name = "Other artist",
                            },
                        }
                    );
                }
                Line(
                    new
                    {
                        kind = "source_sample",
                        row = new
                        {
                            id = source.Id,
                            uuid = source.Uuid,
                            artist_id = source.ArtistId,
                            show_id = source.Id + 100,
                            upstream_identifier = source.Upstream,
                        },
                        show_context = source.IncludeShow
                            ? new
                            {
                                id = source.Id + 100,
                                uuid = source.ShowUuid,
                                artist_id = source.ArtistId,
                            }
                            : null,
                    }
                );
            }
            Line(new { kind = "snapshot_complete", shard_rows = sources.Length });
        }
        var raw = File.ReadAllBytes(Path.Combine(catalog, name));
        WriteJson(
            catalog,
            "manifest.json",
            new
            {
                complete = true,
                completedShards = new[]
                {
                    new
                    {
                        shardId = "0000-sources-1-99",
                        gzipPath = name,
                        actualRows = sources.Length,
                        compressed = new { bytes = raw.Length, sha256 = Hash(raw) },
                    },
                },
            }
        );
        return Path.Combine(catalog, "manifest.json");
    }

    private static object WriteJson(string parent, string name, object value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        File.WriteAllBytes(Path.Combine(parent, name), bytes);
        return new
        {
            path = name,
            bytes = bytes.Length,
            sha256 = Hash(bytes),
        };
    }

    private static byte[] ArchiveBody() =>
        JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                metadata = new { identifier = "item-one", date = "1970-02-03" },
                files = new[]
                {
                    new
                    {
                        name = "song.mp3",
                        title = "A Song",
                        track = "1",
                    },
                },
            }
        );

    private static byte[] PhishBody(int id = 11) =>
        JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                success = true,
                data = new
                {
                    id,
                    date = "1970-02-03",
                    taper_notes = "Original notes",
                    venue = new { id = 9, name = "Hall" },
                    tags = new[] { new { name = "Tag", notes = "Notes" } },
                    tracks = new[]
                    {
                        new
                        {
                            id = 21,
                            show_id = id,
                            title = "A Song > Another Song",
                            position = 1,
                            set = "S1",
                            mp3 = "https://example.test/song.mp3",
                            song_ids = new[] { 5, 6 },
                        },
                    },
                },
            }
        );

    private static NormalizationInputRevision Revision(byte[] body, string version) =>
        new()
        {
            Uuid = Guid.NewGuid(),
            Resource = new("phish.in", "show", "11"),
            Payload = body,
            Sha256 = Hash(body),
            MediaType = "application/json",
            CaptureVersion = version,
        };

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private sealed record SourceSpec(
        Guid Uuid,
        string Upstream,
        long Id,
        Guid ShowUuid,
        long ArtistId = 1,
        bool IncludeShow = true,
        Guid? CatalogArtistUuid = null
    );

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => AdmissionAt;
    }
}
