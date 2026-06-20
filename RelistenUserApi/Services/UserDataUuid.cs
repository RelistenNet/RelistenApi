namespace Relisten.UserApi.Services;

public static class UserDataUuid
{
    public static Guid New()
    {
        return Guid.CreateVersion7();
    }
}
