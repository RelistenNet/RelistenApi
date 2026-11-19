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
using RelistenIngestionWorker.AccountDeletion;
using RelistenUserService;
using RelistenUserService.Authentication;
using RelistenUserService.Mcp;

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
    private const string Native = "relisten-mobile-ios-dev";
    private const string NativeRedirect = "net.relisten.mobile:/oauth2redirect/ios";
    private const string Verifier =
        "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-._~abc";
    private const string Challenge = "dYSqskoTcWrpu8GYY0XpWlzOc0c5rd9YO3uAgh_zmV4";
    private const string ReviewServiceKey = "synthetic-catalog-review-key-for-tests-only";
    private const string ReadScopes = "openid offline_access user.read library.read";

    private readonly CatalogStub catalog = new();
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
                .BeEquivalentTo("user.read", "library.read");
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

        using var existingGrant = await Authorize(
            AuthorizeFields(ChatGpt, ChatGptRedirect, ReadScopes, Resource),
            cookie
        );
        ReadCode(existingGrant).Should().NotBeEmpty();
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
            .GetProperty("metadata")
            .GetProperty("name")
            .GetString()
            .Should()
            .Be("Renamed guide");
        playlist
            .GetProperty("segments")[0]
            .GetProperty("entries")[0]
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
        using var newConsent = await Authorize(
            AuthorizeFields(ChatGpt, ChatGptRedirect, ReadScopes, Resource),
            cookie
        );
        newConsent.StatusCode.Should().Be(HttpStatusCode.OK);
        using var sameNativeSession = await Get(AccountsOrigin + "/v1/me", native.Access);
        sameNativeSession.StatusCode.Should().Be(HttpStatusCode.OK);
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
            .Be("consent_remembered");
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

    private static readonly object Draft = new
    {
        name = "Listening guide",
        description = "Guide description",
        segments = new[] { new { entries = new[] { new { description = "Opening note" } } } },
    };

    private async Task StartApp(bool mountCatalog = true)
    {
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
        consent
            .Headers.GetValues("Content-Security-Policy")
            .Single()
            .Should()
            .Contain("form-action 'self' https://chatgpt.com;");
        consent.StatusCode.Should().Be(HttpStatusCode.OK, html);
        html.Should().Contain("chatgpt.com").And.Contain(scopeDescription);
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

    private Task<HttpResponseMessage> Refresh(string token) =>
        SendForm(
            AuthOrigin + "/connect/token",
            new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = ChatGpt,
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

    private sealed class CatalogStub : HttpMessageHandler
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
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(
                        new
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
                    ),
                }
            );
        }
    }
}
