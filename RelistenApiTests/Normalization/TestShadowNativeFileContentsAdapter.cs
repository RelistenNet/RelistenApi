using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;
using Relisten.Normalization.Batch;

namespace RelistenApiTests.Normalization;

[TestFixture]
public sealed class TestShadowNativeFileContentsAdapter
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Guid Artist = Guid.Parse("30000000-0000-0000-0000-000000000001");
    private static readonly Guid Source = Guid.Parse("30000000-0000-0000-0000-000000000002");
    private static readonly Guid Show = Guid.Parse("30000000-0000-0000-0000-000000000003");
    private static readonly Guid Track = Guid.Parse("30000000-0000-0000-0000-000000000004");
    private static readonly Guid SourceSet = Guid.Parse("30000000-0000-0000-0000-000000000005");

    [Test]
    public void ArchiveRetainsUntitledAndMalformedFilesAlongsideInterpretedTitles()
    {
        var revision = Archive(
            """
            [{"name":"song.flac","title":"Morning"},
             {"name":"notes.txt"},
             {"name":"broken-title.mp3","title":42}]
            """
        );

        var result = Adapter().Process(PlainInput(revision)).Single();
        var proposal = Proposal(result);

        proposal.Files.Select(file => file.FileOrdinal).Should().Equal(0, 1, 2);
        proposal
            .Files.Select(file => file.File.Filename!.Value)
            .Should()
            .Equal("song.flac", "notes.txt", "broken-title.mp3");
        proposal
            .Files.Select(file => file.File.Origin.Evidence.JsonPointer)
            .Should()
            .Equal("/files/0", "/files/1", "/files/2");
        proposal
            .Files.Should()
            .OnlyContain(file =>
                file.InputRevisionUuid == revision.Uuid
                && file.RecordingOrigin.Evidence.JsonPointer == "/metadata"
                && file.CatalogTrackUuid == null
                && file.CatalogPlayback == null
            );
        proposal.Files[0].Mentions.Should().ContainSingle();
        proposal
            .Files.Skip(1)
            .Should()
            .OnlyContain(file => file.TitleInterpretation == null && file.Mentions.Count == 0);
        proposal
            .DecoderIssues.Should()
            .Contain(issue =>
                issue.Code == "expected_text" && issue.Evidence.JsonPointer == "/files/2/title"
            );
        proposal
            .Files[2]
            .Issues.Should()
            .ContainSingle()
            .Which.Evidence.JsonPointer.Should()
            .Be("/files/2/title");
        proposal.Files.Take(2).Should().OnlyContain(file => file.Issues.Count == 0);
        proposal.Files.Select(file => file.EntityKey).Should().OnlyHaveUniqueItems();
        ValidateEvidence(revision, proposal.Files.Select(file => file.File.Origin.Evidence));
    }

    [Test]
    public void CompoundUnicodeTitleUsesArtistDictionaryAndOriginalNativeCitations()
    {
        var revision = Archive("""[{"name":"compound.mp3","title":"Café 🎵 > Evening"}]""");
        var song = Guid.NewGuid();
        var dictionary = new ShadowRepertoireAdapter();
        dictionary.Observe(Song(song, "Café 🎵"));
        var adapter = Adapter(dictionary);

        var result = adapter.Process(PlainInput(revision)).Single();
        var file = Proposal(result).Files.Should().ContainSingle().Which;

        file.Mentions.Select(mention => mention.Interpretation.Title)
            .Should()
            .Equal("Café 🎵", "Evening");
        file.Mentions[0].Interpretation.Song!.LegacySongUuid.Should().Be(song);
        file.Mentions[1].Interpretation.Song!.Basis.Should().Be("title_derived");
        file.Mentions.Select(mention => mention.Evidence.Quote)
            .Should()
            .Equal("Café 🎵", "Evening");
        file.Mentions.Should()
            .OnlyContain(mention =>
                mention.Evidence.InputRevisionUuid == revision.Uuid
                && mention.Evidence.JsonPointer == "/files/0/title"
            );
        file.Mentions[1].Evidence.StartCodePoint.Should().Be(9);
        file.TitleInterpretation!.Segments[0]
            .TransitionAfter.Should()
            .Be(TitleTransition.SegueMarker);
        file.TitleInterpretation.Segments.Should()
            .OnlyContain(segment =>
                segment.StartMs == null && segment.EndMs == null && segment.OccurrenceUuid == null
            );
        result.EntityKeys.Should().Contain(ShadowRepertoireAdapter.KnownEntityKey(song));
        ValidateEvidence(revision, file.Mentions.Select(mention => mention.Evidence));

        JsonSerializer
            .Serialize(adapter.Process(PlainInput(revision)).Single(), Json)
            .Should()
            .Be(JsonSerializer.Serialize(result, Json));
    }

    [Test]
    public void DuplicateDictionaryTitlesRetainBothCandidatesWithoutSelectingOne()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var dictionary = new ShadowRepertoireAdapter();
        dictionary.Observe(Song(first, "Echoes"));
        dictionary.Observe(Song(second, "Echoes"));
        var revision = Archive("""[{"name":"echoes.mp3","title":"Echoes"}]""");

        var result = Adapter(dictionary).Process(PlainInput(revision)).Single();
        var mention = Proposal(result).Files.Single().Mentions.Single();

        mention.Interpretation.Song.Should().BeNull();
        mention
            .Interpretation.CandidateSongs.Select(candidate => candidate.LegacySongUuid)
            .Should()
            .BeEquivalentTo(new Guid?[] { first, second });
        result
            .EntityKeys.Should()
            .Contain(ShadowRepertoireAdapter.KnownEntityKey(first))
            .And.Contain(ShadowRepertoireAdapter.KnownEntityKey(second));
    }

    [Test]
    public void PhishKeepsNativeRecordingFactsWithoutAllocatingCatalogAudioOrOccurrences()
    {
        var revision = Input(
            new("phish.in", "show", "11"),
            "phishin-show-response-v1",
            """
            {"success":true,"data":{"id":11,"date":"2020-01-02","tracks":[
              {"id":101,"show_id":11,"title":"Morning > Evening","position":7,"set":"2","mp3":"https://media.example.test/101.mp3","song_ids":[4,5]},
              {"id":102,"show_id":11,"position":8,"set_name":"Encore","mp3":"https://media.example.test/102.mp3"}]}}
            """
        );

        var files = Proposal(Adapter().Process(PlainInput(revision)).Single()).Files;

        files.Select(file => file.File.Position!.Value).Should().Equal(7, 8);
        files.Select(file => file.File.SetLabel!.Value).Should().Equal("2", "Encore");
        files
            .Select(file => file.File.PlaybackLocator!.Value)
            .Should()
            .Equal("https://media.example.test/101.mp3", "https://media.example.test/102.mp3");
        files[0]
            .File.SongIdentities.Select(identity => identity.Identity.ProviderKey)
            .Should()
            .Equal("4", "5");
        files
            .Should()
            .OnlyContain(file =>
                file.CatalogSourceUuid == null
                && file.CatalogShowUuid == null
                && file.CatalogTrackUuid == null
                && file.CatalogSourceSetUuid == null
                && file.CatalogPlayback == null
            );
        files[0]
            .TitleInterpretation!.Segments.Should()
            .OnlyContain(segment =>
                segment.OccurrenceUuid == null && segment.StartMs == null && segment.EndMs == null
            );
        files[1].Mentions.Should().BeEmpty();
        ValidateEvidence(
            revision,
            files
                .Select(file => file.File.Origin.Evidence)
                .Concat(files.SelectMany(file => file.Mentions).Select(mention => mention.Evidence))
        );
    }

    [TestCase(false)]
    [TestCase(true)]
    public void NativeFileOwnerMustAgreeBeforeItInheritsTheEnclosingRecordingHome(
        bool conflictingOwner
    )
    {
        var revision = Input(
            new("phish.in", "show", "11"),
            "phishin-show-response-v1",
            JsonSerializer.Serialize(
                new
                {
                    success = true,
                    data = new
                    {
                        id = 11,
                        tracks = new[]
                        {
                            new
                            {
                                id = 101,
                                show_id = conflictingOwner ? 99 : 11,
                                title = "Morning",
                                mp3 = "https://media.example.test/101.mp3",
                            },
                        },
                    },
                }
            )
        );

        var proposal = Proposal(Adapter().Process(HttpInput(revision)).Single());
        var file = proposal.Files.Should().ContainSingle().Which;

        file.File.RecordingIdentity!.Identity.ProviderKey.Should()
            .Be(conflictingOwner ? "99" : "11");
        file.CatalogSourceUuid.Should().Be(conflictingOwner ? null : Source);
        file.CatalogShowUuid.Should().Be(conflictingOwner ? null : Show);
        file.CatalogPlayback.Should().BeNull();
        file.Mentions.Should().ContainSingle();
        if (conflictingOwner)
        {
            file.CatalogBindingReason.Should().NotBeNullOrEmpty();
            proposal
                .DecoderIssues.Should()
                .Contain(issue => issue.Code == "track_show_identity_mismatch");
        }
        else
        {
            file.CatalogBindingReason.Should().Be("native_file_has_no_exact_catalog_anchor");
            proposal.DecoderIssues.Should().BeEmpty();
        }
    }

    [Test]
    public void MissingLocalRootKeepsFilesAndFreshReplayRestoresOnlyExactPlaybackAnchors()
    {
        var (root, directory) = LocalInput();
        var partial = Proposal(Adapter().Process(directory).Single());

        partial.Files.Should().HaveCount(2);
        partial.InputIssues.Should().Contain(issue => issue.Code == "missing_retained_local_root");
        partial.Files.Should().OnlyContain(file => file.CatalogPlayback == null);

        var adapter = Adapter();
        adapter.Observe(root);
        var recovered = Proposal(adapter.Process(directory).Single());

        recovered.InputIssues.Should().BeEmpty();
        recovered.Files.Should().HaveCount(2);
        var bound = recovered.Files[0];
        bound.CatalogSourceUuid.Should().Be(Source);
        bound.CatalogShowUuid.Should().Be(Show);
        bound.CatalogTrackUuid.Should().Be(Track);
        bound.CatalogSourceSetUuid.Should().Be(SourceSet);
        bound
            .CatalogPlayback.Should()
            .Be(
                new ShadowFilePlayback(
                    Track,
                    Source,
                    Show,
                    "https://audio.relisten.net/trey/target/song.mp3"
                )
            );
        recovered.Files[1].CatalogTrackUuid.Should().BeNull();
        recovered.Files[1].CatalogPlayback.Should().BeNull();
        recovered
            .Files.Select(file => file.EntityKey)
            .Should()
            .Equal(partial.Files.Select(file => file.EntityKey));
        var replay = Adapter();
        replay.Observe(root);
        JsonSerializer
            .Serialize(Proposal(replay.Process(directory).Single()), Json)
            .Should()
            .Be(JsonSerializer.Serialize(recovered, Json));
    }

    [Test]
    public void MoreThanOneThousandNativeFilesRemainIndividuallyInspectable()
    {
        var revision = Archive(
            JsonSerializer.Serialize(
                Enumerable
                    .Range(0, 1001)
                    .Select(index => new { name = $"file-{index}.mp3", title = $"Song {index}" })
            )
        );

        var result = Adapter().Process(PlainInput(revision)).Single();
        var files = Proposal(result).Files;

        files.Should().HaveCount(1001);
        files.Select(file => file.FileOrdinal).Should().Equal(Enumerable.Range(0, 1001));
        files.Select(file => file.EntityKey).Should().OnlyHaveUniqueItems();
        files[^1].File.Filename!.Value.Should().Be("file-1000.mp3");
        files[^1].Mentions.Single().Evidence.JsonPointer.Should().Be("/files/1000/title");
        ValidateEvidence(
            revision,
            files.SelectMany(file => file.Mentions).Select(mention => mention.Evidence)
        );
    }

    [Test]
    public void UnsupportedCaptureIsAnExplicitFailureWithoutPartialFileProposals()
    {
        var revision = Input(
            new("archive.org", "item_metadata", "native-item"),
            "unknown-v1",
            "{}"
        );

        var result = Adapter().Process(PlainInput(revision)).Single();

        result.Outcome.Should().Be("failed");
        result.Reason.Should().Be("unsupported_provider_capture");
        result.Proposal.Should().BeNull();
    }

    [Test]
    public async Task NativeOnlyBatchKeepsDictionaryContextAndRejectsChangedScopeOnResume()
    {
        var directory = Directory.CreateTempSubdirectory("native-file-batch-");
        try
        {
            var song = Guid.NewGuid();
            var native = PlainInput(Archive("""[{"name":"cafe.mp3","title":"Café 🎵"}]"""));
            var snapshot = Path.Combine(directory.FullName, "inputs.jsonl");
            var output = Path.Combine(directory.FullName, "output");
            var artist = JsonSerializer.SerializeToElement(
                new
                {
                    kind = "artist",
                    row = new
                    {
                        id = 1,
                        uuid = Artist,
                        name = "Fixture artist",
                    },
                    configured_provider_mappings = new[] { new { provider_name = "archive.org" } },
                },
                Json
            );
            await File.WriteAllLinesAsync(
                snapshot,
                new[] { artist, Song(song, "Café 🎵"), native.Record }.Select(row =>
                    row.GetRawText()
                )
            );

            await NativeBatch().RunRowsInlineAsync(snapshot, output, providerInputsOnly: true);

            var original = ReadBatchResults(output);
            var result = original.Should().ContainSingle().Which;
            result.GetProperty("domain").GetString().Should().Be("file_contents");
            result.GetProperty("inputKind").GetString().Should().Be("provider_input");
            var file = result
                .GetProperty("proposal")
                .GetProperty("files")
                .EnumerateArray()
                .Single();
            var mention = file.GetProperty("mentions").EnumerateArray().Single();
            mention
                .GetProperty("interpretation")
                .GetProperty("song")
                .GetProperty("legacySongUuid")
                .GetGuid()
                .Should()
                .Be(song);

            await NativeBatch()
                .RunRowsInlineAsync(snapshot, output, resume: true, providerInputsOnly: true);
            ReadBatchResults(output)
                .Select(row => row.GetRawText())
                .Should()
                .Equal(original.Select(row => row.GetRawText()));
            var manifest = await File.ReadAllBytesAsync(Path.Combine(output, "manifest.json"));
            var changedScope = () =>
                NativeBatch()
                    .RunRowsInlineAsync(snapshot, output, resume: true, providerInputsOnly: false);

            await changedScope.Should().ThrowAsync<InvalidDataException>();
            ReadBatchResults(output)
                .Select(row => row.GetRawText())
                .Should()
                .Equal(original.Select(row => row.GetRawText()));
            (await File.ReadAllBytesAsync(Path.Combine(output, "manifest.json")))
                .Should()
                .Equal(manifest);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static ShadowCatalogBatch NativeBatch()
    {
        var repertoire = new ShadowRepertoireAdapter();
        return new([repertoire, new ShadowNativeFileContentsAdapter(new(), repertoire)]);
    }

    [Test]
    public async Task PartialNativeResumeKeepsOriginalStreamCoordinatesAndLaterDictionaryContext()
    {
        var directory = Directory.CreateTempSubdirectory("native-file-resume-");
        try
        {
            var song = Guid.NewGuid();
            var first = PlainInput(Archive("""[{"name":"first.mp3","title":"Café 🎵"}]"""));
            var second = PlainInput(Archive("""[{"name":"second.mp3","title":"Café 🎵"}]"""));
            var artist = JsonSerializer.SerializeToElement(
                new
                {
                    kind = "artist",
                    row = new
                    {
                        id = 1,
                        uuid = Artist,
                        name = "Fixture artist",
                    },
                    configured_provider_mappings = new[] { new { provider_name = "archive.org" } },
                },
                Json
            );
            var snapshot = Path.Combine(directory.FullName, "inputs.jsonl");
            var interruptedOutput = Path.Combine(directory.FullName, "interrupted");
            var cleanOutput = Path.Combine(directory.FullName, "clean");
            // The dictionary is deliberately after the cancellation point. Both the initial
            // pass and resume must observe it without shifting the original input checkpoint.
            await File.WriteAllLinesAsync(
                snapshot,
                new[] { artist, first.Record, Song(song, "Café 🎵"), second.Record }.Select(row =>
                    row.GetRawText()
                )
            );
            using var cancellation = new CancellationTokenSource();
            var interrupted = () =>
                PartialBatch(cancellation.Cancel)
                    .RunRowsInlineAsync(
                        snapshot,
                        interruptedOutput,
                        providerInputsOnly: true,
                        ct: cancellation.Token
                    );

            await interrupted.Should().ThrowAsync<OperationCanceledException>();

            using var checkpoint = JsonDocument.Parse(
                await File.ReadAllBytesAsync(Path.Combine(interruptedOutput, "checkpoint.json"))
            );
            checkpoint.RootElement.GetProperty("lastLine").GetInt64().Should().Be(2);
            using var progress = JsonDocument.Parse(
                await File.ReadAllBytesAsync(Path.Combine(interruptedOutput, "progress.json"))
            );
            progress.RootElement.GetProperty("state").GetString().Should().Be("interrupted");
            var prefix = ReadBatchResults(interruptedOutput);
            prefix
                .Should()
                .ContainSingle()
                .Which.GetProperty("inputUuid")
                .GetGuid()
                .Should()
                .Be(first.Uuid);

            await PartialBatch()
                .RunRowsInlineAsync(
                    snapshot,
                    interruptedOutput,
                    resume: true,
                    providerInputsOnly: true
                );
            await PartialBatch()
                .RunRowsInlineAsync(snapshot, cleanOutput, providerInputsOnly: true);

            var resumed = ReadBatchResults(interruptedOutput);
            resumed.Should().HaveCount(2);
            resumed
                .Select(row => row.GetProperty("inputUuid").GetGuid())
                .Should()
                .BeEquivalentTo(new[] { first.Uuid, second.Uuid });
            resumed
                .Select(row => row.GetRawText())
                .Should()
                .Equal(ReadBatchResults(cleanOutput).Select(row => row.GetRawText()));
            foreach (var result in resumed)
            {
                var file = result
                    .GetProperty("proposal")
                    .GetProperty("files")
                    .EnumerateArray()
                    .Single();
                file.GetProperty("mentions")
                    .EnumerateArray()
                    .Single()
                    .GetProperty("interpretation")
                    .GetProperty("song")
                    .GetProperty("legacySongUuid")
                    .GetGuid()
                    .Should()
                    .Be(song);
            }
            using var completed = JsonDocument.Parse(
                await File.ReadAllBytesAsync(Path.Combine(interruptedOutput, "checkpoint.json"))
            );
            completed.RootElement.GetProperty("lastLine").GetInt64().Should().Be(4);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static ShadowCatalogBatch PartialBatch(Action? afterFirstProvider = null) =>
        new([new CancelAfterFirstProviderAdapter(Adapter(), afterFirstProvider)]);

    private sealed class CancelAfterFirstProviderAdapter(
        ShadowNativeFileContentsAdapter inner,
        Action? afterFirstProvider
    ) : IShadowCatalogAdapter
    {
        private bool processed;
        public string Version => inner.Version;

        public IReadOnlyList<string> DomainsFor(string inputKind) => inner.DomainsFor(inputKind);

        public void Observe(JsonElement record) => inner.Observe(record);

        public IEnumerable<ShadowCatalogResult> Process(ShadowCatalogInput input)
        {
            var results = inner.Process(input).ToArray();
            if (!processed)
            {
                processed = true;
                afterFirstProvider?.Invoke();
            }
            return results;
        }
    }

    private static List<JsonElement> ReadBatchResults(string output)
    {
        var results = new List<JsonElement>();
        foreach (
            var path in Directory
                .GetFiles(output, "*.jsonl.gz", SearchOption.AllDirectories)
                .Order()
        )
        {
            using var gzip = new GZipStream(File.OpenRead(path), CompressionMode.Decompress);
            using var reader = new StreamReader(gzip, Encoding.UTF8);
            while (reader.ReadLine() is { } line)
            {
                using var document = JsonDocument.Parse(line);
                results.Add(document.RootElement.Clone());
            }
        }
        return results;
    }

    private static ShadowNativeFileContentsAdapter Adapter(
        ShadowRepertoireAdapter? repertoire = null
    ) => new(new ShadowProviderObservations(), repertoire);

    private static ShadowNativeFileContentsProposal Proposal(ShadowCatalogResult result) =>
        (ShadowNativeFileContentsProposal)result.Proposal!;

    private static void ValidateEvidence(
        NormalizationInputRevision revision,
        IEnumerable<NormalizedEvidenceReference> evidence
    )
    {
        using var document = NormalizationRetainedDocument.Open(revision);
        foreach (var reference in evidence)
        {
            document.ValidateEvidence(reference);
        }
    }

    private static NormalizationInputRevision Archive(string files) =>
        Input(
            new("archive.org", "item_metadata", "native-item"),
            "archive-item-response-v1",
            "{\"metadata\":{\"identifier\":\"native-item\"},\"files\":" + files + "}"
        );

    private static NormalizationInputRevision Input(
        NormalizationResourceKey resource,
        string version,
        string json
    )
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        return new()
        {
            Uuid = Guid.NewGuid(),
            Resource = resource,
            Payload = bytes,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
            CaptureVersion = version,
            MediaType = "application/json",
            CapturedAt = new DateTime(2026, 10, 18, 0, 0, 0, DateTimeKind.Utc),
        };
    }

    private static JsonElement Song(Guid uuid, string title) =>
        JsonSerializer.SerializeToElement(
            new
            {
                kind = "song_sample",
                row = new
                {
                    uuid,
                    artist_id = 1,
                    name = title,
                },
            },
            Json
        );

    private static ShadowCatalogInput PlainInput(NormalizationInputRevision revision) =>
        Project(
            new
            {
                kind = "provider_input",
                provider = revision.Resource.Provider,
                row = new
                {
                    uuid = revision.Uuid,
                    artist_id = 1,
                    artist_uuid = Artist,
                },
                input = revision,
            }
        );

    private static ShadowCatalogInput HttpInput(NormalizationInputRevision revision)
    {
        var body = new RetainedHttpArtifact(
            "body.json",
            revision.Payload.LongLength,
            revision.Sha256
        );
        var binding = new RetainedHttpCatalogBinding(
            "bound",
            null,
            Source,
            Source,
            Show,
            null,
            body,
            "sources.jsonl.gz",
            2
        );
        var provenance = new RetainedHttpProviderProvenance(
            revision.Uuid,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            "https://phish.in/api/v1/shows/11",
            200,
            body,
            body,
            body,
            0,
            body,
            binding
        );
        return Project(
            new
            {
                kind = "provider_input",
                provider = revision.Resource.Provider,
                row = new
                {
                    uuid = revision.Uuid,
                    artist_id = 1,
                    artist_uuid = Artist,
                },
                input = revision,
                retained_http = provenance,
                catalog_binding = binding,
                source_context = new
                {
                    id = 10,
                    uuid = Source,
                    artist_id = 1,
                    show_id = 20,
                    upstream_identifier = "11",
                },
                show_context = new
                {
                    id = 20,
                    uuid = Show,
                    artist_id = 1,
                },
            }
        );
    }

    private static (JsonElement Root, ShadowCatalogInput Directory) LocalInput()
    {
        var root = Input(
            new("local", "metadata_root", "trey"),
            "local-http-json-v1",
            """
            {"data":{"2020-01-02":[{"dir":"target","venue":"Example Hall"},{"dir":"neighbor"}]}}
            """
        );
        var directory = Input(
            NormalizationProviderResources.LocalDirectory("trey", "target"),
            "local-http-json-v1",
            """
            {"tracks":[{"mp3":"song.mp3","tags":{"title":"Morning","track":{"no":1}}},
              {"mp3":"unmatched.mp3","tags":{"title":"Evening","track":{"no":2}}}],"txts":[]}
            """
        );
        var rootBody = new RetainedHttpArtifact("root.json", root.Payload.LongLength, root.Sha256);
        var body = new RetainedHttpArtifact(
            "directory.json",
            directory.Payload.LongLength,
            directory.Sha256
        );
        var binding = new RetainedLocalCatalogBinding(
            "bound",
            null,
            Source,
            false,
            Source,
            Show,
            null,
            [Source],
            [new(1, Artist, "trey")],
            [
                new(
                    0,
                    "https://audio.relisten.net/trey/target/song.mp3",
                    "bound",
                    null,
                    Track,
                    Source,
                    SourceSet
                ),
                new(
                    1,
                    "https://audio.relisten.net/trey/target/different.mp3",
                    "bound",
                    null,
                    Guid.NewGuid(),
                    Source,
                    SourceSet
                ),
            ],
            body,
            "sources.jsonl.gz",
            2
        );
        var provenance = new RetainedLocalProviderProvenance(
            directory.Uuid,
            root.Uuid,
            DateTimeOffset.UtcNow,
            "trey",
            "target",
            "https://audio.relisten.net/trey/target/metadata.json",
            rootBody,
            rootBody,
            body,
            body,
            body,
            0,
            body,
            binding
        );
        var rootRow = JsonSerializer.SerializeToElement(
            new
            {
                kind = "local_root_input",
                row = new { uuid = root.Uuid },
                input = root,
            },
            Json
        );
        return (
            rootRow,
            Project(
                new
                {
                    kind = "provider_input",
                    provider = "local",
                    row = new
                    {
                        uuid = directory.Uuid,
                        artist_id = 1,
                        artist_uuid = Artist,
                    },
                    input = directory,
                    retained_local = provenance,
                    source_context = new
                    {
                        id = 10,
                        uuid = Source,
                        artist_id = 1,
                        show_id = 20,
                    },
                    show_context = new
                    {
                        id = 20,
                        uuid = Show,
                        artist_id = 1,
                    },
                }
            )
        );
    }

    private static ShadowCatalogInput Project(object value) =>
        new(
            JsonSerializer.SerializeToElement(value, Json),
            null,
            new(1, Artist, "Fixture artist", []),
            "native"
        );
}
