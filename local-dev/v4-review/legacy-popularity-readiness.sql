-- Run read-only after PopularityJobs.RefreshReadModel completes, before enabling
-- Catalog__LegacyRecordingAdaptersEnabled. Inactive targets have rows with NULL
-- momentum; they are populated. Missing rows mean incomplete publication.
SELECT count(*) AS catalog_shows,
       count(p.uuid) AS populated_shows,
       count(*) FILTER (WHERE p.uuid IS NULL) AS missing_shows,
       count(DISTINCT a.uuid) FILTER (WHERE p.uuid IS NULL) AS artists_with_missing_shows,
       count(*) FILTER (WHERE p.uuid IS NOT NULL AND p.momentum_score IS NULL) AS unscored_shows,
       count(*) FILTER (WHERE p.generated_at < now() - interval '1 hour') AS stale_shows,
       min(p.generated_at) AS oldest_publication,
       max(p.generated_at) AS newest_publication
FROM shows s JOIN artists a ON a.id=s.artist_id
LEFT JOIN show_popularity p ON p.uuid=s.uuid AND p.artist_uuid=a.uuid AND NOT p.is_global;
