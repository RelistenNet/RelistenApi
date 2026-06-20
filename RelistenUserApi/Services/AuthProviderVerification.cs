namespace Relisten.UserApi.Services;

public sealed record ProviderIdentity(string Provider, string ProviderSubject);

public interface IAuthProviderVerifier
{
    Task<ProviderIdentity> Verify(string provider, string providerToken, string? nonce);
}

public sealed class UnsupportedAuthProviderVerifier : IAuthProviderVerifier
{
    public Task<ProviderIdentity> Verify(string provider, string providerToken, string? nonce)
    {
        throw new UserAuthException("provider_not_configured");
    }
}

public sealed class UserAuthException : Exception
{
    public UserAuthException(string code)
        : base(code)
    {
        Code = code;
    }

    public string Code { get; }
}
