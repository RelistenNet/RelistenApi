namespace Relisten.UserApi.Services;

public sealed record RefreshToken(
    string Plaintext,
    string Selector,
    string SecretHash,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt);

public sealed class RefreshTokenService
{
    private readonly OpaqueTokenService _tokens;

    public RefreshTokenService(OpaqueTokenService tokens)
    {
        _tokens = tokens;
    }

    public RefreshToken Issue(DateTimeOffset now)
    {
        var token = _tokens.IssueSelector(now, TimeSpan.FromDays(365));
        return new RefreshToken(
            token.Plaintext,
            token.Selector,
            token.SecretHash,
            token.IssuedAt,
            token.ExpiresAt);
    }

    public ParsedRefreshToken Parse(string refreshToken)
    {
        var parsed = _tokens.ParseSelector(refreshToken, "invalid_refresh_token");
        return new ParsedRefreshToken(parsed.Selector, parsed.Secret);
    }

    public bool Verify(string secret, string expectedHash)
    {
        return _tokens.Verify(secret, expectedHash);
    }
}

public sealed record ParsedRefreshToken(string Selector, string Secret);
