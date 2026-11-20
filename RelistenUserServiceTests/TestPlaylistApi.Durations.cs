using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NUnit.Framework;
using Relisten.Accounts.Contracts.Library;
using Relisten.Accounts.Contracts.Playlists;
using Relisten.Identifiers;
using RelistenUserService.Playlists;

namespace RelistenUserServiceTests;

public sealed partial class TestPlaylistApi
{
    private readonly Dictionary<string, (string ResolvedId, int Duration)> _catalogDurations =
        new();
    private bool _durationCatalogUnavailable;

    [Test]
    public async Task Replacing_a_track_updates_duration_without_changing_count_or_double_counting_retries()
    {
        var replacement = UuidTypeId.Format("track", Guid.NewGuid());
        var redirect = UuidTypeId.Format("track", Guid.NewGuid());
        _catalogDurations[Track] = (Track, 300);
        _catalogDurations[replacement] = (redirect, 120);
        var created = await Create(2);
        var segment = created.Segments.Single();
        var before = await SharingLibrary(_user);
        before.Playlists.Single().DurationSeconds.Should().Be(600);

        var edit = new PlaylistBatchRequest(
            1,
            [
                Op(
                    "insert_entries",
                    new InsertPlaylistEntries(
                        segment.SegmentId,
                        [new(PlaylistEntryId.New(), replacement)]
                    )
                ),
                Op("delete_entry", new DeletePlaylistEntry(segment.Entries[0].EntryId)),
            ]
        );
        var result = await Apply(created.PlaylistId, edit);
        (await Apply(created.PlaylistId, edit)).Should().BeEquivalentTo(result);

        var list = await ReadResponse<PlaylistList>(HttpMethod.Get, "/v1/playlists");
        var summary = list.Playlists.Single();
        summary.TrackCount.Should().Be(2);
        summary.DurationSeconds.Should().Be(420);
        var changes = await PullSharingLibrary(_user, before.NextCursor);
        changes.Changes.Should().ContainSingle().Which.Playlist.Should().BeEquivalentTo(summary);
    }

    [Test]
    public async Task Missing_durations_count_as_zero_until_startup_backfills_them()
    {
        await WaitForDurationBackfill(_app);
        _catalogDurations[Track] = (Track, 300);
        var unknownTrack = UuidTypeId.Format("track", Guid.NewGuid());
        var request = NewPlaylist(1);
        var segment = request.Segments.Single();
        request = request with
        {
            Segments =
            [
                segment with
                {
                    Entries = [.. segment.Entries, new(PlaylistEntryId.New(), unknownTrack)],
                },
            ],
        };
        await ReadResponse<PlaylistCreated>(HttpMethod.Post, "/v1/playlists", request);
        var before = await SharingLibrary(_user);
        before.Playlists.Single().TrackCount.Should().Be(2);
        before.Playlists.Single().DurationSeconds.Should().Be(300);

        _catalogDurations[unknownTrack] = (unknownTrack, 430);
        await using (var restarted = await StartApp())
        {
            await WaitForDurationBackfill(restarted);
        }
        await using (var restarted = await StartApp())
        {
            await WaitForDurationBackfill(restarted);
        }

        var changes = await PullSharingLibrary(_user, before.NextCursor);
        var backfilled = changes.Changes.Should().ContainSingle().Which.Playlist!;
        backfilled.DurationSeconds.Should().Be(730);
        backfilled.Revision.Should().Be(before.Playlists.Single().Revision);
        (await SharingLibrary(_user)).Playlists.Single().Should().BeEquivalentTo(backfilled);
    }

    private static Task WaitForDurationBackfill(WebApplication app) =>
        app
            .Services.GetServices<IHostedService>()
            .OfType<PlaylistDurationStartupBackfill>()
            .Single()
            .ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(15));

    private sealed class DurationCatalogHandler(TestPlaylistApi fixture) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            if (fixture._durationCatalogUnavailable)
            {
                return new(HttpStatusCode.ServiceUnavailable);
            }

            var body = await request.Content!.ReadFromJsonAsync<JsonElement>(cancellationToken);
            var ids = body.GetProperty("references")
                .EnumerateArray()
                .Select(reference => reference.GetProperty("id").GetString()!)
                .Where(fixture._catalogDurations.ContainsKey)
                .ToArray();
            return new(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(
                    new
                    {
                        references = ids.Select(id => new
                        {
                            id,
                            availability = id == fixture._catalogDurations[id].ResolvedId
                                ? "available"
                                : "redirected",
                            target = new { id = fixture._catalogDurations[id].ResolvedId },
                        }),
                        entities = new
                        {
                            tracks = ids.Select(id => fixture._catalogDurations[id])
                                .Distinct()
                                .Select(track => new
                                {
                                    id = track.ResolvedId,
                                    duration = track.Duration,
                                }),
                        },
                    }
                ),
            };
        }
    }
}
