using FluentAssertions;
using Relisten.Data;

namespace RelistenApiTests.Services;

[TestFixture]
public class TestSourceSetService
{
    [TestCase(SourceSetUuidVersion.V1, "::source_set::")]
    [TestCase(SourceSetUuidVersion.V2, "::source_set:v2::")]
    public void UsesExpectedUuidNamespace(SourceSetUuidVersion version, string expectedNamespace)
    {
        SourceSetService.UuidNamespaceFor(version).Should().Be(expectedNamespace);
    }
}
