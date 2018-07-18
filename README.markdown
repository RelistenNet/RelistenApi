# Db backups

```
pg_dump --schema-only --no-acl --no-owner alecgorge > Data/base.sql

pg_dump --no-acl --no-owner alecgorge  > Data/base-with-data.sql

pg_dump postgres:///alecgorge --schema-only --no-acl --no-owner alecgorge > Data/base.sql
pg_dump postgres:///alecgorge --data-only --inserts --no-owner --no-privileges -t artists -t features -t upstream_sources -t artists_upstream_sources > Data/base-data-init.sql

cat Data/base.sql Data/base-data-init.sql > Data/seed.sql
```

# Delete all content for artist from a botched import

```
delete from setlist_songs where artist_id = 5;
delete from setlist_shows where artist_id = 5;
delete from shows where artist_id = 5;
delete from sources where artist_id = 5;
delete from tours where artist_id = 5;
delete from venues where artist_id = 5;
delete from years where artist_id = 5;
```

```
ALTER TABLE "public"."artists" ADD COLUMN "uuid" uuid;
ALTER TABLE "public"."tours" ADD COLUMN "uuid" uuid;
ALTER TABLE "public"."venues" ADD COLUMN "uuid" uuid;
ALTER TABLE "public"."years" ADD COLUMN "uuid" uuid;
ALTER TABLE "public"."shows" ADD COLUMN "uuid" uuid;
ALTER TABLE "public"."sources" ADD COLUMN "uuid" uuid;
ALTER TABLE "public"."setlist_shows" ADD COLUMN "uuid" uuid;
ALTER TABLE "public"."setlist_songs" ADD COLUMN "uuid" uuid;
```

```
artist_id::artist::artist_id
artist_id::tour::tour_upstream_identifier
artist_id::venue::venue_upstream_identifier
artist_id::year::year
artist_id::show::display_date
artist_id::source::upstream_identifier
artist_id::setlist_show::upstream_identifier
artist_id::setlist_song::upstream_identifier
```

```
update artists set uuid = md5('root::artist::' || slug)::uuid;
update tours set uuid = md5(artist_id || '::tour::' || upstream_identifier)::uuid;
update venues set uuid = md5(artist_id || '::venue::' || upstream_identifier)::uuid;
update years set uuid = md5(artist_id || '::year::' || year)::uuid;
update shows set uuid = md5(artist_id || '::show::' || display_date)::uuid;
update sources set uuid = md5(artist_id || '::source::' || upstream_identifier)::uuid;
update setlist_shows set uuid = md5(artist_id || '::setlist_show::' || upstream_identifier)::uuid;
update setlist_songs set uuid = md5(artist_id || '::setlist_song::' || upstream_identifier)::uuid;
```

```
ALTER TABLE "public"."artists" 	ALTER COLUMN "uuid" SET NOT NULL;
ALTER TABLE "public"."tours" 	ALTER COLUMN "uuid" SET NOT NULL;
ALTER TABLE "public"."venues" 	ALTER COLUMN "uuid" SET NOT NULL;
ALTER TABLE "public"."years" 	ALTER COLUMN "uuid" SET NOT NULL;
ALTER TABLE "public"."shows" 	ALTER COLUMN "uuid" SET NOT NULL;
ALTER TABLE "public"."sources" 	ALTER COLUMN "uuid" SET NOT NULL;
ALTER TABLE "public"."setlist_shows" 	ALTER COLUMN "uuid" SET NOT NULL;
ALTER TABLE "public"."setlist_songs" 	ALTER COLUMN "uuid" SET NOT NULL;

ALTER TABLE "public"."artists" 	ADD UNIQUE ("uuid");
ALTER TABLE "public"."tours" 	ADD UNIQUE ("uuid");
ALTER TABLE "public"."venues" 	ADD UNIQUE ("uuid");
ALTER TABLE "public"."years" 	ADD UNIQUE ("uuid");
ALTER TABLE "public"."shows" 	ADD UNIQUE ("uuid");
ALTER TABLE "public"."sources" 	ADD UNIQUE ("uuid");
ALTER TABLE "public"."setlist_shows" 	ADD UNIQUE ("uuid");
ALTER TABLE "public"."setlist_songs" 	ADD UNIQUE ("uuid");
```