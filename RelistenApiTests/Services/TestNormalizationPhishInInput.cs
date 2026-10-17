using System.Net;
using System.Text;
using System.Transactions;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Relisten.Api.Models;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
using Relisten.Import;
using Relisten.Ingestion;

namespace RelistenApiTests.Services;

/// <summary>Verifies provider item identity and pre-request freshness across changing Phish.in pages.</summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestNormalizationPhishInInput
{
    private NormalizationDomainFixture fixture = null!;
    private NormalizationEvidenceService evidence = null!;
    private NormalizationPhishInInputService inputs = null!;
    private static int nextId = 1000000;

    [OneTimeSetUp]
    public void Start()
    {
        fixture = new NormalizationDomainFixture();
        evidence = new(fixture.Db);
        inputs = Service(true);
    }

    [OneTimeTearDown]
    public void Stop() => fixture?.Dispose();

    [TestCase("utf-8")]
    [TestCase("iso-8859-1")]
    [TestCase("utf-16")]
    public async Task ImporterRetainsCompleteShowObjectsBeforeSelectingWorkflowFields(
        string charset
    )
    {
        var id = Interlocked.Increment(ref nextId);
        var show = $$$"""
            { "id": {{{id}}}, "date": "1998-04-05", "venue": {"id":7,"name":"A room"},
              "tags":[{"name":"tag","notes":{"unknown":"retained"}}],
              "tracks":[{"id":11,"song_ids":[4,4],"position":2},{"id":12,"song_ids":[4],"position":3}],
              "unmodeled": "café", "escaped": "caf\u00e9" }
            """;
        using var upstream = new ResponseHandler(Page(show, 2), Encoding.GetEncoding(charset));
        using var importer = new PhishinImporter(
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            NullLogger<PhishinImporter>.Instance,
            new ConfigurationBuilder().Build(),
            null!,
            inputs
        );
        importer.UseIngestionHttpClient(new HttpClient(upstream));
        var context = new ProviderContext(
            new Artist(),
            new ArtistUpstreamSource(),
            new IngestionExecution("evidence-test", DateTimeOffset.UtcNow.AddDays(1)),
            null!,
            CancellationToken.None
        );

        var selected = await importer.ReadShowPageAsync(context, null, null);

        selected.Items.Single().Id.Value.Should().Be(id);
        selected.Items.Single().Input.Tracks.Select(track => track.position).Should().Equal(2, 3);
        selected.Next!.Number.Should().Be(2);
        upstream.Calls.Should().Be(1);
        var current = await evidence.GetCurrentInputAsync(Resource(id));
        Encoding.UTF8.GetString(current!.Revision.Payload).Should().Be(show);
        current.Revision.CaptureVersion.Should().Be("phishin-show-object-v1");
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task OverlappingPagesCannotReplaceNewerShowEvidence(bool alreadyKnown)
    {
        var id = Interlocked.Increment(ref nextId);
        if (alreadyKnown)
        {
            await inputs.FetchShowsAsync(_ =>
                Task.FromResult(Response(Page(Show(id, "initial"), 1)))
            );
        }

        var older = () =>
            inputs.FetchShowsAsync(async ct =>
            {
                // A shifted page commits the same show while the older request is in flight.
                await inputs.FetchShowsAsync(
                    _ => Task.FromResult(Response(Page(Show(id, "newer"), 3))),
                    ct
                );
                return Response(Page(Show(id, "older"), 1));
            });

        var rejected = (await older.Should().ThrowAsync<NormalizationStaleInputException>()).Which;
        rejected.Resource.Should().Be(Resource(id));
        rejected.Capture.Fetch.ExpectedGeneration.Should().Be(alreadyKnown ? 1 : 0);
        rejected.Capture.Fetch.AcceptedAsCurrent.Should().BeFalse();
        Encoding
            .UTF8.GetString((await evidence.GetCurrentInputAsync(Resource(id)))!.Revision.Payload)
            .Should()
            .Be(Show(id, "newer"));
        Encoding
            .UTF8.GetString((await evidence.GetInputAsync(rejected.Capture.Revision.Uuid))!.Payload)
            .Should()
            .Be(Show(id, "older"));

        await inputs.FetchShowsAsync(_ =>
            Task.FromResult(Response(Page(Show(id, "recovered"), 2)))
        );
        Encoding
            .UTF8.GetString((await evidence.GetCurrentInputAsync(Resource(id)))!.Revision.Payload)
            .Should()
            .Be(Show(id, "recovered"));
    }

    [Test]
    public async Task EqualShowBytesAdvanceGenerationAndReuseRevision()
    {
        var id = Interlocked.Increment(ref nextId);
        await inputs.FetchShowsAsync(_ => Task.FromResult(Response(Page(Show(id, "same"), 1))));
        var first = await evidence.GetCurrentInputAsync(Resource(id));
        await inputs.FetchShowsAsync(_ => Task.FromResult(Response(Page(Show(id, "same"), 3))));

        var current = await evidence.GetCurrentInputAsync(Resource(id));
        current!.Generation.Should().Be(2);
        current.Revision.Uuid.Should().Be(first!.Revision.Uuid);
    }

    [TestCase("duplicate")]
    [TestCase("invalid")]
    public async Task InvalidLaterIdentityPreventsAllPageCapture(string invalid)
    {
        var id = Interlocked.Increment(ref nextId);
        var second = invalid == "duplicate" ? Show(id, "again") : "{\"id\":\"invalid\"}";
        var fetch = () =>
            inputs.FetchShowsAsync(_ =>
                Task.FromResult(Response(Page(Show(id, "first") + "," + second, 1)))
            );

        await fetch.Should().ThrowAsync<InvalidDataException>();
        (await evidence.GetCurrentInputAsync(Resource(id))).Should().BeNull();
        (await evidence.GetFetchHistoryAsync(Resource(id))).Should().BeEmpty();
    }

    [Test]
    public async Task SnapshotLimitFailsBeforeHttpAndDoesNotTruncateKnownIdentities()
    {
        var first = Interlocked.Increment(ref nextId);
        var second = Interlocked.Increment(ref nextId);
        await inputs.FetchShowsAsync(_ =>
            Task.FromResult(Response(Page(Show(first, "first") + "," + Show(second, "second"), 1)))
        );
        var calls = 0;
        var fetch = () =>
            inputs.FetchShowsAsync(
                _ =>
                {
                    calls++;
                    return Task.FromResult(Response(Page(Show(first, "not fetched"), 1)));
                },
                maxResources: 1
            );

        await fetch
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*snapshot exceeds*");
        calls.Should().Be(0);
        var generations = await evidence.GetResourceGenerationsAsync("phish.in", "show");
        generations[first.ToString()].Should().Be(1);
        generations[second.ToString()].Should().Be(1);
        (await evidence.GetResourceGenerationsAsync("unrelated", "show")).Should().BeEmpty();
    }

    [Test]
    public async Task PartialRetentionSurvivesStaleLaterItemAndWholeFetchRecovers()
    {
        var first = Interlocked.Increment(ref nextId);
        var second = Interlocked.Increment(ref nextId);
        var fetch = () =>
            inputs.FetchShowsAsync(async ct =>
            {
                await inputs.FetchShowsAsync(
                    _ => Task.FromResult(Response(Page(Show(second, "newer"), 2))),
                    ct
                );
                return Response(Page(Show(first, "first") + "," + Show(second, "older"), 1));
            });

        await fetch.Should().ThrowAsync<NormalizationStaleInputException>();
        (await evidence.GetCurrentInputAsync(Resource(first)))!.Generation.Should().Be(1);
        var revision = (await evidence.GetCurrentInputAsync(Resource(first)))!.Revision.Uuid;
        await inputs.FetchShowsAsync(_ =>
            Task.FromResult(Response(Page(Show(first, "first") + "," + Show(second, "fresh"), 1)))
        );
        var recovered = await evidence.GetCurrentInputAsync(Resource(first));
        recovered!.Generation.Should().Be(2);
        recovered.Revision.Uuid.Should().Be(revision);
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task CancellationReachesFetchAndPreventsReturn(bool enabled)
    {
        var id = Interlocked.Increment(ref nextId);
        using var cancellation = new CancellationTokenSource();
        var fetch = () =>
            Service(enabled)
                .FetchShowsAsync(
                    ct =>
                    {
                        ct.Should().Be(cancellation.Token);
                        cancellation.Cancel();
                        ct.ThrowIfCancellationRequested();
                        return Task.FromResult(Response(Page(Show(id, "cancelled"), 1)));
                    },
                    cancellation.Token
                );

        await fetch.Should().ThrowAsync<OperationCanceledException>();
        (await evidence.GetFetchHistoryAsync(Resource(id))).Should().BeEmpty();
    }

    [Test]
    public async Task CancellationAfterCommittedPrefixPreventsProgressAndRetryRetainsTheWholePage()
    {
        var first = Interlocked.Increment(ref nextId);
        var second = Interlocked.Increment(ref nextId);
        await inputs.FetchShowsAsync(_ =>
            Task.FromResult(Response(Page(Show(second, "initial"), 1)))
        );
        await using var blocker = new NpgsqlConnection(LocalTestDatabase.ConnectionString);
        await blocker.OpenAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await blocker.ExecuteAsync(
            "SELECT uuid FROM normalization.input_resources WHERE provider='phish.in' AND resource_type='show' AND provider_key=@key FOR UPDATE",
            new { key = second.ToString() },
            transaction
        );
        await using var observer = new NpgsqlConnection(LocalTestDatabase.ConnectionString);
        await observer.OpenAsync();
        using var cancellation = new CancellationTokenSource();
        var page = Page(Show(first, "first") + "," + Show(second, "updated"), 1);
        var pending = inputs.FetchShowsAsync(
            _ => Task.FromResult(Response(page)),
            cancellation.Token
        );

        // The second item waits behind our row lock, after the first item has committed.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        var waiting = false;
        while (!waiting && DateTime.UtcNow < deadline)
        {
            waiting = await observer.QuerySingleAsync<bool>(
                """
                SELECT EXISTS(SELECT 1 FROM pg_stat_activity
                    WHERE datname=current_database() AND wait_event_type='Lock'
                    AND @blockerPid=ANY(pg_blocking_pids(pid))
                    AND query LIKE '%normalization.input_resources%')
                """,
                new { blockerPid = blocker.ProcessID }
            );
            if (!waiting)
            {
                await Task.Delay(10);
            }
        }

        waiting.Should().BeTrue("the second capture must reach its resource lock");
        (await evidence.GetCurrentInputAsync(Resource(first)))!.Generation.Should().Be(1);
        cancellation.Cancel();
        var cancelled = () => pending;
        await cancelled.Should().ThrowAsync<OperationCanceledException>();
        await transaction.RollbackAsync();

        await inputs.FetchShowsAsync(_ => Task.FromResult(Response(page)));
        (await evidence.GetCurrentInputAsync(Resource(first)))!.Generation.Should().Be(2);
        Encoding
            .UTF8.GetString(
                (await evidence.GetCurrentInputAsync(Resource(second)))!.Revision.Payload
            )
            .Should()
            .Be(Show(second, "updated"));
    }

    [Test]
    public async Task EnabledCaptureRejectsCatalogTransactionBeforeHttp()
    {
        using var transaction = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled);
        var calls = 0;
        var fetch = () =>
            inputs.FetchShowsAsync(_ =>
            {
                calls++;
                return Task.FromResult(Response(Page("", 1)));
            });

        await fetch
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*outside the catalog transaction*");
        calls.Should().Be(0);
    }

    [Test]
    public async Task DisabledCaptureReturnsValueWithoutParsingOrDatabaseAccess()
    {
        var calls = 0;
        var disabled = new NormalizationPhishInInputService(
            null!,
            new(null!, Options.Create(new NormalizationProviderCaptureOptions()))
        );
        var value = await disabled.FetchShowsAsync(_ =>
        {
            calls++;
            return Task.FromResult(Response("unused payload"));
        });

        value.Should().Be("value");
        calls.Should().Be(1);
    }

    [Test]
    public async Task CatalogKeepsExactResponseAndUsesItsOwnResource()
    {
        const string body =
            "{\"success\":true,\"data\":[{\"id\":4,\"title\":\"Alias\",\"alias_for\":3,\"unknown\":true}]}";
        await inputs.FetchCatalogAsync("songs", _ => Task.FromResult(Response(body)));
        var current = await evidence.GetCurrentInputAsync(new("phish.in", "catalog", "songs"));

        Encoding.UTF8.GetString(current!.Revision.Payload).Should().Be(body);
        current.Revision.CaptureVersion.Should().Be("raw-http:1");
    }

    private NormalizationPhishInInputService Service(bool enabled) =>
        new(
            evidence,
            new(
                evidence,
                Options.Create(new NormalizationProviderCaptureOptions { Enabled = enabled })
            )
        );

    private static NormalizationResourceKey Resource(int id) =>
        new("phish.in", "show", id.ToString());

    private static string Show(int id, string note) => $$"""{"id":{{id}},"note":"{{note}}"}""";

    private static string Page(string shows, int page) =>
        $$"""{"success":true,"page":{{page}},"total_pages":3,"data":[{{shows}}]}""";

    private static NormalizationProviderResponse<string> Response(string payload) =>
        new("value", Encoding.UTF8.GetBytes(payload), "application/json");

    private sealed class ResponseHandler(string body, Encoding? encoding = null)
        : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken ct
        )
        {
            Calls++;
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        body,
                        encoding ?? Encoding.UTF8,
                        "application/json"
                    ),
                }
            );
        }
    }
}
