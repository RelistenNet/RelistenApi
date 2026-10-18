using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Transactions;
using Dapper;
using FluentAssertions;
using Npgsql;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
using Relisten.Normalization;

namespace RelistenApiTests.Services;

/// <summary>Real retained evidence and PostgreSQL admission, with every HTTP send controlled by the fixture.</summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestNormalizationMusicBrainzAcquisition
{
    private const string UserAgent = "RelistenNormalization.Tests/1.0 (https://relisten.net)";
    private NormalizationDomainFixture fixture = null!;
    private NormalizationEvidenceService evidence = null!;
    private NormalizationMusicBrainzInputStore store = null!;
    private readonly List<HttpClient> clients = [];

    [OneTimeSetUp]
    public void Start()
    {
        fixture = new();
        evidence = new(fixture.Db);
        store = new(fixture.Db);
    }

    [SetUp]
    public Task ResetAdmission() =>
        fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                """
                UPDATE normalization.musicbrainz_gate
                SET attempt_uuid=NULL,token=NULL,lease_until=NULL,not_before=clock_timestamp()-interval '10 seconds'
                WHERE singleton
                """
            )
        );

    [TearDown]
    public void DisposeClients()
    {
        foreach (var client in clients)
        {
            client.Dispose();
        }
        clients.Clear();
    }

    [OneTimeTearDown]
    public void Stop() => fixture?.Dispose();

    [TestCase(3, MusicBrainzLookupKind.Work, "artist-rels%20aliases", "musicbrainz-work-json-v1")]
    [TestCase(
        2,
        MusicBrainzLookupKind.Recording,
        "work-rels%20artist-credits",
        "musicbrainz-recording-work-json-v1"
    )]
    [TestCase(11, MusicBrainzLookupKind.Artist, "aliases", "musicbrainz-artist-json-v1")]
    public async Task ExactLookupProfilesRetainRealBodiesWithoutWritingAcceptedMusicOrPlayback(
        int ordinal,
        MusicBrainzLookupKind kind,
        string includes,
        string captureVersion
    )
    {
        var lookup = Captured(ordinal, kind);
        var before = await DomainCounts();
        var upstream = new StubHandler(
            (_, _) =>
            {
                var response = Response(lookup.Body);
                response.Headers.ETag = new EntityTagHeaderValue("\"fixture-version\"");
                return Task.FromResult(response);
            }
        );
        var service = Service(upstream);

        var result = await service.AcquireAsync(Guid.NewGuid(), lookup.Request);

        result.State.Should().Be(MusicBrainzAcquisitionState.Completed);
        result.Result!.Disposition.Should().Be(MusicBrainzAcquisitionDisposition.Captured);
        var sent = upstream.Requests.Should().ContainSingle().Subject;
        sent.Method.Should().Be("GET");
        sent.Url.Should()
            .Be(
                "https://musicbrainz.org/ws/2/"
                    + kind.ToString().ToLowerInvariant()
                    + "/"
                    + lookup.Request.Mbid.ToString("D")
                    + "?inc="
                    + includes
                    + "&fmt=json"
            );
        sent.UserAgent.Should().Be(UserAgent);
        sent.Accept.Should().Equal("application/json");
        sent.Encodings.Should().Equal("identity");
        sent.Conditional.Should().BeFalse();
        var capture = (await evidence.GetCaptureAsync(result.Result.FetchUuid!.Value))!;
        capture.Revision.Payload.Should().Equal(lookup.Body);
        capture.Revision.CaptureVersion.Should().Be(captureVersion);
        capture.Revision.Resource.Should().Be(lookup.Profile.Resource);
        capture.Fetch.ProviderRevision.Should().Be("\"fixture-version\"");
        capture.Fetch.ExpectedGeneration.Should().Be(result.Attempts.Single().ExpectedGeneration);
        var decoded = new NormalizationProviderDecoder().Decode(capture.Revision);
        decoded.Repertoire.Should().BeEmpty();
        decoded.Recordings.Should().BeEmpty();
        decoded.Setlists.Should().BeEmpty();
        if (kind == MusicBrainzLookupKind.Work)
        {
            decoded
                .MusicBrainzWorks!.Single()
                .Relationships!.Select(relation => relation.Target!.Type!.Value)
                .Should()
                .OnlyContain(type => type == "Person");
        }
        if (kind == MusicBrainzLookupKind.Artist)
        {
            decoded.MusicBrainzArtists!.Single().Artist.Type!.Value.Should().Be("Group");
        }
        (await DomainCounts()).Should().Be(before);
    }

    [Test]
    public async Task FreshCacheReuseDoesNotCaptureAgainButAnEqualByteRefreshAdvancesTheGeneration()
    {
        var lookup = Work();
        var upstream = Success(lookup);
        var service = Service(upstream);
        var first = await service.AcquireAsync(Guid.NewGuid(), lookup.Request);
        var cachedRequest = lookup.Request with
        {
            CacheMode = MusicBrainzCacheMode.ReuseFresh,
            MaximumCacheAge = TimeSpan.MaxValue,
        };

        var cached = await service.AcquireAsync(Guid.NewGuid(), cachedRequest);

        cached.Result!.Disposition.Should().Be(MusicBrainzAcquisitionDisposition.Cached);
        cached.Result.Input.Should().Be(first.Result!.Input);
        cached.Result.FetchUuid.Should().Be(first.Result.FetchUuid);
        cached.Result.ResourceGeneration.Should().Be(first.Result.ResourceGeneration);
        cached.Attempts.Should().BeEmpty();
        upstream.Calls.Should().Be(1);
        (await evidence.GetCurrentInputAsync(lookup.Profile.Resource))!.Generation.Should().Be(1);
        await ReadyGate();

        var refreshed = await service.AcquireAsync(Guid.NewGuid(), lookup.Request);

        refreshed.Result!.Disposition.Should().Be(MusicBrainzAcquisitionDisposition.Captured);
        refreshed.Result.Input.Should().Be(first.Result.Input);
        refreshed.Result.FetchUuid!.Value.Should().NotBe(first.Result.FetchUuid!.Value);
        refreshed.Result.ResourceGeneration.Should().Be(2);
        refreshed.Attempts.Single().ExpectedGeneration.Should().Be(1);
        upstream.Calls.Should().Be(2);
        (await evidence.GetCaptureAsync(refreshed.Result.FetchUuid!.Value))!
            .Fetch.AdvancedCurrent.Should()
            .BeFalse();
    }

    [Test]
    public async Task EqualFreshBytesWithoutAnAdapterReceiptCannotSatisfyTheRetainedCache()
    {
        var lookup = Work();
        var unprovenanced = await Capture(lookup, Guid.NewGuid(), 0, await Now());
        var upstream = Success(lookup);
        var service = Service(upstream);

        var acquired = await service.AcquireAsync(
            Guid.NewGuid(),
            lookup.Request with
            {
                CacheMode = MusicBrainzCacheMode.ReuseFresh,
                MaximumCacheAge = TimeSpan.FromHours(1),
            }
        );

        acquired.Result!.Disposition.Should().Be(MusicBrainzAcquisitionDisposition.Captured);
        acquired.Result.Input!.InputRevisionUuid.Should().Be(unprovenanced.Revision.Uuid);
        acquired.Result.FetchUuid.Should().NotBe(unprovenanced.Fetch.FetchUuid);
        acquired.Result.ResourceGeneration.Should().Be(2);
        upstream.Calls.Should().Be(1);
    }

    [Test]
    public async Task AnAdapterCaptureOutsideTheExplicitCacheAgeRequiresANewRequest()
    {
        var lookup = Work();
        var upstream = Success(lookup);
        var service = Service(upstream);
        var original = await service.AcquireAsync(Guid.NewGuid(), lookup.Request);
        await ReadyGate();

        var acquired = await service.AcquireAsync(
            Guid.NewGuid(),
            lookup.Request with
            {
                CacheMode = MusicBrainzCacheMode.ReuseFresh,
                MaximumCacheAge = TimeSpan.FromTicks(1),
            }
        );

        acquired.Result!.Disposition.Should().Be(MusicBrainzAcquisitionDisposition.Captured);
        acquired.Result.Input.Should().Be(original.Result!.Input);
        acquired.Result.FetchUuid!.Value.Should().NotBe(original.Result.FetchUuid!.Value);
        acquired.Result.ResourceGeneration.Should().Be(2);
        upstream.Calls.Should().Be(2);
    }

    [Test]
    public async Task TerminalReplayReturnsTheOriginalReceiptAndChangedRequestsCannotReuseItsCommand()
    {
        var lookup = Work();
        var upstream = Success(lookup);
        var service = Service(upstream);
        var command = Guid.NewGuid();
        var first = await service.AcquireAsync(command, lookup.Request);

        (await service.AcquireAsync(command, lookup.Request)).Should().BeEquivalentTo(first);
        (await service.ResumeAsync(command)).Should().BeEquivalentTo(first);
        Func<Task> changed = () =>
            service.AcquireAsync(command, lookup.Request with { Mbid = Guid.NewGuid() });
        await changed.Should().ThrowAsync<NormalizationConflictException>();

        upstream.Calls.Should().Be(1);
        (await service.GetAsync(command)).Should().BeEquivalentTo(first);
        (await evidence.GetCurrentInputAsync(lookup.Profile.Resource))!.Generation.Should().Be(1);
    }

    [Test]
    public async Task AHistoricalTerminalReceiptReplaysBeforeCurrentAcquisitionVersionChecks()
    {
        var lookup = Work();
        var command = Guid.NewGuid();
        await store.GetOrCreateAsync(
            command,
            lookup.Request,
            lookup.Profile,
            "musicbrainz-acquisition-historical-v0"
        );
        var terminal = await store.FailAsync(command, "historical_retained_failure");
        var upstream = Success(lookup);
        var service = Service(upstream);

        (await service.AcquireAsync(command, lookup.Request)).Should().BeEquivalentTo(terminal);

        upstream.Calls.Should().Be(0);
        (await evidence.GetCurrentInputAsync(lookup.Profile.Resource)).Should().BeNull();
    }

    [TestCase(503, MusicBrainzAcquisitionState.Pending)]
    [TestCase(404, MusicBrainzAcquisitionState.Failed)]
    public async Task FailedHttpResponsesRetainExactBodiesAndAllowlistedHeadersWithoutReplacingTheHead(
        int status,
        MusicBrainzAcquisitionState expectedState
    )
    {
        var lookup = Work();
        var initialService = Service(Success(lookup));
        await initialService.AcquireAsync(Guid.NewGuid(), lookup.Request);
        var before = (await evidence.GetCurrentInputAsync(lookup.Profile.Resource))!;
        await ReadyGate();
        var body = Encoding.UTF8.GetBytes("{\"error\":\"Retained upstream failure\"}\n");
        var upstream = new StubHandler(
            (_, _) =>
            {
                var response = Response(body, (HttpStatusCode)status);
                response.Headers.TryAddWithoutValidation("Retry-After", "120");
                response.Headers.TryAddWithoutValidation("X-MB-Rate-Limiter", "fixture");
                response.Headers.TryAddWithoutValidation("Set-Cookie", "private-cookie=omitted");
                response.Headers.TryAddWithoutValidation("X-Unrelated", "omitted");
                return Task.FromResult(response);
            }
        );
        var service = Service(upstream);
        var started = DateTimeOffset.UtcNow;

        var failed = await service.AcquireAsync(Guid.NewGuid(), lookup.Request);

        failed.State.Should().Be(expectedState);
        var attempt = failed.Attempts.Should().ContainSingle().Subject;
        var saved = (await store.GetResponseAsync(attempt.AttemptUuid))!;
        saved.Response.StatusCode.Should().Be(status);
        saved.Response.Body.Should().Equal(body);
        saved.Response.BodyComplete.Should().BeTrue();
        saved.Sha256.Should().Be(Convert.ToHexStringLower(SHA256.HashData(body)));
        saved
            .Response.Headers.Keys.Should()
            .Contain(key => key.Equals("Retry-After", StringComparison.OrdinalIgnoreCase));
        saved
            .Response.Headers.Keys.Should()
            .NotContain(key =>
                key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase)
                || key.Equals("X-Unrelated", StringComparison.OrdinalIgnoreCase)
            );
        if (status == 503)
        {
            failed.NotBefore.Should().BeOnOrAfter(started.AddSeconds(119));
            (await service.ResumeAsync(failed.CommandUuid)).Attempts.Should().HaveCount(1);
        }
        (await evidence.GetCurrentInputAsync(lookup.Profile.Resource))
            .Should()
            .BeEquivalentTo(before);
        (await evidence.GetCaptureAsync(attempt.AttemptUuid)).Should().BeNull();
        upstream.Calls.Should().Be(1);
    }

    [TestCase("120", false)]
    [TestCase("9223372036854775807", true)]
    public async Task OversizedResponseMetadataPreservesRetryAfterAndBlocksEveryLookupKind(
        string retryAfter,
        bool clamped
    )
    {
        var lookup = Work();
        var upstream = new StubHandler(
            (_, _) =>
            {
                var response = Response(
                    Encoding.UTF8.GetBytes("{\"error\":\"busy\"}"),
                    HttpStatusCode.ServiceUnavailable
                );
                response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
                response.Headers.TryAddWithoutValidation("ETag", new string('x', 70 * 1024));
                return Task.FromResult(response);
            }
        );
        var service = Service(upstream);
        var started = DateTimeOffset.UtcNow;

        var failed = await service.AcquireAsync(Guid.NewGuid(), lookup.Request);

        failed.State.Should().Be(MusicBrainzAcquisitionState.Failed);
        failed.Result!.Reason.Should().Be("response_metadata_too_large");
        var saved = (await store.GetResponseAsync(failed.Attempts.Single().AttemptUuid))!;
        saved
            .Response.Headers.Single(pair =>
                pair.Key.Equals("Retry-After", StringComparison.OrdinalIgnoreCase)
            )
            .Value.Should()
            .Equal(retryAfter);
        saved
            .Response.Headers.Keys.Should()
            .NotContain(key => key.Equals("ETag", StringComparison.OrdinalIgnoreCase));
        saved.Response.BodyComplete.Should().BeFalse();
        saved.Response.Body.Should().BeNull();
        saved.Sha256.Should().BeNull();

        var otherKind = await service.AcquireAsync(
            Guid.NewGuid(),
            new(MusicBrainzLookupKind.Artist, Guid.NewGuid(), MusicBrainzCacheMode.Refresh)
        );

        otherKind.State.Should().Be(MusicBrainzAcquisitionState.Pending);
        var notBefore = otherKind.NotBefore!.Value;
        notBefore.Should().BeOnOrAfter(started.AddSeconds(119));
        if (clamped)
        {
            notBefore.Year.Should().Be(9998);
        }
        otherKind.Attempts.Should().BeEmpty();
        upstream.Calls.Should().Be(1);
        (await evidence.GetCurrentInputAsync(lookup.Profile.Resource)).Should().BeNull();
    }

    [Test]
    public async Task ConcurrentResumeAcrossServiceInstancesCommitsOnlyOneDispatchAndSend()
    {
        var lookup = Work();
        var command = Guid.NewGuid();
        await store.GetOrCreateAsync(
            command,
            lookup.Request,
            lookup.Profile,
            NormalizationMusicBrainzInputService.Version
        );
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<HttpResponseMessage> Send(HttpRequestMessage _, CancellationToken ct)
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            return Response(lookup.Body);
        }
        var firstHandler = new StubHandler(Send);
        var secondHandler = new StubHandler(Send);
        var firstService = Service(firstHandler);
        var secondService = Service(secondHandler);
        var first = firstService.ResumeAsync(command);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            var concurrent = await secondService.ResumeAsync(command);
            concurrent.State.Should().Be(MusicBrainzAcquisitionState.Pending);
            concurrent.Attempts.Should().ContainSingle();
            (firstHandler.Calls + secondHandler.Calls).Should().Be(1);
        }
        finally
        {
            release.TrySetResult();
            await first;
        }

        (await first).State.Should().Be(MusicBrainzAcquisitionState.Completed);
        (await secondService.ResumeAsync(command)).Attempts.Should().ContainSingle();
        (firstHandler.Calls + secondHandler.Calls).Should().Be(1);
    }

    [Test]
    public async Task OneProviderGateBlocksOtherKindsAndAPeerCacheFillAvoidsASecondSend()
    {
        var lookup = Work();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activeHandler = new StubHandler(
            async (_, ct) =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
                return Response(lookup.Body);
            }
        );
        var active = Service(activeHandler).AcquireAsync(Guid.NewGuid(), lookup.Request);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var waitingHandler = Success(lookup);
        var waitingService = Service(waitingHandler);
        var artist = Captured(11, MusicBrainzLookupKind.Artist);
        var artistHandler = Success(artist);
        var artistService = Service(artistHandler);
        MusicBrainzAcquisition waiting;
        MusicBrainzAcquisition otherKind;
        try
        {
            waiting = await waitingService.AcquireAsync(
                Guid.NewGuid(),
                lookup.Request with
                {
                    CacheMode = MusicBrainzCacheMode.ReuseFresh,
                    MaximumCacheAge = TimeSpan.FromHours(1),
                }
            );
            otherKind = await artistService.AcquireAsync(Guid.NewGuid(), artist.Request);
            waiting.State.Should().Be(MusicBrainzAcquisitionState.Pending);
            otherKind.State.Should().Be(MusicBrainzAcquisitionState.Pending);
            waiting.Attempts.Should().BeEmpty();
            otherKind.Attempts.Should().BeEmpty();
            waiting.NotBefore.Should().Be(otherKind.NotBefore);
            waitingHandler.Calls.Should().Be(0);
            artistHandler.Calls.Should().Be(0);
        }
        finally
        {
            release.TrySetResult();
            await active;
        }
        (await active).State.Should().Be(MusicBrainzAcquisitionState.Completed);

        var filled = await waitingService.ResumeAsync(waiting.CommandUuid);

        filled.Result!.Disposition.Should().Be(MusicBrainzAcquisitionDisposition.Cached);
        filled.Attempts.Should().BeEmpty();
        waitingHandler.Calls.Should().Be(0);
        await ReadyGate();
        (await artistService.ResumeAsync(otherKind.CommandUuid))
            .State.Should()
            .Be(MusicBrainzAcquisitionState.Completed);
        artistHandler.Calls.Should().Be(1);
    }

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, true)]
    public async Task SavedEligibleResponsesRecoverWithoutHttpEvenAfterExpiryOrCaptureCommit(
        bool captureCommitted,
        bool expireAdmission
    )
    {
        var lookup = Work();
        var dispatch = await Dispatch(lookup);
        var response = await SavedResponse(dispatch, lookup.Body);
        (await store.SaveResponseAsync(dispatch, response)).Eligible.Should().BeTrue();
        NormalizationCaptureResult? committed = null;
        if (captureCommitted)
        {
            committed = await Capture(
                lookup,
                dispatch.AttemptUuid,
                dispatch.ExpectedGeneration,
                response.CompletedAt
            );
        }
        if (expireAdmission)
        {
            // A different transport owner can expire while this immutable eligible receipt
            // waits for capture. Expiry must classify only that unsaved owner as uncertain.
            await ReadyGate();
            await Dispatch(Work());
            await ExpireGate();
        }
        var upstream = Success(lookup);

        var resumed = await Service(upstream).ResumeAsync(dispatch.CommandUuid);

        resumed.State.Should().Be(MusicBrainzAcquisitionState.Completed);
        resumed.Result!.FetchUuid.Should().Be(dispatch.AttemptUuid);
        resumed.Result.ResourceGeneration.Should().Be(1);
        resumed.Attempts.Should().ContainSingle().Which.Phase.Should().Be("Captured");
        upstream.Calls.Should().Be(0);
        var retained = (await evidence.GetCaptureAsync(dispatch.AttemptUuid))!;
        retained.Revision.Payload.Should().Equal(lookup.Body);
        if (committed != null)
        {
            retained.Should().BeEquivalentTo(committed);
        }
        (await store.GetResponseAsync(dispatch.AttemptUuid))!.Eligible.Should().BeTrue();
    }

    [Test]
    public async Task ExpiredUnsavedDispatchConsumesAnAttemptAndRecoversWithOneNewGet()
    {
        var lookup = Work();
        var lost = await Dispatch(lookup);
        await ExpireGate();
        var upstream = Success(lookup);

        var recovered = await Service(upstream).ResumeAsync(lost.CommandUuid);

        recovered.State.Should().Be(MusicBrainzAcquisitionState.Completed);
        recovered.Attempts.Should().HaveCount(2);
        recovered.Attempts[0].Phase.Should().Be("Uncertain");
        recovered.Attempts[1].AttemptUuid.Should().NotBe(lost.AttemptUuid);
        recovered.Result!.FetchUuid.Should().Be(recovered.Attempts[1].AttemptUuid);
        upstream.Calls.Should().Be(1);
        (await store.GetResponseAsync(lost.AttemptUuid)).Should().BeNull();
        (await evidence.GetCaptureAsync(lost.AttemptUuid)).Should().BeNull();
    }

    [Test]
    public async Task ALateResponseLosesCaptureAuthorityAndCannotReplaceTheNewAttemptOrItsReceipt()
    {
        var lookup = Work();
        var lost = await Dispatch(lookup);
        await ExpireGate();
        var replacement = (await store.AdvanceAsync(lost.CommandUuid, Options())).Dispatch!;
        replacement.AttemptUuid.Should().NotBe(lost.AttemptUuid);
        var late = await store.SaveResponseAsync(lost, await SavedResponse(lost, lookup.Body));
        late.Eligible.Should().BeFalse();
        (await evidence.GetCurrentInputAsync(lookup.Profile.Resource)).Should().BeNull();
        var winningResponse = await SavedResponse(replacement, lookup.Body);
        var winner = await store.SaveResponseAsync(replacement, winningResponse);
        winner.Eligible.Should().BeTrue();
        var different = winningResponse with
        {
            Body = Encoding.UTF8.GetBytes("different complete bytes"),
        };
        Func<Task> overwrite = () => store.SaveResponseAsync(replacement, different);
        await overwrite.Should().ThrowAsync<NormalizationConflictException>();
        (await store.SaveResponseAsync(replacement, winningResponse))
            .Should()
            .BeEquivalentTo(winner);
        (await store.GetResponseAsync(replacement.AttemptUuid)).Should().BeEquivalentTo(winner);
        var upstream = Success(lookup);

        var completed = await Service(upstream).ResumeAsync(lost.CommandUuid);

        completed.Result!.FetchUuid.Should().Be(replacement.AttemptUuid);
        completed.Attempts.Should().HaveCount(2);
        (await evidence.GetCaptureAsync(lost.AttemptUuid)).Should().BeNull();
        (await store.GetResponseAsync(lost.AttemptUuid))!.Response.Body.Should().Equal(lookup.Body);
        upstream.Calls.Should().Be(0);
    }

    [Test]
    public async Task OriginalGenerationIsRetainedWhenCaptureIsOutOfOrderAndARetryReadsANewClock()
    {
        var lookup = Work();
        var dispatch = await Dispatch(lookup);
        await store.SaveResponseAsync(dispatch, await SavedResponse(dispatch, lookup.Body));
        var newer = lookup with
        {
            Body = JsonSerializer.SerializeToUtf8Bytes(
                new { id = lookup.Request.Mbid, title = "Newer retained response" }
            ),
        };
        var newerCapture = await Capture(newer, Guid.NewGuid(), 0, await Now());
        var upstream = Success(lookup);
        var service = Service(upstream);

        var stale = await service.ResumeAsync(dispatch.CommandUuid);

        stale.State.Should().Be(MusicBrainzAcquisitionState.Pending);
        var historical = (await evidence.GetCaptureAsync(dispatch.AttemptUuid))!;
        historical.Fetch.ExpectedGeneration.Should().Be(0);
        historical.Fetch.AcceptedAsCurrent.Should().BeFalse();
        historical.Revision.Payload.Should().Equal(lookup.Body);
        (await evidence.GetCurrentInputAsync(lookup.Profile.Resource))!
            .Revision.Uuid.Should()
            .Be(newerCapture.Revision.Uuid);
        upstream.Calls.Should().Be(0);
        await ReadyGate();

        var retried = await service.ResumeAsync(dispatch.CommandUuid);

        retried.State.Should().Be(MusicBrainzAcquisitionState.Completed);
        retried.Attempts.Select(attempt => attempt.ExpectedGeneration).Should().Equal(0, 1);
        retried.Result!.ResourceGeneration.Should().Be(2);
        upstream.Calls.Should().Be(1);
    }

    [Test]
    public async Task ThreeTransientResponsesExhaustTheWholeCommandBudgetWithoutAHiddenRetryLoop()
    {
        var lookup = Work();
        var upstream = new StubHandler(
            (_, _) =>
                Task.FromResult(
                    Response(
                        Encoding.UTF8.GetBytes("{\"error\":\"busy\"}"),
                        HttpStatusCode.ServiceUnavailable
                    )
                )
        );
        var service = Service(upstream);
        var result = await service.AcquireAsync(Guid.NewGuid(), lookup.Request);
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            upstream.Calls.Should().Be(attempt);
            result.Attempts.Should().HaveCount(attempt);
            if (attempt < 3)
            {
                result.State.Should().Be(MusicBrainzAcquisitionState.Pending);
                await ReadyGate();
                result = await service.ResumeAsync(result.CommandUuid);
            }
        }

        result.State.Should().Be(MusicBrainzAcquisitionState.Failed);
        result.Result!.Disposition.Should().Be(MusicBrainzAcquisitionDisposition.Failed);
        (await service.ResumeAsync(result.CommandUuid)).Should().BeEquivalentTo(result);
        upstream.Calls.Should().Be(3);
        foreach (var attempt in result.Attempts)
        {
            (await store.GetResponseAsync(attempt.AttemptUuid))!
                .Response.StatusCode.Should()
                .Be(503);
        }
        (await evidence.GetCurrentInputAsync(lookup.Profile.Resource)).Should().BeNull();
    }

    [TestCase("identity")]
    [TestCase("media")]
    [TestCase("encoding")]
    [TestCase("mixed_encoding")]
    [TestCase("body_bound")]
    [TestCase("header_bound")]
    public async Task InvalidRepresentationsRetainFailureReceiptsWithoutCreatingInputs(string fault)
    {
        var lookup = Work();
        var body = fault switch
        {
            "identity" => JsonSerializer.SerializeToUtf8Bytes(
                new { id = Guid.NewGuid(), title = "Other work" }
            ),
            "body_bound" => new byte[MusicBrainzAcquisitionLimits.MaximumResponseBytes + 1],
            _ => lookup.Body,
        };
        var upstream = new StubHandler(
            (_, _) =>
            {
                var response = Response(body);
                if (fault == "media")
                {
                    response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/html");
                }
                if (fault == "mixed_encoding")
                {
                    response.Content.Headers.ContentEncoding.Add("identity");
                }
                if (fault is "encoding" or "mixed_encoding")
                {
                    response.Content.Headers.ContentEncoding.Add("gzip");
                }
                if (fault == "header_bound")
                {
                    response.Headers.TryAddWithoutValidation(
                        "ETag",
                        new string('x', MusicBrainzAcquisitionLimits.MaximumMetadataBytes + 1)
                    );
                }
                return Task.FromResult(response);
            }
        );

        var failed = await Service(upstream).AcquireAsync(Guid.NewGuid(), lookup.Request);
        var timing =
            failed.State == MusicBrainzAcquisitionState.Failed
                ? null
                : await MusicBrainzTimingDiagnostics.ReadAsync(fixture.Db, failed.CommandUuid);

        failed
            .State.Should()
            .Be(
                MusicBrainzAcquisitionState.Failed,
                "original response timing receipts: {0}",
                timing
            );
        if (fault is "encoding" or "mixed_encoding")
        {
            failed.Result!.Reason.Should().Be("unsupported_content_encoding");
        }
        var saved = (await store.GetResponseAsync(failed.Attempts.Single().AttemptUuid))!;
        if (fault is "body_bound" or "header_bound")
        {
            saved.Response.BodyComplete.Should().BeFalse();
            saved.Response.Body.Should().BeNull();
            saved.Sha256.Should().BeNull();
        }
        else
        {
            saved.Response.BodyComplete.Should().BeTrue();
            saved.Response.Body.Should().Equal(body);
        }
        (await evidence.GetCurrentInputAsync(lookup.Profile.Resource)).Should().BeNull();
        (await evidence.GetCaptureAsync(saved.AttemptUuid)).Should().BeNull();
        upstream.Calls.Should().Be(1);
    }

    [Test]
    public async Task CancellationAfterDispatchRetainsTheAttemptAndRecoversWithoutResendingIt()
    {
        var lookup = Work();
        var command = Guid.NewGuid();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var upstream = new StubHandler(
            async (_, ct) =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return Response(lookup.Body);
            }
        );
        using var cancellation = new CancellationTokenSource();
        var cancelled = Service(upstream).AcquireAsync(command, lookup.Request, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        Func<Task> finish = () => cancelled;

        await finish.Should().ThrowAsync<OperationCanceledException>();

        var retained = (await store.GetAsync(command))!;
        retained.Attempts.Should().ContainSingle();
        var response = (await store.GetResponseAsync(retained.Attempts[0].AttemptUuid))!;
        response.Response.BodyComplete.Should().BeFalse();
        response.Response.Reason.Should().Be("caller_cancelled");
        (await evidence.GetCurrentInputAsync(lookup.Profile.Resource)).Should().BeNull();
        upstream.Calls.Should().Be(1);
        var recoveryHandler = Success(lookup);
        var recovery = Service(recoveryHandler);
        // First resume finalizes the saved cancellation receipt; a later invocation gets
        // its own permit and attempt rather than replaying the uncertain network operation.
        (await recovery.ResumeAsync(command))
            .State.Should()
            .Be(MusicBrainzAcquisitionState.Pending);
        recoveryHandler.Calls.Should().Be(0);
        await ReadyGate();
        var completed = await recovery.ResumeAsync(command);
        completed.State.Should().Be(MusicBrainzAcquisitionState.Completed);
        completed.Attempts.Should().HaveCount(2);
        recoveryHandler.Calls.Should().Be(1);
    }

    [Test]
    public async Task CancellationBeforeDispatchAndAmbientTransactionsPerformNoHttpOrCapture()
    {
        var lookup = Work();
        var upstream = Success(lookup);
        var service = Service(upstream);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelledUuid = Guid.NewGuid();
        Func<Task> cancelled = () =>
            service.AcquireAsync(cancelledUuid, lookup.Request, cancellation.Token);
        await cancelled.Should().ThrowAsync<OperationCanceledException>();
        var ambientUuid = Guid.NewGuid();
        using (var ambient = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
        {
            Func<Task> acquire = () => service.AcquireAsync(ambientUuid, lookup.Request);
            await acquire.Should().ThrowAsync<InvalidOperationException>();
        }

        (await store.GetAsync(cancelledUuid)).Should().BeNull();
        (await store.GetAsync(ambientUuid)).Should().BeNull();
        (await evidence.GetCurrentInputAsync(lookup.Profile.Resource)).Should().BeNull();
        upstream.Calls.Should().Be(0);
    }

    [Test]
    public async Task MissingProviderAdmissionStateFailsClosedBeforeCreatingACommandOrSendingHttp()
    {
        var lookup = Work();
        var command = Guid.NewGuid();
        var upstream = Success(lookup);
        var service = Service(upstream);
        try
        {
            await fixture.Db.WithWriteConnection(c =>
                c.ExecuteAsync("DELETE FROM normalization.musicbrainz_gate WHERE singleton")
            );
            Func<Task> acquire = () => service.AcquireAsync(command, lookup.Request);

            await acquire.Should().ThrowAsync<InvalidOperationException>();

            upstream.Calls.Should().Be(0);
            (await service.GetAsync(command)).Should().BeNull();
            (await evidence.GetCurrentInputAsync(lookup.Profile.Resource)).Should().BeNull();
        }
        finally
        {
            await fixture.Db.WithWriteConnection(c =>
                c.ExecuteAsync(
                    """
                    INSERT INTO normalization.musicbrainz_gate(singleton) VALUES(true) ON CONFLICT DO NOTHING
                    """
                )
            );
        }
    }

    [Test]
    public async Task ASubsequentCatalogRollbackPreservesTheCompletedAcquisitionAndItsIndependentEvidence()
    {
        var source = await fixture.Source();
        var artist = await fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<(Guid Uuid, string Name)>(
                "SELECT uuid,name FROM artists WHERE id=@ArtistId",
                source
            )
        );
        var lookup = Work();
        var upstream = Success(lookup);
        var service = Service(upstream);
        var acquired = await service.AcquireAsync(Guid.NewGuid(), lookup.Request);
        acquired.State.Should().Be(MusicBrainzAcquisitionState.Completed);
        var attempt = acquired.Attempts.Single();
        var saved = (await store.GetResponseAsync(attempt.AttemptUuid))!;
        var capture = (await evidence.GetCaptureAsync(attempt.AttemptUuid))!;

        await fixture.Db.WithWriteConnection(async c =>
        {
            await using var transaction = await ((NpgsqlConnection)c).BeginTransactionAsync();
            var changed = await c.ExecuteAsync(
                "UPDATE artists SET name=@Name WHERE uuid=@Uuid",
                new { artist.Uuid, Name = "Rolled back catalog name" },
                transaction
            );
            changed.Should().Be(1);
            (
                await c.QuerySingleAsync<string>(
                    "SELECT name FROM artists WHERE uuid=@Uuid",
                    new { artist.Uuid },
                    transaction
                )
            )
                .Should()
                .Be("Rolled back catalog name");
            await transaction.RollbackAsync();
        });

        (
            await fixture.Db.WithWriteConnection(c =>
                c.QuerySingleAsync<string>(
                    "SELECT name FROM artists WHERE uuid=@Uuid",
                    new { artist.Uuid }
                )
            )
        )
            .Should()
            .Be(artist.Name);
        (await service.ResumeAsync(acquired.CommandUuid)).Should().BeEquivalentTo(acquired);
        (await store.GetResponseAsync(attempt.AttemptUuid)).Should().BeEquivalentTo(saved);
        saved.Response.Body.Should().Equal(lookup.Body);
        (await evidence.GetCaptureAsync(attempt.AttemptUuid)).Should().BeEquivalentTo(capture);
        (await evidence.GetCurrentInputAsync(lookup.Profile.Resource))!
            .Revision.Uuid.Should()
            .Be(capture.Revision.Uuid);
        upstream.Calls.Should().Be(1);
    }

    private NormalizationMusicBrainzInputService Service(StubHandler upstream)
    {
        var client = new HttpClient(upstream) { Timeout = Timeout.InfiniteTimeSpan };
        clients.Add(client);
        return new(client, store, evidence, Options());
    }

    private static MusicBrainzAcquisitionOptions Options() => new() { UserAgent = UserAgent };

    private static Lookup Work()
    {
        var id = Guid.NewGuid();
        var request = new MusicBrainzLookupRequest(
            MusicBrainzLookupKind.Work,
            id,
            MusicBrainzCacheMode.Refresh
        );
        return new(
            request,
            MusicBrainzHttpPolicy.Profile(request),
            JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    id,
                    title = "Retained work",
                    relations = Array.Empty<object>(),
                }
            )
        );
    }

    private static Lookup Captured(int ordinal, MusicBrainzLookupKind kind)
    {
        var root = Path.Combine(
            TestContext.CurrentContext.TestDirectory,
            "Fixtures",
            "normalization",
            "musicbrainz"
        );
        using var manifest = JsonDocument.Parse(
            File.ReadAllBytes(Path.Combine(root, "manifest.json"))
        );
        var response = manifest
            .RootElement.GetProperty("responses")
            .EnumerateArray()
            .Single(item => item.GetProperty("ordinal").GetInt32() == ordinal);
        var request = new MusicBrainzLookupRequest(
            kind,
            Guid.Parse(response.GetProperty("providerKey").GetString()!),
            MusicBrainzCacheMode.Refresh
        );
        return new(
            request,
            MusicBrainzHttpPolicy.Profile(request),
            File.ReadAllBytes(Path.Combine(root, response.GetProperty("file").GetString()!))
        );
    }

    private async Task<MusicBrainzDispatch> Dispatch(Lookup lookup)
    {
        var command = Guid.NewGuid();
        await store.GetOrCreateAsync(
            command,
            lookup.Request,
            lookup.Profile,
            NormalizationMusicBrainzInputService.Version
        );
        var step = await store.AdvanceAsync(command, Options());
        step.Dispatch.Should().NotBeNull();
        return step.Dispatch!;
    }

    private async Task<MusicBrainzHttpResponse> SavedResponse(
        MusicBrainzDispatch dispatch,
        byte[] body
    ) =>
        new(
            200,
            dispatch.Profile.RequestUrl,
            new Dictionary<string, string[]> { ["Content-Type"] = ["application/json"] },
            body,
            true,
            await Now()
        );

    private Task<NormalizationCaptureResult> Capture(
        Lookup lookup,
        Guid fetchUuid,
        long generation,
        DateTimeOffset observedAt
    ) =>
        evidence.CaptureInputAsync(
            new(
                fetchUuid,
                lookup.Profile.Resource,
                lookup.Body,
                "application/json",
                lookup.Profile.CaptureVersion,
                generation,
                observedAt
            )
        );

    private async Task<DateTimeOffset> Now() =>
        new(
            await fixture.Db.WithWriteConnection(c =>
                c.ExecuteScalarAsync<DateTime>("SELECT clock_timestamp()")
            )
        );

    // Only transport admission clocks are advanced by tests. Immutable command preparation,
    // HTTP receipts and evidence history are always created through their owning public store.
    private Task ReadyGate() =>
        fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                """
                UPDATE normalization.musicbrainz_gate SET not_before=clock_timestamp()-interval '10 seconds' WHERE singleton
                """
            )
        );

    private Task ExpireGate() =>
        fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                """
                UPDATE normalization.musicbrainz_gate
                SET lease_until=CASE WHEN token IS NULL THEN NULL ELSE clock_timestamp()-interval '10 seconds' END,
                    not_before=clock_timestamp()-interval '10 seconds'
                WHERE singleton
                """
            )
        );

    private Task<AcceptedCounts> DomainCounts() =>
        fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<AcceptedCounts>(
                """
                SELECT (SELECT count(*) FROM normalization.people) AS "People",
                    (SELECT count(*) FROM normalization.music_revisions) AS "Music",
                    (SELECT count(*) FROM normalization.work_subjects) AS "Subjects",
                    (SELECT count(*) FROM sources) AS "Sources",
                    (SELECT count(*) FROM source_tracks) AS "Tracks",
                    (SELECT count(*) FROM source_track_plays) AS "Plays"
                """
            )
        );

    private static StubHandler Success(Lookup lookup) =>
        new((_, _) => Task.FromResult(Response(lookup.Body)));

    private static HttpResponseMessage Response(
        byte[] body,
        HttpStatusCode status = HttpStatusCode.OK
    )
    {
        var response = new HttpResponseMessage(status) { Content = new ByteArrayContent(body) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return response;
    }

    private sealed record Lookup(
        MusicBrainzLookupRequest Request,
        MusicBrainzRequestProfile Profile,
        byte[] Body
    );

    private sealed record AcceptedCounts(
        long People,
        long Music,
        long Subjects,
        long Sources,
        long Tracks,
        long Plays
    );

    private sealed record SentRequest(
        string Method,
        string Url,
        string UserAgent,
        IReadOnlyList<string> Accept,
        IReadOnlyList<string> Encodings,
        bool Conditional
    );

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send
    ) : HttpMessageHandler
    {
        private int calls;
        public int Calls => Volatile.Read(ref calls);
        public ConcurrentQueue<SentRequest> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken ct
        )
        {
            Interlocked.Increment(ref calls);
            Requests.Enqueue(
                new(
                    request.Method.Method,
                    request.RequestUri!.AbsoluteUri,
                    request.Headers.UserAgent.ToString(),
                    request.Headers.Accept.Select(value => value.ToString()).ToArray(),
                    request.Headers.AcceptEncoding.Select(value => value.ToString()).ToArray(),
                    request.Headers.Contains("If-None-Match")
                        || request.Headers.Contains("If-Modified-Since")
                )
            );
            var response = await send(request, ct);
            response.RequestMessage ??= request;
            return response;
        }
    }
}
