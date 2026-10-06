using FluentAssertions;
using NUnit.Framework;
using Relisten.Import;
using Relisten.Vendor.ArchiveOrg;

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

    [Test]
    public void ReconciliationOnlyRemovesMissingSourcesOrConfirmedMp3Loss()
    {
        var docs = new[] { new SearchDoc { identifier = "keep" }, new SearchDoc { identifier = "lost-mp3s" },
            new SearchDoc { identifier = "new-unplayable" } };

        var deleted = ArchiveOrgImporter.SourceIdentifiersToDelete(
            ["keep", "removed", "lost-mp3s"], docs, ["lost-mp3s", "new-unplayable"]);

        deleted.Should().BeEquivalentTo("removed", "lost-mp3s");
    }

    [Test]
    public void CompleteEmptySearchStillTriggersGuardForMoreThanFiveExistingSources()
    {
        var deleted = ArchiveOrgImporter.SourceIdentifiersToDelete(
            Enumerable.Range(0, 6).Select(i => $"source-{i}"), [], []);

        deleted.Should().HaveCount(6);
        ArchiveOrgImporter.ExceedsDeletionLimit(deleted.Count).Should().BeTrue();
    }
}
