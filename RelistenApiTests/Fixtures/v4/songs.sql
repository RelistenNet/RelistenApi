-- Song discovery edges are isolated from the original playback and venue fixtures.
-- Fixed IDs keep assertions readable without depending on random normalized IDs.
BEGIN;
SET LOCAL norm.author_type='importer';
SET LOCAL norm.author_ref='v4-review';
SET LOCAL norm.run_uuid='00000000-0000-0000-0000-000000000099';
INSERT INTO artists(id,uuid,name,slug,updated_at)
SELECT 925000+i,('00000000-0000-0000-0000-'||lpad((300+i)::text,12,'0'))::uuid,
       label,'v4-song-review-'||i,'2026-01-01'
FROM (VALUES(1,'Song Artist'),(2,'Disabled Song Artist'),(3,'Imported Song Artist')) x(i,label);
INSERT INTO features(id,artist_id,norm_catalogue)
SELECT 925000+i,925000+i,i=1 FROM generate_series(1,3) i;
INSERT INTO venues(id,uuid,artist_id,name,location,slug,updated_at)
SELECT 925000+i,('00000000-0000-0000-0000-'||lpad((370+i)::text,12,'0'))::uuid,
       925000+i,'Song Venue','Philadelphia, PA','song-venue','2026-01-01'
FROM generate_series(1,3) i;
INSERT INTO shows(id,uuid,artist_id,venue_id,date,display_date,updated_at)
SELECT 925010+i,('00000000-0000-0000-0000-'||lpad((310+i)::text,12,'0'))::uuid,
       925001,925001,('1995-07-0'||i)::date,'1995-07-0'||i,'2026-01-01'
FROM generate_series(1,3) i;
-- A different artist on the same date must not join this artist's setlist.
INSERT INTO shows(id,uuid,artist_id,venue_id,date,display_date,updated_at)
VALUES(925014,'00000000-0000-0000-0000-000000000314',925002,925002,'1995-07-01','1995-07-01','2026-01-01');
INSERT INTO sources(id,uuid,show_id,artist_id,venue_id,is_soundboard,is_remaster,has_jamcharts,upstream_identifier,display_date,updated_at)
SELECT 925020+i,('00000000-0000-0000-0000-'||lpad((320+i)::text,12,'0'))::uuid,
       CASE WHEN i IN(1,2) THEN 925011 ELSE 925012 END,
       925001,925001,false,false,false,'song-recording-'||i,
       CASE WHEN i IN(1,2) THEN '1995-07-01' ELSE '1995-07-02' END,'2026-01-01'
FROM generate_series(1,3) i;
INSERT INTO source_sets(id,uuid,source_id,index,name,updated_at)
SELECT 925030+i,('00000000-0000-0000-0000-'||lpad((330+i)::text,12,'0'))::uuid,
       925020+i,1,'Set 1','2026-01-01'
FROM generate_series(1,3) i;
INSERT INTO source_tracks(id,uuid,artist_id,source_id,source_set_id,track_position,title,slug,mp3_url,updated_at,is_orphaned)
SELECT 925040+i,('00000000-0000-0000-0000-'||lpad((340+i)::text,12,'0'))::uuid,
       925001,925020+least(i,3),925030+least(i,3),CASE WHEN i<4 THEN 1 ELSE i-2 END,
       CASE WHEN i=1 THEN 'Original Scarlet > Fire > Scarlet' ELSE 'Imported song recording '||i END,
       'song-track-'||i,'https://example.invalid/song/'||i||'.mp3','2026-01-01',i=4
FROM generate_series(1,5) i;
INSERT INTO norm.songs(id,artist_uuid,name,slug,kind,original_artist,author_type,author_ref,run_uuid)
VALUES ('song_00000101','00000000-0000-0000-0000-000000000301','Scarlet Begonias','scarlet-begonias','song','Grateful Dead','importer','v4-review','00000000-0000-0000-0000-000000000099'),
       ('song_00000102','00000000-0000-0000-0000-000000000301','Fire on the Mountain','fire-on-the-mountain','song',NULL,'importer','v4-review','00000000-0000-0000-0000-000000000099'),
       ('song_00000103','00000000-0000-0000-0000-000000000301','Unrecorded Song','unrecorded-song','song',NULL,'importer','v4-review','00000000-0000-0000-0000-000000000099'),
       ('song_00000104','00000000-0000-0000-0000-000000000301','Old Scarlet Name','old-scarlet-name','song',NULL,'importer','v4-review','00000000-0000-0000-0000-000000000099'),
       ('song_00000105','00000000-0000-0000-0000-000000000302','Disabled Song','disabled-song','song',NULL,'importer','v4-review','00000000-0000-0000-0000-000000000099');
UPDATE norm.songs SET merged_into='song_00000101' WHERE id='song_00000104';
INSERT INTO norm.song_aliases(artist_uuid,normalized,alias,song_id,author_type,author_ref,run_uuid)
VALUES ('00000000-0000-0000-0000-000000000301','scarlet','Scarlet','song_00000101','importer','v4-review','00000000-0000-0000-0000-000000000099'),
       ('00000000-0000-0000-0000-000000000301','old-scarlet','Old Scarlet','song_00000104','importer','v4-review','00000000-0000-0000-0000-000000000099');
INSERT INTO norm.track_songs(track_uuid,part,song_id,text,resolved,basis,author_type,author_ref,run_uuid)
VALUES ('00000000-0000-0000-0000-000000000341',1,'song_00000101','Scarlet Begonias',true,'ruling','importer','v4-review','00000000-0000-0000-0000-000000000099'),
       ('00000000-0000-0000-0000-000000000341',2,'song_00000102','Fire on the Mountain',true,'ruling','importer','v4-review','00000000-0000-0000-0000-000000000099'),
       ('00000000-0000-0000-0000-000000000341',3,'song_00000101','Scarlet Begonias reprise',true,'ruling','importer','v4-review','00000000-0000-0000-0000-000000000099'),
       ('00000000-0000-0000-0000-000000000342',1,'song_00000104','Old Scarlet Name',true,'ruling','importer','v4-review','00000000-0000-0000-0000-000000000099'),
       ('00000000-0000-0000-0000-000000000343',1,'song_00000101','Scarlet',true,'ruling','importer','v4-review','00000000-0000-0000-0000-000000000099'),
       ('00000000-0000-0000-0000-000000000343',2,NULL,'Unknown ending',false,'open','importer','v4-review','00000000-0000-0000-0000-000000000099'),
       ('00000000-0000-0000-0000-000000000344',1,'song_00000101','Orphaned Scarlet',true,'ruling','importer','v4-review','00000000-0000-0000-0000-000000000099'),
       ('00000000-0000-0000-0000-000000000345',1,'song_00000101','Unresolved proposal',false,'ruling','importer','v4-review','00000000-0000-0000-0000-000000000099');
INSERT INTO setlist_songs(id,uuid,artist_id,name,slug,upstream_identifier,updated_at)
VALUES (925051,'00000000-0000-0000-0000-000000000351',925001,'Imported Scarlet','imported-scarlet','review-351','2026-01-01'),
       (925052,'00000000-0000-0000-0000-000000000352',925001,'Scarlet Begonias','same-name-unmapped','review-352','2026-01-01'),
       (925053,'00000000-0000-0000-0000-000000000353',925001,'Unused imported song','unused-imported','review-353','2026-01-01'),
       (925054,'00000000-0000-0000-0000-000000000354',925001,'Unrecorded imported song','unrecorded-imported','review-354','2026-01-01'),
       (925055,'00000000-0000-0000-0000-000000000355',925002,'Disabled imported song','disabled-imported','review-355','2026-01-01'),
       (925056,'00000000-0000-0000-0000-000000000356',925003,'Only imported song','only-imported','review-356','2026-01-01');
INSERT INTO norm.song_evidence(setlist_song_uuid,song_id,author_type,author_ref,run_uuid)
VALUES ('00000000-0000-0000-0000-000000000351','song_00000104','importer','v4-review','00000000-0000-0000-0000-000000000099'),
       ('00000000-0000-0000-0000-000000000354','song_00000103','importer','v4-review','00000000-0000-0000-0000-000000000099'),
       ('00000000-0000-0000-0000-000000000355','song_00000105','importer','v4-review','00000000-0000-0000-0000-000000000099');
-- Two independent setlists for one date must not duplicate its catalog show.
INSERT INTO setlist_shows(id,uuid,artist_id,venue_id,upstream_identifier,date,updated_at)
VALUES (925061,'00000000-0000-0000-0000-000000000361',925001,925001,'song-setlist-1','1995-07-01','2026-01-01'),
       (925062,'00000000-0000-0000-0000-000000000362',925001,925001,'song-setlist-2','1995-07-01','2026-01-01'),
       (925063,'00000000-0000-0000-0000-000000000363',925001,925001,'song-setlist-3','1995-07-03','2026-01-01'),
       (925064,'00000000-0000-0000-0000-000000000364',925001,925001,'song-setlist-only','1998-01-01','2026-01-01'),
       (925065,'00000000-0000-0000-0000-000000000365',925001,925001,'song-setlist-unmapped','1995-07-02','2026-01-01');
INSERT INTO setlist_songs_plays(played_setlist_song_id,played_setlist_show_id)
VALUES (925051,925061),(925051,925062),(925051,925063),(925051,925064),(925052,925065),(925054,925064);
COMMIT;
