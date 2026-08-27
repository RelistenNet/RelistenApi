using FluentAssertions;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using RelistenUserService.Authentication;
using RelistenUserService.Authentication.Authorization;
using RelistenUserService.Authentication.Browser;
using RelistenUserService.Identity.Entities;

namespace RelistenUserServiceTests;

[TestFixture]
public sealed class TestWebSessionAntiforgery
{
    [Test]
    public async Task A_token_is_valid_only_for_the_session_that_created_it()
    {
        using var provider = BuildProvider();
        var firstSessionId = Guid.NewGuid();
        var tokens = CreateTokens(provider, firstSessionId);

        (await IsValidAsync(provider, firstSessionId, tokens)).Should().BeTrue();
        (await IsValidAsync(provider, Guid.NewGuid(), tokens)).Should().BeFalse();
    }

    [Test]
    public async Task A_preissued_cookie_keeps_concurrent_request_tokens_compatible()
    {
        using var provider = BuildProvider();
        string cookieToken;
        using (var scope = provider.CreateScope())
        {
            var context = new DefaultHttpContext
            {
                RequestServices = scope.ServiceProvider
            };
            var tokens = scope.ServiceProvider
                .GetRequiredService<IAntiforgery>()
                .GetAndStoreTokens(context);
            tokens.CookieToken.Should().NotBeNull();
            cookieToken = tokens.CookieToken!;
            context.Response.Headers.SetCookie.Should().ContainSingle();
        }

        var sessionId = Guid.CreateVersion7();
        var first = CreateRequestToken(provider, sessionId, cookieToken);
        var second = CreateRequestToken(provider, sessionId, cookieToken);

        first.SetCookieHeaders.Should().BeEmpty();
        second.SetCookieHeaders.Should().BeEmpty();
        (await IsValidAsync(provider, sessionId, cookieToken, first.RequestToken))
            .Should().BeTrue();
        (await IsValidAsync(provider, sessionId, cookieToken, second.RequestToken))
            .Should().BeTrue();
    }

    [Test]
    public async Task Native_mutation_does_not_require_browser_csrf_or_origin()
    {
        var currentAccount = new CurrentAccountContext();
        currentAccount.SetNative(
            new User { Id = Guid.CreateVersion7() },
            Guid.CreateVersion7());
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        var reachedApplication = false;

        await new BrowserMutationProtectionMiddleware(_ =>
            {
                reachedApplication = true;
                return Task.CompletedTask;
            })
            .InvokeAsync(context, currentAccount, null!);

        reachedApplication.Should().BeTrue();
    }

    [Test]
    public async Task Web_mutation_requires_the_exact_origin_and_a_valid_token()
    {
        using var provider = BuildProvider();
        var sessionId = Guid.CreateVersion7();
        var tokens = CreateTokens(provider, sessionId);
        using var scope = provider.CreateScope();
        var context = Context(scope, sessionId);
        context.Request.Method = HttpMethods.Post;
        context.Request.Headers.Origin = AuthenticationConstants.LocalWebOrigin;
        context.Request.Headers.Cookie =
            $"{AuthenticationConstants.WebSessionCookie}=opaque; "
            + $"{AuthenticationConstants.CsrfCookie}={tokens.CookieToken}";
        context.Request.Headers[AuthenticationConstants.CsrfHeader] = tokens.RequestToken;
        var reachedApplication = false;

        await new BrowserMutationProtectionMiddleware(_ =>
            {
                reachedApplication = true;
                return Task.CompletedTask;
            })
            .InvokeAsync(
                context,
                scope.ServiceProvider.GetRequiredService<CurrentAccountContext>(),
                scope.ServiceProvider.GetRequiredService<IAntiforgery>());

        reachedApplication.Should().BeTrue();
    }

    [TestCase(null)]
    [TestCase("https://wrong.example")]
    public async Task Web_mutation_rejects_a_missing_or_incorrect_origin(string? origin)
    {
        var currentAccount = new CurrentAccountContext();
        currentAccount.SetWeb(
            new User { Id = Guid.CreateVersion7() },
            Guid.CreateVersion7(),
            AuthenticationConstants.LocalWebOrigin,
            IdentitySessionCapabilities.AllWeb);
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Headers.Cookie =
            $"{AuthenticationConstants.WebSessionCookie}=opaque";
        if (origin is not null)
        {
            context.Request.Headers.Origin = origin;
        }
        context.Request.Headers[AuthenticationConstants.CsrfHeader] = "present";

        await new BrowserMutationProtectionMiddleware(_ => Task.CompletedTask)
            .InvokeAsync(context, currentAccount, null!);

        context.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Test]
    public async Task Cookie_mutation_fails_when_no_web_session_was_authenticated()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Headers.Cookie =
            $"{AuthenticationConstants.WebSessionCookie}=opaque";

        await new BrowserMutationProtectionMiddleware(_ => Task.CompletedTask)
            .InvokeAsync(context, new CurrentAccountContext(), null!);

        context.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection();
        services.AddAntiforgery(options =>
        {
            options.HeaderName = AuthenticationConstants.CsrfHeader;
            options.Cookie.Name = AuthenticationConstants.CsrfCookie;
        });
        services.AddScoped<CurrentAccountContext>();
        services.AddSingleton<IAntiforgeryAdditionalDataProvider,
            WebSessionAntiforgeryAdditionalDataProvider>();
        return services.BuildServiceProvider();
    }

    private static AntiforgeryTokenSet CreateTokens(
        ServiceProvider provider,
        Guid sessionId)
    {
        using var scope = provider.CreateScope();
        var context = Context(scope, sessionId);
        return scope.ServiceProvider
            .GetRequiredService<IAntiforgery>()
            .GetAndStoreTokens(context);
    }

    private static async Task<bool> IsValidAsync(
        ServiceProvider provider,
        Guid sessionId,
        AntiforgeryTokenSet tokens)
    {
        tokens.CookieToken.Should().NotBeNull();
        tokens.RequestToken.Should().NotBeNull();
        return await IsValidAsync(
            provider,
            sessionId,
            tokens.CookieToken!,
            tokens.RequestToken!);
    }

    private static async Task<bool> IsValidAsync(
        ServiceProvider provider,
        Guid sessionId,
        string cookieToken,
        string requestToken)
    {
        using var scope = provider.CreateScope();
        var context = Context(scope, sessionId);
        context.Request.Method = HttpMethods.Post;
        context.Request.Headers.Cookie =
            $"{AuthenticationConstants.CsrfCookie}={cookieToken}";
        context.Request.Headers[AuthenticationConstants.CsrfHeader] = requestToken;
        return await scope.ServiceProvider
            .GetRequiredService<IAntiforgery>()
            .IsRequestValidAsync(context);
    }

    private static (string RequestToken, string?[] SetCookieHeaders) CreateRequestToken(
        ServiceProvider provider,
        Guid sessionId,
        string cookieToken)
    {
        using var scope = provider.CreateScope();
        var context = Context(scope, sessionId);
        context.Request.Headers.Cookie =
            $"{AuthenticationConstants.CsrfCookie}={cookieToken}";
        var tokens = scope.ServiceProvider
            .GetRequiredService<IAntiforgery>()
            .GetAndStoreTokens(context);
        tokens.RequestToken.Should().NotBeNull();
        return (tokens.RequestToken!, context.Response.Headers.SetCookie.ToArray());
    }

    private static DefaultHttpContext Context(IServiceScope scope, Guid sessionId)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider
        };
        scope.ServiceProvider.GetRequiredService<CurrentAccountContext>().SetWeb(
            new User { Id = Guid.NewGuid() },
            sessionId,
            AuthenticationConstants.LocalWebOrigin,
            IdentitySessionCapabilities.AllWeb);
        return context;
    }
}
