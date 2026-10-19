using System.Net;
using System.Text;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Relisten;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
using Relisten.Normalization;
using Relisten.Normalization.Batch;

namespace RelistenApiTests.Normalization;

[TestFixture]
public sealed class TestLunaCompositionHypothesisSelector
{
    internal static readonly Guid FirstWork = Guid.Parse("00000000-0000-0000-0000-000000000011");
    internal static readonly Guid SecondWork = Guid.Parse("00000000-0000-0000-0000-000000000012");

    [Test]
    public void SelectionAndAlternativesRemainSuppliedProposalsWithSeparateRationale()
    {
        var result = LunaCompositionHypothesisSelector.ValidateResponse(Input(), Response());
        result.SelectedWorkMbid.Should().Be(FirstWork);
        result.AlternativeWorkMbids.Should().Equal(SecondWork);
        result.Outcome.Should().Be("selected");
        result.PublicationPermitted.Should().BeFalse();
        result.ExternalUniverseComplete.Should().BeFalse();
    }

    [Test]
    public void OneCandidateMayStillHaveInsufficientEvidence()
    {
        var input = Input();
        input = input with { SuppliedCandidates = [input.SuppliedCandidates[0]] };
        var response = JsonSerializer.Serialize(
            new
            {
                selectedWorkMbid = (Guid?)null,
                alternativeWorkMbids = new[] { FirstWork },
                outcome = "insufficient_evidence",
                rationale = "The retained facts cannot distinguish this interpretation.",
            }
        );
        var result = LunaCompositionHypothesisSelector.ValidateResponse(input, response);
        result.SelectedWorkMbid.Should().BeNull();
        result.Outcome.Should().Be("insufficient_evidence");
    }

    [TestCase("invented_selected")]
    [TestCase("invented_alternative")]
    [TestCase("duplicate_alternative")]
    [TestCase("selected_alternative")]
    [TestCase("selected_without_id")]
    [TestCase("id_without_selection")]
    [TestCase("invented_authors")]
    public void InvalidOrInventedChoicesCannotBecomeReusableProposals(string mutation)
    {
        var row = System.Text.Json.Nodes.JsonNode.Parse(Response())!;
        switch (mutation)
        {
            case "invented_selected":
                row["selectedWorkMbid"] = Guid.NewGuid().ToString();
                break;
            case "invented_alternative":
                row["alternativeWorkMbids"]![0] = Guid.NewGuid().ToString();
                break;
            case "duplicate_alternative":
                row["alternativeWorkMbids"]!.AsArray().Add(SecondWork.ToString());
                break;
            case "selected_alternative":
                row["alternativeWorkMbids"]![0] = FirstWork.ToString();
                break;
            case "selected_without_id":
                row["selectedWorkMbid"] = null;
                break;
            case "id_without_selection":
                row["outcome"] = "insufficient_evidence";
                break;
            case "invented_authors":
                row["authors"] = "Invented canonical person";
                break;
        }
        var read = () =>
            LunaCompositionHypothesisSelector.ValidateResponse(Input(), row.ToJsonString());
        read.Should().Throw<LunaCreditResponseException>();
    }

    [Test]
    public async Task OversizedCandidateUniverseIsNotClippedOrSent()
    {
        var input = Input();
        var work = input.SuppliedCandidates[0];
        input = input with
        {
            SuppliedCandidates =
            [
                work with
                {
                    Titles =
                    [
                        new(
                            new string(
                                'x',
                                LunaCompositionHypothesisSelector.MaxRenderedRequestBytes
                            ),
                            Evidence()
                        ),
                    ],
                },
                input.SuppliedCandidates[1],
            ],
        };
        var handler = new Handler(Response());
        using var http = new HttpClient(handler);
        var result = await new LunaCompositionHypothesisSelector(
            http,
            null!,
            () => throw new AssertionException("No key should be requested")
        ).SelectAsync(Guid.NewGuid(), input);
        result.Reason.Should().Be("input_too_large");
        var description = LunaCompositionHypothesisSelector.DescribeRequest(input);
        description.WithinRequestLimit.Should().BeFalse();
        description.SuppliedCandidates.Should().Be(2);
        result.SelectedWorkMbid.Should().BeNull();
        handler.Calls.Should().Be(0);
    }

    internal static CompositionHypothesisSelectionInput Input() =>
        new(
            new(
                "catalogue",
                Guid.NewGuid(),
                "Performing band",
                "repertoire:song",
                "Song",
                "catalog_dictionary",
                null,
                [],
                ["IdentityAlias"],
                new(
                    "/retained",
                    "repertoire/chunk.gz",
                    1,
                    1,
                    "/proposal/name",
                    "source-sha",
                    "retained_result"
                ),
                230
            ),
            new[] { FirstWork, SecondWork }
                .Select(id => new CompositionSelectionWork(
                    id,
                    "composition:musicbrainz.org:work:" + id,
                    new(new("musicbrainz.org", "work", id.ToString()), Evidence()),
                    [new("Song", Evidence())],
                    [],
                    [],
                    [],
                    [],
                    [],
                    [],
                    []
                ))
                .ToArray()
        );

    private static NormalizedEvidenceReference Evidence() =>
        new(Guid.Parse("00000000-0000-0000-0000-000000000099"), "/title", Quote: "Song");

    internal static string Response() =>
        JsonSerializer.Serialize(
            new
            {
                selectedWorkMbid = FirstWork,
                alternativeWorkMbids = new[] { SecondWork },
                outcome = "selected",
                rationale = "A plausible work correspondence; this is model inference.",
            }
        );

    internal sealed class Handler(string json, CancellationTokenSource? cancellation = null)
        : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken ct
        )
        {
            Calls++;
            RequestBody = await request.Content!.ReadAsStringAsync(ct);
            if (cancellation is not null)
            {
                cancellation.Cancel();
                throw new OperationCanceledException(ct);
            }
            var body = JsonSerializer.Serialize(
                new
                {
                    status = "completed",
                    usage = new { input_tokens = 100, output_tokens = 200 },
                    output = new[]
                    {
                        new
                        {
                            type = "message",
                            content = new[] { new { type = "output_text", text = json } },
                        },
                    },
                }
            );
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            response.Headers.Add("x-request-id", "composition-fixture");
            return response;
        }
    }
}

[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestLunaCompositionHypothesisSelectorCache
{
    private DbService db = null!;
    private NormalizationExtractionCacheService cache = null!;
    private string? previousWrite;
    private string? previousRead;

    [OneTimeSetUp]
    public void Start()
    {
        if (Environment.GetEnvironmentVariable("RELISTEN_LOCAL_INGESTION_TESTS") != "1")
        {
            Assert.Ignore("Run the dedicated local database tests.");
        }
        previousWrite = DbService.ConnStr;
        previousRead = DbService.ReadOnlyConnStr;
        db = new(
            LocalTestDatabase.DatabaseUrl,
            null,
            null,
            Host.CreateApplicationBuilder().Environment,
            NullLogger<DbService>.Instance
        );
        cache = new(db);
    }

    [OneTimeTearDown]
    public void Stop()
    {
        if (previousWrite is not null)
        {
            DbService.ConnStr = previousWrite;
            DbService.ReadOnlyConnStr = previousRead!;
        }
    }

    [Test]
    public async Task ToolAdapterPreservesObservedRolesAndExactCoordinatesWithoutInventingArtistContext()
    {
        var coreWork = TestLunaCompositionHypothesisSelector.Input().SuppliedCandidates[0];
        var evidence = coreWork.Identity.Evidence;
        var source = new MusicTitleWitness(
            "/retained",
            "repertoire/chunk.gz",
            4,
            8,
            "/proposal/name",
            "pinned-sha"
        );
        var artist = Guid.NewGuid();
        var song = new CompositionHypothesisSong(
            "catalogue",
            artist,
            new("repertoire:song", artist, null, "Original title", "title_derived"),
            [new("song_sample:1", "/row/name", "Original title") { RetainedWitness = source }],
            [MusicRepertoireRelation.Tease],
            source,
            230
        );
        var retained = new RetainedCompositionWork(
            coreWork.EntityKey,
            coreWork.Identity,
            coreWork.Titles,
            [],
            [],
            [
                new(
                    new("lyricist", evidence),
                    new("backward", evidence),
                    new("artist", evidence),
                    null,
                    evidence,
                    "work_lookup"
                ),
            ],
            [],
            [],
            [],
            []
        );

        var job = Guid.NewGuid();
        await cache.CreateJobAsync(job, maximumTotalAttempts: 2);
        var response = JsonSerializer.Serialize(
            new
            {
                selectedWorkMbid = TestLunaCompositionHypothesisSelector.FirstWork,
                alternativeWorkMbids = Array.Empty<Guid>(),
                outcome = "selected",
                rationale = "A retained candidate is a plausible correspondence.",
            }
        );
        var handler = new TestLunaCompositionHypothesisSelector.Handler(response);
        using var http = new HttpClient(handler);
        var adapter = new CachedCompositionHypothesisSelector(
            new LunaCompositionHypothesisSelector(http, cache, () => "fixture-key"),
            job
        );
        var selection = await adapter.SelectAsync(song, [retained]);
        selection.Inference!.ProviderRequestId.Should().Be("composition-fixture");
        selection.SelectedWorkMbid.Should().Be(TestLunaCompositionHypothesisSelector.FirstWork);
        var input = SentInput();

        input.Song.ArtistName.Should().BeNull();
        input.Song.SourceWitness.ValuePointer.Should().Be("/proposal/name");
        input.Song.DictionaryNames.Single().RetainedWitness!.ChunkSha256.Should().Be("pinned-sha");
        input.Song.OriginalMemberships.Should().Be(230);
        input
            .SuppliedCandidates.Single()
            .WriterRoles.Single()
            .Direction!.Value.Should()
            .Be("backward");
        input.SuppliedCandidates.Single().CompleteAuthorship.Should().BeNull();

        var contextual = song with
        {
            ArtistName = "Actual catalogue artist",
            ArtistContextManifestSha256 = "context-manifest-sha",
            ArtistContext = new(
                "catalogue",
                artist,
                "Actual catalogue artist",
                null,
                new(
                    "derived_catalogue_cell",
                    "artists",
                    new("catalogue", artist),
                    "name",
                    "catalogue-sha"
                )
            ),
        };
        await adapter.SelectAsync(contextual, [retained]);
        var context = SentInput().Song.ArtistContext!;
        context.ManifestSha256.Should().Be("context-manifest-sha");
        context.CatalogueSha256.Should().Be("catalogue-sha");
        context.ArtistUuid.Should().Be(artist);
        context.MusicBrainzId.Should().BeNull();

        CompositionHypothesisSelectionInput SentInput()
        {
            using var request = JsonDocument.Parse(handler.RequestBody!);
            var supplied = request
                .RootElement.GetProperty("input")[1]
                .GetProperty("content")
                .GetString()!;
            return JsonSerializer.Deserialize<CompositionHypothesisSelectionInput>(
                supplied,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)
            )!;
        }
    }

    [Test]
    public async Task SelectionCachesWithoutAnotherKeyOrAttemptAndChangedFactsUseTheJobCap()
    {
        var job = Guid.NewGuid();
        await cache.CreateJobAsync(job, maximumTotalAttempts: 1);
        var input = TestLunaCompositionHypothesisSelector.Input();
        var handler = new TestLunaCompositionHypothesisSelector.Handler(
            TestLunaCompositionHypothesisSelector.Response()
        );
        using var http = new HttpClient(handler);
        var selector = new LunaCompositionHypothesisSelector(http, cache, () => "fixture-key");
        var first = await selector.SelectAsync(job, input);
        var replay = await new LunaCompositionHypothesisSelector(
            http,
            cache,
            () => throw new AssertionException("Cached selections require no key")
        ).SelectAsync(job, input);
        var changed = input with
        {
            Song = input.Song with { ArtistName = "Different performing artist" },
        };
        var limited = await selector.SelectAsync(job, changed);

        first.SelectedWorkMbid.Should().Be(TestLunaCompositionHypothesisSelector.FirstWork);
        first.ProviderRequestId.Should().Be("composition-fixture");
        first.ActualUsd.Should().Be(.00011m);
        replay.FromCache.Should().BeTrue();
        replay.SelectedWorkMbid.Should().Be(first.SelectedWorkMbid);
        replay.CacheKey.Should().Be(first.CacheKey);
        var description = LunaCompositionHypothesisSelector.DescribeRequest(input);
        first.CacheKey.Should().Be(description.CacheKey);
        description.MaximumReservedUsd.Should().BeGreaterThan(first.ActualUsd!.Value);
        limited.Reason.Should().Be("job_attempts_exhausted");
        limited.SelectedWorkMbid.Should().BeNull();
        handler.Calls.Should().Be(1);
        (await cache.GetJobAsync(job)).AccountedUsd.Should().Be(first.ActualUsd);
    }

    [Test]
    public async Task InventedWorkRetainsKnownUsageWithoutCachingOrRetrying()
    {
        var job = Guid.NewGuid();
        await cache.CreateJobAsync(job);
        var input = TestLunaCompositionHypothesisSelector.Input();
        var json = TestLunaCompositionHypothesisSelector
            .Response()
            .Replace(
                TestLunaCompositionHypothesisSelector.FirstWork.ToString(),
                Guid.NewGuid().ToString()
            );
        var handler = new TestLunaCompositionHypothesisSelector.Handler(json);
        using var http = new HttpClient(handler);
        var result = await new LunaCompositionHypothesisSelector(
            http,
            cache,
            () => "fixture-key"
        ).SelectAsync(job, input);

        result.State.Should().Be("unresolved");
        result.Reason.Should().Be("invalid_response");
        result.SelectedWorkMbid.Should().BeNull();
        result.ActualUsd.Should().Be(.00011m);
        handler.Calls.Should().Be(1);
        (await cache.GetJobAsync(job)).AccountedUsd.Should().Be(.00011m);
        (await cache.GetCachedResponseAsync(LunaCompositionHypothesisSelector.RenderRequest(input)))
            .Should()
            .BeNull();
        var resumed = await new LunaCompositionHypothesisSelector(
            http,
            cache,
            () => "fixture-key"
        ).SelectAsync(job, input);
        resumed.Reason.Should().Be("attempts_exhausted");
        handler.Calls.Should().Be(1);
        (await cache.GetJobAsync(job)).AccountedUsd.Should().Be(.00011m);
    }

    [Test]
    public async Task CancellationSettlesTheAttemptAndRetainsItsUnknownCostReservation()
    {
        var job = Guid.NewGuid();
        await cache.CreateJobAsync(job);
        using var cancellation = new CancellationTokenSource();
        var handler = new TestLunaCompositionHypothesisSelector.Handler("{}", cancellation);
        using var http = new HttpClient(handler);
        var call = () =>
            new LunaCompositionHypothesisSelector(http, cache, () => "fixture-key").SelectAsync(
                job,
                TestLunaCompositionHypothesisSelector.Input(),
                cancellation.Token
            );

        await call.Should().ThrowAsync<OperationCanceledException>();
        handler.Calls.Should().Be(1);
        (await cache.GetJobAsync(job)).AccountedUsd.Should().BeGreaterThan(0m);
        var state = await db.WithWriteConnection(connection =>
            connection.QuerySingleAsync<string>(
                "SELECT state FROM normalization.extraction_attempts WHERE job_uuid=@job",
                new { job }
            )
        );
        state.Should().Be("failed");
    }

    [Test]
    public async Task InsufficientJobBudgetMakesNoHttpAttempt()
    {
        var job = Guid.NewGuid();
        await cache.CreateJobAsync(job, .000001m);
        var handler = new TestLunaCompositionHypothesisSelector.Handler("{}");
        using var http = new HttpClient(handler);
        var result = await new LunaCompositionHypothesisSelector(
            http,
            cache,
            () => "fixture-key"
        ).SelectAsync(job, TestLunaCompositionHypothesisSelector.Input());
        result.Reason.Should().Be("budget_exhausted");
        handler.Calls.Should().Be(0);
        (await cache.GetJobAsync(job)).AccountedUsd.Should().Be(0m);
    }
}
