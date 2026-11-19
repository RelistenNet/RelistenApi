using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using NUnit.Framework;
using Relisten.Accounts.Contracts.Deletion;
using RelistenIngestionWorker.AccountDeletion;
using RelistenUserService;
using RelistenUserService.Authentication;
using RelistenUserService.Identity.Deletion;
using RelistenUserService.Identity.Entities;
using RelistenUserService.Persistence;
using Temporalio.Client;
using Temporalio.Client.Interceptors;
using Temporalio.Exceptions;
using Temporalio.Testing;
using Temporalio.Worker;

namespace RelistenUserServiceTests;

[TestFixture]
[NonParallelizable]
public sealed class TestAccountDeletionApi
{
    private const string AuthOrigin = "https://auth.relisten.localhost:5443";
    private const string ClientId = "relisten-mobile-ios-dev";
    private const string RedirectUri = "net.relisten.mobile:/oauth2redirect/ios";
    private const string Verifier =
        "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-._~abc";
    private const string Challenge = "dYSqskoTcWrpu8GYY0XpWlzOc0c5rd9YO3uAgh_zmV4";
    private static readonly TimeSpan TestDeadline = TimeSpan.FromSeconds(30);

    private PostgresIntegrationDatabase _database = null!;
    private WorkflowEnvironment _temporal = null!;
    private WebApplication _app = null!;
    private HttpClient _auth = null!;
    private HttpClient _accounts = null!;
    private string _directory = "";
    private ObserveWorkflowStarts _starts = null!;
    private PauseAuthorizationRead _pauseRead = null!;

    [OneTimeSetUp]
    public async Task StartTemporal() => _temporal = await WorkflowEnvironment.StartLocalAsync();

    [OneTimeTearDown]
    public async Task StopTemporal()
    {
        if (_temporal is not null)
        {
            await _temporal.DisposeAsync();
        }
    }

    [SetUp]
    public async Task StartDatabase()
    {
        _database = new PostgresIntegrationDatabase();
        await _database.StartAsync();
        _directory = Directory.CreateTempSubdirectory("relisten-deletion-test-").FullName;
        _starts = new ObserveWorkflowStarts();
        _pauseRead = new PauseAuthorizationRead();
    }

    [TearDown]
    public async Task Stop()
    {
        _auth?.Dispose();
        _accounts?.Dispose();
        if (_app is not null)
        {
            await _app.DisposeAsync();
            _app = null!;
        }
        await _database.StopAsync();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Test]
    public async Task AcceptedDeletionPurgesTheUserAndInvalidatesTheSameSession()
    {
        await StartAppAsync();
        var original = await SignInAsync();
        using var response = await _accounts.PostAsync("/v1/account-deletions", null);
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("state")
            .GetString()
            .Should()
            .Be("deleting");

        var job = await GetJobAsync(original.UserId);
        job.CompletedAt.Should().BeNull();
        await RunPurgeAsync(job.Id);

        (await GetJobAsync(original.UserId)).CompletedAt.Should().NotBeNull();
        var handle = _temporal.Client.GetWorkflowHandle(AccountDeletionInput.WorkflowId(job.Id));
        var closedRunId = (await handle.DescribeAsync()).RunId;
        await _app
            .Services.GetRequiredService<AccountDeletionStarter>()
            .StartAsync(new(job.Id, original.UserId), CancellationToken.None);
        (await handle.DescribeAsync()).RunId.Should().Be(closedRunId);

        using var profile = await _accounts.GetAsync("/v1/me");
        profile.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using var retry = await _accounts.PostAsync("/v1/account-deletions", null);
        retry.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var newAccount = await SignInAsync();
        newAccount.UserId.Should().NotBe(original.UserId);
    }

    [Test]
    public async Task RejectedStartLeavesTheSessionActiveAndARepeatReusesTheWorkflow()
    {
        await StartAppAsync();
        var session = await SignInAsync();
        _starts.RejectNext();
        using var failed = await _accounts.PostAsync("/v1/account-deletions", null);
        failed.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        using var profile = await _accounts.GetAsync("/v1/me");
        profile.StatusCode.Should().Be(HttpStatusCode.OK);
        var job = await GetJobAsync(session.UserId);
        job.CompletedAt.Should().BeNull();

        using var retry = await _accounts.PostAsync("/v1/account-deletions", null);
        retry.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var handle = _temporal.Client.GetWorkflowHandle(AccountDeletionInput.WorkflowId(job.Id));
        var execution = await handle.DescribeAsync();

        using var repeat = await _accounts.PostAsync("/v1/account-deletions", null);
        repeat.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await GetJobAsync(session.UserId)).Id.Should().Be(job.Id);
        (await handle.DescribeAsync()).RunId.Should().Be(execution.RunId);
        _starts.RequestedIds.Should().Equal(handle.Id, handle.Id, handle.Id);
        await RunPurgeAsync(job.Id);
    }

    [Test]
    public async Task AWorkflowCompletesItsJobAfterTheStartReplyIsLost()
    {
        await StartAppAsync();
        var session = await SignInAsync();
        _starts.LoseNextReply();
        using var response = await _accounts.PostAsync("/v1/account-deletions", null);
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var job = await GetJobAsync(session.UserId);

        await RunPurgeAsync(job.Id);

        (await GetJobAsync(session.UserId)).CompletedAt.Should().NotBeNull();
        using var retry = await _accounts.PostAsync("/v1/account-deletions", null);
        retry.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task AFailedPurgeRollsBackAndTemporalRetriesTheDatabaseTransaction()
    {
        await StartAppAsync();
        var session = await SignInAsync();
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        // Sequence increments survive rollback. Throw after the OpenIddict deletes so a
        // successful retry must recover from a failure partway through the transaction.
        await connection.ExecuteAsync(
            """
            CREATE SEQUENCE purge_attempts;
            CREATE FUNCTION fail_first_purge() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF nextval('purge_attempts') = 1 THEN
                    RAISE EXCEPTION 'transient failure during account purge';
                END IF;
                RETURN OLD;
            END;
            $$;
            CREATE TRIGGER fail_first_purge BEFORE DELETE ON identity.users
                FOR EACH ROW EXECUTE FUNCTION fail_first_purge();
            """
        );
        using var response = await _accounts.PostAsync("/v1/account-deletions", null);
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var job = await GetJobAsync(session.UserId);

        await RunPurgeAsync(job.Id);

        (await connection.QuerySingleAsync<long>("SELECT last_value FROM purge_attempts"))
            .Should()
            .Be(2);
        var completedAt = (await GetJobAsync(session.UserId)).CompletedAt;
        completedAt.Should().NotBeNull();
        await new AccountPurge(_database.ConnectionString).RunJobAsync(
            new(job.Id, session.UserId),
            CancellationToken.None
        );
        (await GetJobAsync(session.UserId)).CompletedAt.Should().Be(completedAt);
        using var profile = await _accounts.GetAsync("/v1/me");
        profile.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task PurgeAndOAuthAuthorizationCannotLeaveOrphanedRows(bool issuanceLocksFirst)
    {
        await StartAppAsync();
        var session = await SignInAsync();
        // Pause either after Auth SSO read the active user or after issuance took its share
        // lock. The first case lets purge win; the second must make purge wait for issuance.
        _pauseRead.Arm(issuanceLocksFirst ? "FOR SHARE" : "FROM identity.sessions");
        var authorization = AuthorizeAsync(session.AuthSsoCookie);
        Task? purge = null;
        try
        {
            await _pauseRead.Read.WaitAsync(TestDeadline);
            purge = new AccountPurge(_database.ConnectionString).RunForUserAsync(
                session.UserId,
                CancellationToken.None
            );
            if (issuanceLocksFirst)
            {
                await WaitForBlockedPurgeAsync();
            }
            else
            {
                await purge.WaitAsync(TestDeadline);
            }
        }
        finally
        {
            _pauseRead.Resume();
        }

        using var response = await authorization.WaitAsync(TestDeadline);
        if (issuanceLocksFirst)
        {
            response.StatusCode.Should().Be(HttpStatusCode.Redirect);
            await purge!.WaitAsync(TestDeadline);
        }
        else
        {
            ((int)response.StatusCode).Should().BeOneOf(401, 403);
        }

        await AssertNoOAuthRowsAsync(session.UserId);
    }

    [Test]
    public async Task PurgeWaitsForAnInProgressRefreshAndRemovesItsTokens()
    {
        await StartAppAsync();
        var session = await SignInAsync();
        _pauseRead.Arm("FOR SHARE");
        using var grant = new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = ClientId,
                ["refresh_token"] = session.RefreshToken,
            }
        );
        var refresh = _auth.PostAsync("/connect/token", grant);
        Task purge;
        try
        {
            await _pauseRead.Read.WaitAsync(TestDeadline);
            purge = new AccountPurge(_database.ConnectionString).RunForUserAsync(
                session.UserId,
                CancellationToken.None
            );
            await WaitForBlockedPurgeAsync();
        }
        finally
        {
            _pauseRead.Resume();
        }

        using var response = await refresh.WaitAsync(TestDeadline);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await purge.WaitAsync(TestDeadline);
        await AssertNoOAuthRowsAsync(session.UserId);
    }

    private async Task AssertNoOAuthRowsAsync(string userId)
    {
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        var records = await connection.QuerySingleAsync<int>(
            """
            SELECT count(*)::int FROM (
                SELECT id FROM identity.openiddict_tokens WHERE subject = @UserId
                UNION ALL
                SELECT id FROM identity.openiddict_authorizations WHERE subject = @UserId
            ) AS subject_records
            """,
            new { UserId = userId }
        );
        records.Should().Be(0);
    }

    private async Task StartAppAsync()
    {
        var caPath = Path.Combine(_directory, "test-ca.pem");
        using var key = RSA.Create(2048);
        var certificateRequest = new CertificateRequest(
            "CN=Accounts deletion test",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1
        );
        using var certificate = certificateRequest.Create(
            certificateRequest.SubjectName,
            X509SignatureGenerator.CreateForRSA(key, RSASignaturePadding.Pkcs1),
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddHours(1),
            [1]
        );
        await File.WriteAllTextAsync(caPath, certificate.ExportCertificatePem());

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
                ["Accounts:AuthHost"] = "auth.relisten.localhost:5443",
                ["Accounts:AccountsHost"] = "accounts.relisten.localhost:5443",
                ["Accounts:Issuer"] = AuthOrigin,
                ["Accounts:WebClientSecret"] = "account-deletion-test-secret",
                ["Accounts:DevelopmentCertificateAuthorityPath"] = caPath,
                ["Accounts:EnableDevelopmentPersonas"] = "true",
                ["Temporal:Address"] = _temporal.Client.Connection.Options.TargetHost,
            }
        );
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        builder.Services.Configure<TemporalClientConnectOptions>(options =>
            options.Interceptors = [_starts]
        );
        builder.Services.AddDbContext<AccountsDbContext>(
            (_, options) => options.AddInterceptors(_pauseRead)
        );

        _app = AccountsApplication.Build(builder);
        await AccountsApplication.InitializeAsync(_app, CancellationToken.None);
        await _app.StartAsync();
        _auth = _app.GetTestClient();
        _auth.BaseAddress = new Uri(AuthOrigin);
        _accounts = _app.GetTestServer().CreateClient();
        _accounts.BaseAddress = new Uri("https://accounts.relisten.localhost:5443");
    }

    private async Task<NativeLogin> SignInAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/development/sign-in")
        {
            Content = new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["persona"] = "google-alice",
                    ["return_url"] = AuthorizePath(),
                }
            ),
        };
        request.Headers.Add("Origin", AuthOrigin);
        using var signIn = await _auth.SendAsync(request);
        signIn.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var cookie = signIn
            .Headers.GetValues("Set-Cookie")
            .Single(value =>
                value.StartsWith(
                    $"{AuthenticationConstants.AuthSsoCookie}=",
                    StringComparison.Ordinal
                )
            )
            .Split(';', 2)[0];
        using var authorize = await AuthorizeAsync(cookie);
        authorize.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var code = QueryHelpers.ParseQuery(authorize.Headers.Location!.Query)["code"].ToString();
        code.Should().NotBeEmpty();
        using var tokens = await _auth.PostAsync(
            "/connect/token",
            new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["grant_type"] = "authorization_code",
                    ["client_id"] = ClientId,
                    ["redirect_uri"] = RedirectUri,
                    ["code"] = code,
                    ["code_verifier"] = Verifier,
                }
            )
        );
        tokens.StatusCode.Should().Be(HttpStatusCode.OK, await tokens.Content.ReadAsStringAsync());
        var tokenBody = await tokens.Content.ReadFromJsonAsync<JsonElement>();
        _accounts.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            tokenBody.GetProperty("access_token").GetString()
        );

        using var profile = await _accounts.GetAsync("/v1/me");
        profile.StatusCode.Should().Be(HttpStatusCode.OK);
        var profileBody = await profile.Content.ReadFromJsonAsync<JsonElement>();
        return new(
            profileBody.GetProperty("user_id").GetString()!,
            cookie,
            tokenBody.GetProperty("refresh_token").GetString()!
        );
    }

    private async Task<HttpResponseMessage> AuthorizeAsync(string cookie)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, AuthorizePath());
        request.Headers.Add("Cookie", cookie);
        return await _auth.SendAsync(request);
    }

    private static string AuthorizePath() =>
        QueryHelpers.AddQueryString(
            "/connect/authorize",
            new Dictionary<string, string?>
            {
                ["client_id"] = ClientId,
                ["redirect_uri"] = RedirectUri,
                ["response_type"] = "code",
                ["scope"] = "openid profile offline_access user.read account.manage",
                ["code_challenge"] = Challenge,
                ["code_challenge_method"] = "S256",
                ["provider"] = "google",
                ["state"] = Guid.NewGuid().ToString("N"),
            }
        );

    private async Task<AccountDeletionJob> GetJobAsync(string userId)
    {
        await using var database = _database.CreateContext();
        var id = UserId.Parse(userId);
        return await database.AccountDeletionJobs.SingleAsync(job => job.UserId == id);
    }

    private async Task RunPurgeAsync(Guid jobId)
    {
        using var worker = new TemporalWorker(
            _temporal.Client,
            new TemporalWorkerOptions(AccountDeletionInput.TaskQueue)
                .AddWorkflow<AccountDeletionWorkflow>()
                .AddAllActivities(
                    new AccountDeletionActivities(new AccountPurge(_database.ConnectionString))
                )
        );
        var handle = _temporal.Client.GetWorkflowHandle(AccountDeletionInput.WorkflowId(jobId));
        await worker.ExecuteAsync(() => handle.GetResultAsync().WaitAsync(TestDeadline));
    }

    private async Task WaitForBlockedPurgeAsync()
    {
        using var deadline = new CancellationTokenSource(TestDeadline);
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync(deadline.Token);
        while (
            !await connection.QuerySingleAsync<bool>(
                new CommandDefinition(
                    """
                    SELECT EXISTS (
                        SELECT FROM pg_stat_activity
                        WHERE datname = current_database()
                            AND wait_event_type = 'Lock'
                            AND query LIKE 'SELECT id FROM identity.users%FOR UPDATE'
                    )
                    """,
                    cancellationToken: deadline.Token
                )
            )
        )
        {
            await Task.Delay(20, deadline.Token);
        }
    }

    private sealed record NativeLogin(string UserId, string AuthSsoCookie, string RefreshToken);

    private sealed class ObserveWorkflowStarts : IClientInterceptor
    {
        private bool _reject;
        private bool _loseReply;

        public List<string> RequestedIds { get; } = [];

        public void RejectNext() => _reject = true;

        public void LoseNextReply() => _loseReply = true;

        public ClientOutboundInterceptor InterceptClient(ClientOutboundInterceptor next) =>
            new StartObserver(next, this);

        private sealed class StartObserver(
            ClientOutboundInterceptor next,
            ObserveWorkflowStarts starts
        ) : ClientOutboundInterceptor(next)
        {
            public override async Task<WorkflowHandle<TWorkflow, TResult>> StartWorkflowAsync<
                TWorkflow,
                TResult
            >(StartWorkflowInput input)
            {
                starts.RequestedIds.Add(input.Options.Id!);
                if (starts._reject)
                {
                    starts._reject = false;
                    throw new RpcException(
                        RpcException.StatusCode.InvalidArgument,
                        "Synthetic rejected start",
                        []
                    );
                }

                var handle = await base.StartWorkflowAsync<TWorkflow, TResult>(input);
                if (starts._loseReply)
                {
                    starts._loseReply = false;
                    throw new RpcException(
                        RpcException.StatusCode.Unavailable,
                        "Synthetic lost start reply",
                        []
                    );
                }

                return handle;
            }
        }
    }

    private sealed class PauseAuthorizationRead : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _read = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private readonly TaskCompletionSource _resume = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private string? _sql;

        public Task Read => _read.Task;

        public void Arm(string sql) => _sql = sql;

        public void Resume() => _resume.TrySetResult();

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default
        )
        {
            if (_sql is { } sql && command.CommandText.Contains(sql, StringComparison.Ordinal))
            {
                _sql = null;
                _read.TrySetResult();
                await _resume.Task.WaitAsync(cancellationToken);
            }

            return result;
        }
    }
}
