using System.Net;
using System.Net.Http.Headers;
using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Relisten.UserApi.Models;
using Relisten.UserApi.Services;
using Relisten.UserApi.Serialization;

namespace RelistenUserApiTests;

[TestFixture]
public class UserLibraryShareTokenTests
{
    [Test]
    public async Task ViewerShareToken_ShouldIssueMobileGrantAndResolveTokenlessLink()
    {
        await EnsurePostgresOrIgnore();
        await using var factory = NewFactory();
        using var client = factory.CreateClient();
        var owner = await SignIn(client);
        var playlist = await CreatePlaylist(client, owner.AccessToken);
        var shareToken = await CreateShareToken(client, owner.AccessToken, playlist.PlaylistUuid, "viewer");
        var ownerReadAfterShare = await GetPlaylist(
            client,
            playlist.PlaylistUuid.ToString(),
            owner.AccessToken);

        ownerReadAfterShare.Visibility.Should().Be("unlisted");
        shareToken.Token.Should().NotBeNullOrWhiteSpace();

        var tokenlessAnonymous = await client.GetAsync($"/api/v3/library/playlists/{playlist.ShortId}");
        tokenlessAnonymous.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var exchange = await ExchangeShareToken(
            client,
            playlist.ShortId,
            new ExchangePlaylistShareTokenRequest
            {
                Token = shareToken.Token!,
                DeviceId = "ios-simulator",
                Platform = "ios"
            });

        exchange.ResultStatus.Should().Be("mobile_grant_issued");
        exchange.AccessRole.Should().Be("viewer");
        exchange.MobileAccessGrant.Should().NotBeNull();
        exchange.MobileAccessGrant!.Token.Should().NotBe(shareToken.Token);

        var reopened = await GetPlaylist(
            client,
            playlist.ShortId,
            accessToken: null,
            exchange.MobileAccessGrant.Token,
            "ios-simulator");
        reopened.PlaylistUuid.Should().Be(playlist.PlaylistUuid);

        var wrongDeviceRead = await GetPlaylistResponse(
            client,
            playlist.ShortId,
            accessToken: null,
            exchange.MobileAccessGrant.Token,
            "other-device");
        wrongDeviceRead.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var revoked = await DeleteShareToken(
            client,
            owner.AccessToken,
            playlist.PlaylistUuid,
            shareToken.ShareTokenUuid);
        revoked.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var revokedGrantRead = await GetPlaylistResponse(
            client,
            playlist.ShortId,
            accessToken: null,
            exchange.MobileAccessGrant.Token,
            "ios-simulator");
        revokedGrantRead.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var revokedExchange = await ExchangeShareTokenExpectingError(
            client,
            playlist.ShortId,
            new ExchangePlaylistShareTokenRequest
            {
                Token = shareToken.Token!,
                DeviceId = "ios-simulator",
                Platform = "ios"
            });
        revokedExchange.Should().Be("invalid_share_token");
    }

    [Test]
    public async Task MobileGrant_ShouldNotOutliveExpiringShareToken()
    {
        await EnsurePostgresOrIgnore();
        await using var factory = NewFactory();
        using var client = factory.CreateClient();
        var owner = await SignIn(client);
        var playlist = await CreatePlaylist(client, owner.AccessToken);
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        var shareToken = await CreateShareToken(
            client,
            owner.AccessToken,
            playlist.PlaylistUuid,
            "viewer",
            expiresAt);

        var exchange = await ExchangeShareToken(
            client,
            playlist.ShortId,
            new ExchangePlaylistShareTokenRequest
            {
                Token = shareToken.Token!,
                DeviceId = "near-expiry-device",
                Platform = "ios"
            });

        exchange.MobileAccessGrant.Should().NotBeNull();
        exchange.MobileAccessGrant!.ExpiresAt.Should().BeOnOrBefore(expiresAt.AddSeconds(1));
        exchange.MobileAccessGrant.ExpiresAt.Should().BeBefore(DateTimeOffset.UtcNow.AddHours(1));
    }

    [Test]
    public async Task ShareTokenManagement_ShouldBeOwnerOnly()
    {
        await EnsurePostgresOrIgnore();
        await using var factory = NewFactory();
        using var client = factory.CreateClient();
        var owner = await SignIn(client);
        var other = await SignIn(client);
        var playlist = await CreatePlaylist(client, owner.AccessToken);

        var nonOwnerCreate = await CreateShareTokenResponse(
            client,
            other.AccessToken,
            playlist.PlaylistUuid,
            new CreatePlaylistShareTokenRequest { Role = "viewer" });
        nonOwnerCreate.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var shareToken = await CreateShareToken(client, owner.AccessToken, playlist.PlaylistUuid, "viewer");
        var nonOwnerRevoke = await DeleteShareToken(
            client,
            other.AccessToken,
            playlist.PlaylistUuid,
            shareToken.ShareTokenUuid);
        nonOwnerRevoke.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Exchange_ShouldFailWhenConcurrentRevokeWinsTokenLock()
    {
        await EnsurePostgresOrIgnore();
        await using var factory = NewFactory();
        using var client = factory.CreateClient();
        var owner = await SignIn(client);
        var playlist = await CreatePlaylist(client, owner.AccessToken);
        var shareToken = await CreateShareToken(client, owner.AccessToken, playlist.PlaylistUuid, "viewer");
        await using var connection = NewDbService().CreateConnection();
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await connection.ExecuteAsync(
            """
            SELECT 1
            FROM user_data.playlist_share_tokens
            WHERE id = @ShareTokenUuid
            FOR UPDATE
            """,
            new { shareToken.ShareTokenUuid },
            transaction);

        var exchangeTask = ExchangeShareTokenResponse(
            client,
            playlist.ShortId,
            new ExchangePlaylistShareTokenRequest
            {
                Token = shareToken.Token!,
                DeviceId = "race-device",
                Platform = "ios"
            },
            accessToken: null);
        await Task.Delay(250);
        exchangeTask.IsCompleted.Should().BeFalse("exchange must wait on the share-token row lock");

        await connection.ExecuteAsync(
            """
            UPDATE user_data.playlist_share_tokens
            SET revoked_at = now()
            WHERE id = @ShareTokenUuid
            """,
            new { shareToken.ShareTokenUuid },
            transaction);
        await transaction.CommitAsync();

        var response = await exchangeTask;
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        JObject.Parse(body)["error"]!.Value<string>().Should().Be("invalid_share_token");
    }

    [Test]
    public async Task EditorShareToken_ShouldRequireSignInThenGrantCollaboratorWriteAccess()
    {
        await EnsurePostgresOrIgnore();
        await using var factory = NewFactory();
        using var client = factory.CreateClient();
        var owner = await SignIn(client);
        var editor = await SignIn(client);
        var playlist = await CreatePlaylist(client, owner.AccessToken);
        var shareToken = await CreateShareToken(client, owner.AccessToken, playlist.PlaylistUuid, "editor");

        var anonymousEditorExchange = await ExchangeShareTokenResponse(
            client,
            playlist.ShortId,
            new ExchangePlaylistShareTokenRequest { Token = shareToken.Token! },
            accessToken: null);
        anonymousEditorExchange.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        JObject.Parse(await anonymousEditorExchange.Content.ReadAsStringAsync())["error"]!
            .Value<string>()
            .Should()
            .Be("sign_in_required");

        var exchange = await ExchangeShareToken(
            client,
            playlist.ShortId,
            new ExchangePlaylistShareTokenRequest { Token = shareToken.Token! },
            editor.AccessToken);

        exchange.ResultStatus.Should().Be("collaborator_access_granted");
        exchange.AccessRole.Should().Be("editor");
        exchange.MobileAccessGrant.Should().BeNull();

        var viewerState = await GetViewerState(client, editor.AccessToken, playlist.PlaylistUuid);
        viewerState.IsCollaborator.Should().BeTrue();
        viewerState.CanEdit.Should().BeTrue();

        var editorWrite = await ApplyOperation(
            client,
            editor.AccessToken,
            playlist.PlaylistUuid,
            new PlaylistOperationRequest
            {
                Op = "add_track",
                IdempotencyKey = Guid.NewGuid(),
                EntryUuid = Guid.NewGuid(),
                SourceTrackUuid = Guid.NewGuid()
            });

        editorWrite.ResultStatus.Should().Be("applied");
        editorWrite.Playlist.Entries.Should().ContainSingle();
    }

    [Test]
    public async Task Follow_ShouldPersistTokenlessViewerAccessForSignedInUser()
    {
        await EnsurePostgresOrIgnore();
        await using var factory = NewFactory();
        using var client = factory.CreateClient();
        var owner = await SignIn(client);
        var viewer = await SignIn(client);
        var playlist = await CreatePlaylist(client, owner.AccessToken);
        var shareToken = await CreateShareToken(client, owner.AccessToken, playlist.PlaylistUuid, "viewer");
        var exchange = await ExchangeShareToken(
            client,
            playlist.ShortId,
            new ExchangePlaylistShareTokenRequest
            {
                Token = shareToken.Token!,
                DeviceId = "viewer-device",
                Platform = "ios"
            },
            viewer.AccessToken);

        var follow = await FollowPlaylist(
            client,
            viewer.AccessToken,
            playlist.PlaylistUuid,
            exchange.MobileAccessGrant!.Token,
            "viewer-device");

        follow.IsFollowing.Should().BeTrue();
        follow.AccessRole.Should().Be("viewer");

        var reopenedWithoutGrant = await GetPlaylist(
            client,
            playlist.ShortId,
            viewer.AccessToken);
        reopenedWithoutGrant.PlaylistUuid.Should().Be(playlist.PlaylistUuid);

        var viewerLibrary = await ListPlaylists(client, viewer.AccessToken);
        viewerLibrary.Should().ContainSingle(item => item.PlaylistUuid == playlist.PlaylistUuid);

        var viewerWrite = await ApplyOperationResponse(
            client,
            viewer.AccessToken,
            playlist.PlaylistUuid,
            new PlaylistOperationRequest
            {
                Op = "add_track",
                IdempotencyKey = Guid.NewGuid(),
                EntryUuid = Guid.NewGuid(),
                SourceTrackUuid = Guid.NewGuid()
            });
        viewerWrite.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private static async Task<AuthTokenResponse> SignIn(HttpClient client)
    {
        return await PostJson<DevelopmentSessionRequest, AuthTokenResponse>(
            client,
            "/api/v3/library/auth/development/session",
            new DevelopmentSessionRequest
            {
                Username = UniqueUsername(),
                DisplayName = "Share Token Test User",
                DeviceId = $"share-token-test-{Guid.NewGuid():N}",
                DeviceName = "Test Device",
                Platform = "ios"
            },
            accessToken: null);
    }

    private static async Task<PlaylistResponse> CreatePlaylist(HttpClient client, string accessToken)
    {
        return await PostJson<CreatePlaylistRequest, PlaylistResponse>(
            client,
            "/api/v3/library/playlists",
            new CreatePlaylistRequest
            {
                PlaylistUuid = Guid.NewGuid(),
                Name = "Shared Playlist"
            },
            accessToken,
            HttpStatusCode.Created);
    }

    private static async Task<PlaylistShareTokenResponse> CreateShareToken(
        HttpClient client,
        string accessToken,
        Guid playlistUuid,
        string role,
        DateTimeOffset? expiresAt = null)
    {
        var response = await CreateShareTokenResponse(
            client,
            accessToken,
            playlistUuid,
            new CreatePlaylistShareTokenRequest { Role = role, ExpiresAt = expiresAt });
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Created, body);
        return JsonConvert.DeserializeObject<PlaylistShareTokenResponse>(
            body,
            UserLibraryJson.SerializerSettings)!;
    }

    private static async Task<HttpResponseMessage> CreateShareTokenResponse(
        HttpClient client,
        string accessToken,
        Guid playlistUuid,
        CreatePlaylistShareTokenRequest request)
    {
        var httpRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v3/library/playlists/{playlistUuid}/share-tokens");
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        httpRequest.Content = JsonContent(request);
        return await client.SendAsync(httpRequest);
    }

    private static async Task<HttpResponseMessage> DeleteShareToken(
        HttpClient client,
        string accessToken,
        Guid playlistUuid,
        Guid shareTokenUuid)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Delete,
            $"/api/v3/library/playlists/{playlistUuid}/share-tokens/{shareTokenUuid}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return await client.SendAsync(request);
    }

    private static async Task<ExchangePlaylistShareTokenResponse> ExchangeShareToken(
        HttpClient client,
        string playlistIdentifier,
        ExchangePlaylistShareTokenRequest exchange,
        string? accessToken = null)
    {
        var response = await ExchangeShareTokenResponse(client, playlistIdentifier, exchange, accessToken);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        body.Should().NotContain(exchange.Token);
        return JsonConvert.DeserializeObject<ExchangePlaylistShareTokenResponse>(
            body,
            UserLibraryJson.SerializerSettings)!;
    }

    private static async Task<string> ExchangeShareTokenExpectingError(
        HttpClient client,
        string playlistIdentifier,
        ExchangePlaylistShareTokenRequest exchange)
    {
        var response = await ExchangeShareTokenResponse(client, playlistIdentifier, exchange, accessToken: null);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        return JObject.Parse(body)["error"]!.Value<string>()!;
    }

    private static async Task<HttpResponseMessage> ExchangeShareTokenResponse(
        HttpClient client,
        string playlistIdentifier,
        ExchangePlaylistShareTokenRequest exchange,
        string? accessToken)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v3/library/playlists/{playlistIdentifier}/share-tokens/exchange");
        if (accessToken != null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        request.Content = JsonContent(exchange);
        return await client.SendAsync(request);
    }

    private static async Task<PlaylistResponse> GetPlaylist(
        HttpClient client,
        string playlistIdentifier,
        string? accessToken,
        string? mobileGrant = null,
        string? deviceId = null)
    {
        var response = await GetPlaylistResponse(client, playlistIdentifier, accessToken, mobileGrant, deviceId);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonConvert.DeserializeObject<PlaylistResponse>(body, UserLibraryJson.SerializerSettings)!;
    }

    private static async Task<HttpResponseMessage> GetPlaylistResponse(
        HttpClient client,
        string playlistIdentifier,
        string? accessToken,
        string? mobileGrant = null,
        string? deviceId = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v3/library/playlists/{playlistIdentifier}");
        if (accessToken != null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        if (mobileGrant != null && deviceId != null)
        {
            request.Headers.Add("X-Relisten-Mobile-Grant", mobileGrant);
            request.Headers.Add("X-Relisten-Device-Id", deviceId);
        }

        return await client.SendAsync(request);
    }

    private static async Task<PlaylistViewerStateResponse> GetViewerState(
        HttpClient client,
        string accessToken,
        Guid playlistUuid)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/v3/library/playlists/{playlistUuid}/viewer-state");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonConvert.DeserializeObject<PlaylistViewerStateResponse>(
            body,
            UserLibraryJson.SerializerSettings)!;
    }

    private static async Task<PlaylistViewerStateResponse> FollowPlaylist(
        HttpClient client,
        string accessToken,
        Guid playlistUuid,
        string mobileGrant,
        string deviceId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v3/library/playlists/{playlistUuid}/follow");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Add("X-Relisten-Mobile-Grant", mobileGrant);
        request.Headers.Add("X-Relisten-Device-Id", deviceId);
        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonConvert.DeserializeObject<PlaylistViewerStateResponse>(
            body,
            UserLibraryJson.SerializerSettings)!;
    }

    private static async Task<IReadOnlyList<PlaylistResponse>> ListPlaylists(HttpClient client, string accessToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v3/library/playlists");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonConvert.DeserializeObject<IReadOnlyList<PlaylistResponse>>(
            body,
            UserLibraryJson.SerializerSettings)!;
    }

    private static async Task<PlaylistOperationResponse> ApplyOperation(
        HttpClient client,
        string accessToken,
        Guid playlistUuid,
        PlaylistOperationRequest operation)
    {
        var response = await ApplyOperationResponse(client, accessToken, playlistUuid, operation);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonConvert.DeserializeObject<PlaylistOperationResponse>(
            body,
            UserLibraryJson.SerializerSettings)!;
    }

    private static async Task<HttpResponseMessage> ApplyOperationResponse(
        HttpClient client,
        string accessToken,
        Guid playlistUuid,
        PlaylistOperationRequest operation)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v3/library/playlists/{playlistUuid}/operations");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = JsonContent(operation);
        return await client.SendAsync(request);
    }

    private static async Task<TResponse> PostJson<TRequest, TResponse>(
        HttpClient client,
        string path,
        TRequest request,
        string? accessToken,
        HttpStatusCode expectedStatus = HttpStatusCode.OK)
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

    private static WebApplicationFactory<Program> NewFactory()
    {
        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
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
            Assert.Ignore("Local Postgres is not available for share-token integration tests.");
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
        return $"share_{Guid.NewGuid():N}"[..30];
    }

    private static string DatabaseUrl =>
        Environment.GetEnvironmentVariable("DATABASE_URL") ??
        "postgresql://relisten:local_dev_password@127.0.0.1:15432/relisten_db";

    private const string TestSigningKey = "test-access-token-signing-key-with-more-than-32-bytes";
}
