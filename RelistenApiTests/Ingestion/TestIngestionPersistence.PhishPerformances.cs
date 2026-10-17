using Dapper;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Relisten.Data;
using Relisten.Import;
using Relisten.Ingestion;
using Relisten.Ingestion.Providers.PhishIn;
using Relisten.Ingestion.Providers.PhishNet;

namespace RelistenApiTests.Ingestion;

public partial class TestIngestionPersistence
{
    [Test]
    public async Task PhishInSeparatesSameDayProviderShowsAndKeepsIdentitiesAfterCorrection()
    {
        var (activities, context, catalogs, items) = await PreparePhishShows();
        var sameDay = items
            .Select(item => item with { Input = item.Input with { Date = "2000-01-01" } })
            .ToArray();
        var results = new List<PhishInItemResult>();
        foreach (var item in sameDay)
        {
            results.Add(
                await new Temporalio.Testing.ActivityEnvironment().RunAsync(() =>
                    activities.ImportShowAsync(context, catalogs, item)
                )
            );
        }
        await RebuildPhish();
        var before = await PhishIdentities();
        before.Should().HaveCount(2);
        before.Select(row => row.Show).Should().OnlyHaveUniqueItems();
        before.Select(row => row.Setlist).Should().OnlyHaveUniqueItems();
        results.Select(result => result.Setlist).Should().OnlyHaveUniqueItems();

        foreach (var item in sameDay.Reverse())
        {
            await new Temporalio.Testing.ActivityEnvironment().RunAsync(() =>
                activities.ImportShowAsync(context, catalogs, item)
            );
        }
        await RebuildPhish();
        (await PhishIdentities()).Should().BeEquivalentTo(before);

        var corrected = sameDay[0] with
        {
            Input = sameDay[0].Input with
            {
                Date = "2000-01-03",
                UpdatedAt = DateTime.UtcNow.AddMinutes(1),
            },
        };
        await new Temporalio.Testing.ActivityEnvironment().RunAsync(() =>
            activities.ImportShowAsync(context, catalogs, corrected)
        );
        await RebuildPhish();
        (await PhishIdentities())
            .Should()
            .BeEquivalentTo(
                before,
                "date corrections preserve recording, setlist, performance and playback identities"
            );
        var dates = await db.WithWriteConnection(connection =>
            connection.QueryAsync<string>(
                "SELECT display_date FROM shows WHERE artist_id=@artistId ORDER BY display_date",
                new { artistId }
            )
        );
        dates.Should().Equal("2000-01-01", "2000-01-03");
        var correctedSource = (
            await host
                .Services.GetRequiredService<SourceService>()
                .FullSourcesGeneric("s.id=@sourceId", new { sourceId = results[0].Source.Value })
        ).Single();
        correctedSource
            .links.Single(link => link.upstream_source_id == 2)
            .url.Should()
            .Be("https://phish.in/2000-01-03");
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task PhishInUpgradesDateKeyedSetlistWithoutReplacingItsUuidOrSongReferences(
        bool correctDate
    )
    {
        var (activities, context, catalogs, items) = await PreparePhishShows();
        var item = items[0];
        var environment = new Temporalio.Testing.ActivityEnvironment();
        var first = await environment.RunAsync(() =>
            activities.ImportShowAsync(context, catalogs, item)
        );
        await RebuildPhish();
        var before = await PhishIdentities();
        var songsBefore = (await Snapshot())["plays"];
        songsBefore.Should().ContainSingle();
        await db.WithWriteConnection(connection =>
            connection.ExecuteAsync(
                "UPDATE setlist_shows SET upstream_identifier=@date WHERE id=@id",
                new { date = item.Input.Date, id = first.Setlist.Value }
            )
        );

        var refreshed = correctDate
            ? item with
            {
                Input = item.Input with
                {
                    Date = "2000-01-03",
                    UpdatedAt = DateTime.UtcNow.AddMinutes(1),
                },
            }
            : item;
        var repeated = await environment.RunAsync(() =>
            activities.ImportShowAsync(context, catalogs, refreshed)
        );
        repeated.Setlist.Should().Be(first.Setlist);
        await RebuildPhish();
        (await PhishIdentities()).Should().BeEquivalentTo(before);
        (await Snapshot())["plays"].Should().Equal(songsBefore);
        var key = await db.WithWriteConnection(connection =>
            connection.QuerySingleAsync<string>(
                "SELECT upstream_identifier FROM setlist_shows WHERE id=@id",
                new { id = first.Setlist.Value }
            )
        );
        key.Should().Be(item.Id.Value.ToString());
    }

    [Test]
    public async Task PhishNetRetainsProviderShowIdentityAndOfficialLinkWithoutChangingPerformance()
    {
        await Import();
        var before = await SourceBeforeRebuild();
        await ConfigureProvider("phish.net", 3);
        const string permalink = "https://phish.net/setlists/phish-january-01-2000-fixture.html";
        upstream.Respond = uri =>
            uri.AbsolutePath.StartsWith("/v5/shows")
                ? JsonConvert.SerializeObject(
                    new
                    {
                        error = false,
                        data = new[]
                        {
                            new
                            {
                                showid = 1234,
                                showdate = "2000-01-01",
                                artist_name = "Phish",
                                setlist_notes = "Individual show notes",
                                permalink,
                            },
                        },
                    }
                )
            : uri.AbsolutePath.Contains("/reviews/")
                ? JsonConvert.SerializeObject(
                    new
                    {
                        error = false,
                        data = new[]
                        {
                            new
                            {
                                showid = 1234,
                                username = "Matching reviewer",
                                review_text = "Correct performance",
                                posted_at = "2026-01-01 00:00:00",
                            },
                            new
                            {
                                showid = 5678,
                                username = "Other reviewer",
                                review_text = "Other performance",
                                posted_at = "2026-01-01 00:00:00",
                            },
                        },
                    }
                )
            : ProviderFixtures.Response("phish.net", uri);
        var activities = host.Services.GetRequiredService<PhishNetActivities>();
        var context = new ProviderWorkflowContext(
            new ArtistId(artistUuid),
            new IngestionExecution(Guid.NewGuid().ToString(), DateTimeOffset.UtcNow.AddDays(1))
        );
        var environment = new Temporalio.Testing.ActivityEnvironment();
        var selected = await environment.RunAsync(() =>
            activities.SelectSourcesAsync(
                new ProviderRequest<RefreshPhishNetArtist>(new(context.Artist), context.Execution)
            )
        );
        selected.Single().Input.Show!.ShowId.Should().Be(1234);
        var result = await environment.RunAsync(() =>
            activities.EnrichSourceAsync(context, selected.Single())
        );
        result.Disposition.Should().Be(ItemDisposition.Applied);
        var source = (
            await host
                .Services.GetRequiredService<SourceService>()
                .FullSourcesGeneric("s.artist_id=@artistId", new { artistId })
        ).Single();
        source.links.Single(link => link.upstream_source_id == 3).url.Should().Be(permalink);
        source.description.Should().Be("Individual show notes");
        source
            .num_reviews.Should()
            .Be(1, "reviews from a different provider show must not be attached");
        var reviews = await host
            .Services.GetRequiredService<SourceService>()
            .ReviewsForSource(source.id);
        reviews.Should().ContainSingle().Which.review.Should().Be("Correct performance");
        source.uuid.Should().Be(before.SourceUuid);
        var retained = await db.WithWriteConnection(connection =>
            connection.QuerySingleAsync<int>(
                "SELECT (evidence->'provider_evidence'->'phish.net'->>'show_id')::int FROM source_show_assignments WHERE source_id=@sourceId",
                new { sourceId = result.Source.Value }
            )
        );
        retained.Should().Be(1234);
    }

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(true, true)]
    public async Task PhishNetSkipsAmbiguousPerformancesWithoutFetchingOrOverwritingEnrichment(
        bool catalogSplit,
        bool legacy
    )
    {
        if (catalogSplit)
        {
            var (activities, context, catalogs, items) = await PreparePhishShows();
            foreach (var item in items)
            {
                var sameDay = item with { Input = item.Input with { Date = "2000-01-01" } };
                await new Temporalio.Testing.ActivityEnvironment().RunAsync(() =>
                    activities.ImportShowAsync(context, catalogs, sameDay)
                );
            }
            await RebuildPhish();
        }
        else
        {
            await Import();
        }
        var before = await Snapshot();
        await ConfigureProvider("phish.net", 3);
        var requestsBefore = upstream.Requests;
        upstream.Respond = _ =>
            JsonConvert.SerializeObject(
                new
                {
                    error = false,
                    data = Enumerable
                        .Range(1, catalogSplit ? 1 : 2)
                        .Select(id => new
                        {
                            showid = id,
                            showdate = "2000-01-01",
                            artist_name = "Phish",
                            setlist_notes = "Must not replace accepted metadata",
                            permalink = $"https://phish.net/setlists/show-{id}.html",
                        }),
                }
            );
        await AssertPhishNetSkipsAll(legacy);
        (await Snapshot()).Should().BeEquivalentTo(before);
        (upstream.Requests - requestsBefore)
            .Should()
            .Be(1, "ambiguous dates must only fetch the show listing, without ratings or reviews");
    }

    [TestCase("combined", false)]
    [TestCase("unresolved", false)]
    [TestCase("combined", true)]
    [TestCase("unresolved", true)]
    [TestCase("missing", false)]
    [TestCase("missing", true)]
    public async Task PhishNetKeepsUnsupportedRecordingPlayableWithoutEnrichingIt(
        string kind,
        bool legacy
    )
    {
        await Import();
        await ConfigureProvider("phish.net", 3);
        await db.WithWriteConnection(connection =>
            connection.ExecuteAsync(
                "UPDATE source_show_assignments SET assignment_kind=@kind WHERE source_id IN (SELECT id FROM sources WHERE artist_id=@artistId)",
                new { kind = kind == "missing" ? "legacy" : kind, artistId }
            )
        );
        var before = await Snapshot();
        var requestsBefore = upstream.Requests;
        upstream.Respond = uri =>
            kind == "missing"
                ? ProviderFixtures.Response("phish.net", uri).Replace("2000-01-01", "2000-01-02")
                : ProviderFixtures.Response("phish.net", uri);
        await AssertPhishNetSkipsAll(legacy);
        (await Snapshot()).Should().BeEquivalentTo(before);
        (upstream.Requests - requestsBefore)
            .Should()
            .Be(1, "uncertain assignments must only fetch the listing, without ratings or reviews");
    }

    [Test]
    public async Task PhishNetHoldsASelectionWhoseRecordingDateChangedBeforeEnrichment()
    {
        await Import();
        await ConfigureProvider("phish.net", 3);
        upstream.Respond = uri => ProviderFixtures.Response("phish.net", uri);
        var activities = host.Services.GetRequiredService<PhishNetActivities>();
        var context = new ProviderWorkflowContext(
            new ArtistId(artistUuid),
            new IngestionExecution(Guid.NewGuid().ToString(), DateTimeOffset.UtcNow.AddDays(1))
        );
        var environment = new Temporalio.Testing.ActivityEnvironment();
        var items = await environment.RunAsync(() =>
            activities.SelectSourcesAsync(
                new ProviderRequest<RefreshPhishNetArtist>(new(context.Artist), context.Execution)
            )
        );
        await db.WithWriteConnection(connection =>
            connection.ExecuteAsync(
                "UPDATE sources SET display_date='2000-01-02' WHERE artist_id=@artistId",
                new { artistId }
            )
        );
        await RebuildPhish();
        var before = await Snapshot();
        var requestsBefore = upstream.Requests;
        var result = await environment.RunAsync(() =>
            activities.EnrichSourceAsync(context, items.Single())
        );
        result.Disposition.Should().Be(ItemDisposition.Skipped);
        result.SkipReason.Should().Contain("date changed after selection");
        (await Snapshot()).Should().BeEquivalentTo(before);
        upstream
            .Requests.Should()
            .Be(requestsBefore, "stale selection must be replaced before HTTP enrichment");
    }

    private async Task AssertPhishNetSkipsAll(bool legacy)
    {
        if (legacy)
        {
            using var scope = host.Services.CreateScope();
            var artist = (
                await scope
                    .ServiceProvider.GetRequiredService<ArtistService>()
                    .FindArtistForImport(artistUuid.ToString())
            )!;
            var importer = scope.ServiceProvider.GetRequiredService<PhishNetImporter>();
            importer.UseIngestionHttpClient(
                new HttpClient(new FixtureRedirectHandler(upstream.Address))
            );
            var stats = await importer.ImportDataForArtist(
                artist,
                artist.upstream_sources.Single(),
                null
            );
            stats.Created.Should().Be(0);
            stats.Updated.Should().Be(0);
            return;
        }

        var activities = host.Services.GetRequiredService<PhishNetActivities>();
        var context = new ProviderWorkflowContext(
            new ArtistId(artistUuid),
            new IngestionExecution(Guid.NewGuid().ToString(), DateTimeOffset.UtcNow.AddDays(1))
        );
        var environment = new Temporalio.Testing.ActivityEnvironment();
        var selected = await environment.RunAsync(() =>
            activities.SelectSourcesAsync(
                new ProviderRequest<RefreshPhishNetArtist>(new(context.Artist), context.Execution)
            )
        );
        foreach (var item in selected)
        {
            var result = await environment.RunAsync(() =>
                activities.EnrichSourceAsync(context, item)
            );
            result.Disposition.Should().Be(ItemDisposition.Skipped);
            result.SkipReason.Should().NotBeNullOrWhiteSpace();
        }
    }

    private async Task<(
        PhishInActivities Activities,
        ProviderWorkflowContext Context,
        PhishInCatalogs Catalogs,
        DiscoveredItem<PhishInShowId, PhishInShowInput>[] Items
    )> PreparePhishShows()
    {
        await ConfigureProvider("phish.in", 2);
        upstream.Respond = uri => ProviderFixtures.Response("phish.in", uri);
        var activities = host.Services.GetRequiredService<PhishInActivities>();
        var context = new ProviderWorkflowContext(
            new ArtistId(artistUuid),
            new IngestionExecution(Guid.NewGuid().ToString(), DateTimeOffset.UtcNow.AddDays(1))
        );
        var environment = new Temporalio.Testing.ActivityEnvironment();
        var catalogs = await environment.RunAsync(() => activities.PrepareCatalogsAsync(context));
        var page = await environment.RunAsync(() =>
            activities.ReadShowPageAsync(
                new ProviderRequest<RefreshPhishInArtist>(new(context.Artist), context.Execution),
                null
            )
        );
        return (activities, context, catalogs, page.Items);
    }

    private async Task RebuildPhish()
    {
        var artist = (
            await host
                .Services.GetRequiredService<ArtistService>()
                .FindArtistForImport(artistUuid.ToString())
        )!;
        await host.Services.GetRequiredService<CatalogRebuildService>().RebuildShows(artist);
    }

    private Task<IEnumerable<PhishIdentity>> PhishIdentities() =>
        db.WithWriteConnection(connection =>
            connection.QueryAsync<PhishIdentity>(
                """
                SELECT source.uuid AS source, show.uuid AS show, setlist.uuid AS setlist,
                    track.uuid AS track, track.mp3_url AS url
                FROM sources source
                JOIN shows show ON show.id=source.show_id
                JOIN source_tracks track ON track.source_id=source.id
                JOIN source_show_assignments source_assignment ON source_assignment.source_id=source.id
                JOIN setlist_show_assignments setlist_assignment ON setlist_assignment.show_id=source_assignment.show_id
                JOIN setlist_shows setlist ON setlist.id=setlist_assignment.setlist_show_id
                WHERE source.artist_id=@artistId
                """,
                new { artistId }
            )
        );

    private sealed record PhishIdentity(
        Guid Source,
        Guid Show,
        Guid Setlist,
        Guid Track,
        string Url
    );
}
