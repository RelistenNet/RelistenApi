using FluentAssertions;
using NUnit.Framework;
using Relisten.Import;

namespace RelistenApiTests.Importers.ArchiveOrg;

[TestFixture]
public class TestArchiveOrgDeletionGuard
{
    [TestCase(0, false)]
    [TestCase(5, false)]
    [TestCase(6, true)]
    public void ShouldBlockOnlyWhenMoreThanFiveSourcesWouldBeDeleted(int sourceCount, bool shouldBlock)
    {
        ArchiveOrgImporter.ExceedsDeletionLimit(sourceCount).Should().Be(shouldBlock);
    }
}
