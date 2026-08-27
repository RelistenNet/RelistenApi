using RelistenUserService.Identity.Entities;

namespace RelistenUserService.Authentication.Authorization;

public sealed class CurrentAccountContext
{
    public User User { get; private set; } = null!;
    public AccountCredentialKind CredentialKind { get; private set; }
    public Guid SessionId { get; private set; }
    public string? WebOrigin { get; private set; }
    public IdentitySessionCapabilities WebCapabilities { get; private set; }
    public bool IsLoaded { get; private set; }
    public bool IsNative => IsLoaded && CredentialKind == AccountCredentialKind.Native;
    public bool IsWeb => IsLoaded && CredentialKind == AccountCredentialKind.Web;

    public Guid NativeSessionId => IsNative
        ? SessionId
        : throw new InvalidOperationException("The current credential is not a native session.");

    public Guid WebSessionId => IsWeb
        ? SessionId
        : throw new InvalidOperationException("The current credential is not a web session.");

    public void SetNative(User user, Guid nativeSessionId)
    {
        User = user;
        CredentialKind = AccountCredentialKind.Native;
        SessionId = nativeSessionId;
        WebOrigin = null;
        WebCapabilities = IdentitySessionCapabilities.None;
        IsLoaded = true;
    }

    public void SetWeb(
        User user,
        Guid webSessionId,
        string webOrigin,
        IdentitySessionCapabilities capabilities)
    {
        User = user;
        CredentialKind = AccountCredentialKind.Web;
        SessionId = webSessionId;
        WebOrigin = webOrigin;
        WebCapabilities = capabilities;
        IsLoaded = true;
    }
}

public enum AccountCredentialKind
{
    None,
    Native,
    Web
}
