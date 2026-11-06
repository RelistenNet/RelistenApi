using System.Diagnostics.Metrics;
using System.Text.Json.Nodes;
using Dapper;
using FluentAssertions;
using Relisten.Identifiers;
using Relisten.Services.Popularity;

namespace RelistenApiTests.V4;

public sealed partial class TestV4CatalogHttp
{
    [Test]
    public async Task GraphQL_bulk_parent_traversal_keeps_database_work_bounded()
    {
        await WithGraphReadFixture(async () =>
        {
            long commands = 0;
            using var listener = new MeterListener();
            listener.InstrumentPublished = (instrument, meter) =>
            {
                if (
                    instrument.Meter.Name == "Relisten.Catalog"
                    && instrument.Name == "catalog.read.commands"
                )
                {
                    meter.EnableMeasurementEvents(instrument);
                }
            };
            listener.SetMeasurementEventCallback<long>(
                (_, value, _, _) => Interlocked.Add(ref commands, value)
            );
            listener.Start();
            const string query = """
                query($ids:[ID!]!) { sourceTracksByIds(ids:$ids) {
                  id source { id } show { id artist { id } }
                  normalization { parts { song { id } } }
                } }
                """;
            var ids = Enumerable
                .Range(1, 250)
                .Select(i =>
                    UuidTypeId.Format("track", Guid.Parse($"10000000-0000-0000-0004-{i:D12}"))
                )
                .ToArray();
            var before = Interlocked.Read(ref commands);
            await Graph(query, new { ids = ids.Take(10).ToArray() });
            var small = Interlocked.Read(ref commands) - before;
            before = Interlocked.Read(ref commands);
            var large = await Graph(query, new { ids });
            var largeCommands = Interlocked.Read(ref commands) - before;
            large["sourceTracksByIds"]!
                .AsArray()
                .Select(track => track!["id"]!.GetValue<string>())
                .Should()
                .Equal(ids);
            // A 25-fold increase in files and parents must not become per-item SQL.
            // Allow command shape changes rather than prescribing an exact private call count.
            largeCommands.Should().BeLessThan(small * 3);
        });
    }

    [Test]
    public async Task GraphQL_complete_recordings_do_not_slice_tracks_at_list_estimates()
    {
        await WithGraphReadFixture(async () =>
        {
            var source = UuidTypeId.Format(
                "source",
                Guid.Parse("10000000-0000-0000-0002-000000000001")
            );
            var graph = await Graph(
                "query($source:ID!){ source(id:$source){ sets{tracks{id}} } }",
                new { source }
            );
            var actual = graph["source"]!["sets"]!
                .AsArray()
                .SelectMany(set => set!["tracks"]!.AsArray())
                .Select(track => track!["id"]!.GetValue<string>())
                .ToArray();
            var rest = await GetJson("/api/v4/sources/" + source);
            actual
                .Should()
                .HaveCount(210)
                .And.Equal(
                    rest["sets"]!
                        .AsArray()
                        .SelectMany(set => set!["tracks"]!.AsArray())
                        .Select(track => track!["id"]!.GetValue<string>())
                );
        });
    }

    [Test]
    public async Task GraphQL_search_continues_past_the_rest_search_window()
    {
        await WithGraphReadFixture(async () =>
        {
            var expected = Enumerable
                .Range(1, 25)
                .Reverse()
                .Select(i =>
                    UuidTypeId.Format("source", Guid.Parse($"10000000-0000-0000-0002-{i:D12}"))
                )
                .ToArray();
            var rest = await GetJson(
                "/api/v4/search?q=graphql-boundary&artists=false&shows=false&songs=false&tours=false&venues=false"
            );
            rest["sources"]!
                .AsArray()
                .Select(source => source!["id"]!.GetValue<string>())
                .Should()
                .Equal(expected.Take(20));
            var actual = new List<string>();
            string? after = null;
            do
            {
                var data = await Graph(
                    """
                    query($after:String){ search(q:"graphql-boundary") { sources(first:17,after:$after) {
                      nodes { id } pageInfo { endCursor hasNextPage }
                    } } }
                    """,
                    new { after }
                );
                var page = data["search"]!["sources"]!;
                actual.AddRange(
                    page["nodes"]!.AsArray().Select(source => source!["id"]!.GetValue<string>())
                );
                after = page["pageInfo"]!["hasNextPage"]!.GetValue<bool>()
                    ? page["pageInfo"]!["endCursor"]!.GetValue<string>()
                    : null;
            } while (after is not null);
            actual.Should().Equal(expected);
        });
    }

    [Test]
    public async Task GraphQL_collects_refreshes_once_across_reader_scopes()
    {
        await WithGraphReadFixture(async () =>
        {
            refreshJobs.Batches.Clear();
            await Graph(
                """
                query($artist:ID!,$show:ID!){
                  artist(id:$artist){shows(first:1){nodes{id}}} show(id:$show){id}
                }
                """,
                new
                {
                    artist = UuidTypeId.Format(
                        "artist",
                        Guid.Parse("10000000-0000-0000-0000-000000000000")
                    ),
                    show = UuidTypeId.Format(
                        "show",
                        Guid.Parse("10000000-0000-0000-0001-000000000026")
                    ),
                }
            );
            refreshJobs.Batches.Should().ContainSingle();
            var populations = refreshJobs.Batches.Single();
            foreach (
                var artist in new[]
                {
                    Guid.Parse("10000000-0000-0000-0000-000000000000"),
                    Guid.Parse("10000000-0000-0000-0000-000000000001"),
                }
            )
            {
                populations
                    .Should()
                    .Contain(population =>
                        population.Kind == PopularityPopulationKind.Show
                        && population.Artist == artist
                    );
            }
        });
    }

    private static async Task WithGraphReadFixture(Func<Task> read)
    {
        await using var connection = await MigratedTestDatabase.OpenAsync();
        try
        {
            await connection.ExecuteAsync(
                await File.ReadAllTextAsync(
                    Path.Combine(
                        AppContext.BaseDirectory,
                        "Fixtures",
                        "v4",
                        "graphql",
                        "read-boundaries.sql"
                    )
                )
            );
            await read();
        }
        finally
        {
            await connection.ExecuteAsync("ROLLBACK");
            await ChangeNormalization(
                connection,
                """
                DELETE FROM norm.track_songs WHERE track_uuid::text LIKE '10000000-0000-0000-0004-%';
                DELETE FROM norm.song_counts WHERE song_id IN
                    (SELECT id FROM norm.songs WHERE artist_uuid='10000000-0000-0000-0000-000000000000');
                DELETE FROM norm.songs WHERE artist_uuid='10000000-0000-0000-0000-000000000000';
                DELETE FROM source_tracks WHERE id BETWEEN 960001 AND 961200;
                DELETE FROM source_sets WHERE id BETWEEN 960001 AND 960025;
                DELETE FROM sources WHERE id BETWEEN 960001 AND 960025;
                DELETE FROM show_source_information WHERE show_id BETWEEN 960001 AND 960025;
                DELETE FROM shows WHERE id BETWEEN 960001 AND 960026;
                DELETE FROM features WHERE artist_id IN (960000,960001);
                DELETE FROM artists WHERE id IN (960000,960001);
                """
            );
        }
    }
}
