using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using RelistenUserService.Authentication;
using RelistenUserService.Authentication.Sessions;
using RelistenUserService.Configuration;
using RelistenUserService.Identity.Entities;

namespace RelistenUserServiceTests;

[TestFixture]
[NonParallelizable]
public sealed class TestIdentitySessionLifecycleIntegration
{
    private readonly PostgresIntegrationDatabase _database = new();
    private readonly ManualTimeProvider _clock = new(
        new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero));
    private AccountsRuntimeConfiguration _runtime = null!;
    private Guid _userId;

    [OneTimeSetUp]
    public async Task SetUpDatabase()
    {
        await _database.StartAsync();
        _runtime = new AccountsRuntimeConfiguration(
            new AccountsOptions(),
            new Uri("https://auth.relisten.net"),
            TrustedProxyNetworks: []);
    }

    [OneTimeTearDown]
    public Task TearDownDatabase() => _database.StopAsync();

    [SetUp]
    public async Task CreateUser()
    {
        _clock.SetUtcNow(new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero));
        _userId = await _database.CreateUserAsync($"s_{Guid.NewGuid():N}"[..30]);
    }

    [Test]
    public async Task Creates_hash_only_auth_sso_and_web_sessions_with_exact_lifetimes()
    {
        IssuedIdentitySession authSso;
        await using (var dbContext = _database.CreateContext())
        {
            authSso = await Lifecycle(dbContext).CreateAuthSsoAsync(
                _userId,
                CancellationToken.None);
        }

        IssuedIdentitySession web;
        await using (var dbContext = _database.CreateContext())
        {
            web = await Lifecycle(dbContext).CreateWebAsync(
                _userId,
                authSso.SessionId,
                "https://web.relisten.localhost:5173",
                CancellationToken.None);
        }

        await using var verificationContext = _database.CreateContext();
        var authRow = await verificationContext.Sessions.AsNoTracking()
            .SingleAsync(session => session.Id == authSso.SessionId);
        var webRow = await verificationContext.Sessions.AsNoTracking()
            .SingleAsync(session => session.Id == web.SessionId);
        var codec = new SessionCredentialCodec();
        codec.TryParse(web.CookieValue, out var parsed).Should().BeTrue();

        authRow.Purpose.Should().Be(IdentitySessionPurposes.AuthSso);
        authRow.Capabilities.Should().Be(IdentitySessionCapabilities.None);
        authRow.SlidingExpiresAt.Should().Be(_clock.GetUtcNow() + TimeSpan.FromDays(30));
        authRow.AbsoluteExpiresAt.Should().Be(authRow.SlidingExpiresAt);
        webRow.Purpose.Should().Be(IdentitySessionPurposes.Web);
        webRow.AuthSsoSessionId.Should().Be(authRow.Id);
        webRow.Capabilities.Should().Be(IdentitySessionCapabilities.AllWeb);
        webRow.SlidingExpiresAt.Should().Be(_clock.GetUtcNow() + TimeSpan.FromDays(30));
        webRow.AbsoluteExpiresAt.Should().Be(_clock.GetUtcNow() + TimeSpan.FromDays(180));
        webRow.ValidatorHash.Should().Equal(parsed!.ValidatorHash);
        webRow.ValidatorHash.Should().HaveCount(32);
        (await verificationContext.NativeSessions
                .AnyAsync(session => session.UserId == _userId))
            .Should().BeFalse();
        parsed.Clear();
    }

    [Test]
    public async Task Validates_purpose_validator_user_state_and_security_version()
    {
        var authSso = await CreateAuthSsoAsync();

        (await AuthenticateAsync(authSso.CookieValue, IdentitySessionPurposes.AuthSso))
            .Should().NotBeNull();
        (await AuthenticateAsync(authSso.CookieValue, IdentitySessionPurposes.Web))
            .Should().BeNull();

        var changedCredential = authSso.CookieValue.ToCharArray();
        changedCredential[^1] = changedCredential[^1] == 'A' ? 'B' : 'A';
        (await AuthenticateAsync(
            new string(changedCredential),
            IdentitySessionPurposes.AuthSso)).Should().BeNull();

        await using (var dbContext = _database.CreateContext())
        {
            await dbContext.Users
                .Where(user => user.Id == _userId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(
                        user => user.SecurityVersion,
                        user => user.SecurityVersion + 1));
        }

        (await AuthenticateAsync(authSso.CookieValue, IdentitySessionPurposes.AuthSso))
            .Should().BeNull();
    }

    [Test]
    public async Task Touches_at_most_once_per_hour_and_caps_web_sliding_expiry()
    {
        var authSso = await CreateAuthSsoAsync();
        var web = await CreateWebAsync(authSso.SessionId);

        _clock.Advance(TimeSpan.FromHours(1));
        var attempts = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ =>
                AuthenticateAsync(web.CookieValue, IdentitySessionPurposes.Web)));

        attempts.Should().OnlyContain(result => result != null);
        attempts.Count(result => result!.WasTouched).Should().Be(1);
        await using (var dbContext = _database.CreateContext())
        {
            var row = await dbContext.Sessions.AsNoTracking()
                .SingleAsync(session => session.Id == web.SessionId);
            row.LastSeenAt.Should().Be(_clock.GetUtcNow());
            row.SlidingExpiresAt.Should().Be(_clock.GetUtcNow() + TimeSpan.FromDays(30));
        }

        _clock.Advance(TimeSpan.FromDays(150));
        await using (var dbContext = _database.CreateContext())
        {
            var row = await dbContext.Sessions
                .SingleAsync(session => session.Id == web.SessionId);
            row.LastSeenAt = _clock.GetUtcNow() - TimeSpan.FromHours(1);
            row.SlidingExpiresAt = _clock.GetUtcNow() + TimeSpan.FromMinutes(30);
            row.UpdatedAt = _clock.GetUtcNow();
            await dbContext.SaveChangesAsync();
        }

        var finalTouch = await AuthenticateAsync(
            web.CookieValue,
            IdentitySessionPurposes.Web);
        finalTouch!.WasTouched.Should().BeTrue();
        finalTouch.ExpiresAt.Should().Be(web.ExpiresAt + TimeSpan.FromDays(150));
    }

    [Test]
    public async Task Parent_natural_expiry_does_not_shorten_web_but_revocation_does()
    {
        var authSso = await CreateAuthSsoAsync();
        var web = await CreateWebAsync(authSso.SessionId);

        _clock.Advance(TimeSpan.FromDays(31));
        await using (var dbContext = _database.CreateContext())
        {
            await dbContext.Sessions
                .Where(session => session.Id == web.SessionId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(session => session.LastSeenAt, _clock.GetUtcNow())
                    .SetProperty(
                        session => session.SlidingExpiresAt,
                        _clock.GetUtcNow() + TimeSpan.FromDays(30))
                    .SetProperty(session => session.UpdatedAt, _clock.GetUtcNow()));
        }

        (await AuthenticateAsync(web.CookieValue, IdentitySessionPurposes.Web))
            .Should().NotBeNull();

        await using (var dbContext = _database.CreateContext())
        {
            await Lifecycle(dbContext).RevokeWebAndParentAsync(
                web.SessionId,
                CancellationToken.None);
        }

        (await AuthenticateAsync(web.CookieValue, IdentitySessionPurposes.Web))
            .Should().BeNull();
        await using var verificationContext = _database.CreateContext();
        var rows = await verificationContext.Sessions.AsNoTracking()
            .Where(session => session.Id == authSso.SessionId || session.Id == web.SessionId)
            .ToListAsync();
        rows.Should().OnlyContain(session => session.RevokedAt == _clock.GetUtcNow());
    }

    [Test]
    public async Task Database_rejects_cross_user_parent_links_and_raw_validator_columns_do_not_exist()
    {
        var authSso = await CreateAuthSsoAsync();
        var otherUser = await _database.CreateUserAsync($"o_{Guid.NewGuid():N}"[..30]);
        await using var dbContext = _database.CreateContext();
        var now = _clock.GetUtcNow();
        dbContext.Sessions.Add(new IdentitySession
        {
            Id = Guid.CreateVersion7(now),
            UserId = otherUser,
            Purpose = IdentitySessionPurposes.Web,
            ValidatorHash = new byte[32],
            SecurityVersion = 1,
            AuthenticatedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
            LastSeenAt = now,
            SlidingExpiresAt = now + TimeSpan.FromDays(30),
            AbsoluteExpiresAt = now + TimeSpan.FromDays(180),
            AuthSsoSessionId = authSso.SessionId,
            WebOrigin = "https://web.relisten.localhost:5173",
            Capabilities = IdentitySessionCapabilities.AllWeb
        });

        var save = () => dbContext.SaveChangesAsync();
        await save.Should().ThrowAsync<DbUpdateException>();

        await using var schemaContext = _database.CreateContext();
        var columns = await schemaContext.Database
            .SqlQueryRaw<string>("""
                SELECT column_name AS "Value"
                FROM information_schema.columns
                WHERE table_schema = 'identity' AND table_name = 'sessions'
                """)
            .ToListAsync();
        columns.Should().Contain("validator_hash");
        columns.Should().NotContain(column => column.Contains("validator", StringComparison.Ordinal)
            && column != "validator_hash");
    }

    [Test]
    public async Task Database_rejects_a_web_session_without_an_origin()
    {
        var authSso = await CreateAuthSsoAsync();
        await using var dbContext = _database.CreateContext();
        var now = _clock.GetUtcNow();
        dbContext.Sessions.Add(new IdentitySession
        {
            Id = Guid.CreateVersion7(now),
            UserId = _userId,
            Purpose = IdentitySessionPurposes.Web,
            ValidatorHash = new byte[32],
            SecurityVersion = 1,
            AuthenticatedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
            LastSeenAt = now,
            SlidingExpiresAt = now + TimeSpan.FromDays(30),
            AbsoluteExpiresAt = now + TimeSpan.FromDays(180),
            AuthSsoSessionId = authSso.SessionId,
            WebOrigin = null,
            Capabilities = IdentitySessionCapabilities.AllWeb
        });

        var save = () => dbContext.SaveChangesAsync();
        await save.Should().ThrowAsync<DbUpdateException>();
    }

    private async Task<IssuedIdentitySession> CreateAuthSsoAsync()
    {
        await using var dbContext = _database.CreateContext();
        return await Lifecycle(dbContext).CreateAuthSsoAsync(
            _userId,
            CancellationToken.None);
    }

    private async Task<IssuedIdentitySession> CreateWebAsync(Guid authSsoSessionId)
    {
        await using var dbContext = _database.CreateContext();
        return await Lifecycle(dbContext).CreateWebAsync(
            _userId,
            authSsoSessionId,
            "https://web.relisten.localhost:5173",
            CancellationToken.None);
    }

    private async Task<AuthenticatedIdentitySession?> AuthenticateAsync(
        string cookieValue,
        string purpose)
    {
        await using var dbContext = _database.CreateContext();
        return await Lifecycle(dbContext).AuthenticateAsync(
            cookieValue,
            purpose,
            CancellationToken.None);
    }

    private IdentitySessionLifecycle Lifecycle(
        RelistenUserService.Persistence.AccountsDbContext dbContext) => new(
            dbContext,
            new SessionCredentialCodec(),
            _clock,
            _runtime);

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void SetUtcNow(DateTimeOffset value) => _utcNow = value;

        public void Advance(TimeSpan value) => _utcNow += value;
    }
}
