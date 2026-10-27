using System.Linq;
using FluentAssertions;
using NUnit.Framework;
using Relisten.Api.Models;
using Relisten.Import;

namespace RelistenApiTests.Importers.ArchiveOrg;

[TestFixture]
public class TestArchiveOrgDeletionGuard
{
    private static readonly Artist MaxCreek = new() { name = "Max Creek", slug = "max-creek" };

    [TestCase(0, false)]
    [TestCase(5, false)]
    [TestCase(6, true)]
    public void ShouldBlockOnlyWhenMoreThanFiveSourcesWouldBeDeleted(
        int sourceCount,
        bool shouldBlock
    )
    {
        ArchiveOrgImporter.ExceedsDeletionLimit(sourceCount).Should().Be(shouldBlock);
    }

    [Test]
    public void MessageLinksEveryBlockedSourceWhenFewWereBlocked()
    {
        var candidates = Enumerable.Range(0, 10).Select(i => $"mc1990-01-{i:00}").ToArray();

        var message = ArchiveOrgImporter.BuildDeletionGuardMessage(MaxCreek, candidates, "42");

        message.Should().StartWith("🛑 Archive.org deletion guard blocked 10 source deletions");
        foreach (var identifier in candidates)
        {
            message.Should().Contain($"[{identifier}](<https://archive.org/details/{identifier}>)");
        }
        message.Should().NotContain("more");
        message.Should().EndWith("Hangfire job: /relisten-admin/hangfire/jobs/details/42");
    }

    [Test]
    public void MessageListsOnlyASampleWhenManyWereBlocked()
    {
        var candidates = Enumerable.Range(0, 5441).Select(i => $"gd{i:00000}").ToArray();

        var message = ArchiveOrgImporter.BuildDeletionGuardMessage(MaxCreek, candidates, null);

        message
            .Split('\n')
            .Count(line => line.StartsWith("- ["))
            .Should()
            .Be(ArchiveOrgImporter.MaxDeletionGuardSourcesListed);
        message.Should().Contain("…and 5431 more");
        message.Length.Should().BeLessThan(2000);
    }

    [Test]
    public void MessageStaysWithinDiscordLimitForLongIdentifiers()
    {
        var candidates = Enumerable
            .Range(0, 10)
            .Select(i => $"{i}-{new string('x', 200)}")
            .ToArray();

        var message = ArchiveOrgImporter.BuildDeletionGuardMessage(MaxCreek, candidates, "42");

        message.Length.Should().BeLessThan(2000);
        message.Should().Contain("more (see worker logs");
    }
}
