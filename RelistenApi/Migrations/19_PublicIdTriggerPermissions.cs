using SimpleMigrations;

namespace Migrations;

[Migration(19, "Let restricted canonical writers maintain private ID reservations")]
public sealed class PublicIdTriggerPermissions : SqlFileMigration
{
    protected override void Up() =>
        // Canonical writers must not receive direct access to the private reservation
        // registry. Its trigger owns that bookkeeping with a fixed search path.
        Execute(
            """
            REVOKE ALL ON FUNCTION norm.keep_public_id() FROM PUBLIC;
            ALTER FUNCTION norm.keep_public_id() SECURITY DEFINER;
            ALTER FUNCTION norm.keep_public_id() SET search_path = pg_catalog, pg_temp;
            ALTER FUNCTION norm.refresh_artist_venues(uuid[]) SET search_path = pg_catalog, norm, pg_temp;
            ALTER FUNCTION norm.refresh_song_counts(uuid) SET search_path = pg_catalog, norm, pg_temp;
            """
        );
}
