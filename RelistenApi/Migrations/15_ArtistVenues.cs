using SimpleMigrations;

namespace Migrations;

[Migration(15, "Store artist venue identities and eventually refreshed show counts")]
public sealed class ArtistVenues : SqlFileMigration
{
    protected override void Up() =>
        Execute(Relisten.Normalization.NormalizationSql.Read("015_artist_venues.sql"));
}
