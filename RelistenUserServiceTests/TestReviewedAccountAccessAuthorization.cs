using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using OpenIddict.Abstractions;
using OpenIddict.EntityFrameworkCore.Models;
using RelistenUserService.Authentication;
using RelistenUserService.Configuration;
using RelistenUserService.Identity.Entities;
using RelistenUserService.Persistence;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace RelistenUserServiceTests;

[TestFixture]
[NonParallelizable]
public sealed class TestReviewedAccountAccessAuthorization
{
    private readonly PostgresIntegrationDatabase _database = new();
    private AccountsRuntimeConfiguration _runtime = null!;
    private Guid _userId;

    [OneTimeSetUp]
    public async Task SetUpDatabase()
    {
        await _database.StartAsync();
        _runtime = new AccountsRuntimeConfiguration(
            new AccountsOptions { Audience = "https://accounts.relisten.test" },
            new Uri("https://auth.relisten.test"),
            AllowLoopbackHttp: false,
            TrustedProxyNetworks: []);
    }

    [OneTimeTearDown]
    public Task TearDownDatabase() => _database.StopAsync();

    [SetUp]
    public async Task CreateUser() =>
        _userId = await _database.CreateUserAsync($"a_{Guid.NewGuid():N}"[..30]);

    [Test]
    public async Task Native_access_requires_a_valid_session_and_the_native_scope()
    {
        await using var dbContext = _database.CreateContext();
        var (user, session) = await CreateNativeSessionAsync(dbContext);
        var requirement = Requirement();

        var permittedAccount = new CurrentAccountContext();
        var permitted = AuthorizationContext(
            requirement,
            NativePrincipal(user, session, [RelistenScopes.UserRead]));
        await Handler(dbContext, permittedAccount).HandleAsync(permitted);

        permitted.HasSucceeded.Should().BeTrue();
        permittedAccount.IsNative.Should().BeTrue();
        permittedAccount.NativeSessionId.Should().Be(session.Id);

        var missingScopeAccount = new CurrentAccountContext();
        var missingScope = AuthorizationContext(
            requirement,
            NativePrincipal(user, session, []));
        await Handler(dbContext, missingScopeAccount).HandleAsync(missingScope);

        missingScope.HasSucceeded.Should().BeFalse();
        missingScopeAccount.IsNative.Should().BeTrue();
    }

    [Test]
    public async Task Web_access_requires_the_persisted_web_capability()
    {
        await using var dbContext = _database.CreateContext();
        var user = await dbContext.Users.SingleAsync(item => item.Id == _userId);
        var requirement = Requirement();
        var principal = new ClaimsPrincipal(new ClaimsIdentity("web"));

        var permittedAccount = new CurrentAccountContext();
        permittedAccount.SetWeb(
            user,
            Guid.CreateVersion7(),
            AuthenticationConstants.LocalWebOrigin,
            IdentitySessionCapabilities.AccountProfileRead);
        var permitted = AuthorizationContext(requirement, principal);
        await Handler(dbContext, permittedAccount).HandleAsync(permitted);

        permitted.HasSucceeded.Should().BeTrue();

        principal.SetScopes(RelistenScopes.UserRead);
        var missingCapabilityAccount = new CurrentAccountContext();
        missingCapabilityAccount.SetWeb(
            user,
            Guid.CreateVersion7(),
            AuthenticationConstants.LocalWebOrigin,
            IdentitySessionCapabilities.LibraryRead);
        var missingCapability = AuthorizationContext(requirement, principal);
        await Handler(dbContext, missingCapabilityAccount).HandleAsync(missingCapability);

        missingCapability.HasSucceeded.Should().BeFalse();
    }

    [Test]
    public async Task Library_access_uses_read_or_write_permission_for_each_credential_type()
    {
        await using var dbContext = _database.CreateContext();
        var (user, session) = await CreateNativeSessionAsync(dbContext);
        var requirement = LibraryRequirement();

        var nativeReaderPrincipal = NativePrincipal(
            user,
            session,
            [RelistenScopes.LibraryRead]);
        var nativeRead = AuthorizationContext(requirement, nativeReaderPrincipal);
        await Handler(dbContext, new CurrentAccountContext()).HandleAsync(nativeRead);
        nativeRead.HasSucceeded.Should().BeTrue();

        var nativeReadOnlyMutation = AuthorizationContext(
            requirement,
            nativeReaderPrincipal,
            HttpMethods.Post);
        await Handler(dbContext, new CurrentAccountContext())
            .HandleAsync(nativeReadOnlyMutation);
        nativeReadOnlyMutation.HasSucceeded.Should().BeFalse();

        var nativeWriter = AuthorizationContext(
            requirement,
            NativePrincipal(user, session, [RelistenScopes.LibraryWrite]),
            HttpMethods.Post);
        await Handler(dbContext, new CurrentAccountContext()).HandleAsync(nativeWriter);
        nativeWriter.HasSucceeded.Should().BeTrue();

        var webReadOnlyAccount = WebAccount(user, IdentitySessionCapabilities.LibraryRead);
        var webRead = AuthorizationContext(
            requirement,
            new ClaimsPrincipal(new ClaimsIdentity("web")));
        await Handler(dbContext, webReadOnlyAccount).HandleAsync(webRead);
        webRead.HasSucceeded.Should().BeTrue();

        webReadOnlyAccount = WebAccount(user, IdentitySessionCapabilities.LibraryRead);
        var webReadOnly = AuthorizationContext(
            requirement,
            new ClaimsPrincipal(new ClaimsIdentity("web")),
            HttpMethods.Post);
        await Handler(dbContext, webReadOnlyAccount).HandleAsync(webReadOnly);
        webReadOnly.HasSucceeded.Should().BeFalse();

        var webWriterAccount = WebAccount(
            user,
            IdentitySessionCapabilities.FavoriteMutation);
        var webWriter = AuthorizationContext(
            requirement,
            new ClaimsPrincipal(new ClaimsIdentity("web")),
            HttpMethods.Post);
        await Handler(dbContext, webWriterAccount).HandleAsync(webWriter);
        webWriter.HasSucceeded.Should().BeTrue();
    }

    private async Task<(User User, NativeSession Session)> CreateNativeSessionAsync(
        AccountsDbContext dbContext)
    {
        var now = DateTimeOffset.UtcNow;
        var user = await dbContext.Users.SingleAsync(item => item.Id == _userId);
        var authorizationId = Guid.CreateVersion7(now);
        dbContext.Set<OpenIddictEntityFrameworkCoreAuthorization<Guid>>().Add(new()
        {
            Id = authorizationId
        });
        var session = new NativeSession
        {
            Id = Guid.CreateVersion7(now),
            UserId = user.Id,
            AuthorizationId = authorizationId,
            ClientId = "test-native-client",
            SecurityVersion = user.SecurityVersion,
            AuthenticatedAt = now,
            LastUsedAt = now,
            AbsoluteExpiresAt = now + TimeSpan.FromDays(1),
            CreatedAt = now,
            UpdatedAt = now
        };
        dbContext.NativeSessions.Add(session);
        await dbContext.SaveChangesAsync();
        return (user, session);
    }

    private ReviewedAccountAccessAuthorizationHandler Handler(
        AccountsDbContext dbContext,
        CurrentAccountContext currentAccount) =>
        new(
            new NativeSessionAuthenticator(dbContext, _runtime, TimeProvider.System),
            currentAccount);

    private ClaimsPrincipal NativePrincipal(
        User user,
        NativeSession session,
        IEnumerable<string> scopes)
    {
        var principal = new NativePrincipalFactory(_runtime).Create(user, session, scopes);
        principal.SetAudiences(_runtime.Options.Audience);
        return principal;
    }

    private static ReviewedAccountAccessRequirement Requirement() =>
        new(new ReviewedAccountAccessRule(
            RelistenScopes.UserRead,
            IdentitySessionCapabilities.AccountProfileRead));

    private static ReviewedAccountAccessRequirement LibraryRequirement() =>
        new(
            new ReviewedAccountAccessRule(
                RelistenScopes.LibraryRead,
                IdentitySessionCapabilities.LibraryRead),
            new ReviewedAccountAccessRule(
                RelistenScopes.LibraryWrite,
                IdentitySessionCapabilities.FavoriteMutation));

    private static CurrentAccountContext WebAccount(
        User user,
        IdentitySessionCapabilities capabilities)
    {
        var account = new CurrentAccountContext();
        account.SetWeb(
            user,
            Guid.CreateVersion7(),
            AuthenticationConstants.LocalWebOrigin,
            capabilities);
        return account;
    }

    private static AuthorizationHandlerContext AuthorizationContext(
        ReviewedAccountAccessRequirement requirement,
        ClaimsPrincipal principal,
        string method = "GET")
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        return new AuthorizationHandlerContext([requirement], principal, context);
    }
}
