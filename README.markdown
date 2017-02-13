# Db backups

```
pg_dump --schema-only --no-acl --no-owner alecgorge_development --exclude-table="hangfire*" --exclude-schema="hangfire*" > Data/base.sql

pg_dump --no-acl --no-owner alecgorge_development --exclude-table="hangfire*" --exclude-schema="hangfire*" > Data/base-with-data.sql

pg_dump postgres:///alecgorge --data-only --inserts --no-owner --no-privileges -t artists -t features -t upstream_sources -t artists_upstream_sources > Data/base-data-init.sql
``
