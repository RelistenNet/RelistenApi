using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Relisten.Accounts.Contracts.Identifiers;
using Relisten.Accounts.Contracts.Library;
using Relisten.Accounts.Contracts.Playlists;
using Relisten.Identifiers;
using RelistenIngestionWorker.AccountDeletion;
using RelistenUserService.Identity.Entities;
using RelistenUserService.Playlists;
using RelistenUserService.Playlists.Entities;

namespace RelistenUserServiceTests;

public sealed partial class TestPlaylistApi
{
    private const string FavoriteMutationsPath = "/v1/library/favorite-mutations:batch";

    [Test]
    public async Task Playlists_start_private_and_owner_publication_controls_cached_reads()
    {
        var playlist = await Create(1);
        var listener = await NewSharingListener();
        var publish = new SetPlaylistPublicationRequest(1, true);
        var route = $"/v1/playlists/{playlist.PlaylistId}/publication";

        var privatePlaylist = await Snapshot(playlist.PlaylistId);
        privatePlaylist.Published.Should().BeFalse();
        privatePlaylist.PublicCode.Should().BeNull();
        await Problem(
            await Send(HttpMethod.Put, route, publish, actor: listener),
            404,
            "playlist_not_found"
        );
        var published = await ReadResponse<PlaylistPublication>(HttpMethod.Put, route, publish);
        var publicUrl = $"/v1/public-playlists/{published.PublicCode}";

        using var anonymous = await _client.GetAsync(publicUrl);
        anonymous.Headers.CacheControl!.NoCache.Should().BeTrue();
        anonymous.Headers.GetValues("CDN-Cache-Control").Should().Equal("max-age=60");
        var publicSnapshot = await Read<PublicPlaylistSnapshot>(anonymous);
        publicSnapshot.Owner.Id.Should().Be(_user);
        publicSnapshot.PlaylistId.Should().Be(playlist.PlaylistId);
        publicSnapshot.HistoryGeneration.Should().Be(published.HistoryGeneration);
        publicSnapshot.Segments.Single().Entries.Single().TrackId.Should().Be(Track);
        using var publicBody = JsonDocument.Parse(await anonymous.Content.ReadAsStringAsync());
        publicBody
            .RootElement.EnumerateObject()
            .Select(property => property.Name)
            .Should()
            .BeEquivalentTo(
                "contract_version",
                "history_generation",
                "playlist_id",
                "public_code",
                "revision",
                "owner",
                "metadata",
                "segments"
            );
        publicBody
            .RootElement.GetProperty("owner")
            .EnumerateObject()
            .Select(property => property.Name)
            .Should()
            .BeEquivalentTo("id", "username");

        await Problem(
            await Send(HttpMethod.Put, route, raw: """{"contract_version":1}"""),
            422,
            "invalid_playlist_operation"
        );
        (await _client.GetAsync(publicUrl)).StatusCode.Should().Be(HttpStatusCode.OK);

        using var credentialed = new HttpRequestMessage(HttpMethod.Get, publicUrl);
        credentialed.Headers.Add("Authorization", "Bearer opaque");
        using var privateResponse = await _client.SendAsync(credentialed);
        privateResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        AssertNoStore(privateResponse);

        await ReadResponse<PlaylistPublication>(
            HttpMethod.Put,
            route,
            new SetPlaylistPublicationRequest(1, false)
        );
        using var unpublished = await _client.GetAsync(publicUrl);
        unpublished.StatusCode.Should().Be(HttpStatusCode.NotFound);
        AssertNoStore(unpublished);

        var republished = await Publish(playlist.PlaylistId);
        republished.PublicCode.Should().Be(published.PublicCode);
        (await Publish(playlist.PlaylistId)).Should().Be(republished with { Changed = false });
        await ReadResponse<PlaylistArchived>(
            HttpMethod.Put,
            $"/v1/playlists/{playlist.PlaylistId}/archive-state",
            new ArchivePlaylistRequest(1, CommandId.New(), true)
        );
        (await _client.GetAsync(publicUrl)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        await ReadResponse<PlaylistArchived>(
            HttpMethod.Put,
            $"/v1/playlists/{playlist.PlaylistId}/archive-state",
            new ArchivePlaylistRequest(1, CommandId.New(), false)
        );
        await using var database = _database.CreateContext();
        var restoredGeneration = Guid.CreateVersion7().ToString();
        await database.HistoryGenerations.ExecuteUpdateAsync(update =>
            update.SetProperty(row => row.Value, restoredGeneration)
        );
        var restored = await Read<PublicPlaylistSnapshot>(await _client.GetAsync(publicUrl));
        restored.Revision.Should().Be(publicSnapshot.Revision);
        restored.HistoryGeneration.Should().Be(restoredGeneration);
    }

    [Test]
    public async Task Copy_replays_after_moderation_and_owner_purge_and_preserves_authored_content()
    {
        var source = NewPlaylist(2);
        var segment = source.Segments.Single();
        var originalTrack = segment.Entries[0].EntryId;
        var timestamp = $"#entry={originalTrack}&t=";
        var block = PlaylistEntryId.New();
        source = source with
        {
            Metadata = new("Source guide", $"[Cue]({timestamp}2:00) Mention {originalTrack}"),
            Segments =
            [
                segment with
                {
                    Description = $"Example: {timestamp}2:00",
                    Entries =
                    [
                        segment.Entries[0],
                        new(block, Description: $"[Again]({timestamp}2:00)"),
                        segment.Entries[1] with
                        {
                            Description = $"[First]({timestamp}2:00)",
                        },
                    ],
                },
                new(PlaylistSegmentId.New(), [], "Prose-only segment"),
            ],
        };
        await ReadResponse<PlaylistCreated>(HttpMethod.Post, "/v1/playlists", source);
        var publication = await Publish(source.PlaylistId);
        var publicUrl = $"/v1/public-playlists/{publication.PublicCode}";
        var listener = await NewSharingListener();

        using var follow = await Send(HttpMethod.Put, $"{publicUrl}/follow", actor: listener);
        follow
            .StatusCode.Should()
            .Be(HttpStatusCode.NoContent, await follow.Content.ReadAsStringAsync());
        var followed = (await SharingLibrary(listener)).FollowedPlaylists.Single();
        followed.Owner!.Id.Should().Be(_user);
        followed.Available.Should().BeTrue();
        followed.PublicCode.Should().Be(publication.PublicCode);

        var copyCommand = new CopyPublicPlaylistRequest(1, CommandId.New());
        using var copied = await Send(
            HttpMethod.Post,
            $"{publicUrl}/copy",
            copyCommand,
            actor: listener
        );
        var copy = await Read<PlaylistCreated>(copied);
        copied.StatusCode.Should().Be(HttpStatusCode.Created);
        var copyUrl = $"/v1/playlists/{copy.PlaylistId}/snapshot";
        var authoredCopy = await Read<PlaylistSnapshot>(
            await Send(HttpMethod.Get, copyUrl, actor: listener)
        );
        authoredCopy.Published.Should().BeFalse();
        authoredCopy.PublicCode.Should().BeNull();
        copy.PlaylistId.Should().NotBe(source.PlaylistId);
        var copiedSegment = authoredCopy.Segments[0];
        var newTrackEntry = copiedSegment.Entries[0].EntryId;
        copiedSegment.SegmentId.Should().NotBe(segment.SegmentId);
        newTrackEntry.Should().NotBe(originalTrack);
        copiedSegment.Entries.Select(entry => entry.TrackId).Should().Equal(Track, null, Track);
        copiedSegment.Entries[1].EntryId.Should().NotBe(block);
        var newTimestamp = $"#entry={newTrackEntry}&t=";
        authoredCopy
            .Metadata.Description.Should()
            .Be($"[Cue]({newTimestamp}2:00) Mention {originalTrack}");
        copiedSegment.Description.Should().Be($"Example: {newTimestamp}2:00");
        copiedSegment.Entries[1].Description.Should().Be($"[Again]({newTimestamp}2:00)");
        copiedSegment.Entries[2].Description.Should().Be($"[First]({newTimestamp}2:00)");
        authoredCopy.Segments[1].Description.Should().Be("Prose-only segment");
        var copyPublication = await Publish(copy.PlaylistId, listener);

        await Moderate("take-down", source.PlaylistId.Value);
        using var takenDown = await _client.GetAsync(publicUrl);
        takenDown.StatusCode.Should().Be(HttpStatusCode.NotFound);
        AssertNoStore(takenDown);
        (await Snapshot(source.PlaylistId)).TakenDown.Should().BeTrue();
        (await SharingLibrary(_user)).Playlists.Single().TakenDown.Should().BeTrue();
        await AssertHiddenFollow(source.PlaylistId, listener);
        await Problem(
            await Send(HttpMethod.Put, $"{publicUrl}/follow", actor: listener),
            404,
            "playlist_not_found"
        );
        await Problem(
            await Send(
                HttpMethod.Post,
                $"{publicUrl}/copy",
                new CopyPublicPlaylistRequest(1, CommandId.New()),
                actor: listener
            ),
            404,
            "playlist_not_found"
        );
        await Problem(
            await Send(
                HttpMethod.Put,
                $"/v1/playlists/{source.PlaylistId}/publication",
                new SetPlaylistPublicationRequest(1, true)
            ),
            409,
            "playlist_taken_down"
        );
        await Problem(
            await Send(HttpMethod.Post, $"{publicUrl}/copy", copyCommand),
            409,
            "idempotency_conflict"
        );
        using var replay = await Send(
            HttpMethod.Post,
            $"{publicUrl}/copy",
            copyCommand,
            actor: listener
        );
        replay.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Read<PlaylistCreated>(replay)).Should().Be(copy);
        (await _client.GetAsync($"/v1/public-playlists/{copyPublication.PublicCode}"))
            .StatusCode.Should()
            .Be(HttpStatusCode.OK);

        await Moderate("lift-takedown", source.PlaylistId.Value);
        (await _client.GetAsync(publicUrl)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await SharingLibrary(listener)).FollowedPlaylists.Single().Available.Should().BeTrue();
        await new AccountPurge(_database.ConnectionString).RunForUserAsync(
            _user.Value,
            CancellationToken.None
        );
        await AssertHiddenFollow(source.PlaylistId, listener);
        (await _client.GetAsync(publicUrl)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (
            await Read<PlaylistCreated>(
                await Send(HttpMethod.Post, $"{publicUrl}/copy", copyCommand, actor: listener)
            )
        )
            .Should()
            .Be(copy);
    }

    [Test]
    public async Task Copy_waiting_for_a_takedown_observes_the_committed_visibility()
    {
        var playlist = await Create(1);
        var published = await Publish(playlist.PlaylistId);
        var listener = await NewSharingListener();
        var copyCommand = new CopyPublicPlaylistRequest(1, CommandId.New());

        await using var moderator = _database.CreateContext();
        await using var transaction = await moderator.Database.BeginTransactionAsync();
        // Hold the same row lock used by the operator until PostgreSQL lets us
        // observe whether an in-flight copy has reached that boundary.
        var moderatorPid = await moderator
            .Database.SqlQuery<int>($"""SELECT pg_backend_pid() AS "Value" """)
            .SingleAsync();
        await moderator
            .Playlists.Where(row => row.Id == playlist.PlaylistId)
            .ExecuteUpdateAsync(update => update.SetProperty(row => row.TakenDown, true));

        var attempt = Send(
            HttpMethod.Post,
            $"/v1/public-playlists/{published.PublicCode}/copy",
            copyCommand,
            actor: listener
        );
        await using var observer = _database.CreateContext();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (
            !attempt.IsCompleted
            && !await observer
                .Database.SqlQuery<bool>(
                    $"""
                    SELECT EXISTS (
                        SELECT FROM pg_stat_activity
                        WHERE datname = current_database() AND {moderatorPid} = ANY(pg_blocking_pids(pid))
                    ) AS "Value"
                    """
                )
                .SingleAsync(timeout.Token)
        )
        {
            await Task.Delay(20, timeout.Token);
        }

        attempt.IsCompleted.Should().BeFalse("the copy must wait for the source lock");
        await transaction.CommitAsync();
        await Problem(await attempt, 404, "playlist_not_found");
        (await SharingLibrary(listener)).Playlists.Should().BeEmpty();
    }

    [Test]
    public async Task Follow_membership_is_paged_and_unchanged_polls_see_owner_edits_without_fanout()
    {
        var playlist = await Create(1);
        var published = await Publish(playlist.PlaylistId);
        var listener = await NewSharingListener();
        var before = await SharingLibrary(listener);
        var follow = $"/v1/public-playlists/{published.PublicCode}/follow";
        (await Send(HttpMethod.Put, follow, actor: listener))
            .StatusCode.Should()
            .Be(HttpStatusCode.NoContent);
        (await Send(HttpMethod.Put, follow, actor: listener))
            .StatusCode.Should()
            .Be(HttpStatusCode.NoContent);

        var current = await SharingLibrary(listener);
        current.LibraryRevision.Should().Be(before.LibraryRevision + 1);
        var edit = Op("update_metadata", new UpdatePlaylistMetadata(new(true, "Public edit")));
        await Apply(playlist.PlaylistId, new(1, [edit]));
        var unchanged = await PullSharingLibrary(listener, current.NextCursor);
        unchanged.LibraryRevision.Should().Be(current.LibraryRevision);
        unchanged.Changes.Should().BeEmpty();
        unchanged.FollowedPlaylists!.Single().Name.Should().Be("Public edit");
        unchanged.FollowedPlaylists!.Single().Revision.Should().Be(2);

        var favorites = Enumerable
            .Range(0, 499)
            .Select(_ => new FavoriteMutationRequestItem(
                MutationId.New(),
                new(FavoriteCatalogTypes.Track, UuidTypeId.Format("track", Guid.NewGuid())),
                "favorite",
                FavoriteId.New()
            ))
            .ToArray();
        var batch = new FavoriteMutationBatchRequest(1, favorites);
        using var padding = await Send(
            HttpMethod.Post,
            "/v1/library/favorite-mutations:batch",
            batch,
            actor: listener
        );
        padding.IsSuccessStatusCode.Should().BeTrue(await padding.Content.ReadAsStringAsync());

        await ReadResponse<PlaylistPublication>(
            HttpMethod.Put,
            $"/v1/playlists/{playlist.PlaylistId}/publication",
            new SetPlaylistPublicationRequest(1, false)
        );
        await AssertHiddenFollow(playlist.PlaylistId, listener);
        var unfollow = $"/v1/playlists/{playlist.PlaylistId}/follow";
        (await Send(HttpMethod.Delete, unfollow, actor: listener))
            .StatusCode.Should()
            .Be(HttpStatusCode.NoContent);
        (await Send(HttpMethod.Delete, unfollow, actor: listener))
            .StatusCode.Should()
            .Be(HttpStatusCode.NoContent);

        var firstPage = await PullSharingLibrary(listener, before.NextCursor);
        firstPage.HasMore.Should().BeTrue();
        firstPage.FollowedPlaylists.Should().BeNull();
        firstPage.Changes.First().ChangeType.Should().Be("playlist_followed");
        firstPage.Changes.First().FollowedPlaylistId.Should().Be(playlist.PlaylistId);
        var lastPage = await PullSharingLibrary(listener, firstPage.NextCursor);
        lastPage.HasMore.Should().BeFalse();
        lastPage.FollowedPlaylists.Should().BeEmpty();
        lastPage.Changes.Single().ChangeType.Should().Be("playlist_unfollowed");
        lastPage.Changes.Single().FollowedPlaylistId.Should().Be(playlist.PlaylistId);
    }

    [Test]
    public async Task Reports_are_rate_limited_and_only_the_operator_changes_visibility()
    {
        var targets = await SeedPublicPlaylists(10);
        var reporter = await NewSharingListener();
        var report = new ReportPublicPlaylistRequest("Please review the description");
        string ReportsPath(Playlist target) => $"/v1/public-playlists/{target.PublicCode}/reports";
        (
            await Send(
                HttpMethod.Post,
                ReportsPath(targets[0]),
                report,
                actor: reporter,
                readOnly: true
            )
        )
            .StatusCode.Should()
            .Be(HttpStatusCode.Forbidden);

        foreach (
            var attempt in await Task.WhenAll(
                Send(HttpMethod.Post, ReportsPath(targets[0]), report, actor: reporter),
                Send(HttpMethod.Post, ReportsPath(targets[0]), report, actor: reporter)
            )
        )
        {
            attempt.StatusCode.Should().Be(HttpStatusCode.NoContent);
        }

        using var openReports = JsonDocument.Parse(await Moderate("list-reports"));
        var open = openReports
            .RootElement.EnumerateArray()
            .Single(row => row.GetProperty("user_id").GetString() == reporter.Value);
        open.GetProperty("reason").GetString().Should().Be(report.Reason);
        await Moderate("close-report", open.GetProperty("id").GetString()!);

        foreach (var target in targets.Take(9))
        {
            (await Send(HttpMethod.Post, ReportsPath(target), report, actor: reporter))
                .StatusCode.Should()
                .Be(HttpStatusCode.NoContent);
        }
        await Problem(
            await Send(HttpMethod.Post, ReportsPath(targets[9]), report, actor: reporter),
            429,
            "playlist_report_rate_limited"
        );
        (await _client.GetAsync($"/v1/public-playlists/{targets[0].PublicCode}"))
            .StatusCode.Should()
            .Be(HttpStatusCode.OK);
        (await Snapshot(targets[0].Id)).TakenDown.Should().BeFalse();
    }

    [Test]
    public async Task Unchanged_library_poll_returns_all_500_heads_and_the_next_follow_is_refused()
    {
        var sources = await SeedPublicPlaylists(501);
        var listener = await NewSharingListener();
        await using (var database = _database.CreateContext())
        {
            database.PlaylistFollows.AddRange(
                sources
                    .Take(500)
                    .Select(playlist => new PlaylistFollow
                    {
                        UserId = listener,
                        PlaylistId = playlist.Id,
                        CreatedAt = DateTimeOffset.UtcNow,
                    })
            );
            await database.SaveChangesAsync();
        }
        var snapshot = await SharingLibrary(listener);
        snapshot.FollowedPlaylists.Should().HaveCount(500);
        await Problem(
            await Send(
                HttpMethod.Put,
                $"/v1/public-playlists/{sources[^1].PublicCode}/follow",
                actor: listener
            ),
            422,
            "quota_exceeded"
        );

        var unchanged = await PullSharingLibrary(listener, snapshot.NextCursor);
        unchanged.HasMore.Should().BeFalse();
        unchanged.Changes.Should().BeEmpty();
        unchanged.FollowedPlaylists.Should().BeEquivalentTo(snapshot.FollowedPlaylists);
    }

    [Test]
    public async Task Owner_cannot_follow_their_own_playlist()
    {
        var playlist = await Create(1);
        var published = await Publish(playlist.PlaylistId);

        await Problem(
            await Send(HttpMethod.Put, $"/v1/public-playlists/{published.PublicCode}/follow"),
            422,
            "own_playlist"
        );
        (await SharingLibrary(_user)).FollowedPlaylists.Should().BeEmpty();
    }

    [Test]
    public async Task Owner_pending_deletion_hides_the_public_playlist_and_its_follows()
    {
        var playlist = await Create(1);
        var published = await Publish(playlist.PlaylistId);
        var publicUrl = $"/v1/public-playlists/{published.PublicCode}";
        var listener = await NewSharingListener();
        (await Send(HttpMethod.Put, $"{publicUrl}/follow", actor: listener))
            .StatusCode.Should()
            .Be(HttpStatusCode.NoContent);

        // The owner row and playlist stay until the purge runs; only the status changes.
        await using (var database = _database.CreateContext())
        {
            await database
                .Users.Where(row => row.Id == _user)
                .ExecuteUpdateAsync(update =>
                    update.SetProperty(row => row.Status, UserStatuses.Deleting)
                );
        }

        (await _client.GetAsync(publicUrl)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        await AssertHiddenFollow(playlist.PlaylistId, listener);
    }

    [Test]
    public async Task A_playlist_bookmark_syncs_before_the_playlist_exists()
    {
        var before = await SharingLibrary(_user);
        var target = new FavoriteTarget("playlist", "playlist_01h455vb4pex5vsknk084sn04j");
        var favoriteId = FavoriteId.New();
        var add = new FavoriteMutationRequestItem(
            MutationId.New(),
            target,
            FavoriteDesiredStates.Favorite,
            favoriteId
        );
        var addBatch = new FavoriteMutationBatchRequest(1, [add]);

        var added = await ReadResponse<FavoriteMutationBatchResponse>(
            HttpMethod.Post,
            FavoriteMutationsPath,
            addBatch
        );
        var replay = await ReadResponse<FavoriteMutationBatchResponse>(
            HttpMethod.Post,
            FavoriteMutationsPath,
            addBatch
        );
        replay.Should().BeEquivalentTo(added);

        var snapshot = await SharingLibrary(_user);
        var bookmark = snapshot.Favorites.Should().ContainSingle().Which;
        bookmark.FavoriteId.Should().Be(favoriteId);
        bookmark.Target.Should().Be(target);

        var remove = new FavoriteMutationRequestItem(
            MutationId.New(),
            target,
            FavoriteDesiredStates.NotFavorite,
            null
        );
        using var removed = await Send(
            HttpMethod.Post,
            FavoriteMutationsPath,
            new FavoriteMutationBatchRequest(1, [remove])
        );
        removed.StatusCode.Should().Be(HttpStatusCode.OK);
        (await SharingLibrary(_user)).Favorites.Should().BeEmpty();

        var changes = await PullSharingLibrary(_user, before.NextCursor);
        changes
            .Changes.Select(change => (change.ChangeType, change.FavoriteId, change.Target))
            .Should()
            .Equal(
                (FavoriteChangeTypes.Added, favoriteId, target),
                (FavoriteChangeTypes.Removed, favoriteId, target)
            );
    }

    [Test]
    public async Task Bookmarking_a_private_playlist_keeps_it_private()
    {
        var playlistId = (await Create(1)).PlaylistId;
        var listener = await NewSharingListener();
        var target = new FavoriteTarget(FavoriteCatalogTypes.Playlist, playlistId.Value);
        var add = new FavoriteMutationBatchRequest(
            1,
            [new(MutationId.New(), target, FavoriteDesiredStates.Favorite, FavoriteId.New())]
        );

        using var response = await Send(
            HttpMethod.Post,
            FavoriteMutationsPath,
            add,
            actor: listener
        );
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var snapshot = await SharingLibrary(listener);
        snapshot.Favorites.Should().ContainSingle().Which.Target.Should().Be(target);
        snapshot.Playlists.Should().BeEmpty();
        snapshot.FollowedPlaylists.Should().BeEmpty();
        (await Send(HttpMethod.Get, $"/v1/playlists/{playlistId}/snapshot", actor: listener))
            .StatusCode.Should()
            .Be(HttpStatusCode.NotFound);

        var ownerView = await Snapshot(playlistId);
        ownerView.Published.Should().BeFalse();
        ownerView.PublicCode.Should().BeNull();
    }

    private Task<UserId> NewSharingListener() =>
        _database.CreateUserAsync($"l_{Guid.NewGuid():N}"[..30]);

    private async Task<PlaylistPublication> Publish(PlaylistId id, UserId? actor = null) =>
        await Read<PlaylistPublication>(
            await Send(
                HttpMethod.Put,
                $"/v1/playlists/{id}/publication",
                new SetPlaylistPublicationRequest(1, true),
                actor: actor
            )
        );

    private async Task<FavoriteLibrarySnapshot> SharingLibrary(UserId actor) =>
        await Read<FavoriteLibrarySnapshot>(
            await Send(HttpMethod.Get, "/v1/library/snapshot", actor: actor)
        );

    private async Task<FavoriteLibraryChanges> PullSharingLibrary(UserId actor, string cursor) =>
        await Read<FavoriteLibraryChanges>(
            await Send(
                HttpMethod.Get,
                $"/v1/library/changes?after={Uri.EscapeDataString(cursor)}",
                actor: actor
            )
        );

    private async Task AssertHiddenFollow(PlaylistId id, UserId listener)
    {
        using var response = await Send(HttpMethod.Get, "/v1/library/snapshot", actor: listener);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var head = payload.RootElement.GetProperty("followed_playlists").EnumerateArray().Single();
        head.GetProperty("playlist_id").GetString().Should().Be(id.Value);
        head.GetProperty("available").GetBoolean().Should().BeFalse();
        head.EnumerateObject()
            .Select(property => property.Name)
            .Should()
            .BeEquivalentTo("playlist_id", "available");
    }

    private async Task<Playlist[]> SeedPublicPlaylists(int count)
    {
        await using var database = _database.CreateContext();
        var now = DateTimeOffset.UtcNow;
        var playlists = Enumerable
            .Range(0, count)
            .Select(index => new Playlist
            {
                Id = PlaylistId.New(),
                OwnerId = _user,
                Name = $"Public playlist {index}",
                CreatedAt = now,
                UpdatedAt = now,
                Revision = 1,
                Published = true,
                PublicCode = RandomNumberGenerator.GetHexString(32, lowercase: true),
            })
            .ToArray();
        database.Playlists.AddRange(playlists);
        await database.SaveChangesAsync();
        return playlists;
    }

    private async Task<string> Moderate(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = await PlaylistModerationCommand.RunAsync(
            args,
            _database.CreateContext,
            output,
            error,
            CancellationToken.None
        );
        exit.Should().Be(0, error.ToString());
        return output.ToString();
    }

    private static void AssertNoStore(HttpResponseMessage response)
    {
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        response.Headers.Contains("CDN-Cache-Control").Should().BeFalse();
    }
}
