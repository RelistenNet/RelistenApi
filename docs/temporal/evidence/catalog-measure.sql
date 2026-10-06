\timing on
BEGIN READ ONLY;
SET LOCAL statement_timeout = '10s';
SELECT current_timestamp AS measured_at, pg_is_in_recovery() AS replica;
SELECT count(*) AS artists FROM artists;
SELECT u.name, count(*) AS artist_provider_mappings, count(distinct aus.artist_id) AS artists
FROM artists_upstream_sources aus JOIN upstream_sources u ON u.id=aus.upstream_source_id
GROUP BY u.name ORDER BY artist_provider_mappings DESC;
SELECT relname, n_live_tup AS estimated_rows, last_analyze, last_autoanalyze
FROM pg_stat_user_tables WHERE relname IN ('sources','shows','source_tracks');
SELECT count(*) AS sources FROM sources;
SELECT count(*) AS shows FROM shows;
SELECT percentile_disc(ARRAY[0.5,0.95,0.99]) WITHIN GROUP (ORDER BY n) AS sources_per_artist_p50_p95_p99, max(n) AS max_sources_per_artist
FROM (SELECT artist_id,count(*) n FROM sources GROUP BY artist_id) x;
SELECT a.slug,count(*) AS sources,count(distinct s.show_id) AS distinct_shows
FROM sources s JOIN artists a ON a.id=s.artist_id GROUP BY a.slug ORDER BY sources DESC LIMIT 10;
SELECT count(distinct artist_id) AS active_artists_last_7_days FROM shows WHERE date > CURRENT_DATE - 7;
SELECT date_trunc('day',created_at) AS created_day,count(*) AS current_sources_created
FROM sources WHERE created_at >= CURRENT_DATE - 7 GROUP BY 1 ORDER BY 1;
SELECT tablename,indexdef FROM pg_indexes WHERE schemaname='public' AND tablename IN ('sources','shows','source_tracks') AND indexdef LIKE '%UNIQUE%';
COMMIT;
