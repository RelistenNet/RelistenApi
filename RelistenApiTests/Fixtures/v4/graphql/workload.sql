BEGIN;
INSERT INTO artists(id,uuid,name,slug,updated_at)
VALUES (960000,'10000000-0000-0000-0000-000000000000','Graph workload','graphql-workload',now()),
       (960001,'10000000-0000-0000-0000-000000000001','Graph workload B','graphql-workload-b',now());
INSERT INTO features(id,artist_id,norm_catalogue) VALUES(960000,960000,true),(960001,960001,false);
INSERT INTO shows(id,uuid,artist_id,date,display_date,updated_at)
SELECT 960000+i,('10000000-0000-0000-0001-'||lpad(i::text,12,'0'))::uuid,
       CASE WHEN i=101 THEN 960001 ELSE 960000 END,
       '2025-01-01'::date+(i-1),('2025-01-01'::date+(i-1))::text,now()
FROM generate_series(1,101) i;
INSERT INTO sources(id,uuid,artist_id,show_id,is_soundboard,is_remaster,has_jamcharts,upstream_identifier,display_date,updated_at)
SELECT 960000+i,('10000000-0000-0000-0002-'||lpad(i::text,12,'0'))::uuid,
       960000,960000+i,true,false,false,'graphql-workload-'||i,('2025-01-01'::date+(i-1))::text,now()
FROM generate_series(1,100) i;
INSERT INTO source_sets(id,uuid,source_id,index,name,updated_at)
SELECT 960000+i,('10000000-0000-0000-0003-'||lpad(i::text,12,'0'))::uuid,
       960000+i,1,'Set 1',now() FROM generate_series(1,100) i;
INSERT INTO source_tracks(id,uuid,artist_id,source_id,source_set_id,track_position,duration,title,slug,mp3_url,updated_at)
SELECT 960000+i,('10000000-0000-0000-0004-'||lpad(i::text,12,'0'))::uuid,
       960000,960001+(i-1)/10,960001+(i-1)/10,1+(i-1)%10,600,
       'Workload track '||i,'graphql-workload-track-'||i,'https://example.invalid/graphql/'||i||'.mp3',now()
FROM generate_series(1,1000) i;
INSERT INTO sources(id,uuid,artist_id,show_id,is_soundboard,is_remaster,has_jamcharts,upstream_identifier,display_date,updated_at)
SELECT 960000+i,('10000000-0000-0000-0002-'||lpad(i::text,12,'0'))::uuid,
       960000,960001,true,false,false,'graphql-extra-'||i,'2025-01-01',now()
FROM generate_series(101,104) i;
INSERT INTO source_sets(id,uuid,source_id,index,name,updated_at)
SELECT 960000+i,('10000000-0000-0000-0003-'||lpad(i::text,12,'0'))::uuid,
       960000+i,1,'Set 1',now() FROM generate_series(101,104) i;
INSERT INTO source_tracks(id,uuid,artist_id,source_id,source_set_id,track_position,duration,title,slug,mp3_url,updated_at)
SELECT 960000+i,('10000000-0000-0000-0004-'||lpad(i::text,12,'0'))::uuid,
       960000,960101+(i-1001)/10,960101+(i-1001)/10,1+(i-1001)%10,600,
       'Extra workload track '||i,'graphql-extra-track-'||i,'https://example.invalid/graphql/'||i||'.mp3',now()
FROM generate_series(1001,1040) i;
INSERT INTO show_source_information(show_id,artist_id,source_count,max_avg_rating_weighted,has_soundboard_source,has_flac,max_created_at,max_updated_at)
SELECT 960000+i,960000,1,0,true,false,now(),now() FROM generate_series(1,100) i;
UPDATE show_source_information SET source_count=5 WHERE show_id=960001;
INSERT INTO norm.songs(id,artist_uuid,name,slug,kind,author_type,author_ref,run_uuid)
VALUES('song_00000960','10000000-0000-0000-0000-000000000000','Workload song','graphql-workload-song','song',
       'importer','v4-review','00000000-0000-0000-0000-000000000099');
INSERT INTO norm.track_songs(track_uuid,part,song_id,text,resolved,basis,author_type,author_ref,run_uuid)
SELECT ('10000000-0000-0000-0004-'||lpad(i::text,12,'0'))::uuid,1,'song_00000960','Workload song',
       true,'song_name','importer','v4-review','00000000-0000-0000-0000-000000000099'
FROM generate_series(1,1040) i;
SELECT norm.refresh_song_counts('10000000-0000-0000-0000-000000000000'::uuid);
COMMIT;
