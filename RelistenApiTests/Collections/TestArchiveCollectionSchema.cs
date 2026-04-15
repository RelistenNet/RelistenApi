using FluentAssertions;
using RelistenApiTests;

namespace RelistenApiTests.Collections;

[TestFixture]
public class TestArchiveCollectionSchema
{
    [Test]
    public void MigrationAddsCollectionTablesAndArtistDeltaColumn()
    {
        var migrationPath = Path.Combine(TestContext.CurrentContext.TestDirectory,
            "../../../../RelistenApi/Migrations/11_AddCollectionsAndArtistDelta.cs");
        var migration = File.ReadAllText(Path.GetFullPath(migrationPath));

        migration.Should().Contain("ALTER TABLE artists ADD COLUMN api_updated_at");
        migration.Should().Contain("CREATE TABLE collections");
        migration.Should().Contain("CREATE TABLE collection_items");
        migration.Should().Contain("CREATE TABLE collection_artist_mappings");
        migration.Should().Contain("CREATE TABLE collection_years");
    }

    [Test]
    public void CollectionItemDomainLinksUseRestrictDeleteSemantics()
    {
        var migrationPath = Path.Combine(TestContext.CurrentContext.TestDirectory,
            "../../../../RelistenApi/Migrations/11_AddCollectionsAndArtistDelta.cs");
        var migration = File.ReadAllText(Path.GetFullPath(migrationPath));

        migration.Should().Contain("artist_uuid uuid references artists(uuid)");
        migration.Should().Contain("show_uuid uuid references shows(uuid)");
        migration.Should().Contain("source_uuid uuid references sources(uuid)");
        migration.Should().Contain("collection_uuid uuid not null references collections(uuid) on delete cascade");
        migration.Should().NotContain("artist_uuid uuid references artists(uuid) on delete set null");
        migration.Should().NotContain("show_uuid uuid references shows(uuid) on delete set null");
        migration.Should().NotContain("source_uuid uuid references sources(uuid) on delete set null");
    }

    [Test]
    public void MigrationAddsRequiredCollectionIndexes()
    {
        var migrationPath = Path.Combine(TestContext.CurrentContext.TestDirectory,
            "../../../../RelistenApi/Migrations/11_AddCollectionsAndArtistDelta.cs");
        var migration = File.ReadAllText(Path.GetFullPath(migrationPath));

        migration.Should().Contain("idx_collection_items_status");
        migration.Should().Contain("idx_collection_items_year");
        migration.Should().Contain("idx_collection_items_artist");
        migration.Should().Contain("idx_collection_items_show");
        migration.Should().Contain("idx_collection_items_source");
        migration.Should().Contain("idx_collection_artist_mappings_artist");
        migration.Should().Contain("idx_collection_years_uuid");
        migration.Should().Contain("idx_artists_api_updated_at");
    }
}
