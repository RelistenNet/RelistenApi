using System.Net;
using System.Net.Http.Headers;
using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Relisten.UserApi.Models;
using Relisten.UserApi.Services;
using Relisten.UserApi.Serialization;

namespace RelistenUserApiTests;

[TestFixture]
public class UserLibraryAccountTests
{
    [Test]
    public async Task Export_ShouldReturnUserOwnedDataWithSnakeCaseContract()
    {
        await EnsurePostgresOrIgnore();
        await using var factory = NewFactory();
        using var client = factory.CreateClient();
        var auth = await SignIn(client, deviceId: $"account-export-{Guid.NewGuid():N}");
        var sourceFavoriteUuid = Guid.NewGuid();
        var playlist = await CreatePlaylist(client, auth.AccessToken, "Exported Playlist");
        var playlistEntryUuid = Guid.NewGuid();
        var sourceTrackUuid = Guid.NewGuid();
        var sourceUuid = Guid.NewGuid();
        var clientEventUuid = Guid.NewGuid();
        var historyDeviceId = $"history-export-{Guid.NewGuid():N}";
        var playedAt = DateTimeOffset.Parse("2026-06-20T18:15:00Z");

        await PutFavorite(client, auth.AccessToken, "source", sourceFavoriteUuid);
        await UpdateSettings(
            client,
            auth.AccessToken,
            JObject.Parse("""{ "history_enabled": true, "preferred_source": "archive" }"""));
        await ApplyOperation(
            client,
            auth.AccessToken,
            playlist.PlaylistUuid,
            new PlaylistOperationRequest
            {
                Op = "add_track",
                IdempotencyKey = Guid.NewGuid(),
                EntryUuid = playlistEntryUuid,
                SourceTrackUuid = sourceTrackUuid
            });
        await UploadBatch(
            client,
            auth.AccessToken,
            new PlaybackHistoryBatchRequest
            {
                Events =
                [
                    new PlaybackHistoryEventRequest
                    {
                        ClientEventUuid = clientEventUuid,
                        SourceTrackUuid = sourceTrackUuid,
                        SourceUuid = sourceUuid,
                        PlaylistUuid = playlist.PlaylistUuid,
                        PlaylistEntryUuid = playlistEntryUuid,
                        PlayedAt = playedAt,
                        Platform = "ios",
                        AppVersion = "4.3.0",
                        DeviceId = historyDeviceId
                    }
                ]
            });

        var (response, body) = await ExportAccount(client, auth.AccessToken);
        var export = JsonConvert.DeserializeObject<AccountExportResponse>(
            body,
            UserLibraryJson.SerializerSettings)!;

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        body.Should().Contain("\"playback_history\"");
        body.Should().Contain("\"user_uuid\"");
        body.Should().NotContain("PlaybackHistory");
        body.Should().NotContain("UserUuid");
        export.User.UserUuid.Should().Be(auth.User.UserUuid);
        export.User.ScopeId.Should().Be($"user:{auth.User.UserUuid}");
        export.AuthMethods.Should().ContainSingle(method =>
            method.Provider == "development" &&
            method.ProviderSubject == auth.User.Username);
        export.Sessions.Should().ContainSingle(session =>
            session.SessionUuid == auth.Session.SessionUuid &&
            session.DeviceId == auth.Session.DeviceId &&
            session.Platform == "ios");
        export.Favorites.Should().ContainSingle(favorite =>
            favorite.EntityType == "source" &&
            favorite.EntityUuid == sourceFavoriteUuid &&
            favorite.DeletedAt == null);
        export.Settings.Settings["history_enabled"]!.Value<bool>().Should().BeTrue();
        export.Settings.Settings["preferred_source"]!.Value<string>().Should().Be("archive");
        export.Playlists.Should().ContainSingle(exportedPlaylist =>
            exportedPlaylist.PlaylistUuid == playlist.PlaylistUuid &&
            exportedPlaylist.Name == "Exported Playlist" &&
            exportedPlaylist.Entries.Any(entry =>
                entry.PlaylistEntryUuid == playlistEntryUuid &&
                entry.SourceTrackUuid == sourceTrackUuid));
        export.PlaybackHistory.Should().ContainSingle(history =>
            history.ClientEventUuid == clientEventUuid &&
            history.SourceTrackUuid == sourceTrackUuid &&
            history.SourceUuid == sourceUuid &&
            history.PlaylistUuid == playlist.PlaylistUuid &&
            history.PlaylistEntryUuid == playlistEntryUuid &&
            history.DeviceId == historyDeviceId &&
            history.Platform == "ios" &&
            history.AppVersion == "4.3.0");
    }

    [Test]
    public async Task Delete_ShouldRemoveAccountDataInvalidateTokensAndPreserveSharedPlaylistContent()
    {
        await EnsurePostgresOrIgnore();
        await using var factory = NewFactory();
        using var client = factory.CreateClient();
        var owner = await SignIn(client);
        var deletedUser = await SignIn(client, deviceId: $"account-delete-{Guid.NewGuid():N}");
        var ownerPlaylist = await CreatePlaylist(client, owner.AccessToken, "Owner Playlist");
        var deletedUsersOwnPlaylist = await CreatePlaylist(client, deletedUser.AccessToken, "Deleted User Playlist");
        var sourceFavoriteUuid = Guid.NewGuid();
        var ownBlockUuid = Guid.NewGuid();
        var blockUuid = Guid.NewGuid();
        var entryUuids = new List<Guid> { Guid.NewGuid(), Guid.NewGuid() };
        var sourceTrackUuids = new List<Guid> { Guid.NewGuid(), Guid.NewGuid() };
        var clientEventUuid = Guid.NewGuid();

        await PutFavorite(client, deletedUser.AccessToken, "source", sourceFavoriteUuid);
        await UpdateSettings(
            client,
            deletedUser.AccessToken,
            JObject.Parse("""{ "history_enabled": true, "delete_me": true }"""));
        await ApplyOperation(
            client,
            deletedUser.AccessToken,
            deletedUsersOwnPlaylist.PlaylistUuid,
            new PlaylistOperationRequest
            {
                Op = "add_tracks_as_block",
                IdempotencyKey = Guid.NewGuid(),
                BlockUuid = ownBlockUuid,
                EntryUuids = [Guid.NewGuid(), Guid.NewGuid()],
                SourceTrackUuids = [Guid.NewGuid(), Guid.NewGuid()]
            });
        await UploadBatch(
            client,
            deletedUser.AccessToken,
            new PlaybackHistoryBatchRequest
            {
                Events =
                [
                    new PlaybackHistoryEventRequest
                    {
                        ClientEventUuid = clientEventUuid,
                        SourceTrackUuid = Guid.NewGuid(),
                        SourceUuid = Guid.NewGuid(),
                        PlayedAt = DateTimeOffset.Parse("2026-06-20T19:30:00Z"),
                        Platform = "ios",
                        AppVersion = "4.3.0",
                        DeviceId = $"delete-history-{Guid.NewGuid():N}"
                    }
                ]
            });
        await InviteCollaborator(
            client,
            owner.AccessToken,
            ownerPlaylist.PlaylistUuid,
            deletedUser.User.Username);
        await AcceptInvitation(client, deletedUser.AccessToken, ownerPlaylist.PlaylistUuid);
        await ApplyOperation(
            client,
            deletedUser.AccessToken,
            ownerPlaylist.PlaylistUuid,
            new PlaylistOperationRequest
            {
                Op = "add_tracks_as_block",
                IdempotencyKey = Guid.NewGuid(),
                BlockUuid = blockUuid,
                EntryUuids = entryUuids,
                SourceTrackUuids = sourceTrackUuids
            });

        var deleteResponse = await DeleteAccount(client, deletedUser.AccessToken);
        var staleAccessResponse = await GetCurrentUserResponse(client, deletedUser.AccessToken);
        var staleRefreshResponse = await RefreshResponse(client, deletedUser.RefreshToken);
        var ownerRead = await GetPlaylist(client, owner.AccessToken, ownerPlaylist.PlaylistUuid);
        await using var connection = NewDbService().CreateConnection();
        var counts = await connection.QuerySingleAsync<AccountDeletionCounts>(
            """
            SELECT
                (SELECT count(*)::int FROM user_data.users WHERE id = @DeletedUserUuid) AS "Users",
                (SELECT count(*)::int FROM user_data.user_auth_methods WHERE user_id = @DeletedUserUuid) AS "AuthMethods",
                (SELECT count(*)::int FROM user_data.user_sessions WHERE user_id = @DeletedUserUuid) AS "Sessions",
                (SELECT count(*)::int FROM user_data.refresh_tokens WHERE session_id = @DeletedSessionUuid) AS "RefreshTokens",
                (SELECT count(*)::int FROM user_data.user_favorites WHERE user_id = @DeletedUserUuid) AS "Favorites",
                (SELECT count(*)::int FROM user_data.user_settings WHERE user_id = @DeletedUserUuid) AS "Settings",
                (SELECT count(*)::int FROM user_data.playback_history WHERE user_id = @DeletedUserUuid) AS "PlaybackHistory",
                (SELECT count(*)::int FROM user_data.playback_history_ingest_keys WHERE user_id = @DeletedUserUuid) AS "PlaybackHistoryIngestKeys",
                (SELECT count(*)::int FROM user_data.playlists WHERE owner_id = @DeletedUserUuid) AS "OwnedPlaylists",
                (SELECT count(*)::int FROM user_data.playlist_collaborators WHERE user_id = @DeletedUserUuid OR invited_by = @DeletedUserUuid) AS "Collaborators",
                (SELECT count(*)::int FROM user_data.playlist_entries WHERE added_by = @DeletedUserUuid) AS "EntriesAddedByDeletedUser",
                (SELECT count(*)::int FROM user_data.playlist_blocks WHERE created_by = @DeletedUserUuid) AS "BlocksCreatedByDeletedUser",
                (SELECT count(*)::int FROM user_data.playlist_edit_log WHERE user_id = @DeletedUserUuid) AS "EditLogRows",
                (SELECT count(*)::int FROM user_data.playlists WHERE id = @OwnerPlaylistUuid) AS "OwnerPlaylists",
                (SELECT count(*)::int FROM user_data.playlists WHERE id = @DeletedUserPlaylistUuid) AS "DeletedUserPlaylists",
                (SELECT count(*)::int FROM user_data.playlist_entries WHERE playlist_id = @OwnerPlaylistUuid) AS "OwnerPlaylistEntries",
                (SELECT count(*)::int FROM user_data.playlist_entries WHERE playlist_id = @OwnerPlaylistUuid AND added_by = @OwnerUserUuid) AS "OwnerPlaylistEntriesReassigned",
                (SELECT count(*)::int FROM user_data.playlist_blocks WHERE id = @BlockUuid AND created_by = @OwnerUserUuid) AS "OwnerPlaylistBlocksReassigned"
            """,
            new
            {
                DeletedUserUuid = deletedUser.User.UserUuid,
                DeletedSessionUuid = deletedUser.Session.SessionUuid,
                OwnerUserUuid = owner.User.UserUuid,
                OwnerPlaylistUuid = ownerPlaylist.PlaylistUuid,
                DeletedUserPlaylistUuid = deletedUsersOwnPlaylist.PlaylistUuid,
                BlockUuid = blockUuid
            });

        deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);
        deleteResponse.Headers.CacheControl!.NoStore.Should().BeTrue();
        staleAccessResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        staleRefreshResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        ownerRead.Entries.Should().HaveCount(2);
        ownerRead.Entries.Should().OnlyContain(entry =>
            entry.AddedByUserUuid == owner.User.UserUuid &&
            entry.BlockUuid == blockUuid);
        counts.Users.Should().Be(0);
        counts.AuthMethods.Should().Be(0);
        counts.Sessions.Should().Be(0);
        counts.RefreshTokens.Should().Be(0);
        counts.Favorites.Should().Be(0);
        counts.Settings.Should().Be(0);
        counts.PlaybackHistory.Should().Be(0);
        counts.PlaybackHistoryIngestKeys.Should().Be(0);
        counts.OwnedPlaylists.Should().Be(0);
        counts.Collaborators.Should().Be(0);
        counts.EntriesAddedByDeletedUser.Should().Be(0);
        counts.BlocksCreatedByDeletedUser.Should().Be(0);
        counts.EditLogRows.Should().Be(0);
        counts.OwnerPlaylists.Should().Be(1);
        counts.DeletedUserPlaylists.Should().Be(0);
        counts.OwnerPlaylistEntries.Should().Be(2);
        counts.OwnerPlaylistEntriesReassigned.Should().Be(2);
        counts.OwnerPlaylistBlocksReassigned.Should().Be(1);
    }

    [Test]
    public async Task ExportAndDelete_ShouldRequireRecentReauthentication()
    {
        await EnsurePostgresOrIgnore();
        var fakeProvider = new FakeProviderVerifier();
        await using var factory = NewFactory(fakeProvider);
        using var client = factory.CreateClient();
        var username = UniqueUsername();
        var providerSubject = $"google-subject-{Guid.NewGuid():N}";
        fakeProvider.AddSubject("google", "sign-in-token", providerSubject);
        fakeProvider.AddSubject("google", "reauth-token", providerSubject);
        var auth = await ProviderSignIn(client, "sign-in-token", username);

        await SetSessionReauthenticatedAt(auth.Session.SessionUuid, DateTimeOffset.UtcNow.AddHours(-1));

        var staleExport = await ExportAccount(client, auth.AccessToken);
        var staleDelete = await DeleteAccount(client, auth.AccessToken);
        var userRowsAfterStaleDelete = await CountUserRows(auth.User.UserUuid);

        staleExport.Response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        JObject.Parse(staleExport.Body)["error"]!.Value<string>().Should().Be("recent_reauthentication_required");
        staleDelete.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        JObject.Parse(await staleDelete.Content.ReadAsStringAsync())["error"]!
            .Value<string>()
            .Should()
            .Be("recent_reauthentication_required");
        userRowsAfterStaleDelete.Should().Be(1);

        await Reauthenticate(client, auth.AccessToken, "reauth-token");
        var markerAfterReauth = await LoadSessionReauthenticatedAt(auth.Session.SessionUuid);
        var freshExport = await ExportAccount(client, auth.AccessToken);
        var freshDelete = await DeleteAccount(client, auth.AccessToken);
        var userRowsAfterDelete = await CountUserRows(auth.User.UserUuid);

        markerAfterReauth.Should().NotBeNull();
        markerAfterReauth.Should().BeAfter(DateTime.UtcNow.AddMinutes(-1));
        freshExport.Response.StatusCode.Should().Be(HttpStatusCode.OK, freshExport.Body);
        freshDelete.StatusCode.Should().Be(HttpStatusCode.NoContent);
        userRowsAfterDelete.Should().Be(0);
    }

    private static async Task<AuthTokenResponse> SignIn(
        HttpClient client,
        string? username = null,
        string? deviceId = null)
    {
        return await PostJson<DevelopmentSessionRequest, AuthTokenResponse>(
            client,
            "/api/v3/library/auth/development/session",
            new DevelopmentSessionRequest
            {
                Username = username ?? UniqueUsername(),
                DisplayName = "Account Test User",
                DeviceId = deviceId ?? $"account-test-{Guid.NewGuid():N}",
                DeviceName = "Test Device",
                Platform = "ios"
            },
            accessToken: null,
            expectedStatus: HttpStatusCode.OK);
    }

    private static async Task<AuthTokenResponse> ProviderSignIn(
        HttpClient client,
        string providerToken,
        string username)
    {
        return await PostJson<ProviderSignInRequest, AuthTokenResponse>(
            client,
            "/api/v3/library/auth/callback/google",
            new ProviderSignInRequest
            {
                ProviderToken = providerToken,
                Username = username,
                DisplayName = "Account Provider User",
                DeviceId = $"provider-account-test-{Guid.NewGuid():N}",
                DeviceName = "Test Device",
                Platform = "ios"
            },
            accessToken: null,
            expectedStatus: HttpStatusCode.OK);
    }

    private static async Task<FavoriteResponse> PutFavorite(
        HttpClient client,
        string accessToken,
        string entityType,
        Guid entityUuid)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Put,
            $"/api/v3/library/favorites/{entityType}/{entityUuid}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonConvert.DeserializeObject<FavoriteResponse>(
            body,
            UserLibraryJson.SerializerSettings)!;
    }

    private static async Task<UserSettingsResponse> UpdateSettings(
        HttpClient client,
        string accessToken,
        JObject settings)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, "/api/v3/library/settings");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = JsonContent(new UpdateUserSettingsRequest { Settings = settings });
        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonConvert.DeserializeObject<UserSettingsResponse>(
            body,
            UserLibraryJson.SerializerSettings)!;
    }

    private static async Task<PlaylistResponse> CreatePlaylist(
        HttpClient client,
        string accessToken,
        string name)
    {
        return await PostJson<CreatePlaylistRequest, PlaylistResponse>(
            client,
            "/api/v3/library/playlists",
            new CreatePlaylistRequest
            {
                PlaylistUuid = Guid.NewGuid(),
                Name = name
            },
            accessToken,
            HttpStatusCode.Created);
    }

    private static async Task<PlaylistResponse> GetPlaylist(
        HttpClient client,
        string accessToken,
        Guid playlistUuid)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v3/library/playlists/{playlistUuid}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonConvert.DeserializeObject<PlaylistResponse>(
            body,
            UserLibraryJson.SerializerSettings)!;
    }

    private static async Task<PlaylistOperationResponse> ApplyOperation(
        HttpClient client,
        string accessToken,
        Guid playlistUuid,
        PlaylistOperationRequest operation)
    {
        return await PostJson<PlaylistOperationRequest, PlaylistOperationResponse>(
            client,
            $"/api/v3/library/playlists/{playlistUuid}/operations",
            operation,
            accessToken,
            HttpStatusCode.OK);
    }

    private static async Task<PlaylistCollaboratorResponse> InviteCollaborator(
        HttpClient client,
        string accessToken,
        Guid playlistUuid,
        string username)
    {
        return await PostJson<CreatePlaylistCollaboratorInvitationRequest, PlaylistCollaboratorResponse>(
            client,
            $"/api/v3/library/playlists/{playlistUuid}/collaborators/invitations",
            new CreatePlaylistCollaboratorInvitationRequest { Username = username },
            accessToken,
            HttpStatusCode.Created);
    }

    private static async Task<PlaylistCollaboratorResponse> AcceptInvitation(
        HttpClient client,
        string accessToken,
        Guid playlistUuid)
    {
        return await PostJson<object, PlaylistCollaboratorResponse>(
            client,
            $"/api/v3/library/playlists/{playlistUuid}/collaborators/invitations/accept",
            new { },
            accessToken,
            HttpStatusCode.OK);
    }

    private static async Task<PlaybackHistoryBatchResponse> UploadBatch(
        HttpClient client,
        string accessToken,
        PlaybackHistoryBatchRequest request)
    {
        return await PostJson<PlaybackHistoryBatchRequest, PlaybackHistoryBatchResponse>(
            client,
            "/api/v3/library/history/batch",
            request,
            accessToken,
            HttpStatusCode.OK);
    }

    private static async Task<(HttpResponseMessage Response, string Body)> ExportAccount(
        HttpClient client,
        string accessToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v3/library/users/me/export");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        return (response, body);
    }

    private static async Task<HttpResponseMessage> DeleteAccount(HttpClient client, string accessToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, "/api/v3/library/users/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> GetCurrentUserResponse(HttpClient client, string accessToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v3/library/users/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> RefreshResponse(HttpClient client, string refreshToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v3/library/auth/refresh");
        request.Content = JsonContent(new RefreshTokenRequest { RefreshToken = refreshToken });
        return await client.SendAsync(request);
    }

    private static async Task<UserSessionResponse> Reauthenticate(
        HttpClient client,
        string accessToken,
        string providerToken)
    {
        return await PostJson<ProviderReauthenticationRequest, UserSessionResponse>(
            client,
            "/api/v3/library/auth/reauthenticate/google",
            new ProviderReauthenticationRequest { ProviderToken = providerToken },
            accessToken,
            HttpStatusCode.OK);
    }

    private static async Task SetSessionReauthenticatedAt(Guid sessionUuid, DateTimeOffset reauthenticatedAt)
    {
        await using var connection = NewDbService().CreateConnection();
        await connection.ExecuteAsync(
            """
            UPDATE user_data.user_sessions
            SET reauthenticated_at = @ReauthenticatedAt
            WHERE id = @SessionUuid
            """,
            new { SessionUuid = sessionUuid, ReauthenticatedAt = reauthenticatedAt });
    }

    private static async Task<DateTime?> LoadSessionReauthenticatedAt(Guid sessionUuid)
    {
        await using var connection = NewDbService().CreateConnection();
        return await connection.QuerySingleAsync<DateTime?>(
            """
            SELECT reauthenticated_at
            FROM user_data.user_sessions
            WHERE id = @SessionUuid
            """,
            new { SessionUuid = sessionUuid });
    }

    private static async Task<int> CountUserRows(Guid userUuid)
    {
        await using var connection = NewDbService().CreateConnection();
        return await connection.QuerySingleAsync<int>(
            """
            SELECT count(*)::int
            FROM user_data.users
            WHERE id = @UserUuid
            """,
            new { UserUuid = userUuid });
    }

    private static async Task<TResponse> PostJson<TRequest, TResponse>(
        HttpClient client,
        string path,
        TRequest request,
        string? accessToken,
        HttpStatusCode expectedStatus)
    {
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, path);
        if (accessToken != null)
        {
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        httpRequest.Content = JsonContent(request);
        var response = await client.SendAsync(httpRequest);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(expectedStatus, body);
        return JsonConvert.DeserializeObject<TResponse>(body, UserLibraryJson.SerializerSettings)!;
    }

    private static StringContent JsonContent<T>(T value)
    {
        return new StringContent(
            JsonConvert.SerializeObject(value, UserLibraryJson.SerializerSettings),
            System.Text.Encoding.UTF8,
            "application/json");
    }

    private static WebApplicationFactory<Program> NewFactory(FakeProviderVerifier? fakeProvider = null)
    {
        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                if (fakeProvider != null)
                {
                    builder.ConfigureTestServices(services =>
                    {
                        services.AddSingleton<IAuthProviderVerifier>(fakeProvider);
                    });
                }

                builder.ConfigureAppConfiguration((_, configuration) =>
                {
                    configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["UserData:DatabaseUrl"] = DatabaseUrl,
                        ["UserAuth:AccessTokenSigningKey"] = TestSigningKey
                    });
                });
            });
    }

    private static async Task EnsurePostgresOrIgnore()
    {
        await using var connection = NewDbService().CreateConnection();

        try
        {
            await connection.OpenAsync();
        }
        catch
        {
            Assert.Ignore("Local Postgres is not available for account integration tests.");
        }
    }

    private static UserApiDbService NewDbService()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["UserData:DatabaseUrl"] = DatabaseUrl
            })
            .Build();
        return new UserApiDbService(configuration);
    }

    private static string UniqueUsername()
    {
        return $"account_{Guid.NewGuid():N}"[..30];
    }

    private static string DatabaseUrl =>
        Environment.GetEnvironmentVariable("DATABASE_URL") ??
        "postgresql://relisten:local_dev_password@127.0.0.1:15432/relisten_db";

    private const string TestSigningKey = "test-access-token-signing-key-with-more-than-32-bytes";

    private sealed class AccountDeletionCounts
    {
        public required int Users { get; init; }
        public required int AuthMethods { get; init; }
        public required int Sessions { get; init; }
        public required int RefreshTokens { get; init; }
        public required int Favorites { get; init; }
        public required int Settings { get; init; }
        public required int PlaybackHistory { get; init; }
        public required int PlaybackHistoryIngestKeys { get; init; }
        public required int OwnedPlaylists { get; init; }
        public required int Collaborators { get; init; }
        public required int EntriesAddedByDeletedUser { get; init; }
        public required int BlocksCreatedByDeletedUser { get; init; }
        public required int EditLogRows { get; init; }
        public required int OwnerPlaylists { get; init; }
        public required int DeletedUserPlaylists { get; init; }
        public required int OwnerPlaylistEntries { get; init; }
        public required int OwnerPlaylistEntriesReassigned { get; init; }
        public required int OwnerPlaylistBlocksReassigned { get; init; }
    }
}
