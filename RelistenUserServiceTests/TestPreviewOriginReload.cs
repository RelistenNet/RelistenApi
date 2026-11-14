using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Npgsql;
using NUnit.Framework;
using OpenIddict.Abstractions;
using OpenIddict.Client;
using RelistenUserService;
using RelistenUserService.Authentication;
using RelistenUserService.Authentication.OpenIdConnect;
using RelistenUserService.Authentication.Sessions;
using RelistenUserService.Configuration;
using RelistenUserService.Identity.Entities;
using RelistenUserService.Persistence;

namespace RelistenUserServiceTests;

[TestFixture]
[NonParallelizable]
public sealed class TestPreviewOriginReload
{
    private const string PreviewA = "https://relisten-pr-138.tail09dbf.ts.net";
    private const string PreviewB = "https://relisten-pr-139.tail09dbf.ts.net";
    private readonly PostgresIntegrationDatabase _database = new();
    private string _directory = "";
    private WebApplication _app = null!;
    private PreviewOriginReload _reload = null!;
    private ProofClock _clock = null!;
    private bool _rejectOptions;

    [SetUp]
    public async Task Start()
    {
        await _database.StartAsync();
        _directory = Path.Combine(Path.GetTempPath(), $"relisten-preview-proof-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=proof-only",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1
        );
        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(1)
        );
        var caPath = Path.Combine(_directory, "ca.pem");
        await File.WriteAllTextAsync(caPath, certificate.ExportCertificatePem());

        await StartApplicationAsync(caPath);
    }

    private async Task StartApplicationAsync(string caPath, string? previewFile = null)
    {
        var builder = WebApplication.CreateBuilder(
            new WebApplicationOptions
            {
                EnvironmentName = Environments.Development,
                ContentRootPath = _directory,
                ApplicationName = typeof(AccountsApplication).Assembly.GetName().Name,
            }
        );
        // No .NET user secrets, ambient connection strings, or real provider
        // credentials participate in this disposable local proof.
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["ACCOUNTS_DATABASE_URL"] = _database.ConnectionString,
                ["ACCOUNTS_LOCK_DATABASE_URL"] = _database.ConnectionString,
                ["AllowedHosts"] =
                    "auth.relisten.localhost;accounts.relisten.localhost;relisten.net",
                ["Accounts:Issuer"] = ProofBrowser.Issuer,
                ["Accounts:PreviewOriginsFile"] = previewFile,
                ["Accounts:AuthHost"] = "auth.relisten.localhost:5443",
                ["Accounts:AccountsHost"] = "accounts.relisten.localhost:5443",
                ["Accounts:WebClientSecret"] = "disposable-proof-secret",
                ["Accounts:EnableDevelopmentPersonas"] = "true",
                ["Accounts:DevelopmentCertificateAuthorityPath"] = caPath,
            }
        );
        _clock = new ProofClock();
        builder.Services.AddSingleton<TimeProvider>(_clock);
        builder.Services.PostConfigure<OpenIddictClientOptions>(_ =>
        {
            if (_rejectOptions)
            {
                throw new InvalidOperationException("Injected options validation failure.");
            }
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        builder.Services.PostConfigureAll<HttpClientFactoryOptions>(options =>
            options.HttpMessageHandlerBuilderActions.Add(http =>
                http.AdditionalHandlers.Insert(
                    0,
                    new ProofBackchannel(() => _app.GetTestServer().CreateHandler())
                )
            )
        );
        _app = AccountsApplication.Build(builder);
        await AccountsApplication.InitializeAsync(_app, CancellationToken.None);
        await _app.StartAsync();
        _reload = _app.Services.GetRequiredService<PreviewOriginReload>();
    }

    [TearDown]
    public async Task Stop()
    {
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
    public async Task Real_browser_protocol_survives_add_retire_and_readd_without_restarting()
    {
        using var canonical = Browser();
        await canonical.LoginAsync(AuthenticationConstants.CanonicalWebOrigin);
        var canonicalInFlight = await canonical.BeginLoginAsync(
            AuthenticationConstants.CanonicalWebOrigin
        );

        await _reload.ApplyAsync(1, [PreviewA, PreviewB]);
        await canonical.CompleteLoginAsync(canonicalInFlight);
        using var first = Browser();
        using var second = Browser();
        await first.LoginAsync(PreviewA);
        await second.LoginAsync(PreviewB);
        (await first.SendAsync(PreviewA + "/v1/me")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await second.SendAsync(PreviewB + "/v1/me")).StatusCode.Should().Be(HttpStatusCode.OK);
        var firstCookie = first.SessionCookie(PreviewA);
        var firstToken = await first.CsrfAsync(PreviewA);

        // A real library mutation, not just an options or redirect assertion.
        using var mutation = JsonContent.Create(
            new
            {
                contract_version = 1,
                mutations = new[]
                {
                    new
                    {
                        mutation_id = MutationId.New().Value,
                        target = new
                        {
                            kind = "artist",
                            id = Relisten.Identifiers.UuidTypeId.Format("artist", Guid.NewGuid()),
                        },
                        desired_state = "favorite",
                        favorite_id = FavoriteId.New().Value,
                    },
                },
            }
        );
        var written = await first.SendAsync(
            PreviewA + "/v1/library/favorite-mutations:batch",
            HttpMethod.Post,
            mutation,
            PreviewA,
            firstToken
        );
        written.StatusCode.Should().Be(HttpStatusCode.OK);
        var shared = await second.SendAsync(PreviewB + "/v1/library/snapshot");
        using (var json = JsonDocument.Parse(await shared.Content.ReadAsStringAsync()))
        {
            json.RootElement.GetProperty("favorites").GetArrayLength().Should().Be(1);
        }

        using var replay = Browser();
        replay.SetSessionCookie(PreviewB, firstCookie);
        (await replay.SendAsync(PreviewB + "/v1/me"))
            .StatusCode.Should()
            .Be(HttpStatusCode.Unauthorized);
        (
            await second.SendAsync(
                PreviewB + "/auth/session/logout",
                HttpMethod.Post,
                origin: PreviewB,
                csrf: firstToken
            )
        )
            .StatusCode.Should()
            .Be(HttpStatusCode.Forbidden);

        // Exercise renewal of the existing session across a registry update.
        var secondInFlight = await second.BeginLoginAsync(PreviewB);
        var retiredInFlight = await first.BeginLoginAsync(PreviewA);
        await _reload.ApplyAsync(2, [PreviewB]);
        (await first.SendAsync(retiredInFlight.ToString()))
            .StatusCode.Should()
            .Be(HttpStatusCode.BadRequest);
        await second.CompleteLoginAsync(secondInFlight);
        _clock.Advance(TimeSpan.FromHours(2));
        var renewal = await second.SendAsync(PreviewB + "/v1/me");
        renewal.StatusCode.Should().Be(HttpStatusCode.OK);
        renewal
            .Headers.GetValues("Set-Cookie")
            .Should()
            .Contain(value => value.StartsWith(AuthenticationConstants.WebSessionCookie + "="));
        (await first.SendAsync(PreviewA + "/v1/me"))
            .StatusCode.Should()
            .Be(HttpStatusCode.BadRequest);
        (await canonical.SendAsync(AuthenticationConstants.CanonicalWebOrigin + "/v1/me"))
            .StatusCode.Should()
            .Be(HttpStatusCode.OK);
        var renewed = await second.SendAsync(PreviewB + "/v1/me");
        renewed.StatusCode.Should().Be(HttpStatusCode.OK);

        await _reload.ApplyAsync(3, [PreviewA, PreviewB]);
        (await first.SendAsync(PreviewA + "/v1/me"))
            .StatusCode.Should()
            .Be(
                HttpStatusCode.Unauthorized,
                "retiring an origin revokes its old sessions even if that hostname is reused"
            );
        await first.LoginAsync(PreviewA);
        var token = await first.CsrfAsync(PreviewA);
        var switched = await first.SendAsync(
            PreviewA + "/auth/session/switch-account",
            HttpMethod.Post,
            origin: PreviewA,
            csrf: token
        );
        switched.StatusCode.Should().Be(HttpStatusCode.OK);
        await first.LoginAsync(PreviewA);
        token = await first.CsrfAsync(PreviewA);
        (
            await first.SendAsync(
                PreviewA + "/auth/session/logout",
                HttpMethod.Post,
                origin: PreviewA,
                csrf: token
            )
        )
            .StatusCode.Should()
            .Be(HttpStatusCode.OK);
        (await first.SendAsync(PreviewA + "/v1/me"))
            .StatusCode.Should()
            .Be(HttpStatusCode.Unauthorized);

        await using var scope = _app.Services.CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var application = (await manager.FindByClientIdAsync(AuthenticationConstants.WebClientId))!;
        (await manager.ValidateClientSecretAsync(application, "disposable-proof-secret"))
            .Should()
            .BeTrue();
        (await manager.GetRedirectUrisAsync(application))
            .Should()
            .Contain(AuthenticationConstants.CanonicalWebCallback);
        TestContext.Progress.WriteLine(
            "Completed canonical and two preview logins, shared write, retirement/re-add, CSRF rejection and logout in one application instance."
        );
    }

    [Test]
    public async Task Invalid_and_stale_updates_leave_canonical_auth_working()
    {
        using var browser = Browser();
        await browser.LoginAsync(AuthenticationConstants.CanonicalWebOrigin);
        await _reload.ApplyAsync(1, [PreviewA]);
        var current = _app
            .Services.GetRequiredService<AccountsRuntimeConfiguration>()
            .BrowserOrigins.Current;
        var invalid = () =>
            _reload.ApplyAsync(
                current.Revision + 1,
                ["https://relisten-pr-1.tail09dbf.ts.net.evil.example"]
            );
        await invalid.Should().ThrowAsync<ArgumentException>();
        var stale = () => _reload.ApplyAsync(current.Revision, []);
        await stale.Should().ThrowAsync<InvalidOperationException>();
        (await browser.SendAsync(AuthenticationConstants.CanonicalWebOrigin + "/v1/me"))
            .StatusCode.Should()
            .Be(HttpStatusCode.OK);
        _app.Services.GetRequiredService<AccountsRuntimeConfiguration>()
            .BrowserOrigins.Current.Should()
            .BeSameAs(current);
    }

    [Test]
    public async Task Failed_options_reload_preserves_existing_callbacks_and_sessions()
    {
        await _reload.ApplyAsync(1, [PreviewA]);
        using var browser = Browser();
        await browser.LoginAsync(PreviewA);
        var callback = await browser.BeginLoginAsync(PreviewA);
        _rejectOptions = true;
        try
        {
            var apply = () => _reload.ApplyAsync(2, [PreviewB]);
            await apply
                .Should()
                .ThrowAsync<InvalidOperationException>()
                .WithMessage("Injected options*");
            await browser.CompleteLoginAsync(callback);
            (await browser.SendAsync(PreviewA + "/v1/me"))
                .StatusCode.Should()
                .Be(HttpStatusCode.OK);
            using var canonical = Browser();
            await canonical.LoginAsync(AuthenticationConstants.CanonicalWebOrigin);
        }
        finally
        {
            _rejectOptions = false;
        }
    }

    [Test]
    public async Task Database_failure_rolls_back_callbacks_sessions_and_the_application_cache()
    {
        await _reload.ApplyAsync(1, [PreviewA]);
        using var browser = Browser();
        await browser.LoginAsync(PreviewA);
        var callback = await browser.BeginLoginAsync(PreviewA);
        await using var database = _database.CreateContext();
        await database.Database.ExecuteSqlRawAsync(
            """
            CREATE FUNCTION identity.reject_proof_retirement() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'Injected retirement failure'; END $$;
            CREATE TRIGGER reject_proof_retirement BEFORE UPDATE OF revoked_at ON identity.sessions
                FOR EACH ROW EXECUTE FUNCTION identity.reject_proof_retirement();
            """
        );
        try
        {
            var apply = () => _reload.ApplyAsync(2, []);
            await apply.Should().ThrowAsync<PostgresException>();
        }
        finally
        {
            await database.Database.ExecuteSqlRawAsync(
                """
                DROP TRIGGER reject_proof_retirement ON identity.sessions;
                DROP FUNCTION identity.reject_proof_retirement();
                """
            );
        }
        // Completing an issued code also exercises server-side callback validation,
        // including the manager's cache after the failed transaction.
        await browser.CompleteLoginAsync(callback);
        (await browser.SendAsync(PreviewA + "/v1/me")).StatusCode.Should().Be(HttpStatusCode.OK);
        await _reload.ApplyAsync(2, [PreviewA, PreviewB]);
        using var second = Browser();
        await second.LoginAsync(PreviewB);
    }

    [Test]
    public async Task Retirement_after_restart_uses_persisted_origins_to_revoke_sessions()
    {
        await _reload.ApplyAsync(1, [PreviewA]);
        using var original = Browser();
        await original.LoginAsync(PreviewA);
        var oldCookie = original.SessionCookie(PreviewA);
        await _app.DisposeAsync();
        await StartApplicationAsync(Path.Combine(_directory, "ca.pem"));
        await _reload.ApplyAsync(1, [PreviewB]);
        await _reload.ApplyAsync(2, [PreviewA, PreviewB]);
        using var restarted = Browser();
        restarted.SetSessionCookie(PreviewA, oldCookie);
        (await restarted.SendAsync(PreviewA + "/v1/me"))
            .StatusCode.Should()
            .Be(HttpStatusCode.Unauthorized);
        await restarted.LoginAsync(PreviewA);
    }

    [Test]
    public async Task Retired_login_callback_cannot_create_a_session_after_immediate_readd()
    {
        await _reload.ApplyAsync(1, [PreviewA]);
        using var browser = Browser();
        var callback = await browser.BeginLoginAsync(PreviewA);
        await _reload.ApplyAsync(2, []);
        await _reload.ApplyAsync(3, [PreviewA]);
        (await browser.SendAsync(callback.ToString()))
            .StatusCode.Should()
            .Be(HttpStatusCode.BadRequest);
        (await browser.SendAsync(PreviewA + "/v1/me"))
            .StatusCode.Should()
            .Be(HttpStatusCode.Unauthorized);
        await browser.LoginAsync(PreviewA);
    }

    [Test]
    public async Task Authenticated_callback_paused_before_session_creation_cannot_cross_retirement()
    {
        await _reload.ApplyAsync(1, [PreviewA]);
        using var browser = Browser();
        await browser.LoginAsync(PreviewA);
        var registry = _app
            .Services.GetRequiredService<AccountsRuntimeConfiguration>()
            .BrowserOrigins;
        var oldRegistration = registry.RegistrationId(PreviewA)!;
        await using var scope = _app.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AccountsDbContext>();
        var original = await database
            .Sessions.AsNoTracking()
            .SingleAsync(session => session.Purpose == IdentitySessionPurposes.Web);
        // Model the exact interleaving: callback authentication has finished, but
        // session creation resumes only after retirement and re-add have committed.
        await _reload.ApplyAsync(2, []);
        await _reload.ApplyAsync(3, [PreviewA]);
        var lifecycle = scope.ServiceProvider.GetRequiredService<IdentitySessionLifecycle>();
        var stale = () =>
            lifecycle.CreateWebAsync(
                original.UserId,
                original.AuthSsoSessionId!.Value,
                PreviewA,
                oldRegistration,
                CancellationToken.None
            );
        await stale.Should().ThrowAsync<InvalidOperationException>();
        (
            await database.Sessions.CountAsync(session =>
                session.Purpose == IdentitySessionPurposes.Web && session.RevokedAt == null
            )
        )
            .Should()
            .Be(0);
        var current = await lifecycle.CreateWebAsync(
            original.UserId,
            original.AuthSsoSessionId!.Value,
            PreviewA,
            registry.RegistrationId(PreviewA)!,
            CancellationToken.None
        );
        current.CookieValue.Should().NotBeNullOrEmpty();
    }

    [Test]
    public async Task Projected_file_replacement_reloads_auth_and_bad_or_missing_files_preserve_it()
    {
        var path = Path.Combine(_directory, "preview-origins.json");
        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(
                new
                {
                    version = 1,
                    revision = 1,
                    origins = new[] { PreviewA, PreviewB },
                }
            )
        );
        await _app.DisposeAsync();
        await StartApplicationAsync(Path.Combine(_directory, "ca.pem"), path);
        var loader = _app.Services.GetRequiredService<PreviewOriginFileLoader>();
        await WaitForAsync(() =>
            loader.Status == new PreviewOriginFileStatus(1, PreviewOriginSourceState.Ready)
        );
        using var first = Browser();
        using var second = Browser();
        await first.LoginAsync(PreviewA);
        await second.LoginAsync(PreviewB);
        await ReplaceFileAsync("not json");
        await WaitForAsync(() =>
            loader.Status == new PreviewOriginFileStatus(1, PreviewOriginSourceState.Invalid)
        );
        (await first.SendAsync(PreviewA + "/v1/me")).StatusCode.Should().Be(HttpStatusCode.OK);
        await ReplaceFileAsync(
            JsonSerializer.Serialize(
                new
                {
                    version = 1,
                    revision = 2,
                    origins = new[] { PreviewB },
                }
            )
        );
        await WaitForAsync(() =>
            loader.Status == new PreviewOriginFileStatus(2, PreviewOriginSourceState.Ready)
        );
        (await first.SendAsync(PreviewA + "/v1/me"))
            .StatusCode.Should()
            .Be(HttpStatusCode.BadRequest);
        (await second.SendAsync(PreviewB + "/v1/me")).StatusCode.Should().Be(HttpStatusCode.OK);
        File.Delete(path);
        await WaitForAsync(() =>
            loader.Status == new PreviewOriginFileStatus(2, PreviewOriginSourceState.Missing)
        );
        using var canonical = Browser();
        await canonical.LoginAsync(AuthenticationConstants.CanonicalWebOrigin);
        (await second.SendAsync(PreviewB + "/v1/me")).StatusCode.Should().Be(HttpStatusCode.OK);

        await ReplaceFileAsync("""{"version":1,"revision":3,"origins":[]}""");
        await WaitForAsync(() =>
            loader.Status == new PreviewOriginFileStatus(3, PreviewOriginSourceState.Ready)
        );
        (await second.SendAsync(PreviewB + "/v1/me"))
            .StatusCode.Should()
            .Be(HttpStatusCode.BadRequest);
        (await canonical.SendAsync(AuthenticationConstants.CanonicalWebOrigin + "/v1/me"))
            .StatusCode.Should()
            .Be(HttpStatusCode.OK);

        async Task ReplaceFileAsync(string json)
        {
            var next = path + ".next";
            await File.WriteAllTextAsync(next, json);
            File.Move(next, path, overwrite: true);
        }
    }

    private static async Task WaitForAsync(Func<bool> ready)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        while (!ready())
        {
            await Task.Delay(25, timeout.Token);
        }
    }

    private sealed class ProofClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan value) => _now += value;
    }

    private ProofBrowser Browser() => new(_app.GetTestServer().CreateHandler());

    private sealed class ProofBackchannel(Func<HttpMessageHandler> createHandler)
        : DelegatingHandler
    {
        private readonly Lazy<HttpMessageInvoker> _transport = new(() => new(createHandler()));

        protected override void Dispose(bool disposing)
        {
            if (disposing && _transport.IsValueCreated)
            {
                _transport.Value.Dispose();
            }
            base.Dispose(disposing);
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            if (request.RequestUri!.GetLeftPart(UriPartial.Authority) != ProofBrowser.Issuer)
            {
                throw new InvalidOperationException(
                    "The proof cannot call any external auth endpoint."
                );
            }
            return _transport.Value.SendAsync(request, cancellationToken);
        }
    }

    /// <summary>
    /// A cookie-preserving browser and the planned same-origin gateway relay.
    /// Only the wire transport is in-process; the actual application, controllers,
    /// OAuth signing/code exchange and database are exercised.
    /// </summary>
    private sealed class ProofBrowser(HttpMessageHandler handler) : IDisposable
    {
        public const string Issuer = "https://auth.relisten.localhost:5443";
        private const string Accounts = "https://accounts.relisten.localhost:5443";
        private readonly CookieContainer _cookies = new();
        private readonly HttpClient _client = new(handler);

        public async Task<HttpResponseMessage> SendAsync(
            string url,
            HttpMethod? method = null,
            HttpContent? content = null,
            string? origin = null,
            string? csrf = null
        )
        {
            var browserUri = new Uri(url);
            var isWeb = browserUri.GetLeftPart(UriPartial.Authority) != Issuer;
            var request = new HttpRequestMessage(
                method ?? HttpMethod.Get,
                isWeb ? new Uri(Accounts + browserUri.PathAndQuery) : browserUri
            );
            request.Content = content;
            if (isWeb)
            {
                request.Headers.Add(
                    AuthenticationConstants.WebOriginHeader,
                    browserUri.GetLeftPart(UriPartial.Authority)
                );
            }
            var cookies = _cookies.GetCookieHeader(browserUri);
            if (cookies.Length > 0)
            {
                request.Headers.Add("Cookie", cookies);
            }
            if (origin is not null)
            {
                request.Headers.Add("Origin", origin);
            }
            if (csrf is not null)
            {
                request.Headers.Add(AuthenticationConstants.CsrfHeader, csrf);
            }
            var response = await _client.SendAsync(request);
            if (response.Headers.TryGetValues("Set-Cookie", out var values))
            {
                foreach (var cookie in values)
                {
                    _cookies.SetCookies(browserUri, cookie);
                }
            }
            return response;
        }

        public async Task<Uri> BeginLoginAsync(string origin)
        {
            var current = new Uri(origin + "/auth/session/start?return_to=/proof-return");
            for (var step = 0; step < 12; step++)
            {
                if (current.AbsolutePath == "/auth/session/callback")
                {
                    return current;
                }
                HttpResponseMessage response;
                if (current.AbsolutePath == "/development/sign-in")
                {
                    response = await SendAsync(
                        current.ToString(),
                        HttpMethod.Post,
                        new FormUrlEncodedContent(
                            new Dictionary<string, string>
                            {
                                ["persona"] = "apple-shared",
                                ["return_url"] = QueryHelpers
                                    .ParseQuery(current.Query)["return_url"]
                                    .ToString(),
                            }
                        ),
                        Issuer
                    );
                }
                else
                {
                    response = await SendAsync(current.ToString());
                }
                response
                    .StatusCode.Should()
                    .Be(
                        HttpStatusCode.Redirect,
                        "login must advance at {0}{1}",
                        current.Host,
                        current.AbsolutePath
                    );
                current = new Uri(current, response.Headers.Location!);
            }
            throw new InvalidOperationException("The local login did not reach a callback.");
        }

        public async Task CompleteLoginAsync(Uri callback)
        {
            var response = await SendAsync(callback.ToString());
            response.StatusCode.Should().Be(HttpStatusCode.Redirect);
            response.Headers.Location!.OriginalString.Should().Be("/proof-return");
        }

        public async Task LoginAsync(string origin) =>
            await CompleteLoginAsync(await BeginLoginAsync(origin));

        public async Task<string> CsrfAsync(string origin)
        {
            var response = await SendAsync(origin + "/api/user/v1/csrf");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return json.RootElement.GetProperty("request_token").GetString()!;
        }

        public string SessionCookie(string origin) =>
            _cookies.GetCookies(new Uri(origin))[AuthenticationConstants.WebSessionCookie]!.Value;

        public void SetSessionCookie(string origin, string value) =>
            _cookies.SetCookies(
                new Uri(origin),
                AuthenticationConstants.WebSessionCookie
                    + "="
                    + value
                    + "; Path=/; Secure; HttpOnly"
            );

        public void Dispose() => _client.Dispose();
    }
}

[TestFixture]
public sealed class TestPreviewOriginPolicy
{
    [TestCase("https://pr-1.preview.relisten.net")]
    [TestCase("https://relisten-pr-1.other-tailnet.ts.net")]
    [TestCase("https://relisten-preview-probe.tail09dbf.ts.net")]
    [TestCase("https://relisten-pr-1.tail09dbf.ts.net.evil.example")]
    [TestCase("http://relisten-pr-1.tail09dbf.ts.net")]
    [TestCase("https://relisten-pr-0.tail09dbf.ts.net")]
    [TestCase("https://relisten-pr-01.tail09dbf.ts.net")]
    [TestCase("https://relisten-pr-1.tail09dbf.ts.net:443")]
    [TestCase("https://relisten-pr-1.tail09dbf.ts.net/")]
    [TestCase("https://user@relisten-pr-1.tail09dbf.ts.net")]
    [TestCase("https://relisten-pr-1.tail09dbf.ts.net?x=1")]
    [TestCase("https://relisten-pr-1.tail09dbf.ts.net\n")]
    public void Preview_origins_must_be_exact_generated_origins(string origin)
    {
        var parse = () => WebOriginRegistry.Parse(1, [origin]);
        parse.Should().Throw<ArgumentException>();
    }
}
