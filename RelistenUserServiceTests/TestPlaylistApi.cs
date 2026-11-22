using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using Relisten.Accounts.Contracts.Identifiers;
using Relisten.Accounts.Contracts.Library;
using Relisten.Accounts.Contracts.Playlists;
using Relisten.Identifiers;
using RelistenUserService;
using RelistenUserService.Authentication;
using RelistenUserService.Authentication.Authorization;
using RelistenUserService.Identity.Entities;
using RelistenUserService.Persistence;
using RelistenUserService.Playlists;

namespace RelistenUserServiceTests;

[TestFixture]
[NonParallelizable]
public sealed partial class TestPlaylistApi
{
    private readonly PostgresIntegrationDatabase _database = new();
    private string _directory = "";
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private UserId _user;
    private static readonly string Track = TrackId
        .Parse(UuidTypeId.Format("track", Guid.NewGuid()))
        .ToString();

    [OneTimeSetUp]
    public async Task Start()
    {
        await _database.StartAsync();
        await _database.LoadCatalogSchemaAsync();
        _directory = Path.Combine(
            Path.GetTempPath(),
            $"relisten-playlist-proof-{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(_directory);
        _app = await StartApp();
        _client = CreateClient(_app);
    }

    private async Task<WebApplication> StartApp()
    {
        var builder = WebApplication.CreateBuilder(
            new WebApplicationOptions
            {
                EnvironmentName = Environments.Development,
                ContentRootPath = _directory,
                ApplicationName = typeof(AccountsApplication).Assembly.GetName().Name,
            }
        );
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["ACCOUNTS_DATABASE_URL"] = _database.ConnectionString,
                ["ACCOUNTS_LOCK_DATABASE_URL"] = _database.ConnectionString,
                ["AllowedHosts"] = "auth.relisten.net;accounts.relisten.net;relisten.net",
                ["Accounts:Issuer"] = "https://auth.relisten.net/",
                ["Accounts:WebClientSecret"] = "disposable-proof-secret",
                // Successful playlist writes must survive an unavailable search wake-up.
                ["Temporal:Address"] = "127.0.0.1:1",
            }
        );
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder
            .Services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, ProofAuthentication>("proof", _ => { });
        builder.Services.PostConfigure<PolicySchemeOptions>(
            AuthenticationConstants.AccountCredentialScheme,
            options => options.ForwardDefaultSelector = _ => "proof"
        );
        builder
            .Services.AddHttpClient(nameof(PlaylistTrackResolver))
            .ConfigurePrimaryHttpMessageHandler(() => _catalog);
        var app = AccountsApplication.Build(builder);
        await AccountsApplication.InitializeAsync(app, CancellationToken.None);
        await app.StartAsync();
        return app;
    }

    private static HttpClient CreateClient(WebApplication app)
    {
        var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        client.DefaultRequestHeaders.Host = "accounts.relisten.net";
        // Loopback stands in for the trusted ingress that terminates TLS.
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        return client;
    }

    [SetUp]
    public async Task CreateUser()
    {
        _catalog.Reset();
        _user = await _database.CreateUserAsync($"p_{Guid.NewGuid():N}"[..30]);
    }

    [OneTimeTearDown]
    public async Task Stop()
    {
        _client?.Dispose();
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
        await _database.StopAsync();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Test]
    public async Task Read_only_credentials_can_read_but_cannot_change_playlists()
    {
        var create = await Create(1);
        (await Send(HttpMethod.Get, $"/v1/playlists/{create.PlaylistId}/snapshot", readOnly: true))
            .StatusCode.Should()
            .Be(HttpStatusCode.OK);
        (
            await Send(
                HttpMethod.Put,
                $"/v1/playlists/{create.PlaylistId}/archive-state",
                new ArchivePlaylistRequest(1, CommandId.New(), true),
                readOnly: true
            )
        )
            .StatusCode.Should()
            .Be(HttpStatusCode.Forbidden);
        (await Snapshot(create.PlaylistId)).Archived.Should().BeFalse();
    }

    [Test]
    public async Task Create_ordered_edits_retries_and_library_discovery_share_one_committed_result()
    {
        var initialLibrary = await Read<FavoriteLibrarySnapshot>(
            await Send(HttpMethod.Get, "/v1/library/snapshot")
        );
        var create = NewPlaylist(2, 1);
        var first = await Send(HttpMethod.Post, "/v1/playlists", create);
        first.StatusCode.Should().Be(HttpStatusCode.Created);
        (await Send(HttpMethod.Post, "/v1/playlists", create))
            .StatusCode.Should()
            .Be(HttpStatusCode.OK);
        var id = create.PlaylistId;
        var initialChanges = await Changes(id, 1);
        initialChanges.Revisions.Should().BeEmpty();
        initialChanges.HeadRevision.Should().Be(1);
        initialChanges.NextAfterRevision.Should().Be(1);
        initialChanges.HasMore.Should().BeFalse();
        var a = create.Segments[0].Entries[0].EntryId;
        var b = create.Segments[0].Entries[1].EntryId;
        var c = create.Segments[1].Entries[0].EntryId;
        var s1 = create.Segments[0].SegmentId;
        var s2 = create.Segments[1].SegmentId;
        var s3 = PlaylistSegmentId.New();
        var s4 = PlaylistSegmentId.New();
        var e = Entry();
        var operations = new[]
        {
            Op("insert_segment", new InsertPlaylistSegment(s3, [Entry()], BeforeSegmentId: s1)),
            Op(
                "move_segment",
                new MovePlaylistSegment(s3, AfterSegmentId: s2, BeforeSegmentId: s1)
            ),
            Op(
                "insert_entries",
                new InsertPlaylistEntries(s1, [e], AfterEntryId: c, BeforeEntryId: b)
            ),
            Op("move_entry", new MovePlaylistEntry(b, new(SegmentId: s2, AfterEntryId: c))),
            Op(
                "update_metadata",
                new UpdatePlaylistMetadata(new(true, "  Café  "), new(true, null))
            ),
            Op("delete_entry", new DeletePlaylistEntry(a)),
            Op("delete_segment", new DeletePlaylistSegment(s3)),
            Op(
                "move_entry",
                new MovePlaylistEntry(e.EntryId, new(NewSegmentId: s4, AfterSegmentId: s1))
            ),
        };
        var batch = new PlaylistBatchRequest(1, operations);
        var applied = await Read<PlaylistBatchResponse>(
            await Send(HttpMethod.Post, $"/v1/playlists/{id}/operations:batch", batch)
        );
        applied
            .Results.Select(result => result.Status)
            .Should()
            .OnlyContain(status => status == "applied");
        applied.HeadRevision.Should().Be(9);
        var replay = await Read<PlaylistBatchResponse>(
            await Send(HttpMethod.Post, $"/v1/playlists/{id}/operations:batch", batch)
        );
        replay.Should().BeEquivalentTo(applied);
        var snapshot = await Snapshot(id);
        snapshot.Metadata.Name.Should().Be("Café");
        snapshot
            .Segments.SelectMany(segment => segment.Entries)
            .Select(entry => entry.EntryId)
            .Should()
            .Equal(e.EntryId, c, b);
        snapshot
            .Segments.SelectMany(segment => segment.Entries)
            .Select(entry => entry.TrackId)
            .Distinct()
            .Should()
            .ContainSingle();
        var lastSegment = snapshot.Segments[^1];
        var alreadyLast = await Apply(
            id,
            new(
                1,
                [
                    Op(
                        "move_entry",
                        new MovePlaylistEntry(
                            lastSegment.Entries[^1].EntryId,
                            new(SegmentId: lastSegment.SegmentId)
                        )
                    ),
                    Op("move_segment", new MovePlaylistSegment(lastSegment.SegmentId)),
                ]
            )
        );
        alreadyLast
            .Results.Should()
            .OnlyContain(row => row.Status == "no_op" && row.Revision == null);
        alreadyLast.HeadRevision.Should().Be(applied.HeadRevision);

        var changes = await Changes(id, 1);
        changes
            .Revisions.Select(revision => revision.Revision)
            .Should()
            .Equal(2, 3, 4, 5, 6, 7, 8, 9);
        var currentChanges = await Changes(id, applied.HeadRevision);
        currentChanges.Revisions.Should().BeEmpty();
        currentChanges.HeadRevision.Should().Be(applied.HeadRevision);
        currentChanges.NextAfterRevision.Should().Be(applied.HeadRevision);
        currentChanges.HasMore.Should().BeFalse();
        changes
            .Revisions[^1]
            .Changes.Should()
            .SatisfyRespectively(
                change =>
                    change
                        .Should()
                        .BeOfType<PlaylistSegmentUpserted>()
                        .Which.SegmentId.Should()
                        .Be(s4),
                change =>
                    change
                        .Should()
                        .BeOfType<PlaylistEntryUpserted>()
                        .Which.EntryId.Should()
                        .Be(e.EntryId),
                change =>
                    change
                        .Should()
                        .BeOfType<PlaylistSegmentDeleted>()
                        .Which.SegmentId.Should()
                        .Be(s1)
            );
        var library = await Read<FavoriteLibrarySnapshot>(
            await Send(HttpMethod.Get, "/v1/library/snapshot")
        );
        library
            .LibraryRevision.Should()
            .Be(2, "one summary is emitted for a content batch, not each operation");
        library.Playlists.Single().EntryCount.Should().Be(3);
        var feed = await Read<FavoriteLibraryChanges>(
            await Send(HttpMethod.Get, $"/v1/library/changes?after={library.NextCursor}")
        );
        feed.Changes.Should().BeEmpty();
        var discovered = await Read<FavoriteLibraryChanges>(
            await Send(HttpMethod.Get, $"/v1/library/changes?after={initialLibrary.NextCursor}")
        );
        discovered
            .Changes.Select(change => change.ChangeType)
            .Should()
            .Equal("playlist_summary", "playlist_summary");
        discovered.Changes[0].Playlist!.Name.Should().Be("Test playlist");
        discovered.Changes[1].Playlist!.Name.Should().Be("Café");
        discovered
            .Changes.Should()
            .OnlyContain(change => change.FavoriteId == null && change.Target == null);
        (
            await Send(
                HttpMethod.Get,
                $"/v1/playlists/{id}/snapshot",
                actor: await _database.CreateUserAsync($"p_{Guid.NewGuid():N}"[..30])
            )
        )
            .StatusCode.Should()
            .Be(HttpStatusCode.NotFound);
        first.Headers.CacheControl!.NoStore.Should().BeTrue();
    }

    [Test]
    public async Task Guarded_batches_reject_stale_edits_and_replay_committed_operations()
    {
        var created = await Create(1);
        var original = await Snapshot(created.PlaylistId);
        PlaylistBatchRequest Rename(string name) =>
            new(1, [Op("update_metadata", new UpdatePlaylistMetadata(new(true, name)))])
            {
                ExpectedRevision = original.Revision,
            };
        var first = Rename("First edit");
        var stale = Rename("Second edit");

        var applied = await Apply(created.PlaylistId, first);
        applied.Results.Single().Status.Should().Be("applied");

        using var conflict = await Send(
            HttpMethod.Post,
            $"/v1/playlists/{created.PlaylistId}/operations:batch",
            stale
        );
        await Problem(conflict, 409, "revision_conflict");
        (await Apply(created.PlaylistId, first)).Should().BeEquivalentTo(applied);

        var current = await Snapshot(created.PlaylistId);
        current.Revision.Should().Be(applied.HeadRevision);
        current.Metadata.Name.Should().Be("First edit");
        var refreshed = await Apply(
            created.PlaylistId,
            stale with
            {
                ExpectedRevision = current.Revision,
            }
        );
        refreshed.Results.Single().Status.Should().Be("applied");
        (await Snapshot(created.PlaylistId)).Metadata.Name.Should().Be("Second edit");
    }

    [Test]
    public async Task Metadata_edits_with_replayed_inserts_preserve_structure_and_entry_count()
    {
        var create = await Create(2, 1);
        var id = create.PlaylistId;
        var insert = Op(
            "insert_entries",
            new InsertPlaylistEntries(create.Segments[0].SegmentId, [Entry(), Entry()])
        );
        var inserted = await Apply(id, new(1, [insert]));
        var before = await Snapshot(id);
        var rename = Op("update_metadata", new UpdatePlaylistMetadata(new(true, "Renamed")));
        var sameName = Op("update_metadata", new UpdatePlaylistMetadata(new(true, "Renamed")));
        var batch = new PlaylistBatchRequest(1, [insert, rename, sameName]);

        var result = await Apply(id, batch);
        result.Results[0].Should().BeEquivalentTo(inserted.Results[0]);
        result.Results.Select(row => row.Status).Should().Equal("applied", "applied", "no_op");
        result.HeadRevision.Should().Be(3);

        var snapshot = await Snapshot(id);
        snapshot.Metadata.Name.Should().Be("Renamed");
        snapshot
            .Segments.Should()
            .BeEquivalentTo(before.Segments, options => options.WithStrictOrdering());
        var library = await Read<FavoriteLibrarySnapshot>(
            await Send(HttpMethod.Get, "/v1/library/snapshot")
        );
        library.Playlists.Single().EntryCount.Should().Be(5);
        library.Playlists.Single().Revision.Should().Be(3);
        (await Apply(id, batch)).Should().BeEquivalentTo(result);
    }

    [Test]
    public async Task Guide_sync_preserves_text_order_and_prose_after_the_last_track_leaves()
    {
        var movedTrack = Entry() with { Description = "Listen at 2:00" };
        var deletedTrack = Entry();
        var targetTrack = Entry();
        var intro = new PlaylistSegmentInput(PlaylistSegmentId.New(), [movedTrack], "Introduction");
        var outro = new PlaylistSegmentInput(PlaylistSegmentId.New(), [deletedTrack], "Afterword");
        var target = new PlaylistSegmentInput(PlaylistSegmentId.New(), [targetTrack]);
        var create = NewPlaylist() with { Segments = [intro, outro, target] };
        var id = create.PlaylistId;
        var libraryBefore = await ReadResponse<FavoriteLibrarySnapshot>(
            HttpMethod.Get,
            "/v1/library/snapshot"
        );
        (await Send(HttpMethod.Post, "/v1/playlists", create))
            .StatusCode.Should()
            .Be(HttpStatusCode.Created);
        var baseline = await Snapshot(id);

        var block = new PlaylistEntryInput(PlaylistEntryId.New(), Description: "Between tracks");
        var link = $"[Hear the transition](#entry={movedTrack.EntryId}&t=2:00)";
        var interlude = PlaylistSegmentId.New();
        var segmentEdit = Op(
            "update_segment",
            new UpdatePlaylistSegment(intro.SegmentId, new(true, "Start here"))
        );
        var entryEdit = Op(
            "update_entry",
            new UpdatePlaylistEntry(block.EntryId, new(true, $"  {link}  "))
        );
        var batch = new PlaylistBatchRequest(
            1,
            [
                Op(
                    "insert_segment",
                    new InsertPlaylistSegment(interlude, [], Description: "Interlude")
                ),
                Op(
                    "insert_entries",
                    new InsertPlaylistEntries(
                        target.SegmentId,
                        [block],
                        BeforeEntryId: targetTrack.EntryId
                    )
                ),
                segmentEdit,
                entryEdit,
                Op(
                    "move_entry",
                    new MovePlaylistEntry(
                        movedTrack.EntryId,
                        new(SegmentId: target.SegmentId, AfterEntryId: block.EntryId)
                    )
                ),
                Op("delete_entry", new DeletePlaylistEntry(deletedTrack.EntryId)),
            ]
        );
        var applied = await Apply(id, batch);
        applied
            .Results.Select(result => result.Status)
            .Should()
            .OnlyContain(status => status == "applied");
        var afterMove = await Snapshot(id);
        afterMove
            .Segments.Single(segment => segment.SegmentId == intro.SegmentId)
            .Should()
            .BeEquivalentTo(
                new { Description = "Start here", Entries = Array.Empty<PlaylistEntrySnapshot>() }
            );
        afterMove
            .Segments.Single(segment => segment.SegmentId == outro.SegmentId)
            .Should()
            .BeEquivalentTo(
                new { Description = "Afterword", Entries = Array.Empty<PlaylistEntrySnapshot>() }
            );
        var ordered = afterMove
            .Segments.Single(segment => segment.SegmentId == target.SegmentId)
            .Entries;
        ordered
            .Select(entry => entry.EntryId)
            .Should()
            .Equal(block.EntryId, movedTrack.EntryId, targetTrack.EntryId);
        ordered[0].TrackId.Should().BeNull();
        ordered[0].Description.Should().Be(link);
        ordered[1].Description.Should().Be("Listen at 2:00");

        var replay = await Apply(id, new(1, [segmentEdit, entryEdit]));
        replay.Results.Should().Equal(applied.Results[2], applied.Results[3]);
        var changed = entryEdit with
        {
            Payload = new UpdatePlaylistEntry(block.EntryId, new(true, "Different text")),
        };
        await Problem(
            await Send(
                HttpMethod.Post,
                $"/v1/playlists/{id}/operations:batch",
                new PlaylistBatchRequest(1, [changed])
            ),
            409,
            "idempotency_conflict"
        );

        await Apply(
            id,
            new(
                1,
                [
                    Op(
                        "update_segment",
                        new UpdatePlaylistSegment(intro.SegmentId, new(true, null))
                    ),
                    Op(
                        "update_entry",
                        new UpdatePlaylistEntry(movedTrack.EntryId, new(true, null))
                    ),
                ]
            )
        );
        var final = await Snapshot(id);
        final
            .Segments.Select(segment => segment.SegmentId)
            .Should()
            .Equal(outro.SegmentId, target.SegmentId, interlude);
        var changes = await Changes(id, 1);
        changes.Revisions[^2].Changes.Should().Equal(new PlaylistSegmentDeleted(intro.SegmentId));
        ReplayStructure(baseline, changes)
            .Should()
            .BeEquivalentTo(final.Segments, options => options.WithStrictOrdering());

        var library = await ReadResponse<FavoriteLibrarySnapshot>(
            HttpMethod.Get,
            "/v1/library/snapshot"
        );
        var summary = library.Playlists.Single();
        summary.EntryCount.Should().Be(3);
        summary.TrackCount.Should().Be(2);
        var list = await ReadResponse<PlaylistList>(HttpMethod.Get, "/v1/playlists");
        list.Playlists.Single().Should().BeEquivalentTo(summary);
        var feed = await ReadResponse<FavoriteLibraryChanges>(
            HttpMethod.Get,
            $"/v1/library/changes?after={libraryBefore.NextCursor}"
        );
        feed.Changes.Last().Playlist.Should().BeEquivalentTo(summary);
    }

    [Test]
    public async Task Segment_insert_retry_distinguishes_descriptions_from_anchors()
    {
        var create = await Create(1, 1, 1);
        var left = create.Segments[0].SegmentId;
        var middle = create.Segments[1].SegmentId;
        var right = create.Segments[2].SegmentId;
        var track = Entry();
        // These are the same three strings in different fields of the same insert.
        var insert = new InsertPlaylistSegment(
            PlaylistSegmentId.New(),
            [track with { Description = left.Value }],
            AfterSegmentId: middle,
            BeforeSegmentId: right
        );
        var operation = Op("insert_segment", insert);
        var first = await Apply(create.PlaylistId, new(1, [operation]));
        first.Results.Single().Status.Should().Be("applied");

        var changed = operation with
        {
            Payload = insert with
            {
                Entries = [track],
                AfterSegmentId = left,
                BeforeSegmentId = middle,
                Description = right.Value,
            },
        };
        await Problem(
            await Send(
                HttpMethod.Post,
                $"/v1/playlists/{create.PlaylistId}/operations:batch",
                new PlaylistBatchRequest(1, [changed])
            ),
            409,
            "idempotency_conflict"
        );
    }

    [TestCase(null)]
    [TestCase("  ")]
    public async Task Clearing_a_text_block_refuses_the_batch_and_allows_a_corrected_retry(
        string? text
    )
    {
        var entry = new PlaylistEntryInput(PlaylistEntryId.New(), Description: "Text");
        var segment = new PlaylistSegmentInput(PlaylistSegmentId.New(), [entry]);
        var create = NewPlaylist() with { Segments = [segment] };
        (await Send(HttpMethod.Post, "/v1/playlists", create))
            .StatusCode.Should()
            .Be(HttpStatusCode.Created);
        var id = create.PlaylistId;
        var rename = Op("update_metadata", new UpdatePlaylistMetadata(new(true, "Renamed")));
        var update = Op("update_entry", new UpdatePlaylistEntry(entry.EntryId, new(true, text)));
        await Problem(
            await Send(
                HttpMethod.Post,
                $"/v1/playlists/{id}/operations:batch",
                new PlaylistBatchRequest(1, [rename, update])
            ),
            422,
            "invalid_playlist_operation"
        );
        (await Snapshot(id)).Revision.Should().Be(1);

        var corrected = update with
        {
            Payload = new UpdatePlaylistEntry(entry.EntryId, new(true, "Replacement")),
        };
        var applied = await Apply(id, new(1, [rename, corrected]));
        applied.Results.Select(result => result.Status).Should().Equal("applied", "applied");
        (await Snapshot(id))
            .Segments.Single()
            .Entries.Single()
            .Description.Should()
            .Be("Replacement");
    }

    [Test]
    public async Task Guide_quotas_check_the_final_state_and_count_normalized_utf8_bytes()
    {
        // Fifty descriptions of 2,000 two-byte characters exactly fill 200,000 UTF-8 bytes.
        var longDescription = new string('é', 2_000);
        var segments = Enumerable
            .Range(0, 50)
            .Select(_ => new PlaylistSegmentInput(PlaylistSegmentId.New(), [], longDescription))
            .ToArray();
        var create = NewPlaylist() with { Segments = segments };
        (await Send(HttpMethod.Post, "/v1/playlists", create))
            .StatusCode.Should()
            .Be(HttpStatusCode.Created);
        var id = create.PlaylistId;
        var addition = Op(
            "insert_segment",
            new InsertPlaylistSegment(PlaylistSegmentId.New(), [], Description: "x")
        );
        await Problem(
            await Send(
                HttpMethod.Post,
                $"/v1/playlists/{id}/operations:batch",
                new PlaylistBatchRequest(1, [addition])
            ),
            422,
            "quota_exceeded"
        );
        var result = await Apply(
            id,
            new(
                1,
                [addition, Op("delete_segment", new DeletePlaylistSegment(segments[0].SegmentId))]
            )
        );
        result.Results.Select(row => row.Status).Should().Equal("applied", "applied");

        var prose = Enumerable
            .Range(0, 101)
            .Select(_ => new PlaylistSegmentInput(PlaylistSegmentId.New(), [], "Text"))
            .ToArray();
        var tooMany = NewPlaylist() with { Segments = prose };
        await Problem(await Send(HttpMethod.Post, "/v1/playlists", tooMany), 422, "quota_exceeded");
        var exact = tooMany with { Segments = prose[..100] };
        (await Send(HttpMethod.Post, "/v1/playlists", exact))
            .StatusCode.Should()
            .Be(HttpStatusCode.Created);
    }

    [Test]
    public async Task Semantic_rejections_continue_and_tombstones_and_receipts_never_resurrect()
    {
        var create = await Create(1);
        var id = create.PlaylistId;
        var segment = create.Segments[0];
        var deleted = segment.Entries[0];
        var operations = new[]
        {
            Op("delete_entry", new DeletePlaylistEntry(deleted.EntryId)),
            Op("insert_entries", new InsertPlaylistEntries(segment.SegmentId, [Entry()])),
            Op("insert_segment", new InsertPlaylistSegment(PlaylistSegmentId.New(), [deleted])),
            Op("delete_entry", new DeletePlaylistEntry(PlaylistEntryId.New())),
            Op(
                "move_entry",
                new MovePlaylistEntry(deleted.EntryId, new(NewSegmentId: segment.SegmentId))
            ),
            Op("update_metadata", new UpdatePlaylistMetadata(new(true, "Still applied"))),
        };
        var request = new PlaylistBatchRequest(1, operations);
        var result = await Apply(id, request);
        result
            .Results.Select(row => row.Status)
            .Should()
            .Equal(
                "applied",
                "parent_missing",
                "id_conflict",
                "target_missing",
                "target_deleted",
                "applied"
            );
        (await Apply(id, request)).Results.Should().BeEquivalentTo(result.Results);
        (await Snapshot(id)).Segments.Should().BeEmpty();
        var conflict = await Send(
            HttpMethod.Post,
            $"/v1/playlists/{id}/operations:batch",
            new PlaylistBatchRequest(
                1,
                [operations[0] with { Payload = new DeletePlaylistEntry(PlaylistEntryId.New()) }]
            )
        );
        await Problem(conflict, 409, "idempotency_conflict");
    }

    [Test]
    public async Task Archive_preserves_content_and_answers_only_all_replay_batches()
    {
        var create = await Create(1);
        var id = create.PlaylistId;
        var operation = Op("update_metadata", new UpdatePlaylistMetadata(new(true, "Edited")));
        var result = await Apply(id, new(1, [operation]));
        var archive = new ArchivePlaylistRequest(1, CommandId.New(), true);
        var response = await Read<PlaylistArchived>(
            await Send(HttpMethod.Put, $"/v1/playlists/{id}/archive-state", archive)
        );
        response.Changed.Should().BeTrue();
        (
            await Read<PlaylistArchived>(
                await Send(HttpMethod.Put, $"/v1/playlists/{id}/archive-state", archive)
            )
        )
            .Should()
            .BeEquivalentTo(response);
        (await Snapshot(id)).Revision.Should().Be(result.HeadRevision);
        (await Apply(id, new(1, [operation]))).Results.Should().BeEquivalentTo(result.Results);
        await Problem(
            await Send(
                HttpMethod.Post,
                $"/v1/playlists/{id}/operations:batch",
                new PlaylistBatchRequest(
                    1,
                    [
                        operation,
                        Op("update_metadata", new UpdatePlaylistMetadata(new(true, "Refused"))),
                    ]
                )
            ),
            409,
            "playlist_archived"
        );
        (await Read<PlaylistList>(await Send(HttpMethod.Get, "/v1/playlists")))
            .Playlists.Should()
            .BeEmpty();
        (await Read<PlaylistList>(await Send(HttpMethod.Get, "/v1/playlists?view=archived")))
            .Playlists.Single()
            .PlaylistId.Should()
            .Be(id);
        await Send(
            HttpMethod.Put,
            $"/v1/playlists/{id}/archive-state",
            new ArchivePlaylistRequest(1, CommandId.New(), false)
        );
        (await Snapshot(id)).Archived.Should().BeFalse();
    }

    [Test]
    public async Task Full_playlist_quota_correction_keeps_the_destination_live()
    {
        var create = await Create(1, 4999);
        var id = create.PlaylistId;
        var segment = create.Segments[0];
        var added = Entry();
        var insert = Op("insert_entries", new InsertPlaylistEntries(segment.SegmentId, [added]));
        await Problem(
            await Send(
                HttpMethod.Post,
                $"/v1/playlists/{id}/operations:batch",
                new PlaylistBatchRequest(1, [insert])
            ),
            422,
            "quota_exceeded"
        );
        (await Snapshot(id)).Revision.Should().Be(1);
        var corrected = await Apply(
            id,
            new(
                1,
                [insert, Op("delete_entry", new DeletePlaylistEntry(segment.Entries[0].EntryId))]
            )
        );
        corrected.Results.Select(row => row.Status).Should().Equal("applied", "applied");
        var snapshot = await Snapshot(id);
        snapshot.Segments[0].SegmentId.Should().Be(segment.SegmentId);
        snapshot.Segments[0].Entries.Single().EntryId.Should().Be(added.EntryId);
        snapshot.Segments.Sum(row => row.Entries.Count).Should().Be(5000);
    }

    [Test]
    public async Task Rebalance_respects_real_unique_indexes_and_is_not_split_across_pages()
    {
        var create = await Create(600);
        var id = create.PlaylistId;
        var segment = create.Segments[0];
        await using (var db = _database.CreateContext())
        {
            await db
                .PlaylistEntries.Where(row => row.Id == segment.Entries[1].EntryId)
                .ExecuteUpdateAsync(update =>
                    update.SetProperty(row => row.Rank, "a0" + new string('0', 45) + "1")
                );
        }
        var result = await Apply(
            id,
            new(
                1,
                [
                    Op(
                        "insert_entries",
                        new InsertPlaylistEntries(
                            segment.SegmentId,
                            [Entry()],
                            segment.Entries[0].EntryId,
                            segment.Entries[1].EntryId
                        )
                    ),
                ]
            )
        );
        result.Results.Single().Revision.Should().Be(3);
        var first = await Changes(id, 1);
        first.Revisions.Single().Kind.Should().Be("rebalance");
        first.Revisions.Single().Changes.Should().HaveCount(600);
        first.NextAfterRevision.Should().Be(2);
        first.HasMore.Should().BeTrue();
        (await Changes(id, 2)).Revisions.Single().Kind.Should().Be("content");
        (await Snapshot(id))
            .Segments.Single()
            .Entries.Should()
            .OnlyContain(entry => entry.Rank.Length <= 48);
    }

    [Test]
    public async Task One_batch_can_swap_the_first_entries_of_two_segments()
    {
        var create = await Create(2, 2);
        var firstSegment = create.Segments[0];
        var secondSegment = create.Segments[1];
        var firstMoved = firstSegment.Entries[0].EntryId;
        var secondMoved = secondSegment.Entries[0].EntryId;
        var firstAnchor = firstSegment.Entries[1].EntryId;
        var secondAnchor = secondSegment.Entries[1].EntryId;

        // Park the first entry at the end of the second segment, then fill the two
        // vacated first positions. The final rows exchange unique (segment_id, rank) keys.
        var parkFirst = new MovePlaylistEntry(
            firstMoved,
            new(SegmentId: secondSegment.SegmentId, AfterEntryId: secondAnchor)
        );
        var placeSecond = new MovePlaylistEntry(
            secondMoved,
            new(SegmentId: firstSegment.SegmentId, BeforeEntryId: firstAnchor)
        );
        var placeFirst = new MovePlaylistEntry(
            firstMoved,
            new(SegmentId: secondSegment.SegmentId, BeforeEntryId: secondAnchor)
        );
        var batch = new PlaylistBatchRequest(
            1,
            [
                Op("move_entry", parkFirst),
                Op("move_entry", placeSecond),
                Op("move_entry", placeFirst),
            ]
        );

        var result = await Apply(create.PlaylistId, batch);
        var snapshot = await Snapshot(create.PlaylistId);

        result
            .Results.Select(operation => operation.Status)
            .Should()
            .Equal("applied", "applied", "applied");
        snapshot
            .Segments.SelectMany(segment =>
                segment.Entries.Select(entry => (segment.SegmentId, entry.EntryId))
            )
            .Should()
            .Equal(
                (firstSegment.SegmentId, secondMoved),
                (firstSegment.SegmentId, firstAnchor),
                (secondSegment.SegmentId, firstMoved),
                (secondSegment.SegmentId, secondAnchor)
            );
    }

    [Test]
    public async Task Restore_generation_detects_equal_revisions_and_wraps_surviving_receipts()
    {
        var create = await Create(1);
        var id = create.PlaylistId;
        await Apply(
            id,
            new(1, [Op("update_metadata", new UpdatePlaylistMetadata(new(true, "Lost branch")))])
        );
        var before = await Read<FavoriteLibrarySnapshot>(
            await Send(HttpMethod.Get, "/v1/library/snapshot")
        );
        var generation = Guid.CreateVersion7().ToString();
        await using (var db = _database.CreateContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM user_data.playlist_receipts WHERE playlist_id = {id.Value} AND kind = 'operation'"
            );
            await db.PlaylistRevisions.Where(row => row.PlaylistId == id).ExecuteDeleteAsync();
            await db
                .LibraryChanges.Where(row => row.UserId == _user && row.Revision > 1)
                .ExecuteDeleteAsync();
            await db
                .Playlists.Where(row => row.Id == id)
                .ExecuteUpdateAsync(update => update.SetProperty(row => row.Revision, 1));
            await db
                .LibraryStates.Where(row => row.UserId == _user)
                .ExecuteUpdateAsync(update => update.SetProperty(row => row.Revision, 1));
            await db.HistoryGenerations.ExecuteUpdateAsync(update =>
                update.SetProperty(row => row.Value, generation)
            );
        }
        await Apply(
            id,
            new(
                1,
                [Op("update_metadata", new UpdatePlaylistMetadata(new(true, "Restored branch")))]
            )
        );
        var after = await Read<FavoriteLibrarySnapshot>(
            await Send(HttpMethod.Get, "/v1/library/snapshot")
        );
        after.LibraryRevision.Should().Be(before.LibraryRevision);
        after.HistoryGeneration.Should().Be(generation).And.NotBe(before.HistoryGeneration);
        await Problem(
            await Send(HttpMethod.Get, $"/v1/library/changes?after={before.NextCursor}"),
            410,
            "sync_cursor_expired"
        );
        (await Read<PlaylistCreated>(await Send(HttpMethod.Post, "/v1/playlists", create)))
            .HistoryGeneration.Should()
            .Be(generation);
        (await Snapshot(id)).Metadata.Name.Should().Be("Restored branch");
        await Problem(
            await Send(HttpMethod.Get, $"/v1/playlists/{id}/changes?after_revision=3"),
            410,
            "snapshot_required"
        );
    }

    [Test]
    public async Task Malformed_HTTP_payloads_fail_before_any_effect()
    {
        var create = await Create(1);
        var operationId = PlaylistOperationId.New();
        var body =
            $$$"""{"contract_version":1,"operations":[{"operation_id":"{{{operationId}}}","operation_type":"update_metadata","payload":{}}]}""";
        await Problem(
            await Send(
                HttpMethod.Post,
                $"/v1/playlists/{create.PlaylistId}/operations:batch",
                raw: body
            ),
            422,
            "invalid_playlist_operation"
        );
        var duplicate =
            $$$"""{"contract_version":1,"operations":[{"operation_id":"{{{operationId}}}","base_revision":0,"operation_type":"update_metadata","payload":{"name":"A","name":"B"}}]}""";
        await Problem(
            await Send(
                HttpMethod.Post,
                $"/v1/playlists/{create.PlaylistId}/operations:batch",
                raw: duplicate
            ),
            422,
            "invalid_playlist_operation"
        );
        var unknownPayloadField =
            $$$"""{"contract_version":1,"operations":[{"operation_id":"{{{operationId}}}","base_revision":0,"operation_type":"update_metadata","payload":{"description":null,"extra":1}}]}""";
        await Problem(
            await Send(
                HttpMethod.Post,
                $"/v1/playlists/{create.PlaylistId}/operations:batch",
                raw: unknownPayloadField
            ),
            422,
            "invalid_playlist_operation"
        );
        var wrongIdPrefix = JsonSerializer
            .Serialize(create, PlaylistJson.Options)
            .Replace(create.PlaylistId.Value, create.CommandId.Value, StringComparison.Ordinal);
        await Problem(
            await Send(HttpMethod.Post, "/v1/playlists", raw: wrongIdPrefix),
            422,
            "invalid_playlist_operation"
        );
        await Problem(
            await Send(
                HttpMethod.Post,
                "/v1/playlists",
                raw: "{\"contract_version\":1,\"extra\":true}"
            ),
            422,
            "invalid_playlist_operation"
        );
        (await Snapshot(create.PlaylistId)).Revision.Should().Be(1);
    }

    [Test]
    public async Task Oversized_requests_return_the_transport_size_limit()
    {
        var body =
            JsonSerializer.Serialize(NewPlaylist(), PlaylistJson.Options)
            + new string(' ', 2 * 1024 * 1024);
        var response = await Send(HttpMethod.Post, "/v1/playlists", raw: body);
        response
            .StatusCode.Should()
            .Be(HttpStatusCode.RequestEntityTooLarge, await response.Content.ReadAsStringAsync());
    }

    [Test]
    public async Task Concurrent_creates_serialize_the_shared_account_quota()
    {
        for (var i = 0; i < 4; i++)
        {
            await Create(5000);
        }
        await Create(4999);
        var responses = await Task.WhenAll(
            Send(HttpMethod.Post, "/v1/playlists", NewPlaylist(1)),
            Send(HttpMethod.Post, "/v1/playlists", NewPlaylist(1))
        );
        responses.Select(response => (int)response.StatusCode).Order().Should().Equal(201, 422);
        var library = await Read<FavoriteLibrarySnapshot>(
            await Send(HttpMethod.Get, "/v1/library/snapshot")
        );
        library.Playlists.Sum(row => row.EntryCount).Should().Be(25000);
    }

    [Test]
    public async Task Concurrent_cross_account_ID_reuse_returns_a_conflict_not_a_unique_violation()
    {
        var first = NewPlaylist(1);
        var second = NewPlaylist(1);
        second = second with
        {
            Segments = [new(second.Segments[0].SegmentId, first.Segments[0].Entries)],
        };
        var other = await _database.CreateUserAsync($"p_{Guid.NewGuid():N}"[..30]);
        var responses = await Task.WhenAll(
            Send(HttpMethod.Post, "/v1/playlists", first),
            Send(HttpMethod.Post, "/v1/playlists", second, actor: other)
        );
        responses.Select(response => (int)response.StatusCode).Order().Should().Equal(201, 409);
    }

    [Test]
    public async Task Persistence_failure_rolls_back_direct_rank_staging_and_leaves_no_effects()
    {
        var create = await Create(2);
        var before = await Snapshot(create.PlaylistId);
        var options = new DbContextOptionsBuilder<AccountsDbContext>()
            .UseNpgsql(_database.ConnectionString)
            .AddInterceptors(new RejectSave())
            .Options;
        await using var db = new AccountsDbContext(options);
        var service = new PlaylistWriteService(
            db,
            new(db),
            new(db),
            TimeProvider.System,
            _app.Services.GetRequiredService<PlaylistTrackResolver>()
        );
        var edit = Op(
            "move_entry",
            new MovePlaylistEntry(
                create.Segments[0].Entries[0].EntryId,
                new(SegmentId: create.Segments[0].SegmentId)
            )
        );
        var action = () =>
            service.ApplyAsync(_user, create.PlaylistId, new(1, [edit]), CancellationToken.None);
        await action
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("Injected persistence failure");
        (await Snapshot(create.PlaylistId)).Should().BeEquivalentTo(before);
        await using var check = _database.CreateContext();
        (await check.PlaylistReceipts.AnyAsync(row => row.Id == edit.OperationId.Value))
            .Should()
            .BeFalse();
        (await check.LibraryStates.SingleAsync(row => row.UserId == _user)).Revision.Should().Be(1);
    }

    private async Task<CreatePlaylistRequest> Create(params int[] counts)
    {
        var request = NewPlaylist(counts);
        var response = await Send(HttpMethod.Post, "/v1/playlists", request);
        response
            .StatusCode.Should()
            .Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return request;
    }

    private static CreatePlaylistRequest NewPlaylist(params int[] counts) =>
        new(
            1,
            CommandId.New(),
            PlaylistId.New(),
            new("Test playlist"),
            counts
                .Select(count => new PlaylistSegmentInput(
                    PlaylistSegmentId.New(),
                    Enumerable.Range(0, count).Select(_ => Entry()).ToArray()
                ))
                .ToArray()
        );

    private static PlaylistEntryInput Entry() => new(PlaylistEntryId.New(), Track);

    private static PlaylistSegmentSnapshot[] ReplayStructure(
        PlaylistSnapshot baseline,
        PlaylistChanges changes
    )
    {
        var segments = baseline.Segments.ToDictionary(
            segment => segment.SegmentId,
            segment => new PlaylistSegmentUpserted(
                segment.SegmentId,
                segment.Rank,
                segment.Description
            )
        );
        var entries = baseline
            .Segments.SelectMany(segment =>
                segment.Entries.Select(entry => new PlaylistEntryUpserted(
                    entry.EntryId,
                    segment.SegmentId,
                    entry.TrackId,
                    entry.Rank,
                    entry.Description
                ))
            )
            .ToDictionary(entry => entry.EntryId);

        foreach (var change in changes.Revisions.SelectMany(revision => revision.Changes))
        {
            switch (change)
            {
                case PlaylistSegmentUpserted segment:
                    segments[segment.SegmentId] = segment;
                    break;
                case PlaylistSegmentDeleted segment:
                    segments.Remove(segment.SegmentId);
                    foreach (
                        var entry in entries
                            .Values.Where(entry => entry.SegmentId == segment.SegmentId)
                            .ToArray()
                    )
                    {
                        entries.Remove(entry.EntryId);
                    }
                    break;
                case PlaylistEntryUpserted entry:
                    entries[entry.EntryId] = entry;
                    break;
                case PlaylistEntryDeleted entry:
                    entries.Remove(entry.EntryId);
                    break;
            }
        }

        return segments
            .Values.OrderBy(segment => segment.Rank, StringComparer.Ordinal)
            .Select(segment => new PlaylistSegmentSnapshot(
                segment.SegmentId,
                segment.Rank,
                entries
                    .Values.Where(entry => entry.SegmentId == segment.SegmentId)
                    .OrderBy(entry => entry.Rank, StringComparer.Ordinal)
                    .Select(entry => new PlaylistEntrySnapshot(
                        entry.EntryId,
                        entry.TrackId,
                        entry.Rank,
                        entry.Description
                    ))
                    .ToArray(),
                segment.Description
            ))
            .ToArray();
    }

    private static PlaylistOperation Op(string type, PlaylistOperationPayload payload) =>
        new(PlaylistOperationId.New(), 0, type, payload);

    private Task<PlaylistSnapshot> Snapshot(PlaylistId id) =>
        ReadResponse<PlaylistSnapshot>(HttpMethod.Get, $"/v1/playlists/{id}/snapshot");

    private Task<PlaylistChanges> Changes(PlaylistId id, long after) =>
        ReadResponse<PlaylistChanges>(
            HttpMethod.Get,
            $"/v1/playlists/{id}/changes?after_revision={after}"
        );

    private Task<PlaylistBatchResponse> Apply(PlaylistId id, PlaylistBatchRequest request) =>
        ReadResponse<PlaylistBatchResponse>(
            HttpMethod.Post,
            $"/v1/playlists/{id}/operations:batch",
            request
        );

    private async Task<T> ReadResponse<T>(HttpMethod method, string url, object? body = null) =>
        await Read<T>(await Send(method, url, body));

    private async Task<HttpResponseMessage> Send(
        HttpMethod method,
        string url,
        object? body = null,
        string? raw = null,
        UserId? actor = null,
        bool readOnly = false
    )
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Add("X-Proof-Account", (actor ?? _user).Value);
        if (readOnly)
        {
            request.Headers.Add("X-Proof-Read-Only", "true");
        }
        if (body is not null || raw is not null)
        {
            request.Content = new StringContent(
                raw ?? JsonSerializer.Serialize(body, body!.GetType(), PlaylistJson.Options),
                Encoding.UTF8,
                "application/json"
            );
        }
        return await _client.SendAsync(request);
    }

    private static async Task<T> Read<T>(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        response.IsSuccessStatusCode.Should().BeTrue(text);
        // Read public wire names independently of the server's input formatter options.
        return JsonSerializer.Deserialize<T>(
            text,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }
        )!;
    }

    private static async Task Problem(HttpResponseMessage response, int status, string code)
    {
        var text = await response.Content.ReadAsStringAsync();
        ((int)response.StatusCode).Should().Be(status, text);
        using var document = JsonDocument.Parse(text);
        document.RootElement.GetProperty("code").GetString().Should().Be(code);
        document
            .RootElement.GetProperty("history_generation")
            .GetString()
            .Should()
            .NotBeNullOrEmpty();
    }

    private sealed class RejectSave : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default
        ) => throw new InvalidOperationException("Injected persistence failure");
    }

    // Substitute credential verification; retain production routing, method-aware authorization,
    // middleware, JSON, services, migrations, PostgreSQL constraints and transactions.
    private sealed class ProofAuthentication(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        AccountsDbContext db,
        CurrentAccountContext account
    ) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!UserId.TryParse(Request.Headers["X-Proof-Account"], out var id))
            {
                return AuthenticateResult.NoResult();
            }
            var user = await db.Users.SingleAsync(row => row.Id == id);
            var capabilities =
                Request.Headers["X-Proof-Read-Only"] == "true"
                    ? IdentitySessionCapabilities.LibraryRead
                    : IdentitySessionCapabilities.AllWeb;
            account.SetWeb(user, SessionId.New(), "https://relisten.net", capabilities);
            return AuthenticateResult.Success(
                new(
                    new ClaimsPrincipal(
                        new ClaimsIdentity(
                            [new Claim(ClaimTypes.NameIdentifier, id.Value)],
                            "proof"
                        )
                    ),
                    "proof"
                )
            );
        }
    }
}
