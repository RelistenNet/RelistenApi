using System.Diagnostics;
using Dapper;
using FluentAssertions;
using Npgsql;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
using Relisten.Normalization;
using CatalogSource = RelistenApiTests.Services.NormalizationDomainFixture.CatalogSource;

namespace RelistenApiTests.Services;

/// <summary>Page recovery exercises the real processor, durable stores and PostgreSQL failure boundaries.</summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestNormalizationRecordingPageService
{
    private NormalizationDomainFixture fixture = null!;
    private NormalizationRecordingPageStore pages = null!;
    private NormalizationRecordingProposalService proposals = null!;
    private NormalizationEvidenceService evidence = null!;
    private NormalizationLegacyInputService legacy = null!;
    private NormalizationRecordingPageService runner = null!;

    [OneTimeSetUp]
    public void Start()
    {
        fixture = new();
        pages = new(fixture.Db);
        proposals = new(fixture.Db);
        evidence = new(fixture.Db);
        legacy = new(fixture.Db, evidence);
        var processor = new NormalizationRecordingProcessor(
            legacy,
            evidence,
            new(fixture.Db),
            new NormalizationProviderDecoder(),
            new(new(), new()),
            fixture.Work,
            proposals
        );
        runner = new(pages, processor, proposals);
    }

    [OneTimeTearDown]
    public void Stop() => fixture?.Dispose();

    [Test]
    public async Task StablePageReplaysTerminalAttemptsWithoutAnotherRefreshOrAcceptedWrites()
    {
        var artist = await ArtistWithSources(3);
        var acceptedBefore = await AcceptedRowCount();
        var pageUuid = Guid.NewGuid();

        var first = await runner.ProcessPageAsync(pageUuid, artist.Uuid, limit: 2);
        var fetches = await Fetches(first.Page);
        var replay = await runner.ProcessPageAsync(pageUuid, artist.Uuid, limit: 2);

        first.Published.Should().Be(2);
        first.Settled.Should().BeTrue();
        first.ContinuationCursor.Should().Be(first.Page.Items[^1].SourceUuid);
        replay.Page.Should().BeEquivalentTo(first.Page);
        replay.InvocationUuid.Should().NotBe(first.InvocationUuid);
        replay.Items.Should().OnlyContain(item => item.Result != null && item.Result.Replayed);
        (await Fetches(first.Page)).Should().Equal(fetches);
        (await AcceptedRowCount()).Should().Be(acceptedBefore);
        var next = await runner.ProcessPageAsync(
            Guid.NewGuid(),
            artist.Uuid,
            first.ContinuationCursor,
            2
        );
        next.Page.Items.Should().ContainSingle();
        next.Page.Items.Should()
            .NotContain(item =>
                first.Page.Items.Any(previous => previous.SourceUuid == item.SourceUuid)
            );
        next.Published.Should().Be(1);
        next.ContinuationCursor.Should().BeNull();
    }

    [Test]
    public async Task RetryableDatabaseFailureKeepsThePagePartialWhileLaterItemsComplete()
    {
        var artist = await ArtistWithSources(3);
        var page = await Select(artist.Uuid, 2);
        NormalizationRecordingPageResult partial = null!;
        await WithAttemptFailure(
            page.Items[0].AttemptUuid,
            "40001",
            async () =>
            {
                partial = await runner.ProcessPageAsync(page.PageUuid, artist.Uuid, limit: 2);
            }
        );

        partial.Settled.Should().BeFalse();
        partial.ContinuationCursor.Should().BeNull();
        partial.Failed.Should().Be(1);
        partial.Published.Should().Be(1);
        var failure = partial.Items[0].Failure!;
        failure.Code.Should().Be(NormalizationRecordingPageFailureCode.DatabaseFailed);
        failure.Retryable.Should().BeTrue();
        var completedFetches = await Fetches(page.Items[1].SourceUuid);
        var resumed = await runner.ProcessPageAsync(page.PageUuid, artist.Uuid, limit: 2);

        resumed.Settled.Should().BeTrue();
        resumed.Published.Should().Be(2);
        resumed.Items[1].Result!.Replayed.Should().BeTrue();
        resumed.ContinuationCursor.Should().Be(page.Items[^1].SourceUuid);
        (await Fetches(page.Items[1].SourceUuid)).Should().Equal(completedFetches);
        (await pages.GetFailuresAsync(page.PageUuid))
            .Should()
            .ContainSingle()
            .Which.Should()
            .BeEquivalentTo(failure);
    }

    [Test]
    public async Task ChangedPreparationGetsARetryableReceiptAndWholeRefreshRecoversTheItem()
    {
        var artist = await ArtistWithSources(2);
        var page = await Select(artist.Uuid, 2);
        var first = page.Items[0];
        await legacy.RefreshRecordingAsync(first.SourceUuid);
        await using var blocker = new NpgsqlConnection(LocalTestDatabase.ConnectionString);
        await blocker.OpenAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await blocker.ExecuteAsync(
            "SELECT 1 FROM normalization.input_resources WHERE provider='legacy_catalog' AND resource_type='recording' AND provider_key=@key FOR UPDATE",
            new { key = first.SourceUuid.ToString("D") },
            transaction
        );
        var processing = runner.ProcessPageAsync(page.PageUuid, artist.Uuid, limit: 2);
        try
        {
            await RequireBlockedBy(blocker.ProcessID);
            // The producer has already read its facts and clocks but cannot retain them yet.
            // Changing those facts must make checked binding fail, without ending the page.
            await fixture.Db.WithWriteConnection(c =>
                c.ExecuteAsync(
                    "UPDATE sources SET taper='Pat Current' WHERE uuid=@uuid",
                    new { uuid = first.SourceUuid }
                )
            );
        }
        finally
        {
            await transaction.CommitAsync();
            await processing;
        }
        var partial = await processing;
        partial.Settled.Should().BeFalse();
        partial.Published.Should().Be(1);
        partial
            .Items[0]
            .Failure!.Code.Should()
            .Be(NormalizationRecordingPageFailureCode.PreparationConflict);
        partial.Items[0].Failure!.Retryable.Should().BeTrue();
        (await proposals.GetAttemptAsync(first.AttemptUuid))!.Preparation.Should().BeNull();

        var resumed = await runner.ProcessPageAsync(page.PageUuid, artist.Uuid, limit: 2);
        resumed.Settled.Should().BeTrue();
        resumed.Published.Should().Be(2);
        resumed.Items[1].Result!.Replayed.Should().BeTrue();
        (await proposals.GetAsync(first.SourceUuid))!
            .Extraction.Credits.Should()
            .Contain(credit => credit.Mention.Name == "Pat Current");
    }

    [Test]
    public async Task CancellationLeavesCompletedItemsAndUnfinishedPreparationResumableWithoutFailureReceipt()
    {
        var artist = await ArtistWithSources(2);
        var page = await Select(artist.Uuid, 2);
        await legacy.RefreshRecordingAsync(page.Items[1].SourceUuid);
        await using var blocker = new NpgsqlConnection(LocalTestDatabase.ConnectionString);
        await blocker.OpenAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await blocker.ExecuteAsync(
            "SELECT 1 FROM normalization.input_resources WHERE provider='legacy_catalog' AND resource_type='recording' AND provider_key=@key FOR UPDATE",
            new { key = page.Items[1].SourceUuid.ToString("D") },
            transaction
        );
        using var cancellation = new CancellationTokenSource();
        var processing = runner.ProcessPageAsync(
            page.PageUuid,
            artist.Uuid,
            limit: 2,
            cancellationToken: cancellation.Token
        );
        try
        {
            await RequireBlockedBy(blocker.ProcessID);
            (await proposals.GetAttemptAsync(page.Items[0].AttemptUuid))!
                .Result!.Disposition.Should()
                .Be(NormalizationRecordingProcessingDisposition.Published);
        }
        finally
        {
            cancellation.Cancel();
            await transaction.RollbackAsync();
            try
            {
                await processing;
            }
            catch (OperationCanceledException)
            {
                // Cancellation intentionally interrupts the second capture after the first commit.
            }
        }
        var cancelled = () => processing;
        await cancelled.Should().ThrowAsync<OperationCanceledException>();
        (await pages.GetFailuresAsync(page.PageUuid)).Should().BeEmpty();
        (await proposals.GetAttemptAsync(page.Items[1].AttemptUuid))!.Result.Should().BeNull();

        var resumed = await runner.ProcessPageAsync(page.PageUuid, artist.Uuid, limit: 2);
        resumed.Settled.Should().BeTrue();
        resumed.Items[0].Result!.Replayed.Should().BeTrue();
        resumed.Published.Should().Be(2);
    }

    [Test]
    public async Task MissingRecordingSettlesAsAnAttentionOutcomeAndAllowsContinuation()
    {
        var artist = await ArtistWithSources(3);
        var page = await Select(artist.Uuid, 2);
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "DELETE FROM sources WHERE uuid=@uuid",
                new { uuid = page.Items[0].SourceUuid }
            )
        );

        var result = await runner.ProcessPageAsync(page.PageUuid, artist.Uuid, limit: 2);

        result.Settled.Should().BeTrue();
        result.Missing.Should().Be(1);
        result.Published.Should().Be(1);
        result.Failed.Should().Be(0);
        result.ContinuationCursor.Should().Be(page.Items[^1].SourceUuid);
    }

    [Test]
    public async Task SavedArtistFilterDoesNotOverrideCurrentRecordingOwnership()
    {
        var artist = await ArtistWithSources(1);
        var page = await Select(artist.Uuid, 2);
        var other = await fixture.Source();
        var otherArtistUuid = await ArtistUuid(other.ArtistId);
        await MoveSource(artist.Sources.Single(), other.ArtistId);

        var result = await runner.ProcessPageAsync(page.PageUuid, artist.Uuid, limit: 2);

        result.Page.ArtistUuid.Should().Be(artist.Uuid);
        result.Published.Should().Be(1);
        var batch = (await proposals.GetAsync(page.Items.Single().SourceUuid))!;
        batch.ArtistUuid.Should().Be(otherArtistUuid);
        batch
            .Preparation.Binding.Files.Single()
            .Binding.Status.Should()
            .Be(NormalizationCatalogBindingStatus.Bound);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task IncompatiblePlannedAttemptIsRecordedAsUnexpectedAndRethrown(bool wrongVersion)
    {
        var artist = await ArtistWithSources(1);
        var page = await Select(artist.Uuid, 2);
        var item = page.Items.Single();
        await proposals.GetOrCreateAttemptAsync(
            item.AttemptUuid,
            wrongVersion ? item.SourceUuid : Guid.NewGuid(),
            wrongVersion ? "incompatible-processor-version" : page.ProcessingVersion
        );

        var process = () => runner.ProcessPageAsync(page.PageUuid, artist.Uuid, limit: 2);
        await process.Should().ThrowAsync<InvalidOperationException>();

        var failure = (await pages.GetFailuresAsync(page.PageUuid))
            .Should()
            .ContainSingle()
            .Subject;
        failure.Code.Should().Be(NormalizationRecordingPageFailureCode.ProcessingFailed);
        failure.Retryable.Should().BeFalse();
        (await Fetches(item.SourceUuid)).Should().BeEmpty();
        (await proposals.GetAttemptAsync(item.AttemptUuid))!.Result.Should().BeNull();
    }

    [Test]
    public async Task UnexpectedProcessorFailureGetsASafeReceiptAndStopsTheInvocation()
    {
        var artist = await ArtistWithSources(2);
        var page = await Select(artist.Uuid, 2);
        await WithAttemptFailure(
            page.Items[0].AttemptUuid,
            "P0001",
            async () =>
            {
                var process = () => runner.ProcessPageAsync(page.PageUuid, artist.Uuid, limit: 2);
                await process.Should().ThrowAsync<Exception>();
            }
        );

        var failure = (await pages.GetFailuresAsync(page.PageUuid))
            .Should()
            .ContainSingle()
            .Subject;
        failure.Code.Should().Be(NormalizationRecordingPageFailureCode.ProcessingFailed);
        failure.Retryable.Should().BeFalse();
        (await proposals.GetAttemptAsync(page.Items[1].AttemptUuid)).Should().BeNull();
        (await runner.ProcessPageAsync(page.PageUuid, artist.Uuid, limit: 2))
            .Published.Should()
            .Be(2);
    }

    private Task<NormalizationRecordingPage> Select(Guid artistUuid, int limit) =>
        pages.GetOrCreateAsync(
            Guid.NewGuid(),
            artistUuid,
            null,
            limit,
            NormalizationRecordingProcessor.Version
        );

    private async Task<ArtistSources> ArtistWithSources(int count)
    {
        var first = await fixture.Source();
        var sources = new List<CatalogSource> { first };
        for (var index = 1; index < count; index++)
        {
            var source = await fixture.Source();
            await MoveSource(source, first.ArtistId);
            sources.Add(source with { ArtistId = first.ArtistId });
        }
        return new(await ArtistUuid(first.ArtistId), sources);
    }

    private Task<Guid> ArtistUuid(int id) =>
        fixture.Db.WithWriteConnection(c =>
            c.ExecuteScalarAsync<Guid>("SELECT uuid FROM artists WHERE id=@id", new { id })
        );

    private Task MoveSource(CatalogSource source, int artistId) =>
        fixture.Db.WithWriteConnection(async c =>
        {
            await c.ExecuteAsync(
                "UPDATE source_tracks SET artist_id=@artistId,mp3_url=@url WHERE source_id=@id",
                new
                {
                    artistId,
                    url = "https://example.test/" + source.SourceUuid + ".mp3",
                    id = source.SourceId,
                }
            );
            await c.ExecuteAsync(
                "UPDATE sources SET artist_id=@artistId WHERE id=@id",
                new { artistId, id = source.SourceId }
            );
        });

    private async Task WithAttemptFailure(Guid attemptUuid, string sqlState, Func<Task> body)
    {
        var name = "page_test_" + Guid.NewGuid().ToString("N");
        // A real database error crosses DbService's exception wrapper. The trigger is scoped to
        // one planned attempt and always removed, including assertion and cancellation failures.
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                $"""
                CREATE FUNCTION normalization.{name}() RETURNS trigger LANGUAGE plpgsql AS $body$
                BEGIN
                    IF NEW.attempt_uuid='{attemptUuid:D}'::uuid THEN
                        RAISE EXCEPTION 'unretained provider detail' USING ERRCODE='{sqlState}';
                    END IF;
                    RETURN NEW;
                END $body$;
                CREATE TRIGGER {name} BEFORE INSERT ON normalization.recording_processing_attempts
                    FOR EACH ROW EXECUTE FUNCTION normalization.{name}();
                """
            )
        );
        try
        {
            await body();
        }
        finally
        {
            await fixture.Db.WithWriteConnection(c =>
                c.ExecuteAsync(
                    $"DROP TRIGGER {name} ON normalization.recording_processing_attempts; DROP FUNCTION normalization.{name}();"
                )
            );
        }
    }

    private async Task<Guid[]> Fetches(NormalizationRecordingPage page)
    {
        var fetches = new List<Guid>();
        foreach (var item in page.Items)
        {
            fetches.AddRange(await Fetches(item.SourceUuid));
        }
        return fetches.ToArray();
    }

    private async Task<Guid[]> Fetches(Guid sourceUuid) =>
        (
            await evidence.GetFetchHistoryAsync(
                new("legacy_catalog", "recording", sourceUuid.ToString("D"))
            )
        )
            .Select(fetch => fetch.FetchUuid)
            .ToArray();

    private Task<long> AcceptedRowCount() =>
        fixture.Db.WithWriteConnection(c =>
            c.ExecuteScalarAsync<long>(
                """
                SELECT (SELECT count(*) FROM normalization.people)+(SELECT count(*) FROM normalization.recording_credits)
                    +(SELECT count(*) FROM normalization.canonical_venues)+(SELECT count(*) FROM normalization.repertoire_songs)
                    +(SELECT count(*) FROM normalization.compositions)+(SELECT count(*) FROM normalization.song_occurrences)
                    +(SELECT count(*) FROM normalization.track_contents)
                """
            )
        );

    private async Task RequireBlockedBy(int pid)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(5))
        {
            if (
                await fixture.Db.WithWriteConnection(c =>
                    c.ExecuteScalarAsync<bool>(
                        "SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE @pid=ANY(pg_blocking_pids(pid)))",
                        new { pid }
                    )
                )
            )
            {
                return;
            }
            await Task.Delay(10);
        }
        Assert.Fail("The expected capture lock wait was not observed.");
    }

    private sealed record ArtistSources(Guid Uuid, IReadOnlyList<CatalogSource> Sources);
}
