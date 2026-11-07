BEGIN;
INSERT INTO artists(id,uuid,name,slug,updated_at)
VALUES (960000,'10000000-0000-0000-0000-000000000000','Graph read boundary','graphql-boundary',now()),
       (960001,'10000000-0000-0000-0000-000000000001','Graph read boundary B','graphql-boundary-b',now());
INSERT INTO features(id,artist_id,norm_catalogue) VALUES(960000,960000,true),(960001,960001,false);
INSERT INTO shows(id,uuid,artist_id,date,display_date,updated_at)
SELECT 960000+i,('10000000-0000-0000-0001-'||lpad(i::text,12,'0'))::uuid,
       CASE WHEN i=26 THEN 960001 ELSE 960000 END,
       '2025-01-01'::date+(i-1),('2025-01-01'::date+(i-1))::text,now()
FROM generate_series(1,26) i;
INSERT INTO sources(id,uuid,artist_id,show_id,is_soundboard,is_remaster,has_jamcharts,upstream_identifier,display_date,updated_at)
SELECT 960000+i,('10000000-0000-0000-0002-'||lpad(i::text,12,'0'))::uuid,
       960000,960000+i,true,false,false,'graphql-boundary-'||i,('2025-01-01'::date+(i-1))::text,now()
FROM generate_series(1,25) i;
INSERT INTO source_sets(id,uuid,source_id,index,name,updated_at)
SELECT 960000+i,('10000000-0000-0000-0003-'||lpad(i::text,12,'0'))::uuid,
       960000+i,1,'Set 1',now() FROM generate_series(1,25) i;
INSERT INTO source_tracks(id,uuid,artist_id,source_id,source_set_id,track_position,duration,title,slug,mp3_url,updated_at)
SELECT 960000+i,('10000000-0000-0000-0004-'||lpad(i::text,12,'0'))::uuid,
       960000,960001+(i-1)/10,960001+(i-1)/10,1+(i-1)%10,600,
       'Boundary track '||i,'graphql-boundary-track-'||i,'https://example.invalid/graphql/'||i||'.mp3',now()
FROM generate_series(1,250) i;
-- One long recording verifies that complete track arrays are not sliced at estimates.
INSERT INTO source_tracks(id,uuid,artist_id,source_id,source_set_id,track_position,duration,title,slug,mp3_url,updated_at)
SELECT 960000+i,('10000000-0000-0000-0004-'||lpad(i::text,12,'0'))::uuid,
       960000,960001,960001,i-990,600,
       'Long recording track '||i,'graphql-long-track-'||i,'https://example.invalid/graphql/'||i||'.mp3',now()
FROM generate_series(1001,1200) i;
INSERT INTO show_source_information(show_id,artist_id,source_count,max_avg_rating_weighted,has_soundboard_source,has_flac,max_created_at,max_updated_at)
SELECT 960000+i,960000,1,0,true,false,now(),now() FROM generate_series(1,25) i;
-- Canonical identities remain retired after cleanup, so each fixture run needs a fresh ID.
INSERT INTO norm.songs(id,artist_uuid,name,slug,kind,author_type,author_ref,run_uuid)
VALUES(norm.new_id('song'),'10000000-0000-0000-0000-000000000000','Boundary song','graphql-boundary-song','song',
       'importer','v4-review','00000000-0000-0000-0000-000000000099');
INSERT INTO norm.track_songs(track_uuid,part,song_id,text,resolved,basis,author_type,author_ref,run_uuid)
SELECT t.uuid,1,s.id,'Boundary song',
       true,'song_name','importer','v4-review','00000000-0000-0000-0000-000000000099'
FROM source_tracks t JOIN norm.songs s ON s.artist_uuid='10000000-0000-0000-0000-000000000000'
WHERE t.artist_id=960000;
COMMIT;
