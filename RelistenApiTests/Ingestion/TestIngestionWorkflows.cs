using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Relisten.Api.Models;
using Relisten.Ingestion;
using Relisten.Ingestion.Providers.Archive;
using Relisten.Ingestion.Providers.PhantasyTour;
using Relisten.Ingestion.Providers.PhishIn;
using Relisten.Ingestion.Workflows;
using Relisten.Normalization;
using Relisten.Normalization.Songs;
using Relisten.Normalization.Temporal;
using Relisten.Search;
using Relisten.Vendor.ArchiveOrg;
using Temporalio.Activities;
using Temporalio.Client;
using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Testing;
using Temporalio.Worker;

namespace RelistenApiTests.Ingestion;

/// <summary>Runs production workflows against a real Temporal server with deterministic provider boundaries.</summary>
[TestFixture, NonParallelizable]
public class TestIngestionWorkflows
{
    private WorkflowEnvironment environment = null!;

    [OneTimeSetUp]
    public async Task StartServer() => environment = await WorkflowEnvironment.StartLocalAsync();

    [OneTimeTearDown]
    public async Task StopServer() => await environment.DisposeAsync();

    [Test]
    public async Task FullImportRetainsSuccessfulItemsAndReportsPartialFailure()
    {
        var activities = new RecordingActivities(4) { FailedIndex = 1, SkippedIndex = 2 };
        var result = await Run(activities);
        result.CompletedItems.Should().Be(2);
        result.SkippedItems.Should().Be(1);
        result.FailedItems.Should().Be(1);
        result.Status.Should().Be(ImportStatus.Partial);
        activities.Applied.Should().BeEquivalentTo([0, 3]);
        activities.Rebuilds.Should().Equal("shows", "years", "cache", "venues");
        activities.DeletionApplications.Should().Be(1);
    }

    [Test]
    public async Task TargetedImportCompletesWhileItsSearchReindexIsStillRunning()
    {
        var artist = new ArtistId(Guid.NewGuid());
        var searchStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var releaseSearch = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var activities = new RecordingActivities(1)
        {
            RefreshSearch = async indexedArtist =>
            {
                // Other workflows in this fixture can leave detached reindexes queued.
                if (indexedArtist != artist.Value)
                {
                    return;
                }

                searchStarted.SetResult();
                await releaseSearch.Task;
                throw new ApplicationFailureException("Search service unavailable");
            },
        };

        await WithWorkers(
            activities,
            async () =>
            {
                var import = await environment.Client.StartWorkflowAsync(
                    (ArchiveItemImportWorkflow workflow) =>
                        workflow.RunAsync(
                            new ImportArchiveItem(artist, new ArchiveItemId("0")),
                            null
                        ),
                    Options()
                );
                var timeout = TimeSpan.FromSeconds(20);
                try
                {
                    await searchStarted.Task.WaitAsync(timeout);
                    var result = await import.GetResultAsync().WaitAsync(timeout);
                    result.Status.Should().Be(ImportStatus.Completed);
                    result.CompletedItems.Should().Be(1);
                }
                finally
                {
                    releaseSearch.TrySetResult();
                }

                await WaitForSearchReindex(await import.FetchHistoryAsync()).WaitAsync(timeout);
                return true;
            }
        );
    }

    [Test]
    public async Task TargetedImportUsesTheSameActivityWithoutDiscoveryOrReconciliation()
    {
        var activities = new RecordingActivities(1);
        var result = await Run(activities, targeted: true);
        result.CompletedItems.Should().Be(1);
        activities.Discoveries.Should().Be(0);
        activities.Verifications.Should().Be(0);
        activities.DeletionApplications.Should().Be(0);
        activities.RequestedItems.Should().Be(1);
        activities.Applied.Should().Equal(0);
        // A one-item import leaves venues to the nightly run.
        activities.Rebuilds.Should().Equal("shows", "years", "cache");
    }

    [Test]
    public async Task EmptySelectionStillRebuildsTheArtist()
    {
        var activities = new RecordingActivities(0);
        var result = await Run(activities);
        result.Status.Should().Be(ImportStatus.Completed);
        result.CompletedItems.Should().Be(0);
        activities.Rebuilds.Should().Equal("shows", "years", "cache", "venues");
    }

    [TestCase("venue")]
    [TestCase("song")]
    public async Task AFailedMatchStillCompletesTheImportAndStartsReindex(string kind)
    {
        var artist = new ArtistId(Guid.NewGuid());
        var reindexed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var activities = new RecordingActivities(1)
        {
            FailVenues = kind == "venue",
            MatchImportedSongs =
                kind == "song"
                    ? _ =>
                        throw new ApplicationFailureException(
                            "Song matcher unavailable",
                            nonRetryable: true
                        )
                    : null,
            RefreshSearch = indexedArtist =>
            {
                if (indexedArtist == artist.Value)
                {
                    reindexed.TrySetResult();
                }

                return Task.CompletedTask;
            },
        };

        (await Run(activities, artistId: artist)).Status.Should().Be(ImportStatus.Completed);
        reindexed.Task.IsCompletedSuccessfully.Should().BeTrue();
    }

    [Test, Category("LocalIngestion")]
    public async Task FullImportMatchesANewFileBeforeSearchReindexes()
    {
        MigratedTestDatabase.RequireLocalDatabase();
        await using var connection = await MigratedTestDatabase.OpenAsync();
        var artist = Guid.NewGuid();
        var run = Guid.NewGuid();
        var track = Guid.NewGuid();
        const string song = "song_000005ab";
        await connection.ExecuteAsync(
            """
            WITH owner AS (
                INSERT INTO artists(name,slug,uuid,updated_at)
                VALUES ('Import song fixture',@slug,@artist,now()) RETURNING id
            )
            INSERT INTO features(artist_id,norm_catalogue) SELECT id,true FROM owner;
            INSERT INTO norm.runs(uuid,author_type,author_ref)
            VALUES (@run,'importer','import-song-fixture');
            INSERT INTO norm.songs(id,artist_uuid,name,slug,kind,author_type,author_ref,run_uuid)
            VALUES (@song,@artist,'Sugaree','sugaree','song','importer','import-song-fixture',@run)
            """,
            new
            {
                artist,
                slug = "import-song-" + artist,
                run,
                song,
            }
        );
        var database = new TestNormalizationDatabase();
        var songs = new SongActivities(
            null!,
            new CatalogueMatch(database),
            new SongCounts(database),
            new NormFlags(database),
            NullLogger<SongActivities>.Instance
        );
        var indexedSong = new TaskCompletionSource<string?>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var activities = new RecordingActivities(1)
        {
            PersistArchiveItem = () =>
                connection.ExecuteAsync(
                    """
                    WITH recording AS (
                        INSERT INTO sources(artist_id,upstream_identifier,display_date,uuid,updated_at,
                                            is_soundboard,is_remaster,has_jamcharts)
                        SELECT id,@upstream,'1977-05-08',gen_random_uuid(),now(),false,false,false
                        FROM artists WHERE uuid=@artist RETURNING id,artist_id
                    )
                    INSERT INTO source_tracks(source_id,track_position,title,slug,mp3_url,updated_at,artist_id,uuid)
                    SELECT id,1,'Sugaree','sugaree','https://example.test/sugaree.mp3',now(),artist_id,@track
                    FROM recording
                    """,
                    new
                    {
                        artist,
                        track,
                        upstream = "import-song-" + artist,
                    }
                ),
            MatchImportedSongs = songs.MatchImportedTracks,
            RefreshSearch = async indexedArtist =>
            {
                if (indexedArtist == artist)
                {
                    indexedSong.TrySetResult(
                        await connection.QuerySingleOrDefaultAsync<string>(
                            "SELECT song_id FROM norm.track_songs WHERE track_uuid=@track AND resolved",
                            new { track }
                        )
                    );
                }
            },
        };

        var result = await Run(activities, artistId: new ArtistId(artist));

        result.Status.Should().Be(ImportStatus.Completed);
        result.CompletedItems.Should().Be(1);
        (await indexedSong.Task.WaitAsync(TimeSpan.FromSeconds(20))).Should().Be(song);
    }

    [TestCase("years")]
    [TestCase("cache")]
    public async Task RebuildRetriesOnlyTheFailedStep(string failedStep)
    {
        var activities = new RecordingActivities(0) { FailRebuildStep = failedStep };
        (await Run(activities)).Status.Should().Be(ImportStatus.Completed);
        activities.Rebuilds.Count(step => step == "shows").Should().Be(1);
        activities
            .Rebuilds.Count(step => step == "years")
            .Should()
            .Be(failedStep == "years" ? 2 : 1);
        activities
            .Rebuilds.Count(step => step == "cache")
            .Should()
            .Be(failedStep == "cache" ? 2 : 1);
    }

    [TestCase(0)]
    [TestCase(13)]
    public async Task ContinueAsNewRetainsSelectionAndResumesAfterTheLastCompletedItem(
        int noMp3Count
    )
    {
        var activities = new RecordingActivities(180) { NoMp3Count = noMp3Count };
        await WithWorkers(
            activities,
            async () =>
            {
                var input = new ProviderRequest<RefreshArchiveArtist>(
                    new(new ArtistId(Guid.NewGuid()), ArchiveDeletionLimit: 10),
                    Execution()
                );
                var handle = await environment.Client.StartWorkflowAsync(
                    (ArchiveWorkflow workflow) => workflow.RunAsync(input, null, null),
                    Options()
                );
                var result = await handle.GetResultAsync();
                result.CompletedItems.Should().Be(180 - noMp3Count);
                result.SkippedItems.Should().Be(noMp3Count);
                activities
                    .Applied.Should()
                    .BeEquivalentTo(Enumerable.Range(noMp3Count, 180 - noMp3Count));
                if (noMp3Count > 10)
                {
                    // Enough evidence must survive rollover to keep this run
                    // above its allowance, regardless of the buffering strategy.
                    activities.VerifiedNoMp3.Should().HaveCountGreaterThan(10);
                }
                activities.Discoveries.Should().Be(1);
                activities.Verifications.Should().Be(1);
                activities.VerifiedDeletionLimit.Should().Be(10);
                activities.DeletionApplications.Should().Be(1);
                var firstHistory = await environment
                    .Client.GetWorkflowHandle(handle.Id, handle.ResultRunId)
                    .FetchHistoryAsync();
                firstHistory
                    .Events.Should()
                    .Contain(e => e.WorkflowExecutionContinuedAsNewEventAttributes != null);
                firstHistory
                    .Events.Count(e =>
                        e.ActivityTaskScheduledEventAttributes?.ActivityType.Name
                        == "Archive.ImportItem"
                    )
                    .Should()
                    .Be(100);
                await new WorkflowReplayer(
                    new WorkflowReplayerOptions().AddWorkflow<ArchiveWorkflow>()
                ).ReplayWorkflowAsync(firstHistory);
                return true;
            }
        );
    }

    [Test]
    public async Task FailedItemDetailsStayBoundedWithoutLosingTheFailureCount()
    {
        var activities = new RecordingActivities(25) { FailEveryItem = true };
        await WithWorkers(
            activities,
            async () =>
            {
                var input = new ProviderRequest<RefreshArchiveArtist>(
                    new(new ArtistId(Guid.NewGuid())),
                    Execution()
                );
                var result = await environment.Client.ExecuteWorkflowAsync(
                    (ArchiveWorkflow workflow) => workflow.RunAsync(input, null, null),
                    Options()
                );
                result.FailedItems.Should().Be(25);
                result.Failures.Should().HaveCount(20);
                result
                    .Failures!.Cast<ItemFailure<ArchiveItemId>>()
                    .Select(failure => failure.Key.Value)
                    .Should()
                    .OnlyHaveUniqueItems();
                result
                    .Failures!.Cast<ItemFailure<ArchiveItemId>>()
                    .Select(failure => int.Parse(failure.Key.Value))
                    .Should()
                    .OnlyContain(index => index >= 0 && index < 25);
                result.CompletedItems.Should().Be(0);
                return true;
            }
        );
    }

    [Test]
    public async Task FilteredArchiveRefreshDoesNotReconcileUnrelatedRecordings()
    {
        var activities = new RecordingActivities(1);
        await WithWorkers(
            activities,
            async () =>
            {
                var input = new ProviderRequest<RefreshArchiveArtist>(
                    new(new ArtistId(Guid.NewGuid()), 1977),
                    Execution()
                );
                var result = await environment.Client.ExecuteWorkflowAsync(
                    (ArchiveWorkflow workflow) => workflow.RunAsync(input, null, null),
                    Options()
                );
                result.CompletedItems.Should().Be(1);
                activities.DiscoveryYear.Should().Be(1977);
                activities.Verifications.Should().Be(0);
                activities.DeletionApplications.Should().Be(0);
                return true;
            }
        );
    }

    [Test]
    public async Task RetryCountsAnItemOnlyAfterItsActivityCompletes()
    {
        var activities = new RecordingActivities(1) { FailFirstFetch = true };
        var result = await Run(activities);
        result.CompletedItems.Should().Be(1);
        result.FailedItems.Should().Be(0);
        activities.Attempts.Should().Equal(0, 0);
        activities.Applied.Should().Equal(0);
    }

    [Test]
    public async Task CancellationWaitsForTheActiveActivityAndPreventsRebuild()
    {
        var activities = new RecordingActivities(2) { HoldFetch = true };
        await WithWorkers(
            activities,
            async () =>
            {
                var request = new RefreshArtist(new ArtistId(Guid.NewGuid()));
                var handle = await environment.Client.StartWorkflowAsync(
                    (ArtistRefreshWorkflow workflow) => workflow.RunAsync(request, null),
                    Options()
                );
                await activities.FetchEntered.Task.WaitAsync(TimeSpan.FromSeconds(20));
                await handle.CancelAsync();
                await activities.FetchCanceled.Task.WaitAsync(TimeSpan.FromSeconds(20));

                // Each active fetch must receive its own cancellation. Releasing
                // siblings after the first acknowledgement would let them apply
                // before their next heartbeat observes the cancellation request.
                Func<Task> finish = async () => await handle.GetResultAsync();
                var failure = await finish.Should().ThrowAsync<WorkflowFailedException>();
                TemporalException
                    .IsCanceledException(failure.Which.InnerException!)
                    .Should()
                    .BeTrue("{0}", failure.Which.ToString());
                activities.Applied.Should().BeEmpty();
                activities.Attempts.Should().OnlyContain(index => index == 0 || index == 1);
                activities.Rebuilds.Should().BeEmpty();
                return true;
            }
        );
    }

    [Test]
    public async Task FailedDiscoveryPreventsItemsAndReconciliationButRebuildsTheCatalog()
    {
        var activities = new RecordingActivities(3) { FailDiscovery = true };
        Func<Task> run = async () => await Run(activities);
        await run.Should().ThrowAsync<WorkflowFailedException>();
        activities.Applied.Should().BeEmpty();
        activities.Verifications.Should().Be(0);
        activities.DeletionApplications.Should().Be(0);
        activities.Rebuilds.Should().Equal("shows", "years", "cache", "venues");
    }

    [Test]
    public async Task FailedPhishInPreparationPreventsListingButRebuildsTheCatalog()
    {
        var activities = new RecordingActivities(1) { Provider = UpstreamProvider.Phishin };
        Func<Task> run = async () => await Run(activities);
        await run.Should().ThrowAsync<WorkflowFailedException>();
        activities.PhishListings.Should().Be(0);
        activities.Applied.Should().BeEmpty();
        activities.Rebuilds.Should().Equal("shows", "years", "cache", "venues");
    }

    [Test]
    public async Task LaterProviderFailureRebuildsCommittedItemsAndPreservesTheFailedOutcome()
    {
        var activities = new RecordingActivities(2)
        {
            Providers = [UpstreamProvider.ArchiveOrg, UpstreamProvider.PhantasyTour],
        };
        using var phantasyWorker = new TemporalWorker(
            environment.Client,
            new TemporalWorkerOptions(
                IngestionQueues.Providers[UpstreamProvider.PhantasyTour]
            ).AddAllActivities(activities)
        );

        await phantasyWorker.ExecuteAsync(() =>
            WithWorkers(
                activities,
                async () =>
                {
                    var handle = await environment.Client.StartWorkflowAsync(
                        (ArtistRefreshWorkflow workflow) =>
                            workflow.RunAsync(
                                new RefreshArtist(new ArtistId(Guid.NewGuid())),
                                null
                            ),
                        Options()
                    );
                    Func<Task> finish = async () => await handle.GetResultAsync();
                    var failure = await finish.Should().ThrowAsync<WorkflowFailedException>();

                    failure
                        .Which.InnerException.Should()
                        .BeOfType<ChildWorkflowFailureException>()
                        .Which.InnerException.Should()
                        .BeOfType<ActivityFailureException>()
                        .Which.InnerException.Should()
                        .BeOfType<ApplicationFailureException>()
                        .Which.ErrorType.Should()
                        .Be("PhantasyTourForbidden");
                    activities.Applied.Should().BeEquivalentTo([0, 1]);
                    activities.Rebuilds.Should().Equal("shows", "years", "cache", "venues");

                    var replayer = new WorkflowReplayer(
                        new WorkflowReplayerOptions().AddWorkflow<ArtistRefreshWorkflow>()
                    );
                    await replayer.ReplayWorkflowAsync(await handle.FetchHistoryAsync());
                    return true;
                }
            )
        );
    }

    [Test]
    public async Task FailedArtistPreparationDoesNotRebuild()
    {
        var activities = new RecordingActivities(1) { FailArtistPreparation = true };
        Func<Task> run = async () => await Run(activities);

        await run.Should().ThrowAsync<WorkflowFailedException>();
        activities.Discoveries.Should().Be(0);
        activities.Rebuilds.Should().BeEmpty();
    }

    [Test]
    public async Task ExpirationInsideAProviderDoesNotRebuild()
    {
        var activities = new RecordingActivities(1)
        {
            FailDiscovery = true,
            DiscoveryFailureType = "ImportExpired",
        };
        Func<Task> run = async () => await Run(activities);

        await run.Should().ThrowAsync<WorkflowFailedException>();
        activities.Applied.Should().BeEmpty();
        activities.Rebuilds.Should().BeEmpty();
    }

    [Test]
    public async Task QueueDelayConsumesTheExecutionLifetime()
    {
        var request = new RefreshArtist(new ArtistId(Guid.NewGuid()));
        var handle = await environment.Client.StartWorkflowAsync(
            (ArtistRefreshWorkflow workflow) => workflow.RunAsync(request, null),
            Options()
        );
        var history = await handle.FetchHistoryAsync();
        var admittedAt = history
            .Events.First(entry => entry.WorkflowExecutionStartedEventAttributes != null)
            .EventTime.ToDateTimeOffset();
        // The workflow is already admitted, but no worker is polling this queue yet.
        await Task.Delay(200);
        var activities = new RecordingActivities(0);
        await WithWorkers(
            activities,
            async () =>
            {
                await handle.GetResultAsync();
                activities
                    .ExecutionDeadline.Should()
                    .Be(admittedAt + IngestionExecution.MaximumDuration);
                return true;
            }
        );
    }

    [Test]
    public async Task ExpiredExecutionFailsBeforeAnyCatalogOrProviderActivity()
    {
        var activities = new RecordingActivities(1);
        await WithWorkers(
            activities,
            async () =>
            {
                var request = new RefreshArtist(new ArtistId(Guid.NewGuid()));
                var execution = new IngestionExecution(
                    Guid.NewGuid().ToString(),
                    DateTimeOffset.UtcNow.AddMinutes(-1)
                );
                Func<Task> run = async () =>
                    await environment.Client.ExecuteWorkflowAsync(
                        (ArtistRefreshWorkflow workflow) => workflow.RunAsync(request, execution),
                        Options()
                    );
                var failure = await run.Should().ThrowAsync<WorkflowFailedException>();
                failure
                    .Which.InnerException.Should()
                    .BeOfType<ApplicationFailureException>()
                    .Which.ErrorType.Should()
                    .Be("ImportExpired");
                activities.Preparations.Should().Be(0);
                activities.Discoveries.Should().Be(0);
                activities.Rebuilds.Should().BeEmpty();
                return true;
            }
        );
    }

    [Test]
    public async Task BatchReplacesCompletedArtistsWhileTheFirstArtistIsStillRunning()
    {
        var activities = new RecordingActivities(1)
        {
            BatchArtists = 23,
            HoldFirstArtist = true,
            FetchDelay = true,
        };
        await WithWorkers(
            activities,
            async () =>
            {
                var handle = await environment.Client.StartWorkflowAsync(
                    (RefreshBatchWorkflow workflow) =>
                        workflow.RunAsync(new RefreshCatalog(), null, null),
                    Options()
                );
                await activities.EleventhArtistPrepared.Task.WaitAsync(TimeSpan.FromSeconds(30));
                activities.ReleaseFetch.TrySetResult();
                (await handle.GetResultAsync()).Should().Be(new ImportBatchResult(23, 0, 0, 0));
                activities.MaxActiveFetches.Should().BeInRange(2, 10);
                activities.Selections.Should().Be(1);
                await new WorkflowReplayer(
                    new WorkflowReplayerOptions().AddWorkflow<RefreshBatchWorkflow>()
                ).ReplayWorkflowAsync(await handle.FetchHistoryAsync());
                return true;
            }
        );
    }

    [Test]
    public async Task BatchCountsConflictSeparatelyFromFailureAndDoesNotCancelTheOwner()
    {
        var activities = new RecordingActivities(1) { BatchArtists = 2, HoldFirstArtist = true };
        await WithWorkers(
            activities,
            async () =>
            {
                var owner = await environment.Client.StartWorkflowAsync(
                    (ArtistRefreshWorkflow workflow) =>
                        workflow.RunAsync(new RefreshArtist(activities.Artists[0]), null),
                    new WorkflowOptions(
                        "ingest/artist/" + activities.Artists[0],
                        IngestionQueues.Workflows
                    )
                );
                await activities.FetchEntered.Task.WaitAsync(TimeSpan.FromSeconds(20));
                var result = await environment.Client.ExecuteWorkflowAsync(
                    (RefreshBatchWorkflow workflow) =>
                        workflow.RunAsync(new RefreshCatalog(), null, null),
                    Options()
                );
                result.Should().Be(new ImportBatchResult(1, 0, 0, 1));
                owner.GetResultAsync().IsCompleted.Should().BeFalse();
                activities.ReleaseFetch.TrySetResult();
                (await owner.GetResultAsync()).Status.Should().Be(ImportStatus.Completed);
                return true;
            }
        );
    }

    [Test]
    public async Task WorkerRestartResumesAPendingProviderActivity()
    {
        var request = new RefreshArtist(new ArtistId(Guid.NewGuid()));
        var handle = await environment.Client.StartWorkflowAsync(
            (ArtistRefreshWorkflow workflow) => workflow.RunAsync(request, null),
            Options()
        );
        var first = new RecordingActivities(2);
        using (var workflowWorker = WorkflowWorker())
        using (
            var databaseWorker = new TemporalWorker(
                environment.Client,
                new TemporalWorkerOptions(IngestionQueues.Database).AddAllActivities(first)
            )
        )
        {
            await workflowWorker.ExecuteAsync(() =>
                databaseWorker.ExecuteAsync(async () =>
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                    while (
                        !(await handle.FetchHistoryAsync()).Events.Any(e =>
                            e.ChildWorkflowExecutionStartedEventAttributes != null
                        )
                    )
                        await Task.Delay(25, timeout.Token);
                })
            );
        }
        var restarted = new RecordingActivities(2);
        await WithWorkers(
            restarted,
            async () =>
            {
                (await handle.GetResultAsync()).CompletedItems.Should().Be(2);
                restarted.Preparations.Should().Be(0);
                restarted.Discoveries.Should().Be(1);
                restarted.Applied.Should().BeEquivalentTo([0, 1]);
                return true;
            }
        );
    }

    private async Task<ArtistImportResult> Run(
        RecordingActivities activities,
        bool targeted = false,
        ArtistId? artistId = null
    )
    {
        return await WithWorkers(
            activities,
            async () =>
            {
                var artist = artistId ?? new ArtistId(Guid.NewGuid());
                var options = Options();
                var result = targeted
                    ? await environment.Client.ExecuteWorkflowAsync(
                        (ArchiveItemImportWorkflow workflow) =>
                            workflow.RunAsync(
                                new ImportArchiveItem(artist, new ArchiveItemId("0")),
                                null
                            ),
                        options
                    )
                    : await environment.Client.ExecuteWorkflowAsync(
                        (ArtistRefreshWorkflow workflow) =>
                            workflow.RunAsync(new RefreshArtist(artist), null),
                        options
                    );
                var replayer = new WorkflowReplayer(
                    new WorkflowReplayerOptions()
                        .AddWorkflow<ArtistRefreshWorkflow>()
                        .AddWorkflow<ArchiveItemImportWorkflow>()
                );
                var history = await environment
                    .Client.GetWorkflowHandle(options.Id!)
                    .FetchHistoryAsync();
                await replayer.ReplayWorkflowAsync(history);
                // Finish the detached child while this test still owns the activity worker.
                await WaitForSearchReindex(history).WaitAsync(TimeSpan.FromSeconds(20));
                return result;
            }
        );
    }

    private Task WaitForSearchReindex(WorkflowHistory history)
    {
        var child = history
            .Events.Select(e => e.ChildWorkflowExecutionStartedEventAttributes)
            .Single(e => e?.WorkflowType.Name == nameof(ArtistSearchReindexWorkflow));
        return environment
            .Client.GetWorkflowHandle(child.WorkflowExecution.WorkflowId)
            .GetResultAsync();
    }

    private TemporalWorker WorkflowWorker() =>
        new(
            environment.Client,
            new TemporalWorkerOptions(IngestionQueues.Workflows)
                .AddWorkflow<ArtistRefreshWorkflow>()
                .AddWorkflow<ArchiveItemImportWorkflow>()
                .AddWorkflow<ArchiveWorkflow>()
                .AddWorkflow<PhishInWorkflow>()
                .AddWorkflow<PhantasyTourWorkflow>()
                .AddWorkflow<RefreshBatchWorkflow>()
                .AddWorkflow<ArtistSearchReindexWorkflow>()
        );

    private async Task<T> WithWorkers<T>(RecordingActivities activities, Func<Task<T>> run)
    {
        using var workflowWorker = WorkflowWorker();
        using var databaseWorker = new TemporalWorker(
            environment.Client,
            new TemporalWorkerOptions(IngestionQueues.Database).AddAllActivities(activities)
        );
        using var providerWorker = new TemporalWorker(
            environment.Client,
            new TemporalWorkerOptions(
                IngestionQueues.Providers[activities.Provider]
            ).AddAllActivities(activities)
        );
        return await workflowWorker.ExecuteAsync(() =>
            databaseWorker.ExecuteAsync(() => providerWorker.ExecuteAsync(run))
        );
    }

    private static WorkflowOptions Options() =>
        new("test/" + Guid.NewGuid(), IngestionQueues.Workflows);

    private static IngestionExecution Execution() =>
        new(Guid.NewGuid().ToString(), DateTimeOffset.UtcNow.AddHours(1));

    private sealed class TestNormalizationDatabase : INormalizationDatabase
    {
        public NpgsqlConnection CreateWriteConnection() => new(LocalTestDatabase.ConnectionString);
    }

    public class RecordingActivities(int count)
    {
        public UpstreamProvider Provider { get; init; } = UpstreamProvider.ArchiveOrg;
        public UpstreamProvider[]? Providers { get; init; }
        public bool FailArtistPreparation { get; init; }
        public string? DiscoveryFailureType { get; init; }
        public int PhishListings { get; private set; }
        public List<int> Attempts { get; } = [];
        public List<int> Applied { get; } = [];
        public List<string> Rebuilds { get; } = [];
        public Func<Guid, Task>? RefreshSearch { get; init; }
        public Func<Task>? PersistArchiveItem { get; init; }
        public Func<Guid, Task>? MatchImportedSongs { get; init; }

        [Activity(SearchReindexStep.ActivityName)]
        public Task SearchRefresh(Guid artist) =>
            RefreshSearch?.Invoke(artist) ?? Task.CompletedTask;

        [Activity("Songs.MatchImportedTracks")]
        public Task MatchImportedTracks(Guid artist) =>
            MatchImportedSongs?.Invoke(artist) ?? Task.CompletedTask;

        public string? FailRebuildStep { get; init; }
        public ArtistId[] Artists { get; } =
            Enumerable.Range(0, 23).Select(_ => new ArtistId(Guid.NewGuid())).ToArray();
        public int Preparations { get; private set; }
        public DateTimeOffset? ExecutionDeadline { get; private set; }
        public int Discoveries { get; private set; }
        public int Verifications { get; private set; }
        public int VerifiedDeletionLimit { get; private set; }
        public ArchiveItemId[] VerifiedNoMp3 { get; private set; } = [];
        public int NoMp3Count { get; init; }
        public int DeletionApplications { get; private set; }
        public int RequestedItems { get; private set; }
        public int Selections { get; private set; }
        public int? FailedIndex { get; init; }
        public int? SkippedIndex { get; init; }
        public bool FailEveryItem { get; init; }
        public int? DiscoveryYear { get; private set; }
        public bool FailFirstFetch { get; set; }
        public bool FailDiscovery { get; init; }
        public bool HoldFetch { get; init; }
        public bool HoldFirstArtist { get; init; }
        public bool FetchDelay { get; init; }
        public int BatchArtists { get; init; }
        public int MaxActiveFetches { get; private set; }
        private int activeFetches;
        private readonly object sync = new();
        public TaskCompletionSource FetchEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FetchCanceled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFetch { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource EleventhArtistPrepared { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        [Activity("Ingestion.SelectArtists")]
        public ArtistId[] SelectArtists(CatalogBatchCommand request, IngestionExecution execution)
        {
            Selections++;
            return Artists.Take(BatchArtists).ToArray();
        }

        [Activity("Catalog.PrepareArtist")]
        public ArtistImportPlan PrepareArtist(
            ArtistRefreshCommand request,
            IngestionExecution execution
        )
        {
            if (FailArtistPreparation)
            {
                throw new ApplicationFailureException("Artist is missing", nonRetryable: true);
            }

            lock (sync)
            {
                Preparations++;
                ExecutionDeadline = execution.Deadline;
                if (Preparations == 11)
                    EleventhArtistPrepared.TrySetResult();
            }
            return new(request.Artist, Providers ?? [Provider]);
        }

        [Activity("PhantasyTour.ReadShowPage")]
        public PhantasyTourDiscovery ReadPhantasyTourPage(
            ProviderWorkflowContext context,
            PhantasyTourPage? cursor
        ) =>
            throw new ApplicationFailureException(
                "Phantasy Tour returned HTTP 403",
                "PhantasyTourForbidden",
                nonRetryable: true
            );

        [Activity("PhishIn.PrepareCatalogs")]
        public PhishInCatalogs PrepareCatalogsAsync(ProviderWorkflowContext context) =>
            throw new ApplicationFailureException(
                "Required venue catalog unavailable",
                "UpstreamUnavailable",
                nonRetryable: true
            );

        [Activity("PhishIn.ReadShowPage")]
        public Page<DiscoveredItem<PhishInShowId, PhishInShowInput>, PhishInPage> ReadShowPageAsync(
            ProviderRequest<RefreshPhishInArtist> input,
            PhishInPage? page
        )
        {
            PhishListings++;
            return new([], null);
        }

        [Activity("Archive.DiscoverRecordings")]
        public ArchiveSelection DiscoverRecordings(ProviderWorkflowContext context, int? year)
        {
            lock (sync)
            {
                Discoveries++;
                DiscoveryYear = year;
            }
            if (FailDiscovery)
                throw new ApplicationFailureException(
                    "Required upstream listing unavailable",
                    DiscoveryFailureType,
                    nonRetryable: true
                );
            var items = Enumerable
                .Range(0, count)
                .Select(index => new DiscoveredItem<ArchiveItemId, ArchiveSelectedItem>(
                    new(index.ToString()),
                    new(new SearchDoc { identifier = index.ToString() }, "fixture")
                ))
                .ToArray();
            return new(items, items.Select(item => item.Id).ToArray());
        }

        [Activity("Archive.ImportItem")]
        public async Task<ArchiveItemResult> ImportItem(
            ProviderWorkflowContext context,
            ArchiveItemInput input
        )
        {
            var key = input switch
            {
                RequestedArchiveItem requested => requested.Item,
                DiscoveredArchiveItem discovered => discovered.Item.Id,
                _ => throw new InvalidOperationException(),
            };
            var index = int.Parse(key.Value);
            lock (sync)
            {
                if (input is RequestedArchiveItem)
                    RequestedItems++;
                Attempts.Add(index);
                MaxActiveFetches = Math.Max(MaxActiveFetches, ++activeFetches);
            }
            try
            {
                var activity = ActivityExecutionContext.Current;
                using var registration = activity.CancellationToken.Register(() =>
                    FetchCanceled.TrySetResult()
                );
                FetchEntered.TrySetResult();
                if (HoldFetch || (HoldFirstArtist && context.Artist == Artists[0]))
                {
                    while (!ReleaseFetch.Task.IsCompleted)
                    {
                        activity.Heartbeat();
                        await Task.WhenAny(ReleaseFetch.Task, Task.Delay(50));
                        activity.CancellationToken.ThrowIfCancellationRequested();
                    }
                }
                if (FetchDelay)
                    await Task.Delay(100, activity.CancellationToken);
                if (FailFirstFetch)
                {
                    FailFirstFetch = false;
                    throw new ApplicationFailureException("Temporary upstream error");
                }
                if (FailEveryItem || index == FailedIndex)
                    throw new ApplicationFailureException(
                        "Invalid provider metadata",
                        "InvalidMetadata",
                        nonRetryable: true
                    );
                if (index < NoMp3Count)
                {
                    return new(
                        key,
                        ItemDisposition.Skipped,
                        new CatalogSourceId(index + 1),
                        ArchiveSkipReason.NoVbrMp3
                    );
                }
                if (index == SkippedIndex)
                    return new(key, ItemDisposition.Skipped, null, ArchiveSkipReason.DarkItem);
                if (PersistArchiveItem is not null)
                {
                    await PersistArchiveItem();
                }
                lock (sync)
                    Applied.Add(index);
                return new(key, ItemDisposition.Applied, new CatalogSourceId(index + 1));
            }
            finally
            {
                lock (sync)
                    activeFetches--;
            }
        }

        [Activity("Archive.VerifyDeletionCandidates")]
        public ArchiveDeletionPlan VerifyDeletionCandidates(
            ProviderWorkflowContext context,
            ArchiveSelection selection,
            ArchiveItemId[] noMp3
        ) =>
            VerifyDeletionCandidatesWithLimit(
                context,
                selection,
                noMp3,
                RefreshArchiveArtist.DefaultDeletionLimit
            );

        [Activity("Archive.VerifyDeletionCandidatesWithLimit")]
        public ArchiveDeletionPlan VerifyDeletionCandidatesWithLimit(
            ProviderWorkflowContext context,
            ArchiveSelection selection,
            ArchiveItemId[] noMp3,
            int deletionLimit
        )
        {
            lock (sync)
            {
                Verifications++;
                VerifiedDeletionLimit = deletionLimit;
                VerifiedNoMp3 = noMp3;
            }
            return new(ArchiveDeletionDisposition.Allowed, []);
        }

        [Activity("Archive.ApplyVerifiedDeletions")]
        public ArchiveDeletionResult ApplyVerifiedDeletions(
            ProviderWorkflowContext context,
            ArchiveDeletionPlan plan
        )
        {
            lock (sync)
                DeletionApplications++;
            return new(plan.Decisions, []);
        }

        [Activity("Catalog.RebuildShows")]
        public void RebuildShows(ProviderWorkflowContext context) => RebuildStep("shows");

        [Activity("Catalog.RebuildYears")]
        public void RebuildYears(ProviderWorkflowContext context) => RebuildStep("years");

        [Activity("Catalog.InvalidateArtistCache")]
        public void InvalidateArtistCache(ProviderWorkflowContext context) => RebuildStep("cache");

        public bool FailVenues { get; init; }

        [Activity("Venues.MatchArtist")]
        public Relisten.Normalization.Venues.VenueMatchResult? MatchArtist(Guid artist)
        {
            lock (sync)
                Rebuilds.Add("venues");
            if (FailVenues)
                throw new ApplicationFailureException(
                    "Venue catalogue unavailable",
                    nonRetryable: true
                );
            return null;
        }

        private void RebuildStep(string step)
        {
            lock (sync)
            {
                Rebuilds.Add(step);
                if (step == FailRebuildStep && Rebuilds.Count(attempt => attempt == step) == 1)
                    throw new ApplicationFailureException("Temporary rebuild dependency failure");
            }
        }
    }
}
