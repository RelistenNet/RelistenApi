using Relisten.UserApi.Services;

namespace RelistenUserApiTests;

[TestFixture]
public class UserDataUuidTests
{
    [Test]
    public void New_ShouldCreateUuidV7()
    {
        UuidTestAssertions.ShouldBeUuidV7(UserDataUuid.New());
    }
}
