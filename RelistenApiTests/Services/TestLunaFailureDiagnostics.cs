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
public sealed class TestLunaFailureDiagnostics
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

    [TestCase("bad-json", "credit_schema", true)]
    [TestCase("bad-shape", "credit_schema", true)]
    [TestCase("wrong-name", "credit_support", true)]
    [TestCase("wrong-role", "credit_support", true)]
    [TestCase("wrong-span", "credit_support", true)]
    [TestCase("incomplete", "response_envelope", true)]
    [TestCase("refusal", "response_envelope", true)]
    [TestCase("no-usage", "response_envelope", false)]
    [TestCase("broken-envelope", "response_envelope", false)]
    [TestCase("over-usage", "usage_limit", false)]
    public async Task FailedAttemptsRetainExactPrivateEnvelopeAndClassifiedStage(
        string fault,
        string stage,
        bool measuredCost
    )
    {
        var job = Guid.NewGuid();
        var fields = Fields();
        var name = fields["taper"]!;
        var credit = Credit(name);
        var body = fault switch
        {
            "bad-json" => Envelope("{"),
            "bad-shape" => Envelope("{}"),
            "wrong-name" => Envelope(Credit("An unsupported name", quote: name)),
            "wrong-role" => Envelope(Credit(name, role: "transferrer")),
            "wrong-span" => Envelope(Credit(name, start: 1, end: name.Length)),
            "incomplete" => Envelope(credit, status: "incomplete"),
            "refusal" => Envelope(credit, partType: "refusal"),
            "no-usage" => Encoding.UTF8.GetBytes("{\"status\":\"completed\"}\n"),
            "broken-envelope" => Encoding.UTF8.GetBytes("{broken envelope 🌿\n"),
            "over-usage" => Envelope(credit, outputTokens: 100_000),
            _ => throw new ArgumentException("Unknown fixture fault."),
        };
        using var http = new HttpClient(new EnvelopeHandler(body));
        await cache.CreateJobAsync(job, 5m);

        var result = await Extractor(http).ExtractAsync(job, fields, auditLocalSuccess: true);

        result.State.Should().Be("unresolved");
        result
            .Reason.Should()
            .Be(fault == "over-usage" ? "usage_exceeded_reservation" : "invalid_response");
        var attempt = await ReadAttempt(job);
        attempt.Stage.Should().Be(stage);
        attempt.Body.Should().Equal(body);
        attempt.State.Should().Be("failed");
        attempt.RequestId.Should().Be("diagnostic-fixture");
        if (measuredCost)
        {
            attempt.Actual.Should().Be(.000015m);
            (await cache.GetJobAsync(job)).AccountedUsd.Should().Be(.000015m);
        }
        else
        {
            attempt.Actual.Should().BeNull();
            (await cache.GetJobAsync(job)).AccountedUsd.Should().Be(attempt.Reserved);
        }
        (await cache.GetCachedResponseAsync(Request(fields))).Should().BeNull();
    }

    [Test]
    public async Task InvalidSiblingRetainsWholeResponseAndPublishesNoPartialCache()
    {
        var job = Guid.NewGuid();
        var fields = new Dictionary<string, string?>
        {
            ["description"] = "Taped by Ari; transferred by Bea",
        };
        using var good = JsonDocument.Parse(
            Normalization.TestLunaRecordingCredits.Credit(
                "Ari",
                "taper",
                "description",
                "Taped by Ari"
            )
        );
        using var bad = JsonDocument.Parse(
            Normalization.TestLunaRecordingCredits.Credit(
                "Bea",
                "taper",
                "description",
                "transferred by Bea"
            )
        );
        var body = Envelope(
            JsonSerializer.Serialize(
                new
                {
                    credits = new[]
                    {
                        good.RootElement.GetProperty("credits")[0],
                        bad.RootElement.GetProperty("credits")[0],
                    },
                }
            )
        );
        var handler = new EnvelopeHandler(body);
        using var http = new HttpClient(handler);
        await cache.CreateJobAsync(job, 5m);

        var result = await Extractor(http).ExtractAsync(job, fields, auditLocalSuccess: true);

        result.State.Should().Be("unresolved");
        result.Reason.Should().Be("invalid_response");
        (await cache.GetCachedResponseAsync(Request(fields))).Should().BeNull();
        var attempt = await ReadAttempt(job);
        attempt.Stage.Should().Be("credit_support");
        attempt.Body.Should().Equal(body);
        attempt.Actual.Should().Be(.000015m);
        handler.Calls.Should().Be(1);
    }

    [Test]
    public async Task NewVersionCannotConsumeOrReplaceAnOlderRequestCache()
    {
        var job = Guid.NewGuid();
        var fields = Fields();
        var currentRequest = Request(fields);
        // Model the former version's distinct rendered request. Exact historical
        // prompt bytes remain in the retained v2 run; this test exercises cache isolation.
        var formerRequest = currentRequest.Replace(
            LunaRecordingCreditExtractor.Version,
            "luna-recording-credits-v2",
            StringComparison.Ordinal
        );
        const string formerResponse = "{\"credits\":[]}";
        await cache.CreateJobAsync(job, 5m);
        var former = await Claim(job, formerRequest);
        await cache.CompleteAsync(
            former.AttemptUuid!.Value,
            new(formerResponse, "former-version", 0, 0),
            0m
        );
        var handler = new EnvelopeHandler(Envelope(Credit(fields["taper"]!)));
        using var http = new HttpClient(handler);

        var result = await Extractor(http).ExtractAsync(job, fields, auditLocalSuccess: true);
        var replay = await Extractor(http, () => "")
            .ExtractAsync(job, fields, auditLocalSuccess: true);

        result.State.Should().Be("extracted");
        result.FromCache.Should().BeFalse();
        result.Extraction.Credits.Should().ContainSingle();
        replay.FromCache.Should().BeTrue();
        replay.Extraction.Should().BeEquivalentTo(result.Extraction);
        handler.Calls.Should().Be(1);
        (await cache.GetCachedResponseAsync(formerRequest)).Should().Be(formerResponse);
        (await cache.GetCachedResponseAsync(currentRequest)).Should().NotBeNull();
    }

    [Test]
    public async Task RetriedSuccessKeepsFailedEvidenceAndReplaysWithoutAnotherPaidRequest()
    {
        var job = Guid.NewGuid();
        var fields = Fields();
        var failedBody = Envelope("{}");
        await cache.CreateJobAsync(job, 5m);
        using var failedHttp = new HttpClient(new EnvelopeHandler(failedBody));
        await Extractor(failedHttp).ExtractAsync(job, fields, auditLocalSuccess: true);
        var failed = await ReadAttempt(job);

        // Repeating a terminal finish must not replace the first evidence or accounting.
        await cache.FailAsync(
            failed.Uuid,
            "invalid_response",
            diagnostic: new(
                LunaCreditFailureStage.CreditSupport,
                Encoding.UTF8.GetBytes("replacement")
            )
        );
        (await ReadAttempt(job)).Body.Should().Equal(failedBody);
        (await ReadAttempt(job)).Stage.Should().Be("credit_schema");

        var handler = new EnvelopeHandler(Envelope(Credit(fields["taper"]!)));
        using var successHttp = new HttpClient(handler);
        var success = await Extractor(successHttp)
            .ExtractAsync(job, fields, auditLocalSuccess: true);
        success.State.Should().Be("extracted");
        success.Extraction.Credits.Should().ContainSingle().Which.Name.Should().Be(fields["taper"]);
        var beforeReplay = (await cache.GetJobAsync(job)).AccountedUsd;
        var replay = await Extractor(successHttp, () => "")
            .ExtractAsync(job, fields, auditLocalSuccess: true);
        replay.FromCache.Should().BeTrue();
        replay.Extraction.Should().BeEquivalentTo(success.Extraction);
        handler.Calls.Should().Be(1);
        (await cache.GetJobAsync(job)).AccountedUsd.Should().Be(beforeReplay);
        var rows = await db.WithWriteConnection(connection =>
            connection.QueryAsync<Attempt>(
                AttemptSql + " WHERE job_uuid=@job ORDER BY created_at,uuid",
                new { job }
            )
        );
        rows.Should().HaveCount(2);
        rows.Single(row => row.State == "failed").Body.Should().Equal(failedBody);
        rows.Single(row => row.State == "succeeded").Body.Should().BeNull();
        rows.Single(row => row.State == "succeeded").Stage.Should().BeNull();
    }

    [Test]
    public async Task DiagnosticSnapshotIsFrozenBeforeTheFirstDatabaseLockWait()
    {
        var job = Guid.NewGuid();
        await cache.CreateJobAsync(job, 5m);
        var request = "diagnostic-freeze-" + job;
        var claim = await Claim(job, request);
        var bytes = Encoding.UTF8.GetBytes("original response 🌿");
        await db.WithWriteConnection(async connection =>
        {
            using var transaction = connection.BeginTransaction();
            await connection.ExecuteAsync(
                "SELECT sha256 FROM normalization.extraction_requests WHERE rendered_request=@request FOR UPDATE",
                new { request },
                transaction
            );
            var finish = cache.FailAsync(
                claim.AttemptUuid!.Value,
                "invalid_response",
                diagnostic: new(LunaCreditFailureStage.CreditSchema, bytes)
            );
            bytes.AsSpan().Fill(0);
            transaction.Rollback();
            await finish;
        });
        (await ReadAttempt(job))
            .Body.Should()
            .Equal(Encoding.UTF8.GetBytes("original response 🌿"));
    }

    [Test]
    public async Task ByteBoundAndStageAreEnforcedWithoutTruncatingOrFinishingTheAttempt()
    {
        var job = Guid.NewGuid();
        await cache.CreateJobAsync(job, 5m);
        var claim = await Claim(job, "diagnostic-bound-" + job);
        var tooLarge = new byte[LunaCreditFailureDiagnostic.MaxResponseBodyBytes + 1];
        var invalidSize = () =>
            cache.FailAsync(
                claim.AttemptUuid!.Value,
                "invalid_response",
                diagnostic: new(LunaCreditFailureStage.ResponseEnvelope, tooLarge)
            );
        await invalidSize.Should().ThrowAsync<ArgumentException>();
        var invalidStage = () =>
            cache.FailAsync(
                claim.AttemptUuid!.Value,
                "invalid_response",
                diagnostic: new((LunaCreditFailureStage)99)
            );
        await invalidStage.Should().ThrowAsync<ArgumentException>();
        (await ReadAttempt(job)).State.Should().Be("running");
        (await cache.GetJobAsync(job)).AccountedUsd.Should().Be(.5m);

        var exactBound = new byte[LunaCreditFailureDiagnostic.MaxResponseBodyBytes];
        exactBound[^1] = 42;
        await cache.FailAsync(
            claim.AttemptUuid!.Value,
            "invalid_response",
            diagnostic: new(LunaCreditFailureStage.ResponseEnvelope, exactBound)
        );
        (await ReadAttempt(job)).Body.Should().Equal(exactBound);
    }

    [Test]
    public async Task NonSuccessHttpDoesNotRetainItsErrorBodyOrInventUsage()
    {
        var job = Guid.NewGuid();
        await cache.CreateJobAsync(job, 5m);
        using var http = new HttpClient(
            new EnvelopeHandler(
                Encoding.UTF8.GetBytes("private upstream error"),
                HttpStatusCode.ServiceUnavailable
            )
        );
        var result = await Extractor(http).ExtractAsync(job, Fields(), auditLocalSuccess: true);

        result.Reason.Should().Be("provider_error");
        var attempt = await ReadAttempt(job);
        attempt.Stage.Should().BeNull();
        attempt.Body.Should().BeNull();
        attempt.Actual.Should().BeNull();
        (await cache.GetJobAsync(job)).AccountedUsd.Should().Be(attempt.Reserved);
    }

    [Test]
    public async Task OversizedReadDoesNotPersistAFabricatedEmptyOrTruncatedBody()
    {
        var job = Guid.NewGuid();
        await cache.CreateJobAsync(job, 5m);
        using var http = new HttpClient(
            new EnvelopeHandler(new byte[LunaCreditFailureDiagnostic.MaxResponseBodyBytes + 1])
        );

        var result = await Extractor(http).ExtractAsync(job, Fields(), auditLocalSuccess: true);

        result.Reason.Should().Be("invalid_response");
        var attempt = await ReadAttempt(job);
        attempt.Stage.Should().Be("response_envelope");
        attempt.Body.Should().BeNull();
        attempt.Actual.Should().BeNull();
        (await cache.GetJobAsync(job)).AccountedUsd.Should().Be(attempt.Reserved);
    }

    private LunaRecordingCreditExtractor Extractor(HttpClient http, Func<string>? key = null) =>
        new(http, cache, new(), key ?? (() => "synthetic-diagnostic-key"));

    private static Dictionary<string, string?> Fields() =>
        new() { ["taper"] = "Quinn " + Guid.NewGuid().ToString("N") };

    private static string Request(IReadOnlyDictionary<string, string?> fields) =>
        LunaRecordingCreditExtractor.RenderRequest(fields, null, new());

    private static string Credit(
        string name,
        string role = "taper",
        string? quote = null,
        int? start = null,
        int? end = null
    ) =>
        RelistenApiTests.Normalization.TestLunaRecordingCredits.Credit(
            name,
            role,
            "taper",
            quote ?? name,
            start,
            end
        );

    private static byte[] Envelope(
        string credit,
        string status = "completed",
        string partType = "output_text",
        int outputTokens = 10
    ) =>
        Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(
                new
                {
                    status,
                    output = new[]
                    {
                        new
                        {
                            type = "message",
                            content = new[] { new { type = partType, text = credit } },
                        },
                    },
                    usage = new { input_tokens = 100, output_tokens = outputTokens },
                }
            ) + "\n"
        );

    private Task<NormalizationExtractionClaim> Claim(Guid job, string request) =>
        cache.TryClaimAsync(job, request, .5m, 2, TimeSpan.FromMinutes(3));

    private const string AttemptSql = """
        SELECT uuid AS "Uuid",state AS "State",reserved_usd AS "Reserved",actual_usd AS "Actual",
            provider_request_id AS "RequestId",failure_stage AS "Stage",failed_response_body AS "Body"
        FROM normalization.extraction_attempts
        """;

    private Task<Attempt> ReadAttempt(Guid job) =>
        db.WithWriteConnection(connection =>
            connection.QuerySingleAsync<Attempt>(AttemptSql + " WHERE job_uuid=@job", new { job })
        );

    private sealed class Attempt
    {
        public Guid Uuid { get; init; }
        public string State { get; init; } = "";
        public decimal Reserved { get; init; }
        public decimal? Actual { get; init; }
        public string? RequestId { get; init; }
        public string? Stage { get; init; }
        public byte[]? Body { get; init; }
    }

    private sealed class EnvelopeHandler(byte[] body, HttpStatusCode status = HttpStatusCode.OK)
        : HttpMessageHandler
    {
        internal int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Calls++;
            var response = new HttpResponseMessage(status) { Content = new ByteArrayContent(body) };
            response.Headers.Add("x-request-id", "diagnostic-fixture");
            return Task.FromResult(response);
        }
    }
}
