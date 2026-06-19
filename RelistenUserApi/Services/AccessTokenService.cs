using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Relisten.UserApi.Auth;
using Relisten.UserApi.Configuration;
using Relisten.UserApi.Models;

namespace Relisten.UserApi.Services;

public sealed class AccessTokenService
{
    private readonly UserAuthOptions _options;

    public AccessTokenService(IOptions<UserAuthOptions> options)
    {
        _options = options.Value;
    }

    public AccessToken Issue(UserAccount user, UserSession session, DateTimeOffset now)
    {
        var signingKey = GetSigningKey();
        var expiresAt = now.AddHours(1);
        var header = Base64UrlEncode(Encoding.UTF8.GetBytes("""{"alg":"HS256","typ":"JWT"}"""));
        var payload = Base64UrlEncode(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new
        {
            sub = user.UserUuid,
            name = user.DisplayName,
            preferred_username = user.Username,
            session_uuid = session.SessionUuid,
            scope_id = $"user:{user.UserUuid}",
            exp = expiresAt.ToUnixTimeSeconds()
        })));
        var signature = Sign($"{header}.{payload}", signingKey);

        return new AccessToken($"{header}.{payload}.{signature}", expiresAt);
    }

    public ClaimsPrincipal? Validate(string token)
    {
        try
        {
            var signingKey = TryGetSigningKey();
            if (signingKey == null)
            {
                return null;
            }

            var parts = token.Split('.');
            if (parts.Length != 3)
            {
                return null;
            }

            var signingInput = $"{parts[0]}.{parts[1]}";
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(Sign(signingInput, signingKey)),
                    Encoding.UTF8.GetBytes(parts[2])))
            {
                return null;
            }

            var payload = JObject.Parse(Encoding.UTF8.GetString(Base64UrlDecode(parts[1])));
            var exp = payload.Value<long?>("exp");
            if (!exp.HasValue || DateTimeOffset.FromUnixTimeSeconds(exp.Value) <= DateTimeOffset.UtcNow)
            {
                return null;
            }

            var userUuid = payload.Value<string>("sub");
            var displayName = payload.Value<string>("name");
            var username = payload.Value<string>("preferred_username");
            var sessionUuid = payload.Value<string>("session_uuid");
            var scopeId = payload.Value<string>("scope_id");
            if (string.IsNullOrWhiteSpace(userUuid) ||
                string.IsNullOrWhiteSpace(displayName) ||
                string.IsNullOrWhiteSpace(username) ||
                string.IsNullOrWhiteSpace(sessionUuid) ||
                string.IsNullOrWhiteSpace(scopeId))
            {
                return null;
            }

            var claims = new[]
            {
                new Claim(RelistenUserAuthenticationDefaults.ClaimTypes.UserUuid, userUuid),
                new Claim(RelistenUserAuthenticationDefaults.ClaimTypes.DisplayName, displayName),
                new Claim(RelistenUserAuthenticationDefaults.ClaimTypes.Username, username),
                new Claim(RelistenUserAuthenticationDefaults.ClaimTypes.ScopeId, scopeId),
                new Claim(RelistenUserAuthenticationDefaults.ClaimTypes.SessionUuid, sessionUuid)
            };

            return new ClaimsPrincipal(new ClaimsIdentity(claims, RelistenUserAuthenticationDefaults.Scheme));
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    private byte[] GetSigningKey()
    {
        return TryGetSigningKey()
            ?? throw new InvalidOperationException(
                "UserAuth:AccessTokenSigningKey must be configured with at least 32 bytes.");
    }

    private byte[]? TryGetSigningKey()
    {
        if (string.IsNullOrWhiteSpace(_options.AccessTokenSigningKey))
        {
            return null;
        }

        var key = Encoding.UTF8.GetBytes(_options.AccessTokenSigningKey);
        return key.Length >= 32 ? key : null;
    }

    private static string Sign(string signingInput, byte[] signingKey)
    {
        using var hmac = new HMACSHA256(signingKey);
        return Base64UrlEncode(hmac.ComputeHash(Encoding.UTF8.GetBytes(signingInput)));
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
        return Convert.FromBase64String(padded);
    }
}

public sealed record AccessToken(string Plaintext, DateTimeOffset ExpiresAt);
