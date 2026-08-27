using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;

namespace RelistenUserService.Authentication.Sessions;

public sealed class SessionCredentialCodec
{
    private const string Version = "v1";
    private const int ValidatorLength = 32;

    public IssuedSessionCredential Issue(Guid sessionId)
    {
        if (sessionId.Version != 7)
        {
            throw new ArgumentException("The session ID must be a UUIDv7.", nameof(sessionId));
        }

        var validator = RandomNumberGenerator.GetBytes(ValidatorLength);
        try
        {
            var cookieValue = string.Join(
                '.',
                Version,
                sessionId.ToString("N"),
                WebEncoders.Base64UrlEncode(validator));
            return new IssuedSessionCredential(
                cookieValue,
                SHA256.HashData(validator));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(validator);
        }
    }

    public bool TryParse(string? cookieValue, out ParsedSessionCredential? credential)
    {
        credential = null;
        if (string.IsNullOrEmpty(cookieValue))
        {
            return false;
        }

        var parts = cookieValue.Split('.');
        if (parts.Length != 3
            || !string.Equals(parts[0], Version, StringComparison.Ordinal)
            || !Guid.TryParseExact(parts[1], "N", out var sessionId)
            || sessionId.Version != 7
            || !string.Equals(parts[1], sessionId.ToString("N"), StringComparison.Ordinal))
        {
            return false;
        }

        byte[] validator;
        try
        {
            validator = WebEncoders.Base64UrlDecode(parts[2]);
        }
        catch (FormatException)
        {
            return false;
        }

        try
        {
            if (validator.Length != ValidatorLength
                || !string.Equals(
                    parts[2],
                    WebEncoders.Base64UrlEncode(validator),
                    StringComparison.Ordinal))
            {
                return false;
            }

            credential = new ParsedSessionCredential(
                sessionId,
                SHA256.HashData(validator));
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(validator);
        }
    }

    public bool ValidatorMatches(
        ReadOnlySpan<byte> candidateHash,
        ReadOnlySpan<byte> persistedHash) =>
        candidateHash.Length == ValidatorLength
        && persistedHash.Length == ValidatorLength
        && CryptographicOperations.FixedTimeEquals(candidateHash, persistedHash);
}

public sealed class IssuedSessionCredential(
    string cookieValue,
    byte[] validatorHash)
{
    public string CookieValue { get; } = cookieValue;
    internal byte[] ValidatorHash { get; } = validatorHash;
}

public sealed class ParsedSessionCredential(
    Guid sessionId,
    byte[] validatorHash)
{
    public Guid SessionId { get; } = sessionId;
    internal byte[] ValidatorHash { get; } = validatorHash;

    internal void Clear() => CryptographicOperations.ZeroMemory(ValidatorHash);
}
