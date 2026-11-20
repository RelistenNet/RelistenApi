using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.WebUtilities;

namespace RelistenUserService.Sonos;

internal sealed record SonosConfiguration(
    string ClientId,
    string ClientSecret,
    Uri PublicBase,
    string ServiceId
)
{
    public Uri RedirectUri => new(PublicBase, "sonos/oauth/callback");

    public static SonosConfiguration? Read(IConfiguration config, IHostEnvironment environment)
    {
        if (!environment.IsDevelopment() || !config.GetValue<bool>("Sonos:Enabled"))
        {
            return null;
        }

        var section = config.GetSection("Sonos");
        var clientId = section["ClientId"];
        var secret = section["ClientSecret"];
        var serviceId = section["ServiceId"];
        if (
            string.IsNullOrWhiteSpace(clientId)
            || string.IsNullOrWhiteSpace(secret)
            || !Uri.TryCreate(section["PublicBaseUrl"], UriKind.Absolute, out var publicBase)
            || publicBase.Scheme != Uri.UriSchemeHttps
            || publicBase.AbsolutePath != "/"
            || publicBase.UserInfo != ""
            || publicBase.Query != ""
            || publicBase.Fragment != ""
            || !int.TryParse(serviceId, out var numericId)
            || numericId <= 0
        )
        {
            throw new InvalidOperationException(
                "Sonos development configuration requires credentials, an HTTPS origin, and a service ID."
            );
        }

        return new(clientId, secret, publicBase, serviceId!);
    }
}

public sealed class SonosProtocol(HttpClient http)
{
    private const string ControlBase = "https://api.ws.sonos.com/control/api/v1/";
    private const string LoginBase = "https://api.sonos.com/login/v3/oauth";

    public static string NewSecret() =>
        WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    public static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public static bool SecretMatches(string actual, string expected) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(actual),
            Encoding.UTF8.GetBytes(expected)
        );

    internal static string AuthorizeUrl(SonosConfiguration config, string state) =>
        QueryHelpers.AddQueryString(
            LoginBase,
            new Dictionary<string, string?>
            {
                ["client_id"] = config.ClientId,
                ["redirect_uri"] = config.RedirectUri.AbsoluteUri,
                ["response_type"] = "code",
                ["scope"] = "playback-control-all",
                ["state"] = state,
            }
        );

    internal async Task<SonosToken> TokenAsync(
        SonosConfiguration config,
        Dictionary<string, string> form,
        CancellationToken cancellationToken
    )
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, LoginBase + "/access");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(
                Encoding.UTF8.GetBytes(config.ClientId + ":" + config.ClientSecret)
            )
        );
        request.Content = new FormUrlEncodedContent(form);
        var result = await SendAsync(request, cancellationToken);
        return result.Deserialize<SonosToken>()
            ?? throw new JsonException("Sonos returned an empty token response.");
    }

    public async Task<JsonElement> ControlAsync(
        string token,
        string path,
        object? body,
        CancellationToken cancellationToken
    )
    {
        using var request = new HttpRequestMessage(
            body is null ? HttpMethod.Get : HttpMethod.Post,
            ControlBase + path
        );
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await SendAsync(request, cancellationToken);
    }

    private async Task<JsonElement> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new SonosRequestException(response.StatusCode);
        }

        var body = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        return body.Length == 0 ? default : JsonSerializer.Deserialize<JsonElement>(body);
    }
}

internal sealed class SonosRequestException(HttpStatusCode status)
    : Exception("Sonos rejected the request.")
{
    public HttpStatusCode Status { get; } = status;
}

internal sealed record SonosToken(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("refresh_token")] string? RefreshToken,
    [property: JsonPropertyName("expires_in")] int ExpiresIn
);

internal sealed record StoredSonosToken(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset ExpiresAt
);

public sealed class SonosDevelopment
{
    internal SonosDevelopment(SonosConfiguration? configuration) => Configuration = configuration;

    internal SonosConfiguration? Configuration { get; }
}
