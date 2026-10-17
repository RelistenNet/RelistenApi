using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Relisten;
using Relisten.Api.Models;
using Relisten.Api.Models.Api;
using Relisten.Controllers;
using Relisten.Data;
using Relisten.Import;
using Relisten.Ingestion;
using Relisten.Ingestion.Providers.TapersSection;
using Relisten.Vendor.ArchiveOrg;
using Relisten.Vendor.ArchiveOrg.Metadata;
using RelistenApiTests.Ingestion;
using Temporalio.Testing;

namespace RelistenApiTests.TapersSection;

/// <summary>
/// Exercises explicit performer review through the registered ingestion workflows and v3
/// listener controllers. Broad provider creators remain evidence rather than project aliases.
/// </summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public class TestReviewedPerformers
{
    private IHost host = null!;
    private WorkflowEnvironment? temporal;
    private FixtureHttpServer upstream = null!;
    private DbService db = null!;
    private string originalWrite = null!;
    private string originalRead = null!;
    private readonly Dictionary<string, JObject> recordings = new();
    private ArchiveIntakeService Intake => host.Services.GetRequiredService<ArchiveIntakeService>();
    private ArchivePerformerReviewService Reviews =>
        host.Services.GetRequiredService<ArchivePerformerReviewService>();
    private ArtistService Artists => host.Services.GetRequiredService<ArtistService>();

    [OneTimeSetUp]
    public async Task Start()
    {
        if (Environment.GetEnvironmentVariable("RELISTEN_LOCAL_INGESTION_TESTS") != "1")
        {
            Assert.Ignore("Run ./test-local.sh to include local integration tests.");
        }
        originalWrite = DbService.ConnStr;
        originalRead = DbService.ReadOnlyConnStr;
        upstream = new FixtureHttpServer();
        temporal = await WorkflowEnvironment.StartLocalAsync();
        var testNamespace = "reviewed-performers-" + Guid.NewGuid().ToString("N");
        await temporal.Client.WorkflowService.RegisterNamespaceAsync(
            new Temporalio.Api.WorkflowService.V1.RegisterNamespaceRequest
            {
                Namespace = testNamespace,
                WorkflowExecutionRetentionPeriod =
                    Google.Protobuf.WellKnownTypes.Duration.FromTimeSpan(TimeSpan.FromDays(1)),
            }
        );
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Temporal:Address"] = temporal.Client.Connection.Options.TargetHost,
                ["Temporal:Namespace"] = testNamespace,
                ["Ingestion:Engine"] = "Temporal",
            }
        );
        builder.Services.AddSingleton(sp => new DbService(
            LocalTestDatabase.DatabaseUrl,
            null,
            null,
            builder.Environment,
            sp.GetRequiredService<ILogger<DbService>>()
        ));
        builder.Services.AddSingleton(_ => new RedisService(
            RedisService.BuildConfiguration("redis://127.0.0.1:16379")
        ));
        builder.Services.AddSingleton<Hangfire.IBackgroundJobClient, IgnoredPopularityJobs>();
        builder
            .Services.AddRelistenCatalogServices()
            .AddTemporalIngestionWorkers(builder.Configuration);
        builder
            .Services.AddHttpClient(nameof(IngestionHttpClientFactory))
            .ConfigurePrimaryHttpMessageHandler(() => new FixtureRedirectHandler(upstream.Address));
        SqlMapper.AddTypeHandler(new PersistentIdentifierHandler());
        SqlMapper.AddTypeHandler(new DateTimeHandler());
        host = builder.Build();
        db = host.Services.GetRequiredService<DbService>();
        await host.StartAsync();
        await db.WithWriteConnection(connection =>
            connection.ExecuteAsync(
                "INSERT INTO upstream_sources(id,name,url) VALUES(1,'archive.org','https://archive.org') ON CONFLICT DO NOTHING"
            )
        );
    }

    [SetUp]
    public async Task ResetReviews()
    {
        await db.WithWriteConnection(connection =>
            connection.ExecuteAsync(
                "TRUNCATE normalization.archive_item_intake,normalization.archive_item_observations,normalization.archive_artist_candidates CASCADE"
            )
        );
        recordings.Clear();
        upstream.BeforeResponse = null;
        upstream.Respond = uri =>
            uri.AbsolutePath.StartsWith("/metadata/")
                ? recordings[Uri.UnescapeDataString(uri.Segments.Last())].ToString()
                : JsonConvert.SerializeObject(
                    new
                    {
                        total = recordings.Count,
                        items = recordings.Keys.Select(identifier => new
                        {
                            identifier,
                            collection = new[] { "taperssection" },
                            mediatype = "audio",
                        }),
                    }
                );
    }

    [OneTimeTearDown]
    public async Task Stop()
    {
        if (host != null)
        {
            await host.StopAsync();
            host.Services.GetRequiredService<RedisService>().connection.Dispose();
            host.Dispose();
        }
        if (temporal != null)
        {
            await temporal.DisposeAsync();
        }
        upstream?.Dispose();
        if (originalWrite != null)
        {
            DbService.ConnStr = originalWrite;
            DbService.ReadOnlyConnStr = originalRead;
        }
    }

    [Test]
    public async Task BroadJerryCreatorPublishesDistinctReviewedProjectsAndRepeatPreservesV3Identities()
    {
        var jgb = await ProposeProject("Jerry Garcia Band");
        var legion = await ProposeProject("Legion of Mary");
        var jgbItem = AddRecording(
            "Jerry Garcia Band Live at Fixture Hall on 1975-10-17",
            "1975-10-17"
        );
        var legionItem = AddRecording(
            "Jerry Garcia Live at Fixture Hall on 1975-01-18",
            "1975-01-18",
            "Legion of Mary performed this concert."
        );
        await Discover();
        var jgbReview = await MakeReview(jgbItem, jgb, "metadata.title");
        var legionReview = await MakeReview(legionItem, legion, "metadata.description");
        var accepted = await Reviews.ReviewAsync(jgbReview);
        (await Reviews.ReviewAsync(jgbReview)).Revision.Should().Be(accepted.Revision);
        await Reviews.ReviewAsync(legionReview);

        var result = await Refresh();
        result.Published.Should().Be(2);
        result.Failed.Should().Be(0);
        var jgbBefore = await ListenerRecording(jgb.Artist, "1975-10-17");
        var legionBefore = await ListenerRecording(legion.Artist, "1975-01-18");
        jgbBefore.Source.Should().NotBe(legionBefore.Source);
        jgbBefore.Artist.Should().Be(jgb.Artist.uuid);
        legionBefore.Artist.Should().Be(legion.Artist.uuid);
        (await DefaultArtistList())
            .Should()
            .Contain(row => row.uuid == jgb.Artist.uuid)
            .And.Contain(row => row.uuid == legion.Artist.uuid);
        (await Artists.All()).Should().NotContain(artist => artist.name == "Jerry Garcia");
        var retainedReview = (await Reviews.FindPerformerDecisionAsync(jgbItem))!;
        JObject.Parse(retainedReview.SupportingPayloadJson)["metadata"]!["creator"]!
            .Value<string>()
            .Should()
            .Be("Jerry Garcia");

        var repeated = await Refresh();
        repeated.Failed.Should().Be(0);
        repeated.Unchanged.Should().Be(2);
        (await ListenerRecording(jgb.Artist, "1975-10-17")).Should().BeEquivalentTo(jgbBefore);
        (await ListenerRecording(legion.Artist, "1975-01-18"))
            .Should()
            .BeEquivalentTo(legionBefore);
        var normalized = await NormalizedArtist(legion.Artist.uuid);
        var show = normalized["shows"]!.Should().ContainSingle().Which;
        Guid.Parse(show["artist_uuid"]!.Value<string>()!).Should().Be(legion.Artist.uuid);
    }

    [Test]
    public async Task PerformerApprovalIsSeparateFromArtistApprovalAndRevocationKeepsLastPlayback()
    {
        var project = await ProposeProject();
        var item = AddRecording(project.Name + " Live at Fixture Hall", "1975-10-17");
        await Discover();
        var review = await MakeReview(item, project, "metadata.title");
        await Reviews.ReviewAsync(review with { Approved = false });
        (await Refresh()).Failed.Should().Be(0);
        (
            await host
                .Services.GetRequiredService<SourceService>()
                .AllForArtistFromPrimary(project.Artist)
        )
            .Should()
            .BeEmpty();
        await Reviews.SetApprovalAsync(item, true, "Reviewed project and original billing");
        (await Refresh()).Published.Should().Be(1);
        var before = await ListenerRecording(project.Artist, "1975-10-17");
        var accepted = (await Reviews.FindPerformerDecisionAsync(item))!;

        var revoked = await Reviews.SetApprovalAsync(item, false, "Pause new updates for review");
        revoked.Revision.Should().BeGreaterThan(accepted.Revision);
        revoked.CandidateId.Should().Be(accepted.CandidateId);
        revoked.SupportingObservationId.Should().Be(accepted.SupportingObservationId);
        revoked.Evidence.Should().BeEquivalentTo(accepted.Evidence);
        recordings[item]["files"]![0]!["title"] = "Must not replace the published track";
        var held = await Refresh();
        held.Failed.Should().Be(0);
        held.Published.Should().Be(0);
        (await ListenerRecording(project.Artist, "1975-10-17")).Should().BeEquivalentTo(before);
    }

    [Test]
    public async Task ChangedSupportingContextHoldsUpdatesEvenWhenTheOriginalProjectSpanStillMatches()
    {
        var project = await ProposeProject();
        var item = AddRecording(project.Name + " Live at Fixture Hall", "1975-10-17");
        await Discover();
        await Reviews.ReviewAsync(await MakeReview(item, project, "metadata.title"));
        (await Refresh()).Published.Should().Be(1);
        var before = await ListenerRecording(project.Artist, "1975-10-17");
        recordings[item]["metadata"]!["title"] =
            recordings[item]["metadata"]!["title"]!.Value<string>()
            + " with changed billing context";
        recordings[item]["files"]![0]!["title"] = "Held metadata update";
        var result = await Refresh();
        result.NeedsReview.Should().Be(1);
        result.Published.Should().Be(0);
        var status = await ItemStatus(item);
        status["reason"]!.Value<string>().Should().Be("performer_review_required");
        (await ListenerRecording(project.Artist, "1975-10-17")).Should().BeEquivalentTo(before);
        (await Reviews.FindPerformerDecisionAsync(item))!.Approved.Should().BeTrue();
    }

    [Test]
    public async Task UnrelatedFilesReviewsAndStructuredDateChangesPreserveReviewedPerformer()
    {
        var project = await ProposeProject();
        var item = AddRecording(
            "Jerry Garcia Live at Fixture Hall",
            "1975-10-17",
            project.Name + " performed this concert."
        );
        await Discover();
        await Reviews.ReviewAsync(await MakeReview(item, project, "metadata.description"));
        (await Refresh()).Published.Should().Be(1);
        var before = await ListenerRecording(project.Artist, "1975-10-17");
        var accepted = (await Reviews.FindPerformerDecisionAsync(item))!;
        recordings[item]["metadata"]!["date"] = "1975-10-18";
        recordings[item]["files"]![0]!["title"] = "Updated first song";
        recordings[item]["reviews"] = new JArray(
            new JObject
            {
                ["stars"] = 5,
                ["reviewtitle"] = "Listener review",
                ["reviewbody"] = "Updated independently",
                ["reviewer"] = "Fixture listener",
                ["reviewdate"] = "2026-10-17T00:00:00Z",
            }
        );
        var result = await Refresh();
        result.Failed.Should().Be(0);
        result.Published.Should().Be(1);
        var current = await ListenerRecording(project.Artist, "1975-10-18", "Updated first song");
        current.Artist.Should().Be(before.Artist);
        current.Source.Should().Be(before.Source);
        current.Sets.Should().Equal(before.Sets);
        current.Tracks.Should().Equal(before.Tracks);
        current.Show.Should().Be(before.Show);
        var decision = (await Reviews.FindPerformerDecisionAsync(item))!;
        decision.SupportingObservationId.Should().Be(accepted.SupportingObservationId);
        decision.CandidateId.Should().Be(accepted.CandidateId);
        decision.Approved.Should().BeTrue();
        var source = (
            await host
                .Services.GetRequiredService<SourceService>()
                .AllForArtistFromPrimary(project.Artist)
        ).Single();
        source.display_date.Should().Be("1975-10-18");
        (await host.Services.GetRequiredService<SourceReviewService>().AllForSources([source.id]))
            .Should()
            .ContainSingle()
            .Which.review.Should()
            .Be("Updated independently");
    }

    [Test]
    public async Task ApprovedPerformerReviewDoesNotWaiveCompilationChecksOrPermitPublishedRetargeting()
    {
        var project = await ProposeProject();
        var compilation = AddRecording("Compilation: " + project.Name, "1975-10-17");
        var ordinary = AddRecording(project.Name + " Live at Fixture Hall", "1975-10-18");
        await Discover();
        await Reviews.ReviewAsync(await MakeReview(compilation, project, "metadata.title"));
        await Reviews.ReviewAsync(await MakeReview(ordinary, project, "metadata.title"));
        var result = await Refresh();
        result.Published.Should().Be(1);
        result.NeedsReview.Should().Be(1);
        (await ItemStatus(compilation))["reason"]!.Value<string>().Should().Be("compilation");
        var before = await ListenerRecording(project.Artist, "1975-10-18");
        var differentProject = await ProposeProject();
        var retarget = await MakeReview(ordinary, project, "metadata.title");
        Func<Task> replacePublishedPerformer = () =>
            Reviews.ReviewAsync(retarget with { CandidateId = differentProject.Candidate });
        await replacePublishedPerformer.Should().ThrowAsync<ArgumentException>();
        (await Reviews.FindPerformerDecisionAsync(ordinary))!
            .CandidateId.Should()
            .Be(project.Candidate);
        (await ListenerRecording(project.Artist, "1975-10-18")).Should().BeEquivalentTo(before);
    }

    [Test]
    public async Task PerformerReviewCannotRetargetAnAlreadyAcceptedCanonicalSource()
    {
        var original = await ProposeProject();
        var different = await ProposeProject();
        var item = AddRecording(different.Name + " Live at Fixture Hall", "1975-10-17");
        // Collection review cannot replace a canonical native performer even when newly
        // retained provider billing names another project.
        var source = await WriteNativeRecording(original, item, "1975-10-17");
        var resolutions = host.Services.GetRequiredService<ArchiveItemResolutionService>();
        (await resolutions.ResolveExistingAsync(new(item)))
            .canonical_source_uuid.Should()
            .Be(source.uuid);
        var review = await MakeReview(item, different, "metadata.title");
        Func<Task> retarget = () => Reviews.ReviewAsync(review);
        await retarget.Should().ThrowAsync<ArgumentException>();
        var retained = (await resolutions.FindAsync(item))!;
        retained.canonical_source_uuid.Should().Be(source.uuid);
        retained.selected_artist_uuid.Should().Be(original.Artist.uuid);
        (await Reviews.FindPerformerDecisionAsync(item)).Should().BeNull();
    }

    [Test]
    public async Task ReviewedNativeSourceRemainsResolverOnlyAndItsNativeImporterCanStillUpdateIt()
    {
        var project = await ProposeProject();
        var item = AddRecording(project.Name + " Live at Fixture Hall", "1975-10-17");
        recordings[item]["metadata"]!["creator"] = project.Name;
        var source = await WriteNativeRecording(project, item, "1975-10-17");
        var before = await ListenerRecording(project.Artist, "1975-10-17");
        await Discover();
        await Reviews.ReviewAsync(await MakeReview(item, project, "metadata.title"));
        var projection = ArchiveObservationProjector.Project(
            new ArchiveItemId(item),
            recordings[item].ToString()
        );
        var linked = await Intake.ApplyObservationAsync(
            new(new ArchiveItemId(item), project.Candidate),
            projection,
            recordings[item].ToString()
        );
        linked.Outcome.Should().Be(TapersSectionItemOutcome.Unchanged);
        linked.SourceUuid.Should().Be(source.uuid);
        var reobserved = await Intake.RecordObservationAsync(projection);
        reobserved.Outcome.Should().Be(TapersSectionItemOutcome.Unchanged);
        (await Intake.PendingObservationsAsync()).Should().NotContain(new ArchiveItemId(item));
        var receipt = await ItemStatus(item);
        receipt["selected_display_date"]!.Type.Should().Be(JTokenType.Null);
        receipt["date_details"]!.Type.Should().Be(JTokenType.Null);
        (await Discover())
            .Observed.Should()
            .Be(0, "native reuse settles the prior discovery receipt");
        var refreshed = await Refresh();
        refreshed.Unchanged.Should().Be(1);
        refreshed.Published.Should().Be(0);
        var transaction = host.Services.GetRequiredService<CatalogImportTransaction>();
        (
            await transaction.RunAsync(
                DateTimeOffset.UtcNow.AddMinutes(1),
                CancellationToken.None,
                () =>
                    host
                        .Services.GetRequiredService<SourceService>()
                        .IsArchiveItemIntakeOwnedForUpdate(item)
            )
        )
            .Should()
            .BeFalse();

        await db.WithWriteConnection(connection =>
            connection.ExecuteAsync(
                "INSERT INTO artists_upstream_sources(artist_id,upstream_source_id,upstream_identifier) VALUES(@id,1,@collection)",
                new
                {
                    id = project.Artist.id,
                    collection = "native-" + Guid.NewGuid().ToString("N"),
                }
            )
        );
        var artist = (await Artists.FindArtistForImport(project.Artist.uuid.ToString()))!;
        recordings[item]["files"]![0]!["title"] = "Native owner changed title";
        upstream.Respond = uri =>
            uri.AbsolutePath.StartsWith("/metadata/")
                ? recordings[item].ToString()
                : JsonConvert.SerializeObject(
                    new
                    {
                        responseHeader = new { status = 0 },
                        response = new
                        {
                            numFound = 1,
                            start = 0,
                            docs = new[]
                            {
                                new
                                {
                                    identifier = item,
                                    date = "1975-10-17",
                                    addeddate = DateTime.UtcNow,
                                },
                            },
                        },
                    }
                );
        using var services = host.Services.CreateScope();
        var importer = services.ServiceProvider.GetRequiredService<ArchiveOrgImporter>();
        importer.UseIngestionHttpClient(
            new HttpClient(new FixtureRedirectHandler(upstream.Address))
        );
        var result = await importer.ImportSpecificShowDataForArtist(
            artist,
            artist.upstream_sources.Single(),
            item,
            null
        );
        result.Updated.Should().Be(1);
        (await ListenerRecording(artist, "1975-10-17", "Native owner changed title"))
            .Should()
            .BeEquivalentTo(before);
    }

    [Test]
    public async Task ApprovedReviewWithUnresolvedProjectAlternativesStaysHeld()
    {
        var project = await ProposeProject();
        var alternative = await ProposeProject();
        var item = AddRecording(
            "Jerry Garcia Live at Fixture Hall",
            "1987-12-31",
            project.Name + " is named in disputed billing."
        );
        await Discover();
        var review = await MakeReview(item, project, "metadata.description");
        await Reviews.ReviewAsync(review with { Alternatives = [alternative.Name] });
        var result = await Refresh();
        result.Published.Should().Be(0);
        result.NeedsReview.Should().Be(1);
        (
            await host
                .Services.GetRequiredService<SourceService>()
                .AllForArtistFromPrimary(project.Artist)
        )
            .Should()
            .BeEmpty();
        (await Reviews.FindPerformerDecisionAsync(item))!
            .Alternatives.Should()
            .Equal(alternative.Name);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ApprovedTitleReviewDoesNotWaiveMissingOrMalformedCreator(
        bool malformedCreator
    )
    {
        var project = await ProposeProject();
        var item = AddRecording(project.Name + " Live at Fixture Hall", "1975-10-17");
        if (malformedCreator)
        {
            recordings[item]["metadata"]!["creator"] = new JObject
            {
                ["unsupported"] = "Jerry Garcia",
            };
        }
        else
        {
            ((JObject)recordings[item]["metadata"]!).Remove("creator");
        }
        await Reviews.ReviewAsync(await MakeReview(item, project, "metadata.title"));
        var result = await Refresh();
        result.Published.Should().Be(0);
        result.NeedsReview.Should().Be(1);
        result.Failed.Should().Be(0);
        (
            await host
                .Services.GetRequiredService<SourceService>()
                .AllForArtistFromPrimary(project.Artist)
        )
            .Should()
            .BeEmpty();
    }

    [Test]
    public async Task ReviewValidationRejectsAnotherItemObservationUnsupportedFieldsAndIncorrectSpans()
    {
        var project = await ProposeProject();
        var item = AddRecording(project.Name + " Live at Fixture Hall", "1975-10-17");
        var another = AddRecording(project.Name + " Live at Another Hall", "1975-10-18");
        var valid = await MakeReview(item, project, "metadata.title");
        var anotherReview = await MakeReview(another, project, "metadata.title");
        Func<Task> wrongItem = () =>
            Reviews.ReviewAsync(
                valid with
                {
                    SupportingObservationId = anotherReview.SupportingObservationId,
                }
            );
        Func<Task> dateAsPerformer = () =>
            Reviews.ReviewAsync(
                valid with
                {
                    Evidence = [new("metadata.date", "1975-10-17", "date_is_not_a_project", 0, 10)],
                }
            );
        Func<Task> broadCreator = () =>
            Reviews.ReviewAsync(
                valid with
                {
                    Evidence =
                    [
                        new(
                            "metadata.creator",
                            "Jerry Garcia",
                            "broad_creator_is_not_a_project",
                            0,
                            12
                        ),
                    ],
                }
            );
        Func<Task> wrongSpan = () =>
            Reviews.ReviewAsync(
                valid with
                {
                    Evidence = [valid.Evidence[0] with { Start = valid.Evidence[0].Start + 1 }],
                }
            );
        await wrongItem.Should().ThrowAsync<ArgumentException>();
        await dateAsPerformer.Should().ThrowAsync<ArgumentException>();
        await wrongSpan.Should().ThrowAsync<ArgumentException>();
        await broadCreator.Should().ThrowAsync<ArgumentException>();
        (await Reviews.FindPerformerDecisionAsync(item)).Should().BeNull();
        var accepted = await Reviews.ReviewAsync(valid);
        accepted.Evidence.Should().BeEquivalentTo(valid.Evidence);
        (await Reviews.ReviewAsync(valid)).Revision.Should().Be(accepted.Revision);
    }

    [Test]
    public async Task ExistingArtistApprovalPublishesOnlyMissingItemsAndKeepsNativeOwnershipAndFlags()
    {
        var project = await ExistingProject();
        var native = AddRecording(project.Name + " Live at Fixture Hall", "1975-10-17");
        var missing = AddRecording(project.Name + " Live at Fixture Hall", "1975-10-18");
        recordings[native]["metadata"]!["creator"] = project.Name;
        recordings[missing]["metadata"]!["creator"] = project.Name;
        var nativeSource = await WriteNativeRecording(project, native, "1975-10-17");
        var nativeBefore = await ListenerRecording(project.Artist, "1975-10-17");
        await Discover();
        (await Refresh())
            .Published.Should()
            .Be(0, "bulk approval alone cannot adopt an existing artist");

        await Intake.BindExistingArtistAsync(
            project.Candidate,
            new(project.Artist.uuid),
            "Reviewed exact existing artist"
        );
        var result = await Refresh();
        result.Failed.Should().Be(0);
        result.Published.Should().Be(1);
        result.Unchanged.Should().Be(1);
        (await ListenerRecording(project.Artist, "1975-10-17"))
            .Should()
            .BeEquivalentTo(nativeBefore);
        var published = await ListenerRecording(project.Artist, "1975-10-18");
        var sourceService = host.Services.GetRequiredService<SourceService>();
        var transaction = host.Services.GetRequiredService<CatalogImportTransaction>();
        (
            await transaction.RunAsync(
                DateTimeOffset.UtcNow.AddMinutes(1),
                CancellationToken.None,
                () => sourceService.IsArchiveItemIntakeOwnedForUpdate(native)
            )
        )
            .Should()
            .BeFalse();
        (
            await transaction.RunAsync(
                DateTimeOffset.UtcNow.AddMinutes(1),
                CancellationToken.None,
                () => sourceService.IsArchiveItemIntakeOwnedForUpdate(missing)
            )
        )
            .Should()
            .BeTrue();
        (await sourceService.ArchiveDeletionCandidatesFromPrimary(project.Artist))
            .Select(source => source.uuid)
            .Should()
            .Contain(nativeSource.uuid)
            .And.NotContain(published.Source);
        var after = (await Artists.FindArtistForImport(project.Artist.uuid.ToString()))!;
        after
            .featured.Should()
            .Be(project.Artist.featured, "intake must not reveal or reclassify a native artist");
        after.upstream_sources.Should().BeEmpty();
        var registry = new ArtistRegistryService(db);
        (await registry.HasCohortIdentityConflictAsync(project.Name)).Should().BeFalse();
        await registry.EnsureArtistWriteAllowedAsync(
            new SlimArtist
            {
                id = after.id,
                name = after.name + " renamed",
                slug = after.slug,
            }
        );
        var accepted = (await new ArchiveItemResolutionService(db).FindAsync(missing))!;
        accepted.canonical_source_uuid.Should().Be(published.Source);

        var collections = host.Services.GetRequiredService<CollectionService>();
        var collection = Guid.NewGuid();
        await collections.SaveCollectionAsync(
            new(collection, "existing-artist-" + collection, "Existing artist", "", true)
        );
        await collections.SetArtistMembershipAsync(
            collection,
            project.Artist.uuid,
            true,
            "Reviewed artist selection"
        );
        (await collections.RecordingsAsync(collection))!
            .items.Select(item => item.source_uuid)
            .Should()
            .BeEquivalentTo(new[] { nativeSource.uuid, published.Source });

        (await Refresh()).Failed.Should().Be(0);
        (await ListenerRecording(project.Artist, "1975-10-18")).Should().BeEquivalentTo(published);
        recordings[missing]["metadata"]!["creator"] = "Conflicting performer";
        (await Refresh()).NeedsReview.Should().Be(1);
        (await ListenerRecording(project.Artist, "1975-10-18")).Should().BeEquivalentTo(published);
    }

    [Test]
    public async Task ConcurrentNativeFirstWriteIsReusedWithoutTakingItsRecordingOwnership()
    {
        var project = await ExistingProject();
        await Intake.BindExistingArtistAsync(
            project.Candidate,
            new(project.Artist.uuid),
            "Reviewed existing performer"
        );
        var item = AddRecording(project.Name + " Live at Fixture Hall", "1975-10-17");
        recordings[item]["metadata"]!["creator"] = project.Name;
        await Discover();
        var raw = recordings[item].ToString();
        var metadata = JsonConvert.DeserializeObject<RootObject>(
            raw,
            new TolerantStringConverter()
        )!;
        var nativeHasItemLock = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var releaseNative = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var nativeWrite = Task.Run(async () =>
        {
            using var services = host.Services.CreateScope();
            var transaction =
                services.ServiceProvider.GetRequiredService<CatalogImportTransaction>();
            var sources = services.ServiceProvider.GetRequiredService<SourceService>();
            var writer = services.ServiceProvider.GetRequiredService<ArchiveOrgImporter>();
            return await transaction.RunAsync(
                DateTimeOffset.UtcNow.AddSeconds(30),
                CancellationToken.None,
                async () =>
                {
                    (await sources.IsArchiveItemIntakeOwnedForUpdate(item)).Should().BeFalse();
                    var pid = await db.WithWriteConnection(connection =>
                        connection.QuerySingleAsync<int>("SELECT pg_backend_pid()")
                    );
                    nativeHasItemLock.SetResult(pid);
                    await releaseNative.Task.WaitAsync(TimeSpan.FromSeconds(20));
                    return await writer.WriteResolvedRecordingAsync(
                        project.Artist,
                        1,
                        new SearchDoc { identifier = item, addeddate = DateTime.UtcNow },
                        metadata,
                        "1975-10-17"
                    );
                }
            );
        });
        var nativePid = await nativeHasItemLock.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var intakeWrite = Task.Run(async () =>
        {
            using var services = host.Services.CreateScope();
            return await services
                .ServiceProvider.GetRequiredService<ArchiveIntakeService>()
                .ApplyObservationAsync(
                    new(new(item), project.Candidate),
                    ArchiveObservationProjector.Project(new(item), raw),
                    raw
                );
        });
        try
        {
            // Observe the actual PostgreSQL waiter instead of relying on thread timing.
            var blocked = false;
            var stop = DateTimeOffset.UtcNow.AddSeconds(10);
            while (!blocked && DateTimeOffset.UtcNow < stop)
            {
                blocked = await db.WithWriteConnection(connection =>
                    connection.QuerySingleAsync<bool>(
                        "SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE datname=current_database() AND @nativePid=ANY(pg_blocking_pids(pid)))",
                        new { nativePid }
                    )
                );
                if (!blocked)
                {
                    await Task.Delay(20);
                }
            }
            blocked.Should().BeTrue("intake must wait for the native writer's retained item lock");
        }
        finally
        {
            releaseNative.SetResult();
        }
        var native = await nativeWrite;
        var reused = await intakeWrite;
        reused.Outcome.Should().Be(TapersSectionItemOutcome.Unchanged);
        reused.SourceUuid.Should().Be(native.uuid);
        using var verification = host.Services.CreateScope();
        var sourceService = verification.ServiceProvider.GetRequiredService<SourceService>();
        (await sourceService.AllForArtistFromPrimary(project.Artist))
            .Should()
            .ContainSingle()
            .Which.uuid.Should()
            .Be(native.uuid);
        (
            await verification
                .ServiceProvider.GetRequiredService<CatalogImportTransaction>()
                .RunAsync(
                    DateTimeOffset.UtcNow.AddMinutes(1),
                    CancellationToken.None,
                    () => sourceService.IsArchiveItemIntakeOwnedForUpdate(item)
                )
        )
            .Should()
            .BeFalse();
    }

    [Test]
    public async Task ReviewedBroadCreatorCanPublishAMissingRecordingForAnExistingProject()
    {
        var project = await ExistingProject();
        var item = AddRecording(project.Name + " Live at Fixture Hall", "1975-10-17");
        await Reviews.ReviewAsync(await MakeReview(item, project, "metadata.title"));
        await Intake.BindExistingArtistAsync(
            project.Candidate,
            new(project.Artist.uuid),
            "Reviewed existing project identity"
        );
        var result = await Refresh();
        result.Published.Should().Be(1);
        result.Failed.Should().Be(0);
        var before = await ListenerRecording(project.Artist, "1975-10-17");
        (await Reviews.FindPerformerDecisionAsync(item))!
            .SelectedArtistUuid.Should()
            .Be(project.Artist.uuid);
        await Reviews.SetApprovalAsync(item, false, "Pause this recording for review");
        recordings[item]["files"]![0]!["title"] = "Must remain held";
        (await Refresh()).Published.Should().Be(0);
        (await ListenerRecording(project.Artist, "1975-10-17")).Should().BeEquivalentTo(before);
        (await Artists.All()).Count(artist => artist.name == project.Name).Should().Be(1);
    }

    [Test]
    public async Task ExistingArtistBindingRejectsAnUnrelatedTargetOrAnIntakeCreatedArtist()
    {
        var existing = await ExistingProject();
        var cohort = await ProposeProject();
        Func<Task> unrelated = () =>
            Intake.BindExistingArtistAsync(
                existing.Candidate,
                new(cohort.Artist.uuid),
                "Wrong match"
            );
        Func<Task> created = () =>
            Intake.BindExistingArtistAsync(
                cohort.Candidate,
                new(cohort.Artist.uuid),
                "Cannot change ownership"
            );
        await unrelated.Should().ThrowAsync<ArgumentException>();
        await created.Should().ThrowAsync<InvalidOperationException>();
    }

    [Test]
    public async Task ConflictingPerformanceBillingKeepsOwnedSourceAndAcceptedPublicationDate()
    {
        var project = await ExistingProject();
        await Intake.BindExistingArtistAsync(
            project.Candidate,
            new(project.Artist.uuid),
            "Reviewed existing identity"
        );
        var item = AddRecording(project.Name + " Live at Fixture Hall Early Show", "1975-10-17");
        recordings[item]["metadata"]!["creator"] = project.Name;
        await Discover();
        (await Refresh()).Published.Should().Be(1);
        var before = await ListenerRecording(project.Artist, "1975-10-17");
        var accepted = await ItemStatus(item);

        recordings[item]["metadata"]!["title"] = project.Name + " Live at Fixture Hall Late Show";
        recordings[item]["metadata"]!["date"] = "1975-10-18";
        var result = await Refresh();
        result.Failed.Should().Be(0);
        result.NeedsReview.Should().Be(1);
        result.Published.Should().Be(0);
        var held = await ItemStatus(item);
        held["published_display_date"]!.Value<string>().Should().Be("1975-10-17");
        held["published_observation_id"]!
            .Should()
            .BeEquivalentTo(accepted["published_observation_id"]);
        (await ListenerRecording(project.Artist, "1975-10-17")).Should().BeEquivalentTo(before);
    }

    [Test]
    public async Task ExistingArtistRenameHoldsPublicationUntilItsSameIdentityIsReviewedAgain()
    {
        var project = await ExistingProject();
        await Intake.BindExistingArtistAsync(
            project.Candidate,
            new(project.Artist.uuid),
            "Reviewed initial identity"
        );
        var item = AddRecording(project.Name + " Live at Fixture Hall", "1975-10-17");
        recordings[item]["metadata"]!["creator"] = project.Name;
        await Discover();
        (await Refresh()).Published.Should().Be(1);
        var before = await ListenerRecording(project.Artist, "1975-10-17");
        var renamed = (await Artists.FindArtistForImport(project.Artist.uuid.ToString()))!;
        renamed.name += " renamed";
        renamed.slug += "-renamed";
        var saved = await Artists.Save(renamed);
        saved.uuid.Should().Be(project.Artist.uuid);
        recordings[item]["files"]![0]!["title"] = "Reviewed metadata update";
        var held = await Refresh();
        held.Published.Should().Be(0);
        (await ListenerRecording(project.Artist, "1975-10-17")).Should().BeEquivalentTo(before);

        await Intake.BindExistingArtistAsync(
            project.Candidate,
            new(project.Artist.uuid),
            "Reviewed catalog rename; same performer UUID"
        );
        (await Refresh()).Failed.Should().Be(0);
        (await ListenerRecording(renamed, "1975-10-17", "Reviewed metadata update"))
            .Should()
            .BeEquivalentTo(before);
        (await Artists.FindArtistForImport(renamed.uuid.ToString()))!
            .name.Should()
            .Be(renamed.name);
    }

    [Test]
    public async Task PausedArtistRecoversAnOwnedPerformanceHoldCommittedBeforeTheWorkflowRebuild()
    {
        var project = await ExistingProject();
        await Intake.BindExistingArtistAsync(
            project.Candidate,
            new(project.Artist.uuid),
            "Reviewed existing identity"
        );
        var item = AddRecording(project.Name + " Live at Fixture Hall Early Show", "1975-10-17");
        recordings[item]["metadata"]!["creator"] = project.Name;
        await Discover();
        (await Refresh()).Published.Should().Be(1);
        var before = await ListenerRecording(project.Artist, "1975-10-17");
        recordings[item]["metadata"]!["title"] = project.Name + " Live at Fixture Hall Late Show";
        var raw = recordings[item].ToString();
        var held = await Intake.ApplyObservationAsync(
            new(new(item), project.Candidate),
            ArchiveObservationProjector.Project(new(item), raw),
            raw
        );
        held.Outcome.Should().Be(TapersSectionItemOutcome.NeedsReview);
        var artist = new ArtistId(project.Artist.uuid);
        (await Intake.CaptureDirtyRevisionsAsync(artist)).Should().ContainSingle();
        await db.WithWriteConnection(connection =>
            connection.ExecuteAsync(
                "UPDATE normalization.archive_artist_candidates SET approved=false WHERE candidate_id=@id",
                new { id = project.Candidate }
            )
        );
        (await Intake.PrepareCohortAsync()).Should().Contain(artist);
        var requests = upstream.Requests;
        (await Refresh()).Failed.Should().Be(0);
        upstream
            .Requests.Should()
            .Be(requests, "paused repair must rebuild without fetching metadata");
        (await Intake.CaptureDirtyRevisionsAsync(artist)).Should().BeEmpty();
        (await ListenerRecording(project.Artist, "1975-10-17")).Should().BeEquivalentTo(before);
    }

    [Test]
    public async Task UnlabeledRecordingOnAnExistingSplitDateRemainsHeld()
    {
        var project = await ExistingProject();
        await Intake.BindExistingArtistAsync(
            project.Candidate,
            new(project.Artist.uuid),
            "Reviewed existing identity"
        );
        var early = AddRecording(project.Name + " Live at Fixture Hall Early Show", "1975-10-17");
        var late = AddRecording(project.Name + " Live at Fixture Hall Late Show", "1975-10-17");
        recordings[early]["metadata"]!["creator"] = project.Name;
        recordings[late]["metadata"]!["creator"] = project.Name;
        await Discover();
        (await Refresh()).Published.Should().Be(2);
        var unlabeled = AddRecording(project.Name + " Live at Fixture Hall", "1975-10-17");
        recordings[unlabeled]["metadata"]!["creator"] = project.Name;
        await Discover();
        var result = await Refresh();
        result.NeedsReview.Should().Be(1);
        result.Failed.Should().Be(0);
        result.Published.Should().Be(0);
        (
            await host
                .Services.GetRequiredService<SourceService>()
                .AllForArtistFromPrimary(project.Artist)
        )
            .Should()
            .HaveCount(2);
        (await ItemStatus(unlabeled))["reason"]!.Value<string>().Should().Be("ambiguous_date");
    }

    [TestCase("Early Show & Late Show")]
    [TestCase("Maybe Early Show")]
    public async Task UnresolvedPerformanceBillingIsHeldWithoutFailingTheArtistWorkflow(
        string billing
    )
    {
        var project = await ExistingProject();
        await Intake.BindExistingArtistAsync(
            project.Candidate,
            new(project.Artist.uuid),
            "Reviewed existing identity"
        );
        var item = AddRecording(project.Name + " Live at Fixture Hall " + billing, "1975-10-17");
        recordings[item]["metadata"]!["creator"] = project.Name;
        await Discover();
        var result = await Refresh();
        result.NeedsReview.Should().Be(1);
        result.Failed.Should().Be(0);
        result.Published.Should().Be(0);
        (await ItemStatus(item))["reason"]!.Value<string>().Should().Be("ambiguous_date");
        (
            await host
                .Services.GetRequiredService<SourceService>()
                .AllForArtistFromPrimary(project.Artist)
        )
            .Should()
            .BeEmpty();
    }

    private async Task<Project> ExistingProject()
    {
        var name = "Existing project " + Guid.NewGuid().ToString("N");
        var artist = await Artists.Save(
            new SlimArtistWithFeatures
            {
                name = name,
                slug = "existing-project-" + Guid.NewGuid().ToString("N"),
                featured = (int)ArtistFeaturedFlags.AutoCreated,
                features =
                    Relisten.Services.Indexing.ArchiveOrgArtistDefaults.ArchiveOrgDefaultFeatures(),
            }
        );
        var candidate = await Intake.ProposeProjectAsync(name, artist.slug, [name], true);
        return new(candidate, name, (await Artists.FindArtistForImport(artist.uuid.ToString()))!);
    }

    private async Task<Source> WriteNativeRecording(Project project, string item, string date)
    {
        using var services = host.Services.CreateScope();
        var writer = services.ServiceProvider.GetRequiredService<ArchiveOrgImporter>();
        var transaction = services.ServiceProvider.GetRequiredService<CatalogImportTransaction>();
        var metadata = JsonConvert.DeserializeObject<RootObject>(
            recordings[item].ToString(),
            new TolerantStringConverter()
        )!;
        var source = await transaction.RunAsync(
            DateTimeOffset.UtcNow.AddMinutes(1),
            CancellationToken.None,
            () =>
                writer.WriteResolvedRecordingAsync(
                    project.Artist,
                    1,
                    new SearchDoc { identifier = item, addeddate = DateTime.UtcNow },
                    metadata,
                    date
                )
        );
        var rebuild = services.ServiceProvider.GetRequiredService<CatalogRebuildService>();
        await rebuild.RebuildShows(project.Artist);
        await rebuild.RebuildYears(project.Artist);
        return source;
    }

    private async Task<Project> ProposeProject(string? name = null)
    {
        name ??= "Reviewed project " + Guid.NewGuid().ToString("N");
        var candidate = await Intake.ProposeProjectAsync(
            name,
            "reviewed-project-" + Guid.NewGuid().ToString("N"),
            [name],
            true
        );
        var artist = (await Artists.All()).Single(artist => artist.name == name);
        return new(candidate, name, artist);
    }

    private string AddRecording(string title, string date, string? description = null)
    {
        var item = "reviewed-performer-" + Guid.NewGuid().ToString("N");
        recordings[item] = JObject.FromObject(
            new
            {
                metadata = new
                {
                    identifier = item,
                    creator = "Jerry Garcia",
                    mediatype = "audio",
                    title,
                    description = description ?? "A single performance.",
                    date,
                    collection = new[] { "taperssection" },
                },
                files = new[]
                {
                    new
                    {
                        name = "01.mp3",
                        title = "First song",
                        format = "VBR MP3",
                        length = "1:02",
                        md5 = "first",
                    },
                    new
                    {
                        name = "02.mp3",
                        title = "Second song",
                        format = "VBR MP3",
                        length = "2:03",
                        md5 = "second",
                    },
                },
            }
        );
        return item;
    }

    private async Task<ArchivePerformerReview> MakeReview(
        string item,
        Project project,
        string locator
    )
    {
        var projection = ArchiveObservationProjector.Project(
            new ArchiveItemId(item),
            recordings[item].ToString()
        );
        var observation = await Intake.RetainObservationAsync(projection);
        var raw = recordings[item].SelectToken(locator)!.Value<string>()!;
        var start = raw.IndexOf(project.Name, StringComparison.Ordinal);
        start
            .Should()
            .BeGreaterThanOrEqualTo(
                0,
                "fixture evidence must name the reviewed performing project"
            );
        return new(
            item,
            project.Candidate,
            observation,
            [new(locator, project.Name, "reviewed_project_billing", start, project.Name.Length)],
            "Reviewed literal project billing",
            true
        );
    }

    private async Task<TapersSectionSummary> Discover() =>
        await (
            await host
                .Services.GetRequiredService<TemporalIngestionClient>()
                .DiscoverTapersSectionAsync(new DiscoverTapersSection())
        ).GetResultAsync();

    private async Task<TapersSectionSummary> Refresh() =>
        await (
            await host
                .Services.GetRequiredService<TemporalIngestionClient>()
                .RefreshTapersSectionAsync(new RefreshTapersSection())
        ).GetResultAsync();

    private async Task<JToken> ItemStatus(string item) =>
        JObject.Parse(System.Text.Json.JsonSerializer.Serialize(await Intake.StatusAsync(item)))[
            "items"
        ]!.Single();

    private async Task<ArtistWithCounts[]> DefaultArtistList()
    {
        using var services = host.Services.CreateScope();
        var controller = ActivatorUtilities.CreateInstance<ArtistsController>(
            services.ServiceProvider
        );
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext(),
        };
        controller.HttpContext.Request.Path = "/api/v3/artists";
        return ((JsonResult)await controller.Get())
            .Value.Should()
            .BeAssignableTo<IEnumerable<ArtistWithCounts>>()
            .Which.ToArray();
    }

    private async Task<JObject> NormalizedArtist(Guid artist)
    {
        using var services = host.Services.CreateScope();
        var controller = ActivatorUtilities.CreateInstance<ArtistsController>(
            services.ServiceProvider
        );
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext(),
        };
        controller.HttpContext.Request.Path = "/api/v3/artists/" + artist + "/normalized";
        return JObject.Parse(
            ((ContentResult)await controller.GetFullArtist(artist.ToString())).Content!
        );
    }

    private async Task<RecordingIdentity> ListenerRecording(
        Artist artist,
        string date,
        string firstTitle = "First song"
    )
    {
        var show = await host
            .Services.GetRequiredService<ShowService>()
            .ShowWithSourcesForArtistOnDate(artist, date);
        show.Should().NotBeNull();
        var source = show!.sources.Should().ContainSingle().Subject;
        var tracks = source.sets.SelectMany(set => set.tracks).ToArray();
        tracks.Select(track => track.title).Should().Equal(firstTitle, "Second song");
        tracks
            .Select(track => track.mp3_url)
            .Should()
            .OnlyContain(url => url != null && url.StartsWith("https://archive.org/download/"));
        var controller = new CatalogController(
            host.Services.GetRequiredService<CatalogReferenceResolver>()
        );
        var response = (CatalogResolveResponse)
            (
                (JsonResult)
                    await controller.Resolve(
                        new CatalogResolveRequest { contract_version = 1, references = new[]
                            {
                                new CatalogReferenceRequest
                                {
                                    catalog_type = "source",
                                    catalog_uuid = source.uuid.ToString(),
                                },
                            }.Concat(tracks.Select(track => new CatalogReferenceRequest { catalog_type = "source_track", catalog_uuid = track.uuid.ToString() })).ToArray() }
                    )
            ).Value!;
        response
            .references.Should()
            .OnlyContain(reference => reference.availability == "available");
        response.entities.sources.Should().ContainSingle().Which.uuid.Should().Be(source.uuid);
        response
            .entities.source_tracks.Select(track => track.uuid)
            .Should()
            .BeEquivalentTo(tracks.Select(track => track.uuid));
        response
            .entities.source_tracks.Should()
            .OnlyContain(track => track.artist_uuid == artist.uuid && track.show_uuid == show.uuid);
        return new(
            artist.uuid,
            source.uuid,
            show.uuid,
            source.sets.Select(set => set.uuid).ToArray(),
            tracks.Select(track => track.uuid).ToArray()
        );
    }

    private sealed record Project(Guid Candidate, string Name, Artist Artist);

    private sealed record RecordingIdentity(
        Guid Artist,
        Guid Source,
        Guid Show,
        Guid[] Sets,
        Guid[] Tracks
    );

    private sealed class IgnoredPopularityJobs : Hangfire.IBackgroundJobClient
    {
        public string Create(Hangfire.Common.Job job, Hangfire.States.IState state) =>
            "fixture-popularity";

        public bool ChangeState(string jobId, Hangfire.States.IState state, string expectedState) =>
            true;
    }
}
