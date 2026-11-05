-- Synthetic edge cases for disposable integration databases only.
-- Fixed identities let HTTP and snapshot tests share the same recording facts.
BEGIN;
INSERT INTO artists (id, uuid, name, slug, updated_at)
SELECT 910000+i, ('00000000-0000-0000-0000-'||lpad((10+i)::text,12,'0'))::uuid,
       label, 'v4-review-'||i, '2026-01-01T00:00:00Z'
FROM (VALUES (1,'Raw Artist'),(2,'Disabled Artist'),(3,'Partial Artist'),(4,'Matched Artist')) f(i,label);
INSERT INTO features (id,artist_id,norm_catalogue)
SELECT 910000+i,910000+i,i IN (3,4) FROM generate_series(1,4) i;
INSERT INTO years (id,uuid,artist_id,year,updated_at)
SELECT 910000+i,('00000000-0000-0000-0000-'||lpad((60+i)::text,12,'0'))::uuid,910000+i,'1995','2026-01-01T00:00:00Z'
FROM generate_series(1,4) i;
INSERT INTO venues (id,uuid,artist_id,name,location,slug,updated_at)
SELECT 910000+i,('00000000-0000-0000-0000-'||lpad((70+i)::text,12,'0'))::uuid,910000+i,
       'Imported Civic Center','Philadelphia, PA','civic-center','2026-01-01T00:00:00Z'
FROM generate_series(1,4) i;
INSERT INTO tours (id,uuid,artist_id,start_date,end_date,name,slug,upstream_identifier,updated_at)
VALUES (910004,'00000000-0000-0000-0000-000000000084',910004,'1995-12-01','1996-01-01','Winter Tour','winter-tour','review-tour','2026-01-01T00:00:00Z');
INSERT INTO shows (id,uuid,artist_id,year_id,venue_id,date,display_date,updated_at)
SELECT 910000+i,('00000000-0000-0000-0000-'||lpad(i::text,12,'0'))::uuid,
       910000+i,910000+i,910000+i,'1995-12-31','1995-12-31','2026-01-01T00:00:00Z'
FROM generate_series(1,4) i;
UPDATE shows SET tour_id=910004 WHERE id=910004;
INSERT INTO sources (id,uuid,show_id,artist_id,venue_id,is_soundboard,is_remaster,has_jamcharts,
                     upstream_identifier,display_date,updated_at,description,taper_notes,source,taper,transferrer,lineage)
SELECT 910000+i,('00000000-0000-0000-0000-'||lpad((20+i)::text,12,'0'))::uuid,
       910000+i,910000+i,910000+i,true,false,false,'synthetic-review-'||i,'1995-12-31','2026-01-01T00:00:00Z',
       'Synthetic review recording','Original taper notes','SBD > DAT','Fixture taper','Fixture transferrer','DAT > FLAC'
FROM generate_series(1,4) i;
INSERT INTO source_sets (id,uuid,source_id,index,name,updated_at)
SELECT 910000+i,('00000000-0000-0000-0000-'||lpad((30+i)::text,12,'0'))::uuid,
       910000+i,1,'Set 1','2026-01-01T00:00:00Z' FROM generate_series(1,4) i;
INSERT INTO source_tracks (id,uuid,artist_id,source_id,source_set_id,track_position,duration,title,slug,mp3_url,flac_url,updated_at)
SELECT 910000+i,('00000000-0000-0000-0000-'||lpad((40+i)::text,12,'0'))::uuid,
       910000+i,910000+i,910000+i,1,600,
       'Scarlet Begonias > Fire on the Mountain','scarlet-fire',
       'https://example.invalid/review/'||i||'.mp3','https://example.invalid/review/'||i||'.flac','2026-01-01T00:00:00Z'
FROM generate_series(1,4) i;
INSERT INTO norm.runs (uuid,author_type,author_ref,note)
VALUES ('00000000-0000-0000-0000-000000000099','importer','v4-review','Synthetic C1 fixtures');
INSERT INTO norm.venues (id,name,slug,author_type,author_ref,run_uuid)
VALUES ('venue_00000003','Arena Site','review-arena-site','importer','v4-review','00000000-0000-0000-0000-000000000099');
INSERT INTO norm.venues (id,name,slug,kind,parent_id,city,region,country,author_type,author_ref,run_uuid)
VALUES ('venue_00000001' ,'Current Arena','review-arena','room','venue_00000003','Philadelphia','PA','US','importer','v4-review','00000000-0000-0000-0000-000000000099');
INSERT INTO norm.venue_names (venue_id,normalized,name,kind,valid_from,valid_to,author_type,author_ref,run_uuid)
VALUES ('venue_00000001','historical-arena','Historical Arena','official','1990-01-01','2000-01-01','importer','v4-review','00000000-0000-0000-0000-000000000099');
-- Preserve open bounds, overlapping periods, repeated names, and non-official aliases.
INSERT INTO norm.venue_names (venue_id,normalized,name,kind,valid_from,valid_to,author_type,author_ref,run_uuid)
VALUES ('venue_00000001','original-arena','Original Arena','official','-infinity','1990-01-01','importer','v4-review','00000000-0000-0000-0000-000000000099'),
       ('venue_00000001','historical-arena','Historical Arena','official','1994-01-01','1996-01-01','importer','v4-review','00000000-0000-0000-0000-000000000099'),
       ('venue_00000001','current-arena','Current Arena','official','2000-01-01',NULL,'importer','v4-review','00000000-0000-0000-0000-000000000099'),
       ('venue_00000001','arena','Arena','short','-infinity',NULL,'importer','v4-review','00000000-0000-0000-0000-000000000099'),
       ('venue_00000001','historical-areena','Historical Areena','spelling','-infinity',NULL,'importer','v4-review','00000000-0000-0000-0000-000000000099');
INSERT INTO norm.show_venues (show_uuid,venue_id,basis,author_type,author_ref,run_uuid)
SELECT ('00000000-0000-0000-0000-'||lpad(i::text,12,'0'))::uuid,'venue_00000001','evidence',
       'importer','v4-review','00000000-0000-0000-0000-000000000099'
FROM generate_series(2,4) i;
INSERT INTO norm.songs (id,artist_uuid,name,slug,kind,author_type,author_ref,run_uuid)
SELECT 'song_0000000'||(i-1),('00000000-0000-0000-0000-'||lpad((10+i)::text,12,'0'))::uuid,
       'Scarlet Begonias','scarlet-begonias','song','importer','v4-review','00000000-0000-0000-0000-000000000099'
FROM generate_series(2,4) i;
INSERT INTO norm.songs (id,artist_uuid,name,slug,kind,author_type,author_ref,run_uuid)
VALUES ('song_00000004','00000000-0000-0000-0000-000000000014','Fire on the Mountain','fire','song','importer','v4-review','00000000-0000-0000-0000-000000000099');
INSERT INTO norm.track_songs (track_uuid,part,song_id,text,segue_after,resolved,basis,author_type,author_ref,run_uuid)
SELECT ('00000000-0000-0000-0000-'||lpad((40+i)::text,12,'0'))::uuid,1,'song_0000000'||(i-1),
       'Scarlet Begonias',true,true,'song_name','importer','v4-review','00000000-0000-0000-0000-000000000099'
FROM generate_series(2,4) i;
INSERT INTO norm.track_songs (track_uuid,part,song_id,text,segue_after,resolved,basis,author_type,author_ref,run_uuid)
VALUES ('00000000-0000-0000-0000-000000000043',2,NULL,'Fire on the Mountain',false,false,'open','importer','v4-review','00000000-0000-0000-0000-000000000099'),
       ('00000000-0000-0000-0000-000000000044',2,'song_00000004','Fire on the Mountain',false,true,'song_name','importer','v4-review','00000000-0000-0000-0000-000000000099');
-- A resolved non-song part and an approved track not interpreted yet.
INSERT INTO source_tracks (id,uuid,artist_id,source_id,source_set_id,track_position,duration,title,slug,mp3_url,updated_at)
VALUES (910005,'00000000-0000-0000-0000-000000000045',910004,910004,910004,2,20,'Tuning','tuning','https://example.invalid/tuning.mp3','2026-01-01T00:00:00Z'),
       (910006,'00000000-0000-0000-0000-000000000046',910004,910004,910004,3,30,'Unknown title','unknown','https://example.invalid/unknown.mp3','2026-01-01T00:00:00Z');
INSERT INTO norm.track_songs (track_uuid,part,text,resolved,basis,author_type,author_ref,run_uuid)
VALUES ('00000000-0000-0000-0000-000000000045',1,'Tuning',true,'noise','importer','v4-review','00000000-0000-0000-0000-000000000099');
-- Browse edges: one known but empty auto-created artist, and a second year
-- for the matched artist. These rows distinguish membership, ordering and scope.
UPDATE artists SET featured=1 WHERE id=910001;
UPDATE artists SET featured=2 WHERE id=910002;
INSERT INTO artists (id,uuid,name,slug,featured,updated_at)
VALUES (910010,'00000000-0000-0000-0000-000000000020','Empty Artist','v4-empty',2,'2026-01-01T00:00:00Z');
INSERT INTO features (id,artist_id) VALUES (910010,910010);
UPDATE years SET show_count=1,source_count=1 WHERE id BETWEEN 910001 AND 910004;
INSERT INTO years (id,uuid,artist_id,year,show_count,source_count,updated_at)
VALUES (910010,'00000000-0000-0000-0000-000000000160',910004,'1994',1,1,'2026-01-01T00:00:00Z');
INSERT INTO shows (id,uuid,artist_id,year_id,venue_id,date,display_date,updated_at)
VALUES (910010,'00000000-0000-0000-0000-000000000010',910004,910010,910004,'1994-07-01','1994-07-01','2026-01-01T00:00:00Z');
INSERT INTO sources (id,uuid,show_id,artist_id,venue_id,is_soundboard,is_remaster,has_jamcharts,upstream_identifier,display_date,updated_at)
VALUES (910010,'00000000-0000-0000-0000-000000000120',910010,910004,910004,false,false,false,'browse-review','1994-07-01','2026-01-01T00:00:00Z');
INSERT INTO source_sets (id,uuid,source_id,index,name,updated_at)
VALUES (910010,'00000000-0000-0000-0000-000000000130',910010,1,'Set 1','2026-01-01T00:00:00Z');
INSERT INTO source_tracks (id,uuid,artist_id,source_id,source_set_id,track_position,duration,title,slug,mp3_url,updated_at)
VALUES (910010,'00000000-0000-0000-0000-000000000150',910004,910010,910010,1,120,'Earlier show track','earlier-track','https://example.invalid/earlier.mp3','2026-01-01T00:00:00Z');
INSERT INTO upstream_sources (id,name,url,description,credit_line)
VALUES (910010,'review.provider','https://example.invalid','Unknown provider remains readable','Fixture credit');
INSERT INTO artists_upstream_sources (artist_id,upstream_source_id,upstream_identifier)
VALUES (910004,910010,'matched-upstream');
-- Imported years retain four-digit calendar labels, including early years.
INSERT INTO years (id,uuid,artist_id,year,show_count,source_count,updated_at)
VALUES (910023,'00000000-0000-0000-0000-000000000023',910001,'0023',0,0,'2026-01-01T00:00:00Z');
-- Materialize the same derived relationships that production writes refresh explicitly.
SELECT norm.refresh_artist_venues();
COMMIT;
