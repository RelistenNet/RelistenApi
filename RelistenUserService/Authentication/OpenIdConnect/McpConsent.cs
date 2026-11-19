using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using OpenIddict.Abstractions;
using RelistenUserService.Configuration;
using RelistenUserService.Identity.Entities;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace RelistenUserService.Authentication.OpenIdConnect;

/// <summary>
/// Renders the sign-in and consent pages for the ChatGPT connector and verifies the
/// listener's decision.
/// </summary>
/// <remarks>
/// Consent is asked on every authorization and never remembered. Every ChatGPT user shares
/// one redirect URI, so a silently issued code would let an attacker start a flow in their
/// own ChatGPT session, send the link to a signed-in listener, and end up with that
/// listener's Relisten account linked to the attacker's ChatGPT. The page and its
/// anti-forgery token make the listener the one who approves each link.
/// </remarks>
public sealed class McpConsent(IAntiforgery antiforgery, AccountsOptions options)
{
    public string SignInPage(HttpContext context)
    {
        var google = options.Google.Enabled
            ? "<button name=\"provider\" value=\"google\">Continue with Google</button>"
            : "";
        var apple = options.Apple.Enabled
            ? "<button name=\"provider\" value=\"apple\">Continue with Apple</button>"
            : "";
        var destinations =
            (options.Google.Enabled ? " https://accounts.google.com" : "")
            + (options.Apple.Enabled ? " https://appleid.apple.com" : "");
        return Page(
            context,
            "Sign in to connect ChatGPT",
            destinations,
            $"""
            <form method="get" action="/connect/authorize">
            {RequestFields(context)}
            {google}
            {apple}
            </form>
            """
        );
    }

    public string ConsentPage(
        HttpContext context,
        User user,
        SessionId session,
        OpenIddictRequest request
    )
    {
        BindBrowserSession(context, session);
        var token = antiforgery.GetAndStoreTokens(context);
        var scopes = string.Concat(
            request.GetScopes().Select(scope => $"<li>{Encode(ScopeDescription(scope))}</li>")
        );
        return Page(
            context,
            "Connect ChatGPT to Relisten",
            " " + new Uri(McpOAuth.Redirect).GetLeftPart(UriPartial.Authority),
            $"""
            <p>ChatGPT at chatgpt.com is requesting access to {Encode(user.Username)}.</p>
            <ul>{scopes}</ul>
            <form method="post" action="/connect/authorize">
            {RequestFields(context)}
            {HiddenField(token.FormFieldName, token.RequestToken!)}
            <button name="decision" value="allow">Allow</button>
            <button name="decision" value="deny">Deny</button>
            </form>
            """
        );
    }

    public async Task<bool> IsApprovedAsync(HttpContext context, SessionId session)
    {
        BindBrowserSession(context, session);
        return await antiforgery.IsRequestValidAsync(context)
            && (await context.Request.ReadFormAsync(context.RequestAborted))["decision"] == "allow";
    }

    private static void BindBrowserSession(HttpContext context, SessionId session) =>
        context.User = new ClaimsPrincipal(
            new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, session.Value)], "mcp-consent")
        );

    private static string RequestFields(HttpContext context) =>
        string.Concat(
            context
                .Request.Query.Where(pair =>
                    pair.Key is not ("decision" or "provider" or "__RequestVerificationToken")
                )
                .SelectMany(pair => pair.Value.Select(value => HiddenField(pair.Key, value ?? "")))
        );

    private static string ScopeDescription(string scope) =>
        scope switch
        {
            RelistenScopes.UserRead => "Read your Relisten profile.",
            RelistenScopes.LibraryRead => "Read your favorites and playlists.",
            RelistenScopes.LibraryWrite => "Create or edit your playlists.",
            Scopes.OpenId => "Identify your Relisten account.",
            Scopes.Profile => "Read your display name.",
            Scopes.OfflineAccess => "Stay connected until you disconnect.",
            _ => scope,
        };

    private static string Page(
        HttpContext context,
        string title,
        string redirectOrigins,
        string body
    )
    {
        context.Response.Headers.CacheControl = "private, no-store";
        // Chromium also applies form-action to redirects following a submission.
        context.Response.Headers.ContentSecurityPolicy =
            $"default-src 'none'; form-action 'self'{redirectOrigins}; base-uri 'none'; frame-ancestors 'none'";
        return $"""
            <!doctype html>
            <html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width">
            <title>{Encode(title)}</title><main><h1>{Encode(title)}</h1>{body}</main></html>
            """;
    }

    private static string HiddenField(string name, string value) =>
        $"<input type=\"hidden\" name=\"{Encode(name)}\" value=\"{Encode(value)}\">";

    private static string Encode(string value) => WebUtility.HtmlEncode(value);
}
