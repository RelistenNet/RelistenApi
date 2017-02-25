# Db backups

```
pg_dump --schema-only --no-acl --no-owner alecgorge_development --exclude-table="hangfire*" --exclude-schema="hangfire*" > Data/base.sql

pg_dump --no-acl --no-owner alecgorge_development --exclude-table="hangfire*" --exclude-schema="hangfire*" > Data/base-with-data.sql

pg_dump postgres:///alecgorge --data-only --inserts --no-owner --no-privileges -t artists -t features -t upstream_sources -t artists_upstream_sources > Data/base-data-init.sql
``

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