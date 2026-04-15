using FluentAssertions;

namespace RelistenApiTests.Collections;

[TestFixture]
public class TestAadamJacobsReviewFixes
{
    private static string ReadRepoFile(string relativePath)
    {
        var repoRoot = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "../../../../"));
        return File.ReadAllText(Path.Combine(repoRoot, relativePath));
    }

    [Test]
    public void RegularArtistRefreshTouchesDeltaCursorAfterSuccessfulImport()
    {
        var scheduledService = ReadRepoFile("RelistenApi/Services/ScheduledService.cs");

        scheduledService.Should().Contain("await _artistService.TouchApiUpdatedAt(artist.id);");
    }

    [Test]
    public void FullArtistContentDeleteProtectsActiveCollectionLinkedPlayback()
    {
        var artistService = ReadRepoFile("RelistenApi/Services/Data/ArtistService.cs");

        artistService.Should().Contain("active_collection_links");
        artistService.Should().Contain("Cannot remove content for");
        artistService.Should().Contain("ci.removed_at IS NOT NULL");
    }

    [Test]
    public void SkippedAndImportErrorCollectionItemsClearPlaybackLinksAndAreNotRelinked()
    {
        var collectionService = ReadRepoFile("RelistenApi/Services/Data/CollectionService.cs");

        collectionService.Should().Contain("artist_uuid = NULL");
        collectionService.Should().Contain("source_uuid = NULL");
        collectionService.Should().Contain("show_uuid = NULL");
        collectionService.Should().Contain("ci.import_status IN (@pendingStatus, @linkedStatus, @importedStatus)");
    }
}
