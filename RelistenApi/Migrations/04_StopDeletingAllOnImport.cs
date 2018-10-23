using SimpleMigrations;

namespace Migrations
{
    [Migration(4, "Adds necessary indexes and controls to prevent bulk deletes while importing")]
    public class StopDeletingAllOnImport : Migration
    {
        protected override void Up()
        {
            Execute(/*@"
ALTER TABLE ONLY source_reviews
    ADD COLUMN uuid uuid NULL;

UPDATE source_reviews
    SET uuid = md5(source_id || '::review::' || COALESCE('' || rating, 'NULL') || COALESCE('' || title, 'NULL') || COALESCE('' || author, 'NULL') || updated_at)::uuid;

ALTER TABLE ONLY source_reviews
    ALTER COLUMN uuid SET NOT NULL;

delete from source_reviews where uuid IN (select uuid from source_reviews group by uuid having count(*) > 1);

ALTER TABLE ONLY source_reviews
    ADD CONSTRAINT source_reviews_uuid UNIQUE (uuid);

ALTER TABLE ONLY source_tracks
    ADD COLUMN is_orphaned BOOLEAN NOT NULL DEFAULT FALSE;

ALTER TABLE ONLY source_tracks
    ALTER COLUMN source_set_id DROP NOT NULL;
    
ALTER TABLE ONLY source_tracks
    ALTER COLUMN source_id DROP NOT NULL;
    
ALTER TABLE ONLY source_tracks
    DROP CONSTRAINT source_tracks_set_id_fkey;

ALTER TABLE ONLY source_tracks    
    ADD CONSTRAINT source_tracks_set_id_fkey FOREIGN KEY (source_set_id) REFERENCES source_sets(id) ON UPDATE CASCADE ON DELETE SET NULL;

ALTER TABLE ONLY source_tracks
    DROP CONSTRAINT source_tracks_source_id_fkey;

ALTER TABLE ONLY source_tracks
    ADD CONSTRAINT source_tracks_source_id_fkey FOREIGN KEY (source_id) REFERENCES sources(id) ON UPDATE CASCADE ON DELETE SET NULL;

-- weird orphaned duplicates
DELETE FROM source_sets WHERE id IN (136481,135842);

ALTER TABLE ONLY source_sets
    ADD CONSTRAINT source_sets_source_id_index_key UNIQUE (source_id, index);

ALTER TABLE ONLY setlist_songs_plays
    ADD CONSTRAINT setlist_songs_plays_song_id_show_id_key UNIQUE (played_setlist_song_id, played_setlist_show_id);    
           "*/ @"SELECT 1");
        }

        protected override void Down()
        {
            Execute(@"
ALTER TABLE ONLY source_tracks
    DROP COLUMN is_orphaned,
    ALTER COLUMN source_set_id ADD NOT NULL,
    DROP CONSTRAINT source_tracks_set_id_fkey,
    ADD CONSTRAINT source_tracks_set_id_fkey FOREIGN KEY (source_set_id) REFERENCES source_sets(id) ON UPDATE CASCADE ON DELETE CASCADE;

DROP INDEX setlist_songs_plays_key;

ALTER TABLE ONLY source_sets
    DROP CONSTRAINT source_sets_source_id_index;

ALTER TABLE ONLY source_reviews
    DROP COLUMN uuid,
    DROP CONSTRAINT source_reviews_uuid;
            ");
        }
    }
}