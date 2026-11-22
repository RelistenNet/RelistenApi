using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using NUnit.Framework;
using Relisten.Accounts.Contracts.Playlists;
using Relisten.Identifiers;

namespace RelistenUserServiceTests;

public sealed partial class TestPlaylistApi
{
    private readonly PlaylistCatalogStub _catalog = new();

    [Test]
    public async Task Fresh_creation_requires_playable_tracks_and_replays_during_catalog_outages()
    {
        var create = NewPlaylist(1);
        _catalog.Tracks[Track] = false;
        using var rejected = await Send(HttpMethod.Post, "/v1/playlists", create);
        await Problem(rejected, 422, "track_unavailable");

        _catalog.Tracks[Track] = true;
        using var created = await Send(HttpMethod.Post, "/v1/playlists", create);
        created.StatusCode.Should().Be(HttpStatusCode.Created);

        _catalog.Unavailable = true;
        using var replay = await Send(HttpMethod.Post, "/v1/playlists", create);
        replay.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Read<PlaylistCreated>(replay)).Should().Be(await Read<PlaylistCreated>(created));
        (await Snapshot(create.PlaylistId))
            .Segments.Single()
            .Entries.Single()
            .TrackId.Should()
            .Be(Track);
    }

    [Test]
    public async Task Fresh_insertions_reject_unplayable_and_missing_tracks_and_receipt_the_results()
    {
        var create = await Create(1);
        var segment = create.Segments.Single();
        var missingTrack = new TrackId(Guid.NewGuid()).ToString();
        var otherTrack = new TrackId(Guid.NewGuid()).ToString();
        _catalog.Tracks[Track] = false;
        _catalog.Tracks[missingTrack] = null;
        var batch = new PlaylistBatchRequest(
            1,
            [
                Op("insert_entries", new InsertPlaylistEntries(segment.SegmentId, [Entry()])),
                Op(
                    "insert_segment",
                    new InsertPlaylistSegment(
                        PlaylistSegmentId.New(),
                        [Entry() with { TrackId = missingTrack }]
                    )
                ),
                Op(
                    "insert_entries",
                    new InsertPlaylistEntries(
                        segment.SegmentId,
                        [Entry() with { TrackId = otherTrack }]
                    )
                ),
            ]
        );

        var result = await Apply(create.PlaylistId, batch);
        result
            .Results.Select(row => row.Status)
            .Should()
            .Equal("track_unavailable", "track_unavailable", "applied");
        result.Results.Select(row => row.Revision).Should().Equal(null, null, 2);
        (await Snapshot(create.PlaylistId))
            .Segments.Single()
            .Entries.Select(entry => entry.TrackId)
            .Should()
            .Equal(Track, otherTrack);

        _catalog.Tracks.Clear();
        _catalog.Tracks[otherTrack] = false;
        (await Apply(create.PlaylistId, batch)).Should().BeEquivalentTo(result);

        // A replay of the successful add still works alongside a fresh failed add.
        var fresh = Op(
            "insert_entries",
            new InsertPlaylistEntries(segment.SegmentId, [Entry() with { TrackId = otherTrack }])
        );
        var mixed = await Apply(create.PlaylistId, new(1, [batch.Operations[2], fresh]));
        mixed.Results.Select(row => row.Status).Should().Equal("applied", "track_unavailable");
        mixed.HeadRevision.Should().Be(2);
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task Catalog_response_failures_leave_a_fresh_batch_retryable(bool httpFailure)
    {
        var create = await Create(1);
        var before = await Snapshot(create.PlaylistId);
        var batch = new PlaylistBatchRequest(
            1,
            [
                Op("update_metadata", new UpdatePlaylistMetadata(new(true, "New name"))),
                Op(
                    "insert_entries",
                    new InsertPlaylistEntries(create.Segments[0].SegmentId, [Entry()])
                ),
            ]
        );
        _catalog.Unavailable = httpFailure;
        _catalog.OmitPlayable = !httpFailure;

        using var failure = await Send(
            HttpMethod.Post,
            $"/v1/playlists/{create.PlaylistId}/operations:batch",
            batch
        );
        await Problem(failure, 503, "catalog_unavailable");
        (await Snapshot(create.PlaylistId)).Should().BeEquivalentTo(before);

        _catalog.Reset();
        (await Apply(create.PlaylistId, batch))
            .Results.Select(row => row.Status)
            .Should()
            .Equal("applied", "applied");
        var after = await Snapshot(create.PlaylistId);
        after.Metadata.Name.Should().Be("New name");
        after.Segments.Single().Entries.Select(entry => entry.TrackId).Should().Equal(Track, Track);
    }

    [Test]
    public async Task A_copy_preserves_saved_track_references_without_catalog_access()
    {
        var create = await Create(1);
        var publication = await Publish(create.PlaylistId);
        _catalog.Unavailable = true;

        var copy = await ReadResponse<PlaylistCreated>(
            HttpMethod.Post,
            $"/v1/public-playlists/{publication.PublicCode}/copy",
            new CopyPublicPlaylistRequest(1, CommandId.New())
        );
        (await Snapshot(copy.PlaylistId))
            .Segments.Single()
            .Entries.Single()
            .TrackId.Should()
            .Be(Track);
    }

    [Test]
    public async Task Creation_checks_all_tracks_when_the_playlist_exceeds_the_catalog_request_limit()
    {
        var create = NewPlaylist(1);
        var entries = Enumerable
            .Range(0, 501)
            .Select(_ => Entry() with { TrackId = new TrackId(Guid.NewGuid()).ToString() })
            .ToArray();
        create = create with { Segments = [create.Segments[0] with { Entries = entries }] };
        _catalog.Tracks[entries[^1].TrackId!] = false;

        using var rejected = await Send(HttpMethod.Post, "/v1/playlists", create);
        await Problem(rejected, 422, "track_unavailable");

        _catalog.Reset();
        using var created = await Send(HttpMethod.Post, "/v1/playlists", create);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        (await Snapshot(create.PlaylistId)).Segments.Single().Entries.Should().HaveCount(501);
    }

    private sealed class PlaylistCatalogStub : HttpMessageHandler
    {
        public Dictionary<string, bool?> Tracks { get; } = new(StringComparer.Ordinal);
        public bool Unavailable { get; set; }
        public bool OmitPlayable { get; set; }

        public void Reset()
        {
            Tracks.Clear();
            Unavailable = false;
            OmitPlayable = false;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            request.Method.Should().Be(HttpMethod.Post);
            request.RequestUri!.AbsolutePath.Should().Be("/api/v4/catalog/resolve");
            if (Unavailable)
            {
                return new(HttpStatusCode.ServiceUnavailable);
            }

            var payload = await request.Content!.ReadFromJsonAsync<ResolveRequest>(
                cancellationToken
            );
            payload!.References.Length.Should().BeInRange(1, 500);
            var results = payload
                .References.Select(reference => new
                {
                    reference.Id,
                    Playable = Tracks.GetValueOrDefault(reference.Id, true),
                })
                .ToArray();

            return new(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(
                    new
                    {
                        references = results.Select(result => new
                        {
                            kind = "track",
                            scheme = "v4",
                            result.Id,
                            availability = result.Playable is null ? "unavailable" : "available",
                            target = result.Playable is null
                                ? null
                                : new { kind = "track", result.Id },
                        }),
                        entities = new
                        {
                            tracks = results
                                .Where(result => result.Playable is not null)
                                .Select(result =>
                                    OmitPlayable ? (object)new { result.Id } : result
                                ),
                        },
                    }
                ),
            };
        }

        private sealed record ResolveRequest(ResolveReference[] References);

        private sealed record ResolveReference(string Id);
    }
}
