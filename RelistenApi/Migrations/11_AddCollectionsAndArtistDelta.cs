using SimpleMigrations;

namespace Migrations;

[Migration(11, "Add collections and artist delta")]
public class AddCollectionsAndArtistDelta : Migration
{
    protected override void Up()
    {
        Execute(@"
            ALTER TABLE artists ADD COLUMN api_updated_at timestamp with time zone;
            UPDATE artists SET api_updated_at = updated_at;
            ALTER TABLE artists ALTER COLUMN api_updated_at SET DEFAULT timezone('utc'::text, now());
            ALTER TABLE artists ALTER COLUMN api_updated_at SET NOT NULL;

            CREATE INDEX idx_artists_api_updated_at ON artists (api_updated_at);

            CREATE TABLE collections (
                uuid uuid primary key,
                slug text not null unique,
                upstream_source_id integer not null references upstream_sources(id),
                upstream_identifier text not null unique,
                collection_type text not null,
                name text not null,
                description text,
                item_count integer not null default 0,
                indexed_at timestamp with time zone,
                last_imported_at timestamp with time zone,
                created_at timestamp with time zone default timezone('utc'::text, now()) not null,
                updated_at timestamp with time zone default timezone('utc'::text, now()) not null
            );

            CREATE TABLE collection_items (
                collection_uuid uuid not null references collections(uuid) on delete cascade,
                upstream_identifier text not null,
                title text not null,
                creator_raw text,
                date_raw text,
                display_date text,
                year integer,
                artist_uuid uuid references artists(uuid),
                show_uuid uuid references shows(uuid),
                source_uuid uuid references sources(uuid),
                import_status integer not null,
                import_error text,
                last_seen_at timestamp with time zone not null,
                removed_at timestamp with time zone,
                last_imported_at timestamp with time zone,
                created_at timestamp with time zone default timezone('utc'::text, now()) not null,
                updated_at timestamp with time zone default timezone('utc'::text, now()) not null,
                primary key (collection_uuid, upstream_identifier)
            );

            CREATE TABLE collection_artist_mappings (
                collection_uuid uuid not null references collections(uuid) on delete cascade,
                creator_name text not null,
                artist_uuid uuid references artists(uuid),
                canonical_name text not null,
                blocked boolean not null default false,
                block_reason text,
                decision_source text not null,
                created_at timestamp with time zone default timezone('utc'::text, now()) not null,
                updated_at timestamp with time zone default timezone('utc'::text, now()) not null,
                primary key (collection_uuid, creator_name)
            );

            CREATE TABLE collection_years (
                collection_uuid uuid not null references collections(uuid) on delete cascade,
                uuid uuid not null unique,
                year text not null,
                artist_count integer not null,
                show_count integer not null,
                source_count integer not null,
                duration bigint not null,
                avg_duration double precision,
                avg_rating double precision,
                created_at timestamp with time zone default timezone('utc'::text, now()) not null,
                updated_at timestamp with time zone not null,
                primary key (collection_uuid, year)
            );

            CREATE INDEX idx_collection_items_status
                ON collection_items (collection_uuid, removed_at, import_status);
            CREATE INDEX idx_collection_items_year
                ON collection_items (collection_uuid, removed_at, year);
            CREATE INDEX idx_collection_items_artist
                ON collection_items (collection_uuid, removed_at, artist_uuid);
            CREATE INDEX idx_collection_items_show
                ON collection_items (collection_uuid, removed_at, show_uuid);
            CREATE INDEX idx_collection_items_source
                ON collection_items (collection_uuid, removed_at, source_uuid);
            CREATE INDEX idx_collection_items_source_uuid
                ON collection_items (source_uuid);
            CREATE INDEX idx_collection_artist_mappings_artist
                ON collection_artist_mappings (collection_uuid, artist_uuid);
            CREATE INDEX idx_collection_years_uuid
                ON collection_years (collection_uuid, uuid);
        ");
    }

    protected override void Down()
    {
        throw new System.NotImplementedException();
    }
}
