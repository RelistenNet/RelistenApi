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
using RelistenApiTests.Services;

namespace RelistenApiTests;

[TestFixture]
public sealed class TestLunaPeopleContextMatcher
{
    [Test]
    public void ProposalKeepsExactQuestionAndActorEvidence()
    {
        var input = Input();
        var decision = LunaPeopleContextMatcher.ValidateResponse(input, Response()).Single();

        decision.QuestionId.Should().Be("pair-1");
        decision.Relation.Should().Be("same_person");
        decision
            .Evidence.Should()
            .BeEquivalentTo(
                new[]
                {
                    new PeopleContextCitation("left", "taper_notes", "Taped by Sam (samtapes)"),
                    new PeopleContextCitation("right", "description", "Transferred by samtapes"),
                }
            );
    }

    [TestCase("{\"decisions\":[]}")]
    [TestCase(
        "{\"decisions\":[{\"questionId\":\"other\",\"relation\":\"insufficient_evidence\",\"reason\":\"No distinction\",\"evidence\":[]}]}"
    )]
    [TestCase(
        "{\"decisions\":[{\"questionId\":\"pair-1\",\"relation\":\"accepted\",\"reason\":\"Known\",\"evidence\":[]}]}"
    )]
    public void RejectsMissingInventedOrAcceptedDecisions(string response)
    {
        var act = () => LunaPeopleContextMatcher.ValidateResponse(Input(), response);
        act.Should().Throw<LunaCreditResponseException>();
    }

    [Test]
    public void RejectsDuplicateQuestionsAndCrossSourceQuotes()
    {
        var response = JsonDocument
            .Parse(Response())
            .RootElement.GetProperty("decisions")[0]
            .GetRawText();
        var duplicate = () =>
            LunaPeopleContextMatcher.ValidateResponse(
                Input(),
                "{\"decisions\":[" + response + "," + response + "]}"
            );
        duplicate.Should().Throw<LunaCreditResponseException>();

        var wrongActor = () =>
            LunaPeopleContextMatcher.ValidateResponse(
                Input(),
                Response().Replace("Taped by Sam (samtapes)", "Transferred by samtapes")
            );
        wrongActor.Should().Throw<LunaCreditResponseException>();
    }

    [TestCase("same_person")]
    [TestCase("different_people")]
    public void IdentityProposalsRequireEvidenceFromBothWitnesses(string relation)
    {
        var response = JsonSerializer.Serialize(
            new
            {
                decisions = new[]
                {
                    new
                    {
                        questionId = "pair-1",
                        relation,
                        reason = "A hypothesis",
                        evidence = new[]
                        {
                            new
                            {
                                side = "left",
                                field = "taper_notes",
                                quote = "Sam",
                            },
                        },
                    },
                },
            }
        );
        var act = () => LunaPeopleContextMatcher.ValidateResponse(Input(), response);
        act.Should().Throw<LunaCreditResponseException>();
    }

    [Test]
    public void InsufficientEvidenceCanRemainUnlinked()
    {
        var response =
            "{\"decisions\":[{\"questionId\":\"pair-1\",\"relation\":\"insufficient_evidence\",\"reason\":\"The name alone cannot distinguish these actors.\",\"evidence\":[]}]}";
        LunaPeopleContextMatcher
            .ValidateResponse(Input(), response)
            .Single()
            .Evidence.Should()
            .BeEmpty();
    }

    [Test]
    public void RequestPreservesWitnessCoordinatesAndSeparatesUntrustedContent()
    {
        var input = Input();
        var request = JsonDocument.Parse(LunaPeopleContextMatcher.RenderRequest(input)).RootElement;
        var supplied = JsonDocument
            .Parse(request.GetProperty("input")[1].GetProperty("content").GetString()!)
            .RootElement;
        var left = supplied.GetProperty("questions")[0].GetProperty("left");

        left.GetProperty("sourceUuid").GetGuid().Should().Be(input.Questions[0].Left.SourceUuid);
        left.GetProperty("creditId").GetInt64().Should().Be(101);
        left.GetProperty("creditKey").GetString().Should().Be("credit:101");
        supplied
            .GetProperty("questions")[0]
            .GetProperty("retrievalBasis")
            .GetString()
            .Should()
            .Be("same lexical handle; retrieval only");
        request.GetProperty("store").GetBoolean().Should().BeFalse();
    }

    [Test]
    public void InvalidWitnessSpanFailsBeforeSpend()
    {
        var input = Input();
        var question = input.Questions[0];
        var bad = input with
        {
            Questions =
            [
                question with
                {
                    Left = question.Left with { Evidence = [new("taper_notes", "𝄞 Sam", 0, 6)] },
                },
            ],
        };
        var act = () => LunaPeopleContextMatcher.ValidateInput(bad);
        act.Should().Throw<ArgumentException>();
    }

    internal static PeopleContextBatchInput Input()
    {
        const string leftQuote = "Taped by Sam (samtapes)";
        const string rightQuote = "Transferred by samtapes";
        return new(
            "batch-1",
            [
                new(
                    "pair-1",
                    "same lexical handle; retrieval only",
                    new(
                        "sam",
                        101,
                        "credit:101",
                        Guid.Parse("11111111-1111-1111-1111-111111111111"),
                        Guid.NewGuid(),
                        "Artist A",
                        "Sam (samtapes)",
                        ["taper"],
                        "1978-00-00",
                        [new("taper_notes", leftQuote, 0, leftQuote.Length)],
                        null
                    ),
                    new(
                        "samtapes",
                        102,
                        null,
                        Guid.Parse("22222222-2222-2222-2222-222222222222"),
                        Guid.NewGuid(),
                        "Artist B",
                        "samtapes",
                        ["transferrer"],
                        null,
                        [new("description", rightQuote, 0, rightQuote.Length)],
                        null
                    )
                ),
            ]
        );
    }

    internal static string Response() =>
        JsonSerializer.Serialize(
            new
            {
                decisions = new[]
                {
                    new
                    {
                        questionId = "pair-1",
                        relation = "same_person",
                        reason = "The explicit handle supplies a plausible connection.",
                        evidence = new[]
                        {
                            new
                            {
                                side = "left",
                                field = "taper_notes",
                                quote = "Taped by Sam (samtapes)",
                            },
                            new
                            {
                                side = "right",
                                field = "description",
                                quote = "Transferred by samtapes",
                            },
                        },
                    },
                },
            }
        );
}

[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestLunaPeopleContextMatcherCache
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
            Assert.Ignore("Run ./test-local.sh for the dedicated local database tests.");
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
    public async Task ValidProposalCachesWithoutAnotherHttpRequestOrApiKey()
    {
        var job = Guid.NewGuid();
        await cache.CreateJobAsync(job, maximumTotalAttempts: 1);
        var handler = new Handler(TestLunaPeopleContextMatcher.Response());
        using var http = new HttpClient(handler);
        var input = TestLunaPeopleContextMatcher.Input();
        var first = await new LunaPeopleContextMatcher(http, cache, () => "fixture-key").MatchAsync(
            job,
            input
        );
        var replay = await new LunaPeopleContextMatcher(
            http,
            cache,
            () => throw new AssertionException("Cached requests need no key")
        ).MatchAsync(job, input);

        first.State.Should().Be("proposed");
        first.ProviderRequestId.Should().Be("context-request");
        first.InputTokens.Should().Be(100);
        first.ActualUsd.Should().Be(.00011m);
        replay.FromCache.Should().BeTrue();
        replay.Decisions.Should().BeEquivalentTo(first.Decisions);
        handler.Calls.Should().Be(1);
        (await cache.GetJobAsync(job)).AccountedUsd.Should().Be(first.ActualUsd);
    }

    [Test]
    public async Task InvalidSupportRetainsKnownUsageButNeverCachesTheProposal()
    {
        var job = Guid.NewGuid();
        await cache.CreateJobAsync(job);
        using var http = new HttpClient(
            new Handler(
                TestLunaPeopleContextMatcher
                    .Response()
                    .Replace("Taped by Sam (samtapes)", "invented evidence")
            )
        );
        var input = TestLunaPeopleContextMatcher.Input();
        var result = await new LunaPeopleContextMatcher(
            http,
            cache,
            () => "fixture-key"
        ).MatchAsync(job, input);

        result.State.Should().Be("unresolved");
        result.Reason.Should().Be("invalid_response");
        result.InputTokens.Should().Be(100);
        result.ActualUsd.Should().Be(.00011m);
        (await cache.GetJobAsync(job)).AccountedUsd.Should().Be(.00011m);
        (await cache.GetCachedResponseAsync(LunaPeopleContextMatcher.RenderRequest(input)))
            .Should()
            .BeNull();
    }

    [Test]
    public async Task CancellationRetainsTheUncertainReservationBeforeReturning()
    {
        var job = Guid.NewGuid();
        await cache.CreateJobAsync(job);
        using var cancellation = new CancellationTokenSource();
        using var http = new HttpClient(new CancelHandler(cancellation));
        var match = () =>
            new LunaPeopleContextMatcher(http, cache, () => "fixture-key").MatchAsync(
                job,
                TestLunaPeopleContextMatcher.Input(),
                cancellation.Token
            );

        await match.Should().ThrowAsync<OperationCanceledException>();
        (await cache.GetJobAsync(job)).AccountedUsd.Should().BeGreaterThan(0m);
        var attemptState = await db.WithWriteConnection(connection =>
            connection.QuerySingleAsync<string>(
                "SELECT state FROM normalization.extraction_attempts WHERE job_uuid=@job",
                new { job }
            )
        );
        attemptState.Should().Be("failed");
    }

    private sealed class Handler(string json) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Calls++;
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
            response.Headers.Add("x-request-id", "context-request");
            return Task.FromResult(response);
        }
    }

    private sealed class CancelHandler(CancellationTokenSource cancellation) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellationToken);
        }
    }
}
