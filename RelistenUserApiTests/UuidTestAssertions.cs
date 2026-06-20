using FluentAssertions;

namespace RelistenUserApiTests;

public static class UuidTestAssertions
{
    public static void ShouldBeUuidV7(Guid uuid)
    {
        uuid.Should().NotBe(Guid.Empty);
        uuid.ToString("D")[14].Should().Be('7');
    }
}
