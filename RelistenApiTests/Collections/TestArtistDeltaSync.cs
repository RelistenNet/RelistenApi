using FluentAssertions;
using Relisten.Api.Models;
using Relisten.Data;

namespace RelistenApiTests.Collections;

[TestFixture]
public class TestArtistDeltaSync
{
    [Test]
    public void CollectionDerivedFlagUsesNextArtistFeaturedBit()
    {
        ((int)ArtistFeaturedFlags.CollectionDerived).Should().Be(1 << 2);
    }

    [Test]
    public void CollectionDerivedVisibilityDoesNotRequireIncludeAutocreated()
    {
        var featured = (int)(ArtistFeaturedFlags.AutoCreated | ArtistFeaturedFlags.CollectionDerived);

        ArtistService.IsArtistVisibleForList(featured,
            includeAutoCreated: false,
            includeCollectionDerived: true).Should().BeTrue();
    }

    [Test]
    public void DefaultVisibilityExcludesAutoCreatedAndCollectionDerivedArtists()
    {
        ArtistService.IsArtistVisibleForList((int)ArtistFeaturedFlags.AutoCreated,
            includeAutoCreated: false,
            includeCollectionDerived: false).Should().BeFalse();

        ArtistService.IsArtistVisibleForList((int)ArtistFeaturedFlags.CollectionDerived,
            includeAutoCreated: false,
            includeCollectionDerived: false).Should().BeFalse();
    }

    [Test]
    public void ArtistDeltaResponseCarriesBoundedCursorAndArtists()
    {
        var timestamp = new DateTime(2026, 4, 15, 12, 0, 0, DateTimeKind.Utc);
        var artists = new[] { new ArtistWithCounts { uuid = Guid.NewGuid(), name = "AJC Artist" } };

        var response = new ArtistDeltaResponse
        {
            server_timestamp = timestamp,
            artists = artists
        };

        response.server_timestamp.Should().Be(timestamp);
        response.artists.Should().BeSameAs(artists);
    }
}
