using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Relisten.UserApi.Configuration;

namespace Relisten.UserApi.Services;

public sealed record WebOAuthTokenSet(string IdToken);

public interface IWebOAuthCodeExchanger
{
    Task<WebOAuthTokenSet> ExchangeCode(
        string provider,
        string code,
        string redirectUri);
}

public sealed class GoogleWebOAuthCodeExchanger : IWebOAuthCodeExchanger
{
    private readonly HttpClient _httpClient;
    private readonly UserAuthOptions _options;

    public GoogleWebOAuthCodeExchanger(
        HttpClient httpClient,
        IOptions<UserAuthOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value;
    }

    public async Task<WebOAuthTokenSet> ExchangeCode(
        string provider,
        string code,
        string redirectUri)
    {
        if (!string.Equals(provider, "google", StringComparison.OrdinalIgnoreCase))
        {
            throw new UserAuthException("provider_not_supported");
        }

        var google = _options.Google;
        if (string.IsNullOrWhiteSpace(google.ClientId) ||
            string.IsNullOrWhiteSpace(google.ClientSecret) ||
            string.IsNullOrWhiteSpace(google.TokenEndpoint))
        {
            throw new UserAuthException("provider_not_configured");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, google.TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = redirectUri,
                ["client_id"] = google.ClientId.Trim(),
                ["client_secret"] = google.ClientSecret.Trim()
            })
        };

        using var response = await _httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            throw new UserAuthException("invalid_provider_token");
        }

        string? idToken;
        try
        {
            var body = JObject.Parse(await response.Content.ReadAsStringAsync());
            idToken = body.Value<string>("id_token");
        }
        catch (JsonException)
        {
            throw new UserAuthException("invalid_provider_token");
        }

        if (string.IsNullOrWhiteSpace(idToken))
        {
            throw new UserAuthException("invalid_provider_token");
        }

        return new WebOAuthTokenSet(idToken);
    }
}
