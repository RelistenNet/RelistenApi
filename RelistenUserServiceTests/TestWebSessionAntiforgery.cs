using FluentAssertions;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using RelistenUserService.Authentication;
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
        using var scope = provider.CreateScope();
        var context = Context(scope, sessionId);
        context.Request.Method = HttpMethods.Post;
        context.Request.Headers.Cookie =
            $"{AuthenticationConstants.CsrfCookie}={tokens.CookieToken}";
        context.Request.Headers[AuthenticationConstants.CsrfHeader] = tokens.RequestToken;
        return await scope.ServiceProvider
            .GetRequiredService<IAntiforgery>()
            .IsRequestValidAsync(context);
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
