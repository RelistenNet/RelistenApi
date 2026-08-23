\set ON_ERROR_STOP on
\pset pager off

\echo 'Phish SourceSet UUID v2 migration'
\echo 'Safe default: this file only runs read-only checks unless apply_phish_source_set_uuid_v2 is set to 1.'
\echo 'Deploy the v2 SourceSet UUID code first, then run the apply phase through a direct connection to the PostgreSQL primary.'

SELECT
    current_database() AS database,
    current_user AS database_user,
    pg_is_in_recovery() AS is_replica,
    current_setting('server_version') AS postgres_version;

SELECT
    artist.id,
    artist.uuid,
    artist.name,
    artist.slug
FROM artists artist
WHERE artist.uuid = 'ca53d281-0041-ae33-a050-c87702d93b0c'::uuid;

WITH targets AS (
    SELECT
        source_set.id,
        source_set.uuid AS current_uuid,
        md5(source.uuid::text || '::source_set::' || source_set.index::text)::uuid AS legacy_uuid,
        md5(source.uuid::text || '::source_set:v2::' || source_set.index::text)::uuid AS target_uuid
    FROM source_sets source_set
    JOIN sources source ON source.id = source_set.source_id
    WHERE source.artist_id = (
        SELECT artist.id
        FROM artists artist
        WHERE artist.uuid = 'ca53d281-0041-ae33-a050-c87702d93b0c'::uuid
    )
)
SELECT
    count(*) AS source_sets,
    count(*) FILTER (WHERE current_uuid = legacy_uuid) AS legacy_source_sets,
    count(*) FILTER (WHERE current_uuid = target_uuid) AS v2_source_sets,
    count(*) FILTER (WHERE current_uuid NOT IN (legacy_uuid, target_uuid)) AS unexpected_source_sets,
    count(DISTINCT target_uuid) AS distinct_v2_uuids
FROM targets;

WITH targets AS (
    SELECT
        source_set.id,
        md5(source.uuid::text || '::source_set:v2::' || source_set.index::text)::uuid AS target_uuid
    FROM source_sets source_set
    JOIN sources source ON source.id = source_set.source_id
    WHERE source.artist_id = (
        SELECT artist.id
        FROM artists artist
        WHERE artist.uuid = 'ca53d281-0041-ae33-a050-c87702d93b0c'::uuid
    )
)
SELECT count(*) AS v2_uuids_owned_by_other_rows
FROM targets target
JOIN source_sets existing
  ON existing.uuid = target.target_uuid
 AND existing.id <> target.id;

\if :{?apply_phish_source_set_uuid_v2}
  \if :apply_phish_source_set_uuid_v2
    SELECT NOT pg_is_in_recovery() AS server_is_primary \gset
    \if :server_is_primary
      \echo 'Primary-server guard passed. Rotating Phish SourceSet UUIDs.'
    \else
      \echo 'ERROR: refusing to rotate SourceSet UUIDs on a replica.'
      SELECT current_setting('relisten.source_set_uuid_v2_requires_primary');
    \endif

    BEGIN;
    SET LOCAL lock_timeout = '10s';
    SET LOCAL statement_timeout = '5min';

    DO $verify_source_set_uuid_v2$
    DECLARE
        candidate_count bigint;
        distinct_target_count bigint;
    BEGIN
        SELECT
            count(*),
            count(DISTINCT md5(source.uuid::text || '::source_set:v2::' || source_set.index::text)::uuid)
        INTO candidate_count, distinct_target_count
        FROM source_sets source_set
        JOIN sources source ON source.id = source_set.source_id
        WHERE source.artist_id = (
            SELECT artist.id
            FROM artists artist
            WHERE artist.uuid = 'ca53d281-0041-ae33-a050-c87702d93b0c'::uuid
        );

        IF candidate_count = 0 THEN
            RAISE EXCEPTION 'No Phish SourceSets found; aborting';
        END IF;

        IF candidate_count <> distinct_target_count THEN
            RAISE EXCEPTION
                'v2 UUID collision within Phish SourceSets: % rows, % distinct targets',
                candidate_count,
                distinct_target_count;
        END IF;

        IF EXISTS (
            SELECT 1
            FROM source_sets source_set
            JOIN sources source ON source.id = source_set.source_id
            WHERE source.artist_id = (
                SELECT artist.id
                FROM artists artist
                WHERE artist.uuid = 'ca53d281-0041-ae33-a050-c87702d93b0c'::uuid
            )
              AND source_set.uuid NOT IN (
                  md5(source.uuid::text || '::source_set::' || source_set.index::text)::uuid,
                  md5(source.uuid::text || '::source_set:v2::' || source_set.index::text)::uuid
              )
        ) THEN
            RAISE EXCEPTION 'A Phish SourceSet has an unexpected UUID; inspect before applying';
        END IF;

        IF EXISTS (
            SELECT 1
            FROM source_sets source_set
            JOIN sources source ON source.id = source_set.source_id
            JOIN source_sets existing
              ON existing.uuid = md5(
                  source.uuid::text || '::source_set:v2::' || source_set.index::text
              )::uuid
             AND existing.id <> source_set.id
            WHERE source.artist_id = (
                SELECT artist.id
                FROM artists artist
                WHERE artist.uuid = 'ca53d281-0041-ae33-a050-c87702d93b0c'::uuid
            )
        ) THEN
            RAISE EXCEPTION 'A v2 UUID is already owned by a different SourceSet row';
        END IF;
    END
    $verify_source_set_uuid_v2$;

    WITH candidates AS (
        SELECT
            source_set.id,
            md5(source.uuid::text || '::source_set:v2::' || source_set.index::text)::uuid AS target_uuid
        FROM source_sets source_set
        JOIN sources source ON source.id = source_set.source_id
        WHERE source.artist_id = (
            SELECT artist.id
            FROM artists artist
            WHERE artist.uuid = 'ca53d281-0041-ae33-a050-c87702d93b0c'::uuid
        )
    )
    UPDATE source_sets source_set
    SET uuid = candidate.target_uuid
    FROM candidates candidate
    WHERE source_set.id = candidate.id
      AND source_set.uuid IS DISTINCT FROM candidate.target_uuid;

    DO $verify_source_set_uuid_v2$
    BEGIN
        IF EXISTS (
            SELECT 1
            FROM source_sets source_set
            JOIN sources source ON source.id = source_set.source_id
            WHERE source.artist_id = (
                SELECT artist.id
                FROM artists artist
                WHERE artist.uuid = 'ca53d281-0041-ae33-a050-c87702d93b0c'::uuid
            )
              AND source_set.uuid IS DISTINCT FROM md5(
                  source.uuid::text || '::source_set:v2::' || source_set.index::text
              )::uuid
        ) THEN
            RAISE EXCEPTION 'Phish SourceSet UUID v2 verification failed';
        END IF;
    END
    $verify_source_set_uuid_v2$;

    COMMIT;
  \endif
\endif

WITH targets AS (
    SELECT
        source_set.uuid AS current_uuid,
        md5(source.uuid::text || '::source_set:v2::' || source_set.index::text)::uuid AS target_uuid
    FROM source_sets source_set
    JOIN sources source ON source.id = source_set.source_id
    WHERE source.artist_id = (
        SELECT artist.id
        FROM artists artist
        WHERE artist.uuid = 'ca53d281-0041-ae33-a050-c87702d93b0c'::uuid
    )
)
SELECT
    count(*) AS source_sets_after,
    count(*) FILTER (WHERE current_uuid = target_uuid) AS v2_source_sets_after,
    count(*) FILTER (WHERE current_uuid <> target_uuid) AS non_v2_source_sets_after
FROM targets;
