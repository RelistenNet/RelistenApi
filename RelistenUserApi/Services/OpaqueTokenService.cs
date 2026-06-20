using System.Security.Cryptography;
using System.Text;

namespace Relisten.UserApi.Services;

public sealed record OpaqueBearerToken(
    string Plaintext,
    string SecretHash);

public sealed record OpaqueSelectorToken(
    string Plaintext,
    string Selector,
    string SecretHash,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt);

public sealed record ParsedSelectorToken(string Selector, string Secret);

public sealed class OpaqueTokenService
{
    public OpaqueBearerToken IssueBearer()
    {
        var plaintext = RandomTokenPart(32);
        return new OpaqueBearerToken(plaintext, HashSecret(plaintext));
    }

    public OpaqueSelectorToken IssueSelector(DateTimeOffset now, TimeSpan lifetime)
    {
        var selector = RandomTokenPart(16);
        var secret = RandomTokenPart(32);
        return new OpaqueSelectorToken(
            $"{selector}.{secret}",
            selector,
            HashSecret(secret),
            now,
            now.Add(lifetime));
    }

    public ParsedSelectorToken ParseSelector(string token, string errorCode)
    {
        var parts = token.Split('.', 2);
        if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
        {
            throw new UserAuthException(errorCode);
        }

        return new ParsedSelectorToken(parts[0], parts[1]);
    }

    public bool Verify(string secret, string expectedHash)
    {
        var actual = Encoding.UTF8.GetBytes(HashSecret(secret));
        var expected = Encoding.UTF8.GetBytes(expectedHash);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    public string HashSecret(string secret)
    {
        return Base64UrlEncode(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
    }

    private static string RandomTokenPart(int byteCount)
    {
        return Base64UrlEncode(RandomNumberGenerator.GetBytes(byteCount));
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
