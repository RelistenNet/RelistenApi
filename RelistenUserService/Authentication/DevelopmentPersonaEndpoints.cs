using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.WebUtilities;
using RelistenUserService.Identity;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace RelistenUserService.Authentication;

public static class DevelopmentPersonaEndpoints
{
    public static void MapDevelopmentPersonaEndpoints(this WebApplication app)
    {
        app.MapGet("/development/sign-in", ShowAsync);
        app.MapPost("/development/sign-in", SignInAsync);
    }

    private static Task<IResult> ShowAsync(
        HttpContext context,
        IAntiforgery antiforgery)
    {
        var returnUrl = context.Request.Query["return_url"].ToString();
        if (!TryGetProvider(returnUrl, out var provider))
        {
            return Task.FromResult(Results.BadRequest());
        }

        var tokens = antiforgery.GetAndStoreTokens(context);
        var personas = DevelopmentPersonaCatalog.All.Where(persona => persona.Provider == provider);
        var buttons = string.Join("", personas.Select(persona => $"""
            <button type="submit" name="persona" value="{WebUtility.HtmlEncode(persona.Id)}">
              <strong>{WebUtility.HtmlEncode(persona.Label)}</strong>
              <span>{WebUtility.HtmlEncode(persona.Profile.Subject)}</span>
            </button>
            """));
        var html = $$"""
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>Relisten development sign in</title>
              <style>
                body { background:#111; color:#f7f7f7; font:16px system-ui; margin:0; }
                main { margin:10vh auto; max-width:34rem; padding:1.5rem; }
                h1 { font-size:1.5rem; }
                p { color:#bbb; line-height:1.5; }
                form { display:grid; gap:.75rem; }
                button { background:#242424; border:1px solid #444; border-radius:.75rem;
                  color:inherit; cursor:pointer; padding:1rem; text-align:left; }
                button:hover { border-color:#888; }
                button span { color:#aaa; display:block; font-size:.85rem; margin-top:.3rem; }
              </style>
            </head>
            <body><main>
              <h1>Choose a development identity</h1>
              <p>This page replaces Apple or Google only. Relisten still uses its normal
                authorization-code, PKCE, token, session, and account code.</p>
              <form method="post">
                <input type="hidden" name="return_url" value="{{WebUtility.HtmlEncode(returnUrl)}}">
                <input type="hidden" name="{{tokens.FormFieldName}}"
                  value="{{WebUtility.HtmlEncode(tokens.RequestToken)}}">
                {{buttons}}
              </form>
            </main></body>
            </html>
            """;

        return Task.FromResult(Results.Content(html, "text/html; charset=utf-8"));
    }

    private static async Task<IResult> SignInAsync(
        HttpContext context,
        IAntiforgery antiforgery,
        ExternalIdentityCompletionService identities,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        await antiforgery.ValidateRequestAsync(context);
        var form = await context.Request.ReadFormAsync(cancellationToken);
        var returnUrl = form["return_url"].ToString();
        if (!TryGetProvider(returnUrl, out var provider))
        {
            return Results.BadRequest();
        }

        var persona = DevelopmentPersonaCatalog.Find(form["persona"].ToString(), provider);
        if (persona is null)
        {
            return Results.BadRequest();
        }

        var user = await identities.CompleteAsync(persona.Profile, cancellationToken);
        var identity = new ClaimsIdentity(AuthenticationConstants.DevelopmentIdentityScheme);
        identity.AddClaim(new Claim(Claims.Subject, user.Id.ToString("D")));
        await context.SignInAsync(
            AuthenticationConstants.DevelopmentIdentityScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties
            {
                AllowRefresh = false,
                IsPersistent = false,
                ExpiresUtc = timeProvider.GetUtcNow().AddMinutes(10)
            });

        return Results.LocalRedirect(returnUrl);
    }

    private static bool TryGetProvider(string value, out string provider)
    {
        provider = "";
        if (!value.StartsWith("/", StringComparison.Ordinal)
            || value.StartsWith("//", StringComparison.Ordinal)
            || value.StartsWith("/\\", StringComparison.Ordinal)
            || !Uri.TryCreate($"http://localhost{value}", UriKind.Absolute, out var uri)
            || uri.AbsolutePath != "/connect/authorize")
        {
            return false;
        }

        provider = QueryHelpers.ParseQuery(uri.Query)["provider"].ToString();
        return provider is "apple" or "google";
    }
}
