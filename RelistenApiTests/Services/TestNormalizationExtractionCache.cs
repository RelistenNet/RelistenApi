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

namespace RelistenApiTests.Services;

[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestNormalizationExtractionCache
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
            Assert.Ignore("Run ./test-local.sh to include local integration tests.");
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
    public async Task ConcurrentReservationsCannotExceedOneJobCap()
    {
        var job = Guid.NewGuid();
        await cache.CreateJobAsync(job, 1m);
        var claims = await Task.WhenAll(
            Claim(job, "one-" + job, .6m),
            Claim(job, "two-" + job, .6m)
        );
        claims.Select(claim => claim.State).Should().BeEquivalentTo("claimed", "budget_exhausted");
        (await cache.GetJobAsync(job)).AccountedUsd.Should().Be(.6m);
    }

    [Test]
    public async Task ConcurrentIdenticalRequestsHaveOnePaidAdmissionAndSuccessIsReused()
    {
        var job = Guid.NewGuid();
        await cache.CreateJobAsync(job, 1m);
        var request = "exact-full-request-" + job;
        var claims = await Task.WhenAll(Claim(job, request), Claim(job, request));
        claims.Select(claim => claim.State).Should().BeEquivalentTo("claimed", "in_progress");
        var admitted = claims.Single(claim => claim.State == "claimed");
        var response = new LunaCreditResponse(
            "{\"credits\":[]}",
            "fixture-provider-request",
            42,
            7
        );
        await cache.CompleteAsync(admitted.AttemptUuid!.Value, response, .1m);
        // A downstream failure requires no compensating cache mutation. Another job reuses the receipt.
        var anotherJob = Guid.NewGuid();
        await cache.CreateJobAsync(anotherJob, .01m);
        var replay = await Claim(anotherJob, request);
        replay.State.Should().Be("cached");
        replay.ResponseJson.Should().Be(response.Json);
        (await cache.GetJobAsync(job)).AccountedUsd.Should().Be(.1m);
        (await cache.GetJobAsync(anotherJob)).AccountedUsd.Should().Be(0);
        await cache.CompleteAsync(admitted.AttemptUuid.Value, response, .1m);
        (await cache.GetJobAsync(job)).AccountedUsd.Should().Be(.1m);
    }

    [Test]
    public async Task ExpiredAndFailedAttemptsKeepPossibleCostAndBoundRetriesAcrossJobs()
    {
        var job = Guid.NewGuid();
        await cache.CreateJobAsync(job, 1m);
        var request = "uncertain-request-" + job;
        var first = await Claim(job, request);
        await db.WithWriteConnection(connection =>
            connection.ExecuteAsync(
                "UPDATE normalization.extraction_attempts SET lease_until=clock_timestamp()-interval '1 second' WHERE uuid=@Uuid",
                new { Uuid = first.AttemptUuid }
            )
        );
        var second = await Claim(job, request);
        second.State.Should().Be("claimed");
        await cache.FailAsync(second.AttemptUuid!.Value, "timeout");
        (await cache.GetJobAsync(job)).AccountedUsd.Should().Be(1m);
        var anotherJob = Guid.NewGuid();
        await cache.CreateJobAsync(anotherJob);
        (await Claim(anotherJob, request)).State.Should().Be("attempts_exhausted");
    }

    [Test]
    public async Task JobReplayCannotRaiseBudgetAndOverReservationUsageCannotCommit()
    {
        var job = Guid.NewGuid();
        await cache.CreateJobAsync(job, 1m);
        await cache.CreateJobAsync(job, 1m);
        var increase = () => cache.CreateJobAsync(job, 2m);
        await increase.Should().ThrowAsync<InvalidOperationException>();
        var claim = await Claim(job, "over-reservation-" + job);
        var complete = () =>
            cache.CompleteAsync(
                claim.AttemptUuid!.Value,
                new("{\"credits\":[]}", "request", 100, 100),
                .6m
            );
        await complete.Should().ThrowAsync<InvalidOperationException>();
        (await cache.GetJobAsync(job)).AccountedUsd.Should().Be(.5m);
    }

    [Test]
    public async Task LocalSuccessDoesNotReadApiKeyOrSpend()
    {
        var job = Guid.NewGuid();
        await cache.CreateJobAsync(job);
        using var http = new HttpClient();
        var extractor = new LunaRecordingCreditExtractor(
            http,
            cache,
            new(),
            () => throw new AssertionException("Local result must not read key")
        );
        var result = await extractor.ExtractAsync(
            job,
            new Dictionary<string, string?> { ["taper"] = "Pat Smith" }
        );
        result.State.Should().Be("local");
        result.Extraction.Credits.Single().Name.Should().Be("Pat Smith");
        (await cache.GetJobAsync(job)).AccountedUsd.Should().Be(0);
    }

    [Test]
    public async Task OversizedInputRemainsLocalAndDoesNotReadKeyOrReserveBudget()
    {
        var job = Guid.NewGuid();
        await cache.CreateJobAsync(job);
        using var http = new HttpClient();
        var extractor = new LunaRecordingCreditExtractor(
            http,
            cache,
            new(),
            () => throw new AssertionException("Oversized input must not read key")
        );
        var result = await extractor.ExtractAsync(
            job,
            new Dictionary<string, string?>
            {
                ["description"] =
                    new string('x', LunaRecordingCreditExtractor.MaxRenderedRequestBytes)
                    + " transfer Alice",
            }
        );
        result.Reason.Should().Be("input_too_large");
        (await cache.GetJobAsync(job)).AccountedUsd.Should().Be(0);
    }

    [Test]
    public async Task MissingConfigurationDoesNotReserveBudget()
    {
        var job = Guid.NewGuid();
        await cache.CreateJobAsync(job);
        using var http = new HttpClient();
        var extractor = new LunaRecordingCreditExtractor(http, cache, new(), () => "");
        var result = await extractor.ExtractAsync(
            job,
            new Dictionary<string, string?> { ["description"] = "Alice handled transfer." }
        );
        result.Reason.Should().Be("configuration_unavailable");
        (await cache.GetJobAsync(job)).AccountedUsd.Should().Be(0);
    }

    [TestCase("taper", "Pat Smith")]
    [TestCase("uploader", "Bob")]
    public async Task UncoveredTransferUsesFallbackAndDurableCacheWithoutReReadingKey(
        string otherField,
        string otherName
    )
    {
        var job = Guid.NewGuid();
        await cache.CreateJobAsync(job);
        var text = "Alice handled transfer.";
        var handler = new CreditHandler(
            Normalization.TestLunaRecordingCredits.Credit(
                "Alice",
                "transferrer",
                "description",
                text
            )
        );
        using var http = new HttpClient(handler);
        var keyReads = 0;
        var extractor = new LunaRecordingCreditExtractor(
            http,
            cache,
            new(),
            () =>
            {
                keyReads++;
                return "synthetic-test-key";
            }
        );
        var fields = new Dictionary<string, string?>
        {
            [otherField] = otherName,
            ["description"] = text,
        };
        var result = await extractor.ExtractAsync(job, fields);
        result.State.Should().Be("extracted");
        result.Extraction.Credits.Single().Name.Should().Be("Alice");
        var replay = await extractor.ExtractAsync(job, fields);
        replay.FromCache.Should().BeTrue();
        keyReads.Should().Be(1);
        handler.Calls.Should().Be(1);
        (await cache.GetJobAsync(job)).AccountedUsd.Should().Be(.000015m);
    }

    [Test]
    public async Task InvalidPaidEvidenceRetainsUsageReceiptWithoutCachingProposals()
    {
        var job = Guid.NewGuid();
        await cache.CreateJobAsync(job);
        var handler = new CreditHandler(
            Normalization.TestLunaRecordingCredits.Credit(
                "Invented",
                "transferrer",
                "description",
                "Alice handled transfer."
            )
        );
        using var http = new HttpClient(handler);
        var extractor = new LunaRecordingCreditExtractor(
            http,
            cache,
            new(),
            () => "synthetic-test-key"
        );
        var fields = new Dictionary<string, string?>
        {
            ["description"] = "Alice handled transfer.",
        };
        var result = await extractor.ExtractAsync(job, fields);
        result.Reason.Should().Be("invalid_response");
        var receipt = await db.WithWriteConnection(connection =>
            connection.QuerySingleAsync<(string State, long Input, long Output, string RequestId)>(
                "SELECT state,input_tokens,output_tokens,provider_request_id FROM normalization.extraction_attempts WHERE job_uuid=@job",
                new { job }
            )
        );
        receipt.Should().Be(("failed", 100L, 10L, "fixture-request"));
        (await cache.GetJobAsync(job)).AccountedUsd.Should().Be(.000015m);
        (
            await cache.GetCachedResponseAsync(
                LunaRecordingCreditExtractor.RenderRequest(fields, null, new())
            )
        )
            .Should()
            .BeNull();
    }

    private sealed class CreditHandler(string creditJson) : HttpMessageHandler
    {
        internal int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Calls++;
            var envelope = JsonSerializer.Serialize(
                new
                {
                    status = "completed",
                    output = new[]
                    {
                        new
                        {
                            type = "message",
                            content = new[] { new { type = "output_text", text = creditJson } },
                        },
                    },
                    usage = new { input_tokens = 100, output_tokens = 10 },
                }
            );
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(envelope, Encoding.UTF8, "application/json"),
            };
            response.Headers.Add("x-request-id", "fixture-request");
            return Task.FromResult(response);
        }
    }

    private Task<NormalizationExtractionClaim> Claim(
        Guid job,
        string request,
        decimal reservation = .5m
    ) => cache.TryClaimAsync(job, request, reservation, 2, TimeSpan.FromMinutes(3));
}
