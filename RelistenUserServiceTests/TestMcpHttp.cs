using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using OpenIddict.Abstractions;
using RelistenIngestionWorker.AccountDeletion;
using RelistenUserService;
using RelistenUserService.Authentication;
using RelistenUserService.Mcp;
using RelistenUserService.Playlists;

namespace RelistenUserServiceTests;

[TestFixture]
[NonParallelizable]
public sealed class TestMcpHttp
{
    private const string AuthOrigin = "https://auth.relisten.localhost:5443";
    private const string AccountsOrigin = "https://accounts.relisten.localhost:5443";
    private const string McpOrigin = "https://mcp.relisten.localhost:5443";
    private const string Resource = McpOrigin + "/mcp";
    private const string MetadataPath = "/.well-known/oauth-protected-resource/mcp";
    private const string ChatGpt = "https://chatgpt.com/oauth/client.json";
    private const string ChatGptRedirect = "https://chatgpt.com/connector_platform_oauth_redirect";
    private const string Codex = "https://chatgpt.com/oauth/codex/client.json";
    private const string CodexRedirect = "http://127.0.0.1:51057/callback";
    private const string Native = "relisten-mobile-ios-dev";
    private const string NativeRedirect = "net.relisten.mobile:/oauth2redirect/ios";
    private const string Verifier =
        "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-._~abc";
    private const string Challenge = "dYSqskoTcWrpu8GYY0XpWlzOc0c5rd9YO3uAgh_zmV4";
    private const string ReviewServiceKey = "synthetic-catalog-review-key-for-tests-only";
    private const string WriteIdKey = "synthetic-mcp-write-id-key-for-tests-only";
    private const string ReadScopes = "openid offline_access user.read library.read";

    private CatalogStub catalog = null!;
    private PostgresIntegrationDatabase database = null!;
    private DirectoryInfo directory = null!;
    private WebApplication app = null!;
    private HttpClient http = null!;

    [OneTimeSetUp]
    public async Task StartDatabase()
    {
        database = new();
        await database.StartAsync();
    }

    [OneTimeTearDown]
    public async Task StopDatabase() => await database.StopAsync();

    [TearDown]
    public async Task StopApp()
    {
        http?.Dispose();
        if (app is not null)
        {
            await app.DisposeAsync();
            app = null!;
        }
        if (directory?.Exists == true)
        {
            directory.Delete(recursive: true);
            directory = null!;
        }
    }

    [Test]
    public async Task Mcp_routes_and_oauth_require_a_configured_host()
    {
        await StartApp(mountCatalog: false);

        using var response = await SendRpc("tools/list");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var metadata = await http.GetAsync(McpOrigin + MetadataPath);
        metadata.StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var authorize = await Authorize(
            AuthorizeFields(ChatGpt, ChatGptRedirect, ReadScopes, Resource)
        );
        (await OAuthError(authorize))
            .Should()
            .BeOneOf("invalid_request", "unauthorized_client", "invalid_client");
        using var discovery = await http.GetAsync(AuthOrigin + "/.well-known/openid-configuration");
        (await Json(discovery))
            .TryGetProperty("client_id_metadata_document_supported", out _)
            .Should()
            .BeFalse();
    }

    [Test]
    public async Task PersistedMcpRegistrationWithoutACodeAllowlistEntryCannotAuthorize()
    {
        await StartApp();
        const string clientId = "https://client.example.test/metadata.json";
        const string callback = "http://127.0.0.1:51059/callback";
        await using var scope = app.Services.CreateAsyncScope();
        var applications =
            scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        await applications.CreateAsync(
            new OpenIddictApplicationDescriptor
            {
                ClientId = clientId,
                ClientType = OpenIddictConstants.ClientTypes.Public,
                RedirectUris = { new Uri(callback) },
                Permissions =
                {
                    OpenIddictConstants.Permissions.Endpoints.Authorization,
                    OpenIddictConstants.Permissions.GrantTypes.AuthorizationCode,
                    OpenIddictConstants.Permissions.ResponseTypes.Code,
                },
            }
        );

        var fields = AuthorizeFields(clientId, callback, "openid");
        using var response = await Authorize(fields, await SignIn(fields));
        (await OAuthError(response)).Should().Be("unauthorized_client");
    }

    [Test]
    public async Task PublicDiscoveryAdvertisesToolPermissionsAndIsolatedHost()
    {
        await StartApp();
        foreach (var host in new[] { AuthOrigin, AccountsOrigin })
        {
            using var tool = await SendRpc("tools/list", endpoint: host + "/mcp/");
            tool.StatusCode.Should().Be(HttpStatusCode.NotFound);
            using var metadata = await http.GetAsync(host + MetadataPath);
            metadata.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
        using var profile = await http.GetAsync(McpOrigin + "/v1/me");
        profile.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var queryToken = await SendRpc(
            "tools/list",
            endpoint: Resource + "?access_token=example"
        );
        queryToken.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var foreignOrigin = await SendRpc(
            "tools/list",
            origin: "https://untrusted.example.test"
        );
        foreignOrigin.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        foreach (var path in new[] { MetadataPath, "/.well-known/oauth-protected-resource" })
        {
            using var discovery = await http.GetAsync(McpOrigin + path);
            var metadata = await Json(discovery);
            metadata.GetProperty("resource").GetString().Should().Be(Resource);
            metadata
                .GetProperty("authorization_servers")[0]
                .GetString()
                .Should()
                .Be(AuthOrigin + "/");
            metadata
                .GetProperty("scopes_supported")
                .EnumerateArray()
                .Select(scope => scope.GetString())
                .Should()
                .BeEquivalentTo("user.read", "library.read", "library.write", "offline_access");
        }

        using var auth = await http.GetAsync(
            AuthOrigin + "/.well-known/oauth-authorization-server"
        );
        var oauth = await Json(auth);
        oauth.GetProperty("client_id_metadata_document_supported").GetBoolean().Should().BeTrue();
        oauth
            .GetProperty("token_endpoint_auth_methods_supported")
            .EnumerateArray()
            .Select(method => method.GetString())
            .Should()
            .Contain("none")
            .And.NotContain("private_key_jwt");

        using var initialize = await SendRpc(
            "initialize",
            new
            {
                protocolVersion = "2025-06-18",
                clientInfo = new { name = "relisten-test", version = "1" },
                capabilities = new { },
            }
        );
        (await Json(initialize))
            .GetProperty("result")
            .GetProperty("serverInfo")
            .GetProperty("name")
            .GetString()
            .Should()
            .NotBeEmpty();

        using var listed = await SendRpc("tools/list");
        var tools = (await Json(listed))
            .GetProperty("result")
            .GetProperty("tools")
            .EnumerateArray()
            .ToDictionary(tool => tool.GetProperty("name").GetString()!);
        tools.Should().HaveCount(14);
        tools["find"]
            .GetProperty("_meta")
            .GetProperty("securitySchemes")[0]
            .GetProperty("type")
            .GetString()
            .Should()
            .Be("noauth");
        tools["whoami"]
            .GetProperty("_meta")
            .GetProperty("openai/profile")
            .GetBoolean()
            .Should()
            .BeTrue();
        tools["whoami"]
            .GetProperty("outputSchema")
            .GetProperty("properties")
            .TryGetProperty("id", out _)
            .Should()
            .BeTrue();
        tools["list_performances"]
            .GetProperty("inputSchema")
            .GetProperty("properties")
            .TryGetProperty("not_in_my_library", out _)
            .Should()
            .BeTrue();
        tools["list_performances"]
            .GetProperty("_meta")
            .GetProperty("securitySchemes")
            .EnumerateArray()
            .Select(scheme => scheme.GetProperty("type").GetString())
            .Should()
            .BeEquivalentTo("noauth", "oauth2");
        tools["edit_playlist"]
            .GetProperty("_meta")
            .GetProperty("securitySchemes")[0]
            .GetProperty("scopes")[0]
            .GetString()
            .Should()
            .Be("library.write");
        tools["edit_playlist"]
            .GetProperty("annotations")
            .GetProperty("destructiveHint")
            .GetBoolean()
            .Should()
            .BeTrue();

        var found = await Call("find", new { text = "synthetic" });
        found
            .GetProperty("structuredContent")
            .GetProperty("artists")[0]
            .GetProperty("name")
            .GetString()
            .Should()
            .Be("Synthetic Artist");
        catalog.LastRequest!.AbsolutePath.Should().Be("/api/v4/search");
        var library = await Call("my_library", new { });
        ChallengeFrom(library)
            .Should()
            .Contain("error=\"invalid_token\"")
            .And.Contain(McpOrigin + MetadataPath);
        var filtered = await Call(
            "list_performances",
            new { artist = "artist_example", not_in_my_library = true }
        );
        ChallengeFrom(filtered).Should().Contain("scope=\"library.read\"");

        using var resource = await SendRpc("resources/read", new { uri = "relisten://caveats" });
        (await Json(resource))
            .GetProperty("result")
            .GetProperty("contents")[0]
            .GetProperty("text")
            .GetString()
            .Should()
            .Contain("track starts");
        using var prompt = await SendRpc("prompts/get", new { name = "build-playlist" });
        (await Json(prompt))
            .GetProperty("result")
            .GetProperty("messages")[0]
            .GetProperty("content")
            .GetProperty("text")
            .GetString()
            .Should()
            .Contain("tastes");
    }

    [Test]
    public async Task Find_returns_shows_for_an_imported_venue_and_year()
    {
        const string venueId = "imported_venue_harbor";
        const string showId = "show_harbor_1998";
        await StartApp(catalogResponse: uri =>
            (
                uri.AbsolutePath,
                QueryHelpers.ParseQuery(uri.Query).GetValueOrDefault("q").ToString()
            ) switch
            {
                ("/api/v4/search", "Harbor Hall 1998") => new { shows = Array.Empty<object>() },
                ("/api/v4/search", "Harbor Hall") => new
                {
                    venues = new[]
                    {
                        new { imported_venue = new { id = venueId, artist_id = "artist_example" } },
                    },
                },
                ($"/api/v4/imported-venues/{venueId}", "") => new
                {
                    shows = new[] { new { id = showId, date = "1998-10-01" } },
                },
                _ => null,
            }
        );

        var result = await Call("find", new { text = "Harbor Hall 1998" });

        result.TryGetProperty("structuredContent", out var data).Should().BeTrue(result.ToString());
        data.GetProperty("shows")
            .EnumerateArray()
            .Select(show => show.GetProperty("id").GetString())
            .Should()
            .Equal(showId);
    }

    [Test]
    public async Task ConsentScopeStepUpAndDisconnectWorkThroughRealOAuthAndMcpRequests()
    {
        await StartApp();
        var nativeFields = AuthorizeFields(Native, NativeRedirect, ReadScopes + " account.manage");
        var cookie = await SignIn(nativeFields);
        using var nativeCode = await Authorize(nativeFields, cookie);
        var native = await Exchange(Native, NativeRedirect, ReadCode(nativeCode));

        var fields = AuthorizeFields(ChatGpt, ChatGptRedirect, ReadScopes, Resource);
        using var wrongTarget = await Authorize(
            AuthorizeFields(ChatGpt, ChatGptRedirect, ReadScopes, AccountsOrigin),
            cookie
        );
        (await OAuthError(wrongTarget)).Should().Be("invalid_target");
        using var forbiddenScope = await Authorize(
            AuthorizeFields(ChatGpt, ChatGptRedirect, ReadScopes + " account.manage", Resource),
            cookie
        );
        (await OAuthError(forbiddenScope)).Should().Be("invalid_request");
        using var forged = await SendForm(
            AuthOrigin + "/connect/authorize",
            fields.Append(new("decision", "allow")),
            cookie
        );
        (await OAuthError(forged)).Should().Be("access_denied");

        var code = await Approve(fields, cookie, "Read your favorites and playlists.");
        using var invalidExchange = await ExchangeResponse(ChatGpt, ChatGptRedirect, code, null);
        (await OAuthError(invalidExchange)).Should().Be("invalid_target");
        var read = await Exchange(ChatGpt, ChatGptRedirect, code, Resource);
        var profile = (await Call("whoami", new { }, read.Access)).GetProperty("structuredContent");
        profile.GetProperty("id").GetString().Should().StartWith("user_");
        (await Call("my_library", new { }, read.Access))
            .GetProperty("structuredContent")
            .GetProperty("favorites")
            .GetArrayLength()
            .Should()
            .Be(0);

        using var reverseAudience = await Get(AccountsOrigin + "/v1/me", read.Access);
        reverseAudience.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using var nativeAudience = await SendRpc("tools/list", token: native.Access);
        nativeAudience.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        ChallengeFrom(await Call("create_playlist", Draft, read.Access))
            .Should()
            .Contain("error=\"insufficient_scope\"")
            .And.Contain("scope=\"library.write\"");

        // A prior grant must not issue a code without the listener's approval.
        using var repeatedAuthorize = await Authorize(
            AuthorizeFields(ChatGpt, ChatGptRedirect, ReadScopes, Resource),
            cookie
        );
        repeatedAuthorize.StatusCode.Should().Be(HttpStatusCode.OK);
        repeatedAuthorize.Headers.Location.Should().BeNull();
        (await repeatedAuthorize.Content.ReadAsStringAsync()).Should().Contain("decision");
        var writeFields = AuthorizeFields(
            ChatGpt,
            ChatGptRedirect,
            ReadScopes + " library.write",
            Resource
        );
        var writeCode = await Approve(writeFields, cookie, "Create or edit your playlists.");
        var write = await Exchange(ChatGpt, ChatGptRedirect, writeCode, Resource);
        var created = (await Call("create_playlist", Draft, write.Access)).GetProperty(
            "structuredContent"
        );
        var id = created.GetProperty("playlist_id").GetString()!;
        var edited = await Call(
            "edit_playlist",
            new
            {
                id,
                changes = new[]
                {
                    new
                    {
                        operation_type = "update_metadata",
                        payload = new { name = "Renamed guide" },
                    },
                },
            },
            write.Access
        );
        edited.TryGetProperty("structuredContent", out _).Should().BeTrue(edited.ToString());
        var playlist = (await Call("read_playlist", new { id }, write.Access)).GetProperty(
            "structuredContent"
        );
        playlist
            .GetProperty("untrusted_listener_text")
            .GetProperty("name")
            .GetString()
            .Should()
            .Be("Renamed guide");
        playlist
            .GetProperty("segments")[0]
            .GetProperty("entries")[0]
            .GetProperty("untrusted_listener_text")
            .GetProperty("description")
            .GetString()
            .Should()
            .Be("Opening note");

        using var refreshedResponse = await Refresh(write.Refresh);
        var refreshed = await ReadTokens(refreshedResponse);
        using var clients = await Get(AccountsOrigin + "/v1/connected-clients", native.Access);
        (await Json(clients))
            .GetProperty("clients")[0]
            .GetProperty("id")
            .GetString()
            .Should()
            .Be("chatgpt");
        using var disconnect = new HttpRequestMessage(
            HttpMethod.Delete,
            AccountsOrigin + "/v1/connected-clients/chatgpt"
        );
        disconnect.Headers.Authorization = new("Bearer", native.Access);
        using var disconnected = await http.SendAsync(disconnect);
        disconnected
            .StatusCode.Should()
            .Be(HttpStatusCode.NoContent, await disconnected.Content.ReadAsStringAsync());

        ChallengeFrom(await Call("my_library", new { }, refreshed.Access))
            .Should()
            .Contain("invalid_token");
        using var invalidRefresh = await Refresh(refreshed.Refresh);
        (await OAuthError(invalidRefresh)).Should().Be("invalid_grant");
        using var sameNativeSession = await Get(AccountsOrigin + "/v1/me", native.Access);
        sameNativeSession.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public async Task CodexCanCreateAndDisconnectIndependentlyOfChatGpt()
    {
        await StartApp();
        var nativeFields = AuthorizeFields(Native, NativeRedirect, ReadScopes + " account.manage");
        var cookie = await SignIn(nativeFields);
        using var nativeCode = await Authorize(nativeFields, cookie);
        var native = await Exchange(Native, NativeRedirect, ReadCode(nativeCode));
        var chatGpt = await ConnectChatGpt("google-alice");

        using var wrongCallback = await Authorize(
            AuthorizeFields(Codex, "http://127.0.0.1:51058/callback", ReadScopes, Resource),
            cookie
        );
        wrongCallback.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var wrongTarget = await Authorize(
            AuthorizeFields(Codex, CodexRedirect, ReadScopes, AccountsOrigin),
            cookie
        );
        (await OAuthError(wrongTarget)).Should().Be("invalid_target");

        var fields = AuthorizeFields(Codex, CodexRedirect, ReadScopes + " library.write", Resource);
        var code = await Approve(fields, cookie, "Create or edit your playlists.");
        var codex = await Exchange(Codex, CodexRedirect, code, Resource);
        using var accounts = await Get(AccountsOrigin + "/v1/me", codex.Access);
        accounts.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var created = await Call("create_playlist", Draft, codex.Access);
        created.TryGetProperty("structuredContent", out _).Should().BeTrue(created.ToString());
        using var refreshedResponse = await Refresh(codex.Refresh, Codex);
        var refreshed = await ReadTokens(refreshedResponse);

        using var clients = await Get(AccountsOrigin + "/v1/connected-clients", native.Access);
        (await Json(clients))
            .GetProperty("clients")
            .EnumerateArray()
            .Select(client => client.GetProperty("id").GetString())
            .Should()
            .BeEquivalentTo(new[] { "chatgpt", "codex" });

        using var disconnected = await Send(
            new(HttpMethod.Delete, AccountsOrigin + "/v1/connected-clients/codex")
            {
                Headers =
                {
                    Authorization = new AuthenticationHeaderValue("Bearer", native.Access),
                },
            }
        );
        disconnected.StatusCode.Should().Be(HttpStatusCode.NoContent);
        ChallengeFrom(await Call("my_library", new { }, refreshed.Access))
            .Should()
            .Contain("invalid_token");
        using var invalidRefresh = await Refresh(refreshed.Refresh, Codex);
        (await OAuthError(invalidRefresh)).Should().Be("invalid_grant");
        var chatGptProfile = await Call("whoami", new { }, chatGpt.Access);
        chatGptProfile.TryGetProperty("structuredContent", out _).Should().BeTrue();
    }

    [Test]
    public async Task Review_search_uses_a_separate_budget_only_while_the_link_is_active()
    {
        await StartApp();
        var nativeFields = AuthorizeFields(Native, NativeRedirect, ReadScopes + " account.manage");
        var cookie = await SignIn(nativeFields, "google-shared");
        using var nativeCode = await Authorize(nativeFields, cookie);
        var native = await Exchange(Native, NativeRedirect, ReadCode(nativeCode));

        var fields = AuthorizeFields(ChatGpt, ChatGptRedirect, ReadScopes, Resource);
        var tokens = await Exchange(
            ChatGpt,
            ChatGptRedirect,
            await Approve(fields, cookie, "Read your favorites"),
            Resource
        );
        var profile = await Call("whoami", new { }, tokens.Access);
        var user = UserId.Parse(
            profile.GetProperty("structuredContent").GetProperty("id").GetString()!
        );
        var args = new { artist = "artist_example", query = "Scarlet" };
        await Call("search_reviews", args);
        catalog.AuthorizedReviewRequests.Should().Be(0);

        for (var search = 0; search < 30; search++)
        {
            var result = await Call("search_reviews", args, tokens.Access);
            result.TryGetProperty("structuredContent", out _).Should().BeTrue();
        }
        var limited = await Call("search_reviews", args, tokens.Access);
        limited.GetProperty("isError").GetBoolean().Should().BeTrue();
        limited
            .GetProperty("content")[0]
            .GetProperty("text")
            .GetString()
            .Should()
            .Contain("signed-in review search limit");
        catalog.AuthorizedReviewRequests.Should().Be(30);

        await using (var db = database.CreateContext())
        {
            await db
                .NativeSessions.Where(session =>
                    session.UserId == user && session.ClientId == ChatGpt
                )
                .ExecuteUpdateAsync(set =>
                    set.SetProperty(
                        session => session.AbsoluteExpiresAt,
                        DateTimeOffset.UtcNow.AddMinutes(-1)
                    )
                );
        }
        using var clients = await Get(AccountsOrigin + "/v1/connected-clients", native.Access);
        (await Json(clients))
            .GetProperty("clients")[0]
            .GetProperty("state")
            .GetString()
            .Should()
            .Be("expired");
        var anonymous = await Call("search_reviews", args, tokens.Access);
        anonymous.TryGetProperty("structuredContent", out _).Should().BeTrue();
        catalog.AuthorizedReviewRequests.Should().Be(30);

        using var request = new HttpRequestMessage(
            HttpMethod.Delete,
            AccountsOrigin + "/v1/connected-clients/chatgpt"
        );
        request.Headers.Authorization = new("Bearer", native.Access);
        using var disconnected = await http.SendAsync(request);
        disconnected.StatusCode.Should().Be(HttpStatusCode.NoContent);
        using var cleared = await Get(AccountsOrigin + "/v1/connected-clients", native.Access);
        (await Json(cleared)).GetProperty("clients").GetArrayLength().Should().Be(0);
    }

    [Test]
    public async Task AccountPurgeInvalidatesPreviouslyIssuedMcpAccessAndRefreshTokens()
    {
        await StartApp();
        var fields = AuthorizeFields(ChatGpt, ChatGptRedirect, ReadScopes, Resource);
        var cookie = await SignIn(fields);
        var tokens = await Exchange(
            ChatGpt,
            ChatGptRedirect,
            await Approve(fields, cookie, "Read your favorites"),
            Resource
        );
        var profile = (await Call("whoami", new { }, tokens.Access)).GetProperty(
            "structuredContent"
        );
        var userId = profile.GetProperty("id").GetString()!;
        await new AccountPurge(database.ConnectionString).RunForUserAsync(
            userId,
            CancellationToken.None
        );

        ChallengeFrom(await Call("whoami", new { }, tokens.Access))
            .Should()
            .Contain("invalid_token");
        using var refresh = await Refresh(tokens.Refresh);
        (await OAuthError(refresh)).Should().Be("invalid_grant");
    }

    [Test]
    public async Task Playlist_tools_report_another_accounts_playlist_as_not_found()
    {
        await StartApp();
        var owner = await ConnectChatGpt("google-alice");
        var other = await ConnectChatGpt("google-shared");
        var id = (await Call("create_playlist", Draft, owner.Access))
            .GetProperty("structuredContent")
            .GetProperty("playlist_id")
            .GetString()!;
        var rename = new[]
        {
            new { operation_type = "update_metadata", payload = new { name = "Taken over" } },
        };

        // The answer must match a playlist that does not exist, so IDs cannot be probed.
        var missing = PlaylistId.New().Value;
        var read = await Call("read_playlist", new { id }, other.Access);
        ErrorText(read)
            .Should()
            .Be(ErrorText(await Call("read_playlist", new { id = missing }, other.Access)));
        var edit = await Call("edit_playlist", new { id, changes = rename }, other.Access);
        ErrorText(edit)
            .Should()
            .Be(
                ErrorText(
                    await Call(
                        "edit_playlist",
                        new { id = missing, changes = rename },
                        other.Access
                    )
                )
            );
        PlaylistName(await Call("read_playlist", new { id }, owner.Access))
            .Should()
            .Be("Listening guide");
    }

    [Test]
    public async Task A_linked_account_is_rate_limited_separately_from_anonymous_callers()
    {
        await StartApp();
        var tokens = await ConnectChatGpt("google-alice");

        for (var request = 0; request < McpEndpoints.AccountRequestsPerMinute; request++)
        {
            using var allowed = await SendRpc("tools/list", token: tokens.Access);
            allowed.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        using var limited = await SendRpc("tools/list", token: tokens.Access);
        limited.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        using var anonymous = await SendRpc("tools/list");
        anonymous.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public async Task Playlist_writes_replay_a_repeated_key_and_refuse_a_stale_revision()
    {
        await StartApp();
        var tokens = await ConnectChatGpt("google-alice");
        var draft = new
        {
            name = "Listening guide",
            segments = new[] { new { entries = new[] { new { description = "Opening note" } } } },
            idempotency_key = "guide-draft-1",
        };

        var created = await Call("create_playlist", draft, tokens.Access);
        var id = created.GetProperty("structuredContent").GetProperty("playlist_id").GetString()!;
        var playlistsAfterCreate = await PlaylistCount(tokens.Access);
        var retried = await Call("create_playlist", draft, tokens.Access);
        retried
            .GetProperty("structuredContent")
            .GetProperty("playlist_id")
            .GetString()
            .Should()
            .Be(id);
        (await PlaylistCount(tokens.Access)).Should().Be(playlistsAfterCreate);

        object Rename(string name, string key) =>
            new
            {
                id,
                changes = new[]
                {
                    new { operation_type = "update_metadata", payload = new { name } },
                },
                expected_revision = 1,
                idempotency_key = key,
            };
        var renamed = await Call(
            "edit_playlist",
            Rename("First rename", "rename-1"),
            tokens.Access
        );
        renamed.TryGetProperty("structuredContent", out _).Should().BeTrue(renamed.ToString());

        // The retry finds the revision advanced by its own first attempt and must still replay.
        var replayed = await Call(
            "edit_playlist",
            Rename("First rename", "rename-1"),
            tokens.Access
        );
        replayed.TryGetProperty("structuredContent", out _).Should().BeTrue(replayed.ToString());

        var stale = await Call("edit_playlist", Rename("Stale rename", "rename-2"), tokens.Access);
        ErrorText(stale).Should().Contain("revision 2");
        PlaylistName(await Call("read_playlist", new { id }, tokens.Access))
            .Should()
            .Be("First rename");
    }

    [Test]
    public async Task A_keyed_edit_rejects_shorter_and_longer_retries()
    {
        await StartApp();
        var tokens = await ConnectChatGpt("google-alice");
        var created = await Call("create_playlist", Draft, tokens.Access);
        var id = created.GetProperty("structuredContent").GetProperty("playlist_id").GetString();
        var rename = new { operation_type = "update_metadata", payload = new { name = "Renamed" } };
        var clearDescription = new
        {
            operation_type = "update_metadata",
            payload = new { description = (string?)null },
        };
        var overwrite = new
        {
            operation_type = "update_metadata",
            payload = new { name = "Overwritten" },
        };
        Task<JsonElement> Edit(params object[] changes) =>
            Call(
                "edit_playlist",
                new
                {
                    id,
                    changes,
                    idempotency_key = "rename-and-clear",
                },
                tokens.Access
            );

        var first = await Edit(rename, clearDescription);
        first.TryGetProperty("structuredContent", out _).Should().BeTrue(first.ToString());

        ErrorText(await Edit(rename)).Should().Contain("operation ID was reused");
        ErrorText(await Edit(rename, clearDescription, overwrite))
            .Should()
            .Contain("operation ID was reused");
        PlaylistName(await Call("read_playlist", new { id }, tokens.Access)).Should().Be("Renamed");
    }

    private static readonly object Draft = new
    {
        name = "Listening guide",
        description = "Guide description",
        segments = new[] { new { entries = new[] { new { description = "Opening note" } } } },
    };

    private async Task StartApp(
        bool mountCatalog = true,
        Func<Uri, object?>? catalogResponse = null
    )
    {
        catalog = new(catalogResponse);
        directory = Directory.CreateTempSubdirectory("relisten-mcp-test-");
        var caPath = Path.Combine(directory.FullName, "test-ca.pem");
        using var key = RSA.Create();
        var certificateRequest = new CertificateRequest(
            "CN=MCP test",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1
        );
        using var certificate = certificateRequest.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddHours(1)
        );
        await File.WriteAllTextAsync(caPath, certificate.ExportCertificatePem());
        var builder = WebApplication.CreateBuilder(
            new WebApplicationOptions
            {
                EnvironmentName = Environments.Development,
                ContentRootPath = directory.FullName,
                ApplicationName = typeof(AccountsApplication).Assembly.GetName().Name,
            }
        );
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["ACCOUNTS_DATABASE_URL"] = database.ConnectionString,
                ["Accounts:AuthHost"] = new Uri(AuthOrigin).Authority,
                ["Accounts:AccountsHost"] = new Uri(AccountsOrigin).Authority,
                ["Accounts:Issuer"] = AuthOrigin,
                ["Accounts:WebClientSecret"] = "mcp-test-secret",
                ["Accounts:DevelopmentCertificateAuthorityPath"] = caPath,
                ["Accounts:EnableDevelopmentPersonas"] = "true",
                ["Accounts:McpHost"] = mountCatalog ? new Uri(McpOrigin).Authority : null,
                ["Accounts:McpCatalogBaseUrl"] = "https://catalog.example.test",
                ["MCP_REVIEW_SEARCH_KEY"] = ReviewServiceKey,
                ["MCP_WRITE_ID_KEY"] = WriteIdKey,
            }
        );
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        builder
            .Services.AddHttpClient(nameof(McpCatalogClient))
            .ConfigureHttpClient(client =>
                client.BaseAddress = new Uri("https://catalog.example.test")
            );
        builder.Services.PostConfigure<Microsoft.Extensions.Http.HttpClientFactoryOptions>(
            nameof(McpCatalogClient),
            options =>
                options.HttpMessageHandlerBuilderActions.Add(handler =>
                    handler.PrimaryHandler = catalog
                )
        );
        builder
            .Services.AddHttpClient(nameof(PlaylistTrackDurations))
            .ConfigurePrimaryHttpMessageHandler(() => new CatalogStub(_ => null));
        app = AccountsApplication.Build(builder);
        await AccountsApplication.InitializeAsync(app, CancellationToken.None);
        await app.StartAsync();
        http = app.GetTestClient();
    }

    private async Task<string> SignIn(
        Dictionary<string, string> fields,
        string persona = "google-alice"
    )
    {
        var returnUrl = QueryHelpers.AddQueryString("/connect/authorize", fields!);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            AuthOrigin + "/development/sign-in"
        )
        {
            Content = new FormUrlEncodedContent(
                new Dictionary<string, string> { ["persona"] = persona, ["return_url"] = returnUrl }
            ),
        };
        request.Headers.Add("Origin", AuthOrigin);
        using var response = await http.SendAsync(request);
        response
            .StatusCode.Should()
            .Be(HttpStatusCode.Redirect, await response.Content.ReadAsStringAsync());
        return ReadCookie(response, AuthenticationConstants.AuthSsoCookie);
    }

    private async Task<TokenPair> ConnectChatGpt(string persona)
    {
        var fields = AuthorizeFields(
            ChatGpt,
            ChatGptRedirect,
            ReadScopes + " library.write",
            Resource
        );
        var cookie = await SignIn(fields, persona);
        var code = await Approve(fields, cookie, "Create or edit your playlists.");
        return await Exchange(ChatGpt, ChatGptRedirect, code, Resource);
    }

    private Task<HttpResponseMessage> Authorize(
        Dictionary<string, string> fields,
        string? cookie = null
    )
    {
        var request = new HttpRequestMessage(
            HttpMethod.Get,
            QueryHelpers.AddQueryString(AuthOrigin + "/connect/authorize", fields!)
        );
        if (cookie is not null)
        {
            request.Headers.Add("Cookie", cookie);
        }
        return Send(request);
    }

    private async Task<string> Approve(
        Dictionary<string, string> fields,
        string cookie,
        string scopeDescription
    )
    {
        using var consent = await Authorize(fields, cookie);
        var html = await consent.Content.ReadAsStringAsync();
        var redirectOrigin = new Uri(fields["redirect_uri"]).GetLeftPart(UriPartial.Authority);
        consent
            .Headers.GetValues("Content-Security-Policy")
            .Single()
            .Should()
            .Contain($"form-action 'self' {redirectOrigin};");
        consent.StatusCode.Should().Be(HttpStatusCode.OK, html);
        html.Should().Contain(redirectOrigin).And.Contain(scopeDescription);
        var csrf = ReadCookie(consent, AuthenticationConstants.CsrfCookie);
        var form = Regex
            .Matches(html, """<input type="hidden" name="([^"]+)" value="([^"]*)">""")
            .Select(match => new KeyValuePair<string, string>(
                WebUtility.HtmlDecode(match.Groups[1].Value),
                WebUtility.HtmlDecode(match.Groups[2].Value)
            ))
            .Append(new("decision", "allow"));
        using var approved = await SendForm(
            AuthOrigin + "/connect/authorize",
            form,
            cookie + "; " + csrf
        );
        return ReadCode(approved);
    }

    private Task<HttpResponseMessage> SendForm(
        string url,
        IEnumerable<KeyValuePair<string, string>> fields,
        string? cookie = null
    )
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(fields),
        };
        if (cookie is not null)
        {
            request.Headers.Add("Cookie", cookie);
        }
        return Send(request);
    }

    private async Task<TokenPair> Exchange(
        string client,
        string redirect,
        string code,
        string? resource = null
    )
    {
        using var response = await ExchangeResponse(client, redirect, code, resource);
        return await ReadTokens(response);
    }

    private Task<HttpResponseMessage> ExchangeResponse(
        string client,
        string redirect,
        string code,
        string? resource
    ) =>
        SendForm(
            AuthOrigin + "/connect/token",
            new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = client,
                ["redirect_uri"] = redirect,
                ["code"] = code,
                ["code_verifier"] = Verifier,
            }.Concat(
                resource is null ? [] : new Dictionary<string, string> { ["resource"] = resource }
            )
        );

    private Task<HttpResponseMessage> Refresh(string token, string client = ChatGpt) =>
        SendForm(
            AuthOrigin + "/connect/token",
            new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = client,
                ["refresh_token"] = token,
                ["resource"] = Resource,
            }
        );

    private static Dictionary<string, string> AuthorizeFields(
        string client,
        string redirect,
        string scopes,
        string? resource = null
    )
    {
        var fields = new Dictionary<string, string>
        {
            ["client_id"] = client,
            ["redirect_uri"] = redirect,
            ["response_type"] = "code",
            ["scope"] = scopes,
            ["code_challenge"] = Challenge,
            ["code_challenge_method"] = "S256",
            ["state"] = Guid.NewGuid().ToString("N"),
            ["provider"] = "google",
        };
        if (resource is not null)
        {
            fields["resource"] = resource;
        }
        return fields;
    }

    private async Task<JsonElement> Call(string name, object arguments, string? token = null)
    {
        using var response = await SendRpc("tools/call", new { name, arguments }, token);
        return (await Json(response)).GetProperty("result");
    }

    private Task<HttpResponseMessage> SendRpc(
        string method,
        object? parameters = null,
        string? token = null,
        string endpoint = Resource,
        string? origin = null
    )
    {
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(
                new
                {
                    jsonrpc = "2.0",
                    id = 1,
                    method,
                    @params = parameters ?? new { },
                }
            ),
        };
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        request.Headers.Add("MCP-Protocol-Version", "2025-06-18");
        if (origin is not null)
        {
            request.Headers.Add("Origin", origin);
        }
        request.Headers.Authorization = token is null
            ? null
            : new AuthenticationHeaderValue("Bearer", token);
        return Send(request);
    }

    private Task<HttpResponseMessage> Get(string url, string token) =>
        Send(
            new(HttpMethod.Get, url)
            {
                Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) },
            }
        );

    private async Task<HttpResponseMessage> Send(HttpRequestMessage request)
    {
        using (request)
        {
            return await http.SendAsync(request);
        }
    }

    private static string ErrorText(JsonElement result)
    {
        result.GetProperty("isError").GetBoolean().Should().BeTrue(result.ToString());
        return result.GetProperty("content")[0].GetProperty("text").GetString()!;
    }

    private async Task<int> PlaylistCount(string token) =>
        (await Call("my_library", new { limit = 50 }, token))
            .GetProperty("structuredContent")
            .GetProperty("playlists")
            .GetArrayLength();

    private static string? PlaylistName(JsonElement result) =>
        result
            .GetProperty("structuredContent")
            .GetProperty("untrusted_listener_text")
            .GetProperty("name")
            .GetString();

    private static string ChallengeFrom(JsonElement result)
    {
        result.GetProperty("isError").GetBoolean().Should().BeTrue();
        return result.GetProperty("_meta").GetProperty("mcp/www_authenticate")[0].GetString()!;
    }

    private static string ReadCookie(HttpResponseMessage response, string name) =>
        response
            .Headers.GetValues("Set-Cookie")
            .Single(value => value.StartsWith(name + "=", StringComparison.Ordinal))
            .Split(';')[0];

    private static string ReadCode(HttpResponseMessage response)
    {
        response
            .StatusCode.Should()
            .Be(HttpStatusCode.Redirect, response.Headers.Location?.ToString());
        var fields = QueryHelpers.ParseQuery(response.Headers.Location!.Query);
        var code = fields["code"].ToString();
        code.Should().NotBeEmpty(response.Headers.Location!.ToString());
        return code;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
        {
            body = body.Split('\n')
                .Single(line => line.StartsWith("data:", StringComparison.Ordinal))[5..];
        }
        return JsonSerializer.Deserialize<JsonElement>(body);
    }

    private static async Task<string> OAuthError(HttpResponseMessage response)
    {
        if (response.Headers.Location is { } redirect)
        {
            return QueryHelpers.ParseQuery(redirect.Query)["error"].ToString();
        }
        if (response.Content.Headers.ContentType?.MediaType == "application/json")
        {
            return (await response.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("error")
                .GetString()!;
        }
        var lines = (await response.Content.ReadAsStringAsync()).Split('\n');
        return lines
            .Single(line => line.StartsWith("error:", StringComparison.Ordinal))[6..]
            .Trim();
    }

    private static async Task<TokenPair> ReadTokens(HttpResponseMessage response)
    {
        var json = await Json(response);
        return new(
            json.GetProperty("access_token").GetString()!,
            json.GetProperty("refresh_token").GetString()!
        );
    }

    private sealed record TokenPair(string Access, string Refresh);

    private sealed class CatalogStub(Func<Uri, object?>? respond) : HttpMessageHandler
    {
        public Uri? LastRequest { get; private set; }
        public int AuthorizedReviewRequests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            request.Headers.Authorization.Should().BeNull();
            LastRequest = request.RequestUri;
            if (request.Headers.TryGetValues("X-Relisten-Mcp-Review-Key", out var credentials))
            {
                request.RequestUri!.AbsolutePath.Should().EndWith("/reviews/search");
                credentials.Should().Equal(ReviewServiceKey);
                AuthorizedReviewRequests++;
            }
            var body = respond is null
                ? new
                {
                    artists = new[]
                    {
                        new
                        {
                            id = "artist_example",
                            name = "Synthetic Artist",
                            slug = "synthetic",
                        },
                    },
                }
                : respond(request.RequestUri!);

            return Task.FromResult(
                body is null
                    ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = JsonContent.Create(body),
                    }
            );
        }
    }
}
