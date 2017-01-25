# Db backups

```
pg_dump --schema-only --no-acl --no-owner alecgorge_development --exclude-table="hangfire*" --exclude-schema="hangfire*" > Data/base.sql

pg_dump --no-acl --no-owner alecgorge_development --exclude-table="hangfire*" --exclude-schema="hangfire*" > Data/base-with-data.sql
``
