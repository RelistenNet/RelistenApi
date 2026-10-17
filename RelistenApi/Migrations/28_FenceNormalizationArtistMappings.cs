using System;
using SimpleMigrations;

namespace Migrations;

/// <summary>
/// Artist identity fields participate in provider lookup even before a recording exists.
/// In particular, Setlist.fm ownership uses MusicBrainz IDs rather than mapping identifiers.
/// </summary>
[Migration(28, "Fence artist identity and MusicBrainz mapping lookups")]
public sealed class FenceNormalizationArtistMappings : Migration
{
    public const string SchemaSql = """
        CREATE FUNCTION normalization.record_artist_mapping_revision() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF TG_OP='UPDATE' AND OLD.uuid IS NOT DISTINCT FROM NEW.uuid
                AND OLD.musicbrainz_id IS NOT DISTINCT FROM NEW.musicbrainz_id THEN
                RETURN NULL;
            END IF;
            UPDATE normalization.catalog_mapping_registry_revision
                SET generation=generation+1 WHERE singleton;
            RETURN NULL;
        END $$;

        -- Run after the normalization_catalog_* artist/source clocks, preserving the
        -- publication lock order. Names and timestamps do not change lookup membership.
        CREATE TRIGGER normalization_mapping_artist_identity AFTER INSERT OR UPDATE OR DELETE ON public.artists
            FOR EACH ROW EXECUTE FUNCTION normalization.record_artist_mapping_revision();
        -- TRUNCATE remains offline maintenance with catalog writers and publishers stopped.
        CREATE TRIGGER normalization_mapping_artist_truncate AFTER TRUNCATE ON public.artists
            FOR EACH STATEMENT EXECUTE FUNCTION normalization.record_mapping_registry_revision();

        -- Older tickets did not protect MusicBrainz or artist-presence lookups. Invalidate
        -- them once when installing this coverage; fresh clocks cannot repair old read sets.
        UPDATE normalization.catalog_mapping_registry_revision SET generation=generation+1 WHERE singleton;
        """;

    protected override void Up() => Execute(SchemaSql);

    protected override void Down() =>
        throw new NotSupportedException(
            "Disable normalization publication on rollback; retain artist mapping invalidation."
        );
}
