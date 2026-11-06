using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using FluentAssertions;
using Relisten.Identifiers;
using Relisten.Services.Popularity;

namespace RelistenApiTests.V4;

public sealed partial class TestV4CatalogHttp
{
    [Test]
    public async Task GraphQL_web_workloads_are_batched_and_fit_public_budgets()
    {
        await using var connection = await MigratedTestDatabase.OpenAsync();
        var sql = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "v4", "graphql", "workload.sql")
        );
        await connection.ExecuteAsync(sql);
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
        try
        {
            var artistId = UuidTypeId.Format(
                "artist",
                Guid.Parse("10000000-0000-0000-0000-000000000000")
            );
            var trackIds = Enumerable
                .Range(1, 250)
                .Select(i =>
                    UuidTypeId.Format("track", Guid.Parse($"10000000-0000-0000-0004-{i:D12}"))
                )
                .ToArray();
            var workloads = new[]
            {
                (
                    Name: "artist-100",
                    Query: "query($artist: ID!) { artist(id: $artist) { id name showCount shows(first: 100) { nodes { id displayDate sourceCount hasSoundboardSource artist { id name } } pageInfo { hasNextPage } } } }",
                    Variables: (object)new { artist = artistId }
                ),
                (
                    Name: "song-history-50",
                    Query: "{ song(id: \"song_00000960\") { id matchedShowCount shows(first: 50) { nodes { show { id displayDate } timings { singleSong { median sourceCount } } tracks(first: 20) { nodes { id title duration mp3Url source { id isSoundboard } } } } } } }",
                    Variables: (object)new { }
                ),
                (
                    Name: "playlist-250",
                    Query: "query($ids: [ID!]!) { sourceTracksByIds(ids: $ids) { id title duration mp3Url source { id isSoundboard } show { id displayDate artist { id name } } normalization { state parts { ordinal song { id name } } } } }",
                    Variables: (object)new { ids = trackIds }
                ),
                (
                    Name: "show-five-recordings",
                    Query: "query($show: ID!) { show(id: $show) { id displayDate sources(first: 10) { nodes { id isSoundboard sets { id tracks { id title mp3Url normalization { state parts { song { id name } } } } } } } } }",
                    Variables: (object)
                        new
                        {
                            show = UuidTypeId.Format(
                                "show",
                                Guid.Parse("10000000-0000-0000-0001-000000000001")
                            ),
                        }
                ),
                (
                    Name: "recording",
                    Query: "query($source: ID!) { source(id: $source) { id sets { id tracks { id title duration mp3Url normalization { state parts { song { id name } } } } } } }",
                    Variables: (object)
                        new
                        {
                            source = UuidTypeId.Format(
                                "source",
                                Guid.Parse("10000000-0000-0000-0002-000000000001")
                            ),
                        }
                ),
            };
            var report = new List<object>();
            foreach (var workload in workloads)
            {
                var timings = new List<double>();
                long maxCommands = 0;
                int bytes = 0;
                JsonNode? cost = null;
                for (var run = 0; run < 6; run++)
                {
                    var before = Interlocked.Read(ref commands);
                    var started = Stopwatch.GetTimestamp();
                    using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v4/graphql")
                    {
                        Content = JsonContent.Create(
                            new { query = workload.Query, variables = workload.Variables }
                        ),
                    };
                    request.Headers.Add("GraphQL-Cost", "report");
                    using var response = await http.SendAsync(request);
                    var body = await response.Content.ReadAsStringAsync();
                    var data = JsonNode.Parse(body)!;
                    data["errors"].Should().BeNull(workload.Name + ": " + body);
                    timings.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                    maxCommands = Math.Max(maxCommands, Interlocked.Read(ref commands) - before);
                    bytes = System.Text.Encoding.UTF8.GetByteCount(body);
                    cost = data["extensions"]?["operationCost"]?.DeepClone();
                }
                // Hundreds of related entities must remain batched. A per-item read
                // regression would exceed this bound well before the workload finishes.
                maxCommands.Should().BeLessThan(100, workload.Name);
                var warm = timings.Skip(1).Order().ToArray();
                report.Add(
                    new
                    {
                        workload.Name,
                        Commands = maxCommands,
                        Bytes = bytes,
                        ColdMs = timings[0],
                        WarmMedianMs = warm[warm.Length / 2],
                        WarmMaxMs = warm[^1],
                        Cost = cost,
                    }
                );
            }
            await File.WriteAllTextAsync(
                "/tmp/relisten-graphql-performance.json",
                JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true })
            );
            var sourceSearch = await GetJson(
                "/api/v4/search?q=graphql-workload&artists=false&shows=false&songs=false&tours=false&venues=false"
            );
            sourceSearch["sources"]!
                .AsArray()
                .Select(n => n!["id"]!.GetValue<string>())
                .Should()
                .Equal(
                    Enumerable
                        .Range(81, 20)
                        .Reverse()
                        .Select(i =>
                            UuidTypeId.Format(
                                "source",
                                Guid.Parse($"10000000-0000-0000-0002-{i:D12}")
                            )
                        )
                );
            refreshJobs.Batches.Clear();
            await Graph(
                "query($artist: ID!, $show: ID!) { artist(id: $artist) { shows(first: 1) { nodes { id } } } show(id: $show) { id } }",
                new
                {
                    artist = artistId,
                    show = UuidTypeId.Format(
                        "show",
                        Guid.Parse("10000000-0000-0000-0001-000000000101")
                    ),
                }
            );
            refreshJobs.Batches.Should().ContainSingle();
            var populations = refreshJobs.Batches.Single();
            populations
                .Should()
                .Contain(p =>
                    p.Kind == PopularityPopulationKind.Show
                    && p.Artist == Guid.Parse("10000000-0000-0000-0000-000000000000")
                );
            populations
                .Should()
                .Contain(p =>
                    p.Kind == PopularityPopulationKind.Show
                    && p.Artist == Guid.Parse("10000000-0000-0000-0000-000000000001")
                );
            await File.WriteAllTextAsync(
                "/tmp/relisten-graphql-performance.json",
                JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true })
            );
        }
        finally
        {
            await ChangeNormalization(
                connection,
                """
                DELETE FROM norm.track_songs WHERE track_uuid::text LIKE '10000000-0000-0000-0004-%';
                DELETE FROM norm.song_counts WHERE song_id='song_00000960';
                DELETE FROM norm.songs WHERE id='song_00000960';
                DELETE FROM source_tracks WHERE id BETWEEN 960001 AND 961040;
                DELETE FROM source_sets WHERE id BETWEEN 960001 AND 960104;
                DELETE FROM sources WHERE id BETWEEN 960001 AND 960104;
                DELETE FROM show_source_information WHERE show_id BETWEEN 960001 AND 960104;
                DELETE FROM shows WHERE id BETWEEN 960001 AND 960101;
                DELETE FROM features WHERE artist_id IN (960000,960001);
                DELETE FROM artists WHERE id IN (960000,960001);
                """
            );
        }
    }
}
