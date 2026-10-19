using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;
using Relisten.Normalization.Batch;

namespace RelistenApiTests.Normalization;

/// <summary>Exercises pinned operator files and durable provider envelopes without transport.</summary>
[TestFixture]
public sealed class TestCompositionWorkSearchBatch
{
    private const long ResponseReserve = 8L * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
    private static readonly Guid FirstArtist = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid SecondArtist = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private string directory = null!;

    [SetUp]
    public void SetUp()
    {
        directory = Path.Combine(
            Path.GetTempPath(),
            "composition-work-search-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(directory);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(directory, recursive: true);

    [TestCase(false)]
    [TestCase(true)]
    public async Task ChangedLateTaskMemberOrArtifactPinBlocksEveryClientConsultation(
        bool changedPin
    )
    {
        var prepared = await PrepareAsync(
            mutateLastFamily: changedPin
                ? null
                : family =>
                    family["sourceMembers"]![0]!["artistUuid"] = Guid.NewGuid().ToString("D")
        );
        if (changedPin)
        {
            await File.AppendAllTextAsync(prepared.TaskPath, "changed after pinning");
        }
        var client = new DurableClient(cached: true);

        var run = () =>
            new CompositionWorkSearchBatch(client).RunAsync(prepared.Path, Output, prepared.Sha256);

        await run.Should().ThrowAsync<InvalidDataException>();
        client.Gets.Should().BeEmpty();
        client.Advances.Should().BeEmpty();
        client.InputReads.Should().Be(0);
        Directory.Exists(Output).Should().BeFalse();
    }

    [Test]
    public async Task SharedDictionaryScopesProduceOnlyLiteralWorkQueriesAndZeroHitsNeverEstablishAbsence()
    {
        var prepared = await PrepareAsync(duplicateScopes: true);
        var client = new DurableClient(cached: true);

        var result = await RunAsync(prepared, client);

        result.Tasks.Should().HaveCount(2);
        foreach (var task in result.Tasks)
        {
            var command = task.Task.Command;
            command.Search!.Query.Kind.Should().Be(MusicBrainzLookupKind.Work);
            command.Search.Query.Text.Should().Be(task.Task.SourceMembers[0].Title);
            command.Search.Query.RelatedArtist.Should().BeNull();
            command.Search.Offset.Should().Be(0);
            command.Search.PlanIdentity!.RequestSha256.Should().Be(prepared.Sha256);
            command.Lookup.Should().BeNull();
            command.CatalogUuids.Should().HaveCount(2).And.OnlyHaveUniqueItems();
            task.Task.SourceMembers.Should().HaveCount(4);
            task.Task.SourceMembers.Select(member => member.ArtistUuid)
                .Distinct()
                .Should()
                .BeEquivalentTo(new[] { FirstArtist, SecondArtist });
            task.ReportedCount.Should().Be(0);
            task.ReturnedSlots.Should().Be(0);
            task.PageState.Should().Be("observed_first_page_end");
        }
        client
            .Gets.Should()
            .HaveCount(2, "search hits never enqueue hydration or performer queries");
        client.Advances.Should().BeEmpty();
        result.ExternalUniverseComplete.Should().BeFalse();
        result.PublicationPermitted.Should().BeFalse();
    }

    [TestCase("application/json; charset=utf-8")]
    [TestCase("application/vnd.musicbrainz+json")]
    public async Task SharedTransportJsonMediaTypesRemainOriginalCaptureMetadata(string mediaType)
    {
        var prepared = await PrepareAsync();
        var client = new DurableClient(cached: true) { InputMediaType = mediaType };

        var result = await RunAsync(prepared, client);

        result.State.Should().Be("completed");
        foreach (var task in result.Tasks)
        {
            var exported = JsonSerializer.Deserialize<NormalizationInputRevision>(
                await File.ReadAllBytesAsync(Path.Combine(Output, task.InputFile!)),
                Json
            )!;
            exported.MediaType.Should().Be(mediaType);
            exported.Payload.Should().Equal(client.Inputs[task.InputUuid!.Value].Payload);
        }
        client.Advances.Should().BeEmpty();
    }

    [Test]
    public async Task CachedWorkPageKeepsOriginalInputBytesAndFetchEnvelopeWithoutDispatch()
    {
        var prepared = await PrepareAsync();
        var client = new DurableClient(cached: true);

        var result = await RunAsync(prepared, client);

        result.ReservedServiceCalls.Should().Be(0);
        foreach (var task in result.Tasks)
        {
            var original = client.Inputs[task.InputUuid!.Value];
            var exported = JsonSerializer.Deserialize<NormalizationInputRevision>(
                await File.ReadAllBytesAsync(Path.Combine(Output, task.InputFile!)),
                Json
            )!;
            exported.Should().BeEquivalentTo(original);
            exported.Payload.Should().Equal(original.Payload);
            task.InputSha256.Should().Be(original.Sha256);
            var capture = client.Captures[task.Receipt!.Result!.FetchUuid!.Value];
            using var envelope = JsonDocument.Parse(
                await File.ReadAllBytesAsync(
                    Path.Combine(
                        Output,
                        "tasks",
                        task.Task.Command.CommandUuid.ToString("D"),
                        "capture.json"
                    )
                )
            );
            envelope
                .RootElement.GetProperty("fetch")
                .Deserialize<NormalizationInputFetch>(Json)
                .Should()
                .BeEquivalentTo(capture.Fetch);
            envelope
                .RootElement.GetProperty("revision")
                .GetProperty("uuid")
                .GetGuid()
                .Should()
                .Be(original.Uuid);
            envelope
                .RootElement.GetProperty("revision")
                .GetProperty("capturedAt")
                .GetDateTime()
                .Should()
                .Be(original.CapturedAt);
        }
        client.Advances.Should().BeEmpty();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ResumeRejectsChangedCheckpointMembersOrCommandBeforeClientConsultation(
        bool changeCommand
    )
    {
        var prepared = await PrepareAsync();
        var client = new DurableClient(cached: true);
        await RunAsync(prepared, client);
        var checkpointPath = Path.Combine(Output, "checkpoint.json");
        var checkpoint = JsonNode.Parse(await File.ReadAllBytesAsync(checkpointPath))!;
        var task = checkpoint["tasks"]![1]!["task"]!;
        var commandUuid = task["command"]!["commandUuid"]!.ToJsonString();
        if (changeCommand)
        {
            task["command"]!["search"]!["query"]!["text"] = "Unobserved replacement query";
        }
        else
        {
            task["sourceMembers"]![0]!["title"] = "Unobserved replacement dictionary title";
        }
        task["command"]!["commandUuid"]!.ToJsonString().Should().Be(commandUuid);
        await File.WriteAllTextAsync(checkpointPath, checkpoint.ToJsonString(Json));
        client.Gets.Clear();
        var inputReads = client.InputReads;

        var resume = () =>
            new CompositionWorkSearchBatch(client).RunAsync(
                prepared.Path,
                Output,
                prepared.Sha256,
                resume: true
            );

        await resume.Should().ThrowAsync<InvalidDataException>();
        client.Gets.Should().BeEmpty();
        client.Advances.Should().BeEmpty();
        client.InputReads.Should().Be(inputReads);
    }

    [Test]
    public async Task ResumeRejectsAlteredRetainedInputWithoutOverwritingItsEvidence()
    {
        var prepared = await PrepareAsync();
        var client = new DurableClient(cached: true);
        var completed = await RunAsync(prepared, client);
        var inputPath = Path.Combine(Output, completed.Tasks[0].InputFile!);
        await File.AppendAllTextAsync(inputPath, "\n");
        var alteredBytes = await File.ReadAllBytesAsync(inputPath);

        var resume = () =>
            new CompositionWorkSearchBatch(client).RunAsync(
                prepared.Path,
                Output,
                prepared.Sha256,
                resume: true
            );

        await resume.Should().ThrowAsync<InvalidDataException>();
        (await File.ReadAllBytesAsync(inputPath)).Should().Equal(alteredBytes);
        client.Advances.Should().BeEmpty();
    }

    [Test]
    public async Task ZeroCallBoundAuthenticatesTheFullQueueAndLeavesItUnexecuted()
    {
        var prepared = await PrepareAsync(limits: Limits() with { MaximumServiceCalls = 0 });
        var client = new DurableClient(cached: false);

        var result = await RunAsync(prepared, client);

        result.State.Should().Be("service_call_bound");
        result.Tasks.Should().HaveCount(2);
        result
            .Tasks.Should()
            .OnlyContain(task => task.State == "unexecuted" && task.Receipt == null);
        result.ReservedServiceCalls.Should().Be(0);
        client
            .Gets.Should()
            .HaveCount(1, "the first durable cache read cannot authorize a dispatch");
        client.Advances.Should().BeEmpty();
        client.InputReads.Should().Be(0);
    }

    [Test]
    public async Task ArtifactHeadroomKeepsTerminalAuditWritableWhenCachedInputCannotFit()
    {
        var cap = ResponseReserve + 128 * 1024;
        var prepared = await PrepareAsync(limits: Limits() with { MaximumArtifactBytes = cap });
        var client = new DurableClient(cached: true) { PayloadPaddingBytes = 256 * 1024 };

        var result = await RunAsync(prepared, client);

        result.State.Should().Be("artifact_byte_bound");
        result.ReservedServiceCalls.Should().Be(0);
        result.Tasks.Should().HaveCount(2);
        result.Tasks[0].State.Should().Be("pending");
        result.Tasks[1].State.Should().Be("unexecuted");
        result.Tasks.Should().OnlyContain(task => task.InputFile == null);
        client.Advances.Should().BeEmpty();
        Directory.Exists(Path.Combine(Output, "inputs")).Should().BeFalse();
        Directory
            .EnumerateFiles(Output, "*.writing", SearchOption.AllDirectories)
            .Should()
            .BeEmpty();
        Directory
            .EnumerateFiles(Output, "*", SearchOption.AllDirectories)
            .Sum(path => new FileInfo(path).Length)
            .Should()
            .BeLessThanOrEqualTo(cap);
        var checkpoint = JsonSerializer.Deserialize<CompositionWorkSearchCheckpoint>(
            await File.ReadAllBytesAsync(Path.Combine(Output, "checkpoint.json")),
            Json
        )!;
        checkpoint.State.Should().Be("artifact_byte_bound");
        using var manifest = JsonDocument.Parse(
            await File.ReadAllBytesAsync(Path.Combine(Output, "manifest.json"))
        );
        manifest.RootElement.GetProperty("state").GetString().Should().Be("artifact_byte_bound");
        foreach (var input in client.Inputs.Values)
        {
            Hash(input.Payload)
                .Should()
                .Be(input.Sha256, "capacity exhaustion cannot rewrite the original cached bytes");
        }
    }

    [Test]
    public async Task LostDispatchWithFailedRecoveryChargesAnUnknownCallBesideTheAlreadyMeasuredAttempt()
    {
        var prepared = await PrepareAsync();
        var client = new DurableClient(cached: false) { LoseReceiptAfterPendingAdvance = true };

        var run = () => RunAsync(prepared, client);

        await run.Should().ThrowAsync<IOException>();
        var checkpoint = JsonSerializer.Deserialize<CompositionWorkSearchCheckpoint>(
            await File.ReadAllBytesAsync(Path.Combine(Output, "checkpoint.json")),
            Json
        )!;
        var command = client.Advances.Single().CommandUuid;
        var measured = client.Responses.Single();
        checkpoint.State.Should().Be("interrupted");
        checkpoint.ReservedServiceCalls.Should().Be(1);
        checkpoint.AttemptBodyCharges.Should().HaveCount(2);
        checkpoint
            .AttemptBodyCharges[measured.Key]
            .Should()
            .Be(measured.Value.Response.Body!.LongLength);
        checkpoint.AttemptBodyCharges[command].Should().Be(ResponseReserve);
        checkpoint.UnmeasuredAttempts.Should().Equal(command);
        checkpoint.Tasks[0].State.Should().Be("pending");
        checkpoint.Tasks[1].State.Should().Be("unexecuted");
        using var manifest = JsonDocument.Parse(
            await File.ReadAllBytesAsync(Path.Combine(Output, "manifest.json"))
        );
        manifest
            .RootElement.GetProperty("responseBudgetChargedBytes")
            .GetInt64()
            .Should()
            .Be(ResponseReserve + measured.Value.Response.Body.LongLength);
    }

    [Test]
    public async Task MeasuredSmallBodiesLeaveRoomForTheNextResponseReservation()
    {
        var prepared = await PrepareAsync(
            limits: Limits() with
            {
                MaximumNewResponseBytes = ResponseReserve + 1024,
            }
        );
        var client = new DurableClient(cached: false);

        var result = await RunAsync(prepared, client);

        result.State.Should().Be("completed");
        result.ReservedServiceCalls.Should().Be(2);
        client.Advances.Should().HaveCount(2);
        result
            .AttemptBodyCharges.Values.Should()
            .Equal(client.Responses.Values.Select(response => response.Response.Body!.LongLength));
        result.AttemptBodyCharges.Values.Sum().Should().BeLessThan(1024);
        result.UnmeasuredAttempts.Should().BeEmpty();
    }

    [TestCase(false, "service_call_bound")]
    [TestCase(true, "response_byte_bound")]
    public async Task ExhaustedBoundPreservesTheRemainingFrozenQueue(
        bool responseBound,
        string expectedState
    )
    {
        var limits = Limits() with
        {
            MaximumServiceCalls = responseBound ? 2 : 1,
            MaximumNewResponseBytes = responseBound ? ResponseReserve : ResponseReserve * 2,
        };
        var prepared = await PrepareAsync(limits: limits);
        var client = new DurableClient(cached: false);

        var result = await RunAsync(prepared, client);

        result.State.Should().Be(expectedState);
        result.Tasks.Should().HaveCount(2);
        result.Tasks[0].State.Should().Be("completed");
        result.Tasks[1].State.Should().Be("unexecuted");
        result.Tasks[1].Receipt.Should().BeNull();
        result.ReservedServiceCalls.Should().Be(1);
        client.Advances.Should().HaveCount(1);
        result.ExternalUniverseComplete.Should().BeFalse();
    }

    [Test]
    public async Task CancellationRecoversSavedDispatchAndResumeKeepsDeadlineAndConsumedReservation()
    {
        var prepared = await PrepareAsync(limits: Limits() with { MaximumServiceCalls = 1 });
        using var cancellation = new CancellationTokenSource();
        var client = new DurableClient(cached: false) { CancelAfterDispatch = cancellation };
        var batch = new CompositionWorkSearchBatch(client);

        var run = () =>
            batch.RunAsync(prepared.Path, Output, prepared.Sha256, ct: cancellation.Token);

        await run.Should().ThrowAsync<OperationCanceledException>();
        var interrupted = JsonSerializer.Deserialize<CompositionWorkSearchCheckpoint>(
            await File.ReadAllBytesAsync(Path.Combine(Output, "checkpoint.json")),
            Json
        )!;
        interrupted.State.Should().Be("cancelled");
        interrupted.ReservedServiceCalls.Should().Be(1);
        interrupted
            .Tasks[0]
            .State.Should()
            .Be("completed", "recovery reads the original durable service receipt");
        interrupted.UnmeasuredAttempts.Should().BeEmpty();
        client.CancelAfterDispatch = null;

        var resumed = await batch.RunAsync(prepared.Path, Output, prepared.Sha256, resume: true);

        resumed.Deadline.Should().Be(interrupted.Deadline);
        resumed.ReservedServiceCalls.Should().Be(1);
        resumed.AttemptBodyCharges.Should().BeEquivalentTo(interrupted.AttemptBodyCharges);
        resumed.Tasks[0].InputUuid.Should().Be(interrupted.Tasks[0].InputUuid);
        resumed.Tasks[1].State.Should().Be("unexecuted");
        resumed.State.Should().Be("service_call_bound");
        client.Advances.Should().HaveCount(1, "resume cannot refund a dispatched call");
    }

    [TestCase("resource")]
    [TestCase("capture_profile")]
    [TestCase("later_page")]
    public async Task WrongQualifiedCaptureIsRejectedAndLaterPageCannotClaimFirstPageAuthority(
        string fault
    )
    {
        var prepared = await PrepareAsync();
        var client = new DurableClient(cached: true)
        {
            WrongResource = fault == "resource",
            WrongProfile = fault == "capture_profile",
            PageOffset = fault == "later_page" ? 100 : 0,
        };
        if (fault != "later_page")
        {
            var run = () => RunAsync(prepared, client);
            await run.Should().ThrowAsync<InvalidDataException>();
        }
        else
        {
            var result = await RunAsync(prepared, client);
            result.Tasks.Should().OnlyContain(task => task.PageState == "malformed_retained_page");
            result
                .Tasks.Should()
                .OnlyContain(task => task.Issues!.Contains("invalid_first_work_search_page"));
            result.ExternalUniverseComplete.Should().BeFalse();
            result.PublicationPermitted.Should().BeFalse();
        }
        client.Advances.Should().BeEmpty();
    }

    private string Output => Path.Combine(directory, "output");

    private Task<CompositionWorkSearchCheckpoint> RunAsync(
        Prepared prepared,
        DurableClient client
    ) => new CompositionWorkSearchBatch(client).RunAsync(prepared.Path, Output, prepared.Sha256);

    private static CompositionWorkSearchLimits Limits() =>
        new(MinimumFreeBytes: 0, MaximumDuration: TimeSpan.FromMinutes(1));

    private async Task<Prepared> PrepareAsync(
        bool duplicateScopes = false,
        CompositionWorkSearchLimits? limits = null,
        Action<JsonObject>? mutateLastFamily = null
    )
    {
        // Synthetic exporter evidence has two distinct catalog artists per literal.
        // Repeating an observation under another scope must not invent another member.
        var source = Path.Combine(directory, "source");
        Directory.CreateDirectory(source);
        var sourceChunk = Path.Combine(source, "dictionary-source.jsonl");
        await File.WriteAllTextAsync(sourceChunk, "synthetic retained dictionary witness\n");
        var sourceSha = Hash(await File.ReadAllBytesAsync(sourceChunk));
        var scopes = duplicateScopes
            ? new[] { "fixture:first", "fixture:second" }
            : new[] { "fixture:first" };
        var names = new List<MusicDictionaryName>();
        var titles = new[] { "Tweezer", "Bouncing Around the Room" };
        for (var family = 0; family < titles.Length; family++)
        {
            foreach (var artist in new[] { FirstArtist, SecondArtist })
            {
                var song = new Guid(
                    family * 10 + (artist == FirstArtist ? 1 : 2),
                    0,
                    0,
                    new byte[8]
                );
                foreach (var scope in scopes)
                {
                    names.Add(
                        new(
                            scope,
                            artist,
                            "known:" + song,
                            song,
                            titles[family],
                            "dictionary:" + song,
                            [],
                            new(
                                source,
                                Path.GetFileName(sourceChunk),
                                names.Count + 1,
                                names.Count + 1,
                                "/proposal/title",
                                sourceSha
                            )
                        )
                    );
                }
            }
        }
        var dictionaryPath = Path.Combine(directory, "dictionary.jsonl.gz");
        await WriteRowsAsync(dictionaryPath, names);
        var unused = new MusicConsolidationArtifact("unused", 0, Hash([]), 0);
        var music = new MusicConsolidationInput(
            "music-consolidation-input-v1",
            "completed",
            new("unused", 0, Hash([]), 1),
            unused,
            new(
                Path.GetFileName(dictionaryPath),
                new FileInfo(dictionaryPath).Length,
                Hash(await File.ReadAllBytesAsync(dictionaryPath)),
                names.Count
            ),
            scopes
                .Select((scope, index) => new MusicConsolidationSource(index + 1, scope, source))
                .ToArray(),
            [
                new(
                    source,
                    Path.GetFileName(sourceChunk),
                    new FileInfo(sourceChunk).Length,
                    sourceSha
                ),
            ]
        );
        var manifestPath = Path.Combine(directory, "dictionary-manifest.json");
        await File.WriteAllBytesAsync(
            manifestPath,
            JsonSerializer.SerializeToUtf8Bytes(music, Json)
        );
        var families = new List<JsonObject>();
        foreach (var title in titles)
        {
            var members = names.Where(name => name.Title == title).ToArray();
            var page = new MusicBrainzSearchPageRequest(
                new(MusicBrainzLookupKind.Work, title),
                0,
                MusicBrainzCacheMode.ReuseFresh,
                TimeSpan.FromDays(7)
            );
            families.Add(
                JsonSerializer
                    .SerializeToNode(
                        new
                        {
                            retrievalTitleKey = title switch
                            {
                                "Tweezer" => "TWEEZER",
                                "Bouncing Around the Room" => "BOUNCING AROUND THE ROOM",
                                _ => throw new InvalidOperationException("Unknown fixture title."),
                            },
                            state = "work_search_required",
                            sourceMembers = members
                                .GroupBy(name => (name.ArtistUuid, name.LegacySongUuid))
                                .Select(group => new
                                {
                                    group.Key.ArtistUuid,
                                    group.Key.LegacySongUuid,
                                    title,
                                    observations = group.Select(name => new
                                    {
                                        dictionaryRow = names.IndexOf(name) + 1,
                                        name.Scope,
                                        name.SongKey,
                                        name.Witness,
                                        name.WorkKeys,
                                    }),
                                }),
                            tasks = new[]
                            {
                                new
                                {
                                    pageRequest = page,
                                    existingResource = MusicBrainzCandidateQueryBuilder
                                        .CreateDescriptor(page)
                                        .Resource,
                                    sourceMemberUuids = members
                                        .Select(name => name.LegacySongUuid)
                                        .Distinct()
                                        .ToArray(),
                                },
                            },
                        },
                        Json
                    )!
                    .AsObject()
            );
        }
        mutateLastFamily?.Invoke(families[^1]);
        var tasksPath = Path.Combine(directory, "tasks.jsonl.gz");
        await WriteRowsAsync(tasksPath, families);
        var request = new CompositionWorkSearchRequest(
            CompositionWorkSearchBatch.RequestVersion,
            Guid.NewGuid(),
            await PinAsync(manifestPath),
            await PinAsync(tasksPath),
            limits ?? Limits()
        );
        var requestPath = Path.Combine(directory, "request.json");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(request, Json);
        await File.WriteAllBytesAsync(requestPath, bytes);
        return new(requestPath, Hash(bytes), tasksPath);
    }

    private static async Task WriteRowsAsync<T>(string path, IEnumerable<T> rows)
    {
        await using var file = File.Create(path);
        await using var gzip = new GZipStream(file, CompressionMode.Compress);
        await using var writer = new StreamWriter(gzip, new UTF8Encoding(false));
        foreach (var row in rows)
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(row, Json));
        }
    }

    private static async Task<ShadowCompositionAcquisitionFile> PinAsync(string path) =>
        new(Path.GetFileName(path), Hash(await File.ReadAllBytesAsync(path)));

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private sealed record Prepared(string Path, string Sha256, string TaskPath);

    private sealed class DurableClient(bool cached) : ICompositionWorkSearchClient
    {
        private static readonly DateTime CapturedAt = new(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);
        private readonly Dictionary<Guid, ShadowCompositionAcquisitionReceipt> receipts = new();
        internal List<ShadowCompositionAcquisitionCommand> Gets { get; } = [];
        internal List<ShadowCompositionAcquisitionCommand> Advances { get; } = [];
        internal Dictionary<Guid, NormalizationInputRevision> Inputs { get; } = new();
        internal Dictionary<Guid, NormalizationCaptureResult> Captures { get; } = new();
        internal Dictionary<Guid, MusicBrainzSavedResponse> Responses { get; } = new();
        internal CancellationTokenSource? CancelAfterDispatch { get; set; }
        internal bool WrongResource { get; init; }
        internal bool WrongProfile { get; init; }
        internal string InputMediaType { get; init; } = "application/json";
        internal int PageOffset { get; init; }
        internal int PayloadPaddingBytes { get; init; }
        internal bool LoseReceiptAfterPendingAdvance { get; init; }
        internal int InputReads { get; private set; }
        private bool recoveryReadsFail;

        public Task<ShadowCompositionAcquisitionReceipt?> GetAsync(
            ShadowCompositionAcquisitionCommand command,
            CancellationToken ct
        )
        {
            Gets.Add(command);
            if (recoveryReadsFail)
            {
                throw new IOException("The service receipt is unavailable during recovery.");
            }
            if (receipts.TryGetValue(command.CommandUuid, out var receipt))
            {
                return Task.FromResult<ShadowCompositionAcquisitionReceipt?>(receipt);
            }
            if (LoseReceiptAfterPendingAdvance)
            {
                // An earlier response is measured, but its command still needs another advance.
                var pending = Save(command, dispatched: true) with
                {
                    State = MusicBrainzAcquisitionState.Pending,
                    Result = null,
                };
                receipts[command.CommandUuid] = pending;
                return Task.FromResult<ShadowCompositionAcquisitionReceipt?>(pending);
            }
            return Task.FromResult<ShadowCompositionAcquisitionReceipt?>(
                cached ? Save(command, dispatched: false) : null
            );
        }

        public Task<ShadowCompositionAcquisitionReceipt> AdvanceAsync(
            ShadowCompositionAcquisitionCommand command,
            CancellationToken ct
        )
        {
            Advances.Add(command);
            if (LoseReceiptAfterPendingAdvance)
            {
                recoveryReadsFail = true;
                throw new IOException("The next dispatch lost its receipt.");
            }
            var receipt = Save(command, dispatched: true);
            if (CancelAfterDispatch is { } cancellation)
            {
                cancellation.Cancel();
                throw new OperationCanceledException(ct);
            }
            return Task.FromResult(receipt);
        }

        private ShadowCompositionAcquisitionReceipt Save(
            ShadowCompositionAcquisitionCommand command,
            bool dispatched
        )
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    count = 0,
                    offset = PageOffset,
                    works = Array.Empty<object>(),
                    fixturePadding = new string('x', PayloadPaddingBytes),
                },
                Json
            );
            var input = new NormalizationInputRevision
            {
                Uuid = Guid.NewGuid(),
                Resource = WrongResource
                    ? command.Resource with
                    {
                        ProviderKey = "wrong-qualified-query",
                    }
                    : command.Resource,
                CaptureVersion = WrongProfile
                    ? "musicbrainz-recording-search-json-v1"
                    : "musicbrainz-work-search-json-v1",
                MediaType = InputMediaType,
                Payload = bytes,
                Sha256 = Hash(bytes),
                CapturedAt = CapturedAt,
            };
            Inputs.Add(input.Uuid, input);
            var fetch = new NormalizationInputFetch
            {
                Sequence = 7,
                FetchUuid = Guid.NewGuid(),
                InputRevisionUuid = input.Uuid,
                ExpectedGeneration = 3,
                ResourceGeneration = 4,
                AcceptedAsCurrent = true,
                AdvancedCurrent = false,
                ObservedAt = CapturedAt.AddHours(-1),
                CapturedAt = CapturedAt,
                ProviderRevision = "original-provider-revision",
            };
            Captures.Add(fetch.FetchUuid, new(input, fetch));
            var attempts = new List<MusicBrainzAcquisitionAttempt>();
            if (dispatched)
            {
                var attempt = Guid.NewGuid();
                attempts.Add(new(attempt, 1, "completed", 3, CapturedAt, CapturedAt, null));
                Responses.Add(
                    attempt,
                    new(
                        attempt,
                        new(
                            200,
                            "https://musicbrainz.org/ws/2/work",
                            new Dictionary<string, string[]>(),
                            bytes,
                            true,
                            CapturedAt
                        ),
                        Hash(bytes),
                        true,
                        CapturedAt
                    )
                );
            }
            var receipt = new ShadowCompositionAcquisitionReceipt(
                command.CommandUuid,
                MusicBrainzAcquisitionState.Completed,
                null,
                new(
                    dispatched
                        ? MusicBrainzAcquisitionDisposition.Captured
                        : MusicBrainzAcquisitionDisposition.Cached,
                    new(input.Uuid, command.Resource),
                    fetch.FetchUuid,
                    fetch.ResourceGeneration
                ),
                attempts,
                JsonSerializer.SerializeToElement(
                    new
                    {
                        original = true,
                        fetch.FetchUuid,
                        input.Uuid,
                    },
                    Json
                )
            );
            receipts.Add(command.CommandUuid, receipt);
            return receipt;
        }

        public Task<NormalizationInputRevision?> ReadInputAsync(
            Guid inputUuid,
            CancellationToken ct
        )
        {
            InputReads++;
            return Task.FromResult(Inputs.GetValueOrDefault(inputUuid));
        }

        public Task<MusicBrainzSavedResponse?> ReadResponseAsync(
            Guid attemptUuid,
            CancellationToken ct
        ) => Task.FromResult(Responses.GetValueOrDefault(attemptUuid));

        public Task<NormalizationCaptureResult?> ReadCaptureAsync(
            Guid fetchUuid,
            CancellationToken ct
        ) => Task.FromResult(Captures.GetValueOrDefault(fetchUuid));
    }
}
