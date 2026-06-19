using System.Security.Cryptography;
using System.Text;

namespace Relisten.UserApi.Services;

public sealed record RefreshToken(
    string Plaintext,
    string Selector,
    string SecretHash,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt);

public sealed class RefreshTokenService
{
    public RefreshToken Issue(DateTimeOffset now)
    {
        var selector = RandomTokenPart(16);
        var secret = RandomTokenPart(32);
        return new RefreshToken(
            $"{selector}.{secret}",
            selector,
            HashSecret(secret),
            now,
            now.AddYears(1));
    }

    public ParsedRefreshToken Parse(string refreshToken)
    {
        var parts = refreshToken.Split('.', 2);
        if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
        {
            throw new UserAuthException("invalid_refresh_token");
        }

        return new ParsedRefreshToken(parts[0], parts[1]);
    }

    public bool Verify(string secret, string expectedHash)
    {
        var actual = Encoding.UTF8.GetBytes(HashSecret(secret));
        var expected = Encoding.UTF8.GetBytes(expectedHash);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static string RandomTokenPart(int byteCount)
    {
        return Base64UrlEncode(RandomNumberGenerator.GetBytes(byteCount));
    }

    private static string HashSecret(string secret)
    {
        return Base64UrlEncode(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}

public sealed record ParsedRefreshToken(string Selector, string Secret);
