using Relisten.UserApi.Services;

namespace RelistenUserApiTests;

public sealed class FakeProviderVerifier : IAuthProviderVerifier
{
    private readonly Dictionary<(string Provider, string Token), ProviderIdentity> _identities = new();

    public void AddSubject(string provider, string token, string subject, string? displayName = null)
    {
        var normalizedProvider = provider.ToLowerInvariant();
        _identities[(normalizedProvider, token)] = new ProviderIdentity(
            normalizedProvider,
            subject,
            displayName);
    }

    public Task<ProviderIdentity> Verify(string provider, string providerToken, string? nonce)
    {
        return _identities.TryGetValue((provider.ToLowerInvariant(), providerToken), out var identity)
            ? Task.FromResult(identity)
            : throw new UserAuthException("invalid_provider_token");
    }
}
