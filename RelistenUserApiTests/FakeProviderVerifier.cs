using Relisten.UserApi.Services;

namespace RelistenUserApiTests;

public sealed class FakeProviderVerifier : IAuthProviderVerifier
{
    private readonly Dictionary<(string Provider, string Token), string> _subjects = new();

    public void AddSubject(string provider, string token, string subject)
    {
        _subjects[(provider.ToLowerInvariant(), token)] = subject;
    }

    public Task<ProviderIdentity> Verify(string provider, string providerToken)
    {
        return _subjects.TryGetValue((provider.ToLowerInvariant(), providerToken), out var subject)
            ? Task.FromResult(new ProviderIdentity(provider.ToLowerInvariant(), subject))
            : throw new UserAuthException("invalid_provider_token");
    }
}
