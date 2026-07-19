using RelistenUserService.Identity.Entities;

namespace RelistenUserService.Authentication;

public sealed class CurrentAccountContext
{
    public User User { get; private set; } = null!;
    public NativeSession NativeSession { get; private set; } = null!;
    public bool IsLoaded { get; private set; }

    public void Set(User user, NativeSession nativeSession)
    {
        User = user;
        NativeSession = nativeSession;
        IsLoaded = true;
    }
}
