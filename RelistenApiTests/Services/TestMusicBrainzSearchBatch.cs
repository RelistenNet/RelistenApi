using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
using Relisten.Normalization.Batch;

namespace RelistenApiTests.Services;

[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestMusicBrainzSearchBatch
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
    private NormalizationDomainFixture fixture = null!;
    private NormalizationEvidenceService evidence = null!;
    private NormalizationMusicBrainzInputStore store = null!;
    private string directory = null!;
    private readonly List<HttpClient> clients = [];

    [OneTimeSetUp]
    public void Start()
    {
        fixture = new();
        evidence = new(fixture.Db);
        store = new(fixture.Db);
    }

    [SetUp]
    public async Task Prepare()
    {
        directory = Path.Combine(
            Path.GetTempPath(),
            "musicbrainz-search-batch-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(directory);
        await ClearCooldown();
    }

    [TearDown]
    public void Cleanup()
    {
        foreach (var client in clients)
        {
            client.Dispose();
        }
        clients.Clear();
        Directory.Delete(directory, true);
    }

    [OneTimeTearDown]
    public void Stop() => fixture?.Dispose();

    [Test]
    public async Task FrozenPlanPrecedesHttpAndZeroResultsRetainExactEvidenceWithoutUniversalNegative()
    {
        var input = await WriteRequest();
        var planUuid = Guid.NewGuid();
        var body = Body(0, 0, []);
        var handler = new Stub(
            (_, _) =>
            {
                File.Exists(Path.Combine(Output("zero"), "plan.json")).Should().BeTrue();
                var plan = Read<MusicBrainzSearchFrozenPlan>(Output("zero"), "plan.json");
                plan.PlanUuid.Should().Be(planUuid);
                plan.Variants.Single()
                    .Pages.Select(x => x.Request.Offset)
                    .Should()
                    .Equal(0, 50, 100, 150);
                plan.Variants.Single()
                    .Pages.Select(x => x.CommandUuid)
                    .Distinct()
                    .Should()
                    .HaveCount(4);
                return Task.FromResult(Response(body));
            }
        );

        var report = await Batch(handler).RunAsync(input, Output("zero"), planUuid);

        report.State.Should().Be("completed");
        handler.Calls.Should().Be(1);
        var receipt = Read<MusicBrainzSearchPlanReceipt>(
            report.OutputDirectory,
            "plan-receipt.json"
        );
        receipt.ExternalUniverseComplete.Should().BeFalse();
        receipt.SeedContextAccepted.Should().BeFalse();
        receipt.HydrationImplemented.Should().BeFalse();
        receipt.Variants.Single().ObservedEnd.Should().BeTrue();
        receipt.Variants.Single().WindowComplete.Should().BeTrue();
        receipt.UnhydratedEntities.Should().BeEmpty();
        var saved = receipt.Pages.Single(x => x.Acquisition != null);
        var command = saved.Acquisition!;
        var prefix =
            $"pages/0-0-{command.CommandUuid:D}/attempts/1-{command.Attempts.Single().AttemptUuid:D}";
        (await File.ReadAllBytesAsync(Path.Combine(report.OutputDirectory, prefix, "body.bin")))
            .Should()
            .Equal(body);
        (await File.ReadAllBytesAsync(Path.Combine(report.OutputDirectory, prefix, "input.bin")))
            .Should()
            .Equal(body);
        (await File.ReadAllBytesAsync(Path.Combine(report.OutputDirectory, "request.json")))
            .Should()
            .Equal(await File.ReadAllBytesAsync(input));
        await CheckArtifactHashes(report.OutputDirectory);

        var replay = await Batch(handler)
            .RunAsync(input, Output("zero-replay"), planUuid, Receipt("zero"));
        replay.State.Should().Be("completed");
        handler.Calls.Should().Be(1);
        Read<MusicBrainzSearchFrozenPlan>(replay.OutputDirectory, "plan.json")
            .Should()
            .BeEquivalentTo(receipt.Plan);
    }

    [Test]
    public async Task EachInvocationAcquiresOnePageAndResumptionKeepsCommandsAndAllUnhydratedHits()
    {
        var input = await WriteRequest();
        var ids = Enumerable.Range(0, 51).Select(_ => Guid.NewGuid()).ToArray();
        var handler = new Stub(
            (request, _) =>
                Task.FromResult(
                    Response(
                        request.RequestUri!.Query.Contains("offset=50")
                            ? Body(51, 50, ids[50..])
                            : Body(51, 0, ids[..50])
                    )
                )
        );
        var batch = Batch(handler);
        var uuid = Guid.NewGuid();

        var first = await batch.RunAsync(input, Output("first"), uuid);
        first.State.Should().Be("pending");
        handler.Calls.Should().Be(1);
        var before = Read<MusicBrainzSearchPlanReceipt>(Output("first"), "plan-receipt.json");
        before.Pages.Count(x => x.Acquisition != null).Should().Be(1);
        before.UnhydratedEntities.Should().HaveCount(50);
        before.Issues.Should().Contain("HydrationLimit");
        await ClearCooldown();
        var second = await batch.RunAsync(input, Output("second"), uuid, Receipt("first"));

        second.State.Should().Be("completed");
        handler.Calls.Should().Be(2);
        var after = Read<MusicBrainzSearchPlanReceipt>(Output("second"), "plan-receipt.json");
        after.Plan.Should().BeEquivalentTo(before.Plan);
        after.Pages.Count(x => x.Acquisition != null).Should().Be(2);
        after
            .Pages.Where(x => x.Observation != null)
            .SelectMany(x => x.Observation!.Hits!)
            .Should()
            .HaveCount(51);
        after.UnhydratedEntities.Should().HaveCount(51);
        after.HydrationSelectionCeiling.Should().Be(20);
        after.Variants.Single().ObservedEnd.Should().BeTrue();
        after.ExternalUniverseComplete.Should().BeFalse();
        await CheckArtifactHashes(second.OutputDirectory);
    }

    [TestCase("failed")]
    [TestCase("pending")]
    public async Task LaterFailureOrPendingPreservesEarlierPageAndCannotClaimObservedEnd(
        string outcome
    )
    {
        var input = await WriteRequest();
        var ids = Enumerable.Range(0, 50).Select(_ => Guid.NewGuid()).ToArray();
        var handler = new Stub(
            (request, _) =>
                Task.FromResult(
                    request.RequestUri!.Query.Contains("offset=50")
                        ? Response(
                            Encoding.UTF8.GetBytes("{\"error\":\"controlled\"}"),
                            outcome == "failed"
                                ? HttpStatusCode.NotFound
                                : HttpStatusCode.ServiceUnavailable
                        )
                        : Response(Body(80, 0, ids))
                )
        );
        var batch = Batch(handler);
        var uuid = Guid.NewGuid();
        await batch.RunAsync(input, Output("prefix"), uuid);
        await ClearCooldown();

        var report = await batch.RunAsync(input, Output("later"), uuid, Receipt("prefix"));

        report.State.Should().Be(outcome == "failed" ? "completed_partial" : "pending");
        handler.Calls.Should().Be(2);
        var receipt = Read<MusicBrainzSearchPlanReceipt>(Output("later"), "plan-receipt.json");
        receipt.UnhydratedEntities.Should().HaveCount(50);
        receipt.Variants.Single().ObservedEnd.Should().BeFalse();
        receipt.Variants.Single().WindowComplete.Should().BeFalse();
        receipt.Issues.Should().Contain(outcome == "failed" ? "PageFailed" : "PagePending");
        Directory
            .GetFiles(Output("later"), "body.bin", SearchOption.AllDirectories)
            .Should()
            .HaveCount(2);
    }

    [TestCase("count-drift", "ChangingResultCount")]
    [TestCase("duplicate", "DuplicateHit")]
    [TestCase("short", "ShortPageBeforeCount")]
    [TestCase("malformed", "PageDecodeIssues")]
    public async Task InconsistentReturnedPagesKeepHitsAndExplicitlyBlockCoverage(
        string kind,
        string expectedIssue
    )
    {
        var input = await WriteRequest();
        var ids = Enumerable.Range(0, 51).Select(_ => Guid.NewGuid()).ToArray();
        var handler = new Stub(
            (request, _) =>
            {
                var second = request.RequestUri!.Query.Contains("offset=50");
                var body = !second
                    ? Body(51, 0, ids[..50])
                    : kind switch
                    {
                        "count-drift" => Body(52, 50, ids[50..]),
                        "duplicate" => Body(51, 50, ids[..1]),
                        "short" => Body(80, 50, ids[50..]),
                        _ => Encoding.UTF8.GetBytes(
                            "{\"count\":51,\"offset\":50,\"works\":[{\"id\":\"invalid\"}]}"
                        ),
                    };
                return Task.FromResult(Response(body));
            }
        );
        var batch = Batch(handler);
        var uuid = Guid.NewGuid();
        await batch.RunAsync(input, Output("initial"), uuid);
        await ClearCooldown();

        var report = await batch.RunAsync(input, Output("inconsistent"), uuid, Receipt("initial"));

        report.State.Should().Be("completed_partial");
        var receipt = Read<MusicBrainzSearchPlanReceipt>(
            Output("inconsistent"),
            "plan-receipt.json"
        );
        receipt.Issues.Should().Contain(expectedIssue);
        receipt.Variants.Single().WindowComplete.Should().BeFalse();
        receipt.Pages.First().Observation!.Hits.Should().HaveCount(50);
        handler.Calls.Should().Be(2);
    }

    [Test]
    public async Task FourPageBoundStopsAtTwoHundredSlotsWithoutRequestingAnotherOffset()
    {
        var input = await WriteRequest();
        var ids = Enumerable.Range(0, 200).Select(_ => Guid.NewGuid()).ToArray();
        var handler = new Stub(
            (request, _) =>
            {
                var offset = new[] { 0, 50, 100, 150 }.Single(x =>
                    request.RequestUri!.Query.Contains($"offset={x}&")
                );
                return Task.FromResult(Response(Body(1000, offset, ids[offset..(offset + 50)])));
            }
        );
        var batch = Batch(handler);
        var uuid = Guid.NewGuid();
        MusicBrainzSearchBatchReport? report = null;
        for (var index = 0; index < 4; index++)
        {
            await ClearCooldown();
            report = await batch.RunAsync(
                input,
                Output("bound-" + index),
                uuid,
                index == 0 ? null : Receipt("bound-" + (index - 1))
            );
            handler.Calls.Should().Be(index + 1);
        }

        report!.State.Should().Be("completed_partial");
        var receipt = Read<MusicBrainzSearchPlanReceipt>(
            report.OutputDirectory,
            "plan-receipt.json"
        );
        receipt.Issues.Should().Contain("PageLimit");
        receipt.UnhydratedEntities.Should().HaveCount(200);
        receipt.Variants.Single().ObservedEnd.Should().BeFalse();
        await batch.RunAsync(input, Output("bound-replay"), uuid, Receipt("bound-3"));
        handler.Calls.Should().Be(4);
    }

    [Test]
    public async Task ExcessSupportedContextRemainsInRequestWhileOnlyThreeVariantsAreFrozen()
    {
        var capture = await evidence.CaptureInputAsync(
            new(
                Guid.NewGuid(),
                new("search-batch-test", "seed", Guid.NewGuid().ToString("D")),
                Encoding.UTF8.GetBytes(
                    "{\"title\":\"context title\",\"authors\":[\"A\",\"B\",\"C\"],\"role\":\"composer\"}"
                ),
                "application/json",
                "search-seed-v1",
                0,
                DateTimeOffset.UtcNow
            )
        );
        var request = new MusicBrainzSearchBatchRequest(
            new(
                MusicBrainzLookupKind.Work,
                new("context title", new(capture.Revision.Uuid, "/title")),
                new[] { "A", "B", "C" }
                    .Select(
                        (name, index) =>
                            new MusicBrainzSearchArtistSeed(
                                MusicBrainzSearchArtistUse.WorkAuthor,
                                new(name, new(capture.Revision.Uuid, "/authors/" + index)),
                                Role: new("composer", new(capture.Revision.Uuid, "/role"))
                            )
                    )
                    .ToArray()
            ),
            [new(capture.Revision.Uuid, capture.Revision.Resource)],
            MusicBrainzCacheMode.Refresh
        );
        var input = Path.Combine(directory, "context.json");
        await File.WriteAllBytesAsync(input, JsonSerializer.SerializeToUtf8Bytes(request, Json));
        var handler = new Stub((_, _) => Task.FromResult(Response(Body(0, 0, []))));
        var batch = Batch(handler);
        var uuid = Guid.NewGuid();
        for (var index = 0; index < 3; index++)
        {
            await ClearCooldown();
            await batch.RunAsync(
                input,
                Output("variant-" + index),
                uuid,
                index == 0 ? null : Receipt("variant-" + (index - 1))
            );
            handler.Calls.Should().Be(index + 1);
        }

        var receipt = Read<MusicBrainzSearchPlanReceipt>(Output("variant-2"), "plan-receipt.json");
        receipt.Report.State.Should().Be("completed_partial");
        receipt.Plan.Variants.Should().HaveCount(3);
        receipt.Plan.Variants.SelectMany(x => x.Pages).Should().HaveCount(12);
        receipt.Plan.Variants[0].Query.RelatedArtist.Should().BeNull();
        receipt.Plan.Issues.Should().Contain("QueryVariantLimit");
        receipt.SeedContextAccepted.Should().BeFalse();
        Read<MusicBrainzSearchBatchRequest>(Output("variant-2"), "request.json")
            .Seed.RelatedArtists.Should()
            .HaveCount(3);
    }

    [Test]
    public async Task UnexpectedLaterCommandProducesOffsetGapInsteadOfCompleteCoverage()
    {
        var input = await WriteRequest();
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "UPDATE normalization.musicbrainz_gate SET not_before=clock_timestamp()+interval '1 hour' WHERE singleton"
            )
        );
        var handler = new Stub((_, _) => Task.FromResult(Response(Body(0, 50, []))));
        var batch = Batch(handler);
        var uuid = Guid.NewGuid();
        await batch.RunAsync(input, Output("gap-plan"), uuid);
        handler.Calls.Should().Be(0);
        var plan = Read<MusicBrainzSearchFrozenPlan>(Output("gap-plan"), "plan.json");
        var later = plan.Variants[0].Pages[1];
        await ClearCooldown();
        await ServiceFor(handler).AcquireSearchAsync(later.CommandUuid, later.Request);

        var report = await batch.RunAsync(input, Output("gap-result"), uuid, Receipt("gap-plan"));

        report.State.Should().Be("completed_partial");
        handler.Calls.Should().Be(1);
        var receipt = Read<MusicBrainzSearchPlanReceipt>(
            report.OutputDirectory,
            "plan-receipt.json"
        );
        receipt.Issues.Should().Contain("OffsetGap");
        receipt.Variants.Single().WindowComplete.Should().BeFalse();
        receipt.Pages.Single(x => x.Offset == 50).Observation.Should().NotBeNull();
    }

    [Test]
    public async Task MissingTextDoesNotInventQueryOrCallHttp()
    {
        var input = await WriteRequest(missingText: true);
        var handler = new Stub(
            (_, _) => throw new AssertionException("Missing title reached HTTP.")
        );

        var report = await Batch(handler).RunAsync(input, Output("missing"), Guid.NewGuid());

        report.State.Should().Be("completed_partial");
        handler.Calls.Should().Be(0);
        var receipt = Read<MusicBrainzSearchPlanReceipt>(
            report.OutputDirectory,
            "plan-receipt.json"
        );
        receipt.Plan.Variants.Should().BeEmpty();
        receipt.Issues.Should().Contain("MissingSearchText");
    }

    [TestCase("unknown-url")]
    [TestCase("duplicate")]
    [TestCase("numeric-enum")]
    [TestCase("oversized")]
    [TestCase("uncited-text")]
    public async Task InvalidRequestIsRejectedBeforeOutputAdmissionOrHttp(string kind)
    {
        var input = await WriteRequest();
        var raw = await File.ReadAllTextAsync(input);
        raw = kind switch
        {
            "unknown-url" => raw.Insert(1, "\"url\":\"https://example.org/\","),
            "duplicate" => raw.Insert(1, "\"cacheMode\":\"refresh\","),
            "numeric-enum" => raw.Replace("\"cacheMode\":\"refresh\"", "\"cacheMode\":1"),
            "oversized" => new string(' ', 65537),
            _ => raw.Replace("retained literal", "invented title"),
        };
        await File.WriteAllTextAsync(input, raw);
        var handler = new Stub(
            (_, _) => throw new AssertionException("Invalid input reached HTTP.")
        );
        var run = () => Batch(handler).RunAsync(input, Output("invalid"), Guid.NewGuid());

        await run.Should().ThrowAsync<InvalidDataException>();
        handler.Calls.Should().Be(0);
        Directory.Exists(Output("invalid")).Should().BeFalse();
    }

    [TestCase("context")]
    [TestCase("input")]
    [TestCase("literal-evidence")]
    [TestCase("identity-evidence")]
    public async Task NullUntrustedMembersFailContextuallyBeforeAdmission(string kind)
    {
        var input = await WriteRequest();
        var request = Read<MusicBrainzSearchBatchRequest>(directory, "request.json");
        request = kind switch
        {
            "context" => request with { Seed = request.Seed with { RelatedArtists = [null!] } },
            "input" => request with { Inputs = [null!] },
            "literal-evidence" => request with
            {
                Seed = request.Seed with { Text = request.Seed.Text! with { Evidence = null! } },
            },
            _ => request with
            {
                Seed = request.Seed with
                {
                    RelatedArtists =
                    [
                        new(
                            MusicBrainzSearchArtistUse.WorkAuthor,
                            Identity: new(
                                new("musicbrainz.org", "artist", Guid.NewGuid().ToString("D")),
                                null!
                            ),
                            Role: new("composer", request.Seed.Text!.Evidence)
                        ),
                    ],
                },
            },
        };
        await File.WriteAllBytesAsync(input, JsonSerializer.SerializeToUtf8Bytes(request, Json));
        var handler = new Stub((_, _) => throw new AssertionException("Null input reached HTTP."));
        var run = () => Batch(handler).RunAsync(input, Output("null-member"), Guid.NewGuid());

        await run.Should().ThrowAsync<InvalidDataException>();
        handler.Calls.Should().Be(0);
        Directory.Exists(Output("null-member")).Should().BeFalse();
    }

    [TestCase("missing")]
    [TestCase("extra")]
    [TestCase("wrong-resource")]
    public async Task SeedRequiresExactCompleteImmutableInputReferences(string kind)
    {
        var input = await WriteRequest();
        var original = Read<MusicBrainzSearchBatchRequest>(directory, "request.json");
        var changed = original with
        {
            Inputs = kind switch
            {
                "missing" => [],
                "extra" => original
                    .Inputs.Concat([
                        new NormalizationBindingInputReference(
                            Guid.NewGuid(),
                            new("test", "seed", "extra")
                        ),
                    ])
                    .ToArray(),
                _ => [original.Inputs[0] with { Resource = new("wrong", "resource", "key") }],
            },
        };
        await File.WriteAllBytesAsync(input, JsonSerializer.SerializeToUtf8Bytes(changed, Json));
        var handler = new Stub(
            (_, _) => throw new AssertionException("Invalid source references reached HTTP.")
        );
        var run = () => Batch(handler).RunAsync(input, Output("bad-refs"), Guid.NewGuid());
        if (kind == "wrong-resource")
        {
            await run.Should().ThrowAsync<ArgumentException>();
        }
        else
        {
            await run.Should().ThrowAsync<InvalidDataException>();
        }
        handler.Calls.Should().Be(0);
        Directory.Exists(Output("bad-refs")).Should().BeFalse();
    }

    [Test]
    public async Task ChangedRequestCannotReuseFrozenPlanEvenWhenTheBroadQueryWouldBeIdentical()
    {
        var input = await WriteRequest();
        var handler = new Stub((_, _) => Task.FromResult(Response(Body(0, 0, []))));
        var batch = Batch(handler);
        var uuid = Guid.NewGuid();
        await batch.RunAsync(input, Output("original"), uuid);
        var raw = await File.ReadAllTextAsync(input);
        await File.WriteAllTextAsync(input, raw + "\n");
        var resumed = () =>
            batch.RunAsync(input, Output("changed-prior"), uuid, Receipt("original"));
        await resumed.Should().ThrowAsync<InvalidDataException>();
        Directory.Exists(Output("changed-prior")).Should().BeFalse();
        var restarted = () => batch.RunAsync(input, Output("changed-restart"), uuid);
        await restarted.Should().ThrowAsync<NormalizationConflictException>();
        handler.Calls.Should().Be(1);
        File.Exists(Path.Combine(Output("changed-restart"), "manifest.json")).Should().BeFalse();
        File.Exists(Path.Combine(Output("changed-restart"), "interruption.json")).Should().BeTrue();
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task CancellationOrUnexpectedFailureRetainsFrozenPlanAndAuditWithoutManifest(
        bool cancel
    )
    {
        var input = await WriteRequest();
        using var cancellation = new CancellationTokenSource();
        var handler = new Stub(
            (_, _) =>
            {
                if (cancel)
                {
                    cancellation.Cancel();
                    throw new OperationCanceledException(cancellation.Token);
                }
                throw new InvalidOperationException("controlled failure");
            }
        );
        var uuid = Guid.NewGuid();
        var batch = Batch(handler);
        var run = () => batch.RunAsync(input, Output("interrupted"), uuid, ct: cancellation.Token);
        if (cancel)
        {
            await run.Should().ThrowAsync<OperationCanceledException>();
        }
        else
        {
            await run.Should().ThrowAsync<InvalidOperationException>();
        }
        handler.Calls.Should().Be(1);
        File.Exists(Path.Combine(Output("interrupted"), "plan.json")).Should().BeTrue();
        File.Exists(Path.Combine(Output("interrupted"), "manifest.json")).Should().BeFalse();
        File.Exists(Path.Combine(Output("interrupted"), "interruption.json")).Should().BeTrue();
        var plan = Read<MusicBrainzSearchFrozenPlan>(Output("interrupted"), "plan.json");
        (await ServiceFor(handler).GetSearchAsync(plan.Variants[0].Pages[0].CommandUuid))
            .Should()
            .NotBeNull();
        var resumed = await batch.RunAsync(
            input,
            Output("interrupted-resume"),
            uuid,
            Path.Combine(Output("interrupted"), "plan.json")
        );
        resumed.State.Should().Be("pending");
        handler.Calls.Should().Be(1);
        Read<MusicBrainzSearchFrozenPlan>(resumed.OutputDirectory, "plan.json")
            .Should()
            .BeEquivalentTo(plan);
    }

    [Test]
    public async Task ExistingOutputIsPreservedBeforeAnyPageCommandIsAdmitted()
    {
        var input = await WriteRequest();
        Directory.CreateDirectory(Output("occupied"));
        await File.WriteAllTextAsync(Path.Combine(Output("occupied"), "keep"), "original");
        var handler = new Stub((_, _) => throw new AssertionException("Collision reached HTTP."));
        var run = () => Batch(handler).RunAsync(input, Output("occupied"), Guid.NewGuid());
        await run.Should().ThrowAsync<IOException>();
        handler.Calls.Should().Be(0);
        (await File.ReadAllTextAsync(Path.Combine(Output("occupied"), "keep")))
            .Should()
            .Be("original");
        Directory.GetFiles(Output("occupied")).Should().ContainSingle();
    }

    private MusicBrainzSearchBatch Batch(Stub handler) => new(ServiceFor(handler), store, evidence);

    private NormalizationMusicBrainzInputService ServiceFor(Stub handler)
    {
        var client = new HttpClient(handler);
        clients.Add(client);
        return new(client, store, evidence, new());
    }

    private async Task<string> WriteRequest(bool missingText = false)
    {
        var capture = await evidence.CaptureInputAsync(
            new(
                Guid.NewGuid(),
                new("search-batch-test", "seed", Guid.NewGuid().ToString("D")),
                Encoding.UTF8.GetBytes("{\"title\":\"retained literal café\"}"),
                "application/json",
                "search-seed-v1",
                0,
                DateTimeOffset.UtcNow
            )
        );
        var request = new MusicBrainzSearchBatchRequest(
            new(
                MusicBrainzLookupKind.Work,
                missingText
                    ? null
                    : new("retained literal café", new(capture.Revision.Uuid, "/title")),
                []
            ),
            missingText ? [] : [new(capture.Revision.Uuid, capture.Revision.Resource)],
            MusicBrainzCacheMode.Refresh
        );
        var path = Path.Combine(directory, "request.json");
        await File.WriteAllBytesAsync(path, JsonSerializer.SerializeToUtf8Bytes(request, Json));
        return path;
    }

    private Task ClearCooldown() =>
        fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "UPDATE normalization.musicbrainz_gate SET attempt_uuid=NULL,token=NULL,lease_until=NULL,not_before=clock_timestamp()-interval '10 seconds' WHERE singleton"
            )
        );

    private string Output(string name) => Path.Combine(directory, name);

    private string Receipt(string name) => Path.Combine(Output(name), "plan-receipt.json");

    private static T Read<T>(string output, string name) =>
        JsonSerializer.Deserialize<T>(File.ReadAllBytes(Path.Combine(output, name)), Json)!;

    private static byte[] Body(int count, int offset, Guid[] ids) =>
        JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                count,
                offset,
                works = ids.Select(id => new
                    {
                        id,
                        title = "observed candidate",
                        score = 100,
                    })
                    .ToArray(),
            }
        );

    private static HttpResponseMessage Response(
        byte[] body,
        HttpStatusCode status = HttpStatusCode.OK
    )
    {
        var response = new HttpResponseMessage(status) { Content = new ByteArrayContent(body) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return response;
    }

    private static async Task CheckArtifactHashes(string output)
    {
        using var manifest = JsonDocument.Parse(
            await File.ReadAllBytesAsync(Path.Combine(output, "manifest.json"))
        );
        foreach (var artifact in manifest.RootElement.GetProperty("artifacts").EnumerateObject())
        {
            var bytes = await File.ReadAllBytesAsync(Path.Combine(output, artifact.Name));
            bytes.LongLength.Should().Be(artifact.Value.GetProperty("bytes").GetInt64());
            Convert
                .ToHexStringLower(SHA256.HashData(bytes))
                .Should()
                .Be(artifact.Value.GetProperty("sha256").GetString());
        }
    }

    private sealed class Stub(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send
    ) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken ct
        )
        {
            Calls++;
            var response = await send(request, ct);
            response.RequestMessage = request;
            return response;
        }
    }
}
